using AspireHcs;
using AspireHcs.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "HcsVirtualMachine",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .ExcludeFromManifest();

        HcsVmOrchestrator.Register(vm);
        return vm;
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

    /// <summary>
    /// Attaches a NIC on the host's NAT network (the Hyper-V Default Switch). The switch's
    /// built-in DHCP leases the guest an address, which AspireHcs discovers host-side and
    /// uses to resolve the VM's endpoints. The guest image must configure its NIC for DHCP
    /// (the default for stock Linux and Windows images).
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithNatNetwork(
        this IResourceBuilder<HcsVirtualMachineResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.NetworkEnabled = true;
        return builder;
    }

    /// <summary>
    /// Declares a service the guest exposes on <paramref name="targetPort"/>. Registered as a
    /// non-proxied Aspire endpoint (DCP cannot proxy into a VM) that resolves to the guest's
    /// DHCP-leased IP once it boots. The first endpoint declared backs the resource's
    /// connection string. Requires <see cref="WithNatNetwork"/>.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithEndpoint(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string name, int targetPort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPort, 0);

        builder.Resource.PrimaryEndpointName ??= name;
        return builder.WithEndpoint(name: name, targetPort: targetPort, isProxied: false);
    }

    /// <summary>
    /// Gates readiness on the guest actually serving: the resource is healthy — and
    /// <c>WaitFor(vm)</c> releases its dependents — only once a TCP connection to
    /// <paramref name="endpointName"/> is accepted. Defaults to the first endpoint declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a VM reports ready as soon as its guest kernel is up and its endpoints
    /// resolve, which is several seconds before any service inside it is listening. Aspire
    /// declares a resource with no health checks ready the moment it reports Running, so this
    /// annotation is what makes the difference.
    /// </para>
    /// <para>
    /// Opt-in, because it only holds where the guest image really runs the service: a stock
    /// image that ships the daemon disabled (Kali's sshd, for instance) will refuse the
    /// connection and stay unhealthy forever, which is the honest report but not always the
    /// one you want while bringing an image up.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<HcsVirtualMachineResource> WithTcpHealthCheck(
        this IResourceBuilder<HcsVirtualMachineResource> builder,
        string? endpointName = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string name = endpointName
            ?? builder.Resource.PrimaryEndpointName
            ?? throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoints; call WithEndpoint(...) before WithTcpHealthCheck().");

        // Checked here rather than at check time so a typo fails the build of the model, not a
        // health report nobody reads.
        if (!builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoint named '{name}'. Declare it with WithEndpoint(\"{name}\", targetPort) first.");
        }

        string key = $"{builder.Resource.Name}_{name}_tcp_check";
        TimeSpan connectTimeout = timeout ?? TimeSpan.FromSeconds(3);

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            key,
            _ => new TcpEndpointHealthCheck(builder.Resource, name, connectTimeout),
            failureStatus: null,
            tags: null));

        return builder.WithHealthCheck(key);
    }
}
