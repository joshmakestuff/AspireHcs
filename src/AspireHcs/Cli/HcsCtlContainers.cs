using System.Globalization;

namespace AspireHcs.Cli;

/// <summary>
/// The container verbs, as methods. All argv construction for the group is here.
/// </summary>
internal static class HcsCtlContainers
{
    /// <summary>
    /// Creates the compute system and its scratch. Does not start it.
    /// </summary>
    /// <remarks>
    /// Mounts and scratch size are properties of the compute system's document, fixed when it is
    /// created. hcsctl's <c>create</c> takes no <c>--env</c>; see <see cref="ExecAsync"/>.
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
            // The endpoint is created here, at create time. NAT assigns an address at create;
            // an ICS network leases one only after the guest starts, so the document's address
            // list is empty there and the current address comes from `network endpoints`.
            arguments.Add("--network");
            arguments.Add(network);
        }

        if (scratchSizeGigabytes is { } gigabytes)
        {
            // hcsctl requires a unit and rejects a bare number.
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
    /// Environment is set here, per guest process; hcsctl's <c>container create</c> takes no
    /// <c>--env</c>. Every exec against a container needs the same environment passed again.
    /// </remarks>
    public static Task<HcsCtlExecDocument> ExecAsync(
        this HcsCtl hcsctl,
        string id,
        string commandLine,
        IReadOnlyDictionary<string, string>? environment = null,
        IProgress<HcsCtlStreamRecord>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["container", "exec", "--id", id, "--cmd", commandLine];

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            arguments.Add("--env");
            arguments.Add($"{name}={value}");
        }

        return hcsctl.InvokeStreamingAsync(arguments, HcsCtlJsonContext.Default.HcsCtlExecDocument, progress, cancellationToken);
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
    /// Removes the compute system, its scratch and any endpoint. Works from a fresh process, which
    /// crash scavenging depends on.
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
