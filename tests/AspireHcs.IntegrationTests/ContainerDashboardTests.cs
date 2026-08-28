using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hosting;
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
    // accrues continuously. Also the live regression for AspireHcs#74: the workload is parked
    // behind the test barrier before its first exec attempt, so the pause is issued inside the
    // pre-create window deterministically — the pause gate must hold it until the workload is
    // visible, never tearing the workload create down into Exited/Failed.
    [SkippableFact]
    public async Task A_paused_container_stops_making_progress_and_resumes()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // The dispatch barrier (HcsContainerInstance.WaitForTestWorkloadBarrierAsync): the
        // workload parks before its first exec until the marker file exists. The AppHost runs
        // in-process via DistributedApplicationTestingBuilder, so the test's environment is the
        // AppHost's — the same mechanism ASPIREHCS_TEST_COMMAND uses.
        string barrier = Path.Combine(Path.GetTempPath(), $"aspirehcs-barrier-{Guid.NewGuid():N}.marker");
        Environment.SetEnvironmentVariable("ASPIREHCS_TEST_WORKLOAD_BARRIER", barrier);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

            await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
            {
                await app.StartAsync(cts.Token);
                await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

                // The barrier holds the workload before its first create, so the resource is
                // Running while the guest does not have the workload yet: the #74 pre-create
                // window, entered deterministically. Prove it before pausing.
                string image = HcsContainerInstance.WorkloadImageName("cmd /c ping -t 127.0.0.1")!;
                string ps = await ContainerFixture.RunHcsCtlJsonAsync(
                    hcsctl, cts.Token, "container", "ps",
                    "--id", ContainerFixture.ContainerIdOf(app), "--store", store);
                using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(ps))
                {
                    bool workloadVisible = document.RootElement.TryGetProperty("processes", out System.Text.Json.JsonElement processes)
                        && processes.EnumerateArray().Any(p =>
                            p.TryGetProperty("ImageName", out System.Text.Json.JsonElement name)
                            && string.Equals(name.GetString(), image, StringComparison.OrdinalIgnoreCase));
                    Assert.False(workloadVisible, $"the workload ({image}) must not exist yet: the barrier did not hold it");
                }

                // AspireHcs#74 regression: pause while the workload is still absent. The gate
                // holds the pause until the workload's process is visible, so the state must
                // move straight Running -> Paused. Watch the state stream from before the
                // command to prove no Exited/Failed appears while the pause is in flight.
                ResourceCommandService commands = app.Services.GetRequiredService<ResourceCommandService>();
                ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
                List<string> statesWhilePausing = [];
                Task<ResourceEvent> pausedEvent = notifications.WaitForResourceAsync(
                    "worker",
                    @event =>
                    {
                        statesWhilePausing.Add(@event.Snapshot.State?.Text ?? "<none>");
                        return @event.Snapshot.State?.Text == "Paused";
                    },
                    cts.Token);

                // Start the pause (it blocks until the gate releases it), then release the
                // workload: the gate's polling sees the guest process appear and pauses.
                Task<ExecuteCommandResult> pauseCommand = commands.ExecuteCommandAsync("worker", "container-pause", cts.Token);

                // Deterministic window: wait until PauseAsync has entered the gate (it writes
                // the signal file just before polling ps), then release the barrier. The
                // pause can only complete after the workload appears — so this sequence
                // fails if the gate is removed from PauseAsync.
                string gateSignal = barrier + ".pause-gate";
                DateTimeOffset gateDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                while (!File.Exists(gateSignal))
                {
                    if (DateTimeOffset.UtcNow > gateDeadline)
                    {
                        throw new TimeoutException($"pause gate signal {gateSignal} never appeared");
                    }

                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }


                using (File.Create(barrier))
                {
                }

                ExecuteCommandResult paused = await pauseCommand;
                Assert.True(paused.Success, paused.Message);

                ResourceEvent reached = await pausedEvent;
                output.WriteLine($"states while pausing: {string.Join(" -> ", statesWhilePausing)}");
                Assert.Equal("Paused", reached.Snapshot.State?.Text);
                Assert.DoesNotContain(statesWhilePausing, state =>
                    state == KnownResourceStates.Exited
                    || state == KnownResourceStates.FailedToStart
                    || state == "Failed");

                // Two readings across a window in which a running container would certainly
                // consume CPU. A paused one cannot: its vCPUs are not scheduled.
                long first = await CpuTicksAsync(hcsctl, store, app, cts.Token);
                await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
                long second = await CpuTicksAsync(hcsctl, store, app, cts.Token);
                output.WriteLine($"cpu ticks while paused: {first} -> {second}");
                Assert.Equal(first, second);

                // The #74 failure publishes Exited over Paused milliseconds after the pause
                // completes; the sampling window is far past that, so the settled state must
                // still be Paused.
                Assert.True(notifications.TryGetCurrentState("worker", out ResourceEvent? settled), "No snapshot for 'worker'.");
                output.WriteLine($"settled state while paused: {settled!.Snapshot.State?.Text}");
                Assert.Equal("Paused", settled.Snapshot.State?.Text);

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
        finally
        {
            Environment.SetEnvironmentVariable("ASPIREHCS_TEST_WORKLOAD_BARRIER", null);
            try
            {
                File.Delete(barrier);
            }
            catch (IOException)
            {
                // A leftover marker file does not fail the test.
            }
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
        // Contract 3: "statistics" is the raw v2 property reply; the counters sit one level
        // down, under "Statistics". A paused container still reports Processor, so no
        // absent-key tolerance is needed here.
        if (!document.RootElement.TryGetProperty("statistics", out System.Text.Json.JsonElement statistics)
            || !statistics.TryGetProperty("Statistics", out System.Text.Json.JsonElement inner)
            || !inner.TryGetProperty("Processor", out System.Text.Json.JsonElement processor)
            || !processor.TryGetProperty("TotalRuntime100ns", out System.Text.Json.JsonElement totalRuntime))
        {
            throw new InvalidOperationException($"unexpected container stats document: {json}");
        }

        return totalRuntime.GetInt64();
    }
}
