using System.Runtime.Versioning;
using Xunit;

namespace AspireHcs.Tests;

// The shipped assembly advertises the literal public platform contract "windows10.0.17763",
// and that advertisement must agree with the runtime guard's minimum.
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
        Assert.Equal("windows10.0.17763", attribute.PlatformName);
        // Ties the advertisement to the runtime guard's constant: raising the minimum in
        // HcsPlatform without updating the assembly attribute would ship a package whose
        // analyzer contract promises less than the guard enforces.
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
