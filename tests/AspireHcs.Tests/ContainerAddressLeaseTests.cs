using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// On an ICS network (the Default Switch, the container default) the endpoint's address is
// leased after the guest starts, not assigned at create, so the instance polls the live HCN
// listing. These pin the wait: what satisfies it, what it filters, and what its failure says.
// The fake reads stand in for `network endpoints --json`, whose live shape the binding tests
// at the bottom capture verbatim.
[SupportedOSPlatform("windows10.0.17763")]
public class ContainerAddressLeaseTests
{
    // A real endpoint id, verbatim.
    private const string EndpointId = "0626e1a4-c040-4af4-be2c-ba695dc7943b";

    private static HcsCtlNetworkEndpointsDocument Listing(params HcsCtlNetworkEndpointRow[] rows) =>
        new() { Ok = true, Endpoints = rows };

    private static HcsCtlNetworkEndpointRow Row(string id, params string[] addresses) =>
        new() { Id = id, Network = "Default Switch", Addresses = addresses };

    private static Task<string> WaitAsync(
        Func<CancellationToken, Task<HcsCtlNetworkEndpointsDocument>> readEndpoints,
        TimeSpan? timeout = null) =>
        HcsContainerInstance.WaitForLeasedAddressAsync(
            readEndpoints, EndpointId, "Default Switch", "hcsworker",
            timeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), CancellationToken.None);

    [Fact]
    public async Task An_address_present_on_the_first_read_returns_without_the_prefix()
    {
        string address = await WaitAsync(_ => Task.FromResult(Listing(Row(EndpointId, "172.18.184.17/20"))));

        Assert.Equal("172.18.184.17", address);
    }

    [Fact]
    public async Task The_wait_polls_until_the_lease_lands()
    {
        int reads = 0;
        string address = await WaitAsync(_ => Task.FromResult(
            ++reads < 3 ? Listing(Row(EndpointId)) : Listing(Row(EndpointId, "172.18.184.17/20"))));

        Assert.Equal("172.18.184.17", address);
        Assert.Equal(3, reads);
    }

    // GUID case differs between HCN read paths: stats report endpoint ids uppercase, the
    // endpoint listing lowercase.
    [Fact]
    public async Task The_endpoint_id_matches_case_insensitively()
    {
        string address = await WaitAsync(_ => Task.FromResult(
            Listing(Row(EndpointId.ToUpperInvariant(), "172.18.184.17/20"))));

        Assert.Equal("172.18.184.17", address);
    }

    // The Default Switch is shared: VMs and other containers lease on it too. Another
    // endpoint's address must not satisfy this container's wait.
    [Fact]
    public async Task Another_endpoints_address_does_not_satisfy_the_wait()
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(
                _ => Task.FromResult(Listing(
                    Row("583b86d9-9845-4cf3-bf3d-1f173fb2063d", "172.18.100.1/20"),
                    Row(EndpointId))),
                timeout: TimeSpan.FromMilliseconds(20)));

        Assert.Contains(EndpointId, thrown.Message);
    }

    // The failure names the network, the endpoint and how long it waited, and never says "Add
    // WithNetwork()": a resource that reaches this wait has a network.
    [Fact]
    public async Task Expiry_names_the_network_the_endpoint_and_the_wait()
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(_ => Task.FromResult(Listing(Row(EndpointId))), timeout: TimeSpan.FromMilliseconds(20)));

        Assert.Contains("Default Switch", thrown.Message);
        Assert.Contains(EndpointId, thrown.Message);
        Assert.Contains("still had no address", thrown.Message);
        Assert.Contains(" s.", thrown.Message);
        Assert.DoesNotContain("WithNetwork", thrown.Message);
    }

    // An endpoint missing from the listing is a different diagnosis than one present but
    // addressless: deleted out from under the container versus a lease that has not landed.
    [Fact]
    public async Task An_endpoint_never_listed_is_reported_as_never_listed()
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(_ => Task.FromResult(Listing()), timeout: TimeSpan.FromMilliseconds(20)));

        Assert.Contains("was never listed", thrown.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HcsContainerInstance.WaitForLeasedAddressAsync(
                _ => Task.FromResult(Listing(Row(EndpointId))), EndpointId, "Default Switch", "hcsworker",
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), cts.Token));
    }

    // Captured verbatim from `hcsctl network endpoints --json` (v0.2.0): a container endpoint on
    // the Default Switch with its leased address, and WSL's own endpoint beside it. The wire
    // names are hcsctl's (lowercase), unlike the stats documents, whose names are hcsshim's.
    private const string LiveEndpoints = """
        {
          "command": "network endpoints",
          "endpoints": [
            {
              "id": "20b4eeab-161e-4645-9d39-0262573776d9",
              "name": "f259008d-9e2e-496e-955c-e14642094248-ep",
              "networkId": "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444",
              "network": "Default Switch",
              "addresses": [
                "172.18.188.173/20"
              ],
              "mac": "02-15-5D-8F-78-69"
            },
            {
              "id": "583b86d9-9845-4cf3-bf3d-1f173fb2063d",
              "name": "Ethernet",
              "networkId": "790e58b4-7939-4434-9358-89ae7ddbe87e",
              "network": "WSL (Hyper-V firewall)",
              "addresses": [
                "172.30.145.141/20"
              ],
              "mac": "00-15-5D-DF-EA-70"
            }
          ],
          "ok": true
        }
        """;

    [Fact]
    public void A_live_endpoints_document_binds_every_field_we_read()
    {
        HcsCtlNetworkEndpointsDocument document = JsonSerializer.Deserialize(
            LiveEndpoints, HcsCtlJsonContext.Default.HcsCtlNetworkEndpointsDocument)!;

        Assert.True(document.Ok);
        Assert.Equal(2, document.Endpoints.Count);

        HcsCtlNetworkEndpointRow endpoint = document.Endpoints
            .Single(e => e.Id == "20b4eeab-161e-4645-9d39-0262573776d9");
        Assert.Equal("Default Switch", endpoint.Network);
        Assert.Equal("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", endpoint.NetworkId);
        Assert.Equal(["172.18.188.173/20"], endpoint.Addresses);
        Assert.Equal("02-15-5D-8F-78-69", endpoint.MacAddress);
    }

    // Go marshals a nil slice as JSON null, so `"addresses": null` and `"endpoints": null` are
    // routine output for "none".
    [Fact]
    public void Null_collections_bind_to_empty_rather_than_null()
    {
        HcsCtlNetworkEndpointsDocument noRows = JsonSerializer.Deserialize(
            """{"ok":true,"command":"network endpoints","endpoints":null}""",
            HcsCtlJsonContext.Default.HcsCtlNetworkEndpointsDocument)!;
        Assert.Empty(noRows.Endpoints);

        HcsCtlNetworkEndpointsDocument noAddresses = JsonSerializer.Deserialize(
            """{"ok":true,"endpoints":[{"id":"x","addresses":null}]}""",
            HcsCtlJsonContext.Default.HcsCtlNetworkEndpointsDocument)!;
        Assert.Empty(Assert.Single(noAddresses.Endpoints).Addresses);
    }
}
