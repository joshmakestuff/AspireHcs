using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #11 phase 0's never-leases finding, pinned: a guest that boots healthy but never
// DHCPs (the StaticNoDhcp Kali variant from tools/guest-images/kali) must fail LOUDLY with
// the cause named — not hang, not report Running, not point anywhere but DHCP. This is the
// suite pin promised when the manual witness was recorded (2026-08-01).
[SupportedOSPlatform("windows10.0.17763")]
public sealed class NoLeaseFailureModeTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_guest_that_never_leases_ends_FailedToStart_with_the_dhcp_cause_named()
    {
        string? noLeaseVhdx = Environment.GetEnvironmentVariable("HCS_TEST_NOLEASE_VHDX");
        Skip.If(string.IsNullOrEmpty(noLeaseVhdx),
            "Set HCS_TEST_NOLEASE_VHDX to the StaticNoDhcp probe variant (tools/guest-images/kali) to run the never-leases pin. Takes ~2 minutes by design (the 90 s lease timeout is the subject).");

        // Boot (~20 s) + the 90 s lease timeout under test + teardown.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", noLeaseVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);

            List<string> logLines = [];
            ResourceLoggerService loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
            Task logWatch = Task.Run(async () =>
            {
                await foreach (IReadOnlyList<LogLine> batch in loggerService.WatchAsync("appliance").WithCancellation(cts.Token))
                {
                    lock (logLines)
                    {
                        logLines.AddRange(batch.Select(l => l.Content));
                    }
                }
            }, cts.Token);

            await app.StartAsync(cts.Token);

            // The whole point: terminal FailedToStart, not a hang and not a lying Running.
            await app.ResourceNotifications.WaitForResourceAsync(
                "appliance", KnownResourceStates.FailedToStart, cts.Token);
            output.WriteLine("resource reached FailedToStart");

            // And the cause is named where the user looks. The message is asserted loosely
            // (the lease-timeout wording), not on exact text.
            bool causeNamed;
            lock (logLines)
            {
                causeNamed = logLines.Any(l => l.Contains("did not obtain a DHCP lease", StringComparison.Ordinal));
                output.WriteLine($"log lines observed: {logLines.Count}; DHCP cause named: {causeNamed}");
            }
            Assert.True(causeNamed, "FailedToStart was reached but no log line names the DHCP lease timeout as the cause.");

            await app.StopAsync(cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", originalVhdx);
        }
    }
}
