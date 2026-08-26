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
/// where the port is a DCP proxy bound to the host's loopback, which no HCS guest can reach.
/// This class is the consumer-aware rewrite: the same substitution stock Aspire performs for its
/// Docker consumers — where a host process gets <c>localhost</c>, a container gets
/// <c>ContainerHostName</c> — done here for HCS guests, with
/// <c>&lt;gateway&gt;:&lt;relay port&gt;</c> as the substitute and <see cref="DockerRelay"/> as
/// the listener that answers there.
///
/// What may be rewritten is decided by provenance, not by what a value happens to spell: only
/// values that <see cref="GuestEnvironment"/> traced to an endpoint of another resource are
/// candidates. A user's literal <c>127.0.0.1:8080</c> — a guest-local listener address — is
/// configuration meant as written and passes through untouched. Values from providers the
/// provenance walk could not see through fall back to matching the resolved text, scoped to
/// those variables alone.
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
    /// The distinct host-loopback ports the environment references, in ascending order. Each is
    /// a target the relay must forward before the consumer exists. Traced endpoints contribute
    /// their port when their host is loopback; opaque values contribute whatever loopback
    /// endpoints their text spells. Nothing else contributes — a literal is never a target.
    /// </summary>
    public static IReadOnlyList<int> FindLoopbackPorts(ResolvedGuestEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        SortedSet<int> ports = [];
        foreach (GuestEndpointOccurrence occurrence in environment.Occurrences)
        {
            if (IsLoopbackHost(occurrence.Host) && occurrence.Port is >= 1 and <= 65535)
            {
                ports.Add(occurrence.Port);
            }
        }

        foreach (string name in environment.OpaqueNames)
        {
            if (!environment.Values.TryGetValue(name, out string? value))
            {
                continue;
            }

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

    /// <summary>The value must be the loopback host alone — this is a whole-variable judgement.</summary>
    private static bool IsLoopbackHost(string value)
        => value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value is "127.0.0.1" or "::1" or "[::1]";

    /// <summary>
    /// Substitutes <c>&lt;gateway&gt;:&lt;relay port&gt;</c> for every traced loopback endpoint
    /// whose port has a relay mapping. A split reference pair is rewritten as a pair — the host
    /// variable to the gateway, the port variable to the relay port — identified by the typed
    /// occurrences, never by name convention. An embedded endpoint is rewritten inside its value
    /// by text match, restricted to the ports its own occurrences name; only an opaque variable
    /// is matched against every relayed port. Everything else passes through untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RewriteLoopback(
        ResolvedGuestEnvironment environment,
        string gateway,
        IReadOnlyDictionary<int, int> relayPorts)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentNullException.ThrowIfNull(relayPorts);

        ILookup<string, GuestEndpointOccurrence> byName = environment.Occurrences
            .Where(o => IsLoopbackHost(o.Host) && relayPorts.ContainsKey(o.Port))
            .ToLookup(o => o.Name, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> rewritten = new(environment.Values.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environment.Values)
        {
            rewritten[name] = RewriteValue(name, value, environment, byName[name], gateway, relayPorts);
        }

        return rewritten;
    }

    private static string RewriteValue(
        string name,
        string value,
        ResolvedGuestEnvironment environment,
        IEnumerable<GuestEndpointOccurrence> occurrences,
        string gateway,
        IReadOnlyDictionary<int, int> relayPorts)
    {
        GuestEndpointOccurrence[] traced = [.. occurrences];

        if (traced.Length == 1 && traced[0].Kind == EndpointOccurrenceKind.HostOnly && IsLoopbackHost(value))
        {
            // The split pair's host half: the whole value is the endpoint's host. Its port half
            // is a PortOnly occurrence of the same endpoint, rewritten below — the pair moves
            // together, so the guest never reads an address spliced from two perspectives.
            return gateway;
        }

        if (traced.Length == 1 && traced[0].Kind == EndpointOccurrenceKind.PortOnly
            && value == traced[0].Port.ToString(CultureInfo.InvariantCulture))
        {
            return relayPorts[traced[0].Port].ToString(CultureInfo.InvariantCulture);
        }

        if (traced.Length > 0)
        {
            // Embedded (or several occurrences in one value): the endpoint sits inside a URL or
            // connection string, so it is rewritten by text — but only the ports this value's
            // own occurrences name. A literal fragment spelling some other loopback port is not
            // this value's reference and stays.
            HashSet<int> allowed = [.. traced.Select(o => o.Port)];
            return LoopbackEndpoint().Replace(value, match =>
                TryReadPort(match, out int port) && allowed.Contains(port) && relayPorts.TryGetValue(port, out int relayPort)
                    ? FormattableString.Invariant($"{gateway}:{relayPort}")
                    : match.Value);
        }

        if (environment.OpaqueNames.Contains(name))
        {
            // Nothing traced, provider unreadable: the resolved text is all there is to go on.
            return LoopbackEndpoint().Replace(value, match =>
                TryReadPort(match, out int port) && relayPorts.TryGetValue(port, out int relayPort)
                    ? FormattableString.Invariant($"{gateway}:{relayPort}")
                    : match.Value);
        }

        return value;
    }

    private static bool TryReadPort(Match match, out int port)
        => int.TryParse(match.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;

    /// <summary>
    /// Resolves the consumer's network name or id — <c>WithNetwork</c> accepts both — to the
    /// listed network, so the gateway can be read from its inspection.
    /// </summary>
    public static HcsCtlNetworkRow FindNetwork(
        HcsCtlNetworkListDocument networks, string networkName, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(networks);

        HcsCtlNetworkRow? network = networks.Networks.FirstOrDefault(n =>
            string.Equals(n.Name, networkName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Id, networkName, StringComparison.OrdinalIgnoreCase));

        return network ?? throw new InvalidOperationException(
            $"Resource '{resourceName}' references endpoints on the host, but its network " +
            $"'{networkName}' is not among the networks hcsctl lists, so the gateway the guest " +
            "routes host-bound traffic through cannot be derived.");
    }

    /// <summary>
    /// The address a guest on the network routes host-bound traffic through. HCN stores it as
    /// the subnet's default route, so that is where it is read: the <c>0.0.0.0/0</c> route with
    /// the lowest metric wins. A network whose subnets carry no default route — HCN does not
    /// require one — falls back to the IPv4 subnet's base address plus one, the convention every
    /// built-in network follows.
    /// </summary>
    public static string GatewayAddress(
        HcsCtlNetworkInspectDocument inspection, string networkName, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        string? routed = null;
        int routedMetric = int.MaxValue;
        string? fallback = null;

        foreach (HcsCtlNetworkIpam ipam in inspection.Ipams)
        {
            foreach (HcsCtlNetworkSubnet subnet in ipam.Subnets)
            {
                foreach (HcsCtlNetworkRoute route in subnet.Routes)
                {
                    if (route.DestinationPrefix == "0.0.0.0/0"
                        && IPAddress.TryParse(route.NextHop, out IPAddress? hop)
                        && hop.AddressFamily == AddressFamily.InterNetwork
                        && route.Metric < routedMetric)
                    {
                        routed = route.NextHop;
                        routedMetric = route.Metric;
                    }
                }

                if (fallback is null && subnet.Prefix is { } prefix)
                {
                    string baseAddress = prefix.Split('/')[0];
                    if (IPAddress.TryParse(baseAddress, out IPAddress? address)
                        && address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // Base plus one, computed arithmetically rather than by swapping a
                        // trailing octet, so a subnet whose base does not end in .0 still yields
                        // the right address.
                        byte[] bytes = address.GetAddressBytes();
                        BinaryPrimitives.WriteUInt32BigEndian(bytes, BinaryPrimitives.ReadUInt32BigEndian(bytes) + 1);
                        fallback = new IPAddress(bytes).ToString();
                    }
                }
            }
        }

        return routed ?? fallback ?? throw new InvalidOperationException(
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
    /// <param name="readNetworks">Reads <c>hcsctl network ls</c>; injectable so the flow is testable.</param>
    /// <param name="inspectNetwork">Reads <c>hcsctl network inspect</c> for one network id.</param>
    /// <param name="ensureRelayPort">
    /// <see cref="DockerRelay.EnsurePublishedAsync"/>: target host port in, published relay port out.
    /// </param>
    public static async Task<IReadOnlyDictionary<string, string>> RedirectLoopbackAsync(
        string resourceName,
        string? networkName,
        ResolvedGuestEnvironment environment,
        Func<CancellationToken, Task<HcsCtlNetworkListDocument>> readNetworks,
        Func<string, CancellationToken, Task<HcsCtlNetworkInspectDocument>> inspectNetwork,
        Func<int, CancellationToken, Task<int>> ensureRelayPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readNetworks);
        ArgumentNullException.ThrowIfNull(inspectNetwork);
        ArgumentNullException.ThrowIfNull(ensureRelayPort);

        IReadOnlyList<int> targets = FindLoopbackPorts(environment);
        if (targets.Count == 0)
        {
            return environment.Values;
        }

        if (networkName is null)
        {
            throw new InvalidOperationException(
                $"Resource '{resourceName}' references endpoints that resolve to the host's loopback " +
                $"(port(s) {string.Join(", ", targets)}), but it has no network — a guest without a NIC " +
                "cannot reach the relay that carries them. Add WithNetwork().");
        }

        HcsCtlNetworkRow network = FindNetwork(
            await readNetworks(cancellationToken).ConfigureAwait(false), networkName, resourceName);

        string gateway = GatewayAddress(
            await inspectNetwork(network.Id ?? networkName, cancellationToken).ConfigureAwait(false),
            networkName, resourceName);

        Dictionary<int, int> relayPorts = [];
        foreach (int target in targets)
        {
            relayPorts[target] = await ensureRelayPort(target, cancellationToken).ConfigureAwait(false);
        }

        return RewriteLoopback(environment, gateway, relayPorts);
    }
}
