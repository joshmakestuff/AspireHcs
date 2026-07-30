using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using AspireHcs.Hcn;
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
    internal const string HcnOwner = "AspireHcs";

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
    private int _hcnEndpointCreated;

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

            List<EndpointAnnotation> endpoints = [.. resource.Annotations.OfType<EndpointAnnotation>()];
            if (endpoints.Count > 0 && !resource.NetworkEnabled)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' declares endpoints but no network; add WithNatNetwork().");
            }

            string bootDisk = PrepareBootDisk();
            ComputeSystemDocument document = BuildDocument(bootDisk);

            if (resource.NetworkEnabled)
            {
                await ScavengeStaleEndpointsAsync().ConfigureAwait(false);
                Guid networkId = HcnClient.FindIcsNetworkId();
                HcnClient.CreateDhcpEndpoint(networkId, resource.HcnEndpointId, resource.MacAddress, HcsVmOrchestrator.HcnOwner);
                Volatile.Write(ref _hcnEndpointCreated, 1);
                logger.LogInformation("Attached NIC {Mac} via HCN endpoint {EndpointId} on network {NetworkId}",
                    resource.MacAddress, resource.HcnEndpointId, networkId);
            }

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

            if (resource.NetworkEnabled)
            {
                await AllocateEndpointsAsync(endpoints, stopping).ConfigureAwait(false);
            }

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

    /// <summary>
    /// Waits for the guest's DHCP lease to surface in the HCN endpoint properties (HNS learns
    /// it against our MAC — verified empirically; typically seconds after guest-ready), then
    /// resolves every declared endpoint at that address.
    /// </summary>
    private async Task AllocateEndpointsAsync(List<EndpointAnnotation> endpoints, CancellationToken cancellationToken)
    {
        string ip = await WaitForLeasedIpAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Guest leased {Ip}; publishing {Count} endpoint(s).", ip, endpoints.Count);

        foreach (EndpointAnnotation endpoint in endpoints)
        {
            int port = endpoint.TargetPort
                ?? throw new InvalidOperationException($"Endpoint '{endpoint.Name}' has no target port.");
            AllocatedEndpoint allocated = new(endpoint, ip, port);
            // Both stores must be written: the legacy property feeds older consumers, while
            // EndpointReference.IsAllocated (and the testing helpers) read the per-network list.
            endpoint.AllocatedEndpoint = allocated;
            endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.LocalhostNetwork, allocated);
        }

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Urls = [.. endpoints.Select(e =>
                new UrlSnapshot(e.Name, $"{e.UriScheme}://{ip}:{e.TargetPort}", IsInternal: false))],
        }).ConfigureAwait(false);
    }

    private async Task<string> WaitForLeasedIpAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            string? properties = HcnClient.QueryEndpointProperties(resource.HcnEndpointId);
            string? ip = properties is null ? null : JsonNode.Parse(properties)?["IPAddress"]?.GetValue<string>();
            if (ip is not null)
            {
                return ip;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The guest of '{resource.Name}' did not obtain a DHCP lease within {timeout}. " +
                    "Ensure the guest image configures its NIC for DHCP.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes AspireHcs-owned HCN endpoints whose VM no longer exists — leftovers of crashed
    /// AppHosts (endpoints persist independently of the ephemeral compute systems). Endpoints
    /// of concurrently running AspireHcs VMs are recognized by their live VM attachment and
    /// left alone.
    /// </summary>
    private async Task ScavengeStaleEndpointsAsync()
    {
        try
        {
            List<Guid> owned = HcnClient.EnumerateEndpointIds(HcsVmOrchestrator.HcnOwner);
            if (owned.Count == 0)
            {
                return;
            }

            string running = await HcsClient.EnumerateComputeSystemsAsync().ConfigureAwait(false) ?? "[]";
            HashSet<string> runtimeIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? system in JsonNode.Parse(running) as JsonArray ?? [])
            {
                if (system?["RuntimeId"]?.GetValue<string>() is { } runtimeId)
                {
                    runtimeIds.Add(runtimeId);
                }
            }

            foreach (Guid endpointId in owned)
            {
                string? properties = HcnClient.QueryEndpointProperties(endpointId);
                string? vmRuntimeId = properties is null ? null : JsonNode.Parse(properties)?["VirtualMachine"]?.GetValue<string>();
                if (vmRuntimeId is null || !runtimeIds.Contains(vmRuntimeId))
                {
                    logger.LogInformation("Scavenging stale HCN endpoint {EndpointId} from a previous run.", endpointId);
                    HcnClient.DeleteEndpoint(endpointId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scavenging stale HCN endpoints failed; continuing.");
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
    /// then terminate, then release the HCN endpoint. Even if this never runs (crash, kill),
    /// ShouldTerminateOnLastHandleClosed reaps the VM and the next run's scavenger reaps the
    /// endpoint.
    /// </summary>
    private void TearDown()
    {
        HcsComputeSystem? vm = Interlocked.Exchange(ref _vm, null);
        if (vm is not null)
        {
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

        if (Interlocked.Exchange(ref _hcnEndpointCreated, 0) == 1)
        {
            try
            {
                HcnClient.DeleteEndpoint(resource.HcnEndpointId);
            }
            catch (Exception)
            {
                // The next run's scavenger will get it.
            }
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
                NetworkAdapters = resource.NetworkEnabled
                    ? new() { ["ext"] = new() { EndpointId = resource.HcnEndpointId.ToString(), MacAddress = resource.MacAddress } }
                    : null,
            },
            Services = new() { Shutdown = new() },
        },
    };
}
