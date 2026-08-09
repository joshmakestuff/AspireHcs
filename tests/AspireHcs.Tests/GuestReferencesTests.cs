using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// #62. WithReference resolves to host-perspective values — endpoints on the host's loopback,
// which no HCS guest can reach (#58: every guest→host-loopback probe drops). These pin the
// consumer-aware rewrite: which values count as loopback targets, what the substitution looks
// like, where the gateway comes from, and what the failures say. Fake documents throughout,
// following ContainerAddressLeaseTests; the live `network ls` shape is captured verbatim at the
// bottom.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestReferencesTests
{
    private static Dictionary<string, string> Env(params (string Name, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    // ---- FindLoopbackPorts: what the relay must forward ----

    [Fact]
    public void A_bare_host_and_port_value_is_a_target()
    {
        // The shape a Redis WithReference injects: DCP proxy on host loopback.
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(
            Env(("ConnectionStrings__cache", "localhost:65063,password=hunter2")));

        Assert.Equal([65063], ports);
    }

    [Theory]
    [InlineData("http://localhost:5488/interop")]
    [InlineData("tcp://127.0.0.1:5488")]
    [InlineData("Endpoint=[::1]:5488;")]
    [InlineData("LOCALHOST:5488")]
    public void Loopback_spellings_inside_larger_values_are_found(string value)
    {
        Assert.Equal([5488], GuestReferences.FindLoopbackPorts(Env(("SERVICE", value))));
    }

    [Fact]
    public void The_same_port_across_variables_is_one_target()
    {
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(Env(
            ("A", "localhost:5000"),
            ("B", "http://127.0.0.1:5000"),
            ("C", "localhost:6000")));

        Assert.Equal([5000, 6000], ports);
    }

    // A non-loopback host is reachable from the guest directly (same HNS network, #58) or not
    // relayable at all; either way it is not this feature's business.
    [Theory]
    [InlineData("172.18.184.17:8080")]              // another HCS guest, reached directly
    [InlineData("redis.example.com:6379")]          // a real remote
    [InlineData("sub.localhost.example:6379")]      // "localhost" as a label inside a longer name
    [InlineData("my-localhost:6379")]               // and as a suffix
    [InlineData("10.127.0.0.1:80")]                 // 127.0.0.1 as the tail of a longer address
    [InlineData("localhost")]                       // no port: nothing to forward
    [InlineData("localhost:0")]                     // not a connectable port
    [InlineData("localhost:70000")]                 // not a port at all
    public void Values_that_are_not_a_host_loopback_endpoint_are_not_targets(string value)
    {
        Assert.Empty(GuestReferences.FindLoopbackPorts(Env(("V", value))));
    }

    // Aspire's other injected shape, observed live: the endpoint split across two variables.
    // No single value carries host and port together, so the value-level match cannot see it.
    [Fact]
    public void A_split_host_and_port_pair_is_a_target()
    {
        IReadOnlyList<int> ports = GuestReferences.FindLoopbackPorts(
            Env(("CACHE_HOST", "localhost"), ("CACHE_PORT", "62310")));

        Assert.Equal([62310], ports);
    }

    [Theory]
    [InlineData("CACHE_HOST", "localhost", "OTHER_PORT", "62310")]  // no sibling: nothing to forward
    [InlineData("CACHE_HOST", "172.18.184.17", "CACHE_PORT", "62310")]  // not loopback: guest-reachable already
    [InlineData("CACHE_HOST", "localhost", "CACHE_PORT", "not-a-port")]
    [InlineData("CACHEHOST", "localhost", "CACHEPORT", "62310")]  // not the pair convention
    public void A_pair_that_is_not_a_loopback_host_with_a_port_is_not_a_target(
        string hostName, string hostValue, string portName, string portValue)
    {
        Assert.Empty(GuestReferences.FindLoopbackPorts(Env((hostName, hostValue), (portName, portValue))));
    }

    // ---- RewriteLoopback: what the guest reads instead ----

    [Fact]
    public void The_loopback_endpoint_is_replaced_by_gateway_and_relay_port()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(("ConnectionStrings__cache", "localhost:65063,password=hunter2")),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [65063] = 55007 });

        Assert.Equal("172.18.176.1:55007,password=hunter2", rewritten["ConnectionStrings__cache"]);
    }

    [Fact]
    public void Every_occurrence_in_a_value_is_rewritten()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(("URLS", "http://localhost:5000;https://127.0.0.1:5001")),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000, [5001] = 40001 });

        Assert.Equal("http://172.18.176.1:40000;https://172.18.176.1:40001", rewritten["URLS"]);
    }

    [Fact]
    public void A_value_without_loopback_targets_passes_through_unchanged()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(("PEER", "172.18.184.17:8080"), ("MODE", "fast")),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [5000] = 40000 });

        Assert.Equal("172.18.184.17:8080", rewritten["PEER"]);
        Assert.Equal("fast", rewritten["MODE"]);
    }

    // The pair is rewritten as a pair — host to the gateway, port to the relay port. One half
    // alone would splice an address from two perspectives, worse than either whole.
    [Fact]
    public void A_split_pair_is_rewritten_together()
    {
        IReadOnlyDictionary<string, string> rewritten = GuestReferences.RewriteLoopback(
            Env(("CACHE_HOST", "localhost"), ("CACHE_PORT", "62310")),
            gateway: "172.18.176.1",
            relayPorts: new Dictionary<int, int> { [62310] = 62315 });

        Assert.Equal("172.18.176.1", rewritten["CACHE_HOST"]);
        Assert.Equal("62315", rewritten["CACHE_PORT"]);
    }

    // The exact environment WithReference(cache) injected in the live run (2026-08-09, password
    // altered): every shape Aspire produces, rewritten coherently or deliberately left alone.
    [Fact]
    public async Task The_live_redis_reference_environment_rewrites_coherently()
    {
        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "rockyvm", "Default Switch",
            Env(
                ("ConnectionStrings__cache", "localhost:62310,password=hunter2,ssl=true"),
                ("CACHE_HOST", "localhost"),
                ("CACHE_PORT", "62310"),
                ("CACHE_PASSWORD", "hunter2"),
                ("CACHE_URI", "rediss://:hunter2@localhost:62310")),
            _ => Task.FromResult(Networks(Network("Default Switch", "172.18.176.0/20"))),
            (target, _) => Task.FromResult(target == 62310 ? 62315 : throw new InvalidOperationException("one target expected")),
            CancellationToken.None);

        Assert.Equal("172.18.176.1:62315,password=hunter2,ssl=true", result["ConnectionStrings__cache"]);
        Assert.Equal("172.18.176.1", result["CACHE_HOST"]);
        Assert.Equal("62315", result["CACHE_PORT"]);
        Assert.Equal("hunter2", result["CACHE_PASSWORD"]);
        Assert.Equal("rediss://:hunter2@172.18.176.1:62315", result["CACHE_URI"]);
    }

    // ---- GatewayAddress: derived from `network ls`, never hardcoded ----

    private static HcsCtlNetworkListDocument Networks(params HcsCtlNetworkRow[] rows)
        => new() { Ok = true, Networks = rows };

    private static HcsCtlNetworkRow Network(string name, params string[] subnets)
        => new() { Id = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", Name = name, Type = "ICS", Subnets = subnets };

    [Fact]
    public void The_gateway_is_the_subnet_base_plus_one()
    {
        string gateway = GuestReferences.GatewayAddress(
            Networks(Network("Default Switch", "172.18.176.0/20")), "Default Switch", "hcsworker");

        Assert.Equal("172.18.176.1", gateway);
    }

    // WithNetwork accepts a name or an id; the gateway lookup must resolve both.
    [Fact]
    public void The_network_resolves_by_id_too()
    {
        string gateway = GuestReferences.GatewayAddress(
            Networks(Network("Default Switch", "172.18.176.0/20")),
            "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444", "hcsworker");

        Assert.Equal("172.18.176.1", gateway);
    }

    // An IPv6 subnet first in the list must not produce a nonsense gateway; the IPv4 one wins.
    [Fact]
    public void An_ipv6_subnet_is_skipped_for_the_ipv4_one()
    {
        string gateway = GuestReferences.GatewayAddress(
            Networks(Network("Default Switch", "fd00::/64", "172.18.176.0/20")), "Default Switch", "hcsworker");

        Assert.Equal("172.18.176.1", gateway);
    }

    [Fact]
    public void A_missing_network_names_itself_and_the_resource()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => GuestReferences.GatewayAddress(Networks(Network("nat", "172.30.32.0/20")), "Default Switch", "hcsworker"));

        Assert.Contains("Default Switch", thrown.Message);
        Assert.Contains("hcsworker", thrown.Message);
    }

    // A Transparent network reports no subnets at all — a real row on this host, not a
    // hypothetical — so the failure must say "no gateway", not crash deriving one.
    [Fact]
    public void A_network_without_an_ipv4_subnet_is_refused_with_the_reason()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => GuestReferences.GatewayAddress(Networks(Network("EXTERNAL")), "EXTERNAL", "hcsworker"));

        Assert.Contains("no IPv4 subnet", thrown.Message);
    }

    // ---- RedirectLoopbackAsync: the whole redirect, over fakes ----

    [Fact]
    public async Task An_environment_without_loopback_targets_never_touches_the_network_or_the_relay()
    {
        // No relay, no docker, no hcsctl: an AppHost whose references stay guest-reachable must
        // not acquire a Docker dependency it does not use.
        IReadOnlyDictionary<string, string> environment = Env(("PEER", "172.18.184.17:8080"));

        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "hcsworker", "Default Switch", environment,
            _ => throw new InvalidOperationException("network ls must not run"),
            (_, _) => throw new InvalidOperationException("the relay must not start"),
            CancellationToken.None);

        Assert.Same(environment, result);
    }

    [Fact]
    public async Task Each_distinct_target_gets_a_relay_forward_and_the_values_are_rewritten()
    {
        List<int> requested = [];

        IReadOnlyDictionary<string, string> result = await GuestReferences.RedirectLoopbackAsync(
            "hcsworker", "Default Switch",
            Env(("CACHE", "localhost:65063"), ("API", "http://localhost:5488/;http://127.0.0.1:65063/")),
            _ => Task.FromResult(Networks(Network("Default Switch", "172.18.176.0/20"))),
            (target, _) =>
            {
                requested.Add(target);
                return Task.FromResult(target + 1000);
            },
            CancellationToken.None);

        Assert.Equal([5488, 65063], requested);
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
                "hcsworker", networkName: null, Env(("CACHE", "localhost:65063")),
                _ => throw new InvalidOperationException("network ls must not run"),
                (_, _) => throw new InvalidOperationException("the relay must not start"),
                CancellationToken.None));

        Assert.Contains("65063", thrown.Message);
        Assert.Contains("WithNetwork", thrown.Message);
    }

    // Captured verbatim from `hcsctl network ls --json` (v0.2.0, 2026-08-09), trimmed to the two
    // rows that matter: the ICS default with its subnet, and a Transparent network with none.
    // The wire names are hcsctl's — lowercase.
    private const string LiveNetworks = """
        {
          "command": "network ls",
          "networks": [
            {
              "id": "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444",
              "name": "Default Switch",
              "type": "ICS",
              "subnets": [
                "172.18.176.0/20"
              ],
              "endpoints": 2
            },
            {
              "id": "7d3c684d-379e-45f1-817a-6a73df3694ad",
              "name": "EXTERNAL",
              "type": "Transparent",
              "subnets": [],
              "endpoints": 0
            }
          ],
          "ok": true
        }
        """;

    [Fact]
    public void A_live_network_listing_binds_every_field_we_read()
    {
        HcsCtlNetworkListDocument document = JsonSerializer.Deserialize(
            LiveNetworks, HcsCtlJsonContext.Default.HcsCtlNetworkListDocument)!;

        Assert.True(document.Ok);
        Assert.Equal(2, document.Networks.Count);

        HcsCtlNetworkRow defaultSwitch = document.Networks.Single(n => n.Name == "Default Switch");
        Assert.Equal("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", defaultSwitch.Id);
        Assert.Equal("ICS", defaultSwitch.Type);
        Assert.Equal(["172.18.176.0/20"], defaultSwitch.Subnets);
        Assert.Equal(2, defaultSwitch.EndpointCount);

        // And the derived gateway off the live shape end to end.
        Assert.Equal("172.18.176.1", GuestReferences.GatewayAddress(document, "Default Switch", "hcsworker"));
    }

    // Go marshals a nil slice as JSON null — the same trap every other document guards against.
    [Fact]
    public void Null_collections_bind_to_empty_rather_than_null()
    {
        HcsCtlNetworkListDocument noRows = JsonSerializer.Deserialize(
            """{"ok":true,"command":"network ls","networks":null}""",
            HcsCtlJsonContext.Default.HcsCtlNetworkListDocument)!;
        Assert.Empty(noRows.Networks);

        HcsCtlNetworkListDocument noSubnets = JsonSerializer.Deserialize(
            """{"ok":true,"networks":[{"id":"x","subnets":null}]}""",
            HcsCtlJsonContext.Default.HcsCtlNetworkListDocument)!;
        Assert.Empty(Assert.Single(noSubnets.Networks).Subnets);
    }

    // The guest exec document's shape, from hcsctl's execResult struct — pinned so a wire rename
    // breaks a test rather than silently binding VM env delivery's verdict to defaults.
    [Fact]
    public void A_guest_exec_document_binds_every_field_we_read()
    {
        HcsCtlGuestExecDocument document = JsonSerializer.Deserialize(
            """
            {
              "ok": true,
              "command": "guest exec",
              "vmId": "8b5c3a51-6f2e-4d5a-9c1e-2f9d2e6f7a10",
              "ran": "printf x",
              "exitCode": 0,
              "timedOut": false,
              "elapsedMs": 412
            }
            """,
            HcsCtlJsonContext.Default.HcsCtlGuestExecDocument)!;

        Assert.True(document.Ok);
        Assert.Equal("8b5c3a51-6f2e-4d5a-9c1e-2f9d2e6f7a10", document.VmId);
        Assert.Equal(0, document.ExitCode);
        Assert.False(document.TimedOut);
        Assert.Equal(412, document.ElapsedMs);
    }
}
