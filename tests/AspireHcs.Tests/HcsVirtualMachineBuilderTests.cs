using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class HcsVirtualMachineBuilderTests
{
    [Fact]
    public void AddHcsVm_registers_resource_with_defaults_and_manifest_exclusion()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("appliance");

        HcsVirtualMachineResource resource = Assert.Single(builder.Resources.OfType<HcsVirtualMachineResource>());
        Assert.Equal("appliance", resource.Name);
        Assert.Equal(2048, resource.MemoryMb);
        Assert.Equal(2, resource.ProcessorCount);
        Assert.True(resource.CopyOnWrite);
        Assert.StartsWith("aspirehcs-appliance-", resource.VmId);

        // A local VM must never land in a publish manifest.
        Assert.Contains(resource.Annotations, a => a is ManifestPublishingCallbackAnnotation);
        _ = vm;
    }

    [Fact]
    public void Builder_methods_configure_the_resource()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithVhdx(@"c:\images\test.vhdx", copyOnWrite: false)
            .WithMemory(gigabytes: 4)
            .WithProcessorCount(6);

        Assert.Equal(@"c:\images\test.vhdx", vm.Resource.VhdxPath);
        Assert.False(vm.Resource.CopyOnWrite);
        Assert.Equal(4096, vm.Resource.MemoryMb);
        Assert.Equal(6, vm.Resource.ProcessorCount);
    }

    [Fact]
    public void Builder_methods_reject_invalid_arguments()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        Assert.ThrowsAny<ArgumentException>(() => vm.WithVhdx("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.WithMemory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.WithProcessorCount(-1));
    }
}
