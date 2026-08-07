using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// Images are acquired out of band — `image import` is elevated and runs once per image — so an
// AppHost normally points at a store someone else prepared, not at hcsctl's per-user default.
// A --store that silently fails to reach hcsctl is the dangerous case: the command *succeeds*,
// against the wrong images.
//
// The exclusion list in HcsCtl is a measured fact about another repo's CLI, so it is pinned here
// from both sides rather than trusted.
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
        File.WriteAllText(Path.Combine(store, "images", "x.json"), "not json");
        return store;
    }

    // Proves the flag actually lands: this store is the only one holding a corrupt record, so the
    // failure could not have come from hcsctl's default store.
    [SkippableFact]
    public async Task The_configured_store_reaches_hcsctl()
    {
        HcsCtl hcsctl = new(RequireBinary(), CorruptStore());

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => hcsctl.InvokeAsync(["image", "rm", "--ref", "x"], HcsCtlJsonContext.Default.HcsCtlResultDocument));

        Assert.Equal(HcsCtlExitCode.Failed, thrown.ExitCode);
    }

    [SkippableFact]
    public async Task No_configured_store_leaves_hcsctl_on_its_default()
    {
        HcsCtl hcsctl = new(RequireBinary());

        // The corrupt store exists but was never named, so this must not fail on it.
        _ = CorruptStore();
        HcsCtlInfoDocument info = await hcsctl.GetInfoAsync();

        Assert.True(info.Ok);
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

    // The other side of the same pin: the exclusion exists because hcsctl really does reject it.
    // If hcsctl ever starts accepting --store on network, this fails and the exclusion can go.
    [SkippableFact]
    public async Task The_network_group_still_rejects_an_explicit_store()
    {
        HcsCtl hcsctl = new(RequireBinary());

        await Assert.ThrowsAsync<HcsCtlUsageException>(
            () => hcsctl.InvokeAsync(["network", "ls", "--store", CorruptStore()],
                HcsCtlJsonContext.Default.HcsCtlResultDocument));
    }

    // The images staged for #39. Reading them needs no elevation — only the import did, and that
    // already happened out of band.
    [SkippableFact]
    public async Task A_prepared_store_is_readable_unelevated_and_its_images_pass_preflight()
    {
        string? store = Environment.GetEnvironmentVariable(PreparedStoreVariable);
        Skip.If(string.IsNullOrWhiteSpace(store),
            $"Set {PreparedStoreVariable} to a prepared hcsctl store (see AspireHcs#39) to run this.");

        HcsCtl hcsctl = new(RequireBinary(), store);
        HcsCtlInfoDocument info = await hcsctl.GetInfoAsync();

        Assert.True(info.Ok);
        Assert.False(info.Elevated);
        Assert.NotNull(info.Store);
        Assert.True(info.Store.Exists);
        Assert.NotEmpty(info.Images);

        // The preflight must pass on a host that can actually run these, and must not report a
        // staged image as missing.
        Assert.Null(HcsCtlPreflight.DescribeBlocker(info));
        foreach (HcsCtlImageInfo image in info.Images)
        {
            Assert.NotNull(image.Reference);
            Assert.Null(HcsCtlPreflight.DescribeMissingImage(info, image.Reference));
        }
    }
}
