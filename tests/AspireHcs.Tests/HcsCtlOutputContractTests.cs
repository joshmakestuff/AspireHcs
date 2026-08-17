using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// hcsctl promises stdout carries exactly one JSON document. These pin what happens when that
// promise is broken, with a stand-in binary: hcsctl cannot be made to violate its own contract
// on demand.
//
// These need no hcsctl and no HCS, so they never skip.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlOutputContractTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aspirehcs-fake-ctl").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A stand-in for hcsctl that emits exactly the stdout and exit code given.</summary>
    private HcsCtl FakeCtl(string batchBody)
    {
        string path = Path.Combine(_directory, $"fake-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off{Environment.NewLine}{batchBody}{Environment.NewLine}");
        return new HcsCtl(path);
    }

    [Fact]
    public async Task Stdout_that_is_not_json_is_a_contract_violation_not_a_success()
    {
        HcsCtl fake = FakeCtl("echo this is not a document");

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument));

        Assert.Contains("not one JSON document", thrown.Message);
        // The offending stdout is quoted.
        Assert.Contains("this is not a document", thrown.Message);
    }

    [Fact]
    public async Task Exit_zero_with_no_document_is_a_contract_violation()
    {
        HcsCtl fake = FakeCtl("exit /b 0");

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument));

        Assert.Contains("no document on stdout", thrown.Message);
    }

    // Reading the first document and ignoring the rest would bind successfully here.
    [Fact]
    public async Task A_second_document_on_stdout_is_rejected_rather_than_ignored()
    {
        HcsCtl fake = FakeCtl("""
            echo {"ok":true}
            echo {"ok":true}
            """);

        HcsCtlContractException thrown = await Assert.ThrowsAsync<HcsCtlContractException>(
            () => fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument));

        Assert.Contains("not one JSON document", thrown.Message);
    }

    [Fact]
    public async Task One_document_binds()
    {
        HcsCtl fake = FakeCtl("""echo {"ok":true,"version":"10.0.26200.8894","build":26200}""");

        HcsCtlInfoDocument info = await fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument);

        Assert.True(info.Ok);
        Assert.Equal("10.0.26200.8894", info.HostOsVersion);
        Assert.Equal(26200, info.HostBuild);
    }

    // A non-zero exit with no failure document must still fail, and must say the document was
    // missing.
    [Fact]
    public async Task A_failure_without_a_document_still_fails_and_says_so()
    {
        HcsCtl fake = FakeCtl("exit /b 1");

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument));

        Assert.Equal(HcsCtlExitCode.Failed, thrown.ExitCode);
        Assert.Contains("without a failure document", thrown.Message);
    }

    // hcsctl is Go, and Go marshals a nil slice or map as JSON `null`, not `[]`. "No containers"
    // arrives as `"containers": null`: routine output, not an error. A `= []` property
    // initializer does not survive it: the deserializer assigns the null over the top.
    [Fact]
    public async Task A_null_collection_binds_to_empty_rather_than_null()
    {
        HcsCtl fake = FakeCtl("""echo {"ok":true,"containers":null}""");

        HcsCtlContainerListDocument listing = await fake.InvokeAsync(
            ["container", "ls"], HcsCtlJsonContext.Default.HcsCtlContainerListDocument);

        Assert.Empty(listing.Containers);
    }

    [Fact]
    public async Task Every_nullable_collection_on_the_create_document_binds_to_empty()
    {
        HcsCtl fake = FakeCtl("""echo {"ok":true,"id":"c1","chain":null,"addresses":null}""");

        HcsCtlContainerCreateDocument created = await fake.InvokeAsync(
            ["container", "create"], HcsCtlJsonContext.Default.HcsCtlContainerCreateDocument);

        Assert.Empty(created.Chain);
        Assert.Empty(created.Addresses);
        Assert.Equal("c1", created.Id);
    }

    [Fact]
    public async Task Every_nullable_collection_on_the_info_document_binds_to_empty()
    {
        HcsCtl fake = FakeCtl("""echo {"ok":true,"privilegesHeld":null,"services":null,"images":null}""");

        HcsCtlInfoDocument info = await fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument);

        Assert.Empty(info.PrivilegesHeld);
        Assert.Empty(info.Services);
        Assert.Empty(info.Images);
    }

    // A null `services` must not read as "the preflight passed". An absent service report is a
    // blocker, and that must survive the null binding too.
    [Fact]
    public async Task A_null_services_map_still_blocks_the_preflight()
    {
        HcsCtl fake = FakeCtl("""echo {"ok":true,"services":null,"hyperVAdministrators":true}""");

        HcsCtlInfoDocument info = await fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument);

        Assert.NotNull(HcsCtlPreflight.DescribeBlocker(info));
    }

    // An exit code hcsctl does not document is a failure, not a success.
    [Fact]
    public async Task An_undocumented_exit_code_is_a_failure_and_keeps_its_code()
    {
        HcsCtl fake = FakeCtl("exit /b 3");

        HcsCtlCommandException thrown = await Assert.ThrowsAsync<HcsCtlCommandException>(
            () => fake.InvokeAsync(["info"], HcsCtlJsonContext.Default.HcsCtlInfoDocument));

        Assert.Equal(3, thrown.ExitCode);
    }
}
