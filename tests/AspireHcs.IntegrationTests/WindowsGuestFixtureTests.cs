using System.Net.Sockets;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #11: the Windows Server 2025 image built by tools/guest-images/windows is the suite's
// POSITIVE fixture — a guest that actually serves something. Until it existed, "healthy" was
// only ever proven against an in-process TcpListener; the Kali image can only prove the
// negative half (refusal withholds readiness). These tests are the other half, live:
// the health check goes Healthy against a real guest listener, readiness fires because of it,
// and the EMS serial console streams through the product pump.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsGuestFixtureTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Windows_guest_serves_ssh_and_health_check_goes_healthy()
    {
        string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_TEST_WINDOWS_VHDX");
        Skip.If(string.IsNullOrEmpty(windowsVhdx),
            "Set HCS_TEST_WINDOWS_VHDX to the sealed Windows guest image (tools/guest-images/windows) to run the positive-fixture tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // The sample AppHost reads HCS_TEST_VHDX; point it at the Windows image for this test
        // only. The suite is serialized (AssemblyInfo), so the override cannot interleave with
        // another test's read; restored in finally either way.
        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", windowsVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
            appHost.CreateResourceBuilder(vm).WithTcpHealthCheck();

            TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            appHost.Eventing.Subscribe<ResourceReadyEvent>((@event, _) =>
            {
                if (@event.Resource.Name == "appliance")
                {
                    ready.TrySetResult();
                }
                return Task.CompletedTask;
            });

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);

            // Collect resource logs from the start so the serial-console assertion below sees
            // the whole boot, not just what happens to arrive after Running.
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

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);

            // What this pins: the check goes Healthy against a real guest listener AND ready
            // fires. What it deliberately does NOT pin is the release-BY-health ordering: the
            // snapshot stream and the eventing stream are separate async channels, and reading
            // the snapshot at the instant ready fired was tried and observed racing (the report
            // entry present, status not yet recorded). The ordering direction is held by the
            // NEGATIVE fixture instead — HealthCheckGatesReadinessTests proves ready is
            // withheld while the same check is Unhealthy — so the pair covers causality.
            ResourceEvent healthy = await app.ResourceNotifications.WaitForResourceAsync(
                "appliance",
                e => e.Snapshot.HealthReports.Any(h => h.Name == "appliance_ssh_tcp_check" && h.Status == HealthStatus.Healthy),
                cts.Token);
            HealthReportSnapshot report = healthy.Snapshot.HealthReports.Single(h => h.Name == "appliance_ssh_tcp_check");
            output.WriteLine($"health report: {report.Status} — {report.Description}");

            await ready.Task.WaitAsync(cts.Token);

            // Hard accept — the refused branch the round-trip test tolerates is a FAILURE here.
            Uri endpoint = app.GetEndpoint("appliance", "ssh");
            using TcpClient client = new();
            await client.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            output.WriteLine($"TCP {endpoint.Host}:{endpoint.Port} -> connected");

            // EMS claim, witnessed through the product pump: the SAC banner lands in the
            // resource logs ("Computer is booting, SAC started and initialized." on the spike's
            // raw capture of this image).
            bool sawSerial;
            lock (logLines)
            {
                sawSerial = logLines.Any(l => l.Contains("SAC", StringComparison.Ordinal));
                output.WriteLine($"log lines observed: {logLines.Count}; serial (SAC) seen: {sawSerial}");
            }
            Assert.True(sawSerial, "no SAC/EMS serial output appeared in the resource logs — the EMS channel is not reaching the pump.");

            await app.StopAsync(cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", originalVhdx);
        }
    }
}
