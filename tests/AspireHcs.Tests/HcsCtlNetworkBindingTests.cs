using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// The wire shapes the reference relay reads: `network ls` (name/id to network), `network
// inspect` (the subnet's default route, where the gateway lives), and `guest exec` (VM env
// delivery's verdict). Pinned from live captures so a wire rename breaks a test rather than
// silently binding to defaults.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlNetworkBindingTests
{
    // Captured verbatim from `hcsctl network ls --json` (v0.5.0, 2026-08-26), trimmed to the two
    // rows that matter: the ICS default with its subnet, and a Transparent network with none.
    private const string LiveNetworks = """
        {
          "command": "network ls",
          "networks": [
            {
              "id": "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444",
              "name": "Default Switch",
              "type": "ICS",
              "subnets": [
                "172.27.96.0/20"
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
        Assert.Equal(["172.27.96.0/20"], defaultSwitch.Subnets);
        Assert.Equal(2, defaultSwitch.EndpointCount);
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

    // Captured verbatim from `hcsctl network inspect --id ... --json` (v0.5.0, 2026-08-26),
    // trimmed to the fields this side binds. The default route's nextHop is the gateway; metric
    // is omitted on the wire when zero.
    private const string LiveInspect = """
        {
          "ok": true,
          "command": "network inspect",
          "id": "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444",
          "name": "Default Switch",
          "type": "ICS",
          "schemaVersion": "2.0",
          "flags": 11,
          "flagNames": [
            "EnableNonPersistent"
          ],
          "ipams": [
            {
              "type": "",
              "subnets": [
                {
                  "prefix": "172.27.96.0/20",
                  "routes": [
                    {
                      "nextHop": "172.27.96.1",
                      "destinationPrefix": "0.0.0.0/0"
                    }
                  ]
                }
              ]
            }
          ],
          "endpoints": []
        }
        """;

    [Fact]
    public void A_live_network_inspection_binds_every_field_we_read()
    {
        HcsCtlNetworkInspectDocument document = JsonSerializer.Deserialize(
            LiveInspect, HcsCtlJsonContext.Default.HcsCtlNetworkInspectDocument)!;

        Assert.True(document.Ok);
        Assert.Equal("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", document.Id);
        Assert.Equal("Default Switch", document.Name);

        HcsCtlNetworkSubnet subnet = Assert.Single(Assert.Single(document.Ipams).Subnets);
        Assert.Equal("172.27.96.0/20", subnet.Prefix);

        HcsCtlNetworkRoute route = Assert.Single(subnet.Routes);
        Assert.Equal("172.27.96.1", route.NextHop);
        Assert.Equal("0.0.0.0/0", route.DestinationPrefix);
        Assert.Equal(0, route.Metric);
    }

    [Fact]
    public void An_inspection_without_routes_binds_to_empty_rather_than_null()
    {
        HcsCtlNetworkInspectDocument document = JsonSerializer.Deserialize(
            """{"ok":true,"id":"x","ipams":[{"subnets":[{"prefix":"172.30.10.0/24","routes":null}]}]}""",
            HcsCtlJsonContext.Default.HcsCtlNetworkInspectDocument)!;

        Assert.Empty(Assert.Single(Assert.Single(document.Ipams).Subnets).Routes);
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
