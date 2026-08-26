using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// The relay that lets an HCS guest reach Aspire endpoints bound to the host's loopback. One
/// hidden Docker container per AppHost session, running one <c>socat</c> forwarder per relayed
/// endpoint; each forwarder is published on <c>0.0.0.0</c> and dials
/// <c>host.docker.internal:&lt;target&gt;</c>, which reaches host loopback from inside Docker.
/// The chain is measured end to end from inside a VM (HTTP 200 from a loopback-bound Kestrel):
/// guest → HNS gateway → docker-published port → socat → host.docker.internal → target.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a docker-CLI-managed container, not a hidden Aspire container resource.
/// Two facts force that. DCP publishes every port of a container it owns at
/// <c>HostIp 127.0.0.1</c> — its request, not Docker's default, verified by <c>docker inspect</c>
/// — so an Aspire-owned relay could never get the <c>0.0.0.0</c> bind that is its entire job.
/// And the ports the relay must publish are the referenced endpoints' host ports, which exist
/// only after DCP has started its proxies — long past the time a model-declared container has to
/// fix its port list.
/// </para>
/// <para>
/// One multiplexing container, mirroring Aspire's own tunnel shape (one <c>aspire</c> container
/// carrying all endpoints). Docker cannot add published ports to a running container, so a
/// target that arrives after the relay exists recreates it with the superset — keeping every
/// already-issued relay port on its number, so values injected into earlier consumers stay
/// valid. The recreate drops live relayed connections for the moment it takes; consumers created
/// after it see nothing.
/// </para>
/// <para>
/// The container is named with this process's id plus a per-session suffix — two AppHosts hosted
/// in one process get distinct relays — and scavenged by the same discipline as HCS containers:
/// a name this integration wrote, whose recorded pid is not running, and nothing else.
/// </para>
/// </remarks>
internal sealed class DockerRelay(IHostApplicationLifetime lifetime, ILogger<DockerRelay> logger)
{
    /// <summary>
    /// Identifies relay containers this integration owns; the pid in the name makes ownership
    /// provable, exactly as <see cref="Aspire.Hosting.ApplicationModel.HcsContainerResource.IdPrefix"/> does for HCS containers.
    /// </summary>
    internal const string NamePrefix = "aspirehcs-relay-";

    /// <summary>The relay image. <c>socat</c> is the whole requirement.</summary>
    internal const string Image = "alpine/socat";

    /// <summary>Serializes creation, recreation and scavenging across concurrently booting consumers.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Target host port → the relay port ever handed to a consumer. Never cleared: an issued
    /// number is baked into environments consumers already received, so every recreate pins it.
    /// Mutated only under the gate.
    /// </summary>
    private readonly Dictionary<int, int> _issued = [];

    /// <summary>
    /// The subset of <see cref="_issued"/> the running container is known to serve. Cleared
    /// pessimistically — before any mutation of the container, and whenever the container turns
    /// out to be gone — so a failure can never leave a claim of a live forward that nothing
    /// answers. Mutated only under the gate.
    /// </summary>
    private readonly Dictionary<int, int> _published = [];

    private bool _initialized;

    /// <summary>The docker CLI, injectable for tests. Signature: arguments, token, ignoreFailure → stdout.</summary>
    private Func<IReadOnlyList<string>, CancellationToken, bool, Task<string>> _runDocker = RunDockerCliAsync;

    internal void UseDockerRunner(Func<IReadOnlyList<string>, CancellationToken, bool, Task<string>> runner)
        => _runDocker = runner;

    internal string ContainerName { get; } = FormattableString.Invariant(
        $"{NamePrefix}{Environment.ProcessId}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}");

    /// <summary>
    /// Ensures the relay forwards <paramref name="targetPort"/> on the host's loopback, and
    /// returns the <c>0.0.0.0</c>-published host port a guest reaches it at via its gateway.
    /// </summary>
    /// <remarks>
    /// The published port is Docker's to choose: it owns the bind, so there is no window between
    /// picking a free port and claiming it. It is read back with <c>docker port</c> after
    /// create. A cached forward is verified against the live container before it is trusted —
    /// the container is host state anything can remove.
    /// </remarks>
    public async Task<int> EnsurePublishedAsync(int targetPort, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_published.TryGetValue(targetPort, out int existing))
            {
                if (await IsRelayRunningAsync(cancellationToken).ConfigureAwait(false))
                {
                    return existing;
                }

                // The container died or was removed under us: nothing in the map is served any
                // more, whatever created the entries.
                _published.Clear();
            }

            if (!_initialized)
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }

            // From here the container is being replaced, so no forward is live until the new one
            // stands. Cleared BEFORE the removal: a failure anywhere below must leave no claim a
            // later call would trust, only issued numbers to pin on the retry.
            _published.Clear();

            // Recreate rather than create: Docker has no way to add a published port to a running
            // container. Removal of a name that does not exist yet is the ordinary first-time path.
            await _runDocker(["rm", "-f", ContainerName], cancellationToken, true).ConfigureAwait(false);

            int[] targets = [.. DesiredTargets(_issued.Keys, targetPort)];
            List<string> arguments = ["run", "-d", "--name", ContainerName, "--entrypoint", "/bin/sh"];
            foreach (int target in targets)
            {
                // Ports already handed out keep their numbers across the recreate; only a target
                // never issued before lets Docker pick.
                arguments.Add("-p");
                arguments.Add(_issued.TryGetValue(target, out int pinned)
                    ? FormattableString.Invariant($"{pinned}:{target}")
                    : target.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add(Image);
            arguments.Add("-c");
            arguments.Add(BuildScript(targets));

            await _runDocker(arguments, cancellationToken, false).ConfigureAwait(false);

            // Every forward is read back from the container that now serves it, not assumed from
            // the pins: _published holds only what docker port confirms.
            foreach (int target in targets)
            {
                string mapping = await _runDocker(
                    ["port", ContainerName, FormattableString.Invariant($"{target}/tcp")], cancellationToken, false)
                    .ConfigureAwait(false);
                int hostPort = ParsePublishedPort(mapping);
                _published[target] = hostPort;
                _issued[target] = hostPort;
            }

            logger.LogInformation(
                "Relay {ContainerName} publishes 0.0.0.0:{HostPort} -> host.docker.internal:{TargetPort} ({Count} forward(s) total).",
                ContainerName, _published[targetPort], targetPort, _published.Count);

            return _published[targetPort];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The forwards the next container must serve: everything ever issued plus the new target,
    /// ascending. Pure, so the recreate-keeps-prior-forwards rule is testable without Docker.
    /// </summary>
    internal static IEnumerable<int> DesiredTargets(IEnumerable<int> issued, int newTarget)
        => issued.Append(newTarget).Distinct().Order();

    /// <summary>The cached map is only as good as the container behind it.</summary>
    private async Task<bool> IsRelayRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            string state = await _runDocker(
                ["inspect", "--format", "{{.State.Running}}", ContainerName], cancellationToken, false)
                .ConfigureAwait(false);
            return state.Trim() == "true";
        }
        catch (InvalidOperationException)
        {
            // No such container — inspect fails rather than reporting a state.
            return false;
        }
    }

    /// <summary>
    /// First-use work: prove Docker answers, reclaim relays left by dead AppHosts, and register
    /// this session's teardown. Done lazily so an AppHost whose HCS resources reference nothing
    /// never requires Docker at all.
    /// </summary>
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A running daemon, not merely a CLI on PATH: `docker version` with a server format
            // fails when the engine is down, which `docker --version` would not notice.
            await _runDocker(["version", "--format", "{{.Server.Version}}"], cancellationToken, false)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "WithReference on an HCS resource needs Docker: the endpoints it references are " +
                "bound to the host's loopback, which an HCS guest cannot reach, so AspireHcs " +
                "relays them through a Docker container. Start Docker Desktop (or another " +
                $"engine with a docker-compatible CLI on PATH) and retry. {ex.Message}", ex);
        }

        await ScavengeAbandonedRelaysAsync(cancellationToken).ConfigureAwait(false);

        // Registered once, on first use rather than per forward. The callback cannot await, so
        // the removal is synchronous and bounded — the same shape as the HCS teardown hooks.
        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
                _runDocker(["rm", "-f", ContainerName], timeout.Token, false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Removing relay container {ContainerName} failed; the next run will scavenge it.",
                    ContainerName);
            }
        });
    }

    /// <summary>
    /// Removes relay containers left behind by dead AppHost processes. Docker restarts do not
    /// reclaim them — the relay runs detached — so nothing does but this.
    /// </summary>
    private async Task ScavengeAbandonedRelaysAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ORDER MATTERS, same argument as the HCS container sweep: names are enumerated
            // BEFORE the pid snapshot, so a recycled pid can only make a dead run look alive
            // (deferring removal), never a live run look dead.
            string listing = await _runDocker(
                ["ps", "-a", "--filter", $"name={NamePrefix}", "--format", "{{.Names}}"], cancellationToken, false)
                .ConfigureAwait(false);
            string[] names = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0)
            {
                return;
            }

            HashSet<int> livePids = [.. Process.GetProcesses().Select(static p => p.Id)];

            foreach (string name in SelectScavengeable(names, ContainerName, livePids.Contains))
            {
                // Guarded per container: concurrent AppHosts may sweep the same leftovers, and
                // losing that race on one must not abort the rest of the sweep.
                try
                {
                    logger.LogInformation("Scavenging relay container {ContainerName} left by a dead run.", name);
                    await _runDocker(["rm", "-f", name], cancellationToken, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping relay container {ContainerName} during scavenging.", name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scavenging abandoned relay containers failed; continuing.");
        }
    }

    /// <summary>
    /// Decides which listed relay containers are abandoned leftovers. Pure, so the rules are
    /// testable without Docker: anything not named by this integration, owned by a live process,
    /// or belonging to this run is left alone. A live pid protects every relay it owns — a
    /// sibling AppHost in this same process included.
    /// </summary>
    internal static IEnumerable<string> SelectScavengeable(
        IEnumerable<string> names, string ownName, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (string name in names)
        {
            if (string.Equals(name, ownName, StringComparison.Ordinal))
            {
                continue;
            }

            // The docker filter is a substring match, so a foreign container whose name merely
            // contains the prefix can be listed. The name is not a licence to delete; the pid is.
            if (OwnerProcessId(name) is not { } pid)
            {
                continue;
            }

            if (isProcessAlive(pid))
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>
    /// Extracts the owning process id from a relay container's name, or null if this integration
    /// did not write it. The name is <c>aspirehcs-relay-&lt;pid&gt;-&lt;hex&gt;</c>; the bare
    /// <c>aspirehcs-relay-&lt;pid&gt;</c> form earlier builds wrote is accepted too, so their
    /// leftovers still get reclaimed. Anything else — <c>aspirehcs-relay-123x</c>, a malformed
    /// suffix — is somebody else's container, never a candidate.
    /// </summary>
    internal static int? OwnerProcessId(string? name)
    {
        if (name is null || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = name.AsSpan(NamePrefix.Length);
        int separator = rest.IndexOf('-');

        ReadOnlySpan<char> suffix = separator < 0 ? [] : rest[(separator + 1)..];
        if (separator >= 0 && (suffix.IsEmpty || suffix.ContainsAnyExcept(LowercaseHex)))
        {
            return null;
        }

        ReadOnlySpan<char> pidSpan = separator < 0 ? rest : rest[..separator];
        return int.TryParse(pidSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            ? pid
            : null;
    }

    private static readonly System.Buffers.SearchValues<char> LowercaseHex =
        System.Buffers.SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// The shell line the relay runs: one backgrounded <c>socat</c> per forwarded port, then
    /// <c>wait</c> so the container lives as long as its forwarders do. Inside the container
    /// each listener uses the target's own port number; the numbers are distinct by construction
    /// and nothing else runs in the container to collide with.
    /// </summary>
    internal static string BuildScript(IEnumerable<int> targetPorts)
    {
        ArgumentNullException.ThrowIfNull(targetPorts);

        StringBuilder script = new();
        foreach (int port in targetPorts)
        {
            script.Append(CultureInfo.InvariantCulture,
                $"socat TCP-LISTEN:{port},fork,reuseaddr TCP:host.docker.internal:{port} & ");
        }

        if (script.Length == 0)
        {
            throw new ArgumentException("At least one target port is required.", nameof(targetPorts));
        }

        return script.Append("wait").ToString();
    }

    /// <summary>
    /// Reads the host port out of <c>docker port</c> output — one binding per line, e.g.
    /// <c>0.0.0.0:55007</c>, with an IPv6 sibling below it. The first line's port is the answer;
    /// Docker publishes both families on the same number.
    /// </summary>
    internal static int ParsePublishedPort(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        int separator = first?.LastIndexOf(':') ?? -1;

        if (first is null || separator < 0
            || !int.TryParse(first.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"docker port reported '{output.Trim()}', which does not contain a host port. " +
                "The relay's published port cannot be discovered, so no guest-reachable address exists for it.");
        }

        return port;
    }

    /// <summary>
    /// Runs one docker CLI command and returns its stdout. The only place this class starts a
    /// process, mirroring <see cref="Cli.HcsCtl"/>'s role for hcsctl.
    /// </summary>
    private static async Task<string> RunDockerCliAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool ignoreFailure)
    {
        ProcessStartInfo startInfo = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Starting 'docker' failed.");
            }
        }
        catch (Win32Exception ex)
        {
            // The CLI is not even on PATH. The caller that reaches first use wraps this into the
            // message naming the dependency; later calls should never get here.
            throw new InvalidOperationException($"Starting 'docker' failed: {ex.Message}", ex);
        }

        // Both streams drained concurrently — draining one to completion first deadlocks as soon
        // as the child fills the pipe nothing is reading. Same trap as running hcsctl.
        Task<string> readStdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> readStderr = process.StandardError.ReadToEndAsync(cancellationToken);

        string stdout;
        string stderr;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stdout = await readStdout.ConfigureAwait(false);
            stderr = await readStderr.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone between the check and the kill.
            }

            throw;
        }

        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
