namespace AspireHcs.Cli;

/// <summary>
/// The <c>network</c> verbs, as methods. Same reason as <see cref="HcsCtlContainers"/>: an option
/// spelled wrong is exit 64, indistinguishable from a bad value in a resource's configuration.
/// The group is read-only and unelevated, and it rejects <c>--store</c> — which <see cref="HcsCtl"/>
/// already knows, so nothing here has to.
/// </summary>
internal static class HcsCtlNetworks
{
    /// <summary>
    /// Lists HNS endpoints with their current addresses, read live from HCN, optionally filtered
    /// to one network by name or id.
    /// </summary>
    /// <remarks>
    /// This is what the container path polls for an ICS lease. hcsctl's state.json — and
    /// <c>container inspect</c>, which reports that snapshot — keeps only the create-time address
    /// list, empty forever on an ICS network; this listing is the one view that updates when the
    /// lease lands (#63; hcsctl#43 is the same timing on the VM side, where <c>vm ip</c> waits).
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
