using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace AspireHcs.IntegrationTests;

// Issue #3 acceptance: the sample AppHost boots a real VM as an Aspire resource, the
// resource reaches Running, and ResourceReadyEvent fires once the guest OS is up
// (which is what makes WaitFor(vm) release dependents).
[SupportedOSPlatform("windows10.0.17763")]
public sealed class AspireResourceRoundTripTests
{
    [SkippableFact]
    public async Task Sample_apphost_boots_vm_to_running_and_ready()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HCS_TEST_VHDX")),
            "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        IDistributedApplicationTestingBuilder appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        appHost.Eventing.Subscribe<ResourceReadyEvent>((@event, _) =>
        {
            if (@event.Resource.Name == "appliance")
            {
                ready.TrySetResult();
            }
            return Task.CompletedTask;
        });

        await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(
            "appliance", KnownResourceStates.Running, cts.Token);

        await ready.Task.WaitAsync(cts.Token);

        // Issue #4 acceptance: the endpoint and connection string resolve to the guest's
        // DHCP-leased address, and the guest is reachable there from the host. A refused
        // TCP SYN proves reachability (the guest's stack answered); only timeouts fail.
        Uri endpoint = app.GetEndpoint("appliance", "ssh");
        string? connectionString = await app.GetConnectionStringAsync("appliance", cancellationToken: cts.Token);
        Assert.Equal($"{endpoint.Host}:{endpoint.Port}", connectionString);
        Assert.Equal(22, endpoint.Port);

        using System.Net.Sockets.TcpClient client = new();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            // Reachable, nothing listening on 22 in the stock image — acceptable.
        }

        await app.StopAsync(cts.Token);
    }
}
