using System.Text.Json.Serialization;

namespace AspireHcs.Cli;

// Every result document hcsctl emits under --json is bound here, with explicit wire names. The
// names are the contract with a separate repo, so they are pinned; a rename on either side
// breaks a test.
//
// EVERY collection property reads `field ?? []`. hcsctl is Go, and Go marshals a nil slice or map
// as JSON `null`, not `[]`, so `"containers": null` and `"addresses": null` are routine output
// for "none". A plain `= []` initializer does not survive that: the deserializer assigns the null
// over it.

/// <summary>
/// The shape hcsctl emits on every failure path: <c>{"ok":false,"stage":...,"error":...}</c>.
/// Parsed from the same stdout as a success document; hcsctl puts one document there whether the
/// command worked or not.
/// </summary>
internal sealed record HcsCtlFailureDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>"usage" when nothing was attempted, "run" when it was.</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// <c>hcsctl info --json</c>. Reports what the caller's token holds, not what the machine could
/// hold.
/// </summary>
internal sealed record HcsCtlInfoDocument
{
    private static readonly Dictionary<string, string> EmptyServices = [];

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    // hcsctl's "build" and "version" fields are the host OS build, not hcsctl's own identity;
    // that is toolVersion and contractVersion.

    [JsonPropertyName("build")]
    public int HostBuild { get; init; }

    [JsonPropertyName("buildRevision")]
    public long HostBuildRevision { get; init; }

    [JsonPropertyName("version")]
    public string? HostOsVersion { get; init; }

    /// <summary>hcsctl's own release identity, e.g. <c>v0.3.0</c>.</summary>
    [JsonPropertyName("toolVersion")]
    public string? ToolVersion { get; init; }

    /// <summary>The wire-contract version AspireHcs parses against. Missing means unknown shape.</summary>
    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }

    /// <summary>Whether the calling token is elevated. Running a xenon should not require it.</summary>
    [JsonPropertyName("elevated")]
    public bool Elevated { get; init; }

    /// <summary>Hyper-V Administrators membership — the documented prerequisite for both paths.</summary>
    [JsonPropertyName("hyperVAdministrators")]
    public bool HyperVAdministrators { get; init; }

    /// <summary>Privileges enabled in the calling token, e.g. <c>SeManageVolumePrivilege</c>.</summary>
    [JsonPropertyName("privilegesHeld")]
    public IReadOnlyList<string> PrivilegesHeld { get => field ?? []; init; } = [];

    [JsonPropertyName("cimfsSupported")]
    public bool CimfsSupported { get; init; }

    [JsonPropertyName("blockCimSupported")]
    public bool BlockCimSupported { get; init; }

    /// <summary>Service name to state, e.g. <c>vmcompute</c> to <c>running</c>.</summary>
    [JsonPropertyName("services")]
    public IReadOnlyDictionary<string, string> Services { get => field ?? EmptyServices; init; } = EmptyServices;

    [JsonPropertyName("store")]
    public HcsCtlStoreInfo? Store { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<HcsCtlImageInfo> Images { get => field ?? []; init; } = [];
}

/// <summary>Where hcsctl's per-user store lives, and whether anything has been pulled into it.</summary>
internal sealed record HcsCtlStoreInfo
{
    [JsonPropertyName("root")]
    public string? Root { get; init; }

    [JsonPropertyName("exists")]
    public bool Exists { get; init; }
}

/// <summary>One materialized image in the store, with its process-isolation compatibility.</summary>
internal sealed record HcsCtlImageInfo
{
    [JsonPropertyName("ref")]
    public string? Reference { get; init; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; init; }

    /// <summary>
    /// AspireHcs does not act on it. Process isolation is out of scope here — it needs an
    /// elevated create, which the unelevated dev loop does not have.
    /// </summary>
    [JsonPropertyName("processIsolationCompatible")]
    public bool ProcessIsolationCompatible { get; init; }
}

/// <summary>
/// The fields every hcsctl result document carries. For invocations whose payload the caller
/// does not need — a command run for its effect, not its answer — binding this still checks that
/// a well-formed document arrived.
/// </summary>
internal sealed record HcsCtlResultDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }
}

/// <summary><c>hcsctl container create</c>.</summary>
internal sealed record HcsCtlContainerCreateDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("ref")]
    public string? Reference { get; init; }

    /// <summary>The container's scratch layer directory. Must be gone after teardown.</summary>
    [JsonPropertyName("scratch")]
    public string? Scratch { get; init; }

    [JsonPropertyName("utilityVM")]
    public string? UtilityVM { get; init; }

    /// <summary>The resolved layer chain, topmost first. Length &gt; 1 for a multi-layer image.</summary>
    [JsonPropertyName("chain")]
    public IReadOnlyList<string> Chain { get => field ?? []; init; } = [];

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>
    /// CIDR strings, populated only when the network assigns the address at create, as NAT does.
    /// An ICS network like the Default Switch leases the address after the guest starts, so this
    /// list is empty there and the current address must be read from <c>network endpoints</c>.
    /// </summary>
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get => field ?? []; init; } = [];
}

/// <summary>
/// <c>hcsctl network endpoints</c> — the host's HNS endpoints, read live from HCN.
/// </summary>
/// <remarks>
/// The only address source that tracks an ICS lease. hcsctl's state.json, and with it
/// <c>container inspect</c>, records the create-time address list and never updates it, so on an
/// ICS network it stays empty. This document reports the endpoint's current
/// <c>IpConfigurations</c>, unelevated.
/// </remarks>
internal sealed record HcsCtlNetworkEndpointsDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("endpoints")]
    public IReadOnlyList<HcsCtlNetworkEndpointRow> Endpoints { get => field ?? []; init; } = [];
}

/// <summary>One HNS endpoint with its current addresses.</summary>
internal sealed record HcsCtlNetworkEndpointRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("networkId")]
    public string? NetworkId { get; init; }

    [JsonPropertyName("network")]
    public string? Network { get; init; }

    /// <summary>CIDR strings. Empty on an ICS network until the guest's lease arrives.</summary>
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get => field ?? []; init; } = [];

    [JsonPropertyName("mac")]
    public string? MacAddress { get; init; }
}

/// <summary><c>hcsctl network ls</c> — the host's HNS networks, read live from HCN.</summary>
internal sealed record HcsCtlNetworkListDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("networks")]
    public IReadOnlyList<HcsCtlNetworkRow> Networks { get => field ?? []; init; } = [];
}

/// <summary>One HNS network.</summary>
internal sealed record HcsCtlNetworkRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>HNS's network flavor: <c>ICS</c>, <c>NAT</c>, <c>Transparent</c>, ...</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>CIDR strings, e.g. <c>172.18.176.0/20</c>. Empty for a Transparent network.</summary>
    [JsonPropertyName("subnets")]
    public IReadOnlyList<string> Subnets { get => field ?? []; init; } = [];

    [JsonPropertyName("endpoints")]
    public int EndpointCount { get; init; }
}

/// <summary>
/// <c>hcsctl network inspect</c> — one network in full. Only the parts AspireHcs reads are
/// bound; the document carries more (policies, DNS, flags).
/// </summary>
internal sealed record HcsCtlNetworkInspectDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("ipams")]
    public IReadOnlyList<HcsCtlNetworkIpam> Ipams { get => field ?? []; init; } = [];
}

/// <summary>One IPAM block: how addresses on the network are assigned.</summary>
internal sealed record HcsCtlNetworkIpam
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("subnets")]
    public IReadOnlyList<HcsCtlNetworkSubnet> Subnets { get => field ?? []; init; } = [];
}

/// <summary>One subnet with its routes.</summary>
internal sealed record HcsCtlNetworkSubnet
{
    /// <summary>CIDR, e.g. <c>172.18.176.0/20</c>.</summary>
    [JsonPropertyName("prefix")]
    public string? Prefix { get; init; }

    [JsonPropertyName("routes")]
    public IReadOnlyList<HcsCtlNetworkRoute> Routes { get => field ?? []; init; } = [];
}

/// <summary>
/// One route. The default route's next hop is the gateway a guest reaches the host through —
/// HCN stores it here, not as a property of the prefix.
/// </summary>
internal sealed record HcsCtlNetworkRoute
{
    [JsonPropertyName("nextHop")]
    public string? NextHop { get; init; }

    [JsonPropertyName("destinationPrefix")]
    public string? DestinationPrefix { get; init; }

    [JsonPropertyName("metric")]
    public int Metric { get; init; }
}

/// <summary><c>hcsctl guest exec</c> — one command run inside a VM guest, over hvsocket.</summary>
internal sealed record HcsCtlGuestExecDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("vmId")]
    public string? VmId { get; init; }

    /// <summary>The command line that ran, echoed back for attribution.</summary>
    [JsonPropertyName("ran")]
    public string? Ran { get; init; }

    /// <summary>
    /// The guest process's own exit code — never hcsctl's, which reports the two separately for
    /// exactly this reason. <c>-1</c> when the guest never produced one.
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; init; }

    /// <summary>The agent's error text for a process that ended abnormally, when there is one.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }
}

/// <summary>
/// <c>hcsctl guest info</c> — whether the guest agent answered over hvsocket, and what it said
/// about itself. Only the fields <c>guest forward</c>'s agent-presence check reads are bound.
/// </summary>
internal sealed record HcsCtlGuestInfoDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>False with no <c>hcsguest</c> in the image, or a guest that has not answered yet.</summary>
    [JsonPropertyName("reachable")]
    public bool Reachable { get; init; }

    /// <summary>The reading behind <see cref="Reachable"/>: <c>absent</c>, <c>unreachable</c>, or <c>ready</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// <c>hcsctl guest forward</c> — the one document this long-running command emits, as soon as
/// its listener is up and before it starts relaying connections.
/// </summary>
internal sealed record HcsCtlGuestForwardDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("vmId")]
    public string? VmId { get; init; }

    /// <summary>
    /// The host address actually bound, e.g. <c>127.0.0.1:54321</c> — the real port even when
    /// <c>--listen 127.0.0.1:0</c> asked for an OS-assigned one.
    /// </summary>
    [JsonPropertyName("listen")]
    public string? Listen { get; init; }

    [JsonPropertyName("guestPort")]
    public int GuestPort { get; init; }
}

/// <summary><c>hcsctl container ls</c> — the store and HCS reconciled.</summary>
internal sealed record HcsCtlContainerListDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("containers")]
    public IReadOnlyList<HcsCtlContainerRow> Containers { get => field ?? []; init; } = [];
}

/// <summary>One row of <c>container ls</c>.</summary>
internal sealed record HcsCtlContainerRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("ref")]
    public string? Reference { get; init; }

    /// <summary>
    /// HCS's view, with two values hcsctl supplies rather than HCS: <c>absent</c> when no compute
    /// system exists, and <c>created</c> for one created but never started, which HCS itself
    /// reports as a <em>blank</em> state. See <see cref="HcsCtlContainerState"/>.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

/// <summary>The two states <c>container ls</c> reports that do not come from HCS.</summary>
internal static class HcsCtlContainerState
{
    /// <summary>No compute system at all. The only state that means "torn down".</summary>
    public const string Absent = "absent";

    /// <summary>Created but never started — HCS reports this as a blank state. Not absent.</summary>
    public const string Created = "created";
}

/// <summary><c>hcsctl container exec</c> and <c>container run</c>.</summary>
internal sealed record HcsCtlExecDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("cmd")]
    public string? Command { get; init; }

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    /// <summary>
    /// The guest process's own exit code, never hcsctl's. Null when the process was killed on a
    /// timeout; the guest never produced one.
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; init; }

    /// <summary>The guest's output, complete but only at exit. Live output arrives on stderr.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; init; }
}

// The statistics document is a raw v2 HCS property passthrough (contract 3): hcsctl asks HCS
// for the Statistics property and puts the whole reply, unmodified, under "statistics". The
// names inside are PascalCase because they are HCS's, not hcsctl's. HCS omits zero counters,
// so an absent key and zero mean the same thing and every numeric defaults to zero.
//
// There is no network section: the schema-1 per-endpoint counters did not survive into v2.

/// <summary><c>hcsctl container stats</c>.</summary>
internal sealed record HcsCtlStatsDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The raw v2 property reply. The counters are one level down, under Statistics.</summary>
    [JsonPropertyName("statistics")]
    public HcsCtlContainerProperties? Properties { get; init; }
}

/// <summary>
/// The v2 property reply's envelope. It carries system identity fields (Id, SystemType, State…)
/// that AspireHcs does not read; only the nested Statistics object is bound.
/// </summary>
internal sealed record HcsCtlContainerProperties
{
    [JsonPropertyName("Statistics")]
    public HcsCtlStatistics? Statistics { get; init; }
}

/// <summary>What HCS reports about a running compute system.</summary>
internal sealed record HcsCtlStatistics
{
    [JsonPropertyName("Timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("ContainerStartTime")]
    public DateTimeOffset? ContainerStartTime { get; init; }

    /// <summary>Uptime in 100-nanosecond ticks, the unit HCS reports.</summary>
    [JsonPropertyName("Uptime100ns")]
    public long Uptime100ns { get; init; }

    [JsonPropertyName("Memory")]
    public HcsCtlMemoryStats? Memory { get; init; }

    [JsonPropertyName("Processor")]
    public HcsCtlProcessorStats? Processor { get; init; }

    [JsonPropertyName("Storage")]
    public HcsCtlStorageStats? Storage { get; init; }

    /// <summary>Uptime as a duration. HCS ticks are 100 ns, so this is ticks × 100 ns.</summary>
    public TimeSpan Uptime => TimeSpan.FromTicks(Uptime100ns);
}

internal sealed record HcsCtlMemoryStats
{
    [JsonPropertyName("MemoryUsageCommitBytes")]
    public long CommitBytes { get; init; }

    [JsonPropertyName("MemoryUsageCommitPeakBytes")]
    public long CommitPeakBytes { get; init; }

    [JsonPropertyName("MemoryUsagePrivateWorkingSetBytes")]
    public long PrivateWorkingSetBytes { get; init; }
}

internal sealed record HcsCtlProcessorStats
{
    [JsonPropertyName("TotalRuntime100ns")]
    public long TotalRuntime100ns { get; init; }

    [JsonPropertyName("RuntimeUser100ns")]
    public long UserRuntime100ns { get; init; }

    [JsonPropertyName("RuntimeKernel100ns")]
    public long KernelRuntime100ns { get; init; }

    public TimeSpan TotalRuntime => TimeSpan.FromTicks(TotalRuntime100ns);
}

internal sealed record HcsCtlStorageStats
{
    [JsonPropertyName("ReadCountNormalized")]
    public long ReadCount { get; init; }

    [JsonPropertyName("ReadSizeBytes")]
    public long ReadBytes { get; init; }

    [JsonPropertyName("WriteCountNormalized")]
    public long WriteCount { get; init; }

    [JsonPropertyName("WriteSizeBytes")]
    public long WriteBytes { get; init; }
}

/// <summary><c>hcsctl container ps</c> — what is running inside the guest.</summary>
internal sealed record HcsCtlProcessListDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("processes")]
    public IReadOnlyList<HcsCtlGuestProcess> Processes { get => field ?? []; init; } = [];
}

/// <summary>
/// One process inside the guest. Contract 3 reports exactly these five fields, always present:
/// hcsctl parses the v2 ProcessList property into a typed row and re-emits it.
/// </summary>
/// <remarks>
/// <b>There is no parent process id.</b> HCS does not report one, so this is a flat list and
/// cannot be presented as a tree.
/// </remarks>
internal sealed record HcsCtlGuestProcess
{
    [JsonPropertyName("ProcessId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("ImageName")]
    public string? ImageName { get; init; }

    [JsonPropertyName("MemoryCommitBytes")]
    public long MemoryCommitBytes { get; init; }

    [JsonPropertyName("KernelTime100ns")]
    public long KernelTime100ns { get; init; }

    [JsonPropertyName("UserTime100ns")]
    public long UserTime100ns { get; init; }

    /// <summary>Kernel plus user time.</summary>
    public TimeSpan CpuTime => TimeSpan.FromTicks(KernelTime100ns + UserTime100ns);
}

/// <summary><c>hcsctl vm create</c>.</summary>
internal sealed record HcsCtlVmCreateDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The differencing child this VM boots, not the base image it was made from.</summary>
    [JsonPropertyName("diskPath")]
    public string? DiskPath { get; init; }

    [JsonPropertyName("serialPipe")]
    public string? SerialPipe { get; init; }

    [JsonPropertyName("network")]
    public string? Network { get; init; }

    [JsonPropertyName("endpointId")]
    public string? EndpointId { get; init; }

    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; init; }

    /// <summary>
    /// Always empty: an HCN endpoint carries no address when it is created, none when it is
    /// attached to a NIC, and none while the VM runs without a guest. The address comes from the
    /// guest's DHCP client; <see cref="HcsCtlVirtualMachines.WaitForAddressAsync"/> produces one.
    /// </summary>
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get => field ?? []; init; } = [];
}

/// <summary><c>hcsctl vm start</c>.</summary>
internal sealed record HcsCtlVmStartDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    /// <summary>
    /// The firmware is running. It does <b>not</b> mean the guest OS is up — unlike a container,
    /// where start returning is the guest being ready.
    /// </summary>
    [JsonPropertyName("started")]
    public bool Started { get; init; }

    /// <summary>
    /// The compute system had to be rebuilt from hcsctl's store record because the previous one
    /// exited. Same disk, so this is a power cycle rather than a fresh VM.
    /// </summary>
    [JsonPropertyName("recreated")]
    public bool Recreated { get; init; }
}

/// <summary><c>hcsctl vm ip</c> — the address the guest leased.</summary>
internal sealed record HcsCtlVmAddressDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("endpointId")]
    public string? EndpointId { get; init; }

    /// <summary>CIDR strings, e.g. <c>172.18.187.241/20</c>. Never empty on success.</summary>
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get => field ?? []; init; } = [];

    [JsonPropertyName("waitedMs")]
    public long WaitedMs { get; init; }
}

/// <summary><c>hcsctl vm ls</c>, and with <c>--all</c> the host's compute systems too.</summary>
internal sealed record HcsCtlVmListDocument
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>VMs in the store this AppHost is pointed at.</summary>
    [JsonPropertyName("vms")]
    public IReadOnlyList<HcsCtlVmRow> VirtualMachines { get => field ?? []; init; } = [];

    /// <summary>
    /// Every compute system on the host, present only when <c>--all</c> was passed. Other tools'
    /// VMs are in here — WSL's, Hyper-V Manager's — so the owner is what separates ours.
    /// </summary>
    [JsonPropertyName("systems")]
    public IReadOnlyList<HcsCtlComputeSystemRow> Systems { get => field ?? []; init; } = [];
}

/// <summary>One row of <c>vm ls</c>.</summary>
internal sealed record HcsCtlVmRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>See <see cref="HcsCtlVmState"/>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>
    /// Opaque key/value pairs this AppHost stamped at create time. hcsctl never interprets them;
    /// run ownership is the consumer's policy.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string> Labels { get => field ?? EmptyLabels; init; } = EmptyLabels;

    private static readonly Dictionary<string, string> EmptyLabels = [];
}

/// <summary>One compute system from <c>vm ls --all</c>, straight from HcsEnumerateComputeSystems.</summary>
internal sealed record HcsCtlComputeSystemRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>What the system's own document set. hcsctl's VMs say <c>hcsctl</c>.</summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("runtimeId")]
    public string? RuntimeId { get; init; }

    /// <summary>Blank for a system created but never started — HCS reports no state for one.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

/// <summary>
/// The states <c>vm ls</c> reports. A container that is not running is <c>absent</c> because its
/// scratch is gone; a VM keeps its disk and store record, so it is <c>stopped</c> and can be
/// started again.
/// </summary>
internal static class HcsCtlVmState
{
    /// <summary>No compute system. The disk and the record survive, so this is restartable.</summary>
    public const string Stopped = "stopped";

    /// <summary>Created but never started. HCS reports no state at all for one.</summary>
    public const string Created = "created";

    public const string Running = "running";
}

/// <summary>
/// One framed line of <c>hcsctl --stream-json</c> stderr. Progress is
/// <c>{"stream":"progress","msg":…}</c>; guest output is <c>{"stream":"stdout"|"stderr","data":…}</c>;
/// <c>{"stream":"exec","event":"started","pid":N}</c> marks the guest process existing, before
/// any of its output.
/// </summary>
internal sealed record HcsCtlStreamRecord
{
    [JsonPropertyName("stream")]
    public string? Stream { get; init; }

    [JsonPropertyName("msg")]
    public string? Msg { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("pid")]
    public long? Pid { get; init; }

    /// <summary>The exec's guest process now exists. This is what the pause gate latches.</summary>
    public bool IsExecStarted =>
        string.Equals(Stream, "exec", StringComparison.Ordinal)
        && string.Equals(Event, "started", StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(HcsCtlStatsDocument))]
[JsonSerializable(typeof(HcsCtlProcessListDocument))]
[JsonSerializable(typeof(HcsCtlFailureDocument))]
[JsonSerializable(typeof(HcsCtlResultDocument))]
[JsonSerializable(typeof(HcsCtlInfoDocument))]
[JsonSerializable(typeof(HcsCtlContainerCreateDocument))]
[JsonSerializable(typeof(HcsCtlContainerListDocument))]
[JsonSerializable(typeof(HcsCtlNetworkEndpointsDocument))]
[JsonSerializable(typeof(HcsCtlNetworkListDocument))]
[JsonSerializable(typeof(HcsCtlNetworkInspectDocument))]
[JsonSerializable(typeof(HcsCtlGuestExecDocument))]
[JsonSerializable(typeof(HcsCtlGuestInfoDocument))]
[JsonSerializable(typeof(HcsCtlGuestForwardDocument))]
[JsonSerializable(typeof(HcsCtlExecDocument))]
[JsonSerializable(typeof(HcsCtlStreamRecord))]
[JsonSerializable(typeof(HcsCtlVmCreateDocument))]
[JsonSerializable(typeof(HcsCtlVmStartDocument))]
[JsonSerializable(typeof(HcsCtlVmAddressDocument))]
[JsonSerializable(typeof(HcsCtlVmListDocument))]
internal sealed partial class HcsCtlJsonContext : JsonSerializerContext;
