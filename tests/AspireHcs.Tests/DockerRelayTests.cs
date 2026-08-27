using System.Runtime.Versioning;
using AspireHcs.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AspireHcs.Tests;

// The relay container is docker-CLI-managed, so the judgements that must never go wrong — what
// gets scavenged, what the socat script says, what `docker port` answered, what a recreate must
// preserve — are pure or driven through a fake docker CLI and pinned here without Docker. The
// live half is proven by the running scenario, not by tests.
[SupportedOSPlatform("windows10.0.17763")]
public class DockerRelayTests
{
    // ---- ownership: the name parse that licenses deletion ----

    [Theory]
    [InlineData("aspirehcs-relay-4242-0f3a9b2c")]   // current form: pid plus session suffix
    [InlineData("aspirehcs-relay-4242")]            // legacy form: pid alone, still reclaimed
    public void The_owner_pid_is_read_back_from_a_relay_name(string name)
    {
        Assert.Equal(4242, DockerRelay.OwnerProcessId(name));
    }

    // The docker name filter is a substring match, so foreign containers can be listed. A name
    // that is not exactly prefix-plus-pid(-suffix) proves nothing and licenses nothing.
    [Theory]
    [InlineData("aspirehcs-relay-")]                // no pid at all
    [InlineData("aspirehcs-relay-12x")]             // not a pid
    [InlineData("aspirehcs-relay--1")]              // signs rejected: NumberStyles.None
    [InlineData("aspirehcs-relay-4242-")]           // empty suffix
    [InlineData("aspirehcs-relay-4242-XYZ")]        // suffix this integration would not write
    [InlineData("aspirehcs-relay-4242-0f3a-9b2c")]  // a second separator
    [InlineData("my-aspirehcs-relay-4242")]         // prefix not at the start
    [InlineData("redis-cache")]                     // somebody else entirely
    public void A_name_this_integration_did_not_write_has_no_owner(string name)
    {
        Assert.Null(DockerRelay.OwnerProcessId(name));
    }

    [Fact]
    public void Scavenging_takes_only_owned_names_with_dead_pids()
    {
        string[] listed =
        [
            "aspirehcs-relay-1111-aaaa0000",    // dead → scavenged
            "aspirehcs-relay-1112",             // dead, legacy form → scavenged
            "aspirehcs-relay-2222-bbbb1111",    // alive → left alone
            "aspirehcs-relay-9999-cccc2222",    // this run's own → left alone
            "aspirehcs-relay-oops",             // not ours → left alone
        ];

        IEnumerable<string> scavengeable = DockerRelay.SelectScavengeable(
            listed, ownName: "aspirehcs-relay-9999-cccc2222", isProcessAlive: pid => pid == 2222);

        Assert.Equal(["aspirehcs-relay-1111-aaaa0000", "aspirehcs-relay-1112"], scavengeable);
    }

    // A sibling AppHost in this same process owns a relay under the same pid. Its name is not
    // this run's own, but its pid is alive — which is this run's pid — so it is protected.
    [Fact]
    public void A_live_sibling_relay_in_this_process_is_never_scavenged()
    {
        int pid = Environment.ProcessId;
        string sibling = $"aspirehcs-relay-{pid}-dddd3333";

        Assert.Empty(DockerRelay.SelectScavengeable(
            [sibling], ownName: $"aspirehcs-relay-{pid}-eeee4444", isProcessAlive: p => p == pid));
    }

    // Two AppHosts in one OS process must own distinct relays: same pid, different session
    // suffix, so neither can remove or recreate the other's container.
    [Fact]
    public void Two_relays_in_one_process_get_distinct_names_under_the_same_pid()
    {
        DockerRelay first = NewRelay();
        DockerRelay second = NewRelay();

        Assert.NotEqual(first.ContainerName, second.ContainerName);
        Assert.Equal(Environment.ProcessId, DockerRelay.OwnerProcessId(first.ContainerName));
        Assert.Equal(Environment.ProcessId, DockerRelay.OwnerProcessId(second.ContainerName));
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

    // ---- what a recreate must serve ----

    [Fact]
    public void A_recreate_serves_every_issued_target_plus_the_new_one()
    {
        Assert.Equal([5000, 6000, 65063], DockerRelay.DesiredTargets([65063, 5000], 6000));
    }

    [Fact]
    public void A_target_already_issued_is_not_doubled()
    {
        Assert.Equal([5000, 65063], DockerRelay.DesiredTargets([65063, 5000], 5000));
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

    // ---- the cache rules, driven through a fake docker CLI ----

    private static DockerRelay NewRelay() => new(new FakeLifetime(), NullLogger<DockerRelay>.Instance);

    /// <summary>
    /// A scripted docker CLI: answers version/ps/rm normally, delegates run/port/inspect to the
    /// test, and records every invocation.
    /// </summary>
    private sealed class FakeDocker
    {
        public List<string> Commands { get; } = [];

        public Func<IReadOnlyList<string>, string>? OnRun { get; set; }

        public Func<int, int>? PublishedPortFor { get; set; }

        public bool Running { get; set; }

        public Task<string> InvokeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool ignoreFailure)
        {
            Commands.Add(string.Join(' ', arguments));
            switch (arguments[0])
            {
                case "version":
                    return Task.FromResult("99.0.0\n");
                case "ps":
                    return Task.FromResult("");
                case "rm":
                    return Task.FromResult("");
                case "inspect":
                    return Running
                        ? Task.FromResult("true\n")
                        : throw new InvalidOperationException("docker inspect exited 1: no such container");
                case "run":
                    string result = OnRun?.Invoke(arguments) ?? "container-id\n";
                    Running = true;
                    return Task.FromResult(result);
                case "port":
                    int target = int.Parse(arguments[2].Split('/')[0], System.Globalization.CultureInfo.InvariantCulture);
                    int host = PublishedPortFor?.Invoke(target) ?? target + 1000;
                    return Task.FromResult($"0.0.0.0:{host}\n[::]:{host}\n");
                default:
                    throw new InvalidOperationException($"unexpected docker {arguments[0]}");
            }
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task A_cached_forward_is_returned_only_while_the_container_runs()
    {
        DockerRelay relay = NewRelay();
        FakeDocker docker = new();
        relay.UseDockerRunner(docker.InvokeAsync);

        int first = await relay.EnsurePublishedAsync(5000, CancellationToken.None);
        docker.Commands.Clear();

        // Container still running: the cache answers after one liveness probe, no recreate.
        int cached = await relay.EnsurePublishedAsync(5000, CancellationToken.None);
        Assert.Equal(first, cached);
        Assert.Equal(["inspect --format {{.State.Running}} " + relay.ContainerName], docker.Commands);

        // Container killed externally: the cache must not answer; the relay is recreated with
        // the issued port pinned, so the consumer's baked-in number still works. The readback
        // reports the pinned binding, as Docker would.
        docker.Running = false;
        docker.PublishedPortFor = _ => first;
        int recreated = await relay.EnsurePublishedAsync(5000, CancellationToken.None);

        Assert.Equal(first, recreated);
        Assert.Contains(docker.Commands, c => c.StartsWith("run", StringComparison.Ordinal) && c.Contains($"-p {first}:5000"));
    }

    [Fact]
    public async Task A_failed_recreate_leaves_no_claim_and_the_retry_keeps_issued_numbers()
    {
        DockerRelay relay = NewRelay();
        FakeDocker docker = new();
        relay.UseDockerRunner(docker.InvokeAsync);

        int issued = await relay.EnsurePublishedAsync(5000, CancellationToken.None);

        // Adding a second target fails at docker run. Nothing must be cached for either target.
        docker.OnRun = _ => throw new InvalidOperationException("docker run exited 125: port cannot bind");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.EnsurePublishedAsync(6000, CancellationToken.None));

        // A consumer retrying the FIRST target must not get the dead cache: the container was
        // removed by the failed replacement, so answering from memory is a black hole. The
        // retry recreates, pinning the issued number.
        docker.OnRun = null;
        docker.Running = false;
        int retried = await relay.EnsurePublishedAsync(5000, CancellationToken.None);

        Assert.Equal(issued, retried);
        Assert.Contains(docker.Commands, c => c.StartsWith("run", StringComparison.Ordinal) && c.Contains($"-p {issued}:5000"));
    }

    [Fact]
    public async Task A_late_target_recreates_with_the_superset_and_prior_ports_pinned()
    {
        DockerRelay relay = NewRelay();
        FakeDocker docker = new();
        relay.UseDockerRunner(docker.InvokeAsync);

        int first = await relay.EnsurePublishedAsync(5000, CancellationToken.None);
        docker.Commands.Clear();

        int second = await relay.EnsurePublishedAsync(6000, CancellationToken.None);

        Assert.NotEqual(first, second);
        string run = Assert.Single(docker.Commands, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains($"-p {first}:5000", run);      // issued number pinned
        Assert.Contains("-p 6000 ", run);              // new target left for Docker to choose
        Assert.Contains("TCP-LISTEN:5000", run);
        Assert.Contains("TCP-LISTEN:6000", run);
    }
}
