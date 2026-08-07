using System.Text.Json.Serialization;

namespace AspireHcs.Cli;

// Every result document hcsctl emits under --json is bound here, with explicit wire names. The
// names are the contract with a separate repo, so they are pinned rather than derived from a
// naming policy: a rename on either side should break a test, not silently bind to null.
//
// EVERY collection property reads `field ?? []`, and that is not defensive habit. hcsctl is Go,
// and Go marshals a nil slice or map as JSON `null`, not `[]` — so `"containers": null` and
// `"addresses": null` are both normal, routine output for "none". A plain `= []` initializer does
// not survive that: the deserializer assigns the null straight over it. Measured the hard way,
// by a teardown verification that threw instead of verifying.

/// <summary>
/// The shape hcsctl emits on every failure path: <c>{"ok":false,"stage":...,"error":...}</c>.
/// Deliberately parsed from the same stdout as a success document — hcsctl's contract is that a
/// consumer parses one shape whether the command worked or not.
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
/// <c>hcsctl info --json</c>. This is the whole preflight: it reports what the caller's token
/// actually holds rather than what the machine could hold, which is the distinction that makes
/// every elevation question in this repo answerable.
/// </summary>
internal sealed record HcsCtlInfoDocument
{
    private static readonly Dictionary<string, string> EmptyServices = [];

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    // Named "host" deliberately. hcsctl's "version" field is the *host OS* build, not hcsctl's
    // own version — the tool has no machine-readable version yet (hcsctl#29), and a property
    // called Version here would be read as one.

    [JsonPropertyName("build")]
    public int HostBuild { get; init; }

    [JsonPropertyName("buildRevision")]
    public long HostBuildRevision { get; init; }

    [JsonPropertyName("version")]
    public string? HostOsVersion { get; init; }

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
    /// Reported for completeness only. AspireHcs never acts on it: process isolation is out of
    /// scope permanently (#46), and hcsctl does not implement it at all.
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

    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get => field ?? []; init; } = [];
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
    /// system exists, and <c>created</c> for one created but never started — which HCS itself
    /// reports as a <em>blank</em> state. Conflating blank with absent is the trap named in #48,
    /// and it is hcsctl that keeps them apart here. See <see cref="HcsCtlContainerState"/>.
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
    /// The guest process's own exit code — never hcsctl's. Null when the process was killed on
    /// a timeout: the guest never produced one, and inventing it would make "we gave up" look
    /// like "it exited".
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; init; }

    /// <summary>The guest's output, complete but only at exit. Live output arrives on stderr.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(HcsCtlFailureDocument))]
[JsonSerializable(typeof(HcsCtlResultDocument))]
[JsonSerializable(typeof(HcsCtlInfoDocument))]
[JsonSerializable(typeof(HcsCtlContainerCreateDocument))]
[JsonSerializable(typeof(HcsCtlContainerListDocument))]
[JsonSerializable(typeof(HcsCtlExecDocument))]
internal sealed partial class HcsCtlJsonContext : JsonSerializerContext;
