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
///
/// Nothing here scrapes stderr for an answer. If an answer is not in the document, the fix is in
/// hcsctl.
/// </summary>
internal sealed class HcsCtl(string executablePath, string? storePath = null)
{
    /// <summary>
    /// How many trailing stderr lines are kept for a failure message. Bounded because a
    /// long-running <c>container exec</c> writes the guest's own output to this stream, so it is
    /// unbounded in principle — and it is not a result, so keeping all of it buys nothing.
    /// </summary>
    private const int DiagnosticLineLimit = 50;

    /// <summary>
    /// The verb groups that reject <c>--store</c>. Measured against the binary, not assumed:
    /// every other group accepts it, and passing it to one of these is exit 64. Pinned by
    /// <c>HcsCtlStoreTests</c> so a change in hcsctl breaks a test rather than a run.
    /// </summary>
    private static readonly string[] GroupsWithoutStore = ["network"];

    private HcsCtlInfoDocument? _info;

    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// The hcsctl store to operate against, or <see langword="null"/> for hcsctl's per-user
    /// default. Images are acquired out of band — the import is elevated and once per image — so
    /// an AppHost normally points at a store someone else prepared.
    /// </summary>
    public string? StorePath { get; } = storePath;

    /// <summary>Resolves the binary per <see cref="HcsCtlBinary"/> and wraps it.</summary>
    public static HcsCtl Locate(string? explicitPath = null, string? storePath = null)
        => new(HcsCtlBinary.Locate(explicitPath), storePath);

    /// <summary>
    /// Runs <c>hcsctl info</c> and caches it for the lifetime of this instance. The token's
    /// privileges and group memberships do not change under a running AppHost, and this is the
    /// preflight every resource start consults, so re-reading it per resource buys nothing.
    /// </summary>
    public async Task<HcsCtlInfoDocument> GetInfoAsync(CancellationToken cancellationToken = default)
        => _info ??= await InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Runs one hcsctl command and deserializes its single stdout document.
    /// </summary>
    /// <param name="arguments">
    /// argv without <c>--json</c>, which is added here. Passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/>, so a value containing spaces or quotes is
    /// escaped by the runtime rather than by string concatenation here.
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
            // hcsctl writes UTF-8. Without this the streams are decoded with the console's code
            // page, which mangles any non-ASCII value on its way back — exactly the class of bug
            // SerialLineFramer was written to stop on the VM side.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // --store is appended centrally so no call site can forget it and silently operate on
        // hcsctl's per-user default store instead of the one the resource was pointed at. The
        // failure mode that guards against is the worst kind: it succeeds, against the wrong
        // images.
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

        // Both streams are drained concurrently. Draining one to completion first deadlocks as
        // soon as the child fills the pipe nothing is reading — the same trap hcsctl documents on
        // its own side of these pipes.
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
            // handles, and an orphan would keep them past the teardown that cancelled it.
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
                // Thrown rather than reported to progress: a bare-text line means hcsctl ignored
                // --stream-json, so the guest/progress split is not trustworthy. The exit code is
                // not meaningful mid-stream, hence -1 like the failed-to-start path.
                throw new HcsCtlContractException(
                    $"hcsctl's stderr was not NDJSON under --stream-json: {line}",
                    commandLine, exitCode: -1, diagnostics: null);
            }

            progress?.Report(record);

            // Guest output is log content, never failure diagnostics, so only hcsctl's own
            // progress lines feed the bounded tail that a failure message carries.
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
            // hcsctl emits a well-formed failure document on every non-zero path, so the message
            // comes from the tool. A missing or unparseable one is a contract violation and is
            // reported as such rather than papered over with the exit code alone.
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
            // Deserialize rejects trailing content, which is the "exactly one document" half of
            // the contract. A second document would be a silent truncation otherwise.
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
