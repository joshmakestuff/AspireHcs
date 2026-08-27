using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// `guest forward` breaks InvokeAsync's "wait for exit, then parse stdout" assumption: the process
// never exits on its own. These pin the piece that makes StartLongRunningAsync possible — reading
// exactly the one JSON object hcsctl's contract promises without waiting for EOF — and the
// process-level contract around it, with a stand-in binary since hcsctl cannot be made to violate
// its own contract on demand.
//
// These need no hcsctl and no HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlLongRunningInvocationTests : IDisposable
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

    // ---- ReadOneJsonObjectAsync: the pure framing logic, no process involved ----

    [Fact]
    public async Task A_single_line_object_is_read_in_full()
    {
        using StringReader reader = new("""{"ok":true,"listen":"127.0.0.1:54321"}""");

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal("""{"ok":true,"listen":"127.0.0.1:54321"}""", document);
    }

    [Fact]
    public async Task A_pretty_printed_multiline_object_is_read_in_full()
    {
        // The exact shape hcsctl's json.MarshalIndent(doc, "", "  ") produces for forwardResult.
        string body = "{\n  \"ok\": true,\n  \"command\": \"guest forward\",\n  \"listen\": \"127.0.0.1:54321\",\n  \"guestPort\": 22\n}";
        using StringReader reader = new(body);

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal(body, document);
    }

    [Fact]
    public async Task Leading_whitespace_before_the_object_is_skipped()
    {
        using StringReader reader = new("\n\n  {\"ok\":true}");

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", document);
    }

    [Fact]
    public async Task Braces_inside_a_string_value_do_not_affect_nesting()
    {
        using StringReader reader = new("""{"detail":"unexpected {token} near }"}""");

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal("""{"detail":"unexpected {token} near }"}""", document);
    }

    [Fact]
    public async Task An_escaped_quote_inside_a_string_does_not_end_it_early()
    {
        using StringReader reader = new("""{"detail":"a \"quoted\" word","ok":true}""");

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal("""{"detail":"a \"quoted\" word","ok":true}""", document);
    }

    [Fact]
    public async Task Only_the_object_is_consumed_leaving_the_rest_of_the_stream_untouched()
    {
        // Models the real shape: hcsctl's one document, then a process that keeps running and
        // writes nothing further to stdout — but the reader must not block past the object to
        // find out there is nothing more.
        using StringReader reader = new("""{"ok":true}rest-left-on-the-stream""");

        string? document = await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", document);
        Assert.Equal("rest-left-on-the-stream", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Eof_before_the_object_completes_returns_null()
    {
        using StringReader reader = new("""{"ok":true,"listen":"127.0.0""");

        Assert.Null(await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Eof_with_no_output_at_all_returns_null()
    {
        using StringReader reader = new("");

        Assert.Null(await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Output_that_is_not_json_returns_null_rather_than_hanging()
    {
        using StringReader reader = new("this is not a document");

        Assert.Null(await HcsCtl.ReadOneJsonObjectAsync(reader, CancellationToken.None));
    }

    // ---- StartLongRunningAsync: the process-level contract ----

    [Fact]
    public async Task A_command_that_emits_its_document_and_keeps_running_is_returned_live()
    {
        HcsCtl fake = FakeCtl(
            """
            echo {
            echo   "ok": true,
            echo   "command": "guest forward",
            echo   "vmId": "11111111-1111-1111-1111-111111111111",
            echo   "listen": "127.0.0.1:54321",
            echo   "guestPort": 22
            echo }
            ping -n 50 127.0.0.1 >nul
            """);

        HcsCtlLongRunningInvocation<HcsCtlGuestForwardDocument> invocation = await fake.StartLongRunningAsync(
            ["guest", "forward"], HcsCtlJsonContext.Default.HcsCtlGuestForwardDocument);

        try
        {
            Assert.True(invocation.Result.Ok);
            Assert.Equal("127.0.0.1:54321", invocation.Result.Listen);
            Assert.Equal(22, invocation.Result.GuestPort);
            Assert.False(invocation.Process.HasExited);
        }
        finally
        {
            HcsCtl.KillQuietly(invocation.Process);
            invocation.Process.Dispose();
        }
    }

    [Fact]
    public async Task A_nonzero_exit_is_reported_the_same_way_a_one_shot_failure_is()
    {
        HcsCtl fake = FakeCtl(
            """
            echo {"ok":false,"stage":"run","error":"guest 11111111-1111-1111-1111-111111111111: unreachable"}
            exit /b 1
            """);

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => fake.StartLongRunningAsync(["guest", "forward"], HcsCtlJsonContext.Default.HcsCtlGuestForwardDocument));

        Assert.Equal(HcsCtlExitCode.Failed, thrown.ExitCode);
        Assert.Contains("unreachable", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_exit_right_after_the_document_is_a_contract_violation()
    {
        // A long-running command that exits 0 on its own is not the contract this API is for.
        HcsCtl fake = FakeCtl("""echo {"ok":true,"command":"guest forward","listen":"127.0.0.1:1","guestPort":22}""");

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.StartLongRunningAsync(["guest", "forward"], HcsCtlJsonContext.Default.HcsCtlGuestForwardDocument));

        Assert.Contains("must not do", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_with_no_document_at_all_says_the_document_was_missing()
    {
        HcsCtl fake = FakeCtl("exit /b 1");

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.StartLongRunningAsync(["guest", "forward"], HcsCtlJsonContext.Default.HcsCtlGuestForwardDocument));

        Assert.Contains("before emitting its result document", thrown.Message, StringComparison.Ordinal);
    }
}
