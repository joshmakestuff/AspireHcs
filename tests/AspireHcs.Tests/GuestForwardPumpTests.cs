using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AspireHcs.Tests;

// GuestForwardPump decides whether Connect (SSH) gets an hvsocket forward instead of the leased
// address: it must never fail the boot, so an absent agent or a forward that cannot start has to
// degrade to nothing rather than throw, and a forward that dies mid-session has to un-publish
// itself so the button falls back cleanly. These pin that decision with a stand-in hcsctl, since
// a real one needs a live guest agent to answer `guest info` truthfully.
//
// These need no hcsctl and no HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestForwardPumpTests : IDisposable
{
    private readonly FakeHcsCtlDirectory _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private static HcsVirtualMachineResource Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm").WithNetwork().Resource;

    [Fact]
    public async Task A_vm_with_no_connect_command_never_calls_hcsctl()
    {
        // No WithSshCommand: HvsocketForwardTargets is empty, so nothing here should even start
        // a `guest info` probe. A fake that fails any invocation proves it was never called.
        HcsCtl fake = _fakes.Create(new() { DefaultResponse = new() { ExitCode = 99 } });
        HcsVirtualMachineResource resource = Vm();
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task An_unreachable_agent_leaves_the_leased_address_as_the_only_option()
    {
        HcsCtl fake = _fakes.Create(new()
        {
            Responses = [new() { ArgumentPrefix = ["guest", "info"], Stdout = "{\"ok\":false,\"reachable\":false,\"state\":\"absent\"}", ExitCode = HcsCtlExitCode.Failed }],
            DefaultResponse = new() { ExitCode = 99 },
        });
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_reachable_agent_starts_the_forward_and_publishes_its_address()
    {
        string release = Path.Combine(_fakes.Directory, "reachable-release");
        HcsCtl fake = _fakes.Create(new()
        {
            Responses =
            [
                new() { ArgumentPrefix = ["guest", "info"], Stdout = "{\"ok\":true,\"reachable\":true,\"state\":\"ready\"}" },
                new() { ArgumentPrefix = ["guest", "forward"], Stdout = "{\"ok\":true,\"command\":\"guest forward\",\"listen\":\"127.0.0.1:54321\",\"guestPort\":22}", ReleasePath = release },
            ],
            DefaultResponse = new() { ExitCode = 99 },
        });
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        try
        {
            await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

            Assert.Equal("127.0.0.1:54321", resource.ForwardedConnectAddresses["ssh"]);
        }
        finally
        {
            // Draining is the normal teardown path (mirrors the boot ledger on VM stop); it must
            // both kill the process and un-publish the address so a stale one is never dialled.
            File.WriteAllText(release, "release");
            ledger.Drain();
        }

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_forward_that_starts_but_cannot_bind_leaves_the_leased_address_as_the_only_option()
    {
        HcsCtl fake = _fakes.Create(new()
        {
            Responses =
            [
                new() { ArgumentPrefix = ["guest", "info"], Stdout = "{\"ok\":true,\"reachable\":true,\"state\":\"ready\"}" },
                new() { ArgumentPrefix = ["guest", "forward"], Stdout = "{\"ok\":false,\"stage\":\"run\",\"error\":\"listen 127.0.0.1:0: address already in use\"}", ExitCode = HcsCtlExitCode.Failed },
            ],
            DefaultResponse = new() { ExitCode = 99 },
        });
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_forward_that_exits_on_its_own_mid_session_un_publishes_its_address()
    {
        // The forward holds its listener until this test releases it, then exits on its own —
        // the same observable shape as the guest agent crashing or the relay being killed
        // externally. A marker file, not a fixed delay: a delay races the first assertion on a
        // slow runner (#90), where the exit and un-publish can land before the address is read.
        string release = Path.Combine(_fakes.Directory, "release");
        HcsCtl fake = _fakes.Create(new()
        {
            Responses =
            [
                new() { ArgumentPrefix = ["guest", "info"], Stdout = "{\"ok\":true,\"reachable\":true,\"state\":\"ready\"}" },
                new() { ArgumentPrefix = ["guest", "forward"], Stdout = "{\"ok\":true,\"command\":\"guest forward\",\"listen\":\"127.0.0.1:54322\",\"guestPort\":22}", ReleasePath = release },
            ],
            DefaultResponse = new() { ExitCode = 99 },
        });
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        try
        {
            await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);
            Assert.Equal("127.0.0.1:54322", resource.ForwardedConnectAddresses["ssh"]);

            File.WriteAllText(release, "released");

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (resource.ForwardedConnectAddresses.ContainsKey("ssh") && !timeout.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None);
            }

            Assert.Empty(resource.ForwardedConnectAddresses);
        }
        finally
        {
            // Kills a still-held fake if an assertion failed before the release.
            ledger.Drain();
        }
    }
}
