using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AspireHcs.IntegrationTests;

/// <summary>
/// The container reported FailedToStart. Carries the resource's logs, which hold hcsctl's own
/// error and the reason the boot failed.
/// </summary>
internal sealed class ContainerStartFailedException(string logs)
    : Exception($"The container resource failed to start. Resource logs:{Environment.NewLine}{logs}")
{
    public string Logs { get; } = logs;
}

/// <summary>
/// Thread-safe view over the log lines collected so far by
/// <see cref="ContainerFixture.ObserveResourceLogsAsync"/>.
/// </summary>
internal sealed class ResourceLogBuffer
{
    private readonly List<string> _lines = [];

    internal void Add(IReadOnlyList<LogLine> batch)
    {
        lock (_lines)
        {
            _lines.AddRange(batch.Select(l => l.Content));
        }
    }

    public int Count
    {
        get
        {
            lock (_lines)
            {
                return _lines.Count;
            }
        }
    }

    public bool Any(Func<string, bool> predicate)
    {
        lock (_lines)
        {
            return _lines.Any(predicate);
        }
    }

    /// <summary>
    /// Polls for a matching line. The resource state stream and the log stream are separate
    /// async channels: a line the product wrote can arrive here after the state change that
    /// prompted the assertion, so an instant read can miss it.
    /// </summary>
    public async Task<bool> WaitForLineAsync(
        Func<string, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!Any(predicate))
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return true;
    }
}

/// <summary>
/// Shared plumbing for the live container tests: locating the fixture, building the sample
/// AppHost, running a workload to completion, and asking hcsctl what actually exists.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class ContainerFixture
{
    public const string ImageVariable = "ASPIREHCS_TEST_IMAGE";
    public const string StoreVariable = "ASPIREHCS_TEST_STORE";
    public const string HcsCtlVariable = "ASPIREHCS_HCSCTL";

    /// <summary>
    /// Whether this process holds an enabled <c>BUILTIN\Administrators</c> SID. Sizing the
    /// scratch needs it; running a container does not.
    /// </summary>
    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public static (string HcsCtl, string Store, string Image) Require()
    {
        string? hcsctl = Environment.GetEnvironmentVariable(HcsCtlVariable);
        string? store = Environment.GetEnvironmentVariable(StoreVariable);
        string? image = Environment.GetEnvironmentVariable(ImageVariable);

        Skip.If(string.IsNullOrWhiteSpace(hcsctl) || !File.Exists(hcsctl),
            $"Set {HcsCtlVariable} to hcsctl.exe (./eng/Get-HcsCtl.ps1 installs it) to run container integration tests.");
        Skip.If(string.IsNullOrWhiteSpace(store),
            $"Set {StoreVariable} to an hcsctl store holding an imported image.");
        Skip.If(string.IsNullOrWhiteSpace(image),
            $"Set {ImageVariable} to an image reference materialized in that store.");

        return (hcsctl!, store!, image!);
    }

    /// <summary>
    /// Builds the sample AppHost, which adds the container when <c>ASPIREHCS_TEST_IMAGE</c> is
    /// set. The sample is the only path that configures DCP and the dashboard.
    /// </summary>
    public static async Task<IDistributedApplicationTestingBuilder> SampleAppHostAsync(
        string command, CancellationToken cancellationToken)
    {
        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cancellationToken);

        // The tests exercise the sample's container alone. Everything else the showcase adds
        // (frontend, Postgres, opt-in VMs) drags its own dependencies into every container
        // test, and web's WaitFor(worker) runs inside app.StartAsync — with a test workload
        // that never opens the health endpoint, that wait never ends. Keep-list, not
        // denylist: a resource the sample grows later must not silently re-enter the tests.
        foreach (IResource extra in appHost.Resources
            .Where(r => r is not HcsContainerResource)
            .ToList())
        {
            appHost.Resources.Remove(extra);
        }

        HcsContainerResource worker = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(worker).WithCommand(command);

        return appHost;
    }

    /// <summary>Reads the id the resource generated, so absence can be asserted against it.</summary>
    public static string ContainerIdOf(DistributedApplication app, string resourceName = "worker")
    {
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();
        return model.Resources.OfType<HcsContainerResource>().Single(r => r.Name == resourceName).ContainerId;
    }

    /// <summary>
    /// Runs one workload to completion and returns everything the resource logged, which is what
    /// a developer would see in the dashboard.
    /// </summary>
    /// <param name="configure">
    /// Applied to the sample's container before the app is built; a test can add environment,
    /// mounts or a scratch size.
    /// </param>
    public static async Task<string> RunAndCaptureAsync(
        string command,
        Action<IResourceBuilder<HcsContainerResource>>? configure,
        CancellationToken cancellationToken)
    {
        IDistributedApplicationTestingBuilder appHost = await SampleAppHostAsync(command, cancellationToken);

        if (configure is not null)
        {
            HcsContainerResource resource = appHost.Resources.OfType<HcsContainerResource>().Single();
            configure(appHost.CreateResourceBuilder(resource));
        }

        StringBuilder captured = new();
        await using DistributedApplication app = await appHost.BuildAsync(cancellationToken);

        await app.StartAsync(cancellationToken);

        // Watch concurrently with the run: a one-shot workload can finish before a watcher
        // started afterwards sees anything.
        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = PumpLogsAsync(app, captured, watching.Token);

        // FailedToStart is waited for alongside the success states: a boot that fails never
        // reaches Finished.
        string reached = await app.ResourceNotifications.WaitForResourceAsync(
            "worker",
            [KnownResourceStates.Finished, KnownResourceStates.Exited, KnownResourceStates.FailedToStart],
            cancellationToken);

        // Grace period: the resource reaching a terminal state and its logs being flushed are
        // not the same instant.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await watching.CancelAsync();
        await pump;

        await app.StopAsync(cancellationToken);

        if (reached == KnownResourceStates.FailedToStart)
        {
            throw new ContainerStartFailedException(captured.ToString());
        }

        return captured.ToString();
    }

    /// <summary>
    /// Collects a resource's log lines for the duration of <paramref name="observation"/>: the
    /// watch starts before the observation so early lines are not missed, and is cancelled and
    /// drained in every outcome. A watcher fault fails a green observation; after a failed
    /// observation it is reported without replacing the original failure.
    /// </summary>
    public static async Task ObserveResourceLogsAsync(
        DistributedApplication app,
        string resourceName,
        Xunit.Abstractions.ITestOutputHelper output,
        CancellationToken cancellationToken,
        Func<ResourceLogBuffer, Task> observation)
    {
        ResourceLogBuffer logs = new();
        ResourceLoggerService loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task logWatch = WatchLogsAsync();
        bool observationFailed = false;

        try
        {
            await observation(logs);
        }
        catch
        {
            observationFailed = true;
            throw;
        }
        finally
        {
            await watching.CancelAsync();

            try
            {
                await logWatch;
            }
            catch (Exception watcherFailure) when (observationFailed)
            {
                // The observation already failed; report the pump fault without replacing it.
                output.WriteLine($"resource log watcher also failed: {watcherFailure}");
            }
        }

        async Task WatchLogsAsync()
        {
            try
            {
                await foreach (IReadOnlyList<LogLine> batch in loggerService.WatchAsync(resourceName).WithCancellation(watching.Token))
                {
                    logs.Add(batch);
                }
            }
            catch (OperationCanceledException) when (watching.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task PumpLogsAsync(DistributedApplication app, StringBuilder into, CancellationToken cancellationToken)
    {
        ResourceLoggerService logs = app.Services.GetRequiredService<ResourceLoggerService>();
        try
        {
            await foreach (IReadOnlyList<LogLine> batch in logs.WatchAsync("worker").WithCancellation(cancellationToken))
            {
                foreach (LogLine line in batch)
                {
                    lock (into)
                    {
                        into.AppendLine(line.Content);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the watch is cancelled once the workload has finished.
        }
    }

    /// <summary>
    /// Runs hcsctl and returns its one stdout document. Independent of the product's own runner.
    /// </summary>
    public static async Task<string> RunHcsCtlJsonAsync(
        string hcsctl, CancellationToken cancellationToken, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(hcsctl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--json");

        using Process process = Process.Start(startInfo)!;
        // Both pipes are drained concurrently: a child that fills the stderr pipe while stdout
        // is read to end blocks writing and never exits.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stderr;
        return await stdout;
    }

    /// <summary>
    /// Polls <c>container ps</c> until a guest process with the given image name exists. A test
    /// that manipulates a running workload (pause, kill) must first see it actually running:
    /// the resource reports Running before the workload's guest process is created, and pausing
    /// inside that window freezes the guest first — the workload's HcsCreateProcess then fails
    /// 0x80370105 and the workload is marked exited (AspireHcs#74).
    /// </summary>
    public static async Task WaitForGuestProcessAsync(
        string hcsctl, string store, DistributedApplication app, string imageName, CancellationToken cancellationToken)
    {
        string id = ContainerIdOf(app);
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        // Monotonic: the lab hosts resync their clocks after checkpoint restores, and a wall
        // clock step would shrink or stretch the bound.
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string lastPsOutput;

        // Bound this wait at two minutes: the sole pause/resume caller has a five-minute
        // end-to-end budget, leaving time to diagnose.
        while (true)
        {
            lastPsOutput = await RunHcsCtlJsonAsync(
                hcsctl, cancellationToken, "container", "ps", "--id", id, "--store", store);
            using (JsonDocument document = JsonDocument.Parse(lastPsOutput))
            {
                if (document.RootElement.TryGetProperty("processes", out JsonElement processes)
                    && processes.ValueKind == JsonValueKind.Array
                    && processes.EnumerateArray().Any(p =>
                        p.TryGetProperty("ImageName", out JsonElement name)
                        && string.Equals(name.GetString(), imageName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            TimeSpan remaining = TimeSpan.FromMilliseconds(deadline - Environment.TickCount64);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(500)
                    ? remaining
                    : TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        // A token cancelled while the last reply was being inspected is cancellation, not a
        // timeout.
        cancellationToken.ThrowIfCancellationRequested();

        throw new InvalidOperationException(
            $"Timed out after {timeout.TotalMinutes:0} minutes waiting for guest image/process '{imageName}' " +
            $"in container '{id}'. Final container ps output: {lastPsOutput}");
    }

    /// <summary>
    /// Skips unless <paramref name="imageReference"/> is materialized in the store. For tests
    /// whose workload needs a tool the default nanoserver image does not carry; the skip message
    /// names the commands that provision it, so an absent image reads as setup, not product
    /// failure.
    /// </summary>
    public static async Task RequireMaterializedImageAsync(
        string hcsctl, string store, string imageReference, CancellationToken cancellationToken)
    {
        string stdout = await RunHcsCtlJsonAsync(hcsctl, cancellationToken, "image", "ls", "--store", store);

        using JsonDocument document = JsonDocument.Parse(stdout);
        bool materialized = document.RootElement.TryGetProperty("images", out JsonElement images)
            && images.ValueKind == JsonValueKind.Array
            && images.EnumerateArray().Any(i =>
                i.TryGetProperty("ref", out JsonElement reference)
                && string.Equals(reference.GetString(), imageReference, StringComparison.OrdinalIgnoreCase)
                && i.TryGetProperty("materialized", out JsonElement m)
                && m.ValueKind == JsonValueKind.True);

        Skip.IfNot(materialized,
            $"{imageReference} is not materialized in {store}. Provision it once with: " +
            $"hcsctl image pull --ref {imageReference} --store {store} && " +
            $"hcsctl image import --ref {imageReference} --store {store} (import needs elevation).");
    }

    /// <summary>Asks hcsctl directly what containers exist.</summary>
    public static async Task<string[]> ListContainerIdsAsync(string hcsctl, string store, CancellationToken cancellationToken)
    {
        string stdout = await RunHcsCtlJsonAsync(hcsctl, cancellationToken, "container", "ls", "--store", store);

        using JsonDocument document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("containers", out JsonElement containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            // hcsctl is Go: an empty list marshals as null, not [].
            return [];
        }

        return [.. containers.EnumerateArray()
            .Select(c => c.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
            .Where(id => id is not null)
            .Select(id => id!)];
    }
}
