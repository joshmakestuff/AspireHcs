using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Hcn;
using AspireHcs.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #15 acceptance: AppHost shutdown while a boot is in flight must not leak anything the
// boot had acquired by that point — no HCN endpoint, no ACL grant on the base image, no
// copy-on-write work directory. The delays land the shutdown in different boot phases (around
// endpoint creation and grants, just after HCS start, and mid guest-ready wait); whichever
// phase is hit, the invariants are the same. The interleaving is inherently timing-dependent —
// what is deterministic is that no timing is allowed to leak.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ShutdownDuringStartupTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(5000)]
    public async Task Shutdown_during_startup_leaves_nothing_behind(int delayMs)
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

        List<string> statesSeen = [];
        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);

            // Watching the state stream is what proves the shutdown really landed mid-boot: if
            // the resource ever reached Running before StopAsync, these cases would be quietly
            // exercising an ordinary post-start shutdown instead of the race they claim to.
            using CancellationTokenSource watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            Task watcher = Task.Run(async () =>
            {
                try
                {
                    await foreach (ResourceEvent resourceEvent in app.ResourceNotifications.WatchAsync(watchCts.Token))
                    {
                        if (resourceEvent.Resource.Name == "appliance" && resourceEvent.Snapshot.State?.Text is { Length: > 0 } state)
                        {
                            lock (statesSeen)
                            {
                                statesSeen.Add(state);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // The watch is cancelled once StopAsync returns; everything relevant is recorded.
                }
            }, cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync("appliance", KnownResourceStates.Starting, cts.Token);
            await Task.Delay(delayMs, cts.Token);
            output.WriteLine($"stopping the AppHost {delayMs} ms into the boot");
            await app.StopAsync(cts.Token);

            watchCts.Cancel();
            await watcher.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        }

        lock (statesSeen)
        {
            output.WriteLine($"states observed: {string.Join(" -> ", statesSeen)}");
            // Starting must have been observed for DoesNotContain to mean anything — WatchAsync
            // replays each resource's current snapshot on subscribe, so an empty or Starting-less
            // list means the watcher never really ran, not that the states never happened.
            Assert.Contains(KnownResourceStates.Starting, statesSeen);
            Assert.DoesNotContain(KnownResourceStates.Running, statesSeen);
        }

        Assert.False(Directory.Exists(workDir), $"copy-on-write work directory leaked: {workDir}");
        Assert.DoesNotContain(vm.HcnEndpointId, HcnClient.EnumerateEndpointIds(HcsVmOrchestrator.HcnOwner));
        Assert.Equal(aclBefore, TeardownProbes.ReadAcl(vhdx!));
    }
}
