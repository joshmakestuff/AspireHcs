using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
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
// the workspace's docs/old/AspireHcs/docs/connect-ux.md rather than a claim asserted here.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ConnectCommandLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Rdp_connect_command_reaches_a_guest_that_serves_remote_desktop()
    {
        string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_TEST_WINDOWS_VHDX");
        Skip.If(string.IsNullOrEmpty(windowsVhdx),
            "Set HCS_TEST_WINDOWS_VHDX to the sealed Windows guest image to run the connect-UX tests.");

        // The image says for itself whether it serves RDP: New-WindowsGuestImage.ps1 refuses to
        // seal unless TermService was observed listening on 3389 during burn-in, and records
        // that in the provenance sidecar. Skipping on an older image is right — failing would
        // report a defect in the command when the truth is the fixture predates the feature.
        string provenancePath = Path.ChangeExtension(windowsVhdx, ".provenance.json");
        Skip.If(!File.Exists(provenancePath), $"No provenance beside {windowsVhdx}; cannot tell whether it serves RDP.");
        using JsonDocument provenance = JsonDocument.Parse(await File.ReadAllTextAsync(provenancePath));
        string? burnInRdp = provenance.RootElement.TryGetProperty("burnIn", out JsonElement burnIn)
            && burnIn.TryGetProperty("rdp", out JsonElement rdp)
                ? rdp.GetString()
                : null;
        Skip.If(burnInRdp != "Listening",
            $"{Path.GetFileName(windowsVhdx)} records burnIn.rdp='{burnInRdp ?? "<absent>"}' — built before the RDP edit.");
        output.WriteLine($"fixture provenance: burnIn.rdp={burnInRdp}");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", windowsVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
            appHost.CreateResourceBuilder(vm)
                .WithEndpoint("rdp", targetPort: 3389)
                .WithRdpCommand(userName: "Administrator");

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);

            ResourceEvent running = await app.ResourceNotifications.WaitForResourceAsync(
                "appliance", e => e.Snapshot.State?.Text == KnownResourceStates.Running, cts.Token);

            EndpointAnnotation endpoint = vm.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "rdp");
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (endpoint.AllocatedEndpoint is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            AllocatedEndpoint allocated = endpoint.AllocatedEndpoint
                ?? throw new TimeoutException("The guest never leased an address.");
            output.WriteLine($"guest leased {allocated.Address}:{allocated.Port}");

            ResourceCommandAnnotation command = vm.Annotations.OfType<ResourceCommandAnnotation>()
                .Single(a => a.Name == ConnectCommands.RdpCommandName);
            Assert.Equal(
                ResourceCommandState.Enabled,
                command.UpdateState(new UpdateCommandStateContext
                {
                    ResourceSnapshot = running.Snapshot,
                    ServiceProvider = app.Services,
                }));

            // The host-side half, and the credential-free analogue of SSH's "Permission denied":
            // 3389 accepting a connection from the host proves the guest is serving Remote
            // Desktop and that the NAT path reaches it. Completing an RDP handshake would need
            // the image's password, which the suite deliberately does not hold.
            // Retried rather than attempted once: the DHCP lease surfaces well before a guest
            // has finished starting its services, and on Desktop Experience that gap is wide.
            // A single attempt would conflate "not up yet" with "not reachable".
            Stopwatch reachable = Stopwatch.StartNew();
            TimeSpan rdpTimeout = TimeSpan.FromMinutes(2);
            Exception? lastFailure = null;
            bool connected = false;
            while (reachable.Elapsed < rdpTimeout && !connected)
            {
                try
                {
                    using TcpClient client = new();
                    await client.ConnectAsync(allocated.Address, allocated.Port)
                        .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
                    connected = true;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException)
                {
                    lastFailure = ex;
                    await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                }
            }

            Assert.True(connected,
                $"{allocated.Address}:{allocated.Port} never accepted a connection within {rdpTimeout}. " +
                $"The image's provenance records burnIn.rdp='{burnInRdp}', but that witnessed TermService during the " +
                "IMAGE BUILD boot only. A timeout rather than a refusal narrows it to something dropping packets — the " +
                "guest firewall, the host firewall, or the HCN path — and does not by itself distinguish those from a " +
                "service that failed to start on this boot. Check the guest directly over SSH. " +
                $"Last failure: {lastFailure?.GetType().Name}: {lastFailure?.Message}");
            output.WriteLine($"TCP {allocated.Address}:{allocated.Port} -> connected after {reachable.Elapsed.TotalSeconds:0.0}s");

            // And the file mstsc would actually be handed, written by the product.
            ProcessStartInfo startInfo = ConnectCommands.BuildRdpStartInfo(
                vm, "rdp", allocated.Address, allocated.Port, "Administrator");
            string rdpPath = Assert.Single(startInfo.ArgumentList);
            string content = await File.ReadAllTextAsync(rdpPath, RdpFile.FileEncoding, cts.Token);
            output.WriteLine($"generated {rdpPath}:{Environment.NewLine}{content.Trim()}");

            Assert.Equal("mstsc.exe", startInfo.FileName);
            Assert.Contains($"full address:s:{allocated.Address}:{allocated.Port}", content, StringComparison.Ordinal);
            Assert.Contains("username:s:Administrator", content, StringComparison.Ordinal);

            await app.StopAsync(cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", originalVhdx);
        }
    }

    [SkippableFact]
    public async Task Ssh_connect_command_line_reaches_the_guest_sshd()
    {
        string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_TEST_WINDOWS_VHDX");
        Skip.If(string.IsNullOrEmpty(windowsVhdx),
            "Set HCS_TEST_WINDOWS_VHDX to the sealed Windows guest image (built by hcsimgtool) to run the connect-UX test.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string? originalVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", windowsVhdx);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());

            // The sample AppHost already calls WithSshCommand, so registering it again here
            // would produce two connect-ssh annotations and break the Single() lookup below.
            // Asserted rather than assumed: if the sample stops registering it, this test must
            // fail loudly instead of silently testing a command nobody ships.
            Assert.Single(
                vm.Annotations.OfType<ResourceCommandAnnotation>(),
                a => a.Name == ConnectCommands.SshCommandName);

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

            // The state guard's lookup is the part with a real chance of being wrong: it asks
            // ResourceNotificationService for a resource by NAME, and the id it indexes by is
            // not guaranteed to equal the name. If that lookup always missed, the guard would
            // silently never fire and every unit test would still pass, because they inject the
            // state directly. So exercise the production path here, against the live host.
            string? observed = ConnectCommands.CurrentState(app.Services, "appliance");
            output.WriteLine($"CurrentState(\"appliance\") -> {observed ?? "<null>"}");
            Assert.Equal(KnownResourceStates.Running, observed);

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
