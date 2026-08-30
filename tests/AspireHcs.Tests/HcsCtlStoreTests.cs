using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// Images are acquired out of band (`image import` is elevated and runs once per image), so an
// AppHost normally points at a store someone else prepared, not at hcsctl's per-user default.
// A --store that fails to reach hcsctl makes the command succeed against the wrong images.
//
// The exclusion list in HcsCtl is a fact about hcsctl's CLI, so it is pinned here from both
// sides.
[Collection(HcsCtlEnvironmentCollection.Name)]
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlStoreTests
{
    /// <summary>Points the store tests at a real, prepared hcsctl store. Opt in per host.</summary>
    private const string PreparedStoreVariable = "ASPIREHCS_TEST_STORE";

    private static string RequireBinary()
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? path, out string? failure), failure);
        return path!;
    }

    private static string CorruptStore()
    {
        string store = Path.Combine(Directory.CreateTempSubdirectory("aspirehcs-store").FullName, "store");
        Directory.CreateDirectory(Path.Combine(store, "images"));
        File.WriteAllText(Path.Combine(store, "images", RepositoryTools.RecordFileName("x")), "not json");
        return store;
    }

    // Proves the flag lands: this store is the only one holding a corrupt record, so the failure
    // cannot come from hcsctl's default store.
    [SkippableFact]
    public async Task The_configured_store_reaches_hcsctl()
    {
        HcsCtl hcsctl = new(RequireBinary(), CorruptStore());

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => hcsctl.InvokeAsync(["image", "rm", "--ref", "x"], HcsCtlJsonContext.Default.HcsCtlResultDocument));

        Assert.Equal(HcsCtlExitCode.Failed, thrown.ExitCode);
    }

    // With no store configured, hcsctl must be launched with no --store at all so it stays on
    // its per-user default. A stand-in binary records the argv it received: the real hcsctl
    // cannot report which flags it was not given. Needs no hcsctl, so it never skips.
    [Fact]
    public async Task No_configured_store_leaves_hcsctl_on_its_default()
    {
        using FakeHcsCtlDirectory fakes = new();
        string argvPath = Path.Combine(fakes.Directory, "argv.txt");
        HcsCtl hcsctl = fakes.Create($$"""
            >"{{argvPath}}" echo %*
            echo {"ok":true}
            """);

        HcsCtlInfoDocument info = await hcsctl.GetInfoAsync();

        Assert.True(info.Ok);
        // The whole argv, so an appended --store fails here rather than passing unnoticed.
        Assert.Equal("info --json", File.ReadAllText(argvPath).Trim());
    }

    // The network group rejects --store. If the exclusion list were wrong, this would be exit 64.
    [SkippableFact]
    public async Task The_network_group_runs_even_when_a_store_is_configured()
    {
        HcsCtl hcsctl = new(RequireBinary(), CorruptStore());

        HcsCtlResultDocument result = await hcsctl.InvokeAsync(
            ["network", "ls"], HcsCtlJsonContext.Default.HcsCtlResultDocument);

        Assert.True(result.Ok);
    }

    // The other side of the same pin: hcsctl rejects it. If hcsctl starts accepting --store on
    // network, this fails and the exclusion can go.
    [SkippableFact]
    public async Task The_network_group_still_rejects_an_explicit_store()
    {
        HcsCtl hcsctl = new(RequireBinary());

        await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["network", "ls", "--store", CorruptStore()],
                HcsCtlJsonContext.Default.HcsCtlResultDocument));
    }

    // The guest group is excluded too: a guest is addressed by VM id over hvsocket, and hcsctl
    // rejects --store there with exit 64. If the exclusion were missing, this rejection would be
    // "unknown option --store" — instead HcsCtl must strip it and let the verb complain about
    // what is actually missing. No VM is needed: the pin is entirely in argument handling.
    [SkippableFact]
    public async Task The_guest_group_runs_even_when_a_store_is_configured()
    {
        HcsCtl hcsctl = new(RequireBinary(), CorruptStore());

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["guest", "exec"], HcsCtlJsonContext.Default.HcsCtlGuestExecDocument));

        Assert.Contains("--vmid", thrown.Message);
        Assert.DoesNotContain("--store", thrown.Message);
    }

    // The other side of the same pin: the exclusion exists because hcsctl really does reject it.
    [SkippableFact]
    public async Task The_guest_group_still_rejects_an_explicit_store()
    {
        HcsCtl hcsctl = new(RequireBinary());

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["guest", "exec", "--store", CorruptStore()],
                HcsCtlJsonContext.Default.HcsCtlGuestExecDocument));

        Assert.Contains("--store", thrown.Message);
    }

    // `vm stop` is the one verb inside a store-accepting group that rejects --store: it drives
    // HCS by id alone so it can stop a system whose store record is gone. With a store
    // configured, HcsCtl must strip the flag; a random id then reports "already stopped" rather
    // than exit 64. This is the defect that wedged every teardown in the integration suite.
    [SkippableFact]
    public async Task Vm_stop_runs_even_when_a_store_is_configured()
    {
        HcsCtl hcsctl = new(RequireBinary(), CorruptStore());

        HcsCtlResultDocument result = await hcsctl.InvokeAsync(
            ["vm", "stop", "--id", Guid.NewGuid().ToString()],
            HcsCtlJsonContext.Default.HcsCtlResultDocument);

        Assert.True(result.Ok);
    }

    // The other side of the same pin: hcsctl really does reject it. If `vm stop` starts
    // accepting --store, this fails and the verb exclusion can go.
    [SkippableFact]
    public async Task Vm_stop_still_rejects_an_explicit_store()
    {
        HcsCtl hcsctl = new(RequireBinary());

        HcsCtlUsageException thrown = await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(
                ["vm", "stop", "--id", Guid.NewGuid().ToString(), "--store", CorruptStore()],
                HcsCtlJsonContext.Default.HcsCtlResultDocument));

        Assert.Contains("--store", thrown.Message);
    }

    // Reading a prepared store needs no elevation; only the import did, out of band.
    [SkippableFact]
    public async Task A_prepared_store_is_readable_unelevated_and_its_images_pass_preflight()
    {
        string? store = Environment.GetEnvironmentVariable(PreparedStoreVariable);
        Skip.If(string.IsNullOrWhiteSpace(store),
            $"Set {PreparedStoreVariable} to a prepared hcsctl store (see AspireHcs#39) to run this.");

        // The claim is about an unelevated token (info.Elevated is asserted false below), so an
        // elevated run cannot exercise it.
        Skip.If(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            "This process is elevated; re-run unelevated to measure the unelevated read path.");

        HcsCtl hcsctl = new(RequireBinary(), store);
        HcsCtlInfoDocument info = await hcsctl.GetInfoAsync();

        Assert.True(info.Ok);
        Assert.False(info.Elevated);
        Assert.NotNull(info.Store);
        Assert.True(info.Store.Exists);
        Assert.NotEmpty(info.Images);

        // The preflight must pass on a host that can run these, and must not report a staged
        // image as missing.
        Assert.Null(HcsCtlPreflight.DescribeBlocker(info));
        foreach (HcsCtlImageInfo image in info.Images)
        {
            Assert.NotNull(image.Reference);
            Assert.Null(HcsCtlPreflight.DescribeMissingImage(info, image.Reference));
        }
    }
}
