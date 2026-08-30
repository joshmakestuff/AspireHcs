using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// A guest that boots healthy but never DHCPs (the StaticNoDhcp variant) must fail with the
// cause named: not hang, not report Running, not point anywhere but DHCP.
// WithGuestAddress() is the supported escape hatch for such a guest (the agentless path skips
// the lease wait entirely); this test deliberately does not use it — the agent path's failure
// mode is the subject.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class NoLeaseFailureModeTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_guest_that_never_leases_ends_FailedToStart_with_the_dhcp_cause_named()
    {
        string? noLeaseVhdx = Environment.GetEnvironmentVariable("HCS_TEST_NOLEASE_VHDX");
        Skip.If(string.IsNullOrEmpty(noLeaseVhdx),
            "Set HCS_TEST_NOLEASE_VHDX to a guest image whose NIC has a static address and no DHCP client to run the never-leases pin. Takes ~2 minutes by design (the 90 s lease timeout is the subject).");

        // Boot (~20 s) + the 90 s lease timeout under test + teardown.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", noLeaseVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);

            await ContainerFixture.ObserveResourceLogsAsync(app, "appliance", output, cts.Token, async logs =>
            {
                await app.StartAsync(cts.Token);

                // Terminal FailedToStart, not a hang and not Running.
                await app.ResourceNotifications.WaitForResourceAsync(
                    "appliance", KnownResourceStates.FailedToStart, cts.Token);
                output.WriteLine("resource reached FailedToStart");

                // The cause is named where the user looks. Asserted on hcsctl's lease-timeout
                // wording ("the guest has not taken a DHCP lease", hcsctl internal/vm/vm.go),
                // not on exact text. The line is awaited rather than read instantly: the log
                // stream can trail the state stream.
                bool causeNamed = await logs.WaitForLineAsync(
                    l => l.Contains("has not taken a DHCP lease", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(15), cts.Token);
                output.WriteLine($"log lines observed: {logs.Count}; DHCP cause named: {causeNamed}");
                Assert.True(causeNamed, "FailedToStart was reached but no log line names the DHCP lease timeout as the cause.");

                await app.StopAsync(cts.Token);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", originalVhdx);
        }
    }
}
