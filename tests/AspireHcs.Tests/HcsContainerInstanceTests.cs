using System.Runtime.Versioning;
using AspireHcs.Cli;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// Pause is gated on the workload process being visible in `container ps`: the resource reports
// Running before the detached HcsCreateProcess lands, and pausing inside that window freezes the
// guest first (AspireHcs#74). These pin the gate: what satisfies it, what it filters, and what
// its failure says. The fake reads stand in for `container ps --json`.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerInstanceTests
{
    private static HcsCtlProcessListDocument Listing(IReadOnlyList<HcsCtlGuestProcess> processes) =>
        new() { Ok = true, Processes = processes };

    private static HcsCtlGuestProcess Process(int pid, string image) =>
        new() { ProcessId = pid, ImageName = image };

    private static Task WaitAsync(
        Func<CancellationToken, Task<HcsCtlProcessListDocument>> readProcesses,
        string expectedImageName,
        TimeSpan? timeout = null) =>
        HcsContainerInstance.WaitForWorkloadProcessAsync(
            readProcesses, expectedImageName, "hcsworker",
            timeout ?? TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None);

    // The guest runs several system processes before the workload is created. None of them may
    // satisfy the gate, and the match must not care how HCS reported the image name's case.
    [Fact]
    public async Task The_wait_ignores_boot_processes_and_matches_the_workload_case_insensitively()
    {
        int reads = 0;
        Task wait = WaitAsync(
            _ => Task.FromResult(
            ++reads == 1
                ? Listing([Process(4, "smss.exe"), Process(12, "csrss.exe"), Process(56, "wininit.exe")])
                : Listing([Process(4, "smss.exe"), Process(340, "CMD.EXE")])),
            "cmd.exe");

        await wait;

        Assert.Equal(2, reads);
    }

    // A workload that never appears must fail the pause, naming the resource, the expected image
    // and the reason, rather than either pausing early or waiting forever.
    [Fact]
    public async Task Expiry_names_the_resource_image_and_the_refusal()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(_ => Task.FromResult(Listing([])), "cmd.exe", timeout: TimeSpan.Zero));

        Assert.Contains("'hcsworker'", ex.Message);
        Assert.Contains("'cmd.exe'", ex.Message);
        Assert.Contains("refusing to pause before the workload starts", ex.Message);
    }
}
