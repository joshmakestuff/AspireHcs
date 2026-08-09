using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #5 acceptance: a TCP health check moves readiness from "the guest kernel is up" to
// "the guest is serving", so WaitFor(vm) releases dependents against a workload.
//
// The negative fixture is a port nothing serves, declared on the resource for this test only.
// It cannot be the guest's own SSH port: the reference image serves it, so the check would pass
// and the test would wait forever for an unhealthy report. The guest boots, leases an address
// and answers this port with an RST, so the check reaches it and still correctly refuses to call
// the resource ready. That is precisely the window that used to be reported as ready.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class HealthCheckGatesReadinessTests(ITestOutputHelper output)
{
    /// <summary>
    /// A port in the IANA dynamic range that the guest image does not serve. Any closed port
    /// works; what matters is that the guest is reachable and answers with an RST, so a failure
    /// here means "nothing is listening" rather than "the address is wrong".
    /// </summary>
    private const int DeadPort = 59999;

    [SkippableFact]
    public async Task Ready_is_withheld_while_nothing_is_listening_in_the_guest()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // The sample AppHost deliberately does not opt in, so the round-trip test still measures
        // the default behaviour; the check is attached to its resource here instead.
        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
        appHost.CreateResourceBuilder(vm)
            .WithEndpoint("dead", targetPort: DeadPort)
            .WithTcpHealthCheck("dead");

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
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);

        // The check only runs once the resource reports Running, and it only reaches the guest
        // once the endpoint is allocated — so an unhealthy report here also proves the
        // orchestrator ordered those two things correctly.
        ResourceEvent unhealthy = await app.ResourceNotifications.WaitForResourceAsync(
            "appliance",
            e => e.Snapshot.HealthReports.Any(h => h.Name == "appliance_dead_tcp_check" && h.Status == HealthStatus.Unhealthy),
            cts.Token);

        HealthReportSnapshot report = unhealthy.Snapshot.HealthReports.Single(h => h.Name == "appliance_dead_tcp_check");
        output.WriteLine($"health report: {report.Status} — {report.Description}");
        Assert.Contains("not accepting connections", report.Description);

        // The gap this closes: without the annotation Aspire declares a resource ready the moment
        // it reports Running, regardless of whether anything inside it is serving.
        Assert.False(ready.Task.IsCompleted, "ResourceReadyEvent fired even though the guest is serving nothing.");

        await app.StopAsync(cts.Token);
    }
}
