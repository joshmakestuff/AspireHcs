using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AspireHcs.Cli;

namespace AspireHcs.Hosting;

/// <summary>
/// Makes <c>WithReference</c> work when the consumer is an HCS guest. Aspire's environment
/// callbacks resolve endpoint references from the host's perspective — <c>localhost:port</c>,
/// where the port is a DCP proxy bound to the host's loopback. An HCS guest cannot reach any of
/// that: every guest→host-loopback probe drops (measured, #58). This class is the consumer-aware
/// rewrite (#62): the same substitution stock Aspire performs for its Docker consumers — where a
/// host process gets <c>localhost</c>, a container gets <c>ContainerHostName</c> — done here for
/// HCS guests, with <c>&lt;gateway&gt;:&lt;relay port&gt;</c> as the substitute and
/// <see cref="DockerRelay"/> as the listener that answers there.
/// </summary>
internal static partial class GuestReferences
{
    /// <summary>
    /// A host-loopback endpoint inside a resolved value: <c>localhost</c>, <c>127.0.0.1</c> or
    /// <c>[::1]</c>, with an explicit port. The lookbehind keeps <c>sub.localhost</c> and
    /// <c>my-localhost</c> out; the port must be complete, not a prefix of a longer number. A
    /// loopback host <em>without</em> a port is left alone: the relay forwards ports, and a
    /// portless value has nothing to forward.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9.-])(?:localhost|127\.0\.0\.1|\[::1\]):(?<port>\d{1,5})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackEndpoint();

    /// <summary>
    /// The distinct host-loopback ports referenced anywhere in the resolved environment, in
    /// ascending order. Each is a target the relay must forward before the consumer exists.
    /// </summary>
    public static IReadOnlyList<int> FindLoopbackPorts(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        SortedSet<int> ports = [];
        foreach (string value in environment.Values)
        {
            foreach (Match match in LoopbackEndpoint().Matches(value))
            {
                if (TryReadPort(match, out int port))
                {
                    ports.Add(port);
                }
            }
        }

        return [.. ports];
    }

    /// <summary>
    /// Substitutes <c>&lt;gateway&gt;:&lt;relay port&gt;</c> for every loopback endpoint whose
    /// port has a relay mapping. String-level on the resolved values, deliberately: the loopback
    /// host:port can be embedded anywhere — a URL, a Redis connection string's first segment — and
    /// the value objects that produced it are gone by resolution time. Stock Aspire rewrites its
    /// containers' values by host-name substitution too (<c>HostUrl</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> RewriteLoopback(
        IReadOnlyDictionary<string, string> environment,
        string gateway,
        IReadOnlyDictionary<int, int> relayPorts)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentNullException.ThrowIfNull(relayPorts);

        Dictionary<string, string> rewritten = new(environment.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environment)
        {
            rewritten[name] = LoopbackEndpoint().Replace(value, match =>
                TryReadPort(match, out int port) && relayPorts.TryGetValue(port, out int relayPort)
                    ? FormattableString.Invariant($"{gateway}:{relayPort}")
                    : match.Value);
        }

        return rewritten;
    }

    private static bool TryReadPort(Match match, out int port)
        => int.TryParse(match.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;

    /// <summary>
    /// The address a guest on <paramref name="networkName"/> routes host-bound traffic through:
    /// the base of the network's IPv4 subnet plus one — <c>172.18.176.1</c> for the Default
    /// Switch's <c>172.18.176.0/20</c>, the address the #59 chain was measured against. Derived
    /// from the live listing rather than hardcoded because the subnet is the host's to assign.
    /// </summary>
    public static string GatewayAddress(HcsCtlNetworkListDocument networks, string networkName, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(networks);

        // Name or id, either spelling — WithNetwork accepts both, so both must resolve here.
        HcsCtlNetworkRow? network = networks.Networks.FirstOrDefault(n =>
            string.Equals(n.Name, networkName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Id, networkName, StringComparison.OrdinalIgnoreCase));

        if (network is null)
        {
            throw new InvalidOperationException(
                $"Resource '{resourceName}' references endpoints on the host, but its network " +
                $"'{networkName}' is not among the networks hcsctl lists, so the gateway the guest " +
                "routes host-bound traffic through cannot be derived.");
        }

        foreach (string subnet in network.Subnets)
        {
            string baseAddress = subnet.Split('/')[0];
            if (IPAddress.TryParse(baseAddress, out IPAddress? address)
                && address.AddressFamily == AddressFamily.InterNetwork)
            {
                // The gateway is the subnet base plus one, whatever the prefix length. Computed
                // arithmetically rather than by swapping a trailing octet so a subnet whose base
                // does not end in .0 still yields the right address.
                byte[] bytes = address.GetAddressBytes();
                BinaryPrimitives.WriteUInt32BigEndian(bytes, BinaryPrimitives.ReadUInt32BigEndian(bytes) + 1);
                return new IPAddress(bytes).ToString();
            }
        }

        throw new InvalidOperationException(
            $"Resource '{resourceName}' references endpoints on the host, but its network " +
            $"'{networkName}' reports no IPv4 subnet, so it has no gateway to relay host-bound " +
            "traffic through.");
    }

    /// <summary>
    /// The whole redirect, in the order that keeps injected values honest: find the loopback
    /// targets, stand a relay forward up for each, and only then rewrite — so a value naming
    /// <c>&lt;gateway&gt;:&lt;relay port&gt;</c> never reaches a guest before something answers
    /// there. An environment that references nothing on the host's loopback passes through
    /// untouched, and Docker is never required for it.
    /// </summary>
    /// <param name="readNetworks">Reads <c>hcsctl network ls</c>; injectable so the seam is testable.</param>
    /// <param name="ensureRelayPort">
    /// <see cref="DockerRelay.EnsurePublishedAsync"/>: target host port in, published relay port out.
    /// </param>
    public static async Task<IReadOnlyDictionary<string, string>> RedirectLoopbackAsync(
        string resourceName,
        string? networkName,
        IReadOnlyDictionary<string, string> environment,
        Func<CancellationToken, Task<HcsCtlNetworkListDocument>> readNetworks,
        Func<int, CancellationToken, Task<int>> ensureRelayPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readNetworks);
        ArgumentNullException.ThrowIfNull(ensureRelayPort);

        IReadOnlyList<int> targets = FindLoopbackPorts(environment);
        if (targets.Count == 0)
        {
            return environment;
        }

        if (networkName is null)
        {
            throw new InvalidOperationException(
                $"Resource '{resourceName}' references endpoints that resolve to the host's loopback " +
                $"(port(s) {string.Join(", ", targets)}), but it has no network — a guest without a NIC " +
                "cannot reach the relay that carries them. Add WithNetwork().");
        }

        string gateway = GatewayAddress(
            await readNetworks(cancellationToken).ConfigureAwait(false), networkName, resourceName);

        Dictionary<int, int> relayPorts = [];
        foreach (int target in targets)
        {
            relayPorts[target] = await ensureRelayPort(target, cancellationToken).ConfigureAwait(false);
        }

        return RewriteLoopback(environment, gateway, relayPorts);
    }
}
