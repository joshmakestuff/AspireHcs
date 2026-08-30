using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Pins the <c>vm create</c> argv and the create-document bindings. The argv is a wire contract
/// with hcsctl: order and spelling are asserted exactly, so an accidental reordering or a
/// renamed flag fails here rather than at boot.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class VmCreateArgumentsTests
{
    private static HcsCtlVmCreateOptions Minimal => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        VhdxPath = @"c:\images\boot.vhdx",
    };

    [Fact]
    public void Defaults_produce_exactly_the_baseline_argv()
    {
        List<string> argv = HcsCtlVirtualMachines.BuildCreateArguments(Minimal);

        Assert.Equal(
        [
            "vm", "create",
            "--id", "11111111-2222-3333-4444-555555555555",
            "--vhdx", @"c:\images\boot.vhdx",
            "--cpus", "2",
            "--memory-mb", "2048",
        ], argv);
    }

    [Fact]
    public void Every_option_emits_its_flag_in_a_stable_order()
    {
        List<string> argv = HcsCtlVirtualMachines.BuildCreateArguments(Minimal with
        {
            DataDisks = [@"c:\images\data1.vhdx", @"c:\images\data2.vhdx"],
            ProcessorCount = 12,
            MemoryMb = 16384,
            Network = "LAB",
            MacAddress = "00-15-5D-02-33-0E",
            VlanId = 10,
            SerialPipe = @"\\.\pipe\vm-com1",
            Labels = new Dictionary<string, string> { ["owner"] = "test" },
        });

        Assert.Equal(
        [
            "vm", "create",
            "--id", "11111111-2222-3333-4444-555555555555",
            "--vhdx", @"c:\images\boot.vhdx",
            "--cpus", "12",
            "--memory-mb", "16384",
            "--disk", @"c:\images\data1.vhdx",
            "--disk", @"c:\images\data2.vhdx",
            "--network", "LAB",
            "--mac", "00-15-5D-02-33-0E",
            "--vlan", "10",
            "--serial-pipe", @"\\.\pipe\vm-com1",
            "--label", "owner=test",
        ], argv);
    }

    [Fact]
    public void Unset_options_emit_no_flag()
    {
        List<string> argv = HcsCtlVirtualMachines.BuildCreateArguments(Minimal);

        Assert.DoesNotContain("--disk", argv);
        Assert.DoesNotContain("--mac", argv);
        Assert.DoesNotContain("--vlan", argv);
        Assert.DoesNotContain("--network", argv);
        Assert.DoesNotContain("--serial-pipe", argv);
        Assert.DoesNotContain("--label", argv);
    }

    [Fact]
    public void Create_document_binds_disks_and_vlan()
    {
        // The shape hcsctl v0.7.0 reports for a create with extra disks and a VLAN.
        const string json = """
            {"ok":true,"id":"abc","diskPath":"c:\\store\\disk.vhdx",
             "disks":["c:\\store\\disk1.vhdx","c:\\store\\disk2.vhdx"],
             "network":"LAB","endpointId":"ep","macAddress":"00-15-5D-02-33-0E","vlan":10}
            """;

        HcsCtlVmCreateDocument document = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlVmCreateDocument)!;

        Assert.Equal([@"c:\store\disk1.vhdx", @"c:\store\disk2.vhdx"], document.Disks);
        Assert.Equal(10, document.Vlan);
    }

    [Fact]
    public void Create_document_defaults_disks_and_vlan_when_absent()
    {
        // hcsctl omits both fields for a single-disk untagged VM (omitempty).
        const string json = """{"ok":true,"id":"abc","diskPath":"c:\\store\\disk.vhdx"}""";

        HcsCtlVmCreateDocument document = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlVmCreateDocument)!;

        Assert.Empty(document.Disks);
        Assert.Equal(0, document.Vlan);
    }
}
