namespace AspireHcs.Cli;

/// <summary>
/// The <c>network</c> verbs, as methods. The group is read-only and unelevated, and it rejects
/// <c>--store</c>; <see cref="HcsCtl"/> omits it for this group.
/// </summary>
internal static class HcsCtlNetworks
{
    /// <summary>
    /// Lists HNS endpoints with their current addresses, read live from HCN, optionally filtered
    /// to one network by name or id.
    /// </summary>
    /// <remarks>
    /// The container path polls this for an ICS lease. hcsctl's state.json, and
    /// <c>container inspect</c> which reports that snapshot, keep only the create-time address
    /// list, which is empty on an ICS network. This listing updates when the lease lands.
    /// </remarks>
    public static Task<HcsCtlNetworkEndpointsDocument> ListEndpointsAsync(
        this HcsCtl hcsctl, string? network = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        string[] arguments = string.IsNullOrEmpty(network)
            ? ["network", "endpoints"]
            : ["network", "endpoints", "--network", network];

        return hcsctl.InvokeAsync(arguments,
            HcsCtlJsonContext.Default.HcsCtlNetworkEndpointsDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Lists the host's HNS networks with their subnets, read live from HCN.
    /// </summary>
    /// <remarks>
    /// How a resource's <c>WithNetwork</c> name or id becomes a network id to inspect. Read per
    /// consumer rather than cached, because networks are host state that another tool can change
    /// under a running AppHost.
    /// </remarks>
    public static Task<HcsCtlNetworkListDocument> ListNetworksAsync(
        this HcsCtl hcsctl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["network", "ls"],
            HcsCtlJsonContext.Default.HcsCtlNetworkListDocument, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// One network in full, read live from HCN — the only view that carries the subnets' routes.
    /// </summary>
    /// <remarks>
    /// This is where a guest's gateway comes from: HCN stores the address a guest routes
    /// host-bound traffic through as the subnet's default route, not as a derivable property of
    /// the prefix. Always inspected by id; a name resolves through
    /// <see cref="ListNetworksAsync"/> first.
    /// </remarks>
    public static Task<HcsCtlNetworkInspectDocument> InspectNetworkAsync(
        this HcsCtl hcsctl, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(["network", "inspect", "--id", id],
            HcsCtlJsonContext.Default.HcsCtlNetworkInspectDocument, cancellationToken: cancellationToken);
    }
}
