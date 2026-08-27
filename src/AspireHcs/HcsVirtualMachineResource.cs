// Resource types live in Aspire.Hosting.ApplicationModel, the convention of first-party hosting integrations.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Hyper-V virtual machine, driven through the <c>hcsctl</c> CLI. The VM is ephemeral: created
/// when the AppHost starts and torn down when it exits.
/// </summary>
/// <remarks>
/// <para>
/// hcsctl is a child process that exits as soon as each command completes, so no handle is held
/// and a crashed AppHost leaves the VM running. This run stamps the VM with an owner label; the
/// next run lists what exists, finds a VM owned by a pid that is gone, and removes it.
/// </para>
/// <para>
/// A VM is an environment consumer — <c>WithReference</c> and <c>WithEnvironment</c> work — but
/// nothing injects variables into a VHDX at create. The values are written to
/// <c>/etc/aspire.env</c> in the guest once it is up, over hvsocket; see the delivery in
/// <c>HcsVmInstance</c> for the boot-ordering caveat that convention carries.
/// </para>
/// </remarks>
public sealed class HcsVirtualMachineResource([ResourceName] string name)
    : Resource(name), IResourceWithEndpoints, IResourceWithConnectionString, IResourceWithEnvironment
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
    /// <c>WithNetwork()</c> defaults it to the Default Switch, the same network HCS containers
    /// default to; guests on different HNS networks cannot reach each other.
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
    /// The VM's id. hcsctl requires a GUID: the id is also the VM's Hyper-V socket address, so
    /// <c>hcsctl guest info --vmid</c> takes it unchanged. Fresh per resource, so a crashed
    /// AppHost's leftover cannot collide with the next run's.
    /// </summary>
    internal string VmId { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Named pipe carrying the guest's COM1 serial console. Passed to hcsctl at create time; this
    /// side only reads it.
    /// </summary>
    internal string SerialPipeName => $"aspirehcs-{Name}-{VmId}-com1";

    /// <summary>
    /// The MAC and HCN endpoint hcsctl generated for this VM, learned from the create result. Both
    /// are null until the VM has been created. hcsctl's store owns the MAC; it must survive a
    /// stop/start for the DHCP lease to hold.
    /// </summary>
    internal string? EndpointId { get; set; }

    /// <inheritdoc cref="EndpointId"/>
    internal string? MacAddress { get; set; }

    /// <summary>
    /// Endpoint names <c>WithSshCommand</c> wants relayed over hvsocket instead of the leased
    /// address, mapped to the endpoint's guest-side target port. Populated at model-build time;
    /// read once, at boot, by <c>GuestForwardPump</c>.
    /// </summary>
    internal Dictionary<string, int> HvsocketForwardTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>host:port</c> for each endpoint name an hvsocket forward is actually running for.
    /// Written by <c>GuestForwardPump</c> once its listener is up, and removed if the forward
    /// process later exits unexpectedly; read by <c>ConnectCommands</c> to prefer the forward
    /// over the leased address. A concurrent dictionary: the forward's exit can be observed on a
    /// process-watcher thread while a dashboard click reads this on another.
    /// </summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, string> ForwardedConnectAddresses { get; }
        = new(StringComparer.OrdinalIgnoreCase);
}
