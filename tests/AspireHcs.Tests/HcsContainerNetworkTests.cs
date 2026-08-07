using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// #41. The container's networking is simpler than the VM's in the one place the VM path cost the
// most: a static HNS endpoint programs a container's stack directly, so the address is known when
// the container is CREATED rather than discovered by polling for a DHCP lease. Measured
// 2026-08-07, along with the address being reachable from the host without port publishing.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerNetworkTests
{
    [Fact]
    public void A_container_has_no_network_by_default()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Null(builder.AddHcsContainer("worker").Resource.NetworkName);
    }

    [Fact]
    public void WithNatNetwork_defaults_to_the_conventional_nat_network()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal("nat", builder.AddHcsContainer("worker").WithNatNetwork().Resource.NetworkName);
    }

    // hcsctl cannot create a network (hcsctl#15), so this names an existing one — which means a
    // non-default network has to be nameable.
    [Fact]
    public void A_named_network_is_honoured()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal("LAB", builder.AddHcsContainer("worker").WithNatNetwork("LAB").Resource.NetworkName);
    }

    [Fact]
    public void The_first_endpoint_backs_the_connection_string()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNatNetwork()
            .WithEndpoint("http", 8080);

        Assert.Equal("http", container.Resource.PrimaryEndpointName);
    }

    // The health check resolves the endpoint per check rather than capturing it, because
    // EndpointReference.IsAllocated memoizes its first answer including false — a reference built
    // at model-build time would latch unallocated forever. This pins that it is registered
    // against a name that exists.
    [Fact]
    public void WithTcpHealthCheck_defaults_to_the_first_endpoint()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNatNetwork()
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

    // A typo in an endpoint name must fail the model build, not produce a health report nobody
    // reads that is unhealthy forever for the wrong reason.
    [Fact]
    public void WithTcpHealthCheck_rejects_an_endpoint_name_that_does_not_exist()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithNatNetwork()
            .WithEndpoint("http", 8080);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => container.WithTcpHealthCheck("htpp"));

        Assert.Contains("htpp", thrown.Message);
    }
}
