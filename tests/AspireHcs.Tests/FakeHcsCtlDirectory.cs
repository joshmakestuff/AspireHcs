using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AspireHcs.Cli;

namespace AspireHcs.Tests;

/// <summary>
/// A stand-in for hcsctl. Each <see cref="Create"/> copies the test-built executable into an
/// isolated directory and writes its scenario beside it. The real binary cannot be made to
/// violate its output contract or hold a controlled lifetime on demand.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class FakeHcsCtlDirectory : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public HcsCtl Create(FakeHcsCtlScenario scenario, string? storePath = null)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "fake-hcsctl");
        if (!System.IO.Directory.Exists(source))
        {
            throw new InvalidOperationException(
                $"The test-built fake hcsctl was not found at '{source}'. Build the test project before running it.");
        }

        string target = Path.Combine(Directory, $"fake-{Guid.NewGuid():N}");
        CopyDirectory(source, target);
        File.WriteAllText(
            Path.Combine(target, "scenario.json"),
            JsonSerializer.Serialize(scenario),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new HcsCtl(Path.Combine(target, "FakeHcsCtl.exe"), storePath);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test: a just-exited executable can
            // still be held open by an antivirus scan.
        }
        catch (UnauthorizedAccessException)
        {
            // Same best-effort cleanup.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (string sourcePath in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, sourcePath);
            string targetPath = Path.Combine(target, relative);
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (targetDirectory is not null)
            {
                System.IO.Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourcePath, targetPath);
        }
    }
}

internal sealed record FakeHcsCtlScenario
{
    public string? ArgumentsPath { get; init; }

    public IReadOnlyList<FakeHcsCtlResponse> Responses { get; init; } = [];

    public FakeHcsCtlResponse? DefaultResponse { get; init; }
}

internal sealed record FakeHcsCtlResponse
{
    public IReadOnlyList<string>? ArgumentPrefix { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    public int ExitCode { get; init; }

    public string? ReadyPath { get; init; }

    public string? ReleasePath { get; init; }
}
