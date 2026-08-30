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
    public void WithDisk_records_full_paths_in_declaration_order()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithDisk(@"c:\images\data1.vhdx")
            .WithDisk(@"c:\images\data2.vhdx");

        // Order is the LUN order: data1 at LUN 1, data2 at LUN 2.
        Assert.Equal([@"c:\images\data1.vhdx", @"c:\images\data2.vhdx"], vm.Resource.DataDiskPaths);
    }

    [Fact]
    public void WithDisk_rejects_a_duplicate_path()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithDisk(@"c:\images\data.vhdx");

        // Case-insensitive: Windows paths are.
        Assert.Throws<InvalidOperationException>(() => vm.WithDisk(@"C:\Images\DATA.vhdx"));
    }

    [Fact]
    public void WithMacAddress_normalizes_to_the_form_hcsctl_echoes()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithNetwork()
            .WithMacAddress("00:15:5d:02:33:0e");

        Assert.Equal("00-15-5D-02-33-0E", vm.Resource.RequestedMacAddress);
    }

    [Fact]
    public void WithMacAddress_rejects_what_is_not_a_48bit_mac()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        Assert.ThrowsAny<ArgumentException>(() => vm.WithMacAddress("not-a-mac"));
        Assert.ThrowsAny<ArgumentException>(() => vm.WithMacAddress("00-15-5D-02-33"));
        Assert.ThrowsAny<ArgumentException>(() => vm.WithMacAddress("  "));
    }

    [Fact]
    public void WithVlan_stores_the_id_and_rejects_out_of_range_values()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm").WithVlan(10);

        Assert.Equal(10, vm.Resource.VlanId);
        // 0 is "untagged" — expressed by not calling WithVlan — and 4095 is reserved.
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.WithVlan(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.WithVlan(4095));
    }

    [Fact]
    public void WithGuestAddress_declares_the_vm_agentless()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithGuestAddress("10.20.10.20", bootTimeout: TimeSpan.FromMinutes(5));

        Assert.Equal("10.20.10.20", vm.Resource.GuestAddress);
        Assert.Equal(TimeSpan.FromMinutes(5), vm.Resource.GuestAddressTimeout);
        Assert.True(vm.Resource.IsAgentless);
    }

    [Fact]
    public void WithGuestAddress_rejects_a_spelling_that_parses_as_a_different_address()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        // IPAddress.Parse reads "020" as octal: this spelling means 10.20.10.16. An address
        // that silently means a different address is the misconfiguration agentless mode
        // cannot diagnose later, so it must fail at the builder.
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => vm.WithGuestAddress("10.20.10.020"));
        Assert.Contains("10.20.10.16", ex.Message);
    }

    [Fact]
    public void WithGuestAddress_defaults_the_timeout_and_rejects_non_addresses()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        Assert.False(vm.Resource.IsAgentless);
        Assert.ThrowsAny<ArgumentException>(() => vm.WithGuestAddress("ten.twenty.ten.twenty"));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.WithGuestAddress("10.20.10.20", TimeSpan.Zero));

        vm.WithGuestAddress("10.20.10.20");
        Assert.Equal(TimeSpan.FromMinutes(15), vm.Resource.GuestAddressTimeout);
    }

    [Fact]
    public void WithInsecureHttpsHealthCheck_registers_a_named_check_on_the_endpoint()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithNetwork()
            .WithEndpoint("https", targetPort: 443, scheme: "https")
            .WithInsecureHttpsHealthCheck(path: "tips");

        HealthCheckAnnotation annotation = Assert.Single(vm.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("vm_https_https_check", annotation.Key);
    }

    [Fact]
    public void WithInsecureHttpsHealthCheck_requires_a_declared_endpoint()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        // No endpoints at all, and an endpoint name that was never declared.
        Assert.Throws<InvalidOperationException>(() => vm.WithInsecureHttpsHealthCheck());
        vm.WithNetwork().WithEndpoint("ssh", targetPort: 22);
        Assert.Throws<InvalidOperationException>(() => vm.WithInsecureHttpsHealthCheck("https"));
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
