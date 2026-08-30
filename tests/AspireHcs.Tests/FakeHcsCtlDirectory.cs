using System.Runtime.Versioning;
using AspireHcs.Cli;

namespace AspireHcs.Tests;

/// <summary>
/// A stand-in for hcsctl: each <see cref="Create"/> writes a batch script that emits exactly
/// the stdout, stderr and exit code its body specifies, since the real binary cannot be made
/// to violate its own contract on demand. Owns the temp directory the scripts live in;
/// <see cref="Directory"/> is available to a body that needs a scratch file (argv recording).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class FakeHcsCtlDirectory : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public HcsCtl Create(string batchBody)
    {
        string path = Path.Combine(Directory, $"fake-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off{Environment.NewLine}{batchBody}{Environment.NewLine}");
        return new HcsCtl(path);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test: a .cmd written moments earlier
            // can still be held open by an antivirus scan.
        }
        catch (UnauthorizedAccessException)
        {
            // Same best-effort cleanup.
        }
    }
}
