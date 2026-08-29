using AspireHcs;
using AspireHcs.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // One relay per AppHost session, shared by every HCS consumer — the multiplexing shape.
        // Registered here so the instance can resolve it; it starts nothing until a reference
        // actually needs forwarding.
        builder.Services.TryAddSingleton<DockerRelay>();

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
    /// Attaches an extra VHDX after the boot disk, at SCSI LUN 1..n in call order. Repeatable.
    /// Shares the boot disk's copy-on-write policy: the VM boots a differencing child, so the
    /// base is never written and a vendor's disks stay pristine.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithDisk(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (builder.Resource.DataDiskPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' already attaches '{fullPath}'; each WithDisk() path must be distinct.");
        }

        builder.Resource.DataDiskPaths.Add(fullPath);
        return builder;
    }

    /// <summary>
    /// Pins the NIC's MAC instead of letting hcsctl generate one. For guests whose network
    /// config is bound to a specific address — a RHEL guest with <c>HWADDR</c> in its interface
    /// config silently leaves the NIC unconfigured under any other MAC. Accepts dash or colon
    /// separators; stored normalized as <c>XX-XX-XX-XX-XX-XX</c>. Requires <see cref="WithNetwork"/>.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithMacAddress(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string macAddress)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);

        if (!System.Net.NetworkInformation.PhysicalAddress.TryParse(macAddress, out var parsed)
            || parsed.GetAddressBytes() is not { Length: 6 } bytes)
        {
            throw new ArgumentException(
                $"'{macAddress}' is not a 48-bit MAC address. Use the form 00-15-5D-02-33-0E.",
                nameof(macAddress));
        }

        // Normalized to the form hcsctl echoes back, so the stored value matches the create result.
        builder.Resource.RequestedMacAddress = string.Join("-", bytes.Select(b => b.ToString("X2")));
        return builder;
    }

    /// <summary>
    /// Tags the NIC's switch port with an access VLAN. For networks whose other ports are
    /// access-tagged — an untagged port on such a switch is isolated from all of them.
    /// Requires <see cref="WithNetwork"/>.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithVlan(
        this IResourceBuilder<HcsVirtualMachineResource> builder, int vlanId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(vlanId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vlanId, 4094);

        builder.Resource.VlanId = vlanId;
        return builder;
    }

    /// <summary>
    /// Declares the guest's fixed in-guest address — and with it, that the VM is agentless. No
    /// hcsguest agent is expected: the boot skips the DHCP-lease wait and environment delivery,
    /// and every endpoint resolves at this address once it accepts a TCP connection (on the
    /// first endpoint's target port, within <paramref name="bootTimeout"/>, default 15 minutes).
    /// For vendor appliance VMs that cannot be modified. Requires <see cref="WithNetwork"/>;
    /// combine with <see cref="WithMacAddress"/> and <see cref="WithVlan"/> when the guest's
    /// static config depends on them. <c>WithReference</c>/<c>WithEnvironment</c> as a consumer
    /// are rejected — there is no agent to deliver values to. With zero endpoints the VM reports
    /// Running right after start; there is nothing to probe.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithGuestAddress(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string address, TimeSpan? bootTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!System.Net.IPAddress.TryParse(address, out var parsed))
        {
            throw new ArgumentException($"'{address}' is not an IP address.", nameof(address));
        }

        // The parser reads leading-zero octets as octal: "10.20.10.020" is 10.20.10.16. A guest
        // address that silently means a different address is exactly the misconfiguration this
        // mode cannot diagnose later, so a non-canonical spelling is rejected here.
        if (parsed.ToString() != address)
        {
            throw new ArgumentException(
                $"'{address}' parses as '{parsed}'. Write the address canonically so it means what it says.",
                nameof(address));
        }

        builder.Resource.GuestAddress = address;
        if (bootTimeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(bootTimeout));
            builder.Resource.GuestAddressTimeout = timeout;
        }

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
    /// <remarks>
    /// Aspire keys service-discovery injection by the endpoint's <em>scheme</em>, and renders
    /// dashboard URLs with it. The default is <c>tcp</c>; pass <paramref name="scheme"/> as
    /// <c>http</c> for an HTTP service so consumers see
    /// <c>services__&lt;name&gt;__http__0=http://...</c> and get a clickable URL.
    /// </remarks>
    public static IResourceBuilder<HcsVirtualMachineResource> WithEndpoint(
        this IResourceBuilder<HcsVirtualMachineResource> builder, string name, int targetPort, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPort, 0);

        builder.Resource.PrimaryEndpointName ??= name;
        return builder.WithEndpoint(name: name, targetPort: targetPort, scheme: scheme, isProxied: false);
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
    /// Gates readiness on the guest serving HTTPS <em>without validating its certificate</em>:
    /// healthy once a GET to <paramref name="path"/> on <paramref name="endpointName"/>
    /// (default: the first endpoint declared) answers 2xx/3xx. For guests with self-signed
    /// certificates — vendor appliances, typically — which Aspire's certificate-validating
    /// <c>WithHttpsHealthCheck</c> can never pass. The check proves the service answers, not
    /// the certificate's identity; the name says so, so it cannot be reached by accident.
    /// </summary>
    public static IResourceBuilder<HcsVirtualMachineResource> WithInsecureHttpsHealthCheck(
        this IResourceBuilder<HcsVirtualMachineResource> builder,
        string? endpointName = null,
        string path = "/",
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(path);

        string name = endpointName
            ?? builder.Resource.PrimaryEndpointName
            ?? throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoints; call WithEndpoint(...) before WithInsecureHttpsHealthCheck().");

        // An unknown endpoint name fails at model-build time.
        RequireEndpoint(builder.Resource, name, nameof(WithInsecureHttpsHealthCheck));

        string normalizedPath = path.StartsWith('/') ? path : "/" + path;
        string key = $"{builder.Resource.Name}_{name}_https_check";
        TimeSpan requestTimeout = timeout ?? TimeSpan.FromSeconds(10);

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            key,
            _ => new HttpsEndpointHealthCheck(builder.Resource, name, normalizedPath, acceptAnyServerCertificate: true, requestTimeout),
            failureStatus: null,
            tags: null));

        return builder.WithHealthCheck(key);
    }

    /// <summary>
    /// Adds a <c>Connect (SSH)</c> command to the resource in the dashboard, which opens an SSH
    /// client on the host — over an hvsocket forward once one is running (issue #56), the
    /// guest's leased address otherwise. Offered only while the VM is running and
    /// <paramref name="endpointName"/> has resolved to an address.
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

        EndpointAnnotation endpoint = RequireEndpoint(builder.Resource, endpointName, nameof(WithSshCommand));

        // The button will prefer an hvsocket forward over the leased address once one is
        // running (issue #56); GuestForwardPump reads this at boot to know what to start.
        // Nothing here needs the target port to be resolved yet — WithEndpoint always sets it.
        if (endpoint.TargetPort is { } targetPort)
        {
            builder.Resource.HvsocketForwardTargets[endpointName] = targetPort;
        }

        ConnectCommands.RegisterSsh(builder, endpointName, userName);
        return builder;
    }

    /// <summary>
    /// Adds a <c>Connect (RDP)</c> command to the resource in the dashboard, which opens mstsc
    /// on the host — over an hvsocket forward once one is running (issue #56), the guest's
    /// leased address otherwise. Offered only while the VM is running and
    /// <paramref name="endpointName"/> has resolved to an address.
    /// </summary>
    /// <param name="builder">The VM to add the command to.</param>
    /// <param name="endpointName">The endpoint carrying RDP; must already be declared with <see cref="WithEndpoint"/>.</param>
    /// <param name="userName">Prefilled in the generated <c>.rdp</c>; mstsc still prompts for the password.</param>
    /// <remarks>
    /// The guest must serve RDP. Windows Server images do not by default: Remote Desktop needs
    /// enabling and its firewall group opening. Over the forward, mstsc connects to
    /// <c>127.0.0.1</c> rather than the guest's own hostname, so its RDP certificate does not
    /// match the address dialled — expect a name-mismatch warning; it is a consequence of the
    /// forward, not a fault.
    /// </remarks>
    public static IResourceBuilder<HcsVirtualMachineResource> WithRdpCommand(
        this IResourceBuilder<HcsVirtualMachineResource> builder,
        [EndpointName] string endpointName = "rdp",
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        RequireUsableUserName(userName);

        EndpointAnnotation endpoint = RequireEndpoint(builder.Resource, endpointName, nameof(WithRdpCommand));

        // A user name the .rdp format cannot represent fails at model-build time, by the same
        // check that guards the write.
        if (!string.IsNullOrEmpty(userName))
        {
            RdpFile.ValidateValue("username", userName);
        }

        // Same reasoning as WithSshCommand: GuestForwardPump reads this at boot to know what to
        // start, and the button prefers the forward once one is running.
        if (endpoint.TargetPort is { } targetPort)
        {
            builder.Resource.HvsocketForwardTargets[endpointName] = targetPort;
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

    private static EndpointAnnotation RequireEndpoint(HcsVirtualMachineResource resource, string endpointName, string caller)
        => resource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no endpoint named '{endpointName}'. " +
                $"Declare it with WithEndpoint(\"{endpointName}\", targetPort) before calling {caller}().");
}
