using System.Collections.Immutable;
using System.Diagnostics;
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

    /// <summary>
    /// The tool for the live boot. Held so the dashboard commands can reach it — they run long
    /// after BootAsync's locals are gone, and resolving the binary again per click would let a
    /// command act on a different hcsctl than the one that created the container.
    /// </summary>
    private HcsCtl? _hcsctl;

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

    /// <summary>
    /// Suspends the container. A paused workload demonstrably stops making progress — that is the
    /// point of the command, and it is what distinguishes pause from stop.
    /// </summary>
    public async Task PauseAsync()
    {
        await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        try
        {
            if (_current is not { Exited: false } || _hcsctl is not { } hcsctl)
            {
                throw new InvalidOperationException("The container is not running.");
            }

            await hcsctl.PauseAsync(resource.ContainerId, _appStopping).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = HcsContainerOrchestrator.PausedState,
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync(_appStopping).ConfigureAwait(false);
        try
        {
            if (_current is not { Exited: false } || _hcsctl is not { } hcsctl)
            {
                throw new InvalidOperationException("The container is not running.");
            }

            await hcsctl.ResumeAsync(resource.ContainerId, _appStopping).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the guest's process list to the resource log and returns a one-line summary.
    /// </summary>
    /// <remarks>
    /// A flat table, and it can be nothing else: HCS reports no parent process ids, so there is no
    /// tree to build here or in any UI downstream. It goes to the log rather than to a snapshot
    /// property because it is tens of rows that a developer reads once while diagnosing, not a
    /// value worth showing continuously.
    /// </remarks>
    public async Task<string> ListGuestProcessesAsync()
    {
        if (_current is not { Exited: false } || _hcsctl is not { } hcsctl)
        {
            throw new InvalidOperationException("The container is not running.");
        }

        HcsCtlProcessListDocument list = await hcsctl
            .ProcessListAsync(resource.ContainerId, _appStopping)
            .ConfigureAwait(false);

        if (list.Processes.Count == 0)
        {
            logger.LogInformation("No processes reported in the guest.");
            return "The guest reported no processes.";
        }

        logger.LogInformation("{Header}", $"{"PID",8}  {"IMAGE",-32} {"COMMIT",12} {"CPU",12}");
        foreach (HcsCtlGuestProcess process in list.Processes.OrderBy(p => p.ProcessId))
        {
            logger.LogInformation("{Row}", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,8}  {1,-32} {2,12} {3,12}",
                process.ProcessId,
                Truncate(process.ImageName ?? "", 32),
                FormatBytes(process.MemoryCommitBytes),
                FormatDuration(process.CpuTime)));
        }

        return $"{list.Processes.Count} process(es) written to the resource logs.";
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

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

            // Caught here rather than after the container exists: a resource whose endpoints can
            // never resolve is misconfigured, and finding that out post-create means cleaning up
            // a compute system to say so.
            if (resource.Annotations.OfType<EndpointAnnotation>().Any() && resource.NetworkName is null)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' declares endpoints but no network; add WithNetwork().");
            }

            HcsCtl hcsctl = new(HcsCtlBinary.Locate(resource.HcsCtlPath), resource.StorePath);
            _hcsctl = hcsctl;

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

            // Resolved before anything is created. An empty value is rejected here (#49), and a
            // resource that cannot be configured correctly should fail before it has acquired a
            // compute system and a scratch layer to clean up.
            IReadOnlyDictionary<string, string> environment = await ContainerEnvironment
                .ResolveAsync(resource, services.GetRequiredService<DistributedApplicationExecutionContext>(), stopping)
                .ConfigureAwait(false);

            logger.LogInformation("Creating container {ContainerId} from {Image} ({MemoryMb} MB, {Processors} vCPU)",
                resource.ContainerId, image, resource.MemoryMb, resource.ProcessorCount);

            HcsCtlContainerCreateDocument created = await hcsctl
                .CreateAsync(
                    resource.ContainerId,
                    image,
                    resource.ProcessorCount,
                    resource.MemoryMb,
                    resource.ScratchSizeGigabytes,
                    [.. resource.Mounts.Select(m => m.ToOptionValue())],
                    resource.NetworkName,
                    progress,
                    stopping)
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
                _ = Task.Run(
                    () => RunWorkloadAsync(hcsctl, boot, command, environment, progress, workloadCts.Token),
                    CancellationToken.None);
            }

            await AllocateEndpointsAsync(hcsctl, created, stopping).ConfigureAwait(false);
            await PublishEndpointsAsync(stopping).ConfigureAwait(false);

            // Boot-scoped like the workload: cancelling the source is what stops the polling, and
            // a poller left over from a previous boot would publish a dead container's numbers
            // over a live one's.
            CancellationTokenSource statsCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("statistics poller", () =>
            {
                statsCts.Cancel();
                statsCts.Dispose();
            });
            _ = Task.Run(() => PollStatisticsAsync(hcsctl, created, statsCts.Token), CancellationToken.None);

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
    /// How long to wait for an ICS network to lease the container's endpoint an address after
    /// start. Mirrors the VM path's timeout: measured leases land within seconds of the guest
    /// coming up (probe-ds, 2026-08-09), so this is generous — and a network that never leases
    /// fails the resource rather than hanging the AppHost.
    /// </summary>
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How often the endpoint is re-read while waiting for its lease.</summary>
    private static readonly TimeSpan AddressPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resolves the resource's declared endpoints against the container's own address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the address exists depends on the network. A NAT network assigns it at create, so the
    /// create document already carries it — that fast path stays a read, no polling (#41's
    /// measurement, 2026-08-07). An ICS network — the Default Switch, the default since #60 —
    /// leases the address only after the guest is up, the same timing the VM path documents
    /// (hcsctl#43), so there this is a bounded wait against a live HCN read. It cannot be a read
    /// of any create-time snapshot: hcsctl's state.json never updates after create (#63).
    /// </para>
    /// <para>
    /// The address is the container's own on the host compute network, reachable from the host
    /// directly (measured 2026-08-07). There is no host port mapping, so an endpoint's port is
    /// the guest's port — nothing is translated.
    /// </para>
    /// </remarks>
    private async Task AllocateEndpointsAsync(
        HcsCtl hcsctl, HcsCtlContainerCreateDocument created, CancellationToken cancellationToken)
    {
        List<EndpointAnnotation> endpoints = [.. resource.Annotations.OfType<EndpointAnnotation>()];
        if (endpoints.Count == 0)
        {
            return;
        }

        string address;
        if (created.Addresses.Count > 0)
        {
            // hcsctl reports the address in CIDR form (172.17.163.120/20). An endpoint wants the
            // address alone; leaving the prefix on produces a host string nothing can connect to.
            address = created.Addresses[0].Split('/')[0];
        }
        else
        {
            // Guarded again here, not only before create, so this method cannot regress into
            // waiting forever for an endpoint that was never attached. This message belongs to
            // the no-network case alone — a resource WITH WithNetwork() whose address is merely
            // late must never be told to add it (#63).
            string network = resource.NetworkName ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' declares endpoints but no network; add WithNetwork().");

            string endpointId = created.Endpoint ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' is on network '{network}' but hcsctl reported no " +
                "endpoint for the container, so its address can never be discovered.");

            address = await WaitForLeasedAddressAsync(
                token => hcsctl.ListEndpointsAsync(network, token),
                endpointId,
                network,
                resource.Name,
                AddressTimeout,
                AddressPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Container address {Address}; publishing {Count} endpoint(s).", address, endpoints.Count);

        foreach (EndpointAnnotation endpoint in endpoints)
        {
            int port = endpoint.TargetPort
                ?? throw new InvalidOperationException($"Endpoint '{endpoint.Name}' has no target port.");

            // Setting the property is enough to make the endpoint resolve: EndpointAnnotation's
            // constructor registers this same snapshot under the endpoint's default network,
            // which is what EndpointReference.IsAllocated consults.
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, address, port);
        }
    }

    /// <summary>
    /// Polls the live HCN endpoint listing until the container's endpoint carries an address, and
    /// returns that address bare (hcsctl reports CIDR).
    /// </summary>
    /// <remarks>
    /// The poll target is <c>network endpoints</c> and nothing else on purpose: hcsctl's
    /// state.json — and <c>container inspect</c>, which reports that snapshot — records only the
    /// create-time address list, which on an ICS network is empty forever. Only HCN's own view
    /// changes when the lease lands, the same fact hcsctl#43 records on the VM side, where
    /// <c>vm ip</c> does this wait inside hcsctl. There is no <c>container ip</c> verb, so the
    /// container side waits here instead (#63).
    /// </remarks>
    internal static async Task<string> WaitForLeasedAddressAsync(
        Func<CancellationToken, Task<HcsCtlNetworkEndpointsDocument>> readEndpoints,
        string endpointId,
        string networkName,
        string resourceName,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        bool listed = false;

        while (true)
        {
            HcsCtlNetworkEndpointsDocument listing = await readEndpoints(cancellationToken).ConfigureAwait(false);

            // Ordinal-insensitive because these are GUIDs, and HCN is not consistent about their
            // case across read paths — stats report them uppercase, the listing lowercase.
            HcsCtlNetworkEndpointRow? endpoint = listing.Endpoints.FirstOrDefault(
                e => string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase));
            listed |= endpoint is not null;

            if (endpoint is { Addresses.Count: > 0 })
            {
                return endpoint.Addresses[0].Split('/')[0];
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                throw new InvalidOperationException(
                    $"Resource '{resourceName}' declares endpoints, but endpoint {endpointId} on " +
                    $"network '{networkName}' {(listed ? "still had no address" : "was never listed")} " +
                    $"after waiting {elapsed.TotalSeconds:0.#} s. An ICS network leases the address " +
                    "after the guest starts; a network that never leases one cannot serve these endpoints.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How often live statistics are refreshed into the dashboard.</summary>
    private static readonly TimeSpan StatisticsInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Refreshes the resource's dashboard properties from <c>container stats</c> until the boot
    /// ends.
    /// </summary>
    /// <remarks>
    /// Failures are logged at debug and the loop continues. A stats call can fail for reasons that
    /// are not faults — the container is paused, or is mid-teardown — and a poller that gave up on
    /// the first error would leave the dashboard frozen on stale numbers with no indication why.
    /// </remarks>
    private async Task PollStatisticsAsync(HcsCtl hcsctl, HcsCtlContainerCreateDocument created, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(StatisticsInterval);
        do
        {
            try
            {
                HcsCtlStatsDocument stats = await hcsctl
                    .StatsAsync(resource.ContainerId, cancellationToken)
                    .ConfigureAwait(false);

                if (stats.Statistics is { } s)
                {
                    await notifications.PublishUpdateAsync(resource, snapshot => snapshot with
                    {
                        Properties = Describe(created, s),
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Reading container statistics failed; will retry.");
            }
        }
        while (await SafeWaitAsync(timer, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The properties the dashboard shows for a container, mirroring what the VM resource
    /// surfaces. Byte counts and HCS's 100-nanosecond ticks are formatted here rather than shown
    /// raw — a dashboard row reading <c>1088274432</c> is data, not information.
    /// </summary>
    private ImmutableArray<ResourcePropertySnapshot> Describe(
        HcsCtlContainerCreateDocument created, HcsCtlStatistics stats)
    {
        List<ResourcePropertySnapshot> properties =
        [
            new("hcs.container.id", resource.ContainerId),
            new("hcs.container.image", resource.ImageReference),
            new("hcs.container.layers", created.Chain.Count),
            new("hcs.container.uptime", FormatDuration(stats.Uptime)),
        ];

        if (stats.Memory is { } memory)
        {
            properties.Add(new("hcs.memory.commit", FormatBytes(memory.CommitBytes)));
            properties.Add(new("hcs.memory.commitPeak", FormatBytes(memory.CommitPeakBytes)));
            properties.Add(new("hcs.memory.workingSetPrivate", FormatBytes(memory.PrivateWorkingSetBytes)));
        }

        if (stats.Processor is { } processor)
        {
            properties.Add(new("hcs.cpu.total", FormatDuration(processor.TotalRuntime)));
        }

        if (stats.Storage is { } storage)
        {
            properties.Add(new("hcs.storage.read", $"{storage.ReadCount} ops, {FormatBytes(storage.ReadBytes)}"));
            properties.Add(new("hcs.storage.write", $"{storage.WriteCount} ops, {FormatBytes(storage.WriteBytes)}"));
        }

        // Indexed because a container can carry more than one endpoint, and an unindexed name
        // would silently show only the last.
        for (int i = 0; i < stats.Network.Count; i++)
        {
            HcsCtlNetworkStats network = stats.Network[i];
            string suffix = stats.Network.Count == 1 ? "" : $".{i}";
            properties.Add(new($"hcs.network{suffix}.received", FormatBytes(network.BytesReceived)));
            properties.Add(new($"hcs.network{suffix}.sent", FormatBytes(network.BytesSent)));
        }

        return [.. properties];
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = value;
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value} {units[unit]}"
            : $"{scaled:0.#} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan value) => value switch
    {
        { TotalSeconds: < 1 } => $"{value.TotalMilliseconds:0} ms",
        { TotalMinutes: < 1 } => $"{value.TotalSeconds:0.#} s",
        { TotalHours: < 1 } => $"{value.Minutes}m {value.Seconds}s",
        _ => $"{(int)value.TotalHours}h {value.Minutes}m",
    };

    private async Task PublishEndpointsAsync(CancellationToken cancellationToken)
    {
        if (!resource.Annotations.OfType<EndpointAnnotation>().Any())
        {
            return;
        }

        // Drives the orchestrator's URL processing — WithUrl callbacks and the dashboard's URL
        // list both hang off this event, and nothing raises it for a non-DCP resource.
        await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource, services), cancellationToken)
            .ConfigureAwait(false);

        // The orchestrator publishes endpoint-derived URLs as inactive (hidden), on the
        // assumption that whoever allocated them activates them. That is us.
        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Urls = [.. s.Urls.Select(u => u with { IsInactive = false })],
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Follows the container's workload for its lifetime and reports its exit. Its output reaches
    /// the dashboard through <see cref="Progress"/>, which carries hcsctl's whole stderr —
    /// guest output and hcsctl's own progress lines are not separable there yet
    /// (<see href="https://github.com/joshmakestuff/hcsctl/issues/28">hcsctl#28</see>), so this is
    /// not the log pipeline #40 asks for.
    /// </summary>
    private async Task RunWorkloadAsync(
        HcsCtl hcsctl,
        BootRecord boot,
        string command,
        IReadOnlyDictionary<string, string> environment,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            HcsCtlExecDocument result = await hcsctl
                .ExecAsync(resource.ContainerId, command, environment, progress, cancellationToken)
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
