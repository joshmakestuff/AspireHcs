using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using AspireHcs.Hcs;
using AspireHcs.Hcs.Schema;
using AspireHcs.Storage;
using Windows.Win32.System.HostComputeSystem;
using Xunit;

namespace AspireHcs.IntegrationTests;

// Issue #2 acceptance: HcsClient round-trips against a real VHDX, and error paths surface
// the HRESULT plus the HCS result-document message. Requires Hyper-V plus either elevation
// or Hyper-V Administrators membership; set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX
// (booted via a differencing child, so the base image is never mutated).
[SupportedOSPlatform("windows10.0.17763")]
public sealed class HcsClientRoundTripTests : IDisposable
{
    private const int MemoryMb = 2048;

    private static string? BaseVhdx => Environment.GetEnvironmentVariable("HCS_TEST_VHDX");

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "AspireHcsIntegration", Guid.NewGuid().ToString("N"));

    [SkippableFact]
    public async Task Create_start_guest_ready_shutdown_round_trip()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX to run HCS integration tests.");

        string vmId = $"AspireHcsIt-{Guid.NewGuid():N}";
        Directory.CreateDirectory(_workDir);
        string diffPath = Path.Combine(_workDir, "boot-diff.vhdx");

        VirtualDisk.CreateDifferencing(BaseVhdx!, diffPath);
        HcsClient.GrantVmAccess(vmId, diffPath);
        HcsClient.GrantVmAccess(vmId, BaseVhdx!);

        List<HcsNotification> notifications = [];
        using HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync(vmId, BuildDocument(diffPath));
        vm.Notification += (_, notification) =>
        {
            lock (notifications)
            {
                notifications.Add(notification);
            }
        };

        await vm.StartAsync();
        await vm.WaitForGuestReadyAsync(MemoryMb, TimeSpan.FromMinutes(2));

        string? properties = await vm.GetPropertiesAsync();
        Assert.NotNull(properties);
        Assert.Equal("Running", JsonNode.Parse(properties)?["State"]?.GetValue<string>());

        await vm.ShutdownAsync();

        // The callback contract: a graceful shutdown must surface a SystemExited notification.
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        bool exited = false;
        while (!exited && DateTime.UtcNow < deadline)
        {
            lock (notifications)
            {
                exited = notifications.Any(n => n.Type == HCS_EVENT_TYPE.HcsEventSystemExited);
            }

            if (!exited)
            {
                await Task.Delay(500);
            }
        }

        Assert.True(exited, "expected an HcsEventSystemExited notification after graceful shutdown");
    }

    [SkippableFact]
    public async Task Start_with_missing_disk_surfaces_hresult_and_service_message()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to run HCS integration tests (this test needs the HCS service, not the image).");

        Directory.CreateDirectory(_workDir);
        ComputeSystemDocument document = BuildDocument(Path.Combine(_workDir, "does-not-exist.vhdx"));

        // Empirical: HcsCreateComputeSystem accepts a config whose disk doesn't exist;
        // validation is deferred to start.
        using HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync($"AspireHcsIt-{Guid.NewGuid():N}", document);

        HcsException ex = await Assert.ThrowsAsync<HcsException>(() => vm.StartAsync());

        Assert.True(ex.HResult != 0, "HRESULT must be preserved on the exception");
        Assert.Contains("0x", ex.Message);
        Assert.True(ex.Message.Contains('—'), $"expected the HCS service's own error text appended after the code, got: {ex.Message}");
    }

    private static ComputeSystemDocument BuildDocument(string vhdxPath) => new()
    {
        // Services (for graceful shutdown) is NewInVersion 2.5; older versions ignore it.
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
            // Best-effort cleanup; a leaked temp diff disk is harmless.
        }
    }
}
