using System.Runtime.Versioning;
using System.Text.Json;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Pins the <c>guest mount</c> and <c>files expose</c> argv, and the bindings for every new
/// document in the VM bind-mount feature. The argv is a wire contract with hcsctl: order and
/// spelling are asserted exactly, so an accidental reorder or a renamed flag fails here rather
/// than at boot. The document names are the other half of that contract, pinned from JSON hcsctl
/// v0.8.0 actually emits.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class VmMountArgumentsTests
{
    private const string VmId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Guest_mount_defaults_produce_exactly_the_baseline_argv()
    {
        List<string> argv = HcsCtlGuests.BuildMountArguments(
            VmId, @"\\172.28.191.1\hcsctl-files\11111111\data", "/mnt/data", "hcsctl-files",
            readOnly: false, uid: 0, gid: 0, timeout: null);

        Assert.Equal(
        [
            "guest", "mount",
            "--vmid", VmId,
            "--unc", @"\\172.28.191.1\hcsctl-files\11111111\data",
            "--path", "/mnt/data",
            "--credential", "hcsctl-files",
        ], argv);
    }

    [Fact]
    public void Guest_mount_emits_every_option_in_a_stable_order()
    {
        List<string> argv = HcsCtlGuests.BuildMountArguments(
            VmId, @"\\gw\hcsctl-files-ro\vm\cfg", "/mnt/cfg", "hcsctl-files",
            readOnly: true, uid: 1000, gid: 1000, timeout: TimeSpan.FromSeconds(60));

        Assert.Equal(
        [
            "guest", "mount",
            "--vmid", VmId,
            "--unc", @"\\gw\hcsctl-files-ro\vm\cfg",
            "--path", "/mnt/cfg",
            "--credential", "hcsctl-files",
            "--ro",
            "--uid", "1000",
            "--gid", "1000",
            "--timeout", "60s",
        ], argv);
    }

    [Fact]
    public void Guest_mount_never_puts_a_password_in_argv()
    {
        // The credential is a Credential Manager target hcsctl reads itself; the password never
        // crosses into AspireHcs, let alone onto a command line.
        List<string> argv = HcsCtlGuests.BuildMountArguments(
            VmId, @"\\gw\s\a", "/mnt/data", "hcsctl-files", readOnly: false, uid: 0, gid: 0, timeout: null);

        Assert.DoesNotContain("--password", argv);
        Assert.DoesNotContain("--password-stdin", argv);
        Assert.DoesNotContain("--user", argv);
    }

    [Fact]
    public void Files_expose_defaults_produce_exactly_the_baseline_argv()
    {
        List<string> argv = HcsCtlFiles.BuildExposeArguments(
            VmId, "data", @"C:\src\data", readOnly: false, labels: null, root: null);

        Assert.Equal(
        [
            "files", "expose",
            "--vmid", VmId,
            "--name", "data",
            "--source", @"C:\src\data",
        ], argv);
    }

    [Fact]
    public void Files_expose_emits_ro_labels_and_root_in_a_stable_order()
    {
        List<string> argv = HcsCtlFiles.BuildExposeArguments(
            VmId, "cfg", @"C:\src\cfg", readOnly: true,
            labels: new Dictionary<string, string> { ["aspirehcs-apphost-pid"] = "1234" },
            root: @"D:\hcsctl\files");

        Assert.Equal(
        [
            "files", "expose",
            "--vmid", VmId,
            "--name", "cfg",
            "--source", @"C:\src\cfg",
            "--ro",
            "--label", "aspirehcs-apphost-pid=1234",
            "--root", @"D:\hcsctl\files",
        ], argv);
    }

    [Fact]
    public void Inspect_document_binds_the_prepared_shape()
    {
        // The shape hcsctl v0.8.0 emits for a prepared host with one network admitted.
        const string json = """
            {"ok":true,"command":"files inspect","prepared":true,"root":"C:\\ProgramData\\hcsctl\\files",
             "shares":{"readWrite":{"name":"hcsctl-files","present":true},"readOnly":{"name":"hcsctl-files-ro","present":true}},
             "user":{"name":"hcsctl-files","present":true},
             "credential":{"target":"hcsctl-files","present":true},
             "firewall":{"rule":"hcsctl-files SMB-In","present":true,"enabled":true,"interfaces":["vEthernet (nat)"]},
             "networks":["nat"],"exposures":2,"missing":[]}
            """;

        HcsCtlFilesInspectDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesInspectDocument)!;

        Assert.True(doc.Prepared);
        Assert.Equal(@"C:\ProgramData\hcsctl\files", doc.Root);
        Assert.Equal("hcsctl-files", doc.Shares!.ReadWrite!.Name);
        Assert.True(doc.Shares.ReadOnly!.Present);
        Assert.Equal("hcsctl-files", doc.Credential!.Target);
        Assert.True(doc.Firewall!.Enabled);
        Assert.Equal(["nat"], doc.Networks);
        Assert.Equal(2, doc.Exposures);
        Assert.Empty(doc.Missing);
    }

    [Fact]
    public void Inspect_document_binds_the_unprepared_shape_with_go_nulls()
    {
        // hcsctl is Go: a nil slice marshals as null, not []. The collection getters must absorb it.
        const string json = """
            {"ok":true,"command":"files inspect","prepared":false,"root":"C:\\ProgramData\\hcsctl\\files",
             "shares":{"readWrite":{"name":"hcsctl-files","present":false},"readOnly":{"name":"hcsctl-files-ro","present":false}},
             "user":{"name":"hcsctl-files","present":false},
             "credential":{"target":"hcsctl-files","present":false},
             "firewall":{"rule":"hcsctl-files SMB-In","present":false,"enabled":false,"interfaces":null},
             "networks":null,"exposures":0,"missing":["share hcsctl-files","user","credential","firewall rule"]}
            """;

        HcsCtlFilesInspectDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesInspectDocument)!;

        Assert.False(doc.Prepared);
        Assert.Empty(doc.Networks);
        Assert.Empty(doc.Firewall!.Interfaces);
        Assert.Contains("credential", doc.Missing);
    }

    [Fact]
    public void Expose_document_binds_the_share_and_relative_path()
    {
        const string json = """
            {"ok":true,"command":"files expose","vmId":"vm","name":"data","source":"C:\\src\\data",
             "junction":"C:\\ProgramData\\hcsctl\\files\\vm\\data","relativePath":"vm\\data",
             "share":"hcsctl-files","readOnly":false,"aceAdded":true}
            """;

        HcsCtlFilesExposeDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesExposeDocument)!;

        Assert.Equal("hcsctl-files", doc.Share);
        Assert.Equal(@"vm\data", doc.RelativePath);
        Assert.True(doc.AceAdded);
    }

    [Fact]
    public void Unexpose_document_binds_removed_and_revoked()
    {
        const string json = """
            {"ok":true,"command":"files unexpose","vmId":"vm","removed":["data","cfg"],"aceRevoked":["C:\\src\\data"]}
            """;

        HcsCtlFilesUnexposeDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesUnexposeDocument)!;

        Assert.Equal(["data", "cfg"], doc.Removed);
        Assert.Equal([@"C:\src\data"], doc.AceRevoked);
    }

    [Fact]
    public void Unexpose_document_absorbs_go_nulls_on_a_noop()
    {
        const string json = """{"ok":true,"command":"files unexpose","vmId":"vm","removed":null,"aceRevoked":null}""";

        HcsCtlFilesUnexposeDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesUnexposeDocument)!;

        Assert.Empty(doc.Removed);
        Assert.Empty(doc.AceRevoked);
    }

    [Fact]
    public void List_document_binds_rows_and_their_labels()
    {
        const string json = """
            {"ok":true,"command":"files ls","root":"C:\\ProgramData\\hcsctl\\files",
             "exposures":[{"vmId":"vm","name":"data","source":"C:\\src\\data","share":"hcsctl-files",
             "readOnly":false,"labels":{"aspirehcs-apphost-pid":"1234"}}]}
            """;

        HcsCtlFilesListDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesListDocument)!;

        HcsCtlFilesExposureRow row = Assert.Single(doc.Exposures);
        Assert.Equal("vm", row.VmId);
        Assert.Equal("1234", row.Labels["aspirehcs-apphost-pid"]);
    }

    [Fact]
    public void List_document_absorbs_a_null_exposures_array()
    {
        const string json = """{"ok":true,"command":"files ls","root":"C:\\ProgramData\\hcsctl\\files","exposures":null}""";

        HcsCtlFilesListDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlFilesListDocument)!;

        Assert.Empty(doc.Exposures);
    }

    [Fact]
    public void Mount_document_binds_the_applied_mechanism()
    {
        const string json = """
            {"ok":true,"command":"guest mount","vmId":"vm","unc":"\\\\gw\\hcsctl-files\\vm\\data",
             "path":"/mnt/data","readOnly":false,"applied":"cifs","elapsedMs":142}
            """;

        HcsCtlGuestMountDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlGuestMountDocument)!;

        Assert.Equal("cifs", doc.Applied);
        Assert.Equal("/mnt/data", doc.Path);
        Assert.Equal(142, doc.ElapsedMs);
    }

    [Fact]
    public void Unmount_document_binds_the_path()
    {
        const string json = """
            {"ok":true,"command":"guest unmount","vmId":"vm","path":"/mnt/data","applied":"cifs","elapsedMs":8}
            """;

        HcsCtlGuestUnmountDocument doc = JsonSerializer.Deserialize(json, HcsCtlJsonContext.Default.HcsCtlGuestUnmountDocument)!;

        Assert.Equal("/mnt/data", doc.Path);
        Assert.Equal("cifs", doc.Applied);
    }
}
