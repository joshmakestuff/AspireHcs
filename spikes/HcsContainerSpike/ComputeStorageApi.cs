// The MODERN layer-storage surface (computestorage.dll), bound for issue #33
// experiment 4: "its privilege model may differ from legacy wclayer — worth one
// probe before concluding anything about 'the API'".
//
// This is a deliberate mirror of the legacy wclayer chain in WcLayerApi.cs, so
// the two can be run side by side at the same privilege level against the same
// store and the HRESULTs compared call for call:
//
//   legacy (vmcompute.dll)          modern (computestorage.dll)
//   ----------------------          ---------------------------
//   CreateSandboxLayer              HcsInitializeWritableLayer
//   ActivateLayer + PrepareLayer    HcsAttachLayerStorageFilter
//   UnprepareLayer + DeactivateLayer HcsDetachLayerStorageFilter
//   DestroyLayer                    HcsDestroyLayer
//
// Signatures read from hcsshim at tag v0.14.1 (computestorage/storage.go
// mkwinsyscall declarations + the wrappers in initialize.go / attach.go /
// detach.go / destroy.go); export names verified present in
// C:\Windows\System32\computestorage.dll (10.0.26100.8875) by dumping the PE
// export table on the reference host, not assumed.
//
// Note the shape difference that matters for the privilege question: the legacy
// calls take a DRIVER_INFO plus an array of descriptor structs, while the modern
// ones take the parent layers as a JSON `LayerData` document. Same information,
// different marshalling — so a divergence in HRESULT between the two is a real
// difference in the platform's gate, not an artifact of how we called it.
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static unsafe partial class ComputeStorage
{
    /// <summary>The `LayerData` document the modern calls take in place of the
    /// legacy descriptor array (hcsshim computestorage/storage.go: SchemaVersion
    /// + Layers). Ids are the same NameToGuid values the legacy path uses.</summary>
    private static string LayerData(IReadOnlyList<(string Path, Guid Id)> parentLayers) => new JsonObject
    {
        ["SchemaVersion"] = new JsonObject { ["Major"] = 2, ["Minor"] = 1 },
        ["Layers"] = new JsonArray([.. parentLayers.Select(l => (JsonNode)new JsonObject
        {
            ["Id"] = l.Id.ToString(),
            ["Path"] = l.Path,
        })]),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Modern equivalent of CreateSandboxLayer. Options are documented
    /// as unused by the platform as of RS5; hcsshim passes "".</summary>
    public static HRESULT InitializeWritableLayer(string writableLayerPath, IReadOnlyList<(string Path, Guid Id)> parentLayers) =>
        new(HcsInitializeWritableLayer(EnsureTrailingSlash(writableLayerPath), LayerData(parentLayers), ""));

    /// <summary>Modern equivalent of ActivateLayer + PrepareLayer.</summary>
    public static HRESULT AttachLayerStorageFilter(string writableLayerPath, IReadOnlyList<(string Path, Guid Id)> parentLayers) =>
        new(HcsAttachLayerStorageFilter(EnsureTrailingSlash(writableLayerPath), LayerData(parentLayers)));

    /// <summary>Modern equivalent of UnprepareLayer + DeactivateLayer.</summary>
    public static HRESULT DetachLayerStorageFilter(string writableLayerPath) =>
        new(HcsDetachLayerStorageFilter(EnsureTrailingSlash(writableLayerPath)));

    public static HRESULT DestroyLayer(string layerPath) =>
        new(HcsDestroyLayer(EnsureTrailingSlash(layerPath)));

    /// <summary>hcsshim's doc comment for these entry points says the platform
    /// appends a trailing separator itself if absent. Depending on undocumented
    /// normalization for a privilege experiment would be sloppy — a path that
    /// silently addressed the wrong thing would read as a privilege result — so
    /// we pass exactly what the API documents it wants.</summary>
    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith('\\') ? path : path + '\\';

    [LibraryImport("computestorage.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int HcsInitializeWritableLayer(string writableLayerPath, string layerData, string options);

    [LibraryImport("computestorage.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int HcsAttachLayerStorageFilter(string layerPath, string layerData);

    [LibraryImport("computestorage.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int HcsDetachLayerStorageFilter(string layerPath);

    [LibraryImport("computestorage.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int HcsDestroyLayer(string layerPath);
}
