using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AspireHcs.Cli;

/// <summary>
/// Runs <c>hcsctl</c> and binds its result document. This is the only place in AspireHcs that
/// starts a process, and the only place that knows hcsctl's output contract:
///
/// <list type="bullet">
///   <item>stdout carries <b>exactly one</b> JSON document, on every path including failure</item>
///   <item>stderr carries progress, and is never a result</item>
///   <item>exit 0 ok, 1 ran and failed, 64 bad arguments with nothing attempted</item>
/// </list>
/// </summary>
internal sealed class HcsCtl(string executablePath, string? storePath = null)
{
    /// <summary>
    /// How many trailing stderr lines are kept for a failure message. A long-running
    /// <c>container exec</c> writes guest output to this stream, so it is unbounded.
    /// </summary>
    private const int DiagnosticLineLimit = 50;

    /// <summary>
    /// The verb groups that reject <c>--store</c>; every other group accepts it, and passing it
    /// to one of these is exit 64. Pinned by <c>HcsCtlStoreTests</c>.
    /// </summary>
    private static readonly string[] GroupsWithoutStore = ["network", "guest"];

    private HcsCtlInfoDocument? _info;

    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// The hcsctl store to operate against, or <see langword="null"/> for hcsctl's per-user
    /// default. Image import is elevated and out of band, so an AppHost normally points at a
    /// store prepared in advance.
    /// </summary>
    public string? StorePath { get; } = storePath;

    /// <summary>Resolves the binary per <see cref="HcsCtlBinary"/> and wraps it.</summary>
    public static HcsCtl Locate(string? explicitPath = null, string? storePath = null)
        => new(HcsCtlBinary.Locate(explicitPath), storePath);

    /// <summary>
    /// Runs <c>hcsctl info</c> and caches it for the lifetime of this instance. The token's
    /// privileges and group memberships do not change under a running AppHost.
    /// </summary>
    public async Task<HcsCtlInfoDocument> GetInfoAsync(CancellationToken cancellationToken = default)
        => _info ??= await InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Runs one hcsctl command and deserializes its single stdout document.
    /// </summary>
    /// <param name="arguments">
    /// argv without <c>--json</c>, which is added here. Passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/>, so the runtime escapes values with spaces or
    /// quotes.
    /// </param>
    /// <param name="resultType">Source-generated type info for the expected result document.</param>
    /// <param name="progress">Receives hcsctl's stderr, line by line, as it arrives.</param>
    public async Task<TResult> InvokeAsync<TResult>(
        IReadOnlyList<string> arguments,
        JsonTypeInfo<TResult> resultType,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // hcsctl writes UTF-8. Without this the streams are decoded with the console code
            // page, which mangles non-ASCII values.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // --store is appended here for every call site. A call without it operates on hcsctl's
        // per-user default store.
        if (!string.IsNullOrEmpty(StorePath) && !RejectsStore(arguments))
        {
            startInfo.ArgumentList.Add("--store");
            startInfo.ArgumentList.Add(StorePath);
        }

        startInfo.ArgumentList.Add("--json");

        string commandLine = $"{Path.GetFileName(ExecutablePath)} {string.Join(' ', startInfo.ArgumentList)}";

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new HcsCtlContractException(
                $"Failed to start '{ExecutablePath}'.", commandLine, exitCode: -1, diagnostics: null);
        }

        // Both streams are drained concurrently. Draining one to completion first deadlocks when
        // the child fills the pipe nothing reads.
        Queue<string> diagnostics = new();
        Task<string> readStdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task readStderr = PumpStandardErrorAsync(process, diagnostics, progress, cancellationToken);

        string stdout;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stdout = await readStdout.ConfigureAwait(false);
            await readStderr.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled invocation must not leave hcsctl running: it holds the compute system's
            // handles.
            KillQuietly(process);
            throw;
        }

        return Interpret(process.ExitCode, stdout, commandLine, string.Join(Environment.NewLine, diagnostics), resultType);
    }

    /// <summary>
    /// Runs one hcsctl command under <c>--stream-json</c> and deserializes its single stdout
    /// document, while its stderr is parsed as framed NDJSON (<see cref="HcsCtlStreamRecord"/>).
    /// Mirrors <see cref="InvokeAsync"/> exactly, differing only in the two stream contracts:
    /// <c>--stream-json</c> is appended after <c>--json</c>, and stderr must be typed records.
    /// </summary>
    /// <param name="progress">Receives every parsed stderr record, as it arrives.</param>
    public async Task<TResult> InvokeStreamingAsync<TResult>(
        IReadOnlyList<string> arguments,
        JsonTypeInfo<TResult> resultType,
        IProgress<HcsCtlStreamRecord>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(StorePath) && !RejectsStore(arguments))
        {
            startInfo.ArgumentList.Add("--store");
            startInfo.ArgumentList.Add(StorePath);
        }

        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--stream-json");

        string commandLine = $"{Path.GetFileName(ExecutablePath)} {string.Join(' ', startInfo.ArgumentList)}";

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new HcsCtlContractException(
                $"Failed to start '{ExecutablePath}'.", commandLine, exitCode: -1, diagnostics: null);
        }

        Queue<string> diagnostics = new();
        Task<string> readStdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task readStderr = PumpStandardErrorStreamingAsync(process, diagnostics, progress, commandLine, cancellationToken);

        string stdout;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stdout = await readStdout.ConfigureAwait(false);
            await readStderr.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return Interpret(process.ExitCode, stdout, commandLine, string.Join(Environment.NewLine, diagnostics), resultType);
    }

    private static bool RejectsStore(IReadOnlyList<string> arguments)
        => arguments.Count > 0 && GroupsWithoutStore.Contains(arguments[0], StringComparer.Ordinal);

    private static async Task PumpStandardErrorAsync(
        Process process,
        Queue<string> diagnostics,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            progress?.Report(line);

            diagnostics.Enqueue(line);
            if (diagnostics.Count > DiagnosticLineLimit)
            {
                diagnostics.Dequeue();
            }
        }
    }

    private static async Task PumpStandardErrorStreamingAsync(
        Process process,
        Queue<string> diagnostics,
        IProgress<HcsCtlStreamRecord>? progress,
        string commandLine,
        CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            HcsCtlStreamRecord record;
            try
            {
                record = JsonSerializer.Deserialize(line, HcsCtlJsonContext.Default.HcsCtlStreamRecord)
                    ?? throw new JsonException("the JSON literal null");
            }
            catch (JsonException)
            {
                // A bare-text line means hcsctl ignored --stream-json, so the guest/progress split
                // is not trustworthy. The exit code is not known mid-stream; -1 matches the
                // failed-to-start path.
                throw new HcsCtlContractException(
                    $"hcsctl's stderr was not NDJSON under --stream-json: {line}",
                    commandLine, exitCode: -1, diagnostics: null);
            }

            progress?.Report(record);

            // Only hcsctl's own progress lines feed the bounded tail a failure message carries.
            // Guest output is log content, not failure diagnostics.
            if (string.Equals(record.Stream, "progress", StringComparison.Ordinal))
            {
                diagnostics.Enqueue(record.Msg ?? string.Empty);
                if (diagnostics.Count > DiagnosticLineLimit)
                {
                    diagnostics.Dequeue();
                }
            }
        }
    }

    private static TResult Interpret<TResult>(
        int exitCode,
        string stdout,
        string commandLine,
        string diagnostics,
        JsonTypeInfo<TResult> resultType)
    {
        string? diagnosticsOrNull = diagnostics.Length == 0 ? null : diagnostics;

        if (exitCode != HcsCtlExitCode.Ok)
        {
            // hcsctl emits a failure document on every non-zero path. A missing or unparseable
            // one is a contract violation.
            HcsCtlFailureDocument? failure = TryReadFailure(stdout);
            string reason = failure?.Error ?? $"hcsctl exited {exitCode} without a failure document. stdout: {Describe(stdout)}";

            throw exitCode switch
            {
                HcsCtlExitCode.Usage => new HcsCtlUsageException(
                    $"hcsctl rejected the command line, so nothing was attempted: {reason}", commandLine, failure?.Stage, diagnosticsOrNull),
                _ => new HcsCtlCommandException(
                    $"hcsctl failed: {reason}", commandLine, exitCode, failure?.Stage, diagnosticsOrNull),
            };
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new HcsCtlContractException(
                "hcsctl exited 0 but put no document on stdout.", commandLine, exitCode, diagnosticsOrNull);
        }

        try
        {
            // Deserialize rejects trailing content, which enforces the "exactly one document"
            // half of the contract.
            return JsonSerializer.Deserialize(stdout, resultType)
                ?? throw new HcsCtlContractException(
                    "hcsctl's document on stdout was the JSON literal null.", commandLine, exitCode, diagnosticsOrNull);
        }
        catch (JsonException ex)
        {
            throw new HcsCtlContractException(
                $"hcsctl's stdout was not one JSON document: {ex.Message} stdout: {Describe(stdout)}",
                commandLine, exitCode, diagnosticsOrNull);
        }
    }

    private static HcsCtlFailureDocument? TryReadFailure(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(stdout, HcsCtlJsonContext.Default.HcsCtlFailureDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Bounds an unparseable stdout before it goes into an exception message.</summary>
    private static string Describe(string stdout)
    {
        const int limit = 512;
        string trimmed = stdout.Trim();
        return trimmed.Length <= limit ? $"'{trimmed}'" : $"'{trimmed[..limit]}' (truncated)";
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting on its own while being killed. Nothing further to do from here.
        }
    }
}
