namespace AspireHcs.Cli;

/// <summary>
/// Raised when an <c>hcsctl</c> invocation does not succeed. Carries the argv, the exit code, and
/// hcsctl's own failure document fields, so a caller reports what the tool said rather than
/// "the process exited with 1".
/// </summary>
internal abstract class HcsCtlException : Exception
{
    private protected HcsCtlException(string message, string commandLine, int exitCode, string? stage, string? diagnostics)
        : base(message)
    {
        CommandLine = commandLine;
        ExitCode = exitCode;
        Stage = stage;
        Diagnostics = diagnostics;
    }

    /// <summary>The argv that was run, for a bug report. Never re-executed — it is not quoted for a shell.</summary>
    public string CommandLine { get; }

    /// <summary>hcsctl's process exit code. See <see cref="HcsCtlExitCode"/>.</summary>
    public int ExitCode { get; }

    /// <summary>The <c>stage</c> field of hcsctl's failure document, when it emitted one.</summary>
    public string? Stage { get; }

    /// <summary>The tail of hcsctl's stderr, bounded. Progress lines, not a result.</summary>
    public string? Diagnostics { get; }
}

/// <summary>
/// Exit 64: the command line was wrong and <b>nothing was attempted</b>. This is always a defect
/// in the argv this assembly built — never a host condition — so it must not be retried, and it
/// must not be reported to a developer as an infrastructure failure.
/// </summary>
internal sealed class HcsCtlUsageException(string message, string commandLine, string? stage, string? diagnostics)
    : HcsCtlException(message, commandLine, HcsCtlExitCode.Usage, stage, diagnostics);

/// <summary>
/// Exit 1: hcsctl ran and failed. The host, the store, or the compute system said no. Also covers
/// any exit code that is neither 0, 1 nor 64 — an unrecognized code is a failure, and treating it
/// as success because it was not on the list is how a broken run gets reported as a good one.
/// </summary>
internal sealed class HcsCtlCommandException(string message, string commandLine, int exitCode, string? stage, string? diagnostics)
    : HcsCtlException(message, commandLine, exitCode, stage, diagnostics);

/// <summary>
/// hcsctl exited but did not honour its output contract: stdout was empty, held something that is
/// not JSON, or held more than one document. Separate from <see cref="HcsCtlCommandException"/>
/// because the fix is in hcsctl (or in the version handshake), not in the request.
/// </summary>
internal sealed class HcsCtlContractException(string message, string commandLine, int exitCode, string? diagnostics)
    : HcsCtlException(message, commandLine, exitCode, stage: null, diagnostics);
