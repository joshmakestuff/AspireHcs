using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// HCS treats FOO= as a deletion, so an empty value leaves the variable absent inside the guest,
// not present-and-empty. Aspire makes empty values likely (an unresolved parameter or a
// not-yet-allocated endpoint reference produces one), so resolution fails on them.
//
// Resolution also records provenance: which values carry endpoints of other resources (the only
// values the loopback redirect may rewrite), and which came from providers it cannot see
// through. A user's literal appears in neither list, whatever it spells.
[SupportedOSPlatform("windows10.0.17763")]
public class GuestEnvironmentTests
{
    private static DistributedApplicationExecutionContext RunMode => new(DistributedApplicationOperation.Run);

    [Fact]
    public async Task A_resource_with_no_environment_resolves_to_nothing()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Empty(resolved.Values);
        Assert.Empty(resolved.Occurrences);
        Assert.Empty(resolved.OpaqueNames);
    }

    [Fact]
    public async Task Literal_values_resolve()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("GREETING", "hello")
            .WithEnvironment("COUNT", "3");

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("hello", resolved.Values["GREETING"]);
        Assert.Equal("3", resolved.Values["COUNT"]);
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

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal(value, resolved.Values["AWKWARD"]);
    }

    [Fact]
    public async Task An_empty_value_fails_loudly_rather_than_being_dropped()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("EMPTY", "");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GuestEnvironment.ResolveAsync(container.Resource, RunMode));

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
            () => GuestEnvironment.ResolveAsync(container.Resource, RunMode));

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

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("Server=localhost;Db=orders", resolved.Values["ConnectionStrings__db"]);
    }

    // ---- Provenance: which values the loopback redirect may touch ----

    /// <summary>An allocated endpoint on a second resource, the way DCP would leave it in run mode.</summary>
    private static EndpointReference AllocatedEndpoint(
        IDistributedApplicationBuilder builder, string host = "localhost", int port = 62310)
    {
        IResourceBuilder<HcsContainerResource> provider = builder.AddHcsContainer("cache")
            .WithEndpoint("tcp", 6379);

        EndpointAnnotation annotation = provider.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, host, port, targetPortExpression: null);

        return new EndpointReference(provider.Resource, annotation);
    }

    // A literal that spells a loopback endpoint is the user's configuration, meant as written —
    // a guest-local listener address, not a reference to another resource. It must produce no
    // occurrence and no opaque mark, so the redirect can never rewrite it.
    [Fact]
    public async Task A_literal_loopback_value_is_not_an_endpoint_occurrence()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("BIND_ADDRESS", "127.0.0.1:8080")
            .WithEnvironment("SELF_HOST", "localhost")
            .WithEnvironment("SELF_PORT", "8080");

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Empty(resolved.Occurrences);
        Assert.Empty(resolved.OpaqueNames);
    }

    [Fact]
    public async Task An_endpoint_reference_is_an_embedded_occurrence()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        EndpointReference endpoint = AllocatedEndpoint(builder);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context => context.EnvironmentVariables["CACHE"] = endpoint);

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        GuestEndpointOccurrence occurrence = Assert.Single(resolved.Occurrences);
        Assert.Equal(("CACHE", EndpointOccurrenceKind.Embedded, "localhost", 62310),
            (occurrence.Name, occurrence.Kind, occurrence.Host, occurrence.Port));
    }

    // Aspire's split shape: WithReference on many integrations injects X_HOST and X_PORT as two
    // property expressions over the same endpoint. Provenance identifies the pair precisely, by
    // object rather than by name convention.
    [Fact]
    public async Task Host_and_port_property_expressions_are_typed_occurrences()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        EndpointReference endpoint = AllocatedEndpoint(builder);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context =>
        {
            context.EnvironmentVariables["CACHE_HOST"] = endpoint.Property(EndpointProperty.Host);
            context.EnvironmentVariables["CACHE_PORT"] = endpoint.Property(EndpointProperty.Port);
        });

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("localhost", resolved.Values["CACHE_HOST"]);
        Assert.Equal("62310", resolved.Values["CACHE_PORT"]);
        Assert.Equal(EndpointOccurrenceKind.HostOnly,
            Assert.Single(resolved.Occurrences, o => o.Name == "CACHE_HOST").Kind);
        Assert.Equal(EndpointOccurrenceKind.PortOnly,
            Assert.Single(resolved.Occurrences, o => o.Name == "CACHE_PORT").Kind);
    }

    // The connection-string shape: an interpolated expression mixing endpoint properties with
    // literals and parameters. The endpoint inside is found; the parameter contributes nothing.
    [Fact]
    public async Task An_interpolated_expression_over_an_endpoint_is_an_embedded_occurrence()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        EndpointReference endpoint = AllocatedEndpoint(builder);
        ParameterResource password = builder.AddParameter("pw", "hunter2", secret: true).Resource;

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context => context.EnvironmentVariables["ConnectionStrings__cache"] =
            ReferenceExpression.Create(
                $"{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)},password={password}"));

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("localhost:62310,password=hunter2", resolved.Values["ConnectionStrings__cache"]);
        Assert.Equal(2, resolved.Occurrences.Count);
        Assert.All(resolved.Occurrences, o => Assert.Equal(62310, o.Port));
        Assert.Empty(resolved.OpaqueNames);
    }

    // A parameter alone resolves to whatever the user configured — even a loopback endpoint. It
    // is neither an occurrence nor opaque: user configuration is meant as written.
    [Fact]
    public async Task A_parameter_spelling_a_loopback_endpoint_stays_untouchable()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<ParameterResource> address = builder.AddParameter("addr", "127.0.0.1:8080");

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEnvironment("UPSTREAM", address);

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("127.0.0.1:8080", resolved.Values["UPSTREAM"]);
        Assert.Empty(resolved.Occurrences);
        Assert.Empty(resolved.OpaqueNames);
    }

    // A provider this walk cannot see through gets the honest middle ground: marked opaque, so
    // the redirect falls back to matching its resolved text — for that variable only.
    [Fact]
    public async Task An_unclassifiable_provider_marks_its_variable_opaque()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");
        container.WithEnvironment(context => context.EnvironmentVariables["CUSTOM"] = new OpaqueProvider());

        ResolvedGuestEnvironment resolved = await GuestEnvironment.ResolveAsync(container.Resource, RunMode);

        Assert.Equal("localhost:5000", resolved.Values["CUSTOM"]);
        Assert.Empty(resolved.Occurrences);
        Assert.Equal("CUSTOM", Assert.Single(resolved.OpaqueNames));
    }

    private sealed class OpaqueProvider : IValueProvider
    {
        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("localhost:5000");
    }

    // ---- BuildEnvFile: the /etc/aspire.env format ----

    [Fact]
    public void The_env_file_is_one_name_value_line_per_variable()
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
