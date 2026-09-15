using System.Runtime.Versioning;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AspireHcs.Tests;

// GuestMounts applies a VM's bind mounts and records the teardown in the boot ledger. These pin
// the argv of each hcsctl call, the UNC it builds from what expose reported, and the unwind order
// (unmount before unexpose) — with a stand-in hcsctl, since a real one needs a prepared host and a
// live guest. No HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestMountsTests : IDisposable
{
    private const string Gateway = "172.28.191.1";

    private readonly FakeHcsCtlDirectory _fakes = new();
    private readonly string _argvPath;

    public GuestMountsTests() => _argvPath = Path.Combine(_fakes.Directory, "argv.json");

    public void Dispose() => _fakes.Dispose();

    private static IResourceBuilder<HcsVirtualMachineResource> Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm").WithNetwork();

    private FakeHcsCtlScenario Scenario(FakeHcsCtlResponse? mount = null) => new()
    {
        ArgumentsPath = _argvPath,
        Responses =
        [
            // relativePath is echoed by hcsctl per VM; the fake returns a fixed one and the UNC is
            // asserted against it, proving MountAllAsync builds the UNC from what expose reported.
            new()
            {
                ArgumentPrefix = ["files", "expose"],
                Stdout = """{"ok":true,"command":"files expose","vmId":"vm","name":"mount","source":"C:\\a","junction":"j","relativePath":"vm\\mount0","share":"hcsctl-files","readOnly":false,"aceAdded":true}""",
            },
            mount ?? new() { ArgumentPrefix = ["guest", "mount"], Stdout = """{"ok":true,"command":"guest mount","applied":"cifs"}""" },
            new() { ArgumentPrefix = ["guest", "unmount"], Stdout = """{"ok":true,"command":"guest unmount","applied":"cifs"}""" },
            new() { ArgumentPrefix = ["files", "unexpose"], Stdout = """{"ok":true,"command":"files unexpose","vmId":"vm","removed":[],"aceRevoked":[]}""" },
        ],
    };

    private List<string[]> RecordedCalls() =>
        [.. File.ReadAllLines(_argvPath)
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<string[]>(l)!)];

    [Theory]
    [InlineData("172.28.191.1", "hcsctl-files", @"vm\data", @"\\172.28.191.1\hcsctl-files\vm\data")]
    [InlineData("172.28.191.1", "hcsctl-files-ro", @"vm\cfg", @"\\172.28.191.1\hcsctl-files-ro\vm\cfg")]
    public void BuildUnc_joins_gateway_share_and_relative_path(string gateway, string share, string relative, string expected)
    {
        Assert.Equal(expected, GuestMounts.BuildUnc(gateway, share, relative));
    }

    [Fact]
    public async Task Each_mount_is_exposed_then_mounted_with_the_expected_argv()
    {
        HcsCtl fake = _fakes.Create(Scenario());
        HcsVirtualMachineResource vm = Vm()
            .WithBindMount(@"C:\a", "/mnt/a")
            .WithBindMount(@"C:\b", "/mnt/b", isReadOnly: true)
            .Resource;
        BootLedger ledger = new(NullLogger.Instance);
        string label = $"{HcsVmOrchestrator.OwnerPidLabel}={HcsVmOrchestrator.OwnerPidValue}";

        await GuestMounts.MountAllAsync(vm, fake, ledger, Gateway, NullLogger.Instance, CancellationToken.None);

        List<string[]> calls = RecordedCalls();
        Assert.Equal(4, calls.Count);

        // Read-write mount: no --ro on either call; the label stamps the owner for scavenging.
        Assert.Equal(
            ["files", "expose", "--vmid", vm.VmId, "--name", "mount0", "--source", @"C:\a", "--label", label, "--json"],
            calls[0]);
        Assert.Equal(
            ["guest", "mount", "--vmid", vm.VmId, "--unc", @"\\172.28.191.1\hcsctl-files\vm\mount0",
             "--path", "/mnt/a", "--credential", "hcsctl-files", "--timeout", "60s", "--json"],
            calls[1]);

        // Read-only mount: --ro on both the expose and the guest mount.
        Assert.Equal(
            ["files", "expose", "--vmid", vm.VmId, "--name", "mount1", "--source", @"C:\b", "--ro", "--label", label, "--json"],
            calls[2]);
        Assert.Equal(
            ["guest", "mount", "--vmid", vm.VmId, "--unc", @"\\172.28.191.1\hcsctl-files\vm\mount0",
             "--path", "/mnt/b", "--credential", "hcsctl-files", "--ro", "--timeout", "60s", "--json"],
            calls[3]);
    }

    [Fact]
    public async Task The_password_never_appears_in_any_recorded_call()
    {
        HcsCtl fake = _fakes.Create(Scenario());
        HcsVirtualMachineResource vm = Vm().WithBindMount(@"C:\a", "/mnt/a").Resource;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestMounts.MountAllAsync(vm, fake, ledger, Gateway, NullLogger.Instance, CancellationToken.None);

        foreach (string[] call in RecordedCalls())
        {
            Assert.DoesNotContain("--password", call);
            Assert.DoesNotContain("--password-stdin", call);
        }
    }

    [Fact]
    public async Task Draining_the_ledger_unmounts_in_reverse_then_unexposes_once()
    {
        HcsCtl fake = _fakes.Create(Scenario());
        HcsVirtualMachineResource vm = Vm()
            .WithBindMount(@"C:\a", "/mnt/a")
            .WithBindMount(@"C:\b", "/mnt/b")
            .Resource;
        BootLedger ledger = new(NullLogger.Instance);

        await GuestMounts.MountAllAsync(vm, fake, ledger, Gateway, NullLogger.Instance, CancellationToken.None);
        ledger.Drain();

        List<string[]> calls = RecordedCalls();
        // 4 setup calls (expose+mount x2), then unmount /mnt/b, unmount /mnt/a, unexpose.
        Assert.Equal(7, calls.Count);
        Assert.Equal(["guest", "unmount", "--vmid", vm.VmId, "--path", "/mnt/b", "--json"], calls[4]);
        Assert.Equal(["guest", "unmount", "--vmid", vm.VmId, "--path", "/mnt/a", "--json"], calls[5]);
        Assert.Equal(["files", "unexpose", "--vmid", vm.VmId, "--json"], calls[6]);
    }

    [Fact]
    public async Task A_failed_mount_throws_and_the_ledger_unwinds_to_unexpose()
    {
        // The expose succeeds, the guest mount fails: MountAllAsync throws, and draining the
        // ledger the caller owns removes the host junction even though no unmount was registered.
        HcsCtl fake = _fakes.Create(Scenario(mount: new()
        {
            ArgumentPrefix = ["guest", "mount"],
            Stdout = """{"ok":false,"stage":"run","error":"mount(2): Host is down"}""",
            ExitCode = HcsCtlExitCode.Failed,
        }));
        HcsVirtualMachineResource vm = Vm().WithBindMount(@"C:\a", "/mnt/a").Resource;
        BootLedger ledger = new(NullLogger.Instance);

        await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => GuestMounts.MountAllAsync(vm, fake, ledger, Gateway, NullLogger.Instance, CancellationToken.None));

        ledger.Drain();

        List<string[]> calls = RecordedCalls();
        Assert.Equal(["files", "expose", "--vmid", vm.VmId, "--name", "mount0", "--source", @"C:\a",
            "--label", $"{HcsVmOrchestrator.OwnerPidLabel}={HcsVmOrchestrator.OwnerPidValue}", "--json"], calls[0]);
        // The failed mount registered no unmount; the unexpose registered before the first expose
        // still runs and is the last call.
        Assert.DoesNotContain(calls, c => c.Length >= 2 && c[0] == "guest" && c[1] == "unmount");
        Assert.Equal(["files", "unexpose", "--vmid", vm.VmId, "--json"], calls[^1]);
    }
}
