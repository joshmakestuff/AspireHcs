using System.Diagnostics;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// The shell connect command launches hcsctl on the host; this proves the resulting command line
// reaches a real, running container. It does not, and cannot, prove that a console window opens
// and a human can type into it — hcsctl's --tty requires its own stdin/stdout to be attached
// terminals, which an automated test cannot supply, and interactive mode has no parseable
// output. Deliberately tripping that guard is used as a proxy instead: it fails with a different,
// earlier message ("no container named ...") if the id/store are wrong, so reaching the tty
// message proves the argv this feature built reached a real container.
//
// Not covered: a console window actually appearing on the desktop with a working prompt inside.
// That is a manual check (same discipline as ConnectCommandLiveTests for SSH/RDP).
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ContainerConnectCommandLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Shell_connect_command_line_reaches_the_running_container()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ping -t 127.0.0.1", cts.Token);

        HcsContainerResource worker = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(worker).WithShellCommand();

        await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        // The predicate overload returns the snapshot, which is needed below.
        ResourceEvent running = await app.ResourceNotifications.WaitForResourceAsync(
            "worker", e => e.Snapshot.State?.Text == KnownResourceStates.Running, cts.Token);

        // The button must be enabled at this moment, evaluated against the real snapshot.
        ResourceCommandAnnotation command = worker.Annotations.OfType<ResourceCommandAnnotation>()
            .Single(a => a.Name == ContainerConnectCommands.ShellCommandName);
        ResourceCommandState state = command.UpdateState(
            new UpdateCommandStateContext { ResourceSnapshot = running.Snapshot, Services = app.Services });
        Assert.Equal(ResourceCommandState.Enabled, state);

        // The product's own argument list, verbatim.
        ProcessStartInfo built = ContainerConnectCommands.BuildShellStartInfo(worker, "cmd.exe");
        output.WriteLine($"connect command: {built.FileName} {string.Join(' ', built.ArgumentList)}");

        ProcessStartInfo probe = new(built.FileName)
        {
            // Redirected: a console window would carry the answer out of the test's reach. This
            // is exactly what trips hcsctl's --tty guard (it checks its OWN stdin/stdout), which
            // is the point: a real console session is the manual check noted above.
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in built.ArgumentList)
        {
            probe.ArgumentList.Add(argument);
        }

        using Process hcsctl = Process.Start(probe)!;
        string stderr = await hcsctl.StandardError.ReadToEndAsync(cts.Token);
        await hcsctl.WaitForExitAsync(cts.Token);
        output.WriteLine($"hcsctl exit {hcsctl.ExitCode}; stderr: {stderr.Trim()}");

        Assert.Contains("--tty requires attached stdin and stdout terminals", stderr, StringComparison.Ordinal);

        await app.StopAsync(cts.Token);
    }
}
