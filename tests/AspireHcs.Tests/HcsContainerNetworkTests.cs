using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// On a NAT network a static HNS endpoint programs a container's stack directly, so the address
// is known when the container is created and is reachable from the host without port
// publishing. An ICS network (the Default Switch, the default) leases the address after the
// guest starts, so the instance waits for it there; ContainerAddressLeaseTests pins that wait.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerNetworkTests
{
    [Fact]
    public void A_container_has_no_network_by_default()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Null(builder.AddHcsContainer("worker").Resource.NetworkName);
    }

    // The literal string is the wire value hcsctl resolves by name, and it must be the same one
    // the VM side defaults to so that both kinds are co-located.
    [Fact]
    public void WithNetwork_defaults_to_the_Default_Switch_shared_with_VMs()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal("Default Switch", builder.AddHcsContainer("worker").WithNetwork().Resource.NetworkName);
        Assert.Equal(HcsNetwork.DefaultSwitchName, builder.AddHcsVm("vm").WithNetwork().Resource.NetworkName);
    }

    // hcsctl cannot create a network (https://github.com/joshmakestuff/hcsctl/issues/15), so
    // this names an existing one. `nat` stays expressible: a container placed there cannot see
    // the Default Switch residents.
    [Fact]
    public void A_named_network_is_honoured()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal("nat", builder.AddHcsContainer("worker").WithNetwork("nat").Resource.NetworkName);
    }

    [Fact]
    public void The_first_endpoint_backs_the_connection_string()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNetwork()
            .WithEndpoint("http", 8080);

        Assert.Equal("http", container.Resource.PrimaryEndpointName);
    }

    // The health check resolves the endpoint per check: EndpointReference.IsAllocated memoizes
    // its first answer including false, so a reference built at model-build time would latch
    // unallocated forever. This pins that it is registered against a name that exists.
    [Fact]
    public void WithTcpHealthCheck_defaults_to_the_first_endpoint()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNetwork()
            .WithEndpoint("http", 8080)
            .WithTcpHealthCheck();

        Assert.Contains(container.Resource.Annotations, a => a is HealthCheckAnnotation);
    }

    [Fact]
    public void WithTcpHealthCheck_on_a_resource_with_no_endpoints_explains_itself()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => container.WithTcpHealthCheck());
        Assert.Contains("WithEndpoint", thrown.Message);
    }

    // A typo in an endpoint name must fail the model build, not produce a health report that is
    // unhealthy forever for the wrong reason.
    [Fact]
    public void WithTcpHealthCheck_rejects_an_endpoint_name_that_does_not_exist()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNetwork()
            .WithEndpoint("http", 8080);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => container.WithTcpHealthCheck("htpp"));

        Assert.Contains("htpp", thrown.Message);
    }
}
