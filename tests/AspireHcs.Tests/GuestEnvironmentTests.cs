using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// #49. The trap this exists for: HCS treats FOO= as a DELETION, so an empty value leaves the
// variable absent inside the guest rather than present-and-empty. An app reading it sees nothing
// set, while the AppHost model swears it was.
//
// Aspire makes empty values likely rather than exotic — an unresolved parameter or a
// not-yet-allocated endpoint reference produces one — so the decision is to fail loudly.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestEnvironmentTests
{
    private static DistributedApplicationExecutionContext RunMode => new(DistributedApplicationOperation.Run);

    [Fact]
    public async Task A_resource_with_no_environment_resolves_to_nothing()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        Assert.Empty(await GuestEnvironment.ResolveAsync(container.Resource, RunMode));
    }

    [Fact]
    public async Task Literal_values_resolve()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("GREETING", "hello")
            .WithEnvironment("COUNT", "3");

        IReadOnlyDictionary<string, string> resolved =
            await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

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
            await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal(value, resolved["AWKWARD"]);
    }

    [Fact]
    public async Task An_empty_value_fails_loudly_rather_than_being_dropped()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("EMPTY", "");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GuestEnvironment.ResolveAsync(container.Resource, RunMode));

        // The message must name the variable and the resource; "an environment variable was
        // empty" would leave a developer grepping their AppHost.
        Assert.Contains("EMPTY", thrown.Message);
        Assert.Contains("worker", thrown.Message);
        // And it must explain the mechanism, or the reader concludes it is an arbitrary rule.
        Assert.Contains("deletion", thrown.Message);
    }

    [Fact]
    public async Task A_null_value_fails_the_same_way()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context => context.EnvironmentVariables["NOTHING"] = null!);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GuestEnvironment.ResolveAsync(container.Resource, RunMode));

        Assert.Contains("NOTHING", thrown.Message);
    }

    // WithReference compiles down to WithEnvironment, so a connection string must survive the
    // same path. Without this, referencing another resource from a container is dead on arrival.
    [Fact]
    public async Task A_referenced_connection_string_resolves_to_its_value()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<ParameterResource> secret = builder.AddParameter("db", "Server=localhost;Db=orders", secret: true);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("ConnectionStrings__db", secret);

        IReadOnlyDictionary<string, string> resolved =
            await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("Server=localhost;Db=orders", resolved["ConnectionStrings__db"]);
    }

    // The env-file rendering VM delivery writes to /etc/aspire.env (#62). Line-oriented: one
    // NAME=value per line, LF endings — what a POSIX shell or an EnvironmentFile= reader parses.
    [Fact]
    public void The_env_file_is_one_variable_per_line()
    {
        string file = GuestEnvironment.BuildEnvFile("rockyvm", new Dictionary<string, string>
        {
            ["ConnectionStrings__cache"] = "172.18.176.1:55007,password=hunter2",
            ["GREETING"] = "hello",
        });

        Assert.Equal("ConnectionStrings__cache=172.18.176.1:55007,password=hunter2\nGREETING=hello\n", file);
    }

    // A value with a line break would truncate on read and leave stray lines the guest parses as
    // other variables. Rejected by name rather than written wrong.
    [Fact]
    public void A_value_with_a_line_break_is_rejected_naming_the_variable()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => GuestEnvironment.BuildEnvFile("rockyvm", new Dictionary<string, string>
            {
                ["BROKEN"] = "line1\nline2",
            }));

        Assert.Contains("BROKEN", thrown.Message);
        Assert.Contains("rockyvm", thrown.Message);
    }

    // '=' in a name splits wrong on read: the guest would see a different variable holding the
    // remainder. Aspire does not produce such names, but WithEnvironment accepts any string.
    [Fact]
    public void A_name_with_an_equals_sign_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => GuestEnvironment.BuildEnvFile("rockyvm", new Dictionary<string, string>
            {
                ["BAD=NAME"] = "value",
            }));
    }
}
