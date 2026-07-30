using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.17763")]

namespace AspireHcs;

/// <summary>
/// Platform gate for the HCS-backed integration. HCS compute systems require
/// Windows 10 1809 (build 17763) / Windows Server 2019 or later.
/// </summary>
internal static class HcsPlatform
{
    internal const string MinimumWindowsVersion = "10.0.17763";

    internal static void ThrowIfUnsupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException(
                "AspireHcs requires Windows 10 1809 / Windows Server 2019 or later with the Hyper-V feature enabled. " +
                $"Current OS: {Environment.OSVersion.VersionString}.");
        }
    }
}
