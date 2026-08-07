using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// The statistics and process documents are hcsshim structs marshalled straight through, so their
// wire names come from hcsshim's tags — NOT from hcsctl, and not from the Go field names, which
// differ (the field is UsageCommitBytes; the wire name is MemoryUsageCommitBytes).
//
// Every payload below was captured verbatim from a live container on 2026-08-07. That is the
// point: a binding tested against a document I invented would only prove I can spell my own
// property names.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlStatsBindingTests
{
    // Captured from `hcsctl container stats --json` against a running servercore container with
    // a NAT endpoint. Note what is ABSENT: Processor carries only TotalRuntime100ns, because the
    // user/kernel splits were zero and every field is omitempty.
    private const string LiveStats = """
        {
          "command": "container stats",
          "id": "stats-net",
          "ok": true,
          "statistics": {
            "Timestamp": "2026-08-07T16:40:20.4091491Z",
            "ContainerStartTime": "2026-08-07T16:40:17.3530983Z",
            "Uptime100ns": 30560508,
            "Memory": {
              "MemoryUsageCommitBytes": 1089224704,
              "MemoryUsageCommitPeakBytes": 2156318720,
              "MemoryUsagePrivateWorkingSetBytes": 366006272
            },
            "Processor": { "TotalRuntime100ns": 17454902 },
            "Storage": {
              "ReadCountNormalized": 159,
              "ReadSizeBytes": 1007616,
              "WriteCountNormalized": 20,
              "WriteSizeBytes": 155648
            },
            "Network": [
              {
                "BytesReceived": 56402,
                "BytesSent": 5787,
                "PacketsReceived": 51,
                "PacketsSent": 58,
                "EndpointId": "918169B1-1264-4B26-9CB5-4988CE1DC3E0",
                "InstanceId": "C917E626-C251-4BEC-99CA-53C4C856EA43"
              }
            ]
          }
        }
        """;

    [Fact]
    public void A_live_statistics_document_binds_every_field_we_read()
    {
        HcsCtlStatsDocument document = JsonSerializer.Deserialize(LiveStats, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        Assert.True(document.Ok);
        HcsCtlStatistics stats = Assert.IsType<HcsCtlStatistics>(document.Statistics);

        Assert.Equal(30560508, stats.Uptime100ns);
        Assert.Equal(1089224704, stats.Memory!.CommitBytes);
        Assert.Equal(2156318720, stats.Memory.CommitPeakBytes);
        Assert.Equal(366006272, stats.Memory.PrivateWorkingSetBytes);
        Assert.Equal(17454902, stats.Processor!.TotalRuntime100ns);
        Assert.Equal(159, stats.Storage!.ReadCount);
        Assert.Equal(155648, stats.Storage.WriteBytes);

        HcsCtlNetworkStats network = Assert.Single(stats.Network);
        Assert.Equal(56402, network.BytesReceived);
        Assert.Equal(5787, network.BytesSent);
    }

    // HCS reports 100-nanosecond ticks, which is also .NET's TimeSpan tick. Getting this wrong by
    // a factor of 100 either way produces a plausible-looking number, which is the dangerous kind.
    [Fact]
    public void Uptime_ticks_convert_to_the_duration_hcsctl_prints()
    {
        HcsCtlStatsDocument document = JsonSerializer.Deserialize(LiveStats, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        // 30,560,508 ticks x 100 ns = 3.056 s, and the capture was taken ~3 s after start.
        Assert.Equal(3.0560508, document.Statistics!.Uptime.TotalSeconds, precision: 4);
    }

    // A container with no endpoint has no Network key at all — measured, not supposed. Binding it
    // to null would NRE the property formatter on the most ordinary container there is.
    [Fact]
    public void A_container_with_no_network_binds_to_an_empty_collection()
    {
        const string noNetwork = """
            {"ok":true,"id":"c","statistics":{"Uptime100ns":20657106,"Memory":{"MemoryUsageCommitBytes":1088274432}}}
            """;

        HcsCtlStatsDocument document = JsonSerializer.Deserialize(noNetwork, HcsCtlJsonContext.Default.HcsCtlStatsDocument)!;

        Assert.Empty(document.Statistics!.Network);
        Assert.Null(document.Statistics.Storage);
        Assert.Equal(0, document.Statistics.Processor?.TotalRuntime100ns ?? 0);
    }

    // Captured from `hcsctl container ps --json`. Two rows on purpose: one with both CPU fields
    // present, one with neither — omitempty means a zero counter is an absent key.
    private const string LiveProcesses = """
        {
          "command": "container ps",
          "id": "stats-probe",
          "ok": true,
          "processes": [
            {
              "CreateTimestamp": "2026-08-07T16:39:41.9911421Z",
              "ImageName": "fontdrvhost.exe",
              "MemoryCommitBytes": 954368,
              "MemoryWorkingSetPrivateBytes": 536576,
              "MemoryWorkingSetSharedBytes": 2691072,
              "ProcessId": 240
            },
            {
              "CreateTimestamp": "2026-08-07T16:39:41.6164725Z",
              "ImageName": "csrss.exe",
              "KernelTime100ns": 468750,
              "MemoryCommitBytes": 2326528,
              "ProcessId": 292,
              "UserTime100ns": 156250
            }
          ]
        }
        """;

    [Fact]
    public void A_live_process_list_binds()
    {
        HcsCtlProcessListDocument document =
            JsonSerializer.Deserialize(LiveProcesses, HcsCtlJsonContext.Default.HcsCtlProcessListDocument)!;

        Assert.Equal(2, document.Processes.Count);

        HcsCtlGuestProcess csrss = document.Processes.Single(p => p.ProcessId == 292);
        Assert.Equal("csrss.exe", csrss.ImageName);
        Assert.Equal(2326528, csrss.MemoryCommitBytes);
        Assert.Equal(TimeSpan.FromTicks(468750 + 156250), csrss.CpuTime);
    }

    // A process with no CPU counters must read as zero, not as missing data. Both keys are absent
    // on the first row above.
    [Fact]
    public void A_process_with_no_cpu_counters_reads_as_zero()
    {
        HcsCtlProcessListDocument document =
            JsonSerializer.Deserialize(LiveProcesses, HcsCtlJsonContext.Default.HcsCtlProcessListDocument)!;

        Assert.Equal(TimeSpan.Zero, document.Processes.Single(p => p.ProcessId == 240).CpuTime);
    }

    // The caveat that shapes any UI built on this: HCS reports no parent process id, so the list
    // is flat and cannot be made a tree. If a ParentProcessId ever appears on the wire, this test
    // is where the assumption should be revisited.
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
