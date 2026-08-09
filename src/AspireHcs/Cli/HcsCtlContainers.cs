using System.Globalization;

namespace AspireHcs.Cli;

/// <summary>
/// The container verbs, as methods. Keeping argv construction here rather than at each call site
/// means option spellings are wrong in one place or none — and an option spelled wrong is exit
/// 64, which is indistinguishable from a genuine argument bug in a resource's configuration.
/// </summary>
internal static class HcsCtlContainers
{
    /// <summary>
    /// Creates the compute system and its scratch. Does not start it.
    /// </summary>
    /// <remarks>
    /// Mounts and scratch size belong here rather than on exec: they are properties of the
    /// compute system's document, fixed when it is created. Environment is the other way round —
    /// hcsctl's <c>create</c> takes no <c>--env</c> at all. See <see cref="ExecAsync"/>.
    /// </remarks>
    public static Task<HcsCtlContainerCreateDocument> CreateAsync(
        this HcsCtl hcsctl,
        string id,
        string imageReference,
        int processorCount,
        int memoryMb,
        int? scratchSizeGigabytes = null,
        IEnumerable<string>? mounts = null,
        string? network = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments =
        [
            "container", "create",
            "--id", id,
            "--ref", imageReference,
            "--cpus", processorCount.ToString(CultureInfo.InvariantCulture),
            "--memory-mb", memoryMb.ToString(CultureInfo.InvariantCulture),
        ];

        if (!string.IsNullOrEmpty(network))
        {
            // The endpoint is created here, at create time. Whether the result document carries
            // an address depends on the network: NAT assigns one at create, while an ICS network
            // leases it only after the guest starts, so the document's list is empty there and
            // the current address comes from `network endpoints` (#63, hcsctl#43).
            arguments.Add("--network");
            arguments.Add(network);
        }

        if (scratchSizeGigabytes is { } gigabytes)
        {
            // hcsctl requires a unit and rejects a bare number: a size is where guessing wrong
            // costs tens of gigabytes.
            arguments.Add("--scratch-size");
            arguments.Add($"{gigabytes.ToString(CultureInfo.InvariantCulture)}GB");
        }

        foreach (string mount in mounts ?? [])
        {
            arguments.Add("--mount");
            arguments.Add(mount);
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlContainerCreateDocument, progress, cancellationToken);
    }

    public static Task<HcsCtlResultDocument> StartAsync(
        this HcsCtl hcsctl, string id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "start", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlResultDocument, progress, cancellationToken);
    }

    /// <summary>
    /// Runs a command in the guest and waits for it. No <c>--timeout</c> is passed, so this does
    /// not return until the guest process exits — which is how a long-running workload is
    /// followed. Cancel the token to tear it down.
    /// </summary>
    /// <remarks>
    /// Environment is set here, per guest process, because hcsctl's <c>container create</c> takes
    /// no <c>--env</c>. Every exec against a container therefore needs the same environment
    /// passed again — nothing on the compute system remembers it — which is why the caller keeps
    /// the resolved set rather than resolving it per call.
    /// </remarks>
    public static Task<HcsCtlExecDocument> ExecAsync(
        this HcsCtl hcsctl,
        string id,
        string commandLine,
        IReadOnlyDictionary<string, string>? environment = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["container", "exec", "--id", id, "--cmd", commandLine];

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            arguments.Add("--env");
            arguments.Add($"{name}={value}");
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlExecDocument, progress, cancellationToken);
    }

    public static Task<HcsCtlResultDocument> StopAsync(
        this HcsCtl hcsctl, string id, bool force = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = force
            ? ["container", "stop", "--id", id, "--force"]
            : ["container", "stop", "--id", id];

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Removes the compute system, its scratch and any endpoint. Releasable from a fresh process,
    /// which is what makes crash scavenging possible at all.
    /// </summary>
    public static Task<HcsCtlResultDocument> RemoveAsync(
        this HcsCtl hcsctl, string id, bool force = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = force
            ? ["container", "rm", "--id", id, "--force"]
            : ["container", "rm", "--id", id];

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    /// <summary>Uptime, memory, CPU, storage and per-endpoint network counters.</summary>
    public static Task<HcsCtlStatsDocument> StatsAsync(
        this HcsCtl hcsctl, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "stats", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlStatsDocument, cancellationToken: cancellationToken);
    }

    /// <summary>What is running inside the guest. Flat — HCS reports no parent pids.</summary>
    public static Task<HcsCtlProcessListDocument> ProcessListAsync(
        this HcsCtl hcsctl, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "ps", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlProcessListDocument, cancellationToken: cancellationToken);
    }

    /// <summary>Suspends the container. A paused workload stops making progress.</summary>
    public static Task<HcsCtlResultDocument> PauseAsync(
        this HcsCtl hcsctl, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "pause", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    public static Task<HcsCtlResultDocument> ResumeAsync(
        this HcsCtl hcsctl, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "resume", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    public static Task<HcsCtlContainerListDocument> ListAsync(
        this HcsCtl hcsctl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "ls"],
            HcsCtlJsonContext.Default.HcsCtlContainerListDocument, cancellationToken: cancellationToken);
    }
}
