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
            // The package contract: an unsupported host fails at model-build time.
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

    /// <summary>
    /// Sets the boot disk: a bootable Gen2/UEFI VHDX. The VM always boots a differencing child of
    /// it, so the image itself is never written to and can back several resources at once.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithVhdx(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.VhdxPath = Path.GetFullPath(path);
        return builder;
    }

    /// <summary>
    /// Points this resource at a specific <c>hcsctl.exe</c> and store, instead of discovering the
    /// binary and defaulting the store (<c>ASPIREHCS_STORE</c> when set, otherwise hcsctl's
    /// per-user default).
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithHcsCtl(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string? executablePath = null, string? storePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HcsCtlPath = executablePath;
        builder.Resource.StorePath = storePath;
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
    /// Attaches a NIC on an existing host compute network, defaulting to the Hyper-V Default
    /// Switch, the same default HCS containers get, so a VM and a container in one AppHost reach
    /// each other. Guests on different HNS networks are isolated; name another network here to
    /// isolate the VM. The network's DHCP leases the guest an address, which AspireHcs discovers
    /// host-side and uses to resolve the VM's endpoints. The guest image must configure its NIC
    /// for DHCP (the default for stock Linux and Windows images). A network without a DHCP
    /// server leaves the guest addressless; only ICS networks like the Default Switch are known
    /// to serve a full VM.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithNetwork(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string networkName = HcsNetwork.DefaultSwitchName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);

        builder.Resource.NetworkName = networkName;
        return builder;
    }

    /// <summary>
    /// Declares a service the guest exposes on <paramref name="targetPort"/>. Registered as a
    /// non-proxied Aspire endpoint (DCP cannot proxy into a VM) that resolves to the guest's
    /// DHCP-leased IP once it boots. The first endpoint declared backs the resource's
    /// connection string. Requires <see cref="WithNetwork"/>.
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
    /// Opt-in. A guest image that ships the daemon disabled (Kali's sshd, for instance) refuses
    /// the connection and the resource stays unhealthy.
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

        // An unknown endpoint name fails at model-build time.
        RequireEndpoint(builder.Resource, name, nameof(WithTcpHealthCheck));

        string key = $"{builder.Resource.Name}_{name}_tcp_check";
        TimeSpan connectTimeout = timeout ?? TimeSpan.FromSeconds(3);

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            key,
            _ => new TcpEndpointHealthCheck(builder.Resource, name, connectTimeout),
            failureStatus: null,
            tags: null));

        return builder.WithHealthCheck(key);
    }

    /// <summary>
    /// Adds a <c>Connect (SSH)</c> command to the resource in the dashboard, which opens an SSH
    /// client on the host pointed at the guest's leased address. Offered only while the VM is
    /// running and <paramref name="endpointName"/> has resolved to an address.
    /// </summary>
    /// <param name="builder">The VM to add the command to.</param>
    /// <param name="endpointName">The endpoint carrying SSH; must already be declared with <see cref="WithEndpoint"/>.</param>
    /// <param name="userName">
    /// Prefilled as <c>ssh -l</c>. Left unset, ssh falls back to the host user name. Pass the
    /// account the guest image has.
    /// </param>
    /// <remarks>
    /// The SSH client is launched on the host: in run mode the AppHost and the browser showing
    /// the dashboard are on the same machine. The guest only has to serve SSH.
    /// </remarks>
    public static IResourceBuilder<HcsVirtualMachineResource> WithSshCommand(
        this IResourceBuilder<HcsVirtualMachineResource> builder,
        [EndpointName] string endpointName = "ssh",
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        RequireUsableUserName(userName);

        RequireEndpoint(builder.Resource, endpointName, nameof(WithSshCommand));
        ConnectCommands.RegisterSsh(builder, endpointName, userName);
        return builder;
    }

    /// <summary>
    /// Adds a <c>Connect (RDP)</c> command to the resource in the dashboard, which opens mstsc
    /// on the host pointed at the guest's leased address. Offered only while the VM is running
    /// and <paramref name="endpointName"/> has resolved to an address.
    /// </summary>
    /// <param name="builder">The VM to add the command to.</param>
    /// <param name="endpointName">The endpoint carrying RDP; must already be declared with <see cref="WithEndpoint"/>.</param>
    /// <param name="userName">Prefilled in the generated <c>.rdp</c>; mstsc still prompts for the password.</param>
    /// <remarks>
    /// The guest must serve RDP. Windows Server images do not by default: Remote Desktop needs
    /// enabling and its firewall group opening.
    /// </remarks>
    public static IResourceBuilder<HcsVirtualMachineResource> WithRdpCommand(
        this IResourceBuilder<HcsVirtualMachineResource> builder,
        [EndpointName] string endpointName = "rdp",
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        RequireUsableUserName(userName);

        RequireEndpoint(builder.Resource, endpointName, nameof(WithRdpCommand));

        // A user name the .rdp format cannot represent fails at model-build time, by the same
        // check that guards the write.
        if (!string.IsNullOrEmpty(userName))
        {
            RdpFile.ValidateValue("username", userName);
        }

        ConnectCommands.RegisterRdp(builder, endpointName, userName);
        return builder;
    }

    /// <summary>
    /// <c>null</c> means "not specified" and is a supported choice. An empty or whitespace
    /// string is rejected: ssh would fall back to the host account and mstsc to the last cached
    /// one, and connect as somebody other than who was asked for.
    /// </summary>
    private static void RequireUsableUserName(string? userName)
    {
        if (userName is not null && string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException(
                "The user name is empty. Pass null to leave it unspecified, or a real account name.",
                nameof(userName));
        }
    }

    private static void RequireEndpoint(HcsVirtualMachineResource resource, string endpointName, string caller)
    {
        if (!resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no endpoint named '{endpointName}'. " +
                $"Declare it with WithEndpoint(\"{endpointName}\", targetPort) before calling {caller}().");
        }
    }
}
