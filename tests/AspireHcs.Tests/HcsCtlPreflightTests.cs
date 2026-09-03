using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// The preflight replaces an unactionable failure with an actionable one; each blocker must name
// the fix.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsCtlPreflightTests
{
    private static HcsCtlInfoDocument Healthy(
        bool hyperVAdministrators = true,
        string? contractVersion = HcsCtlPreflight.SupportedContractVersion,
        Dictionary<string, string>? services = null,
        HcsCtlStoreInfo? store = null,
        IReadOnlyList<HcsCtlImageInfo>? images = null) => new()
        {
            Ok = true,
            HostBuild = 26200,
            HostOsVersion = "10.0.26200.8894",
            ContractVersion = contractVersion,
            Elevated = false,
            HyperVAdministrators = hyperVAdministrators,
            Services = services ?? new Dictionary<string, string>
            {
                ["vmcompute"] = "running",
                ["vmms"] = "running",
                ["hvhost"] = "running",
            },
            Store = store ?? new HcsCtlStoreInfo { Root = @"C:\store", Exists = true },
            Images = images ?? [],
        };

    [Fact]
    public void A_healthy_unelevated_host_has_no_blocker()
    {
        // Elevation is not a prerequisite for running.
        Assert.Null(HcsCtlPreflight.DescribeBlocker(Healthy()));
    }

    // The contract gate runs before every other rule: without a recognized contractVersion the
    // document shape is unknown, so no service or group check is safe to interpret. "3" is the
    // only supported value and is covered by A_healthy_unelevated_host_has_no_blocker above;
    // earlier contracts are rejected, not tolerated — their document shapes differ.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("4")]
    public void An_unknown_or_missing_contract_version_is_a_blocker(string? contractVersion)
    {
        Assert.NotNull(HcsCtlPreflight.DescribeBlocker(Healthy(contractVersion: contractVersion)));
    }

    [Fact]
    public void A_stopped_vmcompute_names_the_service_and_its_state()
    {
        string? blocker = HcsCtlPreflight.DescribeBlocker(Healthy(services: new Dictionary<string, string>
        {
            ["vmcompute"] = "stopped",
            ["hvhost"] = "running",
        }));

        Assert.NotNull(blocker);
        Assert.Contains("vmcompute", blocker);
        Assert.Contains("stopped", blocker);
    }

    [Fact]
    public void A_missing_service_report_is_a_blocker_rather_than_an_assumed_pass()
    {
        // An absent key must not read as "running".
        string? blocker = HcsCtlPreflight.DescribeBlocker(Healthy(services: new Dictionary<string, string>
        {
            ["vmcompute"] = "running",
        }));

        Assert.NotNull(blocker);
        Assert.Contains("hvhost", blocker);
    }

    [Fact]
    public void Missing_hyperv_administrators_names_the_group_and_the_sign_out()
    {
        string? blocker = HcsCtlPreflight.DescribeBlocker(Healthy(hyperVAdministrators: false));

        Assert.NotNull(blocker);
        Assert.Contains("Hyper-V Administrators", blocker);
        // The membership only reaches the token at next logon.
        Assert.Contains("sign out", blocker);
    }

    [Fact]
    public void A_present_image_is_not_reported_as_missing()
    {
        HcsCtlInfoDocument info = Healthy(images:
            [new HcsCtlImageInfo { Reference = "mcr.microsoft.com/windows/nanoserver:ltsc2025" }]);

        Assert.Null(HcsCtlPreflight.DescribeMissingImage(info, "mcr.microsoft.com/windows/nanoserver:ltsc2025"));
    }

    [Fact]
    public void A_missing_image_prints_both_acquisition_commands_and_marks_only_import_elevated()
    {
        HcsCtlInfoDocument info = Healthy(images:
            [new HcsCtlImageInfo { Reference = "mcr.microsoft.com/windows/servercore:ltsc2022" }]);

        string? blocker = HcsCtlPreflight.DescribeMissingImage(info, "mcr.microsoft.com/windows/nanoserver:ltsc2025");

        Assert.NotNull(blocker);
        Assert.Contains("hcsctl image pull", blocker);
        Assert.Contains("hcsctl image import", blocker);
        Assert.Contains("elevated", blocker);
        // Elevating once per image buys unprivileged runs afterwards; the message must say so.
        Assert.Contains("running the container afterwards does not", blocker);
    }

    [Fact]
    public void An_absent_store_is_described_as_absent_rather_than_as_a_missing_image()
    {
        HcsCtlInfoDocument info = Healthy(store: new HcsCtlStoreInfo { Root = @"C:\store", Exists = false });

        string? blocker = HcsCtlPreflight.DescribeMissingImage(info, "mcr.microsoft.com/windows/nanoserver:ltsc2025");

        Assert.NotNull(blocker);
        Assert.Contains("does not exist yet", blocker);
    }

    private static HcsCtlFilesInspectDocument Prepared(params string[] networks) => new()
    {
        Ok = true,
        Prepared = true,
        Root = @"C:\ProgramData\hcsctl\files",
        Networks = networks,
        Missing = [],
    };

    [Fact]
    public void A_prepared_host_whose_rule_admits_the_network_has_no_files_blocker()
    {
        Assert.Null(HcsCtlPreflight.DescribeUnpreparedFiles(Prepared("Default Switch"), "Default Switch", "vm"));
    }

    [Fact]
    public void The_network_coverage_check_is_case_insensitive()
    {
        Assert.Null(HcsCtlPreflight.DescribeUnpreparedFiles(Prepared("nat"), "NAT", "vm"));
    }

    [Fact]
    public void An_unprepared_host_names_the_prepare_command_and_what_is_missing()
    {
        HcsCtlFilesInspectDocument inspect = new()
        {
            Ok = true,
            Prepared = false,
            Root = @"C:\ProgramData\hcsctl\files",
            Networks = [],
            Missing = ["share hcsctl-files", "credential"],
        };

        string? blocker = HcsCtlPreflight.DescribeUnpreparedFiles(inspect, "nat", "vm");

        Assert.NotNull(blocker);
        Assert.Contains("'vm'", blocker);
        Assert.Contains("hcsctl files prepare --network nat", blocker);
        Assert.Contains("elevated", blocker);
        Assert.Contains("credential", blocker);
    }

    [Fact]
    public void A_prepared_host_whose_rule_omits_the_network_names_the_prepare_command()
    {
        // The share exists but the VM's network is not admitted by the 445 rule; prepare is
        // repeatable and adds it.
        string? blocker = HcsCtlPreflight.DescribeUnpreparedFiles(Prepared("Default Switch"), "nat", "vm");

        Assert.NotNull(blocker);
        Assert.Contains("does not admit", blocker);
        Assert.Contains("Default Switch", blocker);
        Assert.Contains("hcsctl files prepare --network nat", blocker);
    }
}
