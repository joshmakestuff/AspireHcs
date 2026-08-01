using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hcn;
using AspireHcs.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// The teardown ledger's adjacent mode: every other orchestration test boots the sample's
// networked configuration, so without this the no-network path — no scavenge, no HCN endpoint,
// no endpoint allocation, straight from guest-ready to Running — would have no coverage at
// all, and neither would its shorter teardown. The other off-diagonal, copyOnWrite:false,
// stays deliberately untested until #11 produces disposable images: a non-CoW boot writes
// into the base VHDX, and the only base image on the runner is shared by every other test.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class NoNetworkTeardownTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task A_vm_without_network_boots_and_tears_down_cleanly()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        Skip.If(string.IsNullOrEmpty(vhdx),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        string aclBefore = TeardownProbes.ReadAcl(vhdx!);

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
        string workDir = Path.Combine(Path.GetTempPath(), "AspireHcs", vm.VmId);

        // Strip the sample down to a network-less VM; the orchestrator refuses endpoints
        // without a network, so both go.
        vm.NetworkEnabled = false;
        vm.PrimaryEndpointName = null;
        foreach (EndpointAnnotation endpoint in vm.Annotations.OfType<EndpointAnnotation>().ToList())
        {
            vm.Annotations.Remove(endpoint);
        }

        await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
        output.WriteLine("booted to Running without a network");

        // A network-less boot must not have created an HCN endpoint at all. Enumerated without
        // an owner filter: owners are run-scoped now, and a filtered query that names the wrong
        // owner would make this assertion pass vacuously.
        Assert.DoesNotContain(vm.HcnEndpointId, HcnClient.EnumerateEndpointIds());

        ExecuteCommandResult stopped = await app.ResourceCommands
            .ExecuteCommandAsync("appliance", KnownResourceCommands.StopCommand, cts.Token);
        Assert.True(stopped.Success, $"Stop failed: {stopped.Message}");
        await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Exited, cts.Token);

        // The shorter ledger — two grants and a work directory, no endpoint — must drain fully.
        Assert.False(Directory.Exists(workDir), $"Stop left the work directory behind: {workDir}");
        Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));

        await app.StopAsync(cts.Token);
    }
}
