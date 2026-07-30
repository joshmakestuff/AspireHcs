using System.Runtime.Versioning;
using Xunit;

namespace AspireHcs.Tests;

// The package advertises "Windows-only, fails fast elsewhere". These tests pin that
// claim to the shipped assembly rather than trusting the label.
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
        // CI and dev machines for this repo are Windows 10 1809+; on anything older or
        // non-Windows the guard is *supposed* to throw, so only assert the happy path here.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        HcsPlatform.ThrowIfUnsupported();
    }
}
