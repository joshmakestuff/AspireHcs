using System.Diagnostics;

namespace AspireHcs.IntegrationTests;

/// <summary>
/// Probes for the host-side residue a leaky teardown leaves behind: ACL entries on the base
/// image (#16) and copy-on-write work directories in TEMP (#17). Tests snapshot before a run
/// and assert nothing new survives it.
/// </summary>
internal static class TeardownProbes
{
    /// <summary>
    /// The file's ACL as icacls prints it, one trimmed line per ACE, sorted so equality is
    /// insensitive to ACE order but still counts duplicates — every leaked VM-identity grant
    /// is its own line.
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

        return string.Join('\n', text
            .Replace(path, "", StringComparison.OrdinalIgnoreCase)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("Successfully", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
    }
}
