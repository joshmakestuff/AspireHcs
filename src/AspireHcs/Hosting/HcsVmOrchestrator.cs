using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using AspireHcs.Hcs;
using AspireHcs.Hcs.Schema;
using AspireHcs.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Win32.System.HostComputeSystem;

namespace AspireHcs.Hosting;

/// <summary>
/// Drives the lifecycle of an <see cref="HcsVirtualMachineResource"/> through Aspire's
/// eventing pipeline: boot on <see cref="InitializeResourceEvent"/>, publish state to the
/// dashboard, raise <see cref="ResourceReadyEvent"/> once the guest OS is up (so
/// <c>WaitFor(vm)</c> works), and tear down on AppHost shutdown.
/// </summary>
internal static class HcsVmOrchestrator
{
    public static void Register(IDistributedApplicationBuilder builder, HcsVirtualMachineResource resource)
    {
        builder.Eventing.Subscribe<InitializeResourceEvent>(resource, (@event, cancellationToken) =>
        {
            var instance = new HcsVmInstance(
                (HcsVirtualMachineResource)@event.Resource,
                @event.Services,
                @event.Eventing,
                @event.Notifications,
                @event.Logger);

            // The event handler must not block orchestration; the boot runs in the background
            // and reports through ResourceNotificationService.
            _ = Task.Run(() => instance.RunAsync(), CancellationToken.None);
            return Task.CompletedTask;
        });
    }
}

internal sealed class HcsVmInstance(
    HcsVirtualMachineResource resource,
    IServiceProvider services,
    IDistributedApplicationEventing eventing,
    ResourceNotificationService notifications,
    ILogger logger)
{
    private HcsComputeSystem? _vm;
    private string? _workDir;

    public async Task RunAsync()
    {
        IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        CancellationToken stopping = lifetime.ApplicationStopping;

        try
        {
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Starting,
                StartTimeStamp = DateTime.Now,
            }).ConfigureAwait(false);

            string bootDisk = PrepareBootDisk();
            ComputeSystemDocument document = BuildDocument(bootDisk);

            HcsClient.GrantVmAccess(resource.VmId, bootDisk);
            if (resource.CopyOnWrite)
            {
                HcsClient.GrantVmAccess(resource.VmId, resource.VhdxPath!);
            }

            logger.LogInformation("Creating HCS compute system {VmId} from {Disk} ({MemoryMb} MB, {Processors} vCPU)",
                resource.VmId, bootDisk, resource.MemoryMb, resource.ProcessorCount);

            _vm = await HcsClient.CreateComputeSystemAsync(resource.VmId, document, stopping).ConfigureAwait(false);
            _vm.Notification += OnVmNotification;
            lifetime.ApplicationStopping.Register(TearDown);

            await _vm.StartAsync(stopping).ConfigureAwait(false);
            _ = Task.Run(() => SerialConsolePump.RunAsync(resource.SerialPipeName, logger, stopping), CancellationToken.None);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);

            logger.LogInformation("VM started; waiting for the guest OS to become ready...");
            await _vm.WaitForGuestReadyAsync(resource.MemoryMb, TimeSpan.FromMinutes(2), stopping).ConfigureAwait(false);

            logger.LogInformation("Guest OS is ready.");
            await eventing.PublishAsync(new ResourceReadyEvent(resource, services), stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // AppHost is shutting down mid-boot; teardown handles the rest.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start HCS virtual machine '{Name}'.", resource.Name);
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.FailedToStart,
            }).ConfigureAwait(false);
        }
    }

    private void OnVmNotification(object? sender, HcsNotification notification)
    {
        if (notification.Type == HCS_EVENT_TYPE.HcsEventSystemExited)
        {
            logger.LogInformation("VM exited: {Detail}", notification.EventData ?? "(no detail)");
            _ = notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Exited,
                StopTimeStamp = DateTime.Now,
            });
        }
    }

    /// <summary>
    /// Best-effort graceful teardown on AppHost shutdown: try a clean guest shutdown briefly,
    /// then terminate. Even if this never runs (crash, kill), HCS's
    /// ShouldTerminateOnLastHandleClosed reaps the VM when the process dies.
    /// </summary>
    private void TearDown()
    {
        HcsComputeSystem? vm = Interlocked.Exchange(ref _vm, null);
        if (vm is null)
        {
            return;
        }

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            try
            {
                vm.ShutdownAsync(readyTimeout: TimeSpan.FromSeconds(10), cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                vm.TerminateAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            // Handle close below still guarantees termination.
        }
        finally
        {
            vm.Dispose();
            CleanUpWorkDir();
        }
    }

    private string PrepareBootDisk()
    {
        string? basePath = resource.VhdxPath;
        if (string.IsNullOrEmpty(basePath))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no boot disk. Call WithVhdx(...) with the path to a bootable Gen2/UEFI VHDX.");
        }
        if (!File.Exists(basePath))
        {
            throw new FileNotFoundException($"Boot VHDX for resource '{resource.Name}' not found.", basePath);
        }

        if (!resource.CopyOnWrite)
        {
            return basePath;
        }

        _workDir = Path.Combine(Path.GetTempPath(), "AspireHcs", resource.VmId);
        Directory.CreateDirectory(_workDir);
        string diffPath = Path.Combine(_workDir, "boot-diff.vhdx");
        VirtualDisk.CreateDifferencing(basePath, diffPath);
        return diffPath;
    }

    private void CleanUpWorkDir()
    {
        try
        {
            if (_workDir is not null && Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked diff disk in %TEMP% is harmless.
        }
    }

    private ComputeSystemDocument BuildDocument(string bootDisk) => new()
    {
        // 2.5 is required for Services.Shutdown (graceful shutdown); silently ignored below that.
        SchemaVersion = new() { Major = 2, Minor = 5 },
        Owner = "AspireHcs",
        ShouldTerminateOnLastHandleClosed = true,
        VirtualMachine = new()
        {
            Chipset = new() { Uefi = new() { BootThis = new() { DevicePath = "Primary disk", DiskNumber = 0 } } },
            ComputeTopology = new()
            {
                Memory = new() { SizeInMB = resource.MemoryMb },
                Processor = new() { Count = resource.ProcessorCount },
            },
            Devices = new()
            {
                Scsi = new()
                {
                    ["Primary disk"] = new() { Attachments = new() { ["0"] = new() { Path = bootDisk } } },
                },
                ComPorts = new()
                {
                    ["0"] = new() { NamedPipe = @"\\.\pipe\" + resource.SerialPipeName },
                },
            },
            Services = new() { Shutdown = new() },
        },
    };
}
