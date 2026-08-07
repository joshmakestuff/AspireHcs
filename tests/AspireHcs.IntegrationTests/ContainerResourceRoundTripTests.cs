using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #39 acceptance: a Hyper-V-isolated Windows container runs as an Aspire resource,
// UNELEVATED, from an hcsctl store, and teardown leaves nothing behind.
//
// Teardown is asserted by ABSENCE, never by a return code (#48): DestroyLayer can report success
// and leave the tree, so "rm returned 0" is not evidence. These shell out to hcsctl to check,
// deliberately using a different path than the product code does.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ContainerResourceRoundTripTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_container_resource_reaches_running_and_leaves_nothing_behind()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // Long-running on purpose: the resource must stay Running rather than reaching Finished
        // the instant a one-shot command exits.
        IDistributedApplicationTestingBuilder appHost = await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        string containerId;
        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            containerId = ContainerFixture.ContainerIdOf(app);
            output.WriteLine($"container id: {containerId}");

            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

            // It really is running, according to something other than our own state machine.
            Assert.Contains(containerId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));

            await app.StopAsync(cts.Token);
        }

        // ABSENCE, not a return code. A container still listed here — in any state, including
        // "created" — means its scratch layer probably survived too.
        string[] remaining = await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token);
        output.WriteLine($"after teardown: {(remaining.Length == 0 ? "(none)" : string.Join(", ", remaining))}");
        Assert.DoesNotContain(containerId, remaining);
    }

    // #39 by name: "cmd /c ver from inside reports the image's own build". Asserted from the
    // resource's own logs, so it also proves the guest's output reaches the dashboard at all.
    //
    // The build asserted here is the IMAGE's, and this host is build 26200 — a guest reporting
    // 26100 could not be the host answering. That mismatch is what makes the isolation real
    // rather than nominal.
    [SkippableFact]
    public async Task The_guest_reports_the_images_own_build_in_the_resource_logs()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost = await ContainerFixture.SampleAppHostAsync("cmd /c ver", cts.Token);

        string containerId;
        string versionLine;
        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            containerId = ContainerFixture.ContainerIdOf(app);
            await app.StartAsync(cts.Token);

            // Watch the logs concurrently with the run: a one-shot workload can finish before a
            // watcher started afterwards sees anything.
            Task<string> version = FirstLogMatchingAsync(app, "worker", "Microsoft Windows [Version", cts.Token);

            // A one-shot workload: the resource reaches a terminal state on its own, driven by
            // the guest process's exit — not by anything the test does.
            await app.ResourceNotifications.WaitForResourceAsync(
                "worker", [KnownResourceStates.Finished, KnownResourceStates.Exited], cts.Token);

            versionLine = await version.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            await app.StopAsync(cts.Token);
        }

        output.WriteLine($"guest reported: {versionLine.Trim()}");
        Assert.Contains("Microsoft Windows [Version", versionLine);

        // Whatever the guest reported, it must not be this host's own build — that would mean
        // the command ran somewhere other than inside the image.
        Assert.DoesNotContain(Environment.OSVersion.Version.Build.ToString(), versionLine);

        Assert.DoesNotContain(containerId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));
    }

    /// <summary>
    /// Reads the resource's dashboard log stream until a line contains <paramref name="needle"/>.
    /// This is the same stream a developer sees, so asserting on it checks the plumbing, not just
    /// the guest.
    /// </summary>
    private static async Task<string> FirstLogMatchingAsync(
        DistributedApplication app, string resourceName, string needle, CancellationToken cancellationToken)
    {
        ResourceLoggerService logs = app.Services.GetRequiredService<ResourceLoggerService>();

        await foreach (IReadOnlyList<LogLine> batch in logs.WatchAsync(resourceName).WithCancellation(cancellationToken))
        {
            foreach (LogLine line in batch)
            {
                if (line.Content.Contains(needle, StringComparison.Ordinal))
                {
                    return line.Content;
                }
            }
        }

        throw new InvalidOperationException($"The '{resourceName}' log stream ended without a line containing '{needle}'.");
    }

    // A crashed AppHost leaves its container behind; the next run must reclaim it. Simulated with
    // an id carrying a pid that is not running, which is exactly what the scavenger keys on.
    [SkippableFact]
    public async Task A_container_left_by_a_dead_run_is_scavenged_by_the_next_one()
    {
        (string hcsctl, string store, string image) = ContainerFixture.Require();

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // Create a container directly, under an id attributed to a pid that cannot be alive.
        string abandonedId = $"aspirehcs-{DeadProcessId()}-orphan-{Guid.NewGuid():N}";
        await RunHcsCtlAsync(hcsctl, cts.Token,
            "container", "create", "--id", abandonedId, "--ref", image, "--store", store);

        Assert.Contains(abandonedId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));

        IDistributedApplicationTestingBuilder appHost = await ContainerFixture.SampleAppHostAsync("cmd /c ver", cts.Token);

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync(
                "worker",
                [KnownResourceStates.Running, KnownResourceStates.Finished, KnownResourceStates.Exited],
                cts.Token);
            await app.StopAsync(cts.Token);
        }

        Assert.DoesNotContain(abandonedId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));
    }

    /// <summary>
    /// A pid that is not running. Chosen by probing rather than hard-coded: any constant could be
    /// a live process on some host, and that would make the test pass by not scavenging.
    /// </summary>
    private static int DeadProcessId()
    {
        HashSet<int> live = [.. Process.GetProcesses().Select(static p => p.Id)];
        for (int candidate = 999_000; candidate > 1000; candidate -= 4)
        {
            if (!live.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a process id that is not in use.");
    }

    private static async Task RunHcsCtlAsync(string hcsctl, CancellationToken cancellationToken, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(hcsctl) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"hcsctl {string.Join(' ', arguments)} exited {process.ExitCode}.");
        }
    }
}
