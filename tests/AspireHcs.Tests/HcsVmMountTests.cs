using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// The VM bind mount mirrors the container one's builder shape, but its guest may be Linux or
// Windows, so the target is an absolute path in either form. hcsctl rejects a relative path, a
// missing host directory and a repeated guest path; the ones the model can catch are caught at
// model-build time.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsVmMountTests
{
    private static IResourceBuilder<HcsVirtualMachineResource> Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm");

    [Fact]
    public void WithBindMount_records_source_target_and_mode()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithBindMount(@"C:\src", "/mnt/data")
            .WithBindMount(@"C:\config", "/mnt/cfg", isReadOnly: true);

        Assert.Collection(vm.Resource.Mounts,
            m => Assert.Equal((@"C:\src", "/mnt/data", false), (m.Source, m.Target, m.IsReadOnly)),
            m => Assert.Equal((@"C:\config", "/mnt/cfg", true), (m.Source, m.Target, m.IsReadOnly)));
    }

    // Resolving here gives the AppHost-relative convention, matching Aspire's Docker path and the
    // container mount.
    [Fact]
    public void A_relative_source_is_resolved_against_the_apphost_directory()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithBindMount("data", "/mnt/data");

        HcsVmMount mount = Assert.Single(vm.Resource.Mounts);
        Assert.True(Path.IsPathFullyQualified(mount.Source));
        Assert.Equal(Path.GetFullPath("data", builder.AppHostDirectory), mount.Source);
    }

    [Theory]
    [InlineData("/mnt/data")]
    [InlineData(@"D:\data")]
    [InlineData("D:/data")]
    public void An_absolute_target_in_either_form_is_accepted(string target)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithBindMount(@"C:\src", target);
        Assert.Equal(target, Assert.Single(vm.Resource.Mounts).Target);
    }

    [Theory]
    [InlineData("data")]
    [InlineData(@"mnt\data")]
    [InlineData("mnt/data")]
    public void A_relative_target_is_rejected(string target)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => vm.WithBindMount(@"C:\src", target));
        Assert.Contains("absolute", thrown.Message);
    }

    [Fact]
    public void The_same_guest_path_twice_is_rejected_at_model_build_time()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithBindMount(@"C:\one", "/mnt/data");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => vm.WithBindMount(@"C:\two", "/mnt/data"));

        Assert.Contains("/mnt/data", thrown.Message);
    }

    // hcsctl compares guest paths case-insensitively with a trailing separator trimmed; the
    // duplicate check must agree, for either separator.
    [Theory]
    [InlineData("/MNT/DATA")]
    [InlineData("/mnt/data/")]
    public void A_duplicate_guest_path_is_caught_however_it_is_spelled(string second)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithBindMount(@"C:\one", "/mnt/data");

        Assert.Throws<InvalidOperationException>(() => vm.WithBindMount(@"C:\two", second));
    }

    // The share sub-path is derived from the index, so it is always a valid hcsctl id and cannot
    // collide with another mount's target basename.
    [Fact]
    public void Exposure_names_are_stable_and_distinct_per_index()
    {
        Assert.Equal("mount0", HcsVirtualMachineResource.ExposureName(0));
        Assert.Equal("mount1", HcsVirtualMachineResource.ExposureName(1));
        Assert.NotEqual(HcsVirtualMachineResource.ExposureName(0), HcsVirtualMachineResource.ExposureName(1));
    }
}
