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
        // The sample AppHost resolves its own store from ASPIREHCS_STORE (AppHost.cs),
        // not from ASPIREHCS_TEST_STORE. Without this mirror the product would create its
        // container in the AppHost's default store while the tests query the test store --
        // every store-backed assertion would then miss the container.
        if (!string.IsNullOrWhiteSpace(store))
        {
            Environment.SetEnvironmentVariable("ASPIREHCS_STORE", store);
        }

        return (hcsctl!, store!, image!);
    }

    /// <summary>
    /// Builds the sample AppHost, which adds the container when <c>ASPIREHCS_TEST_IMAGE</c> is
    /// set. The sample is the only path that configures DCP and the dashboard.
    /// </summary>
    public static async Task<IDistributedApplicationTestingBuilder> SampleAppHostAsync(
        string command, CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable("ASPIREHCS_TEST_COMMAND", command);

        // The AppHost reads ASPIREHCS_STORE, not ASPIREHCS_TEST_STORE (see Require): keep
        // the product and the tests on the same store even when a test path skips Require.
        string? testStore = Environment.GetEnvironmentVariable(StoreVariable);
        if (!string.IsNullOrWhiteSpace(testStore))
        {
            Environment.SetEnvironmentVariable("ASPIREHCS_STORE", testStore);
        }

        return await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cancellationToken);
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
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return stdout;
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
        while (true)
        {
            string json = await RunHcsCtlJsonAsync(hcsctl, cancellationToken, "container", "ps", "--id", id, "--store", store);
            using (JsonDocument document = JsonDocument.Parse(json))
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

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
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
