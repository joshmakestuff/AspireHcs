using AspireHcs.Hcs;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Vhd;

namespace AspireHcs.Storage;

internal static class VirtualDisk
{
    /// <summary>
    /// Creates a differencing (copy-on-write) VHDX child of <paramref name="basePath"/>.
    /// Works without elevation (verified in issue #1).
    /// </summary>
    public static unsafe void CreateDifferencing(string basePath, string diffPath)
    {
        HcsPlatform.ThrowIfUnsupported();

        VIRTUAL_STORAGE_TYPE storageType = new()
        {
            DeviceId = PInvoke.VIRTUAL_STORAGE_TYPE_DEVICE_VHDX,
            VendorId = PInvoke.VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
        };

        fixed (char* pParent = basePath)
        {
            CREATE_VIRTUAL_DISK_PARAMETERS parameters = new()
            {
                Version = CREATE_VIRTUAL_DISK_VERSION.CREATE_VIRTUAL_DISK_VERSION_2,
            };
            parameters.Anonymous.Version2.ParentPath = pParent;

            WIN32_ERROR error = PInvoke.CreateVirtualDisk(
                in storageType,
                diffPath,
                VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_NONE,
                default,
                CREATE_VIRTUAL_DISK_FLAG.CREATE_VIRTUAL_DISK_FLAG_NONE,
                0,
                in parameters,
                null,
                out SafeFileHandle handle);

            if (error != WIN32_ERROR.NO_ERROR)
            {
                HRESULT hr = new(unchecked((int)(0x80070000u | (uint)error)));
                throw HcsException.Create("CreateVirtualDisk", hr, resultDocument: null);
            }

            handle.Dispose();
        }
    }
}
