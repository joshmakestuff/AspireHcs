using AspireHcs;
using AspireHcs.Hosting;
using Aspire.Hosting.ApplicationModel;

// Extensions live in Aspire.Hosting so they are in scope as soon as the package is referenced.
namespace Aspire.Hosting;

public static class HcsVirtualMachineBuilderExtensions
{
    /// <summary>
    /// Adds a Hyper-V virtual machine as an Aspire resource, hosted via the Windows Host
    /// Compute System (HCS) API. The VM is created on AppHost start and destroyed on exit.
    /// Requires Windows 10 1809+ with the Hyper-V feature, running either elevated or as a
    /// member of the Hyper-V Administrators group. Excluded from publish manifests: a local
    /// VM has no deployment story.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> AddHcsVm(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Fail fast at model-build time, not at VM-start time, per the package contract.
            HcsPlatform.ThrowIfUnsupported();
        }

        HcsVirtualMachineResource resource = new(name);
        HcsVmOrchestrator.Register(builder, resource);

        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "HcsVirtualMachine",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .ExcludeFromManifest();
    }

    /// <summary>Sets the boot disk. <paramref name="copyOnWrite"/> (default) boots a differencing child, leaving the base VHDX untouched.</summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithVhdx(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string path, bool copyOnWrite = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.VhdxPath = Path.GetFullPath(path);
        builder.Resource.CopyOnWrite = copyOnWrite;
        return builder;
    }

    public static IResourceBuilder<HcsVirtualMachineResource> WithMemory(
        this IResourceBuilder<HcsVirtualMachineResource> builder, int gigabytes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gigabytes, 0);

        builder.Resource.MemoryMb = gigabytes * 1024;
        return builder;
    }

    public static IResourceBuilder<HcsVirtualMachineResource> WithProcessorCount(
        this IResourceBuilder<HcsVirtualMachineResource> builder, int count)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);

        builder.Resource.ProcessorCount = count;
        return builder;
    }
}
