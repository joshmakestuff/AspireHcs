using System.Diagnostics;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #26. The unit tests pin what the connect command WOULD spawn; nothing there proves the
// resulting command line reaches a guest. This does: it boots the real Windows image and runs
// the argument list the product built, unmodified apart from prefixed batch-mode options, and
// requires sshd to answer.
//
// What it deliberately does NOT cover: ShellExecute putting a client window on the desktop.
// That needs a human to see a window appear, so it is a manual step recorded in
// docs/connect-ux.md rather than a claim asserted here.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ConnectCommandLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Ssh_connect_command_line_reaches_the_guest_sshd()
    {
        string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_TEST_WINDOWS_VHDX");
        Skip.If(string.IsNullOrEmpty(windowsVhdx),
            "Set HCS_TEST_WINDOWS_VHDX to the sealed Windows guest image (tools/guest-images/windows) to run the connect-UX test.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", windowsVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
            appHost.CreateResourceBuilder(vm).WithSshCommand(userName: "Administrator");

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);

            // The predicate overload, because the snapshot itself is needed below — the
            // target-state overload returns nothing.
            ResourceEvent running = await app.ResourceNotifications.WaitForResourceAsync(
                "appliance", e => e.Snapshot.State?.Text == KnownResourceStates.Running, cts.Token);

            // Running arrives before the DHCP lease surfaces, which is exactly the window the
            // command's availability gate exists for — so wait for the allocation the same way
            // the gate reads it, rather than assuming Running implies an address.
            EndpointAnnotation endpoint = vm.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "ssh");
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (endpoint.AllocatedEndpoint is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            AllocatedEndpoint allocated = endpoint.AllocatedEndpoint
                ?? throw new TimeoutException("The guest never leased an address, so there was nothing to connect to.");
            output.WriteLine($"guest leased {allocated.Address}:{allocated.Port}");

            // The button must actually be live at this moment. Evaluated against the real
            // snapshot, not a synthesized one.
            ResourceCommandAnnotation command = vm.Annotations.OfType<ResourceCommandAnnotation>()
                .Single(a => a.Name == ConnectCommands.SshCommandName);
            ResourceCommandState state = command.UpdateState(
                new UpdateCommandStateContext { ResourceSnapshot = running.Snapshot, ServiceProvider = app.Services });
            Assert.Equal(ResourceCommandState.Enabled, state);

            // The product's own argument list, verbatim.
            ProcessStartInfo built = ConnectCommands.BuildSshStartInfo(allocated.Address, allocated.Port, "Administrator");
            output.WriteLine($"connect command: {built.FileName} {string.Join(' ', built.ArgumentList)}");

            string knownHosts = Path.Combine(Path.GetTempPath(), $"aspirehcs-knownhosts-{Guid.NewGuid():N}");
            ProcessStartInfo probe = new(built.FileName)
            {
                // Redirected rather than shell-executed: this asserts where the command line
                // GOES, and a console window would carry the answer out of the test's reach.
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in built.ArgumentList)
            {
                probe.ArgumentList.Add(argument);
            }

            // Prefixed, never appended: ssh stops parsing options at the first non-option, so an
            // option after the host would be sent as a remote command instead.
            probe.ArgumentList.Insert(0, "-o");
            probe.ArgumentList.Insert(1, "BatchMode=yes");
            probe.ArgumentList.Insert(2, "-o");
            probe.ArgumentList.Insert(3, "StrictHostKeyChecking=no");
            probe.ArgumentList.Insert(4, "-o");
            probe.ArgumentList.Insert(5, $"UserKnownHostsFile={knownHosts}");
            probe.ArgumentList.Insert(6, "-o");
            probe.ArgumentList.Insert(7, "ConnectTimeout=15");

            try
            {
                using Process ssh = Process.Start(probe)!;
                string stderr = await ssh.StandardError.ReadToEndAsync(cts.Token);
                string stdout = await ssh.StandardOutput.ReadToEndAsync(cts.Token);
                await ssh.WaitForExitAsync(cts.Token);
                output.WriteLine($"ssh exit {ssh.ExitCode}; stderr: {stderr.Trim()}");

                // "Permission denied" is the SUCCESS condition: reaching authentication means the
                // TCP connect, the version exchange and the key exchange all completed against a
                // real sshd. We hold no credential, so authenticating is not on offer — and a
                // test that needed one would be asserting the image's password, not reachability.
                Assert.True(
                    stderr.Contains("Permission denied", StringComparison.Ordinal)
                        || stderr.Contains("Authenticated", StringComparison.Ordinal),
                    $"ssh did not reach authentication against {allocated.Address}:{allocated.Port}. " +
                    $"exit={ssh.ExitCode} stderr={stderr.Trim()} stdout={stdout.Trim()}");
            }
            finally
            {
                // Never leave a host key for an ephemeral guest in the developer's known_hosts.
                File.Delete(knownHosts);
            }

            await app.StopAsync(cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", originalVhdx);
        }
    }
}
