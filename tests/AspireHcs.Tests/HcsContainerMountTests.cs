using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// #50 and #51. hcsctl rejects a relative path, a missing host directory, a repeated guest path
// and a size without a unit — all with exit 64. Meeting those as resource-start failures would
// be a slower, worse version of failing at model-build time, so the ones we can catch here are
// caught here.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerMountTests
{
    [Fact]
    public void A_mount_renders_hcsctls_spelling()
    {
        Assert.Equal(@"C:\src:C:\app", new HcsContainerMount(@"C:\src", @"C:\app", IsReadOnly: false).ToOptionValue());
        Assert.Equal(@"C:\src:C:\app:ro", new HcsContainerMount(@"C:\src", @"C:\app", IsReadOnly: true).ToOptionValue());
    }

    [Fact]
    public void WithBindMount_records_source_target_and_mode()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithBindMount(@"C:\src", @"C:\app")
            .WithBindMount(@"C:\config", @"C:\etc", isReadOnly: true);

        Assert.Collection(container.Resource.Mounts,
            m => Assert.Equal((@"C:\src", @"C:\app", false), (m.Source, m.Target, m.IsReadOnly)),
            m => Assert.Equal((@"C:\config", @"C:\etc", true), (m.Source, m.Target, m.IsReadOnly)));
    }

    // hcsctl requires both paths drive-letter absolute and would reject a relative source with
    // exit 64 — naming a path the developer never typed. Resolving here is what makes the
    // AppHost-relative convention work, matching Aspire's Docker path.
    [Fact]
    public void A_relative_source_is_resolved_against_the_apphost_directory()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithBindMount("data", @"C:\data");

        HcsContainerMount mount = Assert.Single(container.Resource.Mounts);
        Assert.True(Path.IsPathFullyQualified(mount.Source));
        Assert.Equal(Path.GetFullPath("data", builder.AppHostDirectory), mount.Source);
    }

    // A relative guest path has nothing to resolve against, so it is a mistake rather than a
    // convenience to support.
    [Fact]
    public void A_relative_target_is_rejected()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => container.WithBindMount(@"C:\src", "app"));
        Assert.Contains("absolute", thrown.Message);
    }

    [Fact]
    public void The_same_guest_path_twice_is_rejected_at_model_build_time()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithBindMount(@"C:\one", @"C:\app");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => container.WithBindMount(@"C:\two", @"C:\app"));

        Assert.Contains(@"C:\app", thrown.Message);
    }

    // hcsctl compares guest paths case-insensitively with a trailing separator trimmed, so the
    // duplicate check has to agree or it lets through what hcsctl then rejects.
    [Theory]
    [InlineData(@"c:\app")]
    [InlineData(@"C:\APP")]
    [InlineData(@"C:\app\")]
    public void A_duplicate_guest_path_is_caught_however_it_is_spelled(string second)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithBindMount(@"C:\one", @"C:\app");

        Assert.Throws<InvalidOperationException>(() => container.WithBindMount(@"C:\two", second));
    }

    [Fact]
    public void The_scratch_size_is_unset_by_default_so_the_option_is_opt_in()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Null(builder.AddHcsContainer("worker").Resource.ScratchSizeGigabytes);
    }

    [Fact]
    public void WithScratchSize_records_the_request()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal(40, builder.AddHcsContainer("worker").WithScratchSize(40).Resource.ScratchSizeGigabytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_scratch_size_is_rejected(int gigabytes)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        Assert.Throws<ArgumentOutOfRangeException>(() => container.WithScratchSize(gigabytes));
    }
}
