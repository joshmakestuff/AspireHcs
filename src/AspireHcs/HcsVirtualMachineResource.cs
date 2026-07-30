// Resource types live in Aspire.Hosting.ApplicationModel for discoverability,
// matching the convention used by first-party hosting integrations.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Hyper-V virtual machine hosted through the Windows Host Compute System (HCS) API.
/// The VM is ephemeral: created when the AppHost starts and torn down when it exits,
/// crash-safe via HCS's terminate-on-last-handle-closed semantics.
/// </summary>
public sealed class HcsVirtualMachineResource([ResourceName] string name)
    : Resource(name), IResourceWithEndpoints, IResourceWithConnectionString
{
    /// <summary>
    /// Connection string for <c>WithReference(vm)</c>: host:port of the first endpoint
    /// declared via <c>WithEndpoint</c>, resolved lazily once the guest's DHCP lease is known.
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

    /// <summary>Whether the VM gets a NIC on the host's Default Switch (ICS DHCP/NAT) network.</summary>
    internal bool NetworkEnabled { get; set; }

    /// <summary>First endpoint declared via WithEndpoint; backs the connection string.</summary>
    internal string? PrimaryEndpointName { get; set; }

    /// <summary>HCN endpoint id for this run's vNIC. Fresh per run, like the VM id.</summary>
    internal Guid HcnEndpointId { get; } = Guid.NewGuid();

    /// <summary>
    /// Locally-administered MAC for the vNIC. HNS learns the guest's DHCP lease against this
    /// MAC, which is how the endpoint's IP becomes discoverable host-side.
    /// </summary>
    internal string MacAddress { get; } = GenerateMac();

    private static string GenerateMac()
    {
        byte[] tail = new byte[3];
        Random.Shared.NextBytes(tail);
        return $"02-15-5D-{tail[0]:X2}-{tail[1]:X2}-{tail[2]:X2}";
    }

    /// <summary>Path to the boot VHDX (Gen2/UEFI). Set via <c>WithVhdx</c>.</summary>
    internal string? VhdxPath { get; set; }

    /// <summary>When true, boot a differencing child so the base image is never mutated.</summary>
    internal bool CopyOnWrite { get; set; } = true;

    internal int MemoryMb { get; set; } = 2048;

    internal int ProcessorCount { get; set; } = 2;

    /// <summary>
    /// HCS compute-system id. Includes a random suffix so a crashed AppHost's dying VM
    /// (teardown is asynchronous) never collides with the next run's.
    /// </summary>
    internal string VmId { get; } = $"aspirehcs-{name}-{Guid.NewGuid():N}";

    /// <summary>Named pipe carrying the guest's COM1 serial console.</summary>
    internal string SerialPipeName => $"{VmId}-com1";
}
