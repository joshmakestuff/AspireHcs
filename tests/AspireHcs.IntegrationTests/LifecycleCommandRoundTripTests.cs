using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// The dashboard's Start/Stop/Restart drive a real VM. Aspire wires those commands only for
// resources DCP owns, so the orchestrator stops and re-boots the compute system itself,
// releasing and recreating the HCN endpoint each time.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class LifecycleCommandRoundTripTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Stop_then_start_then_restart_drive_the_vm()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(10));

        // Three boots run below; each grants VM access to the base image and must revoke it.
        string aclBefore = TeardownProbes.ReadAcl(vhdx!);

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
        string workDir = Path.Combine(Path.GetTempPath(), "AspireHcs", vm.VmId);

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);

            await WaitForStateAsync(app, KnownResourceStates.Running, cts.Token);
            output.WriteLine($"booted at {app.GetEndpoint("appliance", "ssh")}");

            await ExecuteAsync(app, KnownResourceCommands.StopCommand, cts.Token);
            await WaitForStateAsync(app, KnownResourceStates.Exited, cts.Token);

            // A dashboard Stop is a complete teardown: the grants and the copy-on-write directory
            // must already be gone while the AppHost keeps running.
            Assert.False(Directory.Exists(workDir), $"Stop left the work directory behind: {workDir}");
            Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));

            // Success proves Stop tore the compute system down: the VM id is stable for the
            // resource's lifetime, so HcsCreateComputeSystem is rejected if the previous one
            // still exists.
            await ExecuteAsync(app, KnownResourceCommands.StartCommand, cts.Token);
            await WaitForStateAsync(app, KnownResourceStates.Running, cts.Token);

            // A second boot re-creates the HCN endpoint and re-resolves the endpoint from a fresh
            // lease; the allocation from the first boot does not survive teardown.
            Uri restarted = app.GetEndpoint("appliance", "ssh");
            output.WriteLine($"restarted at {restarted}");
            Assert.Equal(22, restarted.Port);

            await ExecuteAsync(app, KnownResourceCommands.RestartCommand, cts.Token);
            await WaitForStateAsync(app, KnownResourceStates.Running, cts.Token);
            output.WriteLine($"after restart at {app.GetEndpoint("appliance", "ssh")}");

            await app.StopAsync(cts.Token);
        }

        // Whole-run residue check: three boots, three teardowns, zero net host mutation.
        Assert.False(Directory.Exists(workDir), $"work directory survived the run: {workDir}");
        Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));
    }

    private async Task ExecuteAsync(DistributedApplication app, string command, CancellationToken cancellationToken)
    {
        ExecuteCommandResult result = await app.ResourceCommands
            .ExecuteCommandAsync("appliance", command, cancellationToken);

        output.WriteLine($"{command}: success={result.Success} {result.Message}");
        Assert.True(result.Success, $"{command} failed: {result.Message}");
    }

    private static Task WaitForStateAsync(DistributedApplication app, string state, CancellationToken cancellationToken)
        => app.ResourceNotifications.WaitForResourceAsync("appliance", state, cancellationToken);
}
