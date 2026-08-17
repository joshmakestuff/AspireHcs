using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// Pins the resolution order. Every miss must produce a message that names what was searched.
// ASPIREHCS_HCSCTL is process-wide state, so every test that reads or writes it shares one
// collection; xunit runs collections in parallel by default.
[Collection(HcsCtlEnvironmentCollection.Name)]
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlBinaryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aspirehcs-locator").FullName;
    private readonly string? _originalEnvironment = Environment.GetEnvironmentVariable(HcsCtlBinary.EnvironmentVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HcsCtlBinary.EnvironmentVariable, _originalEnvironment);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFakeBinary()
    {
        string path = Path.Combine(_directory, HcsCtlBinary.FileName);
        File.WriteAllText(path, "not a real binary");
        return path;
    }

    [Fact]
    public void An_explicit_file_path_resolves()
    {
        string expected = CreateFakeBinary();

        Assert.True(HcsCtlBinary.TryLocate(expected, out string? path, out string? failure));
        Assert.Equal(expected, path);
        Assert.Null(failure);
    }

    [Fact]
    public void An_explicit_directory_resolves_to_the_binary_inside_it()
    {
        string expected = CreateFakeBinary();

        Assert.True(HcsCtlBinary.TryLocate(_directory, out string? path, out _));
        Assert.Equal(expected, path);
    }

    [Fact]
    public void An_explicit_path_wins_over_the_environment_variable()
    {
        string expected = CreateFakeBinary();
        Environment.SetEnvironmentVariable(HcsCtlBinary.EnvironmentVariable, @"C:\aspirehcs-no-such-directory");

        Assert.True(HcsCtlBinary.TryLocate(expected, out string? path, out _));
        Assert.Equal(expected, path);
    }

    [Fact]
    public void The_environment_variable_resolves_when_no_explicit_path_is_given()
    {
        string expected = CreateFakeBinary();
        Environment.SetEnvironmentVariable(HcsCtlBinary.EnvironmentVariable, _directory);

        Assert.True(HcsCtlBinary.TryLocate(explicitPath: null, out string? path, out _));
        Assert.Equal(expected, path);
    }

    // A wrong ASPIREHCS_HCSCTL must fail. Falling through to PATH would run a different binary
    // than the one the developer named.
    [Fact]
    public void A_wrong_environment_variable_fails_rather_than_falling_through_to_path()
    {
        Environment.SetEnvironmentVariable(HcsCtlBinary.EnvironmentVariable, @"C:\aspirehcs-no-such-directory");

        Assert.False(HcsCtlBinary.TryLocate(explicitPath: null, out string? path, out string? failure));
        Assert.Null(path);
        Assert.NotNull(failure);
        Assert.Contains(HcsCtlBinary.EnvironmentVariable, failure);
        Assert.Contains(@"C:\aspirehcs-no-such-directory", failure);
    }

    [Fact]
    public void A_wrong_explicit_path_names_the_path_it_was_given()
    {
        Assert.False(HcsCtlBinary.TryLocate(@"C:\aspirehcs-no-such-directory", out _, out string? failure));
        Assert.NotNull(failure);
        Assert.Contains(@"C:\aspirehcs-no-such-directory", failure);
    }

    [Fact]
    public void Locate_throws_with_the_same_message_TryLocate_reports()
    {
        HcsCtlBinary.TryLocate(@"C:\aspirehcs-no-such-directory", out _, out string? failure);

        FileNotFoundException thrown = Assert.Throws<FileNotFoundException>(
            () => HcsCtlBinary.Locate(@"C:\aspirehcs-no-such-directory"));

        Assert.Equal(failure, thrown.Message);
    }
}
