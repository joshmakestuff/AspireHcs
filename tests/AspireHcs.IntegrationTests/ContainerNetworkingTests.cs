using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// #41 acceptance. The claim this issue was opened to confirm-or-refute — that a static HNS
// endpoint programs a container's stack, with no DHCP-lease discovery — is CONFIRMED, and these
// pin it end to end through the Aspire resource rather than through hcsctl alone.
//
// Needs an image with PowerShell, so servercore rather than nanoserver.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ContainerNetworkingTests(ITestOutputHelper output)
{
    private const string NetworkVariable = "ASPIREHCS_TEST_NETWORK";
    private const string ServerCoreVariable = "ASPIREHCS_TEST_SERVERCORE_IMAGE";
    private const int GuestPort = 8080;

    private static (string HcsCtl, string Store, string Image, string Network) RequireNetworkFixture()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();

        string? image = Environment.GetEnvironmentVariable(ServerCoreVariable);
        Skip.If(string.IsNullOrWhiteSpace(image),
            $"Set {ServerCoreVariable} to a servercore image in the store — the listener needs PowerShell, " +
            "which nanoserver does not carry.");

        string network = Environment.GetEnvironmentVariable(NetworkVariable) ?? "nat";
        return (hcsctl, store, image!, network);
    }

    /// <summary>
    /// Writes a PowerShell TCP listener to a host directory, to be bind-mounted into the guest.
    /// A script on a mount rather than an inline <c>--cmd</c> because the quoting would otherwise
    /// cross PowerShell, argv and cmd.exe — three chances to mangle it, none of them the subject
    /// of this test.
    /// </summary>
    private static string WriteListenerScript()
    {
        string directory = Directory.CreateTempSubdirectory("aspirehcs-net").FullName;
        // $$ so that a single brace is literal and {{...}} interpolates — the script is full of
        // PowerShell blocks, and $"""...""" would read the first { as an interpolation hole.
        File.WriteAllText(Path.Combine(directory, "listener.ps1"), $$"""
            $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, {{GuestPort}})
            $l.Start()
            Write-Output "LISTENING"
            $deadline = (Get-Date).AddSeconds(180)
            while ((Get-Date) -lt $deadline) {
              if ($l.Pending()) {
                $c = $l.AcceptTcpClient()
                $s = $c.GetStream()
                $b = [Text.Encoding]::ASCII.GetBytes("HELLO-FROM-CONTAINER")
                $s.Write($b, 0, $b.Length); $s.Flush(); $c.Close()
              }
              Start-Sleep -Milliseconds 200
            }
            $l.Stop()
            """);
        return directory;
    }

    // The whole of #41's first task, through the Aspire surface: the endpoint resolves to an
    // address, and something on the host can actually talk to it.
    [SkippableFact]
    public async Task An_endpoint_resolves_to_an_address_the_host_can_reach()
    {
        (string hcsctl, string store, string image, string network) = RequireNetworkFixture();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string scripts = WriteListenerScript();
        IDistributedApplicationTestingBuilder appHost = await ContainerFixture.SampleAppHostAsync(
            @"powershell -NoProfile -ExecutionPolicy Bypass -File C:\scripts\listener.ps1", cts.Token);

        HcsContainerResource resource = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(resource)
            .WithImage(image)
            .WithBindMount(scripts, @"C:\scripts", isReadOnly: true)
            .WithNatNetwork(network)
            .WithEndpoint("probe", GuestPort);

        string containerId = resource.ContainerId;

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, cts.Token);

            // The endpoint resolved — through Aspire's own model, not by reading hcsctl's output.
            Uri endpoint = app.GetEndpoint("worker", "probe");
            output.WriteLine($"endpoint: {endpoint.Host}:{endpoint.Port}");
            Assert.Equal(GuestPort, endpoint.Port);
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Host));

            // No CIDR prefix survived into the host string — "172.17.163.120/20" parses as a Uri
            // host and then fails to connect, which is a maddening way to find this bug.
            Assert.DoesNotContain('/', endpoint.Host);

            // And the connection string agrees with it.
            string? connectionString = await app.GetConnectionStringAsync("worker", cancellationToken: cts.Token);
            Assert.Equal($"{endpoint.Host}:{endpoint.Port}", connectionString);

            string banner = await ReadBannerAsync(endpoint.Host, endpoint.Port, cts.Token);
            output.WriteLine($"host -> container said: {banner}");
            Assert.Equal("HELLO-FROM-CONTAINER", banner);

            await app.StopAsync(cts.Token);
        }

        Assert.DoesNotContain(containerId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));
        Directory.Delete(scripts, recursive: true);
    }

    // Readiness. For a container this is the only gate there is — start already implies the guest
    // is up — so WaitFor releasing against a container that is not serving would be invisible
    // without this.
    [SkippableFact]
    public async Task WaitFor_releases_only_once_the_container_is_serving()
    {
        (string hcsctl, string store, string image, string network) = RequireNetworkFixture();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string scripts = WriteListenerScript();
        IDistributedApplicationTestingBuilder appHost = await ContainerFixture.SampleAppHostAsync(
            @"powershell -NoProfile -ExecutionPolicy Bypass -File C:\scripts\listener.ps1", cts.Token);

        HcsContainerResource resource = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(resource)
            .WithImage(image)
            .WithBindMount(scripts, @"C:\scripts", isReadOnly: true)
            .WithNatNetwork(network)
            .WithEndpoint("probe", GuestPort)
            .WithTcpHealthCheck();

        string containerId = resource.ContainerId;

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);

            // Healthy is what WaitFor waits on, and it is only reached once the TCP check
            // connects — i.e. once the guest is genuinely serving, not merely running.
            await app.ResourceNotifications.WaitForResourceHealthyAsync("worker", cts.Token);

            Uri endpoint = app.GetEndpoint("worker", "probe");
            Assert.Equal("HELLO-FROM-CONTAINER", await ReadBannerAsync(endpoint.Host, endpoint.Port, cts.Token));

            await app.StopAsync(cts.Token);
        }

        Assert.DoesNotContain(containerId, await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token));
        Directory.Delete(scripts, recursive: true);
    }

    // Endpoints without a network can never resolve. Caught before anything is created, so the
    // failure does not leave a compute system behind to clean up.
    [SkippableFact]
    public async Task An_endpoint_without_a_network_fails_before_anything_is_created()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string[] before = await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token);

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ver", cts.Token);
        HcsContainerResource resource = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(resource).WithEndpoint("orphan", 9999);

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.FailedToStart, cts.Token);
            await app.StopAsync(cts.Token);
        }

        Assert.Equal(before.Order(), (await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token)).Order());
    }

    private static async Task<string> ReadBannerAsync(string host, int port, CancellationToken cancellationToken)
    {
        // The listener polls every 200 ms, and a container that just reported Running may be a
        // beat ahead of its own guest process. Retry briefly rather than racing it.
        SocketException? last = null;
        for (int attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

                byte[] buffer = new byte[64];
                int read = await client.GetStream().ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
            catch (SocketException ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Never connected to {host}:{port}.", last);
    }
}
