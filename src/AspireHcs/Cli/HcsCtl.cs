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

    /// <summary>
    /// The one verb inside a store-accepting group that rejects <c>--store</c>: <c>vm stop</c>
    /// drives HCS by id alone, deliberately, so it can act on a system whose store record is
    /// gone. Passing it is exit 64. Pinned by <c>HcsCtlStoreTests</c>.
    /// </summary>
    private static readonly string[][] VerbsWithoutStore = [["vm", "stop"]];

    /// <summary>
    /// How long <see cref="StartLongRunningAsync{TResult}"/> waits, after reading the result
    /// document, to see whether the process exits on its own before treating it as genuinely
    /// long-running. Stdout completing and the OS signalling process exit are not the same
    /// event, so without this a command that fails right after printing its document can still
    /// look "still running" at the instant the document finishes being read.
    /// </summary>
    private static readonly TimeSpan ExitGraceWindow = TimeSpan.FromMilliseconds(300);

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

    /// <summary>
    /// Starts an hcsctl command that is designed to keep running instead of exiting — currently
    /// only <c>guest forward</c> — and returns as soon as its one stdout document is complete,
    /// without waiting for the process to exit. The caller owns the returned process for as long
    /// as the command's effect is wanted and is responsible for killing it.
    /// </summary>
    /// <remarks>
    /// <see cref="InvokeAsync"/> cannot run a command like this: it waits for process exit before
    /// reading stdout, and this process is designed never to exit on its own. The document is
    /// still exactly one JSON object under hcsctl's contract — this reads only that object,
    /// stopping the instant its closing brace arrives, and leaves everything after it (nothing,
    /// on the success path) alone.
    /// <para>
    /// A process that exits before completing that object, or immediately after — a non-zero
    /// exit is hcsctl's normal failure shape; a clean exit is a contract violation for a command
    /// that must not exit on its own — is reported as a failed start, and nothing is returned to
    /// hold.
    /// </para>
    /// </remarks>
    public async Task<HcsCtlLongRunningInvocation<TResult>> StartLongRunningAsync<TResult>(
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

        string commandLine = $"{Path.GetFileName(ExecutablePath)} {string.Join(' ', startInfo.ArgumentList)}";

        Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new HcsCtlContractException(
                $"Failed to start '{ExecutablePath}'.", commandLine, exitCode: -1, diagnostics: null);
        }

        // Drained for as long as the process lives, not just for the duration of this call: on
        // the success path the process outlives StartLongRunningAsync, and its stderr must keep
        // being read or a chatty child fills the pipe and blocks.
        Queue<string> diagnostics = new();
        Task readStderr = PumpStandardErrorAsync(process, diagnostics, progress, CancellationToken.None);

        string? document;
        try
        {
            document = await ReadOneJsonObjectAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            await readStderr.ConfigureAwait(false);
            process.Dispose();
            throw;
        }

        if (document is null)
        {
            // EOF, or a first non-whitespace character that was not '{', before a complete
            // object arrived.
            KillQuietly(process);
            await readStderr.ConfigureAwait(false);
            int exitCode = process.HasExited ? process.ExitCode : -1;
            string? diagnosticsOrNull = DiagnosticsOrNull(diagnostics);
            process.Dispose();
            throw new HcsCtlContractException(
                "hcsctl exited before emitting its result document.", commandLine, exitCode, diagnosticsOrNull);
        }

        // A process that is about to exit right after writing its document has not necessarily
        // done so yet at this exact instant — stdout completing and the OS signalling exit are
        // not the same event. A short grace window disambiguates "failed and about to exit" from
        // "genuinely long-running" without meaningfully delaying the success path.
        if (!process.HasExited)
        {
            using CancellationTokenSource graceWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            graceWindow.CancelAfter(ExitGraceWindow);
            try
            {
                await process.WaitForExitAsync(graceWindow.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillQuietly(process);
                    await readStderr.ConfigureAwait(false);
                    process.Dispose();
                    throw;
                }
                // Otherwise just the grace window elapsed: still running past it is the expected
                // shape for a command that is designed never to exit on its own.
            }
        }

        if (process.HasExited)
        {
            await readStderr.ConfigureAwait(false);
            string diagnosticsText = string.Join(Environment.NewLine, diagnostics);
            int exitCode = process.ExitCode;

            if (exitCode != HcsCtlExitCode.Ok)
            {
                // hcsctl's failure documents are well-formed JSON objects too, so the same
                // framing read above already captured this one whole. Reusing Interpret gives a
                // one-shot failure the identical exception shape a caller already handles.
                _ = Interpret(exitCode, document, commandLine, diagnosticsText, resultType);
            }

            process.Dispose();
            throw new HcsCtlContractException(
                $"hcsctl exited immediately after starting, which a long-running command must not do. stdout: {Describe(document)}",
                commandLine, exitCode, diagnosticsText.Length == 0 ? null : diagnosticsText);
        }

        TResult result;
        try
        {
            result = JsonSerializer.Deserialize(document, resultType)
                ?? throw new HcsCtlContractException(
                    "hcsctl's document on stdout was the JSON literal null.", commandLine, exitCode: 0, diagnostics: null);
        }
        catch (JsonException ex)
        {
            KillQuietly(process);
            process.Dispose();
            throw new HcsCtlContractException(
                $"hcsctl's stdout was not one JSON document: {ex.Message} stdout: {Describe(document)}",
                commandLine, exitCode: 0, diagnostics: null);
        }

        return new HcsCtlLongRunningInvocation<TResult>(process, result);
    }

    /// <summary>
    /// Reads exactly one JSON object off <paramref name="reader"/> — from its first non-whitespace
    /// character through the closing brace that returns its nesting to zero — without reading
    /// past it. Braces and quotes inside a string value do not affect nesting; a backslash escapes
    /// the character after it while inside one. Returns <see langword="null"/> if the stream ends,
    /// or a character that cannot start a JSON object arrives, before the object completes.
    /// </summary>
    internal static async Task<string?> ReadOneJsonObjectAsync(TextReader reader, CancellationToken cancellationToken)
    {
        StringBuilder buffer = new();
        char[] one = new char[1];
        bool started = false;
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        while (true)
        {
            int read = await reader.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            char c = one[0];

            if (!started)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }
                if (c != '{')
                {
                    return null;
                }
                started = true;
                depth = 1;
                buffer.Append(c);
                continue;
            }

            buffer.Append(c);

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return buffer.ToString();
                }
            }
        }
    }

    private static string? DiagnosticsOrNull(Queue<string> diagnostics)
    {
        string text = string.Join(Environment.NewLine, diagnostics);
        return text.Length == 0 ? null : text;
    }

    private static bool RejectsStore(IReadOnlyList<string> arguments)
        => (arguments.Count > 0 && GroupsWithoutStore.Contains(arguments[0], StringComparer.Ordinal))
        || (arguments.Count > 1 && VerbsWithoutStore.Any(v =>
                string.Equals(v[0], arguments[0], StringComparison.Ordinal)
                && string.Equals(v[1], arguments[1], StringComparison.Ordinal)));

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

    /// <summary>
    /// Terminates a child process without throwing over one that is already gone. Internal: also
    /// used by callers that hold a process past this call returning, such as a
    /// <see cref="HcsCtlLongRunningInvocation{TResult}"/>'s owner tearing it down.
    /// </summary>
    internal static void KillQuietly(Process process)
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

/// <summary>
/// A long-running hcsctl invocation that started successfully: its one result document, and the
/// still-running process that produced it. The caller owns <see cref="Process"/> — it does not
/// exit on its own — and must kill it (<see cref="HcsCtl.KillQuietly"/>) when the command's
/// effect is no longer wanted.
/// </summary>
internal sealed class HcsCtlLongRunningInvocation<TResult>(Process process, TResult result)
{
    public Process Process { get; } = process;
    public TResult Result { get; } = result;
}
