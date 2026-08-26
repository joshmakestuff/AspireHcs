using System.Runtime.Versioning;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// WithReference resolves to host-perspective values — endpoints on the host's loopback, which no
// HCS guest can reach. These pin the consumer-aware rewrite: which values count as loopback
// targets (traced endpoints and opaque fallbacks, never the user's literals), what the
// substitution looks like, where the gateway comes from, and what the failures say. Fake
// documents throughout, following ContainerAddressLeaseTests.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestReferencesTests
{
    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static ResolvedGuestEnvironment Env(
        Dictionary<string, string> values,
        IReadOnlyList<GuestEndpointOccurrence>? occurrences = null,
        params string[] opaque)
        => new(values, occurrences ?? [], opaque.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static GuestEndpointOccurrence Embedded(string name, int port, string host = "localhost")
        => new(name, EndpointOccurrenceKind.Embedded, host, port);

    // ---- FindLoopbackPorts: what the relay must forward ----

    [Fact]
    public void A_traced_endpoint_on_host_loopback_is_a_target()
    {
        // The shape a Redis WithReference injects: DCP proxy on host loopback.
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(Env(
            Values(("ConnectionStrings__cache", "localhost:65063,password=hunter2")),
            [Embedded("ConnectionStrings__cache", 65063)]));

        Assert.Equal([65063], ports);
    }

    [Fact]
    public void The_same_endpoint_across_variables_is_one_target()
    {
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(Env(
            Values(("A", "localhost:5000"), ("B", "http://127.0.0.1:5000"), ("C", "localhost:6000")),
            [Embedded("A", 5000), Embedded("B", 5000, "127.0.0.1"), Embedded("C", 6000)]));

        Assert.Equal([5000, 6000], ports);
    }

    // The split pair, identified by its typed occurrences — no name convention involved.
    [Fact]
    public void A_split_host_and_port_pair_is_a_target()
    {
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(Env(
            Values(("CACHE_HOST", "localhost"), ("CACHE_PORT", "62310")),
            [
                new("CACHE_HOST", EndpointOccurrenceKind.HostOnly, "localhost", 62310),
                new("CACHE_PORT", EndpointOccurrenceKind.PortOnly, "localhost", 62310),
            ]));

        Assert.Equal([62310], ports);
    }

    // A traced endpoint on a non-loopback host is reachable from the guest directly (another
    // guest on the same HNS network) or not relayable at all; either way it is not a target.
    [Fact]
    public void A_traced_endpoint_that_is_not_host_loopback_is_not_a_target()
    {
        Assert.Empty(GuestReferences.FindLoopbackPorts(Env(
            Values(("PEER", "172.18.184.17:8080")),
            [Embedded("PEER", 8080, "172.18.184.17")])));
    }

    // The defect the provenance model exists to fix: a user's literal spelling a loopback
    // endpoint — a guest-local listener, a split-looking pair — is configuration meant as
    // written. With no occurrence and no opaque mark, it contributes no target.
    [Fact]
    public void A_literal_loopback_value_is_never_a_target()
    {
        Assert.Empty(GuestReferences.FindLoopbackPorts(Env(Values(
            ("BIND_ADDRESS", "127.0.0.1:8080"),
            ("FOO_HOST", "localhost"),
            ("FOO_PORT", "6379")))));
    }

    // An opaque value falls back to the text match, for that variable alone.
    [Theory]
    [InlineData("http://localhost:5488/interop")]
    [InlineData("tcp://127.0.0.1:5488")]
    [InlineData("Endpoint=[::1]:5488;")]
    [InlineData("LOCALHOST:5488")]
    public void Loopback_spellings_inside_an_opaque_value_are_found(string value)
    {
        Assert.Equal([5488], GuestReferences.FindLoopbackPorts(
            Env(Values(("SERVICE", value)), opaque: "SERVICE")));
    }

    [Theory]
    [InlineData("sub.localhost.example:6379")]      // "localhost" as a label inside a longer name
    [InlineData("my-localhost:6379")]               // and as a suffix
    [InlineData("10.127.0.0.1:80")]                 // 127.0.0.1 as the tail of a longer address
    [InlineData("localhost")]                       // no port: nothing to forward
    [InlineData("localhost:0")]                     // not a connectable port
    [InlineData("localhost:70000")]                 // not a port at all
    public void Opaque_values_that_do_not_spell_a_loopback_endpoint_are_not_targets(string value)
    {
        Assert.Empty(GuestReferences.FindLoopbackPorts(Env(Values(("V", value)), opaque: "V")));
    }

    // ---- RewriteLoopback: what the guest reads instead ----

    [Fact]
    public void The_loopback_endpoint_is_replaced_by_gateway_and_relay_port()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(
                Values(("ConnectionStrings__cache", "localhost:65063,password=hunter2")),
                [Embedded("ConnectionStrings__cache", 65063)]),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [65063] = 55007 });

        Assert.Equal("172.18.176.1:55007,password=hunter2", rewritten["ConnectionStrings__cache"]);
    }

    [Fact]
    public void Every_traced_occurrence_in_a_value_is_rewritten()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(
                Values(("URLS", "http://localhost:5000;https://127.0.0.1:5001")),
                [Embedded("URLS", 5000), Embedded("URLS", 5001, "127.0.0.1")]),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000, [5001] = 40001 });

        Assert.Equal("http://172.18.176.1:40000;https://172.18.176.1:40001", rewritten["URLS"]);
    }

    // The pair is rewritten as a pair — host to the gateway, port to the relay port. One half
    // alone would splice an address from two perspectives, worse than either whole.
    [Fact]
    public void A_split_pair_is_rewritten_together()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(
                Values(("CACHE_HOST", "localhost"), ("CACHE_PORT", "62310")),
                [
                    new("CACHE_HOST", EndpointOccurrenceKind.HostOnly, "localhost", 62310),
                    new("CACHE_PORT", EndpointOccurrenceKind.PortOnly, "localhost", 62310),
                ]),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [62310] = 62315 });

        Assert.Equal("172.18.176.1", rewritten["CACHE_HOST"]);
        Assert.Equal("62315", rewritten["CACHE_PORT"]);
    }

    // The #67-shaped pin: literals survive whatever they spell, even alongside a real reference
    // being rewritten in the same environment.
    [Fact]
    public void Literal_values_pass_through_untouched_whatever_they_spell()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(
                Values(
                    ("CACHE", "localhost:5000"),
                    ("BIND_ADDRESS", "127.0.0.1:8080"),
                    ("SELF_HOST", "localhost"),
                    ("SELF_PORT", "8080"),
                    ("MODE", "fast")),
                [Embedded("CACHE", 5000)]),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000, [8080] = 40001 });

        Assert.Equal("172.18.176.1:40000", rewritten["CACHE"]);
        Assert.Equal("127.0.0.1:8080", rewritten["BIND_ADDRESS"]);
        Assert.Equal("localhost", rewritten["SELF_HOST"]);
        Assert.Equal("8080", rewritten["SELF_PORT"]);
        Assert.Equal("fast", rewritten["MODE"]);
    }

    // Inside a traced value, only that value's own endpoints are rewritten: a literal fragment
    // naming some other loopback port is the user's text, not this value's reference.
    [Fact]
    public void A_traced_value_rewrites_only_its_own_endpoint_ports()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(
                Values(("COMPOSITE", "api=localhost:5000;debug=localhost:9229")),
                [Embedded("COMPOSITE", 5000)]),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000, [9229] = 40002 });

        Assert.Equal("api=172.18.176.1:40000;debug=localhost:9229", rewritten["COMPOSITE"]);
    }

    [Fact]
    public void An_opaque_value_is_rewritten_by_text_match()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(Values(("CUSTOM", "http://localhost:5000/")), opaque: "CUSTOM"),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000 });

        Assert.Equal("http://172.18.176.1:40000/", rewritten["CUSTOM"]);
    }

    // The exact environment WithReference(cache) injected in the live run (password altered):
    // every shape Aspire produces, rewritten coherently or deliberately left alone.
    [Fact]
    public async Task The_live_redis_reference_environment_rewrites_coherently()
    {
        ResolvedGuestEnvironment environment = Env(
            Values(
                ("ConnectionStrings__cache", "localhost:62310,password=hunter2,ssl=true"),
                ("CACHE_HOST", "localhost"),
                ("CACHE_PORT", "62310"),
                ("CACHE_PASSWORD", "hunter2"),
                ("CACHE_URI", "rediss://:hunter2@localhost:62310")),
            [
                Embedded("ConnectionStrings__cache", 62310),
                new("CACHE_HOST", EndpointOccurrenceKind.HostOnly, "localhost", 62310),
                new("CACHE_PORT", EndpointOccurrenceKind.PortOnly, "localhost", 62310),
                Embedded("CACHE_URI", 62310),
            ]);

        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "rockyvm", "Default Switch", environment,
            _ => Task.FromResult(Networks(Network("Default Switch"))),
            (_, _) => Task.FromResult(Inspection("172.18.176.0/20", Route("172.18.176.1"))),
            (target, _) => Task.FromResult(target == 62310 ? 62315 : throw new InvalidOperationException("one target expected")),
            CancellationToken.None);

        Assert.Equal("172.18.176.1:62315,password=hunter2,ssl=true", result["ConnectionStrings__cache"]);
        Assert.Equal("172.18.176.1", result["CACHE_HOST"]);
        Assert.Equal("62315", result["CACHE_PORT"]);
        Assert.Equal("hunter2", result["CACHE_PASSWORD"]);
        Assert.Equal("rediss://:hunter2@172.18.176.1:62315", result["CACHE_URI"]);
    }

    // ---- FindNetwork and GatewayAddress: where the gateway comes from ----

    private static HcsCtlNetworkListDocument Networks(params HcsCtlNetworkRow[] rows)
        => new() { Ok = true, Networks = rows };

    private static HcsCtlNetworkRow Network(string name)
        => new() { Id = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", Name = name, Type = "ICS" };

    private static HcsCtlNetworkRoute Route(string nextHop, string destination = "0.0.0.0/0", int metric = 0)
        => new() { NextHop = nextHop, DestinationPrefix = destination, Metric = metric };

    private static HcsCtlNetworkInspectDocument Inspection(string? prefix, params HcsCtlNetworkRoute[] routes)
        => new()
        {
            Ok = true,
            Id = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444",
            Ipams = prefix is null
                ? []
                : [new HcsCtlNetworkIpam { Subnets = [new HcsCtlNetworkSubnet { Prefix = prefix, Routes = routes }] }],
        };

    // WithNetwork accepts a name or an id; the lookup must resolve both.
    [Theory]
    [InlineData("Default Switch")]
    [InlineData("C08CB7B8-9B3C-408E-8E30-5E16A3AEB444")]
    public void The_network_resolves_by_name_or_id(string reference)
    {
        HcsCtlNetworkRow row = GuestReferences.FindNetwork(
            Networks(Network("Default Switch")), reference, "hcsworker");

        Assert.Equal("Default Switch", row.Name);
    }

    [Fact]
    public void A_missing_network_names_itself_and_the_resource()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => GuestReferences.FindNetwork(Networks(Network("nat")), "Default Switch", "hcsworker"));

        Assert.Contains("Default Switch", thrown.Message);
        Assert.Contains("hcsworker", thrown.Message);
    }

    // The gateway is the default route's next hop — HCN's model, not an arithmetic convention.
    // A custom network routed through an address other than base+1 must get that address.
    [Fact]
    public void The_gateway_is_the_default_routes_next_hop()
    {
        string gateway = GuestReferences.GatewayAddress(
            Inspection("172.30.10.0/24", Route("172.30.10.254")), "custom", "hcsworker");

        Assert.Equal("172.30.10.254", gateway);
    }

    [Fact]
    public void The_lowest_metric_default_route_wins()
    {
        string gateway = GuestReferences.GatewayAddress(
            Inspection("172.30.10.0/24", Route("172.30.10.9", metric: 10), Route("172.30.10.1", metric: 1)),
            "custom", "hcsworker");

        Assert.Equal("172.30.10.1", gateway);
    }

    // A route to somewhere specific is not the way host-bound traffic leaves the subnet.
    [Fact]
    public void A_non_default_route_is_ignored_for_the_gateway()
    {
        string gateway = GuestReferences.GatewayAddress(
            Inspection("172.30.10.0/24", Route("172.30.10.7", destination: "10.0.0.0/8")), "custom", "hcsworker");

        Assert.Equal("172.30.10.1", gateway);
    }

    // HCN does not require a default route; every built-in network's gateway is base+1, so that
    // is the fallback. Computed arithmetically, so a base not ending in .0 still works.
    [Fact]
    public void Without_a_default_route_the_gateway_falls_back_to_base_plus_one()
    {
        Assert.Equal("172.27.96.1", GuestReferences.GatewayAddress(
            Inspection("172.27.96.0/20"), "Default Switch", "hcsworker"));
    }

    [Fact]
    public void A_network_without_an_ipv4_subnet_is_refused_with_the_reason()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => GuestReferences.GatewayAddress(Inspection(null), "EXTERNAL", "hcsworker"));

        Assert.Contains("no IPv4 subnet", thrown.Message);
    }

    [Fact]
    public void An_ipv6_subnet_is_skipped_for_the_ipv4_one()
    {
        HcsCtlNetworkInspectDocument inspection = new()
        {
            Ok = true,
            Ipams =
            [
                new HcsCtlNetworkIpam { Subnets = [new HcsCtlNetworkSubnet { Prefix = "fd00::/64" }] },
                new HcsCtlNetworkIpam { Subnets = [new HcsCtlNetworkSubnet { Prefix = "172.18.176.0/20" }] },
            ],
        };

        Assert.Equal("172.18.176.1", GuestReferences.GatewayAddress(inspection, "Default Switch", "hcsworker"));
    }

    // ---- RedirectLoopbackAsync: the whole redirect, over fakes ----

    [Fact]
    public async Task An_environment_without_loopback_targets_never_touches_the_network_or_the_relay()
    {
        // No relay, no docker, no hcsctl: an AppHost whose references stay guest-reachable must
        // not acquire a Docker dependency it does not use.
        ResolvedGuestEnvironment environment = Env(
            Values(("PEER", "172.18.184.17:8080"), ("BIND_ADDRESS", "127.0.0.1:8080")),
            [Embedded("PEER", 8080, "172.18.184.17")]);

        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "hcsworker", "Default Switch", environment,
            _ => throw new InvalidOperationException("network ls must not run"),
            (_, _) => throw new InvalidOperationException("network inspect must not run"),
            (_, _) => throw new InvalidOperationException("the relay must not start"),
            CancellationToken.None);

        Assert.Same(environment.Values, result);
    }

    [Fact]
    public async Task Each_distinct_target_gets_a_relay_forward_and_the_values_are_rewritten()
    {
        List<int> requested = [];
        List<string> inspected = [];

        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "hcsworker", "Default Switch",
            Env(
                Values(("CACHE", "localhost:65063"), ("API", "http://localhost:5488/;http://127.0.0.1:65063/")),
                [Embedded("CACHE", 65063), Embedded("API", 5488), Embedded("API", 65063, "127.0.0.1")]),
            _ => Task.FromResult(Networks(Network("Default Switch"))),
            (id, _) =>
            {
                inspected.Add(id);
                return Task.FromResult(Inspection("172.18.176.0/20", Route("172.18.176.1")));
            },
            (target, _) =>
            {
                requested.Add(target);
                return Task.FromResult(target + 1000);
            },
            CancellationToken.None);

        Assert.Equal([5488, 65063], requested);
        // Inspection goes by the listed row's id, not the WithNetwork spelling.
        Assert.Equal(["c08cb7b8-9b3c-408e-8e30-5e16a3aeb444"], inspected);
        Assert.Equal("172.18.176.1:66063", result["CACHE"]);
        Assert.Equal("http://172.18.176.1:6488/;http://172.18.176.1:66063/", result["API"]);
    }

    // The honest failure for a networkless consumer with host references: the guest has no NIC,
    // so no gateway exists to carry the relayed traffic.
    [Fact]
    public async Task A_resource_without_a_network_is_refused_naming_the_ports()
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GuestReferences.RedirectLoopbackAsync(
                "hcsworker", networkName: null,
                Env(Values(("CACHE", "localhost:65063")), [Embedded("CACHE", 65063)]),
                _ => throw new InvalidOperationException("network ls must not run"),
                (_, _) => throw new InvalidOperationException("network inspect must not run"),
                (_, _) => throw new InvalidOperationException("the relay must not start"),
                CancellationToken.None));

        Assert.Contains("65063", thrown.Message);
        Assert.Contains("WithNetwork", thrown.Message);
    }
}
