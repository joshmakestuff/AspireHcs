using System.Diagnostics;

namespace AspireHcs.IntegrationTests;

/// <summary>
/// Probes for the host-side residue a leaky teardown leaves behind: ACL entries on the base
/// image and copy-on-write work directories in TEMP. Tests snapshot before a run and assert
/// nothing new survives it.
/// </summary>
internal static class TeardownProbes
{
    /// <summary>
    /// The file's ACL as icacls prints it, one trimmed line per ACE, sorted. Equality is
    /// insensitive to ACE order but counts duplicates: every leaked VM-identity grant is its
    /// own line.
    /// </summary>
    public static string ReadAcl(string path)
    {
        using Process process = Process.Start(new ProcessStartInfo("icacls", $"\"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("failed to start icacls");

        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // A failed probe throws: two identical error outputs would compare equal and pass the
        // residue assertion vacuously.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"icacls '{path}' failed with exit code {process.ExitCode}: {text}");
        }

        string acl = string.Join('\n', text
            .Replace(path, "", StringComparison.OrdinalIgnoreCase)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("Successfully", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));

        return acl.Length > 0
            ? acl
            : throw new InvalidOperationException($"icacls '{path}' returned no ACL entries; the probe is not measuring anything.");
    }
}
