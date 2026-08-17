using System.Runtime.Versioning;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// Deletion requires proof of abandonment: a VM labelled with this integration's owner pid, and
// that pid gone. These pin the decision table; the window it protects (a VM created by a live
// AppHost that has not started it yet) cannot be reproduced deterministically against a real
// host.
//
// The classifier works on VMs and hcsctl labels: removing a VM removes its endpoint with it, so
// no endpoint exists with no VM to attribute it to.
[SupportedOSPlatform("windows10.0.17763")]
public class VmScavengingTests
{
    private const string OwnVmId = "11111111-1111-1111-1111-111111111111";
    private const string OtherVmId = "22222222-2222-2222-2222-222222222222";

    private static HcsCtlVmListDocument Listing(params HcsCtlVmRow[] rows)
        => new() { Ok = true, VirtualMachines = rows };

    private static HcsCtlVmRow Vm(string id, params (string Key, string Value)[] labels)
        => new() { Id = id, State = HcsCtlVmState.Running, Labels = labels.ToDictionary(l => l.Key, l => l.Value) };

    private static string[] Stale(HcsCtlVmListDocument listing, bool pidAlive)
        => [.. HcsVmOrchestrator.StaleVmIds(listing, OwnVmId, _ => pidAlive)];

    [Fact]
    public void Vm_labelled_with_a_dead_pid_is_stale()
    {
        string[] stale = Stale(Listing(Vm(OtherVmId, (HcsVmOrchestrator.OwnerPidLabel, "1234"))), pidAlive: false);
        Assert.Equal([OtherVmId], stale);
    }

    [Fact]
    public void Vm_labelled_with_a_live_pid_is_kept()
    {
        // The window this protects: the owning AppHost is alive and its VM may not have started.
        string[] stale = Stale(Listing(Vm(OtherVmId, (HcsVmOrchestrator.OwnerPidLabel, "1234"))), pidAlive: true);
        Assert.Empty(stale);
    }

    [Fact]
    public void Our_own_vm_is_never_stale()
    {
        // On a Restart the VM is recreated under the same id. Between the remove and the create
        // it would otherwise look exactly like a leftover.
        string[] stale = Stale(Listing(Vm(OwnVmId, (HcsVmOrchestrator.OwnerPidLabel, "1234"))), pidAlive: false);
        Assert.Empty(stale);
    }

    [Fact]
    public void Vm_without_our_label_is_left_alone()
    {
        // Someone else's VM in a shared store. No label of ours, so no claim over it, also when
        // it carries a label that looks similar.
        string[] stale = Stale(Listing(Vm(OtherVmId), Vm("33333333-3333-3333-3333-333333333333", ("owner", "1234"))), pidAlive: false);
        Assert.Empty(stale);
    }

    [Fact]
    public void Unparseable_pid_is_left_alone()
    {
        // A corrupt label is not proof of anything.
        foreach (string bad in new[] { "", "not-a-pid", "-1", "9999999999999999999" })
        {
            string[] stale = Stale(Listing(Vm(OtherVmId, (HcsVmOrchestrator.OwnerPidLabel, bad))), pidAlive: false);
            Assert.Empty(stale);
        }
    }

    [Fact]
    public void Rows_without_an_id_do_not_throw()
    {
        // hcsctl is Go: an omitted string arrives as null, not "".
        string[] stale = Stale(Listing(new HcsCtlVmRow { State = HcsCtlVmState.Running }), pidAlive: false);
        Assert.Empty(stale);
    }

    [Fact]
    public void This_process_is_alive_by_its_own_reckoning()
    {
        // The value the orchestrator stamps must be a pid the same check can find, or every
        // concurrent AppHost reclaims every other one's VMs.
        string[] stale = Stale(
            Listing(Vm(OtherVmId, (HcsVmOrchestrator.OwnerPidLabel, HcsVmOrchestrator.OwnerPidValue))),
            pidAlive: true);
        Assert.Empty(stale);
    }
}
