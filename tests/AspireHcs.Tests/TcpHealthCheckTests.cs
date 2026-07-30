using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace AspireHcs.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class TcpHealthCheckTests
{
    [Fact]
    public void WithTcpHealthCheck_annotates_the_resource_and_registers_the_check()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithNatNetwork()
            .WithEndpoint("ssh", targetPort: 22)
            .WithTcpHealthCheck();

        // The annotation is what stops Aspire declaring the resource ready the instant it
        // reports Running; the registration is what the monitor resolves the key against.
        // Both are required — an annotation whose key matches no registration reports
        // unhealthy forever.
        HealthCheckAnnotation annotation = Assert.Single(vm.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("vm_ssh_tcp_check", annotation.Key);

        HealthCheckServiceOptions options = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        Assert.Contains(options.Registrations, r => r.Name == "vm_ssh_tcp_check");
    }

    [Fact]
    public void WithTcpHealthCheck_targets_a_named_endpoint_and_rejects_unknown_ones()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm")
            .WithNatNetwork()
            .WithEndpoint("ssh", targetPort: 22)
            .WithEndpoint("api", targetPort: 8080)
            .WithTcpHealthCheck("api");

        Assert.Equal("vm_api_tcp_check", Assert.Single(vm.Resource.Annotations.OfType<HealthCheckAnnotation>()).Key);

        InvalidOperationException unknown = Assert.Throws<InvalidOperationException>(
            () => vm.WithTcpHealthCheck("nope"));
        Assert.Contains("no endpoint named 'nope'", unknown.Message);
    }

    [Fact]
    public void WithTcpHealthCheck_requires_an_endpoint_to_target()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsVirtualMachineResource> vm = builder.AddHcsVm("vm");

        Assert.Throws<InvalidOperationException>(() => vm.WithTcpHealthCheck());
    }

    [Fact]
    public async Task Check_is_unhealthy_until_the_endpoint_is_allocated()
    {
        HcsVirtualMachineResource resource = new("vm");
        resource.Annotations.Add(Endpoint("ssh", 22));

        HealthCheckResult result = await Check(resource, "ssh").CheckHealthAsync(new HealthCheckContext());

        // Before the guest leases an address there is nothing to connect to. Reporting healthy
        // here would release WaitFor dependents against a VM that has no address at all.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not allocated", result.Description);
    }

    [Fact]
    public async Task Check_is_healthy_only_while_something_is_listening()
    {
        using TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        HcsVirtualMachineResource resource = new("vm");
        EndpointAnnotation endpoint = Endpoint("ssh", port);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", port);
        resource.Annotations.Add(endpoint);

        IHealthCheck check = Check(resource, "ssh");

        HealthCheckResult listening = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, listening.Status);

        // A refused connection proves the stack is up but nothing is serving — which is exactly
        // the gap this check exists to close, so it must not count as ready. The reference Kali
        // image ships sshd disabled and lands here.
        listener.Stop();
        HealthCheckResult refused = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, refused.Status);
        Assert.Contains("not accepting connections", refused.Description);
    }

    [Fact]
    public async Task Check_gives_up_on_a_dropped_connection_rather_than_stalling()
    {
        HcsVirtualMachineResource resource = new("vm");
        EndpointAnnotation endpoint = Endpoint("ssh", 22);
        // TEST-NET-1 (RFC 5737): routable nowhere, so the SYN is dropped rather than refused.
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "192.0.2.1", 22);
        resource.Annotations.Add(endpoint);

        HealthCheckResult result = await Check(resource, "ssh", TimeSpan.FromMilliseconds(250))
            .CheckHealthAsync(new HealthCheckContext());

        // Without its own timeout this would block for the OS SYN-retry budget (~21 s on
        // Windows), far longer than the health monitor's polling interval.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("did not answer", result.Description);
    }

    private static IHealthCheck Check(HcsVirtualMachineResource resource, string endpointName, TimeSpan? timeout = null)
        => new TcpEndpointHealthCheck(resource, endpointName, timeout ?? TimeSpan.FromSeconds(3));

    private static EndpointAnnotation Endpoint(string name, int targetPort)
        => new(ProtocolType.Tcp, name: name, targetPort: targetPort, isProxied: false);
}
