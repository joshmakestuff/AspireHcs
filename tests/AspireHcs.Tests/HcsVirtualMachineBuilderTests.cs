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
        // The id is a GUID because hcsctl requires one: it is also the VM's hvsocket address.
        Assert.True(Guid.TryParse(resource.VmId, out _), $"VmId '{resource.VmId}' is not a GUID.");

        // A local VM must not land in a publish manifest.
        Assert.Contains(resource.Annotations, a => a is ManifestPublishingCallbackAnnotation);
        _ = vm;
    }

    [Fact]
    public void Builder_methods_configure_the_resource()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithVhdx(@"c:\images\test.vhdx")
            .WithMemory(gigabytes: 4)
            .WithProcessorCount(6);

        Assert.Equal(@"c:\images\test.vhdx", vm.Resource.VhdxPath);
        Assert.Equal(4096, vm.Resource.MemoryMb);
        Assert.Equal(6, vm.Resource.ProcessorCount);
    }

    [Fact]
    public void WithNetwork_and_WithEndpoint_configure_networking()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithNetwork()
            .WithEndpoint("ssh", targetPort: 22)
            .WithEndpoint("api", targetPort: 8080);

        // The literal string is the wire value hcsctl resolves by name, and the same one
        // containers default to so that both kinds are co-located.
        Assert.Equal("Default Switch", vm.Resource.NetworkName);
        Assert.Equal("ssh", vm.Resource.PrimaryEndpointName);

        // The MAC and the endpoint id are hcsctl's to choose, and are unknown until the VM has
        // been created. The MAC must survive a stop/start for the DHCP lease to hold, so hcsctl's
        // store remembers it, not this process.
        Assert.Null(vm.Resource.MacAddress);
        Assert.Null(vm.Resource.EndpointId);

        List<EndpointAnnotation> endpoints = [.. vm.Resource.Annotations.OfType<EndpointAnnotation>()];
        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.False(e.IsProxied));
        Assert.Equal(22, endpoints[0].TargetPort);

        // Connection string resolves the primary endpoint lazily via its host:port.
        Assert.Equal("{vm.bindings.ssh.host}:{vm.bindings.ssh.port}",
            vm.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void ConnectionString_requires_a_declared_endpoint()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        Assert.Throws<InvalidOperationException>(() => vm.Resource.ConnectionStringExpression);
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
