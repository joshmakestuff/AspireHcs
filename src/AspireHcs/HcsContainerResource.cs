using System.Globalization;

// Resource types live in Aspire.Hosting.ApplicationModel, the convention of first-party hosting integrations.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// One host directory mapped into the guest. Carried over VSMB, not Docker bind-mount semantics.
/// Both paths must be drive-letter absolute.
/// </summary>
/// <param name="Source">Host directory. Must exist when the container is created.</param>
/// <param name="Target">Where it appears in the guest.</param>
/// <param name="IsReadOnly">When true, the guest cannot write through the mount.</param>
internal readonly record struct HcsContainerMount(string Source, string Target, bool IsReadOnly)
{
    /// <summary>hcsctl's <c>--mount HOST:CONTAINER[:ro]</c> spelling.</summary>
    public string ToOptionValue() => IsReadOnly ? $"{Source}:{Target}:ro" : $"{Source}:{Target}";
}

/// <summary>
/// A Hyper-V-isolated Windows container hosted through the Windows Host Compute System (HCS) API,
/// run by driving <c>hcsctl</c>. Ephemeral like the VM resource: created when the AppHost starts
/// and torn down when it exits, with leftovers from a crashed run scavenged on the next one.
/// </summary>
/// <remarks>
/// Hyper-V isolation is the only mode. Process isolation needs an enabled
/// <c>BUILTIN\Administrators</c> SID at <c>PrepareLayer</c>, which runs at <em>every</em>
/// container start; no user-rights assignment satisfies that check in a UAC-filtered token.
/// </remarks>
public sealed class HcsContainerResource([ResourceName] string name)
    : Resource(name), IResourceWithEndpoints, IResourceWithConnectionString, IResourceWithEnvironment
{
    /// <summary>
    /// Identifies containers this integration owns. Crash scavenging deletes only containers
    /// whose owning process (encoded in the id) is gone.
    /// </summary>
    internal const string IdPrefix = "aspirehcs";

    /// <summary>
    /// Connection string for <c>WithReference(container)</c>: host:port of the first endpoint
    /// declared via <c>WithEndpoint</c>.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (PrimaryEndpointName is null)
            {
                throw new InvalidOperationException(
                    $"Resource '{Name}' has no endpoints; declare one with WithEndpoint(name, targetPort) before referencing it.");
            }

            EndpointReference endpoint = new(this, PrimaryEndpointName);
            return ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.HostAndPort)}");
        }
    }

    /// <summary>The image reference, e.g. <c>mcr.microsoft.com/windows/nanoserver:ltsc2025</c>.</summary>
    internal string? ImageReference { get; set; }

    /// <summary>
    /// What the container runs. hcsctl has no notion of a primary process, so this is executed as
    /// a guest process that the AppHost stays attached to for its lifetime.
    /// </summary>
    internal string? Command { get; set; }

    /// <summary>Explicit path to hcsctl.exe, overriding the environment variable and PATH.</summary>
    internal string? HcsCtlPath { get; set; }

    /// <summary>
    /// The hcsctl store holding the imported image. Null uses hcsctl's per-user default.
    /// <c>image import</c> is elevated, so this usually names a store prepared out of band.
    /// </summary>
    internal string? StorePath { get; set; }

    /// <summary>First endpoint declared via WithEndpoint; backs the connection string.</summary>
    internal string? PrimaryEndpointName { get; set; }

    internal int MemoryMb { get; set; } = 2048;

    internal int ProcessorCount { get; set; } = 2;

    /// <summary>
    /// Guest C: size. Null leaves hcsctl's default of <b>20 GB</b>. A full disk gives no error
    /// that names the real cause.
    /// </summary>
    internal int? ScratchSizeGigabytes { get; set; }

    /// <summary>Host directories mapped into the guest over VSMB.</summary>
    internal List<HcsContainerMount> Mounts { get; } = [];

    /// <summary>
    /// The host compute network to attach an endpoint on, by name or id. Null means no NIC.
    /// </summary>
    /// <remarks>
    /// The network must already exist; hcsctl cannot create one
    /// (<see href="https://github.com/joshmakestuff/hcsctl/issues/15">hcsctl#15</see>).
    /// <c>WithNetwork()</c> defaults it to the Default Switch, the same network VMs default to,
    /// so the two resource kinds can reach each other.
    /// </remarks>
    internal string? NetworkName { get; set; }

    /// <summary>
    /// The hcsctl container id. Carries this process's id so a crashed AppHost's leftovers are
    /// attributable, and a random suffix so a container from a previous run that is still being
    /// torn down cannot collide with this one.
    /// </summary>
    internal string ContainerId { get; } =
        $"{IdPrefix}-{Environment.ProcessId}-{Sanitize(name)}-{Guid.NewGuid():N}";

    /// <summary>
    /// Extracts the owning process id from a container id, or null if it was not written by this
    /// integration. An id that does not parse is never a scavenging candidate.
    /// </summary>
    internal static int? OwnerProcessId(string? containerId)
    {
        if (containerId is null)
        {
            return null;
        }

        string prefix = $"{IdPrefix}-";
        if (!containerId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = containerId.AsSpan(prefix.Length);
        int end = rest.IndexOf('-');
        if (end <= 0)
        {
            return null;
        }

        return int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            ? pid
            : null;
    }

    /// <summary>
    /// Keeps the id to characters that are safe in a directory name. hcsctl joins the id into a
    /// filesystem path under its store, so a name carrying a separator would escape it.
    /// </summary>
    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char c in value)
        {
            buffer[length++] = char.IsAsciiLetterOrDigit(c) ? c : '_';
        }

        return new string(buffer[..length]);
    }
}
