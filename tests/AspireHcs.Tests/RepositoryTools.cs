using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AspireHcs.Cli;

namespace AspireHcs.Tests;

/// <summary>
/// Finds the repo-local hcsctl that <c>eng/Get-HcsCtl.ps1</c> installs.
///
/// This lives in the test project on purpose. The shipped assembly must never look for a
/// repository layout — a NuGet consumer has no <c>tools/hcsctl</c> — so the repo-local
/// convenience stops at the test boundary and <see cref="HcsCtlBinary"/> stays honest about the
/// three mechanisms it documents.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class RepositoryTools
{
    /// <summary>Committed at the repository root; marks where to stop walking up.</summary>
    private const string RootMarker = "AspireHcs.slnx";

    /// <summary>
    /// Resolves hcsctl for a test: the repo-local drop first, then whatever
    /// <see cref="HcsCtlBinary"/> would find. Repo-local wins so a developer's global install
    /// cannot silently substitute a different build for the pinned one.
    /// </summary>
    public static bool TryFindHcsCtl([NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? failure)
    {
        if (TryFindRepositoryRoot(out string? root))
        {
            string local = Path.Combine(root, "tools", "hcsctl", HcsCtlBinary.FileName);
            if (File.Exists(local))
            {
                path = local;
                failure = null;
                return true;
            }
        }

        if (HcsCtlBinary.TryLocate(explicitPath: null, out path, out _))
        {
            failure = null;
            return true;
        }

        failure = "hcsctl was not found. Run ./eng/Get-HcsCtl.ps1 to install the pinned preview " +
            $"drop into tools/hcsctl, or set {HcsCtlBinary.EnvironmentVariable}.";
        return false;
    }

    private static bool TryFindRepositoryRoot([NotNullWhen(true)] out string? root)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                root = directory.FullName;
                return true;
            }
        }

        root = null;
        return false;
    }
}
