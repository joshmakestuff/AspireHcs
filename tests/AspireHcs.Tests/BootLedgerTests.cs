using System.Runtime.Versioning;
using AspireHcs.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AspireHcs.Tests;

// The ledger is what makes teardown correctness independent of how far a boot got (#15/#16/#17):
// every acquisition records its release, drains run in reverse order with per-entry isolation,
// and an acquisition that loses the race against AppHost shutdown releases itself. These pin
// each of those properties directly, since no integration test can exercise the interleavings
// deterministically.
[SupportedOSPlatform("windows10.0.17763")]
public class BootLedgerTests
{
    [Fact]
    public void Drain_releases_in_reverse_acquisition_order()
    {
        BootLedger ledger = new(NullLogger.Instance);
        List<string> released = [];

        ledger.Add("first", () => released.Add("first"));
        ledger.Add("second", () => released.Add("second"));
        ledger.Add("third", () => released.Add("third"));

        ledger.Drain();

        Assert.Equal(["third", "second", "first"], released);
    }

    [Fact]
    public void A_failing_release_does_not_stop_the_rest()
    {
        BootLedger ledger = new(NullLogger.Instance);
        List<string> released = [];

        ledger.Add("first", () => released.Add("first"));
        ledger.Add("second", () => throw new UnauthorizedAccessException("cleanup denied"));
        ledger.Add("third", () => released.Add("third"));

        ledger.Drain();

        Assert.Equal(["third", "first"], released);
    }

    [Fact]
    public void Drain_is_idempotent()
    {
        BootLedger ledger = new(NullLogger.Instance);
        int releases = 0;
        ledger.Add("resource", () => releases++);

        ledger.Drain();
        ledger.Drain();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void Add_after_drain_releases_immediately_and_cancels_the_boot()
    {
        BootLedger ledger = new(NullLogger.Instance);
        ledger.Drain();

        bool released = false;
        Assert.Throws<OperationCanceledException>(() => ledger.Add("late straggler", () => released = true));
        Assert.True(released);
    }

    [Fact]
    public void A_failing_self_release_after_drain_still_cancels_the_boot()
    {
        BootLedger ledger = new(NullLogger.Instance);
        ledger.Drain();

        // The boot must unwind even when the immediate release itself fails; the failure is
        // logged like any other release failure rather than replacing the cancellation.
        Assert.Throws<OperationCanceledException>(
            () => ledger.Add("late straggler", () => throw new InvalidOperationException("release failed")));
    }

    [Fact]
    public async Task Concurrent_drains_release_each_entry_exactly_once()
    {
        // The AppHost shutdown hook can drain concurrently with a cancelled boot's own cleanup.
        // Whatever the interleaving, an entry must never be released twice (a double
        // HcnDeleteEndpoint or Dispose is exactly the class of bug the ledger exists to prevent).
        for (int round = 0; round < 100; round++)
        {
            BootLedger ledger = new(NullLogger.Instance);
            int[] releases = new int[8];
            for (int i = 0; i < releases.Length; i++)
            {
                int index = i;
                ledger.Add($"resource {index}", () => Interlocked.Increment(ref releases[index]));
            }

            using Barrier barrier = new(2);
            Task first = Task.Run(() => { barrier.SignalAndWait(); ledger.Drain(); });
            Task second = Task.Run(() => { barrier.SignalAndWait(); ledger.Drain(); });
            await Task.WhenAll(first, second);

            Assert.All(releases, count => Assert.Equal(1, count));
        }
    }
}
