using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public class HcsContainerBuilderTests
{
    [Fact]
    public void AddHcsContainer_registers_resource_with_defaults_and_manifest_exclusion()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        builder.AddHcsContainer("worker");

        HcsContainerResource resource = Assert.Single(builder.Resources.OfType<HcsContainerResource>());
        Assert.Equal("worker", resource.Name);
        Assert.Equal(2048, resource.MemoryMb);
        Assert.Equal(2, resource.ProcessorCount);
        Assert.StartsWith($"aspirehcs-{Environment.ProcessId}-worker-", resource.ContainerId);

        // A locally-run container has no deployment story and must not land in a manifest.
        Assert.Contains(resource.Annotations, a => a is ManifestPublishingCallbackAnnotation);
    }

    [Fact]
    public void Builder_methods_configure_the_resource()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithImage("mcr.microsoft.com/windows/nanoserver:ltsc2025")
            .WithCommand("cmd /c ver")
            .WithMemory(gigabytes: 4)
            .WithProcessorCount(6)
            .WithHcsCtl(@"c:\tools\hcsctl.exe");

        Assert.Equal("mcr.microsoft.com/windows/nanoserver:ltsc2025", container.Resource.ImageReference);
        Assert.Equal("cmd /c ver", container.Resource.Command);
        Assert.Equal(4096, container.Resource.MemoryMb);
        Assert.Equal(6, container.Resource.ProcessorCount);
        Assert.Equal(@"c:\tools\hcsctl.exe", container.Resource.HcsCtlPath);
    }

    // Relative store paths are resolved against the AppHost's working directory, so the path
    // hcsctl receives does not depend on where it is launched from.
    [Fact]
    public void WithStore_resolves_a_relative_path()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker").WithStore("store");

        Assert.NotNull(container.Resource.StorePath);
        Assert.True(Path.IsPathFullyQualified(container.Resource.StorePath));
    }

    [Fact]
    public void The_first_endpoint_declared_backs_the_connection_string()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker")
            .WithEndpoint("http", 8080)
            .WithEndpoint("admin", 9090);

        Assert.Equal("http", container.Resource.PrimaryEndpointName);
        Assert.Contains("http", container.Resource.ConnectionStringExpression.ValueExpression);
    }

    // Referencing a container with no endpoints is a mistake in the AppHost; it fails at model
    // build, not as an expression that resolves to nothing at run time.
    [Fact]
    public void Referencing_a_container_with_no_endpoints_explains_itself()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);

        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => container.Resource.ConnectionStringExpression);

        Assert.Contains("WithEndpoint", thrown.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_sizes_are_rejected_at_model_build_time(int value)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<HcsContainerResource> container = builder.AddHcsContainer("worker");

        Assert.Throws<ArgumentOutOfRangeException>(() => container.WithMemory(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.WithProcessorCount(value));
    }

    // There is no isolation switch: process isolation is out of scope and hcsctl does not
    // implement it.
    [Fact]
    public void No_builder_method_offers_process_isolation()
    {
        Assert.DoesNotContain(
            typeof(HcsContainerBuilderExtensions).GetMethods(),
            m => m.Name.Contains("Isolation", StringComparison.OrdinalIgnoreCase));
    }
}
