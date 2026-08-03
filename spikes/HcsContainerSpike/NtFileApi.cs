// NT-native relative file operations for the base-layer import path (issue
// #30) — a C# port of hcsshim internal/safefile (v0.14.1). Every path from the
// layer tar is opened RELATIVE to a root directory handle with OBJ_DONT_REPARSE,
// so a hostile or corrupt tar cannot escape the layer directory through a
// symlink it just planted; CreateFileW cannot express a RootDirectory-relative
// open, hence ntdll. The sanitizer is stricter than hcsshim's in one deliberate
// way: `..` segments are rejected outright instead of leaning on the relative
// open to contain them.
//
// These exports are not in the Win32 metadata (CsWin32 cannot generate ntdll
// file APIs), so the bindings are hand-written like WcLayerApi.cs.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace HcsContainerSpike;

internal static unsafe partial class NtFile
{
    // Access rights (winnt.h).
    public const uint GenericRead = 0x8000_0000;
    public const uint GenericWrite = 0x4000_0000;
    public const uint Synchronize = 0x0010_0000;
    public const uint WriteDac = 0x0004_0000;
    public const uint WriteOwner = 0x0008_0000;
    public const uint AccessSystemSecurity = 0x0100_0000;
    public const uint FileWriteAttributes = 0x0000_0100;

    // Share modes.
    public const uint ShareRead = 0x1;
    public const uint ShareAll = 0x7; // read | write | delete

    // NT create dispositions (not the CreateFileW values).
    public const uint FileOpen = 1;
    public const uint FileCreate = 2;

    // NT create options.
    public const uint FileDirectoryFile = 0x0000_0001;
    public const uint FileSynchronousIoNonalert = 0x0000_0020;
    public const uint FileOpenForBackupIntent = 0x0000_4000;
    public const uint FileOpenReparsePoint = 0x0020_0000;

    private const uint ObjDontReparse = 0x0000_1000;
    private const uint FileLinkInformationClass = 11;
    private const int MaxNtPathChars = 32767;
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;        // bytes, excluding any terminator
        public ushort MaximumLength; // bytes
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public nint Status;
        public nuint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(
        out nint fileHandle, uint desiredAccess, in OBJECT_ATTRIBUTES objectAttributes, out IO_STATUS_BLOCK ioStatusBlock,
        nint allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        nint eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationFile(
        nint fileHandle, out IO_STATUS_BLOCK ioStatusBlock, void* fileInformation, uint length, uint fileInformationClass);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    /// <summary>Opens the layer root by absolute path with backup semantics —
    /// the anchor handle every tar entry is opened relative to.</summary>
    public static HRESULT OpenRoot(string path, out SafeFileHandle root)
    {
        root = PInvoke.CreateFile(
            ToLongPath(Path.GetFullPath(path)),
            GenericRead,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            null);
        return root.IsInvalid ? HrFromLastError() : default;
    }

    /// <summary>Validates and normalizes a tar entry name into an NT relative
    /// path. Throws on anything that could address outside the root: ':'
    /// (an alternate data stream arriving out of order — ADS are consumed by the
    /// entry loop, never opened by name), absolute paths, and '..' segments.</summary>
    public static string CleanRelativePath(string name)
    {
        if (name.Contains(':'))
        {
            throw new InvalidOperationException(
                $"tar entry '{name}' contains ':' — an alternate data stream out of order, or a hostile name");
        }
        if (name.StartsWith('/') || name.StartsWith('\\'))
        {
            throw new InvalidOperationException($"tar entry '{name}' is absolute — layer entries must be relative");
        }
        // Both separators: NT treats '\' as one regardless of what the tar meant,
        // so validating only '/' would let 'a\..\..' through as a "file name".
        var segments = new List<string>();
        foreach (string segment in name.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                throw new InvalidOperationException($"tar entry '{name}' contains a '..' segment");
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
        {
            throw new InvalidOperationException($"tar entry '{name}' is empty after normalization");
        }
        string clean = string.Join('\\', segments);
        return clean.Length <= MaxNtPathChars
            ? clean
            : throw new InvalidOperationException($"tar entry path is {clean.Length} chars — over the NT limit");
    }

    /// <summary>NtCreateFile relative to <paramref name="root"/>. Mirrors
    /// hcsshim safefile.OpenRelative exactly: OBJ_DONT_REPARSE, backup intent +
    /// synchronous IO always OR'd into the options, SYNCHRONIZE into the access
    /// mask. <paramref name="relativePath"/> must already be cleaned.</summary>
    public static HRESULT OpenRelative(
        string relativePath, SafeFileHandle root, uint access, uint share, uint disposition, uint options,
        out SafeFileHandle handle)
    {
        handle = new SafeFileHandle();
        if (relativePath.Length > MaxNtPathChars)
        {
            return new HRESULT(unchecked((int)0x800700CE)); // HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE)
        }
        fixed (char* path = relativePath)
        {
            var name = new UNICODE_STRING
            {
                Length = (ushort)(relativePath.Length * 2),
                MaximumLength = (ushort)(relativePath.Length * 2),
                Buffer = (nint)path,
            };
            var attributes = new OBJECT_ATTRIBUTES
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = root.DangerousGetHandle(),
                ObjectName = (nint)(&name),
                Attributes = ObjDontReparse,
            };
            int status = NtCreateFile(
                out nint raw, access | Synchronize, in attributes, out _,
                allocationSize: 0, fileAttributes: 0, share, disposition,
                FileOpenForBackupIntent | FileSynchronousIoNonalert | options,
                eaBuffer: 0, eaLength: 0);
            if (status < 0)
            {
                return HrFromNtStatus(status);
            }
            handle = new SafeFileHandle(raw, ownsHandle: true);
            return default;
        }
    }

    /// <summary>Hard link, both ends relative to the root — hcsshim
    /// safefile.LinkRelative: open the source with FILE_WRITE_ATTRIBUTES, open
    /// the new link's parent directory, refuse a reparse-point parent, then
    /// NtSetInformationFile(FileLinkInformation) with RootDirectory = the parent
    /// handle and the base name only.</summary>
    public static HRESULT LinkRelative(string existingRelative, string newRelative, SafeFileHandle root, out string failedStep)
    {
        failedStep = "";
        HRESULT hr = OpenRelative(existingRelative, root, FileWriteAttributes, ShareAll, FileOpen, 0, out SafeFileHandle source);
        if (hr.Failed)
        {
            failedStep = $"open link target '{existingRelative}'";
            return hr;
        }
        using (source)
        {
            int slash = newRelative.LastIndexOf('\\');
            SafeFileHandle parent = root;
            bool ownsParent = false;
            if (slash >= 0)
            {
                hr = OpenRelative(newRelative[..slash], root, GenericRead, ShareAll, FileOpen, FileDirectoryFile, out parent);
                if (hr.Failed)
                {
                    failedStep = $"open parent '{newRelative[..slash]}'";
                    return hr;
                }
                ownsParent = true;
            }
            try
            {
                hr = GetBasicInfo(parent, out FILE_BASIC_INFO parentInfo);
                if (hr.Failed)
                {
                    failedStep = "read parent attributes";
                    return hr;
                }
                if ((parentInfo.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    failedStep = "parent is a reparse point";
                    return HrFromNtStatus(StatusReparsePointEncountered); // same code hcsshim reports here
                }

                string baseName = slash < 0 ? newRelative : newRelative[(slash + 1)..];
                int nameBytes = baseName.Length * 2;
                int total = 20 + nameBytes; // FILE_LINK_INFORMATION: 1(+7 pad) + 8 RootDirectory + 4 FileNameLength, then name
                ulong* raw = stackalloc ulong[(total + 7) / 8];
                byte* buffer = (byte*)raw;
                new Span<byte>(buffer, total).Clear();
                *(nint*)(buffer + 8) = parent.DangerousGetHandle();
                *(uint*)(buffer + 16) = (uint)nameBytes;
                baseName.AsSpan().CopyTo(new Span<char>(buffer + 20, baseName.Length));

                int status = NtSetInformationFile(
                    source.DangerousGetHandle(), out _, buffer, (uint)total, FileLinkInformationClass);
                if (status < 0)
                {
                    failedStep = "NtSetInformationFile(FileLinkInformation)";
                    return HrFromNtStatus(status);
                }
                return default;
            }
            finally
            {
                if (ownsParent)
                {
                    parent.Dispose();
                }
            }
        }
    }

    /// <summary>Opens with OBJ_DONT_REPARSE and WITHOUT FILE_OPEN_REPARSE_POINT:
    /// succeeds only if the leaf is not a reparse point. Guards the path handed
    /// to ProcessUtilityImage (hcsshim EnsureNotReparsePointRelative).</summary>
    public static HRESULT EnsureNotReparsePoint(string relativePath, SafeFileHandle root)
    {
        HRESULT hr = OpenRelative(relativePath, root, access: 0, ShareAll, FileOpen, 0, out SafeFileHandle handle);
        if (hr.Succeeded)
        {
            handle.Dispose();
        }
        return hr;
    }

    public static HRESULT SetBasicInfo(SafeFileHandle file, in FILE_BASIC_INFO info)
    {
        fixed (FILE_BASIC_INFO* p = &info)
        {
            return PInvoke.SetFileInformationByHandle(
                new HANDLE(file.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, p, (uint)sizeof(FILE_BASIC_INFO))
                ? default
                : HrFromLastError();
        }
    }

    public static HRESULT GetBasicInfo(SafeFileHandle file, out FILE_BASIC_INFO info)
    {
        fixed (FILE_BASIC_INFO* p = &info)
        {
            return PInvoke.GetFileInformationByHandleEx(
                new HANDLE(file.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, p, (uint)sizeof(FILE_BASIC_INFO))
                ? default
                : HrFromLastError();
        }
    }

    public static HRESULT HrFromNtStatus(int status)
    {
        uint win32 = RtlNtStatusToDosError(status);
        return new HRESULT(unchecked((int)(0x80070000u | (win32 & 0xFFFF))));
    }

    public static HRESULT HrFromLastError() =>
        new(unchecked((int)0x80070000) | (Marshal.GetLastPInvokeError() & 0xFFFF));

    private static string ToLongPath(string absolute) =>
        absolute.StartsWith(@"\\?\", StringComparison.Ordinal) ? absolute
        : absolute.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + absolute[2..]
        : @"\\?\" + absolute;
}
