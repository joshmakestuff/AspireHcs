using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using AspireHcs.Hcn;
using AspireHcs.Hcs;
using AspireHcs.Hcs.Schema;
using AspireHcs.Storage;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #4 groundwork, verified at the HcsClient/HcnClient level before the Aspire wiring:
// attach a NIC on the Default Switch (ICS) network, read the deterministic IP that HNS
// reserved for the endpoint, let the guest lease it via DHCP, and prove TCP reachability.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class HcnNetworkingTests(ITestOutputHelper output) : IDisposable
{
    private const int MemoryMb = 2048;

    private static string? BaseVhdx => Environment.GetEnvironmentVariable("HCS_TEST_VHDX");

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "AspireHcsIntegration", Guid.NewGuid().ToString("N"));

    [SkippableFact]
    public async Task Guest_leases_the_reserved_ip_and_is_reachable_from_host()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        Guid networkId = HcnClient.FindIcsNetworkId();
        string vmId = $"AspireHcsIt-{Guid.NewGuid():N}";
        Guid endpointId = Guid.NewGuid();
        string mac = $"02-15-5D-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}";
        HcnClient.CreateDhcpEndpoint(networkId, endpointId, mac, owner: "AspireHcs.IntegrationTests");

        try
        {
            output.WriteLine($"Network {networkId}");
            Directory.CreateDirectory(_workDir);
            string diffPath = Path.Combine(_workDir, "boot-diff.vhdx");
            VirtualDisk.CreateDifferencing(BaseVhdx!, diffPath);
            HcsClient.GrantVmAccess(vmId, diffPath);
            HcsClient.GrantVmAccess(vmId, BaseVhdx!);

            ComputeSystemDocument document = BuildDocument(diffPath, endpointId, mac);
            using HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync(vmId, document);
            try
            {
                await vm.StartAsync();
                await vm.WaitForGuestReadyAsync(MemoryMb, TimeSpan.FromMinutes(2));

                // The guest's DHCP lease surfaces in the HCN endpoint properties (HNS learns
                // it against our MAC) — typically within seconds of guest-ready.
                IPAddress? discovered = null;
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(90);
                while (discovered is null && DateTime.UtcNow < discoveryDeadline)
                {
                    string? props = HcnClient.QueryEndpointProperties(endpointId);
                    string? propIp = props is null ? null : System.Text.Json.Nodes.JsonNode.Parse(props)?["IPAddress"]?.GetValue<string>();
                    if (propIp is not null)
                    {
                        discovered = IPAddress.Parse(propIp);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }

                Assert.NotNull(discovered);
                IPAddress ip = discovered;
                output.WriteLine($"Guest IP {ip} discovered via endpoint properties");

                // The guest needs a few seconds post-ready to complete its DHCP handshake.
                // Reachability: a refused TCP SYN still proves the guest network stack
                // answered at that address; only timeouts mean unreachable.
                bool reachable = false;
                string detail = "";
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (!reachable && DateTime.UtcNow < deadline)
                {
                    using TcpClient client = new();
                    try
                    {
                        await client.ConnectAsync(ip, 22).WaitAsync(TimeSpan.FromSeconds(5));
                        reachable = true;
                        detail = "TCP 22 accepted — a service is listening";
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        reachable = true;
                        detail = "TCP 22 refused — no listener, but the guest answered";
                    }
                    catch (Exception)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3));
                    }
                }

                output.WriteLine(detail);
                Assert.True(reachable, $"guest at {ip} never answered TCP within 90s of guest-ready");
            }
            finally
            {
                await vm.TerminateAsync();
            }
        }
        finally
        {
            HcnClient.DeleteEndpoint(endpointId);
            // The grants are persistent ACEs on the files; without these the runner's base image
            // accumulates one dead VM identity per test run (#16).
            HcsClient.RevokeVmAccess(vmId, Path.Combine(_workDir, "boot-diff.vhdx"));
            HcsClient.RevokeVmAccess(vmId, BaseVhdx!);
        }
    }

    private static ComputeSystemDocument BuildDocument(string vhdxPath, Guid endpointId, string mac) => new()
    {
        SchemaVersion = new() { Major = 2, Minor = 5 },
        Owner = "AspireHcs.IntegrationTests",
        ShouldTerminateOnLastHandleClosed = true,
        VirtualMachine = new()
        {
            Chipset = new() { Uefi = new() { BootThis = new() { DevicePath = "Primary disk", DiskNumber = 0 } } },
            ComputeTopology = new()
            {
                Memory = new() { SizeInMB = MemoryMb },
                Processor = new() { Count = 2 },
            },
            Devices = new()
            {
                Scsi = new()
                {
                    ["Primary disk"] = new() { Attachments = new() { ["0"] = new() { Path = vhdxPath } } },
                },
                NetworkAdapters = new()
                {
                    ["ext"] = new() { EndpointId = endpointId.ToString(), MacAddress = mac },
                },
            },
            Services = new() { Shutdown = new() },
        },
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
