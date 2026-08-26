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
/// One container's boot, run and teardown. Same concurrency shape as <c>HcsVmInstance</c>: one
/// gate over boot and teardown, a ledger per boot, an epoch so a dead boot's notification cannot
/// publish over a live one. The work underneath is hcsctl invocations.
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
    /// The tool for the live boot. Dashboard commands use this instance, so they act on the same
    /// hcsctl that created the container.
    /// </summary>
    private HcsCtl? _hcsctl;

    public async Task RunAsync()
    {
        IHostApplicationLifetime lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        _appStopping = lifetime.ApplicationStopping;

        // Registered once for the resource's whole life, not per boot. This hook owns teardown at
        // shutdown and keeps host shutdown blocked until cleanup has finished. The wait for the
        // gate is bounded; past the bound the drain proceeds without the gate, which the ledger's
        // seal semantics permit.
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
    /// Boots the container if it is not already running. Runs against the AppHost's lifetime,
    /// not an invoking command's token: a completed dashboard request must not abandon a boot.
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
    /// Suspends the container. A paused workload stops making progress.
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
    /// A flat table: HCS reports no parent process ids, so there is no tree to build. It goes to
    /// the log, not to a snapshot property.
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

            // Checked before the container exists: endpoints without a network can never resolve.
            if (resource.Annotations.OfType<EndpointAnnotation>().Any() && resource.NetworkName is null)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' declares endpoints but no network; add WithNetwork().");
            }

            HcsCtl hcsctl = new(HcsCtlBinary.Locate(resource.HcsCtlPath), resource.StorePath ?? AspireHcsEnvironment.DefaultStorePath);
            _hcsctl = hcsctl;

            // Preflight before anything is acquired. Each condition has a fix a developer can act on.
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
            // the orchestrator implements WaitFor in its handler for that event. Without this,
            // WaitFor(...) on a container would not hold the boot back.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), stopping).ConfigureAwait(false);

            await HcsContainerOrchestrator
                .ScavengeAbandonedContainersAsync(hcsctl, resource.ContainerId, logger, stopping)
                .ConfigureAwait(false);

            Progress progress = new(logger);

            // Resolved before anything is created. An empty value is rejected here, before a
            // compute system and a scratch layer exist.
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

            // Release with rm, not stop: rm removes the scratch layer, which outlives its compute
            // system. Registered immediately after create so a failure below still reclaims it.
            boot.Ledger.Add($"container {resource.ContainerId}", () => Remove(hcsctl, resource.ContainerId));

            logger.LogInformation("Layer chain resolved to {LayerCount} layer(s); scratch at {Scratch}",
                created.Chain.Count, created.Scratch);

            await hcsctl.StartAsync(resource.ContainerId, progress, stopping).ConfigureAwait(false);

            // The workload runs detached from this boot's await chain: hcsctl's exec stays
            // attached for the guest process's whole life. Cancelling this source tears the exec
            // down.
            CancellationTokenSource workloadCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("workload", () =>
            {
                workloadCts.Cancel();
                workloadCts.Dispose();
            });

            if (resource.Command is { Length: > 0 } command)
            {
                _ = Task.Run(
                    () => RunWorkloadAsync(hcsctl, boot, command, environment, new StreamProgress(logger), workloadCts.Token),
                    CancellationToken.None);
            }

            await AllocateEndpointsAsync(hcsctl, created, stopping).ConfigureAwait(false);
            await PublishEndpointsAsync(stopping).ConfigureAwait(false);

            // Boot-scoped like the workload: cancelling the source stops the polling. A poller
            // left over from a previous boot would publish a dead container's numbers over a live
            // one's.
            CancellationTokenSource statsCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            boot.Ledger.Add("statistics poller", () =>
            {
                statsCts.Cancel();
                statsCts.Dispose();
            });
            _ = Task.Run(() => PollStatisticsAsync(hcsctl, created, statsCts.Token), CancellationToken.None);

            ThrowIfExitedMidBoot(boot);

            // Running is published last. Aspire's health monitor starts when a resource reports
            // Running, and a resource with no health check annotations is declared ready at that
            // moment; WaitFor dependents would be released against a container that is not up.
            await notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
            }).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // AppHost shutting down mid-boot: not a boot failure. No drain here: unwinding
            // releases the gate to the shutdown hook, which does the full drain synchronously and
            // keeps host shutdown blocked until it finishes.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start HCS container '{Name}'.", resource.Name);

            // Release whatever the failed boot claimed so Start can be retried from the dashboard
            // without leaking a compute system or a scratch layer.
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
    /// start. Leases land within seconds of the guest coming up. A network that never leases
    /// fails the resource; it does not hang the AppHost.
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
    /// create document carries it: no polling. An ICS network (the Default Switch, the default)
    /// leases the address only after the guest is up, so there this is a bounded wait against a
    /// live HCN read. hcsctl's state.json never updates after create, so a create-time snapshot
    /// cannot serve.
    /// </para>
    /// <para>
    /// The address is the container's own on the host compute network, reachable from the host
    /// directly. There is no host port mapping: an endpoint's port is the guest's port.
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
            // address alone.
            address = created.Addresses[0].Split('/')[0];
        }
        else
        {
            // Guarded again here, not only before create, so this method cannot wait forever for
            // an endpoint that was never attached. This message is for the no-network case only;
            // a resource with WithNetwork() whose address is late must not be told to add it.
            string network = resource.NetworkName ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' declares endpoints but no network; add WithNetwork().");

            // Contract 3 reports "no endpoint" as an empty string, not a missing key.
            string endpointId = string.IsNullOrEmpty(created.Endpoint)
                ? throw new InvalidOperationException(
                    $"Resource '{resource.Name}' is on network '{network}' but hcsctl reported no " +
                    "endpoint for the container, so its address can never be discovered.")
                : created.Endpoint;

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
    /// The poll target is <c>network endpoints</c>: hcsctl's state.json (and <c>container
    /// inspect</c>, which reports that snapshot) records only the create-time address list, which
    /// on an ICS network is empty. Only HCN's own view changes when the lease lands. hcsctl has
    /// no <c>container ip</c> verb, so the container side waits here.
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

            // Case-insensitive: these are GUIDs, and HCN is not consistent about their case across
            // read paths (stats report them uppercase, the listing lowercase).
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
    /// are not faults: the container is paused, or is mid-teardown.
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

                if (stats.Properties?.Statistics is { } s)
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
    /// surfaces. Byte counts and HCS's 100-nanosecond ticks are formatted here.
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

        // No network counters: the schema-1 per-endpoint section did not survive into the v2
        // statistics this contract passes through.

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

        // Drives the orchestrator's URL processing: WithUrl callbacks and the dashboard's URL
        // list both hang off this event, and nothing raises it for a non-DCP resource.
        await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource, services), cancellationToken)
            .ConfigureAwait(false);

        // The orchestrator publishes endpoint-derived URLs as inactive (hidden); whoever
        // allocated them activates them.
        await notifications.PublishUpdateAsync(resource, s => s with
        {
            Urls = [.. s.Urls.Select(u => u with { IsInactive = false })],
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Follows the container's workload for its lifetime and reports its exit. Its output reaches
    /// the dashboard through <see cref="StreamProgress"/>, which parses hcsctl's <c>--stream-json</c>
    /// stderr framing: guest stdout/stderr land on the resource log, while hcsctl's own progress
    /// lines are debug-only.
    /// </summary>
    private async Task RunWorkloadAsync(
        HcsCtl hcsctl,
        BootRecord boot,
        string command,
        IReadOnlyDictionary<string, string> environment,
        IProgress<HcsCtlStreamRecord> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            HcsCtlExecDocument result = await hcsctl
                .ExecAsync(resource.ContainerId, command, environment, progress, cancellationToken)
                .ConfigureAwait(false);

            // The guest's exit code, not hcsctl's. hcsctl reports the two separately.
            logger.LogInformation("Container workload exited with code {ExitCode}.", result.ExitCode);
            await OnWorkloadExitedAsync(boot, result.ExitCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Torn down by a Stop, a Restart, or AppHost shutdown. Not an exit.
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
    /// Removes the container and <b>verifies it is gone by absence</b>, not by the call
    /// returning. <c>DestroyLayer</c> can report success and leave the tree.
    /// </summary>
    private void Remove(HcsCtl hcsctl, string containerId)
    {
        // Synchronous: this runs from the ledger, which the shutdown hook drains on a callback
        // that cannot await. The timeout keeps a wedged hcsctl from stalling shutdown.
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));

        hcsctl.RemoveAsync(containerId, force: true, timeout.Token).GetAwaiter().GetResult();

        HcsCtlContainerListDocument listing = hcsctl.ListAsync(timeout.Token).GetAwaiter().GetResult();
        HcsCtlContainerRow? survivor = listing.Containers
            .FirstOrDefault(c => string.Equals(c.Id, containerId, StringComparison.Ordinal));

        if (survivor is not null)
        {
            // Thrown, not logged: the ledger catches and logs it, and teardown continues. A
            // container still listed after rm is a leak; "created" is not "absent".
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
    /// Forwards hcsctl's create/start stderr to the resource's log. Those invocations run under
    /// plain <c>--json</c>, where stderr is tool progress only — the workload's framed guest output
    /// takes <see cref="StreamProgress"/> instead.
    /// </summary>
    private sealed class Progress(ILogger logger) : IProgress<string>
    {
        public void Report(string value) => logger.LogInformation("{Line}", value);
    }

    /// <summary>
    /// Routes a parsed <see cref="HcsCtlStreamRecord"/>: guest stdout/stderr to the resource log,
    /// distinguishable by stream; everything else — hcsctl's own progress — to debug.
    /// </summary>
    private sealed class StreamProgress(ILogger logger) : IProgress<HcsCtlStreamRecord>
    {
        public void Report(HcsCtlStreamRecord record)
        {
            switch (record.Stream)
            {
                case "stdout":
                case "stderr":
                    logger.LogInformation("{Stream}: {Data}", record.Stream, record.Data);
                    break;
                default:
                    logger.LogDebug("{Msg}", record.Msg);
                    break;
            }
        }
    }

    /// <summary>
    /// One boot's identity and holdings. The epoch stamps exits so a replaced container cannot
    /// speak for its successor; <see cref="Exited"/> flips when the workload exits on its own,
    /// which lets Start tell a live boot from one awaiting cleanup.
    /// </summary>
    /// <remarks>
    /// <see cref="Exited"/> is a volatile field: it is written from the workload's thread-pool
    /// thread and read from whichever thread calls Start.
    /// </remarks>
    private sealed class BootRecord(int epoch, BootLedger ledger)
    {
        public int Epoch { get; } = epoch;

        public BootLedger Ledger { get; } = ledger;

        public volatile bool Exited;
    }
}
