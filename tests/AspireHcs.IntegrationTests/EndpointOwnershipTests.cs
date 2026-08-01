using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using AspireHcs.Hcn;
using AspireHcs.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #12: scavenging attributes endpoints to runs via a pid-scoped Owner instead of the racy
// "no VM attached yet" heuristic. These need HNS (the Default Switch) but no guest image; they
// still gate on HCS_TEST_VHDX because it is the suite's one "this machine can run HCS" signal —
// the hosted CI runner has no HNS, and a capability probe that swallowed exceptions could skip
// vacuously on the machines where these must run.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class EndpointOwnershipTests(ITestOutputHelper output)
{
    private static string? BaseVhdx => Environment.GetEnvironmentVariable("HCS_TEST_VHDX");

    [SkippableFact]
    public void Owner_round_trips_through_creation_query_and_filtered_enumeration()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        // The whole #12 design rests on two HNS facts this pins: QueryEndpointProperties
        // returns the Owner exactly as written (including the ":pid" suffix), and
        // HcnEnumerateEndpoints exact-match filtering works on such an owner.
        Guid networkId = HcnClient.FindIcsNetworkId();
        Guid endpointId = Guid.NewGuid();
        HcnClient.CreateDhcpEndpoint(networkId, endpointId, RandomMac(), HcsVmOrchestrator.RunHcnOwner);
        try
        {
            string? properties = HcnClient.QueryEndpointProperties(endpointId);
            Assert.NotNull(properties);
            string? owner = JsonNode.Parse(properties)?["Owner"]?.GetValue<string>();
            Assert.Equal(HcsVmOrchestrator.RunHcnOwner, owner);

            Assert.Contains(endpointId, HcnClient.EnumerateEndpointIds(HcsVmOrchestrator.RunHcnOwner));
        }
        finally
        {
            HcnClient.DeleteEndpoint(endpointId);
        }
    }

    [SkippableFact]
    public async Task Scavenger_deletes_dead_run_endpoints_and_spares_live_ones()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        Guid networkId = HcnClient.FindIcsNetworkId();

        // A pid that is provably dead: spawn a process and wait for it to exit. Windows does
        // not reuse a pid this quickly in practice; if it ever did, the scavenger would keep
        // the endpoint and the DoesNotContain below would catch it.
        int deadPid;
        using (Process reaped = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!)
        {
            await reaped.WaitForExitAsync();
            deadPid = reaped.Id;
        }
        output.WriteLine($"dead pid {deadPid}, live pid {Environment.ProcessId}");

        Guid deadRunEndpoint = Guid.NewGuid();
        Guid liveRunEndpoint = Guid.NewGuid();
        Guid legacyEndpoint = Guid.NewGuid();
        HcnClient.CreateDhcpEndpoint(networkId, deadRunEndpoint, RandomMac(), $"AspireHcs:{deadPid}");
        HcnClient.CreateDhcpEndpoint(networkId, liveRunEndpoint, RandomMac(), HcsVmOrchestrator.RunHcnOwner);
        HcnClient.CreateDhcpEndpoint(networkId, legacyEndpoint, RandomMac(), "AspireHcs");
        try
        {
            // ownEndpointId is deliberately none of the three: the live endpoint must survive
            // on the strength of its owner's pid alone, not the own-id skip.
            await HcsVmOrchestrator.ScavengeStaleEndpointsAsync(Guid.NewGuid(), NullLogger.Instance);

            List<Guid> remaining = HcnClient.EnumerateEndpointIds();
            Assert.DoesNotContain(deadRunEndpoint, remaining);
            Assert.DoesNotContain(legacyEndpoint, remaining);
            Assert.Contains(liveRunEndpoint, remaining);
        }
        finally
        {
            Guid[] created = [deadRunEndpoint, liveRunEndpoint, legacyEndpoint];
            foreach (Guid endpointId in created)
            {
                try
                {
                    HcnClient.DeleteEndpoint(endpointId);
                }
                catch (Exception)
                {
                    // Already scavenged — the expected outcome for two of the three.
                }
            }
        }
    }

    private static string RandomMac() =>
        $"02-15-5D-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}";
}
