using System.Globalization;

namespace AspireHcs.Cli;

/// <summary>
/// The container verbs, as methods. Keeping argv construction here rather than at each call site
/// means option spellings are wrong in one place or none — and an option spelled wrong is exit
/// 64, which is indistinguishable from a genuine argument bug in a resource's configuration.
/// </summary>
internal static class HcsCtlContainers
{
    /// <summary>Creates the compute system and its scratch. Does not start it.</summary>
    public static Task<HcsCtlContainerCreateDocument> CreateAsync(
        this HcsCtl hcsctl,
        string id,
        string imageReference,
        int processorCount,
        int memoryMb,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        string[] arguments =
        [
            "container", "create",
            "--id", id,
            "--ref", imageReference,
            "--cpus", processorCount.ToString(CultureInfo.InvariantCulture),
            "--memory-mb", memoryMb.ToString(CultureInfo.InvariantCulture),
        ];

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
    public static Task<HcsCtlExecDocument> ExecAsync(
        this HcsCtl hcsctl,
        string id,
        string commandLine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "exec", "--id", id, "--cmd", commandLine],
            HcsCtlJsonContext.Default.HcsCtlExecDocument, progress, cancellationToken);
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

    public static Task<HcsCtlContainerListDocument> ListAsync(
        this HcsCtl hcsctl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["container", "ls"],
            HcsCtlJsonContext.Default.HcsCtlContainerListDocument, cancellationToken: cancellationToken);
    }
}
