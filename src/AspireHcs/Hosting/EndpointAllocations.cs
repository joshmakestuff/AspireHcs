using Aspire.Hosting.ApplicationModel;

namespace AspireHcs.Hosting;

/// <summary>
/// Resolves the address an endpoint got, if any. Shared by the health check and the connect
/// commands. The endpoint-name match is case-insensitive, as elsewhere in Aspire.
/// <para>
/// Typed on <see cref="IResource"/>: containers allocate endpoints too.
/// </para>
/// </summary>
internal static class EndpointAllocations
{
    internal static AllocatedEndpoint? Find(IResource resource, string endpointName)
        => resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            ?.AllocatedEndpoint;
}
