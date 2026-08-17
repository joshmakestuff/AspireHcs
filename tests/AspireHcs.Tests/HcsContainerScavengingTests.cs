using System.Runtime.Versioning;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// Containers outlive their AppHost: the compute system is host-global and hcsctl's state.json is
// on disk, so nothing reclaims a crashed run's container but this sweep. A concurrent AppHost's
// container must survive it.
//
// Deletion requires proof of abandonment: an id this integration wrote, whose recorded pid is
// dead. These pin every way that proof can fail to arrive.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerScavengingTests
{
    private static HcsCtlContainerListDocument Listing(params string[] ids) => new()
    {
        Ok = true,
        Containers = [.. ids.Select(id => new HcsCtlContainerRow { Id = id, State = "Running" })],
    };

    private const string Dead = "aspirehcs-4242-web-0123456789abcdef0123456789abcdef";
    private const string Live = "aspirehcs-1111-web-fedcba9876543210fedcba9876543210";

    private static bool IsAlive(int pid) => pid == 1111;

    [Fact]
    public void A_container_from_a_dead_run_is_scavenged()
    {
        Assert.Equal([Dead], HcsContainerOrchestrator.SelectScavengeable(Listing(Dead), "own", IsAlive));
    }

    // Two developers, or two AppHosts, on one machine.
    [Fact]
    public void A_container_owned_by_a_live_process_is_left_alone()
    {
        Assert.Empty(HcsContainerOrchestrator.SelectScavengeable(Listing(Live), "own", IsAlive));
    }

    [Fact]
    public void This_run_never_scavenges_itself()
    {
        // Its own pid is alive anyway, but the identity check must not depend on that: during a
        // Restart the container is recreated and must not be swept by its own boot.
        Assert.Empty(HcsContainerOrchestrator.SelectScavengeable(Listing(Dead), Dead, IsAlive));
    }

    // The prefix is not a licence to delete. Something else may adopt a similar-looking id, and
    // an id with no parseable pid carries no proof of anything.
    [Theory]
    [InlineData("servercore-smoke-test")]                 // someone at a shell
    [InlineData("aspirehcs")]                             // prefix, nothing else
    [InlineData("aspirehcs-")]                            // prefix and a separator
    [InlineData("aspirehcs-notapid-web")]                 // unparseable pid
    [InlineData("aspirehcs--web")]                        // empty pid
    [InlineData("aspirehcs-99999999999999999999-web")]    // pid that overflows an int
    [InlineData("aspirehcs-+42-web")]                     // sign is not a digit
    [InlineData("aspirehcsX4242Xweb")]                    // prefix without the separator
    public void An_id_this_integration_did_not_write_is_never_scavenged(string id)
    {
        Assert.Empty(HcsContainerOrchestrator.SelectScavengeable(Listing(id), "own", IsAlive));
    }

    [Fact]
    public void A_row_with_no_id_is_skipped_rather_than_throwing()
    {
        HcsCtlContainerListDocument listing = new()
        {
            Ok = true,
            Containers = [new HcsCtlContainerRow { Id = null }, new HcsCtlContainerRow { Id = "" }],
        };

        Assert.Empty(HcsContainerOrchestrator.SelectScavengeable(listing, "own", IsAlive));
    }

    [Fact]
    public void A_mixed_listing_scavenges_only_the_abandoned()
    {
        Assert.Equal([Dead], HcsContainerOrchestrator.SelectScavengeable(
            Listing(Live, Dead, "docker-something", "own"), "own", IsAlive));
    }

    // The id must round-trip: scavenging reads back the pid the resource wrote. If the two
    // disagree, every leftover is unattributable and nothing is reclaimed.
    [Fact]
    public void A_generated_id_carries_this_process_id_back()
    {
        HcsContainerResource resource = new("web");

        Assert.Equal(Environment.ProcessId, HcsContainerResource.OwnerProcessId(resource.ContainerId));
    }

    // hcsctl joins the id into a filesystem path under its store, so a resource name carrying a
    // separator would escape it.
    [Fact]
    public void A_generated_id_has_no_path_separators()
    {
        HcsContainerResource resource = new("web-api.v2");

        Assert.DoesNotContain(resource.ContainerId, c => c is '\\' or '/' or ':' or '.');
        Assert.Equal(Environment.ProcessId, HcsContainerResource.OwnerProcessId(resource.ContainerId));
    }

    // Teardown is asynchronous, so a previous run's dying container can still exist. Two
    // resources of the same name in the same process must not collide either.
    [Fact]
    public void Two_resources_with_the_same_name_get_different_ids()
    {
        Assert.NotEqual(new HcsContainerResource("web").ContainerId, new HcsContainerResource("web").ContainerId);
    }
}
