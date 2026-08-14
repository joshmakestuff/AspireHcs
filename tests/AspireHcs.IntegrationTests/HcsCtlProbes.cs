using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using AspireHcs.Cli;
using AspireHcs.Tests;

namespace AspireHcs.IntegrationTests;

/// <summary>
/// Asks hcsctl what the host actually holds, for residue assertions.
/// </summary>
/// <remarks>
/// Deliberately its own process invocation rather than the product's <see cref="HcsCtl"/> seam.
/// A leak assertion that runs through the same typed path the product uses can only find bugs
/// that path does not have; going straight to the JSON keeps the check independent of the
/// binding it is verifying.
/// </remarks>
[SupportedOSPlatform("windows10.0.17763")]
internal static class HcsCtlProbes
{
    /// <summary>Every HCN endpoint on the host, by id. Unfiltered, so nothing passes vacuously.</summary>
    public static IReadOnlyList<string> EndpointIds()
        => [.. Query(["network", "endpoints"])["endpoints"]?.AsArray()
            .Select(e => e?["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!) ?? []];

    /// <summary>Every VM id in the store, whatever state it is in.</summary>
    public static IReadOnlyList<string> VmIds(string? storePath = null)
        => [.. Query(["vm", "ls"], storePath)["vms"]?.AsArray()
            .Select(v => v?["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!) ?? []];

    /// <summary>Every container id in the store, whatever state it is in.</summary>
    public static IReadOnlyList<string> ContainerIds(string? storePath = null)
        => [.. Query(["container", "ls"], storePath)["containers"]?.AsArray()
            .Select(c => c?["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!) ?? []];

    /// <summary>
    /// Runs hcsctl out of band, the way a second tool or an operator would. Used to arrange
    /// conditions the AppHost cannot arrange for itself: a colliding VM id, or a guest killed
    /// from outside.
    /// </summary>
    /// <returns>Whether it succeeded; failure detail goes to <paramref name="detail"/>.</returns>
    public static bool TryRun(string[] arguments, out string detail, string? storePath = null)
    {
        try
        {
            _ = Query(arguments, storePath);
            detail = "";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static JsonObject Query(string[] arguments, string? storePath = null)
    {
        if (!RepositoryTools.TryFindHcsCtl(out string? exe, out string? failure))
        {
            throw new InvalidOperationException($"The probe cannot find hcsctl: {failure}");
        }

        ProcessStartInfo startInfo = new(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        // `network` rejects --store; every other group takes it.
        if (!string.IsNullOrEmpty(storePath) && arguments[0] != "network")
        {
            startInfo.ArgumentList.Add("--store");
            startInfo.ArgumentList.Add(storePath);
        }
        startInfo.ArgumentList.Add("--json");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // A failed probe fails the test rather than returning an empty result: "no endpoints"
        // and "the probe broke" must never look the same to a leak assertion.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"hcsctl {string.Join(' ', arguments)} exited {process.ExitCode}: {stdout} {stderr}");
        }

        return JsonNode.Parse(stdout) as JsonObject
            ?? throw new InvalidOperationException($"hcsctl {string.Join(' ', arguments)} returned no document: {stdout}");
    }
}
