using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Dashboard commands and statistics. The pause test requires that a paused container's
// workload stops making progress, not only that the command reports success.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ContainerDashboardTests(ITestOutputHelper output)
{
    private static async Task<CustomResourceSnapshot> SnapshotAsync(
        DistributedApplication app, CancellationToken cancellationToken)
    {
        ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await foreach (ResourceEvent @event in notifications.WatchAsync(cancellationToken))
        {
            if (@event.Resource.Name == "worker")
            {
                return @event.Snapshot;
            }
        }

        throw new InvalidOperationException("No snapshot for 'worker'.");
    }

    [SkippableFact]
    public async Task Live_statistics_reach_the_resource_snapshot()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

        // The poller runs on an interval, so the first snapshot after Running may predate it.
        IReadOnlyList<ResourcePropertySnapshot> properties = [];
        for (int attempt = 0; attempt < 20 && !properties.Any(p => p.Name == "hcs.container.uptime"); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            properties = (await SnapshotAsync(app, cts.Token)).Properties;
        }

        foreach (ResourcePropertySnapshot property in properties.Where(p => p.Name.StartsWith("hcs.", StringComparison.Ordinal)))
        {
            output.WriteLine($"{property.Name} = {property.Value}");
        }

        Assert.Contains(properties, p => p.Name == "hcs.container.uptime");
        Assert.Contains(properties, p => p.Name == "hcs.memory.commit");
        Assert.Contains(properties, p => p.Name == "hcs.cpu.total");

        // Formatted with a unit, not a raw byte count.
        string commit = (string)properties.Single(p => p.Name == "hcs.memory.commit").Value!;
        Assert.Matches(@"^[\d.]+ (B|KB|MB|GB|TB)$", commit);

        await app.StopAsync(cts.Token);
    }

    // Pause must stop the workload. Measured by watching CPU time, which a running `ping -t`
    // accrues continuously.
    [SkippableFact]
    public async Task A_paused_container_stops_making_progress_and_resumes()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

            ResourceCommandService commands = app.Services.GetRequiredService<ResourceCommandService>();

            ExecuteCommandResult paused = await commands.ExecuteCommandAsync("worker", "container-pause", cts.Token);
            Assert.True(paused.Success, paused.Message);
            await app.ResourceNotifications.WaitForResourceAsync("worker", "Paused", cts.Token);

            // Two readings across a window in which a running container would certainly consume
            // CPU. A paused one cannot: its vCPUs are not scheduled.
            long first = await CpuTicksAsync(hcsctl, store, app, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
            long second = await CpuTicksAsync(hcsctl, store, app, cts.Token);
            output.WriteLine($"cpu ticks while paused: {first} -> {second}");
            Assert.Equal(first, second);

            ExecuteCommandResult resumed = await commands.ExecuteCommandAsync("worker", "container-resume", cts.Token);
            Assert.True(resumed.Success, resumed.Message);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

            // Running again, by the same measure.
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
            long afterResume = await CpuTicksAsync(hcsctl, store, app, cts.Token);
            output.WriteLine($"cpu ticks after resume: {afterResume}");
            Assert.True(afterResume > second, $"CPU time did not advance after resume ({second} -> {afterResume}).");

            await app.StopAsync(cts.Token);
        }
    }

    [SkippableFact]
    public async Task The_process_list_command_writes_the_guests_processes_to_the_logs()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

        ResourceCommandService commands = app.Services.GetRequiredService<ResourceCommandService>();
        ExecuteCommandResult result = await commands.ExecuteCommandAsync("worker", "container-ps", cts.Token);

        output.WriteLine(result.Message);
        Assert.True(result.Success, result.Message);

        // A real guest runs several processes; "0 process(es)" means the call succeeded and
        // reported nothing.
        Assert.Matches(@"^\d+ process\(es\) written", result.Message!);
        Assert.DoesNotContain("0 process(es)", result.Message);

        await app.StopAsync(cts.Token);
    }

    /// <summary>
    /// Total guest CPU time, read through hcsctl directly. The snapshot poller only refreshes
    /// every few seconds; this needs a reading on demand.
    /// </summary>
    private static async Task<long> CpuTicksAsync(
        string hcsctl, string store, DistributedApplication app, CancellationToken cancellationToken)
    {
        string id = ContainerFixture.ContainerIdOf(app);
        string json = await ContainerFixture.RunHcsCtlJsonAsync(
            hcsctl, cancellationToken, "container", "stats", "--id", id, "--store", store);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("statistics")
            .GetProperty("Processor")
            .GetProperty("TotalRuntime100ns")
            .GetInt64();
    }
}
