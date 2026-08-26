using System.Runtime.Versioning;
using Xunit;

namespace AspireHcs.Tests;

// Pins the override resolution for ASPIREHCS_STORE and ASPIREHCS_TEMP. The environment
// variables are process-wide state, so the tests that touch them share the serialized
// collection with the other environment-dependent tests.
[Collection(HcsCtlEnvironmentCollection.Name)]
[SupportedOSPlatform("windows10.0.17763")]
public class AspireHcsEnvironmentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_store_means_hcsctls_per_user_default(string? value)
    {
        Assert.Null(AspireHcsEnvironment.ResolveStore(value));
    }

    [Fact]
    public void A_store_value_is_trimmed_and_made_absolute()
    {
        Assert.Equal(@"C:\stores\public", AspireHcsEnvironment.ResolveStore(@"  C:\stores\public  "));
    }

    [Fact]
    public void A_relative_store_value_resolves_against_the_current_directory()
    {
        Assert.Equal(Path.GetFullPath("store"), AspireHcsEnvironment.ResolveStore("store"));
    }

    [Fact]
    public void An_unset_temp_means_AspireHcs_under_the_system_temp_directory()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "AspireHcs"), AspireHcsEnvironment.ResolveTemp(null));
    }

    [Fact]
    public void A_temp_value_overrides_the_system_temp_directory()
    {
        Assert.Equal(@"C:\aspirehcs-tmp", AspireHcsEnvironment.ResolveTemp(@"C:\aspirehcs-tmp"));
    }

    // The properties must read the documented variable names; a typo there would leave the
    // resolver correct and the feature dead.
    [Fact]
    public void The_properties_read_the_documented_variables()
    {
        string? originalStore = Environment.GetEnvironmentVariable(AspireHcsEnvironment.StoreVariable);
        string? originalTemp = Environment.GetEnvironmentVariable(AspireHcsEnvironment.TempVariable);
        try
        {
            Environment.SetEnvironmentVariable("ASPIREHCS_STORE", @"C:\stores\from-env");
            Environment.SetEnvironmentVariable("ASPIREHCS_TEMP", @"C:\tmp\from-env");

            Assert.Equal(@"C:\stores\from-env", AspireHcsEnvironment.DefaultStorePath);
            Assert.Equal(@"C:\tmp\from-env", AspireHcsEnvironment.TempDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AspireHcsEnvironment.StoreVariable, originalStore);
            Environment.SetEnvironmentVariable(AspireHcsEnvironment.TempVariable, originalTemp);
        }
    }
}
