using System.Globalization;

namespace AspireHcs.Cli;

/// <summary>
/// The <c>vm</c> verbs, as methods. All argv construction for the group is here.
///
/// A full VM is not a container. Three differences apply:
/// <list type="bullet">
///   <item><c>vm start</c> returning means the firmware is running, not that the guest is up.</item>
///   <item>The address comes from the guest's own DHCP client and is not known before it
///     boots, so it is waited for.</item>
///   <item>A VM that is not running still exists — disk and store record — so it is
///     <c>stopped</c> rather than gone, and <c>vm rm</c> is what removes it.</item>
/// </list>
/// </summary>
internal static class HcsCtlVirtualMachines
{
    /// <summary>
    /// Creates the compute system and its differencing disks, attaches an endpoint, and does
    /// not start it. See <see cref="HcsCtlVmCreateOptions"/> for what each option means.
    /// </summary>
    public static Task<HcsCtlVmCreateDocument> CreateVmAsync(
        this HcsCtl hcsctl,
        HcsCtlVmCreateOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        ArgumentNullException.ThrowIfNull(options);
        return hcsctl.InvokeAsync(BuildCreateArguments(options), HcsCtlJsonContext.Default.HcsCtlVmCreateDocument, progress, cancellationToken);
    }

    /// <summary>The <c>vm create</c> argv for <paramref name="options"/>. Pure; pinned by tests.</summary>
    internal static List<string> BuildCreateArguments(HcsCtlVmCreateOptions options)
    {
        List<string> arguments =
        [
            "vm", "create",
            "--id", options.Id,
            "--vhdx", options.VhdxPath,
            "--cpus", options.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            "--memory-mb", options.MemoryMb.ToString(CultureInfo.InvariantCulture),
        ];

        foreach (string disk in options.DataDisks)
        {
            arguments.Add("--disk");
            arguments.Add(disk);
        }

        if (!string.IsNullOrEmpty(options.Network))
        {
            arguments.Add("--network");
            arguments.Add(options.Network);
        }

        if (!string.IsNullOrEmpty(options.MacAddress))
        {
            arguments.Add("--mac");
            arguments.Add(options.MacAddress);
        }

        if (options.VlanId is { } vlan)
        {
            arguments.Add("--vlan");
            arguments.Add(vlan.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(options.SerialPipe))
        {
            arguments.Add("--serial-pipe");
            arguments.Add(options.SerialPipe);
        }

        foreach ((string key, string value) in options.Labels ?? new Dictionary<string, string>())
        {
            arguments.Add("--label");
            arguments.Add($"{key}={value}");
        }

        return arguments;
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
    /// Blocks until the lease lands. hcsctl polls the endpoint. A timeout fails the call; it
    /// does not return an empty address.
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
    /// Works from a process that did not create the VM; reclaiming a crashed run's leftovers
    /// depends on this. The endpoint is host-global and outlives every process; this is the only
    /// call that deletes one.
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
    /// Formats a duration the way Go's <c>time.ParseDuration</c> reads it. Seconds only; .NET's
    /// own round-trip formats are not durations hcsctl accepts.
    /// </summary>
    internal static string FormatDuration(TimeSpan timeout)
        => $"{Math.Max(1, (long)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s";
}

/// <summary>
/// Everything <c>vm create</c> takes. Values pass through to hcsctl verbatim; validation belongs
/// to the caller (the builder methods normalize, the orchestrator checks shape).
/// </summary>
internal sealed record HcsCtlVmCreateOptions
{
    public required string Id { get; init; }

    /// <summary>The boot disk, attached at SCSI LUN 0.</summary>
    public required string VhdxPath { get; init; }

    /// <summary>
    /// Extra VHDXs attached at SCSI LUN 1..n in order. They share the boot disk's copy-on-write
    /// policy: hcsctl boots differencing children and never writes the bases.
    /// </summary>
    public IReadOnlyList<string> DataDisks { get; init; } = [];

    public int ProcessorCount { get; init; } = 2;

    public int MemoryMb { get; init; } = 2048;

    /// <summary>
    /// Null means no NIC at all. Networks are named literally; hcsctl's <c>--network default</c>
    /// sentinel is not used. The shared default is <see cref="HcsNetwork"/>.
    /// </summary>
    public string? Network { get; init; }

    /// <summary>
    /// NIC MAC, normalized <c>XX-XX-XX-XX-XX-XX</c>; null lets hcsctl generate one. For guests
    /// whose network config is pinned to a specific address.
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>Access VLAN for the NIC's switch port, 1..4094; null means untagged.</summary>
    public int? VlanId { get; init; }

    public string? SerialPipe { get; init; }

    /// <summary>
    /// Opaque key/value pairs recorded in hcsctl's store and never interpreted by it. A run stamps
    /// its identity onto a VM with these, so a later run can prove the owner is dead before it
    /// reclaims anything.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}
