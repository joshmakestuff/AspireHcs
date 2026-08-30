using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AspireHcs.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// The agentless path end to end: a vendor appliance's disks (no hcsguest, no DHCP, a fixed
// in-guest address) boot to Running and Healthy, the endpoints resolve at the declared address
// — proving `vm ip` never ran — and teardown removes the VM without ever writing the bases.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ApplianceVmTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Agentless_appliance_boots_to_healthy_at_its_fixed_address_and_tears_down()
    {
        string? applianceVhdx = Environment.GetEnvironmentVariable("HCS_TEST_APPLIANCE_VHDX");
        string? applianceAddress = Environment.GetEnvironmentVariable("HCS_TEST_APPLIANCE_ADDRESS");
        Skip.If(string.IsNullOrEmpty(applianceVhdx) || string.IsNullOrEmpty(applianceAddress),
            "Set HCS_TEST_APPLIANCE_VHDX (the appliance's boot VHDX) and HCS_TEST_APPLIANCE_ADDRESS " +
            "(its fixed in-guest IP) to run the agentless appliance test. Optional: " +
            "HCS_TEST_APPLIANCE_DATA_VHDX, _NETWORK, _MAC, _VLAN, _HEALTH_PATH, _MEMORY_GB, _CPUS, " +
            "_SSH_USER. An appliance can take 10-15 minutes to boot; the test budget is 25.");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(25));

        // The base disks must come back untouched: the whole point of copy-on-write children.
        string? dataVhdx = Environment.GetEnvironmentVariable("HCS_TEST_APPLIANCE_DATA_VHDX");
        string[] bases = string.IsNullOrEmpty(dataVhdx) ? [applianceVhdx] : [applianceVhdx, dataVhdx];
        DateTime[] baseTimestamps = [.. bases.Select(File.GetLastWriteTimeUtc)];

        // The appliance block must run alone: the sample also boots its other opt-in guests
        // when their variables happen to be set on this host.
        string? linuxVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_SAMPLE_WINDOWS_VHDX");
        Environment.SetEnvironmentVariable("HCS_TEST_VHDX", null);
        Environment.SetEnvironmentVariable("HCS_SAMPLE_WINDOWS_VHDX", null);
        try
        {
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.HcsSample_AppHost>(cts.Token);

            await using DistributedApplication app = await appHost.BuildAsync(cts.Token);
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync(
                "vendor", KnownResourceStates.Running, cts.Token);
            output.WriteLine("resource reached Running");

            await app.ResourceNotifications.WaitForResourceHealthyAsync("vendor", cts.Token);
            output.WriteLine("resource reached Healthy (the insecure HTTPS check answered)");

            // The endpoints resolve at the declared fixed address — the agentless path's
            // contract. A leased address here would mean `vm ip` ran after all.
            Uri endpoint = app.GetEndpoint("vendor", "https");
            string? connectionString = await app.GetConnectionStringAsync("vendor", cancellationToken: cts.Token);
            Assert.Equal(applianceAddress, endpoint.Host);
            Assert.Equal(443, endpoint.Port);
            Assert.Equal($"{endpoint.Host}:{endpoint.Port}", connectionString);

            string vmId = app.Services.GetRequiredService<DistributedApplicationModel>()
                .Resources.OfType<HcsVirtualMachineResource>().Single(r => r.Name == "vendor").VmId;

            await app.StopAsync(cts.Token);

            // Teardown removed the VM from the store the sample uses.
            Assert.DoesNotContain(vmId, HcsCtlProbes.VmIds(SampleStorePath()), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCS_TEST_VHDX", linuxVhdx);
            Environment.SetEnvironmentVariable("HCS_SAMPLE_WINDOWS_VHDX", windowsVhdx);
        }

        // The vendor's originals were never written.
        for (int i = 0; i < bases.Length; i++)
        {
            Assert.Equal(baseTimestamps[i], File.GetLastWriteTimeUtc(bases[i]));
        }
    }

    /// <summary>
    /// The store the sample defaults to (<c>samples\.store</c>), unless <c>ASPIREHCS_STORE</c>
    /// overrides it — the same resolution the sample's AppHost.cs performs.
    /// </summary>
    private static string SampleStorePath()
    {
        if (Environment.GetEnvironmentVariable("ASPIREHCS_STORE") is { Length: > 0 } configured)
        {
            return configured;
        }

        Assert.True(RepositoryTools.TryFindRepositoryRoot(out string? root),
            "repository root not found from the test base directory");
        return Path.Combine(root!, "samples", ".store");
    }
}
