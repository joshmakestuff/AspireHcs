using System.Runtime.Versioning;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// #62. The relay container is docker-CLI-managed, so the judgements that must never go wrong —
// what gets scavenged, what the socat script says, what `docker port` answered — are pure and
// pinned here without Docker. The live half is proven by the running scenario, not by tests.
[SupportedOSPlatform("windows10.0.17763")]
public class DockerRelayTests
{
    // ---- ownership: the name parse that licenses deletion ----

    [Fact]
    public void The_owner_pid_is_read_back_from_a_relay_name()
    {
        Assert.Equal(4242, DockerRelay.OwnerProcessId("aspirehcs-relay-4242"));
    }

    // The docker name filter is a substring match, so foreign containers can be listed. A name
    // that is not exactly prefix-plus-pid proves nothing and licenses nothing.
    [Theory]
    [InlineData("aspirehcs-relay-")]            // no pid at all
    [InlineData("aspirehcs-relay-12x")]         // not a pid
    [InlineData("aspirehcs-relay--1")]          // signs rejected: NumberStyles.None
    [InlineData("my-aspirehcs-relay-4242")]     // prefix not at the start
    [InlineData("redis-cache")]                 // somebody else entirely
    public void A_name_this_integration_did_not_write_has_no_owner(string name)
    {
        Assert.Null(DockerRelay.OwnerProcessId(name));
    }

    [Fact]
    public void Scavenging_takes_only_owned_names_with_dead_pids()
    {
        string[] listed =
        [
            "aspirehcs-relay-1111",     // dead → scavenged
            "aspirehcs-relay-2222",     // alive → left alone
            "aspirehcs-relay-9999",     // this run's own → left alone
            "aspirehcs-relay-oops",     // not ours → left alone
        ];

        IEnumerable<string> scavengeable = DockerRelay.SelectScavengeable(
            listed, ownName: "aspirehcs-relay-9999", isProcessAlive: pid => pid == 2222);

        Assert.Equal(["aspirehcs-relay-1111"], scavengeable);
    }

    // ---- the multiplexing script ----

    [Fact]
    public void The_script_runs_one_socat_per_target_and_waits()
    {
        Assert.Equal(
            "socat TCP-LISTEN:65063,fork,reuseaddr TCP:host.docker.internal:65063 & " +
            "socat TCP-LISTEN:5488,fork,reuseaddr TCP:host.docker.internal:5488 & wait",
            DockerRelay.BuildScript([65063, 5488]));
    }

    [Fact]
    public void A_script_with_no_targets_is_refused()
    {
        // A relay forwarding nothing is a bug in the caller, not a container worth running.
        Assert.Throws<ArgumentException>(() => DockerRelay.BuildScript([]));
    }

    // ---- reading the published port back ----

    // Real `docker port` output: one binding per line, IPv4 first, both on the same number.
    [Theory]
    [InlineData("0.0.0.0:55007\n[::]:55007\n")]
    [InlineData("0.0.0.0:55007\r\n[::]:55007\r\n")]
    public void The_host_port_is_read_from_the_first_binding(string output)
    {
        Assert.Equal(55007, DockerRelay.ParsePublishedPort(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("Error: no public port published\n")]
    [InlineData("0.0.0.0:notaport\n")]
    public void Unreadable_output_fails_naming_what_docker_said(string output)
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => DockerRelay.ParsePublishedPort(output));

        Assert.Contains("docker port", thrown.Message);
    }
}
