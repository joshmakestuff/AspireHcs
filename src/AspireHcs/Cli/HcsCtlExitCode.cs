namespace AspireHcs.Cli;

/// <summary>
/// hcsctl's exit codes. <see cref="Usage"/> promises <em>nothing was attempted</em>, so it is a
/// defect in the argv this assembly built; <see cref="Failed"/> means hcsctl ran and the host
/// said no. Callers must keep them distinct.
/// </summary>
internal static class HcsCtlExitCode
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The command ran and failed.</summary>
    public const int Failed = 1;

    /// <summary>The command line was wrong and nothing was attempted. EX_USAGE from sysexits.h.</summary>
    public const int Usage = 64;
}
