using AspireHcs;
using AspireHcs.Hosting;
using Aspire.Hosting.ApplicationModel;

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
    /// There is no isolation option. Hyper-V isolation is the only mode AspireHcs supports and
    /// the only one hcsctl implements — process isolation is refused rather than attempted,
    /// because its gate runs at every container start and no grant satisfies it in a
    /// UAC-filtered token. See <c>docs/containers.md</c>.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> AddHcsContainer(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Fail fast at model-build time, not at container-start time, per the package contract.
            HcsPlatform.ThrowIfUnsupported();
        }

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
    /// Names the hcsctl store holding the image. Defaults to hcsctl's per-user store, which is
    /// rarely where a prepared image lives.
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
    /// Declares a service the guest exposes on <paramref name="targetPort"/>, as a non-proxied
    /// Aspire endpoint. The first endpoint declared backs the resource's connection string.
    /// </summary>
    /// <remarks>
    /// Endpoints are declared but not yet allocated an address: container networking is
    /// <see href="https://github.com/joshmakestuff/AspireHcs/issues/41">#41</see>. Until that
    /// lands the endpoint has no address and nothing resolves against it.
    /// </remarks>
    public static IResourceBuilder<HcsContainerResource> WithEndpoint(
        this IResourceBuilder<HcsContainerResource> builder, string name, int targetPort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPort, 0);

        builder.Resource.PrimaryEndpointName ??= name;
        return builder.WithEndpoint(name: name, targetPort: targetPort, isProxied: false);
    }
}
