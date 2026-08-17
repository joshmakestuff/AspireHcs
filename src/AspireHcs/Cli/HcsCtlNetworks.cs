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
}
