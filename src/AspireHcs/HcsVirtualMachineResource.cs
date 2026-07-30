// Resource types live in Aspire.Hosting.ApplicationModel for discoverability,
// matching the convention used by first-party hosting integrations.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Hyper-V virtual machine hosted through the Windows Host Compute System (HCS) API.
/// The VM is ephemeral: created when the AppHost starts and torn down when it exits,
/// crash-safe via HCS's terminate-on-last-handle-closed semantics.
/// </summary>
public sealed class HcsVirtualMachineResource([ResourceName] string name)
    : Resource(name), IResourceWithEndpoints
{
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
