using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// HCS treats FOO= as a deletion, so an empty value leaves the variable absent inside the guest,
// not present-and-empty. Aspire makes empty values likely (an unresolved parameter or a
// not-yet-allocated endpoint reference produces one), so resolution fails on them.
[SupportedOSPlatform("windows10.0.17763")]
public class ContainerEnvironmentTests
{
    private static DistributedApplicationExecutionContext RunMode => new(DistributedApplicationOperation.Run);

    [Fact]
    public async Task A_resource_with_no_environment_resolves_to_nothing()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        Assert.Empty(await ContainerEnvironment.ResolveAsync(container.Resource, RunMode));
    }

    [Fact]
    public async Task Literal_values_resolve()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("GREETING", "hello")
            .WithEnvironment("COUNT", "3");

        IReadOnlyDictionary<string, string> resolved =
            await ContainerEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("hello", resolved["GREETING"]);
        Assert.Equal("3", resolved["COUNT"]);
    }

    // Values with spaces, quotes and non-ASCII cross a process argv boundary as well as the HCS
    // document. Resolution must not be where they get mangled.
    [Theory]
    [InlineData("a value with spaces")]
    [InlineData("has \"quotes\" inside")]
    [InlineData(@"trailing\backslash\")]
    [InlineData("ünïcødé-λ-日本語")]
    [InlineData("semi;colon&ampersand|pipe")]
    public async Task Awkward_values_survive_resolution(string value)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("AWKWARD", value);

        IReadOnlyDictionary<string, string> resolved =
            await ContainerEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal(value, resolved["AWKWARD"]);
    }

    [Fact]
    public async Task An_empty_value_fails_loudly_rather_than_being_dropped()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("EMPTY", "");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ContainerEnvironment.ResolveAsync(container.Resource, RunMode));

        // The message must name the variable and the resource.
        Assert.Contains("EMPTY", thrown.Message);
        Assert.Contains("worker", thrown.Message);
        // And it must name the mechanism.
        Assert.Contains("deletion", thrown.Message);
    }

    [Fact]
    public async Task A_null_value_fails_the_same_way()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context => context.EnvironmentVariables["NOTHING"] = null!);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ContainerEnvironment.ResolveAsync(container.Resource, RunMode));

        Assert.Contains("NOTHING", thrown.Message);
    }

    // WithReference compiles down to WithEnvironment, so a connection string must survive the
    // same path.
    [Fact]
    public async Task A_referenced_connection_string_resolves_to_its_value()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<ParameterResource> secret = builder.AddParameter("db", "Server=localhost;Db=orders", secret: true);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("ConnectionStrings__db", secret);

        IReadOnlyDictionary<string, string> resolved =
            await ContainerEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("Server=localhost;Db=orders", resolved["ConnectionStrings__db"]);
    }
}
