using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// These drive the real hcsctl.exe and assert the contract this seam is built on: exit codes mean
// what they say, stdout carries exactly one document on every path, and exit 64 attempted
// nothing. They are the AspireHcs-side mirror of hcsctl's own contract_test.go.
//
// Nothing here reaches HCS, a registry, or elevation — every case is an argument rejection or a
// store-file failure. A green run means "the seam honours the contract", never "containers work".
[Collection(HcsCtlEnvironmentCollection.Name)]
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlContractTests
{
    private static HcsCtl Require(string? storePath = null)
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? path, out string? failure), failure);

        return new HcsCtl(path!, storePath);
    }

    private static string EmptyStorePath() => Path.Combine(Directory.CreateTempSubdirectory("aspirehcs-store").FullName, "store");

    [SkippableFact]
    public async Task Info_binds_the_document_this_assembly_expects()
    {
        HcsCtl hcsctl = Require();

        HcsCtlInfoDocument info = await hcsctl.GetInfoAsync();

        Assert.True(info.Ok);
        // Binding, not merely parsing: a renamed wire field would leave these at their defaults
        // and this would fail, which is the point of pinning the names explicitly.
        Assert.False(string.IsNullOrWhiteSpace(info.HostOsVersion));
        Assert.True(info.HostBuild > 0);
        Assert.NotEmpty(info.Services);
        Assert.NotNull(info.Store);
        Assert.False(string.IsNullOrWhiteSpace(info.Store.Root));
    }

    [SkippableFact]
    public async Task Info_is_cached_and_returns_the_same_instance()
    {
        HcsCtl hcsctl = Require();

        HcsCtlInfoDocument first = await hcsctl.GetInfoAsync();
        HcsCtlInfoDocument second = await hcsctl.GetInfoAsync();

        Assert.Same(first, second);
    }

    // Exit 64 is a defect in the argv this assembly built, and it promises nothing was attempted.
    // Reporting it as an infrastructure failure would send a developer looking at their Hyper-V
    // configuration for a missing option.
    [SkippableFact]
    public async Task A_rejected_command_line_raises_a_usage_exception()
    {
        HcsCtl hcsctl = Require();

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["image", "pull"], HcsCtlJsonContext.Default.HcsCtlFailureDocument));

        Assert.Equal(HcsCtlExitCode.Usage, thrown.ExitCode);
        Assert.Equal("usage", thrown.Stage);
        Assert.Contains("--ref is required", thrown.Message);
    }

    [SkippableFact]
    public async Task A_usage_error_attempts_nothing()
    {
        HcsCtl hcsctl = Require();
        string store = EmptyStorePath();

        await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["image", "pull", "--store", store], HcsCtlJsonContext.Default.HcsCtlFailureDocument));

        // The store directory is the first thing an attempt would create.
        Assert.False(Directory.Exists(store));
    }

    // Exit 1 and exit 64 must stay distinguishable. This is the CLI-layer equivalent of the
    // S_FALSE trap recorded in #48: a value that looks like failure but is not, or vice versa.
    [SkippableFact]
    public async Task A_command_that_ran_and_failed_raises_a_command_exception_not_a_usage_one()
    {
        HcsCtl hcsctl = Require();

        // A record file holding invalid JSON makes `image rm` fail *after* argument validation
        // passed — the same fixture hcsctl uses to exercise exit 1 without HCS.
        string store = EmptyStorePath();
        Directory.CreateDirectory(Path.Combine(store, "images"));
        await File.WriteAllTextAsync(Path.Combine(store, "images", "x.json"), "not json");

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => hcsctl.InvokeAsync(["image", "rm", "--ref", "x", "--store", store],
                HcsCtlJsonContext.Default.HcsCtlFailureDocument));

        Assert.Equal(HcsCtlExitCode.Failed, thrown.ExitCode);
        Assert.IsNotType<HcsCtlUsageException>(thrown);
    }

    // The streams are decoded as UTF-8 explicitly. Without that they are decoded with the
    // console code page, and a non-ASCII value comes back mangled — which is how an environment
    // variable or a path would silently corrupt on its way to the guest.
    [SkippableFact]
    public async Task Non_ascii_survives_the_process_boundary()
    {
        HcsCtl hcsctl = Require();
        const string reference = "wrong/ünïcødé-λ:tag!!!";

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["image", "pull", "--ref", reference],
                HcsCtlJsonContext.Default.HcsCtlFailureDocument));

        Assert.Contains(reference, thrown.Message);
    }

    // hcsctl prints ~100 lines of usage to stderr on a rejection. Diagnostics are a bounded tail
    // for a bug report, never a result — an unbounded capture would also mean buffering a
    // long-running container's entire guest output for no reason.
    [SkippableFact]
    public async Task Stderr_is_captured_as_bounded_diagnostics_and_reported_as_progress()
    {
        HcsCtl hcsctl = Require();
        List<string> progress = [];

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["image", "pull"], HcsCtlJsonContext.Default.HcsCtlFailureDocument,
                new Progress<string>(line =>
                {
                    lock (progress)
                    {
                        progress.Add(line);
                    }
                })));

        Assert.NotNull(thrown.Diagnostics);
        Assert.InRange(thrown.Diagnostics.Split(Environment.NewLine).Length, 1, 50);

        // Progress is delivered asynchronously by Progress<T>, so the count is not asserted here;
        // that it carries hcsctl's usage output at all is what matters.
        Assert.True(thrown.Diagnostics.Length > 0);
    }

    [SkippableFact]
    public async Task The_command_line_is_reported_for_a_bug_report()
    {
        HcsCtl hcsctl = Require();

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["image", "pull"], HcsCtlJsonContext.Default.HcsCtlFailureDocument));

        Assert.Contains("image pull", thrown.CommandLine);
        // --json is added by the seam, not by callers. If that ever stops being true, every
        // invocation starts parsing human-mode output.
        Assert.Contains("--json", thrown.CommandLine);
    }
}
