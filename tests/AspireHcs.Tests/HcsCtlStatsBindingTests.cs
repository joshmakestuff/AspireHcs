using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// Contract 3: `container stats` passes the raw v2 HCS property reply through under
// "statistics" — an envelope of system identity fields with the counters nested one level
// down, under "Statistics". `container ps` re-emits hcsctl's typed rows, five fields each,
// always present. The PascalCase names inside "statistics" are HCS's, not hcsctl's.
//
// Every payload below was captured from a live unelevated hyperv container
// (nanoserver:ltsc2025, hcsctl v0.5.0, 2026-08-25); the process list is a subset of the
// captured rows. Only the store path in RuntimeImagePath was generalized.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlStatsBindingTests
{
    // Captured from `hcsctl container stats --json` against a running container with no
    // endpoint. Processor carries only TotalRuntime100ns: HCS omits zero counters. Network is
    // an EMPTY array even so — the schema-1 per-endpoint counters did not survive into v2, so
    // nothing binds it.
    private const string LiveStats = """
        {
          "command": "container stats",
          "id": "m91",
          "ok": true,
          "statistics": {
            "Id": "m91",
            "SystemType": "Container",
            "RuntimeOsType": "Windows",
            "Name": "m91",
            "Owner": "hcsctl",
            "RuntimeId": "e104557f-d094-4ad1-9786-781da77eb6fd",
            "RuntimeImagePath": "C:\\hcs\\store\\layers\\dde2700babae600587126dee2c652b3dfa6a0f51e1112fc1585d87f87ca09272\\UtilityVM",
            "State": "Running",
            "Statistics": {
              "Timestamp": "2026-08-25T23:32:19.0685861Z",
              "ContainerStartTime": "2026-08-25T23:32:10.7938384Z",
              "Uptime100ns": 82747477,
              "Processor": {
                "TotalRuntime100ns": 25549260
              },
              "Memory": {
                "MemoryUsageCommitBytes": 1626234880,
                "MemoryUsageCommitPeakBytes": 1626234880,
                "MemoryUsagePrivateWorkingSetBytes": 376479744
              },
              "Storage": {
                "ReadCountNormalized": 17120,
                "ReadSizeBytes": 118282240,
                "WriteCountNormalized": 233,
                "WriteSizeBytes": 1570816
              },
              "Network": []
            },
            "MemoryTopology": {
              "AccessTrackingEnabled": false,
              "HardwareAccessTrackingSupported": false,
              "DeviceAccessTrackingSupported": false,
              "AccessTrackingRangeSizeInPages": 0,
              "AccessTrackingBitmapSizeInBits": 0,
              "GpaMappingSizeInPages": 0
            },
            "ServiceSessionId": 1
          }
        }
        """;

    [Fact]
    public void A_live_statistics_document_binds_every_field_we_read()
    {
        HcsCtlStatsDocument document = JsonSerializer.Deserialize(LiveStats, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        Assert.True(document.Ok);
        HcsCtlStatistics stats = Assert.IsType<HcsCtlStatistics>(document.Properties!.Statistics);

        Assert.Equal(82747477, stats.Uptime100ns);
        Assert.Equal(1626234880, stats.Memory!.CommitBytes);
        Assert.Equal(1626234880, stats.Memory.CommitPeakBytes);
        Assert.Equal(376479744, stats.Memory.PrivateWorkingSetBytes);
        Assert.Equal(25549260, stats.Processor!.TotalRuntime100ns);
        Assert.Equal(17120, stats.Storage!.ReadCount);
        Assert.Equal(118282240, stats.Storage.ReadBytes);
        Assert.Equal(233, stats.Storage.WriteCount);
        Assert.Equal(1570816, stats.Storage.WriteBytes);
    }

    // HCS reports 100-nanosecond ticks, which is also .NET's TimeSpan tick.
    [Fact]
    public void Uptime_ticks_convert_to_the_duration_hcsctl_prints()
    {
        HcsCtlStatsDocument document = JsonSerializer.Deserialize(LiveStats, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        // 82,747,477 ticks x 100 ns = 8.27 s, and the capture was taken ~8 s after start.
        Assert.Equal(8.2747477, document.Properties!.Statistics!.Uptime.TotalSeconds, precision: 4);
    }

    // HCS omits zero counters inside Statistics, so a sparse reply must bind with nulls and
    // zeros, not fail or NRE the property formatter.
    [Fact]
    public void A_sparse_statistics_reply_binds_with_defaults()
    {
        const string sparse = """
            {"ok":true,"id":"c","statistics":{"Id":"c","State":"Running","Statistics":{"Uptime100ns":20657106,"Memory":{"MemoryUsageCommitBytes":1088274432}}}}
            """;

        HcsCtlStatsDocument document = JsonSerializer.Deserialize(sparse, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        HcsCtlStatistics stats = document.Properties!.Statistics!;
        Assert.Null(stats.Storage);
        Assert.Equal(0, stats.Processor?.TotalRuntime100ns ?? 0);
        Assert.Equal(1088274432, stats.Memory!.CommitBytes);
    }

    // The envelope can arrive without a Statistics object at all (a reply for a system that is
    // not running). The consumer's `Properties?.Statistics is { }` guard depends on null here.
    [Fact]
    public void An_envelope_without_statistics_binds_to_null()
    {
        const string headerOnly = """
            {"ok":true,"id":"c","statistics":{"Id":"c","SystemType":"Container","State":"Stopped"}}
            """;

        HcsCtlStatsDocument document = JsonSerializer.Deserialize(headerOnly, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        Assert.NotNull(document.Properties);
        Assert.Null(document.Properties.Statistics);
    }

    // Captured from `hcsctl container ps --json` (three of the seventeen captured rows). All
    // five fields are always present — hcsctl re-marshals its typed row, so a zero counter is
    // an explicit 0, not an absent key.
    private const string LiveProcesses = """
        {
          "command": "container ps",
          "id": "m91",
          "ok": true,
          "processes": [
            {
              "ProcessId": 344,
              "ImageName": "smss.exe",
              "UserTime100ns": 156250,
              "KernelTime100ns": 156250,
              "MemoryCommitBytes": 667648
            },
            {
              "ProcessId": 588,
              "ImageName": "csrss.exe",
              "UserTime100ns": 0,
              "KernelTime100ns": 0,
              "MemoryCommitBytes": 1359872
            },
            {
              "ProcessId": 704,
              "ImageName": "lsass.exe",
              "UserTime100ns": 781250,
              "KernelTime100ns": 625000,
              "MemoryCommitBytes": 3133440
            }
          ]
        }
        """;

    [Fact]
    public void A_live_process_list_binds()
    {
        HcsCtlProcessListDocument document =
            JsonSerializer.Deserialize(LiveProcesses, HcsCtlJsonContext.Default.HcsCtlProcessListDocument)!;

        Assert.Equal(3, document.Processes.Count);

        HcsCtlGuestProcess lsass = document.Processes.Single(p => p.ProcessId == 704);
        Assert.Equal("lsass.exe", lsass.ImageName);
        Assert.Equal(3133440, lsass.MemoryCommitBytes);
        Assert.Equal(TimeSpan.FromTicks(781250 + 625000), lsass.CpuTime);
    }

    [Fact]
    public void A_process_with_zero_cpu_counters_reads_as_zero()
    {
        HcsCtlProcessListDocument document =
            JsonSerializer.Deserialize(LiveProcesses, HcsCtlJsonContext.Default.HcsCtlProcessListDocument)!;

        Assert.Equal(TimeSpan.Zero, document.Processes.Single(p => p.ProcessId == 588).CpuTime);
    }

    // HCS reports no parent process id, so the list is flat and cannot be made a tree. If a
    // ParentProcessId appears on the wire, this test is where to revisit that. Restored after
    // #95 deleted it: it proves an absence, not behavior, but it is the only enforceable home
    // for the flat-rendering decision (owner's call, 2026-08-30).
    [Fact]
    public void The_process_shape_carries_no_parent_pid()
    {
        Assert.DoesNotContain(
            typeof(HcsCtlGuestProcess).GetProperties(),
            p => p.Name.Contains("Parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_empty_process_list_binds_to_empty_rather_than_null()
    {
        HcsCtlProcessListDocument document = JsonSerializer.Deserialize(
            """{"ok":true,"id":"c","processes":null}""",
            HcsCtlJsonContext.Default.HcsCtlProcessListDocument)!;

        Assert.Empty(document.Processes);
    }
}
