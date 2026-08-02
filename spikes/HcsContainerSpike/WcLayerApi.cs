// Manual interop for the legacy layer-management exports of vmcompute.dll.
// These are the APIs Docker/hcsshim use for process-isolated (windowsfilter)
// container storage; they are absent from the Win32 metadata, so CsWin32 cannot
// generate them. Shapes mirror hcsshim's internal/wclayer package: DRIVER_INFO
// with an empty home directory and the full layer path used as the layer id.
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace HcsContainerSpike;

[StructLayout(LayoutKind.Sequential)]
internal struct DriverInfo
{
    public int Flavour;      // 1 = filter driver (wcifs), per hcsshim GraphDriverType
    public nint HomeDir;     // PCWSTR; empty string when layer ids are full paths
}

[StructLayout(LayoutKind.Sequential)]
internal struct WcLayerDescriptor
{
    public Guid LayerId;     // NameToGuid(basename of layer path)
    public uint Flags;
    public nint Path;        // PCWSTR full path of the (read-only) layer
}

internal static unsafe partial class WcLayer
{
    private static readonly nint EmptyHome = Marshal.StringToHGlobalUni("");

    public static HRESULT LayerId(string layerPath, out Guid guid) =>
        new(NameToGuid(System.IO.Path.GetFileName(layerPath.TrimEnd('\\')), out guid));

    public static HRESULT CreateScratchLayer(string sandboxPath, IReadOnlyList<string> parentPaths)
    {
        WcLayerDescriptor[] descriptors = BuildDescriptors(parentPaths, out HRESULT hr);
        if (hr.Failed)
        {
            return hr;
        }
        try
        {
            DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
            fixed (WcLayerDescriptor* pDescs = descriptors)
            {
                return new(CreateSandboxLayer(&info, sandboxPath, 0, pDescs, (uint)descriptors.Length));
            }
        }
        finally
        {
            FreeDescriptors(descriptors);
        }
    }

    public static HRESULT Activate(string sandboxPath)
    {
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        return new(ActivateLayer(&info, sandboxPath));
    }

    public static HRESULT Prepare(string sandboxPath, IReadOnlyList<string> parentPaths)
    {
        WcLayerDescriptor[] descriptors = BuildDescriptors(parentPaths, out HRESULT hr);
        if (hr.Failed)
        {
            return hr;
        }
        try
        {
            DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
            fixed (WcLayerDescriptor* pDescs = descriptors)
            {
                return new(PrepareLayer(&info, sandboxPath, pDescs, (uint)descriptors.Length));
            }
        }
        finally
        {
            FreeDescriptors(descriptors);
        }
    }

    public static HRESULT GetMountPath(string sandboxPath, out string volumePath)
    {
        volumePath = "";
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        nuint length = 0;
        int hr = GetLayerMountPath(&info, sandboxPath, &length, null);
        if (hr < 0 || length == 0)
        {
            return new(hr);
        }

        char[] buffer = new char[length];
        fixed (char* pBuffer = buffer)
        {
            hr = GetLayerMountPath(&info, sandboxPath, &length, pBuffer);
        }
        if (hr >= 0)
        {
            int end = Array.IndexOf(buffer, '\0');
            volumePath = new string(buffer, 0, end < 0 ? buffer.Length : end);
        }
        return new(hr);
    }

    public static HRESULT Unprepare(string sandboxPath)
    {
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        return new(UnprepareLayer(&info, sandboxPath));
    }

    public static HRESULT Deactivate(string sandboxPath)
    {
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        return new(DeactivateLayer(&info, sandboxPath));
    }

    public static HRESULT Destroy(string sandboxPath)
    {
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        return new(DestroyLayer(&info, sandboxPath));
    }

    /// <summary>Writes <paramref name="layerPath"/> out to <paramref name="exportFolderPath"/>
    /// in the layer transport format. Used by the #33 export step to get a layer
    /// out of Docker's Administrators-ACLed store. A plain recursive file copy is
    /// NOT equivalent: the transport format carries Win32 backup streams (security
    /// descriptors, EAs, hard links) that ordinary file I/O drops, which is why
    /// hcsshim never copies layer trees directly.</summary>
    public static HRESULT Export(string layerPath, string exportFolderPath, IReadOnlyList<string> parentPaths)
    {
        WcLayerDescriptor[] descriptors = BuildDescriptors(parentPaths, out HRESULT hr);
        if (hr.Failed)
        {
            return hr;
        }
        try
        {
            DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
            fixed (WcLayerDescriptor* pDescs = descriptors)
            {
                return new(ExportLayer(&info, layerPath, exportFolderPath, pDescs, (uint)descriptors.Length));
            }
        }
        finally
        {
            FreeDescriptors(descriptors);
        }
    }

    /// <summary>Reconstructs a layer at <paramref name="layerPath"/> from a folder
    /// previously written by <see cref="Export"/>. All parent layers must already
    /// be present at <paramref name="parentPaths"/> for the transport format to be
    /// interpretable (hcsshim internal/wclayer/importlayer.go).</summary>
    public static HRESULT Import(string layerPath, string importFolderPath, IReadOnlyList<string> parentPaths)
    {
        WcLayerDescriptor[] descriptors = BuildDescriptors(parentPaths, out HRESULT hr);
        if (hr.Failed)
        {
            return hr;
        }
        try
        {
            DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
            fixed (WcLayerDescriptor* pDescs = descriptors)
            {
                return new(ImportLayer(&info, layerPath, importFolderPath, pDescs, (uint)descriptors.Length));
            }
        }
        finally
        {
            FreeDescriptors(descriptors);
        }
    }

    /// <summary>Asks the layer driver whether a layer exists — unlike
    /// <see cref="File.Exists"/>, which reports false for a path the caller merely
    /// cannot read. That distinction is the whole confound this spike exists to
    /// remove, so the driver's own answer is worth recording separately.</summary>
    public static HRESULT Exists(string layerPath, out bool exists)
    {
        DriverInfo info = new() { Flavour = 1, HomeDir = EmptyHome };
        uint value = 0;
        int hr = LayerExists(&info, layerPath, &value);
        exists = value != 0;
        return new(hr);
    }

    /// <summary>Post-processes a base layer whose files have been extracted to
    /// <c>&lt;path&gt;\Files</c> — the finalize half of the OCI-tar import path
    /// (hcsshim ProcessBaseLayer → vmcompute!ProcessBaseImage).
    ///
    /// Unlike the other exports here, this one is called through a Find-style
    /// guard: hcsshim binds it as an OPTIONAL proc ("ProcessBaseImage?"), so a
    /// SKU without it must be recorded as a missing export rather than crash a
    /// harness whose whole job is to record outcomes.</summary>
    public static HRESULT ProcessBase(string layerPath) => Guarded(() => ProcessBaseImage(layerPath));

    /// <summary>Post-processes an extracted utility VM image at
    /// <c>&lt;layer&gt;\UtilityVM</c> (files under <c>UtilityVM\Files</c>).
    ///
    /// NOTE the export name: hcsshim's Go wrapper is ProcessUtilityVMImage but
    /// the vmcompute.dll export it binds is <c>ProcessUtilityImage</c> — no
    /// "VM". Getting that wrong yields a missing-export result that reads like
    /// an OS-support finding.</summary>
    public static HRESULT ProcessUtilityVm(string utilityVmPath) => Guarded(() => ProcessUtilityImage(utilityVmPath));

    /// <summary>Same contract as ComputeStorageApi's guard: an export this
    /// Windows build does not carry is a RECORDED outcome (0x8007007F,
    /// ERROR_PROC_NOT_FOUND), never an unhandled exception.</summary>
    private static HRESULT Guarded(Func<int> call)
    {
        const int ProcNotFound = unchecked((int)0x8007007F);
        try
        {
            return new HRESULT(call());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return new HRESULT(ProcNotFound);
        }
    }

    private static WcLayerDescriptor[] BuildDescriptors(IReadOnlyList<string> parentPaths, out HRESULT hr)
    {
        hr = default;
        var descriptors = new WcLayerDescriptor[parentPaths.Count];
        for (int i = 0; i < parentPaths.Count; i++)
        {
            hr = LayerId(parentPaths[i], out Guid guid);
            if (hr.Failed)
            {
                FreeDescriptors(descriptors);
                return [];
            }
            descriptors[i] = new WcLayerDescriptor
            {
                LayerId = guid,
                Flags = 0,
                Path = Marshal.StringToHGlobalUni(parentPaths[i]),
            };
        }
        return descriptors;
    }

    private static void FreeDescriptors(WcLayerDescriptor[] descriptors)
    {
        foreach (WcLayerDescriptor d in descriptors)
        {
            if (d.Path != 0)
            {
                Marshal.FreeHGlobal(d.Path);
            }
        }
    }

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NameToGuid(string name, out Guid guid);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateSandboxLayer(DriverInfo* info, string id, nint parentLayerHandle, WcLayerDescriptor* layers, uint layerCount);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateLayer(DriverInfo* info, string id);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PrepareLayer(DriverInfo* info, string id, WcLayerDescriptor* layers, uint layerCount);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetLayerMountPath(DriverInfo* info, string id, nuint* length, char* path);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int UnprepareLayer(DriverInfo* info, string id);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeactivateLayer(DriverInfo* info, string id);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DestroyLayer(DriverInfo* info, string id);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ExportLayer(DriverInfo* info, string id, string path, WcLayerDescriptor* layers, uint layerCount);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ImportLayer(DriverInfo* info, string id, string path, WcLayerDescriptor* layers, uint layerCount);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LayerExists(DriverInfo* info, string id, uint* exists);

    // The two finalize exports take a bare path and no DriverInfo — they operate
    // on an extracted tree, not on a registered layer.
    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ProcessBaseImage(string path);

    [LibraryImport("vmcompute.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ProcessUtilityImage(string path);
}
