// Resource types live in Aspire.Hosting.ApplicationModel for discoverability,
// matching the convention used by first-party hosting integrations.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Hyper-V virtual machine, driven through the <c>hcsctl</c> CLI. The VM is ephemeral: created
/// when the AppHost starts and torn down when it exits.
/// </summary>
/// <remarks>
/// Crash safety is not terminate-on-last-handle-closed any more. hcsctl is a child process that
/// exits as soon as each command completes, so no handle is held and a crashed AppHost leaves the
/// VM running. What reclaims it instead is the label this run stamps on the VM: the next run lists
/// what exists, finds a VM owned by a pid that is gone, and removes it (hcsctl#44).
/// </remarks>
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

    /// <summary>
    /// The host compute network the VM's NIC attaches to, by name or id. Null means no NIC.
    /// Defaults to the Default Switch via <c>WithNetwork()</c> so VMs and HCS containers
    /// co-locate — guests on different HNS networks cannot reach each other (#58).
    /// </summary>
    internal string? NetworkName { get; set; }

    /// <summary>First endpoint declared via WithEndpoint; backs the connection string.</summary>
    internal string? PrimaryEndpointName { get; set; }

    /// <summary>Path to the boot VHDX (Gen2/UEFI). Set via <c>WithVhdx</c>.</summary>
    internal string? VhdxPath { get; set; }

    internal int MemoryMb { get; set; } = 2048;

    internal int ProcessorCount { get; set; } = 2;

    /// <summary>Explicit path to <c>hcsctl.exe</c>, or null to discover it.</summary>
    internal string? HcsCtlPath { get; set; }

    /// <summary>The hcsctl store to operate against, or null for hcsctl's per-user default.</summary>
    internal string? StorePath { get; set; }

    /// <summary>
    /// The VM's id, and a GUID because hcsctl requires one: the id is also the VM's Hyper-V socket
    /// address, so <c>hcsctl guest info --vmid</c> takes it unchanged. Fresh per resource, so a
    /// crashed AppHost's leftover can never collide with the next run's.
    /// </summary>
    internal string VmId { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Named pipe carrying the guest's COM1 serial console. Passed to hcsctl at create time; this
    /// side only reads it, which is why the console survived the move to the CLI unchanged.
    /// </summary>
    internal string SerialPipeName => $"aspirehcs-{Name}-{VmId}-com1";

    /// <summary>
    /// The MAC and HCN endpoint hcsctl generated for this VM, learned from the create result. Both
    /// are null until the VM has been created — nothing on this side chooses them, because the MAC
    /// has to survive a stop/start for the DHCP lease to hold and hcsctl's store is what remembers
    /// it.
    /// </summary>
    internal string? EndpointId { get; set; }

    /// <inheritdoc cref="EndpointId"/>
    internal string? MacAddress { get; set; }
}
