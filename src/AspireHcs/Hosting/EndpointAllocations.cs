using Aspire.Hosting.ApplicationModel;

namespace AspireHcs.Hosting;

/// <summary>
/// Resolving "the address this endpoint got, if any" — the one question the health check and
/// the connect commands both ask. It lives here rather than in each of them so the matching
/// rule (which is case-insensitive, matching how endpoints are named elsewhere in Aspire)
/// cannot drift between the thing that decides a VM is healthy and the thing that decides you
/// can connect to it.
/// </summary>
internal static class EndpointAllocations
{
    internal static AllocatedEndpoint? Find(HcsVirtualMachineResource resource, string endpointName)
        => resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            ?.AllocatedEndpoint;
}
