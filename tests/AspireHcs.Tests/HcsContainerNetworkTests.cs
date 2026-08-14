using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// #41's measurement (2026-08-07, on `nat`): a static HNS endpoint programs a container's stack
// directly, so the address is known when the container is CREATED — and it is reachable from the
// host without port publishing. That inversion holds on NAT networks only. An ICS network — the
// Default Switch, the default since #60 — leases the address after the guest starts, so the
// instance waits for it there; ContainerAddressLeaseTests pins that wait (#63).
[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerNetworkTests
{
    [Fact]
    public void A_container_has_no_network_by_default()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Null(builder.AddHcsContainer("worker").Resource.NetworkName);
    }

    // The literal string is asserted on purpose: it is the wire value hcsctl resolves by name,
    // and it must be the SAME one the VM side defaults to — co-location is the point (#58, #60).
    [Fact]
    public void WithNetwork_defaults_to_the_Default_Switch_shared_with_VMs()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        Assert.Equal("Default Switch", builder.AddHcsContainer("worker").WithNetwork().Resource.NetworkName);
        Assert.Equal(HcsNetwork.DefaultSwitchName, builder.AddHcsVm("vm").WithNetwork().Resource.NetworkName);
    }

    // AspireHcs names an existing network rather than provisioning one, so a non-default network
    // has to be nameable. `nat` in particular stays expressible: a container
    // placed there deliberately cannot see the Default Switch residents (#58).
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

    // The health check resolves the endpoint per check rather than capturing it, because
    // EndpointReference.IsAllocated memoizes its first answer including false — a reference built
    // at model-build time would latch unallocated forever. This pins that it is registered
    // against a name that exists.
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

    // A typo in an endpoint name must fail the model build, not produce a health report nobody
    // reads that is unhealthy forever for the wrong reason.
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
