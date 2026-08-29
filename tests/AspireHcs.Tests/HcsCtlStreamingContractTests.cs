using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// hcsctl's --stream-json turns stderr from raw progress lines into typed NDJSON records:
// {"stream":"progress","msg":…} for the tool, {"stream":"stdout"|"stderr","data":…} for guest
// output. These pin the parsing seam with a stand-in binary, since hcsctl cannot be made to
// violate its own contract on demand.
//
// These need no hcsctl and no HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlStreamingContractTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A stand-in for hcsctl that emits the given batch body (stdout and stderr).</summary>
    private HcsCtl FakeCtl(string batchBody)
    {
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off{Environment.NewLine}{batchBody}{Environment.NewLine}");
        return new HcsCtl(path);
    }

    /// <summary>Captures every parsed record so the routing can be asserted.</summary>
    private sealed class Collector : IProgress<HcsCtlStreamRecord>
    {
        public List<HcsCtlStreamRecord> Records { get; } = [];

        public void Report(HcsCtlStreamRecord value) => Records.Add(value);
    }

    [Fact]
    public async Task Stream_records_route_by_their_stream_tag()
    {
        HcsCtl fake = FakeCtl(
            "echo {\"ok\":true}" + Environment.NewLine +
            "echo {\"stream\":\"stdout\",\"data\":\"hello\"} 1>&2" + Environment.NewLine +
            "echo {\"stream\":\"stderr\",\"data\":\"oops\"} 1>&2" + Environment.NewLine +
            "echo {\"stream\":\"progress\",\"msg\":\"step\"} 1>&2");

        Collector collector = new();
        await fake.InvokeStreamingAsync(
            ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument, collector);

        // The count assertion doubles as the non-empty guard: if the fake's stderr redirection
        // failed, this reports zero and the routing checks below pass vacuously.
        Assert.Equal(3, collector.Records.Count);

        Assert.Equal("stdout", collector.Records[0].Stream);
        Assert.Equal("hello", collector.Records[0].Data);

        Assert.Equal("stderr", collector.Records[1].Stream);
        Assert.Equal("oops", collector.Records[1].Data);

        Assert.Equal("progress", collector.Records[2].Stream);
        Assert.Equal("step", collector.Records[2].Msg);
    }

    // The exec started record is what the pause gate latches (hcsctl#98): stream "exec",
    // event "started", the guest pid. Nothing else may satisfy IsExecStarted.
    [Fact]
    public async Task The_exec_started_record_parses_and_only_it_is_the_start_signal()
    {
        HcsCtl fake = FakeCtl(
            "echo {\"ok\":true}" + Environment.NewLine +
            "echo {\"stream\":\"progress\",\"msg\":\"creating\"} 1>&2" + Environment.NewLine +
            "echo {\"stream\":\"exec\",\"event\":\"started\",\"pid\":4242} 1>&2" + Environment.NewLine +
            "echo {\"stream\":\"stdout\",\"data\":\"started\"} 1>&2");

        Collector collector = new();
        await fake.InvokeStreamingAsync(
            ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument, collector);

        Assert.Equal(3, collector.Records.Count);

        HcsCtlStreamRecord started = Assert.Single(collector.Records, r => r.IsExecStarted);
        Assert.Equal("exec", started.Stream);
        Assert.Equal("started", started.Event);
        Assert.Equal(4242, started.Pid);
    }

    [Fact]
    public async Task Bare_text_under_stream_json_is_a_contract_violation()
    {
        HcsCtl fake = FakeCtl(
            "echo {\"ok\":true}" + Environment.NewLine +
            "echo this is not ndjson 1>&2");

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.InvokeStreamingAsync(
                ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument));

        Assert.Contains("not NDJSON", thrown.Message);
        // The offending line is quoted.
        Assert.Contains("this is not ndjson", thrown.Message);
    }

    [Fact]
    public async Task Non_ascii_data_survives_the_stream()
    {
        // The raw UTF-8 byte path over the process boundary is pinned by HcsCtlContractTests'
        // Non_ascii_survives_the_process_boundary (real binary). Here the fake emits the JSON
        // \u escape (ASCII on the wire, so cmd.exe's code page cannot mangle it) and the
        // assertion checks the record round-trips to the non-ASCII string.
        HcsCtl fake = FakeCtl(
            "echo {\"ok\":true}" + Environment.NewLine +
            "echo {\"stream\":\"stdout\",\"data\":\"caf\\u00e9\"} 1>&2");

        Collector collector = new();
        await fake.InvokeStreamingAsync(
            ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument, collector);

        Assert.Equal("café", Assert.Single(collector.Records).Data);
    }
}
