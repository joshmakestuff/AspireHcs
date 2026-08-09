using System.Globalization;

namespace AspireHcs.Cli;

/// <summary>
/// The <c>vm</c> verbs, as methods. Same reason as <see cref="HcsCtlContainers"/>: an option
/// spelled wrong is exit 64, which reads exactly like a bad value in a resource's configuration.
///
/// A full VM is not a container, and three differences drive everything here:
/// <list type="bullet">
///   <item><c>vm start</c> returning means the firmware is running, not that the guest is up.</item>
///   <item>The address comes from the guest's own DHCP client and is not knowable before it
///     boots, so it is waited for rather than read (hcsctl#43).</item>
///   <item>A VM that is not running still exists — disk and store record — so it is
///     <c>stopped</c> rather than gone, and <c>vm rm</c> is what removes it.</item>
/// </list>
/// </summary>
internal static class HcsCtlVirtualMachines
{
    /// <summary>
    /// The <c>--network</c> value that asks hcsctl to choose. It resolves the Hyper-V Default
    /// Switch, the ICS network whose built-in DHCP serves an arbitrary guest image, and refuses to
    /// guess when a host has several ICS networks. A network genuinely named "default" wins over
    /// it, which is hcsctl's rule and not ours to second-guess.
    /// </summary>
    public const string DefaultNetwork = "default";

    /// <summary>
    /// Creates the compute system and its differencing disk, attaches a DHCP endpoint, and does
    /// not start it.
    /// </summary>
    /// <param name="labels">
    /// Opaque key/value pairs recorded in hcsctl's store and never interpreted by it. This is how
    /// a run stamps its identity onto a VM so a later run can prove the owner is dead before
    /// reclaiming anything (hcsctl#44).
    /// </param>
    public static Task<HcsCtlVmCreateDocument> CreateVmAsync(
        this HcsCtl hcsctl,
        string id,
        string vhdxPath,
        int processorCount,
        int memoryMb,
        string? network = DefaultNetwork,
        string? serialPipe = null,
        IReadOnlyDictionary<string, string>? labels = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments =
        [
            "vm", "create",
            "--id", id,
            "--vhdx", vhdxPath,
            "--cpus", processorCount.ToString(CultureInfo.InvariantCulture),
            "--memory-mb", memoryMb.ToString(CultureInfo.InvariantCulture),
        ];

        if (!string.IsNullOrEmpty(network))
        {
            arguments.Add("--network");
            arguments.Add(network);
        }

        if (!string.IsNullOrEmpty(serialPipe))
        {
            arguments.Add("--serial-pipe");
            arguments.Add(serialPipe);
        }

        foreach ((string key, string value) in labels ?? new Dictionary<string, string>())
        {
            arguments.Add("--label");
            arguments.Add($"{key}={value}");
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlVmCreateDocument, progress, cancellationToken);
    }

    /// <summary>
    /// Starts the VM. Returns as soon as the firmware is running — the guest OS has not booted,
    /// so nothing here is a readiness signal. Wait for the address, or ask the guest agent.
    /// </summary>
    public static Task<HcsCtlVmStartDocument> StartVmAsync(
        this HcsCtl hcsctl, string id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["vm", "start", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlVmStartDocument, progress, cancellationToken);
    }

    /// <summary>
    /// Waits for the address the guest's DHCP client leases, and returns it.
    /// </summary>
    /// <remarks>
    /// This blocks for as long as it takes, which is the point. Measured against a Rocky 10 guest
    /// on the Default Switch: about 16 s from start on a cold boot and 10 s on a restart. hcsctl
    /// polls the endpoint; nothing on the host can produce the address sooner.
    ///
    /// A timeout here fails the resource rather than returning empty, because a VM with no address
    /// cannot serve an endpoint and reporting it as started would be a lie.
    /// </remarks>
    public static Task<HcsCtlVmAddressDocument> WaitForAddressAsync(
        this HcsCtl hcsctl,
        string id,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(
            ["vm", "ip", "--id", id, "--timeout", FormatDuration(timeout)],
            HcsCtlJsonContext.Default.HcsCtlVmAddressDocument, progress, cancellationToken);
    }

    /// <summary>Asks the guest to shut down, or powers it off with <paramref name="force"/>.</summary>
    public static Task<HcsCtlResultDocument> StopVmAsync(
        this HcsCtl hcsctl, string id, bool force = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = force
            ? ["vm", "stop", "--id", id, "--force"]
            : ["vm", "stop", "--id", id];

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Terminates the VM and removes what hcsctl made: the differencing disk, the store record and
    /// the HCN endpoint. A VM created with <c>--no-copy-on-write</c> keeps its base image.
    /// </summary>
    /// <remarks>
    /// Works from a process that did not create the VM, which is what makes reclaiming a crashed
    /// run's leftovers possible at all. The endpoint is host-global and outlives every process, so
    /// this is the only thing that ever deletes one.
    /// </remarks>
    public static Task<HcsCtlResultDocument> RemoveVmAsync(
        this HcsCtl hcsctl, string id, bool force = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = force
            ? ["vm", "rm", "--id", id, "--force"]
            : ["vm", "rm", "--id", id];

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlResultDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The store's VMs, and with <paramref name="includeHostSystems"/> every compute system on the
    /// host as well. Both halves are needed to judge a leftover: a VM row carries the labels this
    /// AppHost stamped, and the systems list says whether anything is still running under that id.
    /// </summary>
    public static Task<HcsCtlVmListDocument> ListVmsAsync(
        this HcsCtl hcsctl, bool includeHostSystems = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = includeHostSystems ? ["vm", "ls", "--all"] : ["vm", "ls"];
        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlVmListDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Formats a duration the way Go's <c>time.ParseDuration</c> reads it. Seconds only, because
    /// .NET's own round-trip formats are not durations hcsctl accepts and a rejected one is exit
    /// 64 — an argument bug dressed up as a configuration error.
    /// </summary>
    private static string FormatDuration(TimeSpan timeout)
        => $"{Math.Max(1, (long)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s";
}
