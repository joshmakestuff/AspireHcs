using AspireHcs;
using AspireHcs.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Extensions live in Aspire.Hosting so they are in scope as soon as the package is referenced.
namespace Aspire.Hosting;

public static class HcsContainerBuilderExtensions
{
    /// <summary>
    /// Adds a Hyper-V-isolated Windows container as an Aspire resource, run through
    /// <c>hcsctl</c>. Requires Windows 10 1809+ with the Hyper-V feature and membership in the
    /// Hyper-V Administrators group; the image must already be imported into an hcsctl store,
    /// which is a one-time elevated step. Excluded from publish manifests: a local container
    /// run this way has no deployment story.
    /// </summary>
    /// <remarks>
    /// There is no isolation option. Hyper-V isolation is the only mode AspireHcs supports:
    /// hcsctl implements process isolation too, but it needs an elevated token at container
    /// create, which the unelevated dev loop this integration targets does not have.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> AddHcsContainer(
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

        HcsContainerResource resource = new(name);

        IResourceBuilder<HcsContainerResource> container = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "HcsContainer",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .ExcludeFromManifest();

        HcsContainerOrchestrator.Register(container);
        return container;
    }

    /// <summary>
    /// Sets the image to run, by the reference it was pulled under, e.g.
    /// <c>mcr.microsoft.com/windows/nanoserver:ltsc2025</c>.
    /// </summary>
    /// <remarks>
    /// The image must already be in the store. AspireHcs will not pull or import on your behalf:
    /// <c>image import</c> needs privileges a UAC-filtered token cannot be granted, so an AppHost
    /// cannot do it. If the image is missing, resource start fails naming the two commands to run.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithImage(
        this IResourceBuilder<HcsContainerResource> builder, string imageReference)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        builder.Resource.ImageReference = imageReference;
        return builder;
    }

    /// <summary>
    /// Sets the command the container runs. The AppHost stays attached to it for its lifetime,
    /// so a long-running process keeps the resource running and its exit stops the resource.
    /// </summary>
    public static IResourceBuilder<HcsContainerResource> WithCommand(
        this IResourceBuilder<HcsContainerResource> builder, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        builder.Resource.Command = commandLine;
        return builder;
    }

    /// <summary>
    /// Names the hcsctl store holding the image. Defaults to <c>ASPIREHCS_STORE</c> when set,
    /// otherwise hcsctl's per-user store — which is rarely where a prepared image lives.
    /// </summary>
    public static IResourceBuilder<HcsContainerResource> WithStore(
        this IResourceBuilder<HcsContainerResource> builder, string storePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        builder.Resource.StorePath = Path.GetFullPath(storePath);
        return builder;
    }

    /// <summary>
    /// Adds a "Connect (Shell)" command to the dashboard, opening an interactive console on the
    /// host attached to a new guest process via <c>hcsctl container exec --interactive --tty</c>.
    /// Unlike <see cref="WithSshCommand"/>/<see cref="WithRdpCommand"/> on the VM resource, no
    /// endpoint or address is required.
    /// </summary>
    /// <param name="shell">
    /// The guest binary to run, e.g. <c>cmd.exe</c> (default) or <c>powershell.exe</c>.
    /// <c>nanoserver</c> has only <c>cmd.exe</c>. If the binary is missing in the guest, hcsctl's
    /// own error appears directly in the console window this command opens.
    /// </param>
    public static IResourceBuilder<HcsContainerResource> WithShellCommand(
        this IResourceBuilder<HcsContainerResource> builder, string shell = "cmd.exe")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        ContainerConnectCommands.RegisterShell(builder, shell);
        return builder;
    }

    /// <summary>
    /// Points at a specific <c>hcsctl.exe</c>, overriding the <c>ASPIREHCS_HCSCTL</c> environment
    /// variable and PATH. Accepts the binary or the directory holding it.
    /// </summary>
    public static IResourceBuilder<HcsContainerResource> WithHcsCtl(
        this IResourceBuilder<HcsContainerResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Resource.HcsCtlPath = path;
        return builder;
    }

    /// <summary>
    /// Maps a host directory into the guest, mirroring Aspire's container API shape. A relative
    /// <paramref name="source"/> is resolved against the AppHost directory, the way Aspire's
    /// Docker path does.
    /// </summary>
    /// <remarks>
    /// Carried over VSMB. hcsctl requires both paths to be drive-letter absolute. The host
    /// directory must exist when the container is created.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithBindMount(
        this IResourceBuilder<HcsContainerResource> builder, string source, string target, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        string resolvedSource = Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);

        if (!Path.IsPathFullyQualified(target))
        {
            throw new ArgumentException(
                $"The mount target '{target}' must be an absolute path in the guest, e.g. C:\\app. " +
                "Relative targets have no meaning: there is no working directory to resolve them against.",
                nameof(target));
        }

        // hcsctl rejects a duplicate guest path with exit 64; reject it at model-build time.
        if (builder.Resource.Mounts.Any(m => string.Equals(
                m.Target.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' already has a mount at '{target}'. " +
                "Each guest path can be mapped only once.");
        }

        builder.Resource.Mounts.Add(new HcsContainerMount(resolvedSource, target, isReadOnly));
        return builder;
    }

    /// <summary>
    /// Sets the guest's C: size. Without this the guest gets hcsctl's default of <b>20 GB</b>,
    /// which anything that unpacks, builds or caches inside the container can fill.
    /// </summary>
    /// <remarks>
    /// The guest sees about 0.1 GB less than requested: a 40 GB request gives a 39.9 GB C:.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithScratchSize(
        this IResourceBuilder<HcsContainerResource> builder, int gigabytes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gigabytes, 0);

        builder.Resource.ScratchSizeGigabytes = gigabytes;
        return builder;
    }

    public static IResourceBuilder<HcsContainerResource> WithMemory(
        this IResourceBuilder<HcsContainerResource> builder, int gigabytes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gigabytes, 0);

        builder.Resource.MemoryMb = gigabytes * 1024;
        return builder;
    }

    public static IResourceBuilder<HcsContainerResource> WithProcessorCount(
        this IResourceBuilder<HcsContainerResource> builder, int count)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);

        builder.Resource.ProcessorCount = count;
        return builder;
    }

    /// <summary>
    /// Attaches a NIC on an existing host compute network, defaulting to the Hyper-V Default
    /// Switch, the same default HCS VMs get, so a container and a VM in one AppHost reach each
    /// other. Guests on different HNS networks are isolated; name another network here
    /// (for example <c>nat</c>) to isolate the container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A static HNS endpoint programs the container's network stack directly, so the address is
    /// known when the container is <em>created</em>. The address is reachable from the host; no
    /// port publishing is involved. On the Default Switch the endpoint takes its address from
    /// the same pool that leases the VMs theirs.
    /// </para>
    /// <para>
    /// The network must already exist: hcsctl cannot create one
    /// (<see href="https://github.com/joshmakestuff/hcsctl/issues/15">hcsctl#15</see>).
    /// </para>
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithNetwork(
        this IResourceBuilder<HcsContainerResource> builder, string networkName = HcsNetwork.DefaultSwitchName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);

        builder.Resource.NetworkName = networkName;
        return builder;
    }

    /// <summary>
    /// Declares a service the guest exposes on <paramref name="targetPort"/>, as a non-proxied
    /// Aspire endpoint resolving to the container's own address. The first endpoint declared
    /// backs the resource's connection string. Requires <see cref="WithNetwork"/>.
    /// </summary>
    /// <remarks>
    /// Aspire keys service-discovery injection by the endpoint's <em>scheme</em>, and renders
    /// dashboard URLs with it. The default is <c>tcp</c>; pass <paramref name="scheme"/> as
    /// <c>http</c> for an HTTP service so consumers see
    /// <c>services__&lt;name&gt;__http__0=http://...</c> and get a clickable URL.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithEndpoint(
        this IResourceBuilder<HcsContainerResource> builder, string name, int targetPort, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPort, 0);

        builder.Resource.PrimaryEndpointName ??= name;
        return builder.WithEndpoint(name: name, targetPort: targetPort, scheme: scheme, isProxied: false);
    }

    /// <summary>
    /// Gates readiness on the container actually serving: the resource is healthy — and
    /// <c>WaitFor(container)</c> releases its dependents — only once a TCP connection to
    /// <paramref name="endpointName"/> is accepted. Defaults to the first endpoint declared.
    /// </summary>
    /// <remarks>
    /// For a container this is the <em>only</em> readiness gate. A VM has a separate
    /// guest-kernel-readiness signal that gates Running; a container's start already implies the
    /// guest is up, so without this a resource is declared ready the moment it reports Running —
    /// before anything inside it is listening.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithTcpHealthCheck(
        this IResourceBuilder<HcsContainerResource> builder,
        string? endpointName = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string name = endpointName
            ?? builder.Resource.PrimaryEndpointName
            ?? throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoints; call WithEndpoint(...) before WithTcpHealthCheck().");

        // An unknown endpoint name fails at model-build time.
        if (!builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoint named '{name}'. " +
                $"Declare it with WithEndpoint(\"{name}\", targetPort) before calling WithTcpHealthCheck().");
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
