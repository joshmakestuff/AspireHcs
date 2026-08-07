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
/// error — the reason a boot failed is in there, and a bare assertion failure would discard it.
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
    /// Whether this process holds an enabled <c>BUILTIN\Administrators</c> SID. A few hcsctl
    /// operations need it — sizing the scratch does, running a container does not — so a test
    /// that needs it says so rather than failing on every ordinary dev box.
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
    /// set. Going through the sample rather than an in-process builder is what #39 asks for, and
    /// it is also the only way to get DCP and the dashboard configured.
    /// </summary>
    public static async Task<IDistributedApplicationTestingBuilder> SampleAppHostAsync(
        string command, CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable("ASPIREHCS_TEST_COMMAND", command);
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
    /// Applied to the sample's container before the app is built, so a test can add environment,
    /// mounts or a scratch size without the sample growing a knob per test.
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

        // FailedToStart is waited for alongside the success states, not left out. A boot that
        // fails never reaches Finished, so omitting it means blocking until the test's own
        // timeout — five minutes of nothing, ending in a cancellation that says far less than
        // the failure did.
        string reached = await app.ResourceNotifications.WaitForResourceAsync(
            "worker",
            [KnownResourceStates.Finished, KnownResourceStates.Exited, KnownResourceStates.FailedToStart],
            cancellationToken);

        // A short grace period so the last lines land before the pump is cut off — the resource
        // reaching a terminal state and its logs being flushed are not the same instant.
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
    /// Asks hcsctl directly what containers exist. Independent of the product's own listing code
    /// on purpose: a teardown check that reuses the code under test can only confirm it is
    /// self-consistent.
    /// </summary>
    public static async Task<string[]> ListContainerIdsAsync(string hcsctl, string store, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(hcsctl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "container", "ls", "--store", store, "--json" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        using JsonDocument document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("containers", out JsonElement containers)
            || containers.ValueKind != JsonValueKind.Array)
        {
            // hcsctl is Go: an empty list marshals as null, not []. That is "none", not a fault.
            return [];
        }

        return [.. containers.EnumerateArray()
            .Select(c => c.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
            .Where(id => id is not null)
            .Select(id => id!)];
    }
}
