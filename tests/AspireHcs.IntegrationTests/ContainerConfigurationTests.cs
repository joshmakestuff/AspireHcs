using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Environment, mounts and scratch size. Every assertion here is made from inside the guest: an
// AppHost can believe it set a variable that HCS dropped, and a mount that appears in a config
// document is not a mount the guest can read.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ContainerConfigurationTests(ITestOutputHelper output)
{
    // A sample AppHost sets a variable via WithEnvironment; cmd /c set inside the guest shows it.
    // The value has spaces and non-ASCII: both cross a process argv boundary and the HCS process
    // spec, and each is a chance to mangle it.
    [SkippableFact]
    public async Task An_environment_variable_reaches_the_guest_intact()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        const string value = "hello world ünïcødé";
        string logs = await ContainerFixture.RunAndCaptureAsync(
            "cmd /c set ASPIREHCS_PROBE",
            container => container.WithEnvironment("ASPIREHCS_PROBE", value),
            cts.Token);

        output.WriteLine(logs);
        Assert.Contains($"ASPIREHCS_PROBE={value}", logs);
    }

    // HCS treats an empty value as a deletion, so this must fail the resource, not start a
    // container whose environment differs from the model.
    [SkippableFact]
    public async Task An_empty_environment_value_fails_the_resource_rather_than_dropping_silently()
    {
        (string hcsctl, string store, _) = ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string[] before = await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token);

        IDistributedApplicationTestingBuilder appHost =
            await ContainerFixture.SampleAppHostAsync("cmd /c ver", cts.Token);
        HcsContainerResource resource = appHost.Resources.OfType<HcsContainerResource>().Single();
        appHost.CreateResourceBuilder(resource).WithEnvironment("EMPTY", "");

        await using (DistributedApplication app = await appHost.BuildAsync(cts.Token))
        {
            await app.StartAsync(cts.Token);
            await app.ResourceNotifications.WaitForResourceAsync(
                "worker", KnownResourceStates.FailedToStart, cts.Token);
            await app.StopAsync(cts.Token);
        }

        // It failed before acquiring anything: no new container was created for this run.
        string[] after = await ContainerFixture.ListContainerIdsAsync(hcsctl, store, cts.Token);
        Assert.Equal(before.Order(), after.Order());
    }

    // A file created on the host appears in the guest without a restart; a file written in the
    // guest appears on the host.
    [SkippableFact]
    public async Task A_bind_mount_carries_files_both_ways()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string shared = Directory.CreateTempSubdirectory("aspirehcs-mount").FullName;
        await File.WriteAllTextAsync(Path.Combine(shared, "from-host.txt"), "written on the host",
            cts.Token);

        string logs = await ContainerFixture.RunAndCaptureAsync(
            @"cmd /c type C:\shared\from-host.txt && echo written in the guest> C:\shared\from-guest.txt",
            container => container.WithBindMount(shared, @"C:\shared"),
            cts.Token);

        output.WriteLine(logs);
        Assert.Contains("written on the host", logs);

        string produced = Path.Combine(shared, "from-guest.txt");
        Assert.True(File.Exists(produced), $"The guest's write did not reach the host at {produced}.");
        Assert.Contains("written in the guest", await File.ReadAllTextAsync(produced, cts.Token));

        Directory.Delete(shared, recursive: true);
    }

    // A read-only mount rejects guest writes. Asserted by the write failing and by the file not
    // appearing on the host; a message alone could be produced by the wrong error.
    [SkippableFact]
    public async Task A_read_only_mount_rejects_guest_writes()
    {
        ContainerFixture.Require();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string shared = Directory.CreateTempSubdirectory("aspirehcs-mount-ro").FullName;
        await File.WriteAllTextAsync(Path.Combine(shared, "readable.txt"), "readable", cts.Token);

        string logs = await ContainerFixture.RunAndCaptureAsync(
            @"cmd /c type C:\ro\readable.txt && (echo nope> C:\ro\denied.txt) && echo WROTE-ANYWAY",
            container => container.WithBindMount(shared, @"C:\ro", isReadOnly: true),
            cts.Token);

        output.WriteLine(logs);

        // The mount is present; otherwise "cannot write" proves nothing.
        Assert.Contains("readable", logs);
        Assert.DoesNotContain("WROTE-ANYWAY", logs);
        Assert.False(File.Exists(Path.Combine(shared, "denied.txt")),
            "A read-only mount let the guest create a file on the host.");

        Directory.Delete(shared, recursive: true);
    }

    // fsutil volume diskfree c: inside the guest reports the requested size (± the measured
    // overhead). The default is 20 GB, so a 40 GB request that did nothing shows ~19.9 and fails.
    //
    // --scratch-size needs elevation, and nothing else on the container path does. Unelevated,
    // hcsctl fails with
    //
    //     ExpandScratchSize: hcsshim::ExpandScratchSize failed in Win32: Access is denied. (0x5)
    //
    // 0x5 is E_ACCESSDENIED (a group check, not a privilege), so no user-rights grant substitutes
    // for it. See https://github.com/joshmakestuff/hcsctl/issues/36. Skipped when not elevated.
    [SkippableFact]
    public async Task The_requested_scratch_size_is_what_the_guest_sees()
    {
        ContainerFixture.Require();
        Skip.IfNot(ContainerFixture.IsElevated,
            "Sizing the scratch needs elevation: ExpandScratchSize returns E_ACCESSDENIED from a filtered token " +
            "(measured 2026-08-07, hcsctl#36). Re-run elevated to exercise this.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        const int requestedGb = 40;
        string logs = await ContainerFixture.RunAndCaptureAsync(
            "cmd /c fsutil volume diskfree c:",
            container => container.WithScratchSize(requestedGb),
            cts.Token);

        output.WriteLine(logs);

        long totalBytes = ParseTotalBytes(logs);
        double totalGb = totalBytes / (double)(1L << 30);
        output.WriteLine($"guest C: total = {totalGb:F1} GB for a {requestedGb} GB request");

        // The measured overhead is ~0.1 GB; half a gigabyte of slack still fails if the option
        // did nothing.
        Assert.InRange(totalGb, requestedGb - 0.5, requestedGb + 0.5);
    }

    /// <summary>
    /// Pulls the "total bytes" figure out of <c>fsutil volume diskfree</c>. Its output carries
    /// three byte counts with localized labels; total is the largest of free, total and avail.
    /// </summary>
    private static long ParseTotalBytes(string logs)
    {
        long largest = 0;
        foreach (string line in logs.Split('\n'))
        {
            int colon = line.LastIndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string digits = new([.. line[(colon + 1)..].Where(char.IsAsciiDigit)]);
            if (digits.Length > 0 && long.TryParse(digits, out long value))
            {
                largest = Math.Max(largest, value);
            }
        }

        Assert.True(largest > 0, $"No byte count found in fsutil output:\n{logs}");
        return largest;
    }
}
