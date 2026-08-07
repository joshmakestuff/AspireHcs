namespace AspireHcs.Cli;

/// <summary>
/// hcsctl's exit codes. These three mean different things and must stay distinguishable at this
/// seam: <see cref="Usage"/> promises <em>nothing was attempted</em>, so it is a defect in the
/// argv this assembly built, while <see cref="Failed"/> means hcsctl ran and the host said no.
/// Collapsing them is the CLI equivalent of reading <c>S_FALSE</c> as a failure.
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
