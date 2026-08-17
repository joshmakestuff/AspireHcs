using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// A boot that fails after the copy-on-write disk exists but before any HCS compute system
// does must release everything it acquired (the work directory in TEMP most of all: each run
// has a fresh VM id, so nothing else cleans it up) and must leave the resource retryable from
// the dashboard.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class BootFailureCleanupTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Pre_hcs_boot_failure_releases_everything_and_start_can_retry()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(10));
        string aclBefore = TeardownProbes.ReadAcl(vhdx!);

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());

        // Occupy the resource's own VM id out of band. hcsctl refuses to create a second VM
        // under an id its store already holds, so the boot fails deterministically inside
        // `vm create`: after the disk work, before anything is running. The scavenger does not
        // clear it: this VM carries no owner label, so it is not reclaimed.
        Assert.True(
            HcsCtlProbes.TryRun(["vm", "create", "--id", vm.VmId, "--vhdx", vhdx!], out string arranged, vm.StorePath),
            $"could not arrange the conflict: {arranged}");
        try
        {
            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.FailedToStart, cts.Token);
            output.WriteLine("boot failed at endpoint creation, as arranged");

            // The failed boot must not have touched the base image's ACL. hcsctl grants VM access
            // inside `vm create` and revokes it in `vm rm`, so a create that failed and was not
            // cleaned up would show here as an extra ACE.
            Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));

            // Clear the conflict; a retry from the dashboard must now boot for real with the same
            // VM id, store and disk, which proves the failed boot released everything.
            Assert.True(
                HcsCtlProbes.TryRun(["vm", "rm", "--id", vm.VmId, "--force"], out string cleared, vm.StorePath),
                $"could not clear the conflict: {cleared}");

            ExecuteCommandResult retried = await app.ResourceCommands
                .ExecuteCommandAsync("appliance", KnownResourceCommands.StartCommand, cts.Token);
            Assert.True(retried.Success, $"Start after a failed boot failed: {retried.Message}");

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            output.WriteLine($"recovered to Running at {app.GetEndpoint("appliance", "ssh")}");

            await app.StopAsync(cts.Token);
            Assert.DoesNotContain(vm.VmId, HcsCtlProbes.VmIds(vm.StorePath));
            Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));
        }
        finally
        {
            // On the success path the retry already owns this id; on any failure path the
            // arranged conflict must not outlive the test.
            _ = HcsCtlProbes.TryRun(["vm", "rm", "--id", vm.VmId, "--force"], out _, vm.StorePath);
        }
    }
}
