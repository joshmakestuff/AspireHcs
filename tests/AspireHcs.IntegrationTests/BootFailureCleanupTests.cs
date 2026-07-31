using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hcn;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #17 acceptance: a boot that fails after the copy-on-write disk exists but before any
// HCS compute system does must still release everything it acquired — the work directory in
// TEMP most of all, since nothing else ever cleans it up (each run has a fresh VM id) — and
// must leave the resource retryable from the dashboard.
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
        string workDir = Path.Combine(Path.GetTempPath(), "AspireHcs", vm.VmId);

        // Occupy the resource's own HCN endpoint id under a foreign owner. The orchestrator's
        // scavenger only touches AspireHcs-owned endpoints, so it leaves this one alone, and
        // endpoint creation then fails deterministically — after PrepareBootDisk, before any
        // grant or compute system. Exactly the window this issue is about.
        Guid networkId = HcnClient.FindIcsNetworkId();
        HcnClient.CreateDhcpEndpoint(networkId, vm.HcnEndpointId, "02-15-5D-00-00-01",
            owner: "AspireHcs.IntegrationTests.Conflict");
        try
        {
            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.FailedToStart, cts.Token);
            output.WriteLine("boot failed at endpoint creation, as arranged");

            // The failed boot's differencing disk used to survive here (cleanup was nested under
            // "was a compute system created", which it never was).
            Assert.False(Directory.Exists(workDir), $"failed boot left its work directory behind: {workDir}");
            Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));

            // Clear the conflict; a retry from the dashboard must now boot for real, which also
            // proves the failed boot released everything a fresh one needs (same VM id, same
            // endpoint id, same work directory).
            HcnClient.DeleteEndpoint(vm.HcnEndpointId);

            ExecuteCommandResult retried = await app.ResourceCommands
                .ExecuteCommandAsync("appliance", KnownResourceCommands.StartCommand, cts.Token);
            Assert.True(retried.Success, $"Start after a failed boot failed: {retried.Message}");

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            output.WriteLine($"recovered to Running at {app.GetEndpoint("appliance", "ssh")}");

            await app.StopAsync(cts.Token);
            Assert.False(Directory.Exists(workDir), $"work directory survived the run: {workDir}");
            Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));
        }
        finally
        {
            try
            {
                HcnClient.DeleteEndpoint(vm.HcnEndpointId);
            }
            catch (Exception)
            {
                // Already deleted mid-test on the success path.
            }
        }
    }
}
