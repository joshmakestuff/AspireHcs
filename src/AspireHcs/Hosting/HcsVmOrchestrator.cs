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
/// dashboard, and tear down on AppHost shutdown. Also registers the dashboard's
/// Start/Stop/Restart commands, which Aspire wires up only for resources DCP owns.
/// </summary>
internal static class HcsVmOrchestrator
{
    internal const string HcnOwner = "AspireHcs";

    public static void Register(IResourceBuilder<HcsVirtualMachineResource> builder)
    {
        // The commands need the instance that the InitializeResourceEvent handler creates, which
        // does not exist yet at model-build time. The holder is the handoff.
        InstanceHolder holder = new();

        builder.ApplicationBuilder.Eventing.Subscribe<InitializeResourceEvent>(builder.Resource, (@event, cancellationToken) =>
        {
            HcsVmInstance instance = new(
                (HcsVirtualMachineResource)@event.Resource,
                @event.Services,
                @event.Eventing,
                @event.Notifications,
                @event.Logger);

            holder.Instance = instance;

            // The event handler must not block orchestration; the boot runs in the background
            // and reports through ResourceNotificationService.
            _ = Task.Run(() => instance.RunAsync(), CancellationToken.None);
            return Task.CompletedTask;
        });

        builder.WithCommand(
            KnownResourceCommands.StartCommand,
            "Start",
            context => ExecuteAsync(holder, i => i.StartAsync(), "started"),
            new CommandOptions
            {
                Description = "Boot the virtual machine.",
                IconName = "Play",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                UpdateState = context =>
                {
                    string? state = State(context);
                    if (IsStopped(state))
                    {
                        return ResourceCommandState.Enabled;
                    }
                    return IsInFlight(state) ? ResourceCommandState.Disabled : ResourceCommandState.Hidden;
                },
            });

        builder.WithCommand(
            KnownResourceCommands.StopCommand,
            "Stop",
            context => ExecuteAsync(holder, i => i.StopAsync(), "stopped"),
            new CommandOptions
            {
                Description = "Shut the virtual machine down gracefully, then terminate it.",
                IconName = "Stop",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                UpdateState = context =>
                {
                    string? state = State(context);
                    if (state == KnownResourceStates.Stopping)
                    {
                        return ResourceCommandState.Disabled;
                    }
                    return IsStopped(state) || state == KnownResourceStates.Starting
                        ? ResourceCommandState.Hidden
                        : ResourceCommandState.Enabled;
                },
            });

        builder.WithCommand(
            KnownResourceCommands.RestartCommand,
            "Restart",
            context => ExecuteAsync(holder, i => i.RestartAsync(), "restarted"),
            new CommandOptions
            {
                Description = "Shut the virtual machine down and boot it again from a fresh disk.",
                IconName = "ArrowCounterclockwise",
                IconVariant = IconVariant.Regular,
                UpdateState = context => State(context) == KnownResourceStates.Running
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Disabled,
            });

        static string? State(UpdateCommandStateContext context) => context.ResourceSnapshot.State?.Text;

        static bool IsInFlight(string? state) =>
            state == KnownResourceStates.Starting || state == KnownResourceStates.Stopping;

        // "Unknown" is treated as stopped for the same reason Aspire does it: it is the state a
        // resource can be left in with nothing running.
        static bool IsStopped(string? state) =>
            KnownResourceStates.TerminalStates.Contains(state)
            || state == KnownResourceStates.NotStarted
            || state == "Unknown"
            || string.IsNullOrEmpty(state);
    }

    /// <summary>
    /// Runs a command against the instance, turning failures into a reported result rather than
    /// an unhandled exception. The instance drives resource state itself, so there is nothing to
    /// publish here beyond the command's own outcome.
    /// </summary>
    private static async Task<ExecuteCommandResult> ExecuteAsync(
        InstanceHolder holder, Func<HcsVmInstance, Task> action, string pastTense)
    {
        if (holder.Instance is not { } instance)
        {
            return new ExecuteCommandResult { Success = false, Message = "The virtual machine has not been initialized yet." };
        }

        try
        {
            await action(instance).ConfigureAwait(false);
            return new ExecuteCommandResult { Success = true, Message = $"Virtual machine {pastTense}." };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = ex.Message };
        }
    }

    private sealed class InstanceHolder
    {
        private HcsVmInstance? _instance;

        public HcsVmInstance? Instance
        {
            get => Volatile.Read(ref _instance);
            set => Volatile.Write(ref _instance, value);
        }
    }
}

internal sealed class HcsVmInstance(
    HcsVirtualMachineResource resource,
    IServiceProvider services,
    IDistributedApplicationEventing eventing,
    ResourceNotificationService notifications,
    ILogger logger)
{
    // Serializes boot and teardown so a Restart, a dashboard Stop, the exit-notification
    // cleanup and the AppHost shutdown hook cannot interleave over the same compute system
    // and HCN endpoint. The shutdown hook is the one caller that may proceed without it —
    // bounded wait first, and the ledger's seal semantics keep the unguarded drain safe.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The live boot, if any. Assigned only by BootAsync (under the gate); cleared only by
    // DrainCurrentBoot, which is safe to call without the gate.
    private BootRecord? _current;

    // Incremented when a boot starts and again when its VM is retired. A terminated VM can
    // still deliver its exit notification while the next boot is already underway; without
    // this the stale event would publish Exited over the new VM's Running.
    private int _epoch;

    private CancellationToken _appStopping;

    public async Task RunAsync()
    {
        IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        _appStopping = lifetime.ApplicationStopping;

        // Registered once for the resource's whole life rather than per boot, so repeated
        // Start/Stop cycles do not stack up shutdown callbacks. This hook OWNS teardown at
        // shutdown: a boot cancelled by the stopping token unwinds without draining, precisely
        // so the drain runs here, synchronously, keeping host shutdown blocked until cleanup
        // finishes in every path where the gate is acquired within the bound. The wait is
        // bounded because a boot stuck in a non-cancellable native HCS call must not stall
        // shutdown indefinitely; past the bound the drain proceeds without the gate — Drain
        // seals the ledger, and anything the straggling boot acquires afterwards releases
        // itself (see BootLedger.Add), possibly after shutdown has already returned.
        lifetime.ApplicationStopping.Register(() =>
        {
            bool acquired = _gate.Wait(TimeSpan.FromSeconds(15));
            try
            {
                DrainCurrentBoot();
            }
            finally
            {
                if (acquired)
                {
                    _gate.Release();
                }
            }
        });

        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Boots the VM if it is not already running. Deliberately runs against the AppHost's
    /// lifetime rather than an invoking command's cancellation token: a boot takes tens of
    /// seconds and must not be abandoned half-built because a dashboard request completed.
    /// </summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        try
        {
            if (_current is { Exited: false })
            {
                return;
            }

            // Either nothing is running, or the guest exited on its own and its background
            // cleanup has not reached the gate yet — collect the remains first so the boot
            // below starts from nothing.
            await Task.Run(DrainCurrentBoot).ConfigureAwait(false);
            await BootAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        try
        {
            await ShutDownAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stop then start, under one lock so nothing can slip in between and observe (or act on)
    /// the resource while it is momentarily down.
    /// </summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        try
        {
            await ShutDownAsync().ConfigureAwait(false);
            await BootAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BootAsync()
    {
        CancellationToken stopping = _appStopping;

        // Assigned before anything is acquired so the shutdown hook always has a ledger to
        // drain, no matter where this boot is when the AppHost starts stopping.
        BootRecord boot = new(Interlocked.Increment(ref _epoch), new BootLedger(logger));
        _current = boot;

        try
        {
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Starting,
                StartTimeStamp = DateTime.Now,
                StopTimeStamp = null,
            }).ConfigureAwait(false);

            List<EndpointAnnotation> endpoints = [.. resource.Annotations.OfType<EndpointAnnotation>()];
            if (endpoints.Count > 0 && !resource.NetworkEnabled)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' declares endpoints but no network; add WithNatNetwork().");
            }

            // Nothing publishes BeforeResourceStartedEvent for resources Aspire does not own,
            // and the orchestrator implements WaitFor in its handler for that event — so without
            // this, WaitFor(...) on an HCS VM would never hold the boot back.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), stopping).ConfigureAwait(false);

            string bootDisk = PrepareBootDisk(boot.Ledger);
            ComputeSystemDocument document = BuildDocument(bootDisk);

            if (resource.NetworkEnabled)
            {
                await ScavengeStaleEndpointsAsync().ConfigureAwait(false);
                Guid networkId = HcnClient.FindIcsNetworkId();
                HcnClient.CreateDhcpEndpoint(networkId, resource.HcnEndpointId, resource.MacAddress, HcsVmOrchestrator.HcnOwner);
                boot.Ledger.Add($"HCN endpoint {resource.HcnEndpointId}",
                    () => HcnClient.DeleteEndpoint(resource.HcnEndpointId));
                logger.LogInformation("Attached NIC {Mac} via HCN endpoint {EndpointId} on network {NetworkId}",
                    resource.MacAddress, resource.HcnEndpointId, networkId);
            }

            HcsClient.GrantVmAccess(resource.VmId, bootDisk);
            boot.Ledger.Add($"VM access grant on '{bootDisk}'",
                () => HcsClient.RevokeVmAccess(resource.VmId, bootDisk));
            if (resource.CopyOnWrite)
            {
                string basePath = resource.VhdxPath!;
                HcsClient.GrantVmAccess(resource.VmId, basePath);
                boot.Ledger.Add($"VM access grant on '{basePath}'",
                    () => HcsClient.RevokeVmAccess(resource.VmId, basePath));
            }

            logger.LogInformation("Creating HCS compute system {VmId} from {Disk} ({MemoryMb} MB, {Processors} vCPU)",
                resource.VmId, bootDisk, resource.MemoryMb, resource.ProcessorCount);

            // Entered ahead of the compute system so the reverse-order drain stops the pump only
            // after the VM is gone — the guest's own shutdown output still reaches the logs. The
            // pump is boot-scoped on purpose: the pipe name is stable across boots, and a pump
            // left over from a previous boot would attach to the next VM's pipe alongside the
            // new one.
            CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("serial console pump", () =>
            {
                pumpCts.Cancel();
                pumpCts.Dispose();
            });

            HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync(resource.VmId, document, stopping).ConfigureAwait(false);
            boot.Ledger.Add($"compute system {resource.VmId}", () => ReleaseVm(boot, vm));
            vm.Notification += (_, notification) => OnVmNotification(boot, notification);

            await vm.StartAsync(stopping).ConfigureAwait(false);
            _ = Task.Run(() => SerialConsolePump.RunAsync(resource.SerialPipeName, logger, pumpCts.Token), CancellationToken.None);

            logger.LogInformation("VM started; waiting for the guest OS to become ready...");
            await vm.WaitForGuestReadyAsync(resource.MemoryMb, TimeSpan.FromMinutes(2), stopping).ConfigureAwait(false);
            logger.LogInformation("Guest OS is ready.");
            ThrowIfExitedMidBoot(boot);

            if (resource.NetworkEnabled)
            {
                await AllocateEndpointsAsync(endpoints, stopping).ConfigureAwait(false);
                ThrowIfExitedMidBoot(boot);
            }

            ThrowIfExitedMidBoot(boot);

            // Running is published last, once the guest is up and its endpoints resolve. Aspire's
            // health monitor starts the moment a resource reports Running, and a resource with no
            // health check annotations is declared ready right there — so publishing Running at
            // HCS-start time would fire ResourceReadyEvent (and release WaitFor dependents) against
            // a VM still sitting in its bootloader. Aspire raises ResourceReadyEvent itself; raising
            // our own would be a duplicate that WaitFor cannot observe anyway, since only the
            // health monitor records it in the resource snapshot.
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // AppHost is shutting down mid-boot: either an await observed the cancellation, or
            // the shutdown hook's drain yanked the VM out from under an in-flight HCS call and
            // that call failed — both are shutdown noise, not boot failures. Deliberately no
            // drain here: unwinding quickly is what releases the gate to the shutdown hook,
            // which always runs on ApplicationStopping and does the full drain itself — that
            // keeps host shutdown blocked until cleanup has actually finished, instead of
            // letting it race a drain running on a thread-pool thread.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start HCS virtual machine '{Name}'.", resource.Name);

            // Release whatever the failed boot did claim — however far it got — so Start can be
            // retried from the dashboard without leaking a compute system, an HCN endpoint, an
            // ACL grant, or the copy-on-write work directory.
            await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.FailedToStart,
                // The boot may have activated endpoint URLs before failing; a FailedToStart
                // resource with live-looking links would invite clicks into nothing.
                Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
            }).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// A guest can exit at any moment of the boot — including between the last phase and the
    /// Running publish. The guarantee is that a dead VM always ends in a terminal state, never
    /// Running: an exit this check observes aborts the boot into FailedToStart (via the catch
    /// in <see cref="BootAsync"/>, which also drains); an exit that slips past the last check
    /// is settled to Exited by <see cref="CleanUpAfterUnexpectedExitAsync"/> after its drain.
    /// </summary>
    private static void ThrowIfExitedMidBoot(BootRecord boot)
    {
        if (boot.Exited)
        {
            throw new InvalidOperationException("The guest exited while the boot was still in progress.");
        }
    }

    private async Task ShutDownAsync()
    {
        if (_current is not { } boot)
        {
            return;
        }

        if (boot.Exited)
        {
            // The guest already exited on its own; the dashboard already shows Exited and only
            // the cleanup is left.
            await Task.Run(DrainCurrentBoot).ConfigureAwait(false);
            return;
        }

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Stopping,
        }).ConfigureAwait(false);

        logger.LogInformation("Stopping virtual machine '{Name}'...", resource.Name);
        await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Exited,
            StopTimeStamp = DateTime.Now,
            // The guest's address is gone; leaving its URLs lit would invite clicks into nothing.
            Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retires the current boot, if any, and releases everything it acquired, most recent
    /// first. Idempotent and thread-safe: the AppHost shutdown hook may run this concurrently
    /// with a cancelled boot's own cleanup, and each resource is still released exactly once.
    /// </summary>
    private void DrainCurrentBoot() => Interlocked.Exchange(ref _current, null)?.Ledger.Drain();

    /// <summary>
    /// Best-effort graceful release of the compute system: try a clean guest shutdown briefly,
    /// then terminate, then close the handle. Even if this never runs (crash, kill),
    /// ShouldTerminateOnLastHandleClosed reaps the VM and the next run's scavenger reaps the
    /// endpoint.
    /// </summary>
    private void ReleaseVm(BootRecord boot, HcsComputeSystem vm)
    {
        // Retires the epoch first: the termination below raises an exit notification that
        // would otherwise fight whatever state the caller is about to publish.
        Interlocked.Increment(ref _epoch);

        try
        {
            // A guest that already exited on its own has nothing left to shut down.
            if (!boot.Exited)
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
        }
        catch (Exception)
        {
            // Handle close below still guarantees termination.
        }
        finally
        {
            vm.Dispose();
        }
    }

    private void OnVmNotification(BootRecord boot, HcsNotification notification)
    {
        if (notification.Type != HCS_EVENT_TYPE.HcsEventSystemExited)
        {
            return;
        }

        // A VM we already replaced or are already tearing down has no business reporting the
        // resource's state — ReleaseVm retires the epoch before it terminates anything.
        if (Volatile.Read(ref _epoch) != boot.Epoch)
        {
            return;
        }

        // Marked before Exited is published: the moment the dashboard shows Exited it offers
        // Start, and Start must be able to tell a live boot from a corpse awaiting cleanup.
        boot.Exited = true;

        logger.LogInformation("VM exited: {Detail}", notification.EventData ?? "(no detail)");
        _ = notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Exited,
            StopTimeStamp = DateTime.Now,
            Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
        });

        // The exited compute system's handle, endpoint, grants and work directory are all still
        // held; release them without racing a lifecycle command over the same resources. This
        // runs on a native callback thread and must not block on the gate itself.
        _ = Task.Run(() => CleanUpAfterUnexpectedExitAsync(boot));
    }

    private async Task CleanUpAfterUnexpectedExitAsync(BootRecord boot)
    {
        try
        {
            await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // AppHost shutdown owns cleanup from here.
        }

        try
        {
            // Only drain if the exited boot is still the current one; a Stop or Start that got
            // the gate first has already collected it.
            if (ReferenceEquals(_current, boot))
            {
                await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

                // Republished after the drain because the exit notification can lose a race
                // with an almost-complete boot: the notification's Exited lands first, then
                // BootAsync — past its last liveness check — publishes Running for a VM that is
                // already dead. Settling the state here, under the gate, corrects that Running.
                // (This branch is not reached when the exit aborted the boot instead — the boot's
                // own catch drained _current and published FailedToStart, also terminal.)
                await notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Exited,
                    StopTimeStamp = DateTime.Now,
                    Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
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
            // Setting the property is enough to make the endpoint resolve: EndpointAnnotation's
            // constructor registers this same snapshot in AllAllocatedEndpoints under the endpoint's
            // default network, which for WithEndpoint is the localhost network that
            // EndpointReference.IsAllocated consults.
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, ip, port);
        }

        // Drives the orchestrator's URL processing — WithUrl callbacks and the dashboard's URL list
        // both hang off this event, and nothing raises it for a non-DCP resource.
        await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource, services), cancellationToken).ConfigureAwait(false);

        // The orchestrator publishes endpoint-derived URLs as inactive (hidden), on the assumption
        // that whoever allocated them activates them once they are really listening. That is us.
        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Urls = [.. s.Urls.Select(u => u with { IsInactive = false })],
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
                // Never scavenge our own endpoint: on a Restart it is recreated under the same id,
                // and between the delete and the new compute system it looks exactly like a
                // leftover. (The same window across *processes* is issue #12.)
                if (endpointId == resource.HcnEndpointId)
                {
                    continue;
                }

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

    private string PrepareBootDisk(BootLedger ledger)
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

        string workDir = Path.Combine(Path.GetTempPath(), "AspireHcs", resource.VmId);
        Directory.CreateDirectory(workDir);
        // Entered before the differencing disk is created so even a failure inside
        // CreateDifferencing leaves nothing behind.
        ledger.Add($"work directory '{workDir}'", () =>
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        });

        string diffPath = Path.Combine(workDir, "boot-diff.vhdx");

        // A restart boots from a fresh differencing disk, discarding the previous run's writes —
        // the same contract as a container restart, and the reason the base image is never touched.
        if (File.Exists(diffPath))
        {
            File.Delete(diffPath);
        }

        VirtualDisk.CreateDifferencing(basePath, diffPath);
        return diffPath;
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

    /// <summary>
    /// One boot's identity and holdings. The epoch stamps notifications so a replaced VM cannot
    /// speak for its successor; <see cref="Exited"/> flips when the guest exits on its own,
    /// which is what lets Start tell a live boot from one awaiting cleanup.
    /// </summary>
    private sealed class BootRecord(int epoch, BootLedger ledger)
    {
        public int Epoch { get; } = epoch;
        public BootLedger Ledger { get; } = ledger;
        public volatile bool Exited;
    }
}
