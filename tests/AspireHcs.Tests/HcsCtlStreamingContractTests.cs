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
    private readonly FakeHcsCtlDirectory _fakes = new();

    public void Dispose() => _fakes.Dispose();

    /// <summary>Captures every parsed record so the routing can be asserted.</summary>
    private sealed class Collector : IProgress<HcsCtlStreamRecord>
    {
        public List<HcsCtlStreamRecord> Records { get; } = [];

        public void Report(HcsCtlStreamRecord value) => Records.Add(value);
    }

    [Fact]
    public async Task Stream_records_route_by_their_stream_tag()
    {
        HcsCtl fake = _fakes.Create(new() { DefaultResponse = new() { Stdout = "{\"ok\":true}", Stderr = "{\"stream\":\"stdout\",\"data\":\"hello\"}\n{\"stream\":\"stderr\",\"data\":\"oops\"}\n{\"stream\":\"progress\",\"msg\":\"step\"}\n" } });

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
        HcsCtl fake = _fakes.Create(new() { DefaultResponse = new() { Stdout = "{\"ok\":true}", Stderr = "{\"stream\":\"progress\",\"msg\":\"creating\"}\n{\"stream\":\"exec\",\"event\":\"started\",\"pid\":4242}\n{\"stream\":\"stdout\",\"data\":\"started\"}\n" } });

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
    public async Task Bare_text_from_a_still_running_child_is_a_contract_violation()
    {
        string ready = Path.Combine(_fakes.Directory, "malformed-ready");
        string release = Path.Combine(_fakes.Directory, "malformed-release");
        HcsCtl fake = _fakes.Create(new() { DefaultResponse = new() { Stdout = "{\"ok\":true}", Stderr = "this is not ndjson\n", ReadyPath = ready, ReleasePath = release } });

        Task invocation = fake.InvokeStreamingAsync(
            ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument);
        try
        {
            await WaitForFileAsync(ready);
            Assert.False(invocation.IsCompleted);
            File.WriteAllText(release, "release");

            HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(() => invocation);
            Assert.Contains("not NDJSON", thrown.Message);
            Assert.Contains("this is not ndjson", thrown.Message);
        }
        finally
        {
            File.WriteAllText(release, "release");
            try
            {
                await invocation;
            }
            catch (HcsCtlContractException)
            {
            }
        }
    }

    [Fact]
    public async Task Non_ascii_data_survives_the_stream()
    {
        HcsCtl fake = _fakes.Create(new() { DefaultResponse = new() { Stdout = "{\"ok\":true}", Stderr = "{\"stream\":\"stdout\",\"data\":\"café\"}\n" } });

        Collector collector = new();
        await fake.InvokeStreamingAsync(
            ["container", "exec"], HcsCtlJsonContext.Default.HcsCtlExecDocument, collector);

        Assert.Equal("café", Assert.Single(collector.Records).Data);
    }

    private static async Task WaitForFileAsync(string path)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
