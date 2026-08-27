using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using AspireHcs.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Drives the lifecycle of an <see cref="HcsVirtualMachineResource"/> through Aspire's
/// eventing pipeline: boot on <see cref="InitializeResourceEvent"/>, publish state to the
/// dashboard, and tear down on AppHost shutdown. Also registers the dashboard's
/// Start/Stop/Restart commands, which Aspire wires up only for resources DCP owns.
/// </summary>
internal static class HcsVmOrchestrator
{
    /// <summary>
    /// The label key hcsctl stores on every VM this integration creates, holding the AppHost's
    /// process id. hcsctl never interprets a label; this is the only record of which run a
    /// leftover VM belongs to.
    /// </summary>
    internal const string OwnerPidLabel = "aspirehcs-apphost-pid";

    /// <summary>The value written under <see cref="OwnerPidLabel"/> by this process.</summary>
    internal static string OwnerPidValue { get; } = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);


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

        // "Unknown" is treated as stopped, as Aspire does: it is the state a resource can be
        // left in with nothing running.
        static bool IsStopped(string? state) =>
            KnownResourceStates.TerminalStates.Contains(state)
            || state == KnownResourceStates.NotStarted
            || state == "Unknown"
            || string.IsNullOrEmpty(state);
    }

    /// <summary>
    /// Runs a command against the instance and turns failures into a reported result. The
    /// instance drives resource state itself; only the command's own outcome is published here.
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

    /// <summary>
    /// Removes VMs left behind by AppHost processes that are gone.
    /// </summary>
    /// <remarks>
    /// hcsctl reports facts and holds no opinion about what a dead run is: it is a CLI that
    /// exits, so it has no long-lived process to test a pid against. The policy lives here.
    ///
    /// Removing the VM removes its HCN endpoint with it, so there is no endpoint-level scavenging.
    ///
    /// Static so tests can drive it without booting anything.
    /// </remarks>
    internal static async Task ScavengeAbandonedVmsAsync(
        HcsCtl hcsctl, string ownVmId, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            // ORDER MATTERS: VMs are listed BEFORE the pid snapshot. A VM in this list was created
            // by a process that existed before the snapshot; if that process is alive now it is in
            // the snapshot. A recycled pid can only make a dead run look alive (deferring a
            // removal), never a live run look dead.
            HcsCtlVmListDocument listing = await hcsctl.ListVmsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (listing.VirtualMachines.Count == 0)
            {
                return;
            }

            HashSet<int> livePids = [.. Process.GetProcesses().Select(static p => p.Id)];

            foreach (string id in StaleVmIds(listing, ownVmId, livePids.Contains))
            {
                // Guarded per VM: concurrent AppHosts may sweep the same leftovers, and losing the
                // race on one must not abort the rest of the sweep.
                try
                {
                    logger.LogInformation("Removing virtual machine {VmId}, left by an AppHost that is gone.", id);
                    await hcsctl.RemoveVmAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping virtual machine {VmId} during scavenging.", id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scavenging abandoned virtual machines failed; continuing.");
        }
    }

    /// <summary>
    /// Picks the VMs that belong to this integration and whose creating AppHost is gone. Pure.
    /// </summary>
    internal static IEnumerable<string> StaleVmIds(
        HcsCtlVmListDocument listing, string ownVmId, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(isProcessAlive);

        foreach (HcsCtlVmRow vm in listing.VirtualMachines)
        {
            if (vm.Id is not { } id || string.Equals(id, ownVmId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Someone else's VM in a shared store. No label of ours, so no claim over it.
            if (!vm.Labels.TryGetValue(OwnerPidLabel, out string? recorded))
            {
                continue;
            }

            // An unparseable pid proves nothing; the VM is left alone.
            if (int.TryParse(recorded, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && !isProcessAlive(pid))
            {
                yield return id;
            }
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
    // and HCN endpoint. The shutdown hook is the one caller that may proceed without it after
    // a bounded wait; the ledger's seal semantics keep the unguarded drain safe.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The live boot, if any. Assigned only by BootAsync (under the gate); cleared only by
    // DrainCurrentBoot, which is safe to call without the gate.
    private BootRecord? _current;

    // Incremented when a boot starts and again when its VM is retired. A terminated VM can
    // still deliver its exit notification while the next boot is already underway; without
    // this the stale event would publish Exited over the new VM's Running.
    private int _epoch;

    private CancellationToken _appStopping;

    /// <summary>
    /// How often the exit watch asks hcsctl whether the VM is still there.
    /// </summary>
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait for the guest to take a DHCP lease. A guest with no DHCP client fails
    /// the resource; it does not hang the AppHost.
    /// </summary>
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(90);

    public async Task RunAsync()
    {
        IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        _appStopping = lifetime.ApplicationStopping;

        // Registered once for the resource's whole life, not per boot. This hook OWNS teardown at
        // shutdown: a boot cancelled by the stopping token unwinds without draining, and the
        // drain runs here, synchronously, which keeps host shutdown blocked until cleanup
        // finishes. The wait for the gate is bounded so a boot stuck in a non-cancellable native
        // HCS call cannot stall shutdown; past the bound the drain proceeds without the gate.
        // Drain seals the ledger, and anything the straggling boot acquires afterwards releases
        // itself (see BootLedger.Add), possibly after shutdown has returned.
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
    /// Boots the VM if it is not already running. Runs against the AppHost's lifetime, not an
    /// invoking command's cancellation token: a boot takes tens of seconds, and a completed
    /// dashboard request must not abandon it.
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
            // cleanup has not reached the gate yet. Collect the remains first so the boot below
            // starts from nothing.
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
    /// Stop then start, under one lock so nothing observes the resource while it is down.
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
            if (endpoints.Count > 0 && resource.NetworkName is null)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' declares endpoints but no network; add WithNetwork().");
            }

            // Nothing publishes BeforeResourceStartedEvent for resources Aspire does not own,
            // and the orchestrator implements WaitFor in its handler for that event — so without
            // this, WaitFor(...) on an HCS VM would never hold the boot back.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), stopping).ConfigureAwait(false);

            HcsCtl hcsctl = new(HcsCtlBinary.Locate(resource.HcsCtlPath), resource.StorePath ?? AspireHcsEnvironment.DefaultStorePath);

            HcsCtlInfoDocument info = await hcsctl.GetInfoAsync(stopping).ConfigureAwait(false);
            if (HcsCtlPreflight.DescribeBlocker(info) is { } blocker)
            {
                throw new InvalidOperationException(blocker);
            }

            await HcsVmOrchestrator.ScavengeAbandonedVmsAsync(hcsctl, resource.VmId, logger, stopping).ConfigureAwait(false);

            // Entered ahead of the compute system so the reverse-order drain stops the pump only
            // after the VM is gone; the guest's shutdown output still reaches the logs. The pump
            // is boot-scoped: the pipe name is stable across boots, and a pump left over from a
            // previous boot would attach to the next VM's pipe alongside the new one.
            CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("serial console pump", () =>
            {
                pumpCts.Cancel();
                pumpCts.Dispose();
            });

            logger.LogInformation("Creating virtual machine {VmId} from {Disk} ({MemoryMb} MB, {Processors} vCPU)",
                resource.VmId, resource.VhdxPath, resource.MemoryMb, resource.ProcessorCount);

            // One call makes the differencing disk, grants the VM access to it and the base, builds
            // the compute system, and attaches a DHCP endpoint. It does not start it. Everything it
            // made is released by `vm rm`, including from a later process. The ledger entry is
            // registered immediately, before anything else can fail.
            boot.Ledger.Add($"virtual machine {resource.VmId}", () => RemoveVm(boot, hcsctl));

            HcsCtlVmCreateDocument created = await hcsctl.CreateVmAsync(
                resource.VmId,
                RequireBootDisk(),
                resource.ProcessorCount,
                resource.MemoryMb,
                network: resource.NetworkName,
                serialPipe: @"\\.\pipe\" + resource.SerialPipeName,
                labels: new Dictionary<string, string> { [HcsVmOrchestrator.OwnerPidLabel] = HcsVmOrchestrator.OwnerPidValue },
                progress: new Progress<string>(line => logger.LogDebug("hcsctl: {Line}", line)),
                cancellationToken: stopping).ConfigureAwait(false);

            resource.EndpointId = created.EndpointId;
            resource.MacAddress = created.MacAddress;
            if (resource.NetworkName is not null)
            {
                logger.LogInformation("Attached NIC {Mac} via HCN endpoint {EndpointId} on {Network}",
                    created.MacAddress, created.EndpointId, created.Network);
            }

            await hcsctl.StartVmAsync(resource.VmId, cancellationToken: stopping).ConfigureAwait(false);
            _ = Task.Run(() => SerialConsolePump.RunAsync(resource.SerialPipeName, logger, pumpCts.Token), CancellationToken.None);

            // The DHCP lease is the readiness gate, and the only one. A VM with no network has no
            // readiness signal short of asking the guest agent (`hcsctl guest info`).
            if (resource.NetworkName is not null)
            {
                logger.LogInformation("VM started; waiting for the guest to take a DHCP lease...");
                await AllocateEndpointsAsync(hcsctl, endpoints, stopping).ConfigureAwait(false);
                ThrowIfExitedMidBoot(boot);
            }

            // After the lease (the guest is demonstrably up), before Running: a dependent that
            // WaitFor's this VM must not be released while the reference values are still in
            // flight. A VM with no environment skips this entirely — no agent required.
            await DeliverEnvironmentAsync(hcsctl, stopping).ConfigureAwait(false);
            ThrowIfExitedMidBoot(boot);

            // Best-effort: a VM with no Connect (SSH) command has nothing to forward, and one
            // whose image lacks hcsguest (or whose forward fails to start) keeps the leased
            // address it already resolved above. Never fails the boot.
            await GuestForwardPump.StartAsync(resource, hcsctl, boot.Ledger, logger, stopping).ConfigureAwait(false);
            ThrowIfExitedMidBoot(boot);

            // hcsctl has no verb that blocks until a compute system exits, so the exit watch is a
            // poll.
            StartExitWatch(boot, hcsctl, pumpCts.Token);

            ThrowIfExitedMidBoot(boot);

            // Running is published last, once the guest is up and its endpoints resolve. Aspire's
            // health monitor starts when a resource reports Running, and a resource with no health
            // check annotations is declared ready at that moment; publishing Running at HCS-start
            // time would fire ResourceReadyEvent (and release WaitFor dependents) against a VM
            // still in its bootloader. Aspire raises ResourceReadyEvent itself; only the health
            // monitor records it in the resource snapshot.
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // AppHost is shutting down mid-boot: either an await observed the cancellation, or
            // the shutdown hook's drain removed the VM under an in-flight HCS call and that call
            // failed. Neither is a boot failure. No drain here: unwinding releases the gate to
            // the shutdown hook, which runs on ApplicationStopping and does the full drain itself,
            // so host shutdown stays blocked until cleanup has finished.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start HCS virtual machine '{Name}'.", resource.Name);

            // Release whatever the failed boot claimed so Start can be retried from the dashboard
            // without leaking a compute system, an HCN endpoint, an ACL grant, or the
            // copy-on-write work directory.
            await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.FailedToStart,
                // The boot may have activated endpoint URLs before failing.
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
            // The guest's address is gone.
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
    /// Removes the VM and everything hcsctl made for it: the compute system, the differencing
    /// disk, the HCN endpoint and the store record.
    /// </summary>
    /// <remarks>
    /// A graceful stop is attempted first so the guest can flush and its shutdown output reaches
    /// the console pump; then <c>vm rm --force</c> takes the rest. Nothing here depends on a
    /// handle: if this process dies without running it, the VM survives and the next run's
    /// scavenger removes it by label.
    /// </remarks>
    private void RemoveVm(BootRecord boot, HcsCtl hcsctl)
    {
        // Retires the epoch first: the exit watch would otherwise see the VM disappear and publish
        // Exited over whatever state the caller is about to publish.
        Interlocked.Increment(ref _epoch);

        try
        {
            // A guest that already exited on its own has nothing left to shut down.
            if (!boot.Exited)
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
                try
                {
                    hcsctl.StopVmAsync(resource.VmId, force: false, cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // A guest with no shutdown integration service cannot be asked. rm terminates.
                }
            }
        }
        finally
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
                hcsctl.RemoveVmAsync(resource.VmId, cancellationToken: cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Reported: this is the one path that deletes the HCN endpoint, and an endpoint
                // that outlives its run is a leak that outlives the process too.
                logger.LogWarning(ex, "Removing virtual machine {VmId} failed; the next run will scavenge it.", resource.VmId);
            }
        }
    }

    /// <summary>
    /// Watches for the guest exiting on its own, by polling.
    /// </summary>
    /// <remarks>
    /// hcsctl has no verb that blocks until a compute system exits, so this polls <c>vm ls</c>;
    /// an exit is noticed within <see cref="ExitPollInterval"/>. That latency shows in the
    /// dashboard only: cleanup is driven by the ledger, and a stop the user asked for does not
    /// wait on this.
    /// </remarks>
    private void StartExitWatch(BootRecord boot, HcsCtl hcsctl, CancellationToken cancellationToken)
        => _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(ExitPollInterval, cancellationToken).ConfigureAwait(false);

                    HcsCtlVmListDocument listing = await hcsctl.ListVmsAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    string? state = listing.VirtualMachines
                        .FirstOrDefault(v => string.Equals(v.Id, resource.VmId, StringComparison.OrdinalIgnoreCase))?.State;

                    // Absent from the store means someone else removed it; stopped means the guest
                    // powered itself off. Both are the VM being gone. A blank or unknown state is
                    // NOT: hcsctl says "created" before a first start and "unknown" when it could
                    // not tell.
                    if (state is null || state == HcsCtlVmState.Stopped)
                    {
                        OnVmExited(boot, state is null ? "removed" : "stopped");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The boot was drained; this watch belongs to it and goes with it.
            }
            catch (Exception ex)
            {
                // A watch that dies must not take the VM with it. Logged: the dashboard may now
                // show Running for a VM that has exited.
                logger.LogWarning(ex, "The exit watch for '{Name}' stopped; its state may go stale.", resource.Name);
            }
        }, CancellationToken.None);

    private void OnVmExited(BootRecord boot, string how)
    {
        // A VM already replaced or in teardown must not report the resource's state. RemoveVm
        // retires the epoch before it stops anything.
        if (Volatile.Read(ref _epoch) != boot.Epoch)
        {
            return;
        }

        // Marked before Exited is published: the moment the dashboard shows Exited it offers
        // Start, and Start must be able to tell a live boot from a corpse awaiting cleanup.
        boot.Exited = true;

        logger.LogInformation("VM exited ({How}).", how);
        _ = notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Exited,
            StopTimeStamp = DateTime.Now,
            Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
        });

        // The store record, disk and endpoint are all still there; release them without racing a
        // lifecycle command over the same resources.
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

                // Republished after the drain: the exit notification can lose a race with an
                // almost-complete boot. The notification's Exited lands first, then BootAsync,
                // past its last liveness check, publishes Running for a VM that is already dead.
                // Settling the state here, under the gate, corrects that Running. This branch is
                // not reached when the exit aborted the boot; the boot's own catch drained
                // _current and published FailedToStart.
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
    /// Waits for the guest's DHCP lease and resolves every declared endpoint at that address.
    /// </summary>
    /// <remarks>
    /// The address is not knowable before the guest boots. An HCN endpoint carries none when it is
    /// created, none when it is attached to a NIC, and none while the VM runs without a guest, so
    /// this is a wait and not a read.
    /// </remarks>
    private async Task AllocateEndpointsAsync(
        HcsCtl hcsctl, List<EndpointAnnotation> endpoints, CancellationToken cancellationToken)
    {
        HcsCtlVmAddressDocument leased = await hcsctl
            .WaitForAddressAsync(resource.VmId, AddressTimeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // hcsctl reports CIDR; an endpoint wants the bare address.
        string ip = leased.Addresses.FirstOrDefault()?.Split('/')[0]
            ?? throw new InvalidOperationException(
                $"hcsctl reported no address for '{resource.Name}' despite succeeding.");

        logger.LogInformation("Guest leased {Ip} after {Elapsed} ms; publishing {Count} endpoint(s).",
            ip, leased.WaitedMs, endpoints.Count);

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

        // Drives the orchestrator's URL processing -- WithUrl callbacks and the dashboard's URL list
        // both hang off this event, and nothing raises it for a non-DCP resource.
        await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource, services), cancellationToken).ConfigureAwait(false);

        // The orchestrator publishes endpoint-derived URLs as inactive (hidden); whoever allocated
        // them activates them.
        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Urls = [.. s.Urls.Select(u => u with { IsInactive = false })],
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// How long the guest shell gets to write <c>/etc/aspire.env</c>. The write is one pipe into
    /// one file; anything slower than this is a wedged guest, and an unbounded exec would wedge
    /// the boot with it.
    /// </summary>
    private static readonly TimeSpan EnvironmentWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Writes the resource's resolved environment — <c>WithReference</c> values included, with
    /// host-loopback endpoints redirected through the relay — to <c>/etc/aspire.env</c> in the
    /// guest, over hvsocket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A VM has no create-time injection: nothing writes environment variables into a VHDX. So
    /// the convention: once the guest is up, the values land in <c>/etc/aspire.env</c>, and a
    /// workload reads the file when it starts. The stated caveat travels with it — a workload
    /// that autostarts at boot may run before the file lands; one that reads the file when it
    /// starts is correct today.
    /// </para>
    /// <para>
    /// The transport needs the <c>hcsguest</c> agent in the image (the same prerequisite as
    /// <c>hcsctl guest info</c>) but no NIC: hvsocket works on a networkless VM, which is why a
    /// VM without <c>WithNetwork()</c> can still receive plain <c>WithEnvironment</c> values —
    /// only host-loopback <em>references</em> are refused there, since the guest could not reach
    /// the relay. The content crosses the guest's shell base64-encoded in a variable rather than
    /// quoted into the command line, so no value can break out of the write. <c>/bin/sh</c> and
    /// <c>base64</c> are assumed in the guest — the convention is for Linux guests today.
    /// </para>
    /// </remarks>
    private async Task DeliverEnvironmentAsync(HcsCtl hcsctl, CancellationToken cancellationToken)
    {
        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(
            resource, services.GetRequiredService<DistributedApplicationExecutionContext>(), cancellationToken)
            .ConfigureAwait(false);

        if (resolved.Values.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> environment = await GuestReferences.RedirectLoopbackAsync(
            resource.Name,
            resource.NetworkName,
            resolved,
            hcsctl.ListNetworksAsync,
            (id, ct) => hcsctl.InspectNetworkAsync(id, ct),
            services.GetRequiredService<DockerRelay>().EnsurePublishedAsync,
            cancellationToken).ConfigureAwait(false);

        string file = GuestEnvironment.BuildEnvFile(resource.Name, environment);
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(file));

        HcsCtlGuestExecDocument written = await hcsctl.GuestExecAsync(
            resource.VmId,
            "printf '%s' \"$ASPIRE_ENV_B64\" | base64 -d > /etc/aspire.env",
            new Dictionary<string, string> { ["ASPIRE_ENV_B64"] = encoded },
            EnvironmentWriteTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (written.ExitCode != 0)
        {
            // The values ARE the reference feature for a VM; a guest that did not take them has
            // not honoured WithReference, and reporting Running anyway would hide that.
            throw new InvalidOperationException(
                $"Writing /etc/aspire.env in '{resource.Name}' failed: the guest shell " +
                $"{(written.TimedOut ? "timed out" : $"exited {written.ExitCode}")}." +
                (string.IsNullOrEmpty(written.Detail) ? "" : $" {written.Detail}"));
        }

        logger.LogInformation("Wrote {Count} environment value(s) to /etc/aspire.env in the guest.", environment.Count);
    }

    /// <summary>
    /// The boot disk. Checked here so a missing one is a clear message; hcsctl would exit 64
    /// about <c>--vhdx</c>. hcsctl makes the differencing child itself and removes it with the VM.
    /// </summary>
    private string RequireBootDisk()
    {
        if (string.IsNullOrEmpty(resource.VhdxPath))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no boot disk. Call WithVhdx(...) with the path to a bootable Gen2/UEFI VHDX.");
        }
        if (!File.Exists(resource.VhdxPath))
        {
            throw new FileNotFoundException($"Boot VHDX for resource '{resource.Name}' not found.", resource.VhdxPath);
        }
        return resource.VhdxPath;
    }


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
