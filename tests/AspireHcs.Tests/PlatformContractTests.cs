using System.Runtime.Versioning;
using Xunit;

namespace AspireHcs.Tests;

// The shipped assembly advertises the literal public platform contract "windows10.0.17763".
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
    }
}
