using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using AspireHcs.Hcs.Schema;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.Tests;

// The HCS service silently ignores unknown/misplaced fields, so pin the exact JSON shape
// the schema types produce; a rename or nesting mistake here only fails at VM runtime.
[SupportedOSPlatform("windows10.0.17763")]
public class SchemaSerializationTests(ITestOutputHelper output)
{
    [Fact]
    public void Document_serializes_with_hcs_schema_shape()
    {
        ComputeSystemDocument document = new()
        {
            SchemaVersion = new() { Major = 2, Minor = 5 },
            Owner = "test",
            VirtualMachine = new()
            {
                Chipset = new() { Uefi = new() { BootThis = new() { DevicePath = "Primary disk", DiskNumber = 0 } } },
                ComputeTopology = new()
                {
                    Memory = new() { SizeInMB = 2048 },
                    Processor = new() { Count = 2 },
                },
                Devices = new()
                {
                    Scsi = new() { ["Primary disk"] = new() { Attachments = new() { ["0"] = new() { Path = @"c:\x.vhdx" } } } },
                    ComPorts = new() { ["0"] = new() { NamedPipe = @"\\.\pipe\test" } },
                },
                Services = new() { Shutdown = new() },
            },
        };

        string json = JsonSerializer.Serialize(document, HcsJsonContext.Default.ComputeSystemDocument);
        output.WriteLine(json);

        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal(2, root["SchemaVersion"]?["Major"]?.GetValue<int>());
        Assert.Equal(5, root["SchemaVersion"]?["Minor"]?.GetValue<int>());
        Assert.True(root["ShouldTerminateOnLastHandleClosed"]?.GetValue<bool>());

        JsonNode vm = root["VirtualMachine"]!;
        Assert.Equal("ScsiDrive", vm["Chipset"]?["Uefi"]?["BootThis"]?["DeviceType"]?.GetValue<string>());
        Assert.Equal("Virtual", vm["ComputeTopology"]?["Memory"]?["Backing"]?.GetValue<string>());
        Assert.Equal(2048, vm["ComputeTopology"]?["Memory"]?["SizeInMB"]?.GetValue<int>());
        Assert.Equal("VirtualDisk", vm["Devices"]?["Scsi"]?["Primary disk"]?["Attachments"]?["0"]?["Type"]?.GetValue<string>());
        Assert.Equal(@"\\.\pipe\test", vm["Devices"]?["ComPorts"]?["0"]?["NamedPipe"]?.GetValue<string>());

        // "Shutdown": {} — must be present and an empty object, or graceful shutdown is unsupported.
        Assert.NotNull(vm["Services"]?["Shutdown"]);
        Assert.Empty(vm["Services"]!["Shutdown"]!.AsObject());

        // Owner-less nulls must be omitted, not emitted as null (HCS rejects null members).
        Assert.DoesNotContain("null", json);
    }
}
