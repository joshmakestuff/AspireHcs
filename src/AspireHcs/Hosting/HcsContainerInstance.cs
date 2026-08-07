using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using AspireHcs.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// One container's boot, run and teardown. Mirrors <c>HcsVmInstance</c>'s concurrency shape — one
/// gate over boot and teardown, a ledger per boot, an epoch so a dead boot's notification cannot
/// publish over a live one — but the work underneath is hcsctl invocations rather than HCS calls.
/// </summary>
internal sealed class HcsContainerInstance(
    HcsContainerResource resource,
    IServiceProvider services,
    IDistributedApplicationEventing eventing,
    ResourceNotificationService notifications,
    ILogger logger)
{
    /// <summary>
    /// Serializes boot and teardown so a Restart, a dashboard Stop, the workload's own exit and
    /// the AppHost shutdown hook cannot interleave over the same container.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BootRecord? _current;
    private int _epoch;
    private CancellationToken _appStopping;

    public async Task RunAsync()
    {
        IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        _appStopping = lifetime.ApplicationStopping;

        // Registered once for the resource's whole life rather than per boot, so repeated
        // Start/Stop cycles do not stack up shutdown callbacks. This hook owns teardown at
        // shutdown, keeping host shutdown blocked until cleanup has actually finished. The wait
        // is bounded so a stuck hcsctl cannot stall shutdown indefinitely; past the bound the
        // drain proceeds without the gate, and the ledger's seal semantics keep that safe.
        lifetime.ApplicationStopping.Register(() =>
        {
            bool acquired = _gate.Wait(TimeSpan.FromSeconds(30));
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
    /// Boots the container if it is not already running. Runs against the AppHost's lifetime
    /// rather than an invoking command's token: a boot must not be abandoned half-built because
    /// a dashboard request completed.
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

    /// <summary>Stop then start, under one lock so nothing observes the resource mid-swap.</summary>
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

        // Assigned before anything is acquired so the shutdown hook always has a ledger to drain,
        // no matter where this boot is when the AppHost starts stopping.
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

            string image = resource.ImageReference ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no image; call WithImage(reference) before running it.");

            HcsCtl hcsctl = new(HcsCtlBinary.Locate(resource.HcsCtlPath), resource.StorePath);

            // Preflight before anything is acquired. Every condition here is knowable in advance,
            // and each has a fix a developer can act on — which a bare exit code would not.
            HcsCtlInfoDocument info = await hcsctl.GetInfoAsync(stopping).ConfigureAwait(false);
            if (HcsCtlPreflight.DescribeBlocker(info) is { } blocker)
            {
                throw new InvalidOperationException(blocker);
            }

            if (HcsCtlPreflight.DescribeMissingImage(info, image) is { } missing)
            {
                throw new InvalidOperationException(missing);
            }

            // Nothing publishes BeforeResourceStartedEvent for resources Aspire does not own, and
            // the orchestrator implements WaitFor in its handler for that event — so without this,
            // WaitFor(...) on a container would never hold the boot back.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), stopping).ConfigureAwait(false);

            await HcsContainerOrchestrator
                .ScavengeAbandonedContainersAsync(hcsctl, resource.ContainerId, logger, stopping)
                .ConfigureAwait(false);

            Progress progress = new(logger);

            logger.LogInformation("Creating container {ContainerId} from {Image} ({MemoryMb} MB, {Processors} vCPU)",
                resource.ContainerId, image, resource.MemoryMb, resource.ProcessorCount);

            HcsCtlContainerCreateDocument created = await hcsctl
                .CreateAsync(resource.ContainerId, image, resource.ProcessorCount, resource.MemoryMb, progress, stopping)
                .ConfigureAwait(false);

            // Release with rm, not stop: rm is what removes the scratch layer, and a scratch
            // outlives its compute system. Registered immediately after create so a failure
            // anywhere below still reclaims it.
            boot.Ledger.Add($"container {resource.ContainerId}", () => Remove(hcsctl, resource.ContainerId));

            logger.LogInformation("Layer chain resolved to {LayerCount} layer(s); scratch at {Scratch}",
                created.Chain.Count, created.Scratch);

            await hcsctl.StartAsync(resource.ContainerId, progress, stopping).ConfigureAwait(false);

            // The workload runs detached from this boot's await chain: hcsctl's exec stays
            // attached for the guest process's whole life, so awaiting it here would mean the
            // boot never completes. Cancelling this source is what tears the exec down.
            CancellationTokenSource workloadCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("workload", () =>
            {
                workloadCts.Cancel();
                workloadCts.Dispose();
            });

            if (resource.Command is { Length: > 0 } command)
            {
                _ = Task.Run(() => RunWorkloadAsync(hcsctl, boot, command, progress, workloadCts.Token), CancellationToken.None);
            }

            ThrowIfExitedMidBoot(boot);

            // Running is published last. Aspire's health monitor starts the moment a resource
            // reports Running, and a resource with no health check annotations is declared ready
            // right there — so publishing early would release WaitFor dependents against a
            // container that is not up.
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // AppHost shutting down mid-boot: shutdown noise, not a boot failure. Deliberately no
            // drain here — unwinding quickly releases the gate to the shutdown hook, which does
            // the full drain synchronously and keeps host shutdown blocked until it finishes.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start HCS container '{Name}'.", resource.Name);

            // Release whatever the failed boot did claim — however far it got — so Start can be
            // retried from the dashboard without leaking a compute system or a scratch layer.
            await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.FailedToStart,
                Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
            }).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Follows the container's workload for its lifetime and reports its exit. Its output reaches
    /// the dashboard through <see cref="Progress"/>, which carries hcsctl's whole stderr —
    /// guest output and hcsctl's own progress lines are not separable there yet
    /// (<see href="https://github.com/joshmakestuff/hcsctl/issues/28">hcsctl#28</see>), so this is
    /// not the log pipeline #40 asks for.
    /// </summary>
    private async Task RunWorkloadAsync(
        HcsCtl hcsctl, BootRecord boot, string command, IProgress<string> progress, CancellationToken cancellationToken)
    {
        try
        {
            HcsCtlExecDocument result = await hcsctl
                .ExecAsync(resource.ContainerId, command, progress, cancellationToken)
                .ConfigureAwait(false);

            // The guest's exit code, never hcsctl's. hcsctl reports the two separately for
            // exactly this reason.
            logger.LogInformation("Container workload exited with code {ExitCode}.", result.ExitCode);
            await OnWorkloadExitedAsync(boot, result.ExitCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately — a Stop, a Restart, or AppHost shutdown. Not an exit.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Container workload failed.");
            await OnWorkloadExitedAsync(boot, exitCode: null).ConfigureAwait(false);
        }
    }

    private async Task OnWorkloadExitedAsync(BootRecord boot, int? exitCode)
    {
        // A workload that exits after its boot was retired must not publish over the next one.
        if (Volatile.Read(ref _epoch) != boot.Epoch)
        {
            return;
        }

        boot.Exited = true;

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            State = exitCode == 0 ? KnownResourceStates.Finished : KnownResourceStates.Exited,
            ExitCode = exitCode,
            StopTimeStamp = DateTime.Now,
            Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
        }).ConfigureAwait(false);
    }

    private async Task ShutDownAsync()
    {
        if (_current is null)
        {
            return;
        }

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Stopping,
        }).ConfigureAwait(false);

        await Task.Run(DrainCurrentBoot).ConfigureAwait(false);

        await notifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Exited,
            StopTimeStamp = DateTime.Now,
            Urls = [.. s.Urls.Select(u => u with { IsInactive = true })],
        }).ConfigureAwait(false);
    }

    private void DrainCurrentBoot()
    {
        BootRecord? boot = Interlocked.Exchange(ref _current, null);
        if (boot is null)
        {
            return;
        }

        Interlocked.Increment(ref _epoch);
        boot.Ledger.Drain();
    }

    /// <summary>
    /// Removes the container and <b>verifies it is gone by absence</b>, never by the call
    /// returning. <c>DestroyLayer</c> can report success and leave the tree, so a return code is
    /// not evidence of teardown (#48).
    /// </summary>
    private void Remove(HcsCtl hcsctl, string containerId)
    {
        // Synchronous by necessity: this runs from the ledger, which the shutdown hook drains on
        // a callback that cannot await. The timeout keeps a wedged hcsctl from stalling shutdown.
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));

        hcsctl.RemoveAsync(containerId, force: true, timeout.Token).GetAwaiter().GetResult();

        HcsCtlContainerListDocument listing = hcsctl.ListAsync(timeout.Token).GetAwaiter().GetResult();
        HcsCtlContainerRow? survivor = listing.Containers
            .FirstOrDefault(c => string.Equals(c.Id, containerId, StringComparison.Ordinal));

        if (survivor is not null)
        {
            // Deliberately thrown rather than logged: the ledger catches and logs it, so teardown
            // continues, but the failure is reported instead of being silently accepted. A
            // container still listed after rm is a leak, not a formality — and "created" is not
            // "absent".
            throw new InvalidOperationException(
                $"hcsctl reported removing container '{containerId}', but it is still listed with state " +
                $"'{survivor.State}'. Its scratch layer may survive; check `hcsctl container ls`.");
        }
    }

    private static void ThrowIfExitedMidBoot(BootRecord boot)
    {
        if (boot.Exited)
        {
            throw new InvalidOperationException("The container's workload exited while it was still starting.");
        }
    }

    /// <summary>
    /// Forwards hcsctl's stderr to the resource's log. In <c>--json</c> mode that stream carries
    /// both hcsctl's progress and the guest's own output, and the two are not distinguishable
    /// (hcsctl#28) — so this is honest plumbing, not the separated log pipeline of #40.
    /// </summary>
    private sealed class Progress(ILogger logger) : IProgress<string>
    {
        public void Report(string value) => logger.LogInformation("{Line}", value);
    }

    /// <summary>
    /// One boot's identity and holdings. The epoch stamps exits so a replaced container cannot
    /// speak for its successor; <see cref="Exited"/> flips when the workload exits on its own,
    /// which is what lets Start tell a live boot from one awaiting cleanup.
    /// </summary>
    /// <remarks>
    /// <see cref="Exited"/> is a volatile field rather than an auto-property because it is
    /// written from the workload's thread-pool thread and read from whichever thread calls Start
    /// — the same reason the VM path declares it that way.
    /// </remarks>
    private sealed class BootRecord(int epoch, BootLedger ledger)
    {
        public int Epoch { get; } = epoch;

        public BootLedger Ledger { get; } = ledger;

        public volatile bool Exited;
    }
}
