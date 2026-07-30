namespace AspireHcs.Hcs.Schema;

// Hand-rolled subset of the HCS schema v2.1 (https://learn.microsoft.com/virtualization/api/hcs/schemareference).
// Property names are the JSON keys — HCS documents are PascalCase, so no naming policy is applied.

internal sealed class ComputeSystemDocument
{
    public SchemaVersion SchemaVersion { get; set; } = new() { Major = 2, Minor = 1 };

    public string? Owner { get; set; }

    /// <summary>Crash-safe teardown: the VM dies with the last open handle (verified in issue #1).</summary>
    public bool ShouldTerminateOnLastHandleClosed { get; set; } = true;

    public VirtualMachine? VirtualMachine { get; set; }
}

internal sealed class SchemaVersion
{
    public uint Major { get; set; }
    public uint Minor { get; set; }
}

internal sealed class VirtualMachine
{
    public Chipset? Chipset { get; set; }
    public ComputeTopology? ComputeTopology { get; set; }
    public Devices? Devices { get; set; }
    public Services? Services { get; set; }
}

/// <summary>
/// Guest services. NewInVersion 2.5 — the document's SchemaVersion must be at least 2.5
/// or HCS silently ignores this section (verified empirically: shutdown then fails with
/// ERROR_NOT_SUPPORTED, 0x80070032).
/// </summary>
internal sealed class Services
{
    /// <summary>Opt-in for the guest shutdown integration service; serialized as an empty object.</summary>
    public ShutdownService? Shutdown { get; set; }
}

/// <summary>Deliberately empty: the schema wants <c>"Shutdown": {}</c>.</summary>
internal sealed class ShutdownService;

internal sealed class Chipset
{
    public Uefi? Uefi { get; set; }
}

internal sealed class Uefi
{
    public UefiBootEntry? BootThis { get; set; }
}

internal sealed class UefiBootEntry
{
    public string? DevicePath { get; set; }
    public int DiskNumber { get; set; }
    public string DeviceType { get; set; } = "ScsiDrive";
}

internal sealed class ComputeTopology
{
    public VmMemory? Memory { get; set; }
    public VmProcessor? Processor { get; set; }
}

internal sealed class VmMemory
{
    public string Backing { get; set; } = "Virtual";
    public int SizeInMB { get; set; }
}

internal sealed class VmProcessor
{
    public int Count { get; set; }
}

internal sealed class Devices
{
    public Dictionary<string, ScsiController>? Scsi { get; set; }
    public Dictionary<string, ComPort>? ComPorts { get; set; }
    public Dictionary<string, NetworkAdapter>? NetworkAdapters { get; set; }
}

internal sealed class ScsiController
{
    public Dictionary<string, Attachment>? Attachments { get; set; }
}

internal sealed class Attachment
{
    public string Type { get; set; } = "VirtualDisk";
    public string? Path { get; set; }
}

internal sealed class ComPort
{
    public string? NamedPipe { get; set; }
}

internal sealed class NetworkAdapter
{
    public string? EndpointId { get; set; }
    public string? MacAddress { get; set; }
}
