using System.Runtime.Versioning;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AspireHcs.Tests;

// The pause race (issue #74): pausing between Running publication and the workload's
// HcsCreateProcess makes that create fail 0x80370105/0xc0370105, and the failure used to publish
// Exited over Paused, after which resume was refused. The recovery (O3 classify + O2 resume) sits
// behind the pause gate as defense in depth: the create failure is recognized as invalid-state
// while paused, the container is resumed, Running is published again and the exec is
// re-dispatched exactly once. These pin the decision (ShouldRetryWorkload) and the whole loop
// (RunWorkloadWithRecoveryAsync) with a stand-in hcsctl whose exec fails on demand — no hcsctl,
// no HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class WorkloadRecoveryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    private const string ContainerId = "cont-1";
    private const string Command = "cmd /c ping -t";

    /// <summary>A realistic hcsctl failure text for HCS_E_INVALID_STATE, as the create would report it.</summary>
    private const string InvalidStateError =
        "CreateProcess(\\\"cmd /c ping -t\\\") failed: the compute system is in an invalid state (0xc0370105)";

    private const string OtherError =
        "CreateProcess(\\\"cmd /c ping -t\\\") failed: the data is invalid (0x8007000d)";

    /// <summary>
    /// A stand-in hcsctl: the first <c>container exec</c> fails with
    /// <paramref name="firstExecFailure"/> embedded in its failure document; a
    /// <c>container resume</c> succeeds and marks a file; the second exec succeeds with exit code
    /// 0, or fails with <paramref name="retryFailure"/> when one is given. Anything else fails
    /// loudly (exit 99).
    /// </summary>
    private HcsCtl FakeCtl(string firstExecFailure, string? retryFailure = null)
    {
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.cmd");
        string resumeMarker = Path.Combine(_directory, "resume-called.marker");
        string execMarker = Path.Combine(_directory, "exec-ran.marker");
        string firstFailureDoc = $"{{\"ok\":false,\"error\":\"{firstExecFailure}\",\"stage\":\"run\"}}";
        string retryDoc = retryFailure is null
            ? "{\"ok\":true,\"id\":\"cont-1\",\"pid\":42,\"exitCode\":0}"
            : $"{{\"ok\":false,\"error\":\"{retryFailure}\",\"stage\":\"run\"}}";
        int retryExit = retryFailure is null ? 0 : 1;

        string script =
            "@echo off" + Environment.NewLine +
            "if \"%2\"==\"resume\" goto resume" + Environment.NewLine +
            "if \"%2\"==\"exec\" goto exec" + Environment.NewLine +
            "exit /b 99" + Environment.NewLine +
            ":resume" + Environment.NewLine +
            "echo {\"ok\":true}" + Environment.NewLine +
            $"echo x>\"{resumeMarker}\"" + Environment.NewLine +
            "exit /b 0" + Environment.NewLine +
            ":exec" + Environment.NewLine +
            $"if exist \"{execMarker}\" goto exec2" + Environment.NewLine +
            $"echo x>\"{execMarker}\"" + Environment.NewLine +
            "echo " + firstFailureDoc + Environment.NewLine +
            "exit /b 1" + Environment.NewLine +
            ":exec2" + Environment.NewLine +
            "echo " + retryDoc + Environment.NewLine +
            $"exit /b {retryExit}";

        File.WriteAllText(path, script + Environment.NewLine);
        return new HcsCtl(path);
    }

    private bool ResumeCalled => File.Exists(Path.Combine(_directory, "resume-called.marker"));

    private sealed record Outcome(int? ExitCode, Exception? Failure);

    /// <summary>Runs the recovery loop against the fake; the recover hook issues the real resume verb.</summary>
    private async Task<List<Outcome>> RunAsync(HcsCtl fake, bool paused)
    {
        List<Outcome> outcomes = [];

        await HcsContainerInstance.RunWorkloadWithRecoveryAsync(
            fake,
            ContainerId,
            Command,
            new Dictionary<string, string>(),
            progress: null,
            NullLogger.Instance,
            () => paused,
            async () =>
            {
                await fake.ResumeAsync(ContainerId, CancellationToken.None).ConfigureAwait(false);
                return true;
            },
            code =>
            {
                outcomes.Add(new(code, null));
                return Task.CompletedTask;
            },
            failure =>
            {
                outcomes.Add(new(null, failure));
                return Task.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);

        return outcomes;
    }

    [Fact]
    public async Task Invalid_state_while_paused_resumes_and_the_redispatch_succeeds()
    {
        HcsCtl fake = FakeCtl(InvalidStateError);
        List<Outcome> outcomes = await RunAsync(fake, paused: true);

        // The resume verb must reach the container before the re-dispatch, and the re-dispatched
        // exec exiting 0 must report an exit — not the null-exit Exited publication the old code
        // made for the paused create.
        Assert.True(ResumeCalled, "the resume verb must be issued");
        Outcome outcome = Assert.Single(outcomes);
        Assert.Null(outcome.Failure);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task A_failed_retry_does_not_loop_again_and_publishes_exited()
    {
        // The retry fails with the same invalid-state text: if the recovery looped instead of
        // stopping at one re-dispatch, the fake's second exec slot (success) would run and the
        // single-outcome assertion below would fail.
        HcsCtl fake = FakeCtl(InvalidStateError, retryFailure: InvalidStateError);
        List<Outcome> outcomes = await RunAsync(fake, paused: true);

        Assert.True(ResumeCalled, "the resume verb must be issued");
        Outcome outcome = Assert.Single(outcomes);
        Assert.Null(outcome.ExitCode);
        Assert.Contains("0xc0370105", outcome.Failure!.Message);
    }

    [Fact]
    public async Task A_non_invalid_state_failure_skips_resume_and_publishes_exited()
    {
        HcsCtl fake = FakeCtl(OtherError);
        List<Outcome> outcomes = await RunAsync(fake, paused: true);

        Assert.False(ResumeCalled, "no resume verb may be issued for a non-invalid-state failure");
        Outcome outcome = Assert.Single(outcomes);
        Assert.Null(outcome.ExitCode);
        Assert.Contains("0x8007000d", outcome.Failure!.Message);
    }

    [Fact]
    public async Task Invalid_state_while_not_paused_skips_resume_and_publishes_exited()
    {
        HcsCtl fake = FakeCtl(InvalidStateError);
        List<Outcome> outcomes = await RunAsync(fake, paused: false);

        Assert.False(ResumeCalled, "no resume verb may be issued when the container was not paused");
        Outcome outcome = Assert.Single(outcomes);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task A_drained_boot_skips_the_redispatch_and_publishes_nothing()
    {
        // The recover hook reports the boot was drained (in the production wiring the epoch and
        // _current re-validation runs under the gate BEFORE any side effect and returns false):
        // whatever the hook did, a false return must skip the re-dispatch and publish nothing —
        // neither the exit nor the failure path may speak for the retired boot.
        HcsCtl fake = FakeCtl(InvalidStateError);
        List<Outcome> outcomes = [];

        await HcsContainerInstance.RunWorkloadWithRecoveryAsync(
            fake,
            ContainerId,
            Command,
            new Dictionary<string, string>(),
            progress: null,
            NullLogger.Instance,
            () => true,
            async () =>
            {
                await fake.ResumeAsync(ContainerId, CancellationToken.None).ConfigureAwait(false);
                return false;
            },
            code =>
            {
                outcomes.Add(new(code, null));
                return Task.CompletedTask;
            },
            failure =>
            {
                outcomes.Add(new(null, failure));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(ResumeCalled, "the supplied hook resumed before reporting the drain; the loop must still publish nothing");
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_hook_that_detects_the_drain_before_side_effects_never_resumes()
    {
        // F1: the production recovery re-validates the boot under the gate before any side
        // effect. A stopped/restarted boot fails that check, so no resume verb and no
        // publication may happen at all — this pins the loop honoring the hook's false return.
        HcsCtl fake = FakeCtl(InvalidStateError);
        List<Outcome> outcomes = [];

        await HcsContainerInstance.RunWorkloadWithRecoveryAsync(
            fake,
            ContainerId,
            Command,
            new Dictionary<string, string>(),
            progress: null,
            NullLogger.Instance,
            () => true,
            () => Task.FromResult(false),
            code =>
            {
                outcomes.Add(new(code, null));
                return Task.CompletedTask;
            },
            failure =>
            {
                outcomes.Add(new(null, failure));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(ResumeCalled, "a drained boot must not receive a resume verb");
        Assert.Empty(outcomes);
    }

    [Fact]
    public void The_decision_requires_both_paused_and_invalid_state()
    {
        Assert.True(HcsContainerInstance.ShouldRetryWorkload(
            true, "failed: the compute system is in an invalid state (0xc0370105)"));
        Assert.True(HcsContainerInstance.ShouldRetryWorkload(
            true, "failed: the compute system is in an invalid state (0x80370105)"));
        Assert.False(HcsContainerInstance.ShouldRetryWorkload(
            true, "failed: the data is invalid (0x8007000d)"));
        Assert.False(HcsContainerInstance.ShouldRetryWorkload(
            false, "failed: the compute system is in an invalid state (0xc0370105)"));
        Assert.False(HcsContainerInstance.ShouldRetryWorkload(true, null));
        Assert.False(HcsContainerInstance.ShouldRetryWorkload(true, ""));
    }
}
