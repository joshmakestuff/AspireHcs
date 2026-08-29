using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AspireHcs.Cli;

namespace AspireHcs.Tests;

/// <summary>
/// Finds the repo-local hcsctl that <c>eng/Get-HcsCtl.ps1</c> installs.
///
/// Test-project only. The shipped assembly must not look for a repository layout (a NuGet
/// consumer has no <c>tools/hcsctl</c>); <see cref="HcsCtlBinary"/> documents the three
/// mechanisms it uses.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class RepositoryTools
{
    /// <summary>Committed at the repository root; marks where to stop walking up.</summary>
    private const string RootMarker = "AspireHcs.slnx";

    /// <summary>
    /// Resolves hcsctl for a test: the repo-local drop first, then whatever
    /// <see cref="HcsCtlBinary"/> finds. Repo-local wins so a developer's global install cannot
    /// substitute a different build for the pinned one.
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

    /// <summary>
    /// The file name hcsctl's store gives a record for <paramref name="reference"/>: the
    /// sanitized reference plus the first eight bytes of its SHA-256, lowercase hex. Mirrored
    /// here so a test can plant a record where hcsctl will actually look.
    /// </summary>
    public static string RecordFileName(string reference)
    {
        string safe = reference.Replace('/', '_').Replace(':', '_').Replace('@', '_').Replace('\\', '_');
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference));
        return $"{safe}-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}.json";
    }

    public static bool TryFindRepositoryRoot([NotNullWhen(true)] out string? root)
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
