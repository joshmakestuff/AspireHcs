using System.Diagnostics.CodeAnalysis;

namespace AspireHcs.Cli;

/// <summary>
/// Finds <c>hcsctl.exe</c>. The tool is not packaged with AspireHcs, so resolution is explicit and
/// its failure message names every place that was searched. hcsctl emits tool and contract
/// versions; AspireHcs does not yet enforce that handshake.
/// </summary>
internal static class HcsCtlBinary
{
    /// <summary>Overrides the search entirely. A file path, or a directory holding the binary.</summary>
    internal const string EnvironmentVariable = "ASPIREHCS_HCSCTL";

    internal const string FileName = "hcsctl.exe";

    /// <summary>
    /// Resolves the binary, in order: an explicit path, then <see cref="EnvironmentVariable"/>,
    /// then PATH.
    /// </summary>
    /// <exception cref="FileNotFoundException">No binary was found at any of the three.</exception>
    public static string Locate(string? explicitPath = null)
    {
        if (TryLocate(explicitPath, out string? path, out string? failure))
        {
            return path;
        }

        throw new FileNotFoundException(failure);
    }

    /// <summary>
    /// Resolves the binary without throwing. <paramref name="failure"/> is a complete message on
    /// a miss — it names the three mechanisms in order, so the reader can tell "I set the wrong
    /// variable" apart from "it is not installed".
    /// </summary>
    public static bool TryLocate(string? explicitPath, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? failure)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (TryResolve(explicitPath, out path))
            {
                failure = null;
                return true;
            }

            path = null;
            failure = $"hcsctl was not found at the configured path '{explicitPath}'. " +
                $"Give the path to {FileName} itself, or to the directory holding it.";
            return false;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            if (TryResolve(fromEnvironment, out path))
            {
                failure = null;
                return true;
            }

            path = null;
            failure = $"{EnvironmentVariable} is set to '{fromEnvironment}', but {FileName} is not there. " +
                $"Point it at {FileName} itself, or at the directory holding it.";
            return false;
        }

        if (TrySearchPath(out path))
        {
            failure = null;
            return true;
        }

        failure = $"{FileName} was not found. AspireHcs runs HCS resources by driving hcsctl " +
            "(https://github.com/joshmakestuff/hcsctl); it does not call HCS directly. Searched, in order: " +
            $"the path configured on the resource, the {EnvironmentVariable} environment variable, and PATH.";
        return false;
    }

    /// <summary>Accepts either the binary itself or the directory holding it.</summary>
    private static bool TryResolve(string candidate, [NotNullWhen(true)] out string? path)
    {
        string trimmed = candidate.Trim();

        if (Directory.Exists(trimmed))
        {
            string inDirectory = Path.Combine(trimmed, FileName);
            if (File.Exists(inDirectory))
            {
                path = Path.GetFullPath(inDirectory);
                return true;
            }

            path = null;
            return false;
        }

        if (File.Exists(trimmed))
        {
            path = Path.GetFullPath(trimmed);
            return true;
        }

        path = null;
        return false;
    }

    private static bool TrySearchPath([NotNullWhen(true)] out string? path)
    {
        string[] entries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string entry in entries)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(entry, FileName);
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters is one bad entry, not a reason to stop
                // searching the rest.
                continue;
            }

            if (File.Exists(candidate))
            {
                path = Path.GetFullPath(candidate);
                return true;
            }
        }

        path = null;
        return false;
    }
}
