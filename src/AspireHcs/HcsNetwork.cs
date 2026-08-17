namespace AspireHcs;

/// <summary>
/// The one default network for every HCS resource kind. VMs and containers land here unless a
/// caller names a network. Guests on one HNS network reach each other in both directions;
/// guests on different networks are isolated.
/// </summary>
internal static class HcsNetwork
{
    /// <summary>
    /// The Hyper-V Default Switch, by its literal HNS name. It is the ICS network whose built-in
    /// DHCP leases a stock VM guest an address; a container endpoint on it takes an address from
    /// the same switch pool.
    /// </summary>
    /// <remarks>
    /// Not hcsctl's <c>--network default</c> sentinel. That sentinel exists only on the vm verbs;
    /// the container verbs resolve a network by name or id only. Both paths pass this literal
    /// name so VMs and containers land on the same network.
    /// </remarks>
    public const string DefaultSwitchName = "Default Switch";
}
