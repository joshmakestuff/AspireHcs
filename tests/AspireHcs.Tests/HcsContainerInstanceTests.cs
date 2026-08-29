using System.Runtime.Versioning;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// Pause is gated on hcsctl's exec started record: the resource reports Running before the
// detached HcsCreateProcess lands, and pausing inside that window freezes the guest first
// (AspireHcs#74). The record latches a per-boot TaskCompletionSource; these pin what the wait
// does with each way that latch can end.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerInstanceTests
{
    private static Task WaitAsync(Task<bool> workloadStarted, TimeSpan? timeout = null) =>
        HcsContainerInstance.WaitForWorkloadStartAsync(
            workloadStarted, "hcsworker", timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task The_wait_completes_when_the_started_record_lands()
    {
        TaskCompletionSource<bool> latch = new();
        Task wait = WaitAsync(latch.Task);

        Assert.False(wait.IsCompleted);
        latch.SetResult(true);

        await wait;
    }

    // A started record that never arrives must fail the pause, naming the resource and the
    // refusal, rather than either pausing early or waiting forever.
    [Fact]
    public async Task Expiry_names_the_resource_and_the_refusal()
    {
        TaskCompletionSource<bool> latch = new();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(latch.Task, timeout: TimeSpan.Zero));

        Assert.Contains("'hcsworker'", ex.Message);
        Assert.Contains("refusing to pause before the workload starts", ex.Message);
    }

    // A workload that ends without ever creating its process completes the latch false. The wait
    // must fail immediately — not hold the pause for the full timeout.
    [Fact]
    public async Task A_workload_that_ended_without_starting_fails_the_wait_at_once()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(Task.FromResult(false), timeout: TimeSpan.FromDays(1)));

        Assert.Contains("'hcsworker'", ex.Message);
        Assert.Contains("nothing to pause", ex.Message);
    }
}
