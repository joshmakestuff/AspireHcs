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
    private readonly string _directory = Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    private HcsCtl FakeCtl(string batchBody)
    {
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off{Environment.NewLine}{batchBody}{Environment.NewLine}");
        return new HcsCtl(path);
    }

    private static HcsVirtualMachineResource Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm").WithNetwork().Resource;

    [Fact]
    public async Task A_vm_with_no_connect_command_never_calls_hcsctl()
    {
        // No WithSshCommand: HvsocketForwardTargets is empty, so nothing here should even start
        // a `guest info` probe. A fake that fails any invocation proves it was never called.
        HcsCtl fake = FakeCtl("exit /b 99");
        HcsVirtualMachineResource resource = Vm();
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task An_unreachable_agent_leaves_the_leased_address_as_the_only_option()
    {
        HcsCtl fake = FakeCtl(
            """
            if "%2"=="info" (
              echo {"ok":false,"reachable":false,"state":"absent"}
              exit /b 1
            )
            exit /b 99
            """);
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_reachable_agent_starts_the_forward_and_publishes_its_address()
    {
        HcsCtl fake = FakeCtl(
            """
            if "%2"=="info" (
              echo {"ok":true,"reachable":true,"state":"ready"}
              exit /b 0
            )
            if "%2"=="forward" (
              echo {"ok":true,"command":"guest forward","listen":"127.0.0.1:54321","guestPort":22}
              ping -n 50 127.0.0.1 >nul
              exit /b 0
            )
            exit /b 99
            """);
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
            ledger.Drain();
        }

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_forward_that_starts_but_cannot_bind_leaves_the_leased_address_as_the_only_option()
    {
        HcsCtl fake = FakeCtl(
            """
            if "%2"=="info" (
              echo {"ok":true,"reachable":true,"state":"ready"}
              exit /b 0
            )
            if "%2"=="forward" (
              echo {"ok":false,"stage":"run","error":"listen 127.0.0.1:0: address already in use"}
              exit /b 1
            )
            exit /b 99
            """);
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(resource.ForwardedConnectAddresses);
    }

    [Fact]
    public async Task A_forward_that_exits_on_its_own_mid_session_un_publishes_its_address()
    {
        // The forward keeps the listener alive only briefly, then exits on its own — the same
        // observable shape as the guest agent crashing or the relay being killed externally.
        HcsCtl fake = FakeCtl(
            """
            if "%2"=="info" (
              echo {"ok":true,"reachable":true,"state":"ready"}
              exit /b 0
            )
            if "%2"=="forward" (
              echo {"ok":true,"command":"guest forward","listen":"127.0.0.1:54322","guestPort":22}
              ping -n 2 127.0.0.1 >nul
              exit /b 0
            )
            exit /b 99
            """);
        HcsVirtualMachineResource resource = Vm();
        resource.HvsocketForwardTargets["ssh"] = 22;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestForwardPump.StartAsync(resource, fake, ledger, NullLogger.Instance, CancellationToken.None);
        Assert.Equal("127.0.0.1:54322", resource.ForwardedConnectAddresses["ssh"]);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (resource.ForwardedConnectAddresses.ContainsKey("ssh") && !timeout.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Empty(resource.ForwardedConnectAddresses);
    }
}
