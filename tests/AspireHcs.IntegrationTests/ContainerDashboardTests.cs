using System.Diagnostics;
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

        // The same message on all three: commit/cpu are the likeliest to be missing on a racing
        // snapshot (the retry above polls only until uptime appears), and the hcs.*-filtered
        // dump above prints nothing when no hcs property arrived at all.
        static void AssertHasProperty(IReadOnlyList<ResourcePropertySnapshot> properties, string name)
            => Assert.True(
                properties.Any(p => p.Name == name),
                $"Snapshot did not contain '{name}'. Observed properties: {string.Join(", ", properties.Select(p => p.Name))}");

        AssertHasProperty(properties, "hcs.container.uptime");
        AssertHasProperty(properties, "hcs.memory.commit");
        AssertHasProperty(properties, "hcs.cpu.total");

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
        using HcsCtlProxy proxy = await HcsCtlProxy.CompileAsync(hcsctl, cts.Token);

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        // Point the sample's container at the proxy, which forwards everything except the
        // workload exec: it holds that until the test releases it, so the real HcsCreateProcess
        // has not occurred when pause is requested. The pre-pause gate is then the only way
        // pause can proceed.
        HcsContainerResource workerResource = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(workerResource).WithHcsCtl(proxy.Path);

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

            // Running is the resource's state, not the workload's. The started marker proves the
            // product has dispatched the workload exec but the real HcsCreateProcess has not run.
            await proxy.WaitForStartedAsync(cts.Token);

            ResourceCommandService commands = app.Services.GetRequiredService<ResourceCommandService>();

            // Pause must wait on the exec started record, which the held exec has not emitted,
            // not report success while the guest is frozen mid-create.
            Task<ExecuteCommandResult> pauseTask = commands.ExecuteCommandAsync("worker", "container-pause", cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            Assert.False(pauseTask.IsCompleted, "container-pause reported success before the workload was created.");

            proxy.Release();
            ExecuteCommandResult paused = await pauseTask;
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

            // Running again, by the same measure. The proxy now forwards exec, so the released
            // workload created its guest process: wait for it, then require CPU to advance.
            await ContainerFixture.WaitForGuestProcessAsync(hcsctl, store, app, "ping.exe", cts.Token);
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
        // Contract 3: "statistics" is the raw v2 property reply; the counters sit one level
        // down, under "Statistics". A paused container still reports Processor, so no
        // absent-key tolerance is needed here.
        return document.RootElement
            .GetProperty("statistics")
            .GetProperty("Statistics")
            .GetProperty("Processor")
            .GetProperty("TotalRuntime100ns")
            .GetInt64();
    }

    /// <summary>
    /// Stands in for hcsctl and forwards every invocation except <c>container exec</c>, which it
    /// holds until <see cref="Release"/> is called. This lets a test prove the product dispatched
    /// the workload before its real <c>HcsCreateProcess</c> occurred.
    /// </summary>
    /// <remarks>
    /// Compiled from a single C# file. A batch shim cannot do this job: cmd.exe re-parses every
    /// forwarded argument, and its only wait primitive (<c>timeout</c>) writes bare text to
    /// stderr when stdin is not a console — which the exec's <c>--stream-json</c> stderr
    /// contract rejects. The shim must stay silent on both streams while blocked and forward
    /// argv verbatim, so it is a compiled process, not a script.
    /// </remarks>
    private sealed class HcsCtlProxy : IDisposable
    {
        private readonly string _directory;

        private HcsCtlProxy(string directory)
        {
            _directory = directory;
            Path = System.IO.Path.Combine(directory, "out", "hcsctl-proxy.exe");
            StartedMarker = System.IO.Path.Combine(directory, "started");
            ReleaseMarker = System.IO.Path.Combine(directory, "release");
        }

        public string Path { get; }

        public string StartedMarker { get; }

        public string ReleaseMarker { get; }

        public static async Task<HcsCtlProxy> CompileAsync(string realHcsCtl, CancellationToken cancellationToken)
        {
            HcsCtlProxy proxy = new(Directory.CreateTempSubdirectory("aspirehcs-proxy").FullName);

            string source = System.IO.Path.Combine(proxy._directory, "hcsctl-proxy.cs");
            await File.WriteAllTextAsync(source, $$"""
                #:property PublishAot=false
                using System.Diagnostics;

                const string RealHcsCtl = @"{{realHcsCtl}}";
                const string StartedMarker = @"{{proxy.StartedMarker}}";
                const string ReleaseMarker = @"{{proxy.ReleaseMarker}}";

                if (args is [var group, var verb, ..]
                    && string.Equals(group, "container", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(verb, "exec", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(StartedMarker, "started");
                    while (!File.Exists(ReleaseMarker))
                    {
                        // A vanished started marker is Dispose tearing the test down: die
                        // without running the exec, so no real workload starts mid-teardown.
                        if (!File.Exists(StartedMarker))
                        {
                            return 1;
                        }

                        Thread.Sleep(TimeSpan.FromMilliseconds(100));
                    }
                }

                // No redirection: the child inherits this process's stdout/stderr pipes, so
                // hcsctl's streams reach the product untouched.
                ProcessStartInfo startInfo = new(RealHcsCtl) { UseShellExecute = false };
                foreach (string argument in args)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process child = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start '{RealHcsCtl}'.");
                child.WaitForExit();
                return child.ExitCode;
                """, cancellationToken);

            ProcessStartInfo publish = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            publish.ArgumentList.Add("publish");
            publish.ArgumentList.Add(source);
            publish.ArgumentList.Add("-o");
            publish.ArgumentList.Add(System.IO.Path.Combine(proxy._directory, "out"));

            using Process process = Process.Start(publish)
                ?? throw new InvalidOperationException("Failed to start 'dotnet publish'.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Publishing the hcsctl proxy failed with exit code {process.ExitCode}:" +
                    $"{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
            }

            return proxy;
        }

        public async Task WaitForStartedAsync(CancellationToken cancellationToken)
        {
            while (!File.Exists(StartedMarker))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        public void Release() => File.WriteAllText(ReleaseMarker, "release");

        public void Dispose()
        {
            // Deleting the started marker makes a still-blocked shim exit within one poll,
            // without running the exec it was holding — no orphan process, no workload started
            // against a container mid-teardown.
            File.Delete(StartedMarker);

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a still-running shim's exe is locked until its forward completes.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort: same as above.
            }
        }
    }
}
