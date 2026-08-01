using System.Runtime.Versioning;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// The scavenging classifier is what closed #12: deletion requires proof of abandonment (a dead
// owner pid, or the legacy bare owner with no compute system attached) rather than the racy
// "no VM yet" heuristic. These pin the decision table, since the race window it protects —
// endpoint created, compute system not yet — cannot be reproduced deterministically in an
// integration test.
[SupportedOSPlatform("windows10.0.17763")]
public class EndpointScavengingTests
{
    private static bool IsStale(
        string? owner,
        string? attachedVmRuntimeId = null,
        bool pidAlive = false,
        bool vmRunning = false)
        => HcsVmOrchestrator.IsStaleAspireHcsEndpoint(
            owner,
            attachedVmRuntimeId,
            isProcessAlive: _ => pidAlive,
            isVmRunning: _ => vmRunning);

    [Fact]
    public void Run_scoped_owner_with_dead_pid_is_stale()
    {
        Assert.True(IsStale("AspireHcs:1234", pidAlive: false));
    }

    [Fact]
    public void Run_scoped_owner_with_live_pid_is_kept_even_without_a_vm()
    {
        // The #12 window: the owning AppHost is alive but its compute system does not exist yet.
        Assert.False(IsStale("AspireHcs:1234", attachedVmRuntimeId: null, pidAlive: true));
    }

    [Fact]
    public void Legacy_bare_owner_without_a_running_vm_is_stale()
    {
        Assert.True(IsStale("AspireHcs", attachedVmRuntimeId: null));
    }

    [Fact]
    public void Legacy_bare_owner_with_a_running_vm_is_kept()
    {
        Assert.False(IsStale("AspireHcs", attachedVmRuntimeId: "some-runtime-id", vmRunning: true));
    }

    [Fact]
    public void An_endpoint_attached_to_a_running_vm_is_never_stale_regardless_of_owner_pid()
    {
        // Belt and braces: even if attribution says the run is dead, an endpoint a running
        // compute system is using must not have its NIC stripped.
        Assert.False(IsStale("AspireHcs:1234", attachedVmRuntimeId: "some-runtime-id", pidAlive: false, vmRunning: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Docker")]
    [InlineData("AspireHcs.IntegrationTests")]
    public void Foreign_or_absent_owners_are_never_stale(string? owner)
    {
        Assert.False(IsStale(owner));
    }

    [Theory]
    [InlineData("AspireHcs:")]
    [InlineData("AspireHcs:abc")]
    [InlineData("AspireHcs:12x4")]
    [InlineData("AspireHcs:-1")]
    [InlineData("AspireHcs: 1234")]
    [InlineData("aspirehcs:1234")]
    public void Unattributable_owner_variants_are_never_stale(string owner)
    {
        // A suffix that does not parse as a bare pid means the endpoint cannot be attributed to
        // a run; deleting on a guess is exactly the class of bug #12 was.
        Assert.False(IsStale(owner));
    }

    [Fact]
    public void The_owner_this_process_writes_is_attributed_back_to_this_process()
    {
        // Round-trip pin: the format RunHcnOwner writes must be the format the classifier
        // parses, and the pid it parses must be this process's.
        int parsedPid = -1;
        bool stale = HcsVmOrchestrator.IsStaleAspireHcsEndpoint(
            HcsVmOrchestrator.RunHcnOwner,
            attachedVmRuntimeId: null,
            isProcessAlive: pid => { parsedPid = pid; return false; },
            isVmRunning: _ => false);

        Assert.True(stale);
        Assert.Equal(Environment.ProcessId, parsedPid);
    }
}
