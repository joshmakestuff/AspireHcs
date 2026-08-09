namespace AspireHcs;

/// <summary>
/// The one default network for every HCS resource kind. VMs and containers land here unless a
/// caller names a network, because guests on one HNS network reach each other in both directions
/// and guests on different networks are isolated — measured (#58). Sharing the default is what
/// makes a VM and a Hyper-V-isolated container in one AppHost able to talk out of the box;
/// placing a resource elsewhere is the isolation opt-in.
/// </summary>
internal static class HcsNetwork
{
    /// <summary>
    /// The Hyper-V Default Switch, by its literal HNS name. It is the ICS network whose built-in
    /// DHCP leases a stock VM guest an address, and a container endpoint on it takes an address
    /// from the same switch pool — measured working in both directions (#60).
    /// </summary>
    /// <remarks>
    /// Deliberately not hcsctl's <c>--network default</c> sentinel. That sentinel is a vm-verb
    /// feature: the container verbs resolve a network by name or id only, so <c>default</c> would
    /// fail container create on any host without a network literally named that. Passing the same
    /// literal name on both paths is what guarantees co-location — or one loud failure, never a
    /// silent divergence onto different networks.
    /// </remarks>
    public const string DefaultSwitchName = "Default Switch";
}
