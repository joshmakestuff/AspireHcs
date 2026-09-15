using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Cli;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// A live bind mount, end to end: the host exposes two directories, the guest agent mounts them
// (read-write and read-only), a host file is visible in the guest and a guest write reaches the
// host, the read-only mount refuses a write, and teardown leaves no exposure behind and does not
// touch the sources. Needs HCS_TEST_VHDX (a Linux fixture) and a host prepared for file sharing;
// the skip names the exact prepare command when it is not.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class VmBindMountTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_vm_mounts_a_host_share_read_write_and_read_only_and_tears_it_down()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI Linux VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(6));

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());

        string network = vm.NetworkName ?? "Default Switch";

        // The prepare step is elevated and out of band; a host that has not had it done skips with
        // the exact command, the same way the product's preflight would fail the boot.
        HcsCtl hcsctl = new(HcsCtlBinary.Locate(vm.HcsCtlPath), vm.StorePath);
        HcsCtlFilesInspectDocument inspect = await hcsctl.InspectFilesAsync(cancellationToken: cts.Token);
        Skip.IfNot(inspect.Prepared && inspect.Networks.Any(n => string.Equals(n, network, StringComparison.OrdinalIgnoreCase)),
            $"Host not prepared for VM file sharing on '{network}'. Run (elevated): hcsctl files prepare --network {network}");

        string shareRoot = inspect.Root ?? throw new InvalidOperationException("files inspect reported no root.");

        // Sources live on E:, off the system volume, per the workspace's runtime-safety rule.
        string tempRoot = Path.Combine(@"E:\", "aspirehcs-bindmount-tests", Guid.NewGuid().ToString("N"));
        string rwSource = Path.Combine(tempRoot, "data");
        string roSource = Path.Combine(tempRoot, "cfg");
        Directory.CreateDirectory(rwSource);
        Directory.CreateDirectory(roSource);
        await File.WriteAllTextAsync(Path.Combine(rwSource, "data.txt"), "from-host-rw", cts.Token);
        await File.WriteAllTextAsync(Path.Combine(roSource, "cfg.txt"), "from-host-ro", cts.Token);

        try
        {
            // Replace whatever the sample declared (HCS_SAMPLE_LINUX_MOUNT may or may not be set)
            // with exactly the two mounts under test.
            vm.Mounts.Clear();
            vm.Mounts.Add(new HcsVmMount(rwSource, "/mnt/data", IsReadOnly: false));
            vm.Mounts.Add(new HcsVmMount(roSource, "/mnt/cfg", IsReadOnly: true));

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            output.WriteLine($"booted to Running with two mounts; share root {shareRoot}");

            // Host file visible in the guest, on both mounts.
            await AssertGuest(hcsctl, vm.VmId, "grep -q from-host-rw /mnt/data/data.txt", 0, "read host file (rw mount)", cts.Token);
            await AssertGuest(hcsctl, vm.VmId, "grep -q from-host-ro /mnt/cfg/cfg.txt", 0, "read host file (ro mount)", cts.Token);

            // Guest write reaches the host through the read-write mount.
            await AssertGuest(hcsctl, vm.VmId, "printf guest-wrote > /mnt/data/out.txt", 0, "write through rw mount", cts.Token);
            string hostSideWrite = Path.Combine(rwSource, "out.txt");
            Assert.True(File.Exists(hostSideWrite), "the guest's write did not reach the host source directory.");
            Assert.Equal("guest-wrote", await File.ReadAllTextAsync(hostSideWrite, cts.Token));

            // The read-only mount refuses a write, host-enforced by the read-only share.
            await AssertGuestFails(hcsctl, vm.VmId, "printf x > /mnt/cfg/x.txt", "write through ro mount is refused", cts.Token);
            Assert.False(File.Exists(Path.Combine(roSource, "x.txt")), "a write reached the read-only source.");

            await app.StopAsync(cts.Token);

            // Teardown removed every exposure and the VM's directory under the share root, and left
            // the sources — save for the expected guest-written file — untouched.
            HcsCtlFilesListDocument after = await hcsctl.ListFilesAsync(cancellationToken: cts.Token);
            Assert.DoesNotContain(after.Exposures, e => string.Equals(e.VmId, vm.VmId, StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(shareRoot, vm.VmId)), "the VM's directory under the share root was left behind.");
            Assert.True(File.Exists(Path.Combine(rwSource, "data.txt")), "unexpose deleted a source file.");
            Assert.True(File.Exists(Path.Combine(roSource, "cfg.txt")), "unexpose deleted a source file.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task AssertGuest(
        HcsCtl hcsctl, string vmId, string command, int expectedExitCode, string what, CancellationToken cancellationToken)
    {
        HcsCtlGuestExecDocument result = await hcsctl.GuestExecAsync(
            vmId, command, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Assert.True(result.ExitCode == expectedExitCode,
            $"guest command for '{what}' exited {result.ExitCode} (wanted {expectedExitCode}). {result.Detail}");
    }

    private static async Task AssertGuestFails(
        HcsCtl hcsctl, string vmId, string command, string what, CancellationToken cancellationToken)
    {
        HcsCtlGuestExecDocument result = await hcsctl.GuestExecAsync(
            vmId, command, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Assert.True(result.ExitCode != 0, $"guest command for '{what}' unexpectedly succeeded (exit 0).");
    }
}
