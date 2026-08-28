using System.Runtime.Versioning;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// The pause gate (issue #74) waits for the workload's guest process before pausing: pausing
// between Running publication and the workload's HcsCreateProcess makes that create fail
// 0x80370105/0xc0370105. These pin the wait with scripted process-list reads — no hcsctl, no HCS.
[SupportedOSPlatform("windows10.0.17763")]
public class PauseGateTests
{
    private static HcsCtlProcessListDocument Listing(params HcsCtlGuestProcess[] processes) =>
        new() { Ok = true, Processes = processes };

    private static HcsCtlGuestProcess Process(string imageName) =>
        new() { ImageName = imageName };

    private static HcsCtlCommandException PsFailure() =>
        new("hcsctl failed: container ps refused (0xc037010a)", "container ps --id 123", 1, "ps", null);

    private static Task<HcsCtlProcessListDocument?> Doc(params HcsCtlGuestProcess[] processes) =>
        Task.FromResult<HcsCtlProcessListDocument?>(Listing(processes));

    private static Task<HcsCtlProcessListDocument?> Fails() =>
        Task.FromException<HcsCtlProcessListDocument?>(PsFailure());

    /// <summary>Reads from a scripted sequence; once exhausted, every further read is empty.</summary>
    private static Func<CancellationToken, Task<HcsCtlProcessListDocument?>> Replay(
        params Task<HcsCtlProcessListDocument?>[] reads)
    {
        Queue<Task<HcsCtlProcessListDocument?>> queue = new(reads);
        return _ => queue.Count == 0
            ? Task.FromResult<HcsCtlProcessListDocument?>(null)
            : queue.Dequeue();
    }

    private static Task<bool> WaitAsync(
        Func<CancellationToken, Task<HcsCtlProcessListDocument?>> listAsync,
        TimeSpan? timeout = null) =>
        HcsContainerInstance.WaitForWorkloadProcessAsync(
            listAsync, "ping.exe", timeout ?? TimeSpan.FromSeconds(2), CancellationToken.None);

    [Fact]
    public async Task A_process_visible_on_the_third_poll_returns_true()
    {
        // smss.exe and friends run in every guest; the workload's own image is what the gate
        // waits for, and it lands after the boot processes.
        bool seen = await WaitAsync(Replay(
            Doc(Process("smss.exe")),
            Doc(Process("smss.exe")),
            Doc(Process("smss.exe"), Process("ping.exe"))));

        Assert.True(seen);
    }

    [Fact]
    public async Task The_wait_returns_false_when_the_process_never_appears()
    {
        bool seen = await WaitAsync(Replay(Doc(Process("smss.exe"))), TimeSpan.FromMilliseconds(500));

        Assert.False(seen);
    }

    [Fact]
    public async Task A_failed_poll_is_retried_and_a_later_sighting_still_succeeds()
    {
        // A container mid-pause refuses `container ps` (0xc037010a, measured); the gate must not
        // treat that as the end of the wait.
        bool seen = await WaitAsync(Replay(Fails(), Fails(), Doc(Process("PING.EXE"))));

        Assert.True(seen);
    }

    [Fact]
    public async Task The_wait_times_out_even_when_every_poll_fails()
    {
        bool seen = await WaitAsync(Replay(Fails(), Fails(), Fails()), TimeSpan.FromMilliseconds(500));

        Assert.False(seen);
    }

    [Fact]
    public async Task The_image_matches_case_insensitively_among_other_guest_processes()
    {
        // hcsctl reports the image name as the guest wrote it (PING.EXE here); the gate compares
        // ordinal-ignore-case against the command-derived name.
        bool seen = await WaitAsync(Replay(Doc(Process("smss.exe"), Process("PING.EXE"))));

        Assert.True(seen);
    }

    [Fact]
    public void A_quoted_executable_path_with_spaces_yields_the_file_name()
    {
        // Quote-aware derivation: splitting "C:\Program Files\worker.exe" on whitespace would
        // yield Program.exe and the gate would wait for the wrong process.
        Assert.Equal("worker.exe", HcsContainerInstance.WorkloadImageName(@"""C:\Program Files\worker.exe"" --run"));
    }

    [Fact]
    public void A_quoted_executable_path_without_spaces_yields_the_file_name()
    {
        Assert.Equal("worker.exe", HcsContainerInstance.WorkloadImageName(@"""C:\worker.exe"" --run"));
    }

    [Fact]
    public void A_quoted_extensionless_executable_yields_the_file_name_with_exe_appended()
    {
        Assert.Equal("worker.exe", HcsContainerInstance.WorkloadImageName(@"""C:\Program Files\worker"" --run"));
    }

    [Fact]
    public void An_empty_command_yields_no_image_name()
    {
        Assert.Null(HcsContainerInstance.WorkloadImageName(""));
        Assert.Null(HcsContainerInstance.WorkloadImageName("   "));
    }

    [Fact]
    public void An_unquoted_command_still_yields_the_first_token()
    {
        // The unquoted form is unchanged by the quote-aware rework, so the live tests' gate
        // image stays cmd.exe/ping.exe.
        Assert.Equal("ping.exe", HcsContainerInstance.WorkloadImageName("ping -t 127.0.0.1"));
        Assert.Equal("cmd.exe", HcsContainerInstance.WorkloadImageName("cmd /c ping -t 127.0.0.1"));
    }
}
