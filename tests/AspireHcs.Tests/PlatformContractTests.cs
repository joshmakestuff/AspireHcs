using System.Runtime.Versioning;
using Xunit;

namespace AspireHcs.Tests;

// The package advertises "Windows-only, fails fast elsewhere". These tests pin that claim to
// the shipped assembly.
[SupportedOSPlatform("windows10.0.17763")]
public class PlatformContractTests
{
    [Fact]
    public void Assembly_advertises_windows_10_17763_support()
    {
        SupportedOSPlatformAttribute? attribute = typeof(HcsPlatform).Assembly
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal("windows" + HcsPlatform.MinimumWindowsVersion, attribute.PlatformName);
    }

    [Fact]
    public void ThrowIfUnsupported_passes_on_a_supported_windows_host()
    {
        // On anything older than Windows 10 1809, or non-Windows, the guard throws; only the
        // happy path is asserted here.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        HcsPlatform.ThrowIfUnsupported();
    }
}
