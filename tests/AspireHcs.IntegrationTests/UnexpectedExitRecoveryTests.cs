using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #14 acceptance: a VM that exits on its own (guest poweroff, kernel failure, or any
// out-of-band termination) must leave the resource genuinely startable — Start performs a real
// fresh boot rather than returning success as a no-op — and the exited boot's endpoint, ACL
// grants and work directory must not stay owned until AppHost shutdown.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class UnexpectedExitRecoveryTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Guest_initiated_exit_is_recovered_by_a_real_start()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(10));
        string aclBefore = TeardownProbes.ReadAcl(vhdx!);

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            output.WriteLine($"booted at {app.GetEndpoint("appliance", "ssh")}");

            // Kill the compute system out of band. From the orchestrator's point of view this is
            // indistinguishable from the guest powering itself off -- both leave the store record
            // in place with no compute system behind it, which is what the exit watch looks for.
            Assert.True(
                HcsCtlProbes.TryRun(["vm", "stop", "--id", vm.VmId, "--force"], out string killed, vm.StorePath),
                $"could not kill the VM out of band: {killed}");

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Exited, cts.Token);
            output.WriteLine("VM exited out-of-band; issuing Start");

            // The lying no-op this issue was filed about: Start used to see a stale compute
            // system reference, skip the boot, and still report success. A real boot is proven by
            // the resource actually reaching Running again with a freshly resolved endpoint.
            ExecuteCommandResult result = await app.ResourceCommands
                .ExecuteCommandAsync("appliance", KnownResourceCommands.StartCommand, cts.Token);
            Assert.True(result.Success, $"Start after guest exit failed: {result.Message}");

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            Uri rebooted = app.GetEndpoint("appliance", "ssh");
            output.WriteLine($"rebooted at {rebooted}");
            Assert.Equal(22, rebooted.Port);

            await app.StopAsync(cts.Token);
        }

        Assert.DoesNotContain(vm.VmId, HcsCtlProbes.VmIds(vm.StorePath));
        Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));
    }
}
