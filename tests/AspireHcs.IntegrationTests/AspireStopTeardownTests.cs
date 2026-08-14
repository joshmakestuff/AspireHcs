using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #66, general half: `aspire stop` must tear down every resource kind, and one resource's
// teardown failure must not strand its siblings. The relay-specific repro (externally killed
// Docker containers) follows the relay to withreference-relay; this pins the half that stays on
// main — both kinds reach Running, one stop removes both, and neither survives in any state.
//
// The assertion runs through HcsCtlProbes, an independent process path that parses hcsctl's JSON
// directly rather than the product's own typed binding — so a leak in the binding cannot hide a
// leak in the host.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class AspireStopTeardownTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Stop_tears_down_both_resource_kinds_and_leaves_nothing_behind()
    {
        string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        string? image = Environment.GetEnvironmentVariable(ContainerFixture.ImageVariable);
        string? store = Environment.GetEnvironmentVariable(ContainerFixture.StoreVariable);
        Skip.If(string.IsNullOrEmpty(vhdx) || string.IsNullOrEmpty(image) || string.IsNullOrEmpty(store),
            "Set HCS_TEST_VHDX, ASPIREHCS_TEST_IMAGE and ASPIREHCS_TEST_STORE to run the stop-teardown test.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(10));

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);
        HcsVirtualMachineResource vm = Assert.Single(appHost.Resources.OfType<HcsVirtualMachineResource>());
        HcsContainerResource container = Assert.Single(appHost.Resources.OfType<HcsContainerResource>());

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Running, cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);
            output.WriteLine("both resources reached Running");

            await app.StopAsync(cts.Token);
            output.WriteLine("AppHost stopped");
        }

        // ABSENCE, never a return code. Neither resource may survive the stop, in any state —
        // a VM keeps its store record as "stopped", and a container's scratch is "absent" only
        // when actually removed.
        Assert.DoesNotContain(vm.VmId, HcsCtlProbes.VmIds(vm.StorePath));
        Assert.DoesNotContain(container.ContainerId, HcsCtlProbes.ContainerIds(container.StorePath));
    }
}
