// Win32 backup-stream plumbing for the base-layer import path (issue #30) — a
// C# port of go-winio's BackupFileWriter/BackupFileReader plus the two payload
// encoders the tar carries metadata in (REPARSE_DATA_BUFFER and
// FILE_FULL_EA_INFORMATION).
//
// A file's security descriptor, EAs, reparse data, contents and alternate data
// streams are all applied by ONE BackupWrite stream per file: WIN32_STREAM_ID
// records written back to back. Two wire facts the port must not get wrong
// (verified against go-winio backup.go):
//   - the record header is PACKED 20 bytes (u32 id, u32 attributes, u64 size,
//     u32 nameSize) + UTF-16 name — a marshalled struct pads to 24 and corrupts
//     the stream, so headers are serialized with BinaryPrimitives;
//   - the BackupWrite context must be freed by the abort call BEFORE the file
//     handle closes, and chunk boundaries are arbitrary (headers may split).

using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace HcsContainerSpike;

/// <summary>The single definition of the locally-judged-failure sentinel. It
/// was defined twice (here and in Program) before review caught it — one
/// semantic with nothing forcing the copies to agree.</summary>
internal static class SpikeHr
{
    /// <summary>E_FAIL, used for proof steps this code judges itself rather
    /// than receiving from a native call.</summary>
    public static readonly HRESULT ProbeFailed = new(unchecked((int)0x80004005));
}

/// <summary>The one UTF-16LE serializer for native buffers. Both the
/// WIN32_STREAM_ID name field and the reparse buffer's two name fields need it;
/// two hand-rolled loops would be two places for the same encoding bug.</summary>
internal static class Utf16
{
    public static int Write(Span<byte> destination, string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], value[i]);
        }
        return value.Length * 2;
    }

    public static int WriteWithNul(Span<byte> destination, string value)
    {
        int written = Write(destination, value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], 0);
        return written + 2;
    }
}

internal static class BackupStreamId
{
    public const uint Data = 1;
    public const uint EaData = 2;
    public const uint Security = 3;
    public const uint AlternateData = 4;
    public const uint ReparseData = 8;
    public const uint SparseBlock = 9;
}

/// <summary>Writes one file's backup stream through a persistent BackupWrite
/// context. Dispose (the abort call) before closing the file handle.</summary>
internal sealed unsafe class BackupStreamWriter(SafeFileHandle file, bool processSecurity) : IDisposable
{
    private void* _context;

    public HRESULT WriteHeader(uint streamId, uint attributes, ulong payloadSize, string? name = null)
    {
        int nameBytes = (name?.Length ?? 0) * 2;
        Span<byte> header = stackalloc byte[20 + nameBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], attributes);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)nameBytes);
        if (name is not null)
        {
            // No terminator — NameSize already said how many bytes.
            Utf16.Write(header[20..], name);
        }
        return Write(header);
    }

    public HRESULT Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            uint written;
            // The context lives in a field but its ADDRESS must be stack-based:
            // &field on a heap object is illegal without pinning, so it makes the
            // round trip through a local on every call.
            void* context = _context;
            bool ok;
            fixed (byte* p = data)
            {
                ok = PInvoke.BackupWrite(
                    new HANDLE(file.DangerousGetHandle()), p, (uint)data.Length, &written,
                    bAbort: false, bProcessSecurity: processSecurity, &context);
            }
            _context = context;
            if (!ok)
            {
                return NtFile.HrFromLastError();
            }
            if (written == 0)
            {
                return SpikeHr.ProbeFailed; // no progress — refuse to spin
            }
            data = data[(int)written..];
        }
        return default;
    }

    /// <summary>Streams exactly <paramref name="length"/> bytes from
    /// <paramref name="source"/> into the current record's payload.</summary>
    public HRESULT CopyFrom(Stream source, long length)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                return SpikeHr.ProbeFailed; // tar said `length` bytes and delivered fewer — truncated archive
            }
            HRESULT hr = Write(buffer.AsSpan(0, read));
            if (hr.Failed)
            {
                return hr;
            }
            remaining -= read;
        }
        return default;
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            uint written;
            void* context = _context;
            PInvoke.BackupWrite(new HANDLE(file.DangerousGetHandle()), null, 0, &written,
                bAbort: true, bProcessSecurity: false, &context);
            _context = null;
        }
    }
}

/// <summary>Pumps a file's raw backup stream out via BackupRead — the
/// self-test's ground truth (what Windows itself serializes for a file) and the
/// shape an exporter would consume.</summary>
internal sealed unsafe class BackupStreamReader(SafeFileHandle file, bool processSecurity) : IDisposable
{
    private void* _context;

    /// <summary>Reads the entire remaining backup stream into memory. Layer
    /// files run to a few hundred MB at most in tests; the import path never
    /// uses this (it writes, not reads).</summary>
    public HRESULT ReadAll(out byte[] data)
    {
        using var collected = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            uint read;
            void* context = _context;
            bool ok;
            fixed (byte* p = buffer)
            {
                ok = PInvoke.BackupRead(
                    new HANDLE(file.DangerousGetHandle()), p, (uint)buffer.Length, &read,
                    bAbort: false, bProcessSecurity: processSecurity, &context);
            }
            _context = context;
            if (!ok)
            {
                data = [];
                return NtFile.HrFromLastError();
            }
            if (read == 0)
            {
                data = collected.ToArray();
                return default;
            }
            collected.Write(buffer, 0, (int)read);
        }
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            uint read;
            void* context = _context;
            PInvoke.BackupRead(new HANDLE(file.DangerousGetHandle()), null, 0, &read,
                bAbort: true, bProcessSecurity: false, &context);
            _context = null;
        }
    }
}

internal static class ReparseBuffer
{
    public const uint MountPointTag = 0xA0000003;
    public const uint SymlinkTag = 0xA000000C;

    /// <summary>Port of go-winio EncodeReparsePoint: builds the
    /// REPARSE_DATA_BUFFER a BackupReparseData record carries. The substitute
    /// name is the NT form (<c>\??\…</c> for absolute targets), the print name
    /// is the original target; both are NUL-terminated in the buffer while the
    /// recorded lengths exclude the NUL. Symlinks append a 4-byte flags word
    /// (1 = relative); mount points (junctions) have no flags field.</summary>
    public static byte[] Encode(string target, bool isMountPoint)
    {
        string ntTarget;
        bool relative = false;
        if (target.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            ntTarget = @"\??\" + target[4..];
        }
        else if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            ntTarget = @"\??\UNC\" + target[2..];
        }
        else if (target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':')
        {
            ntTarget = @"\??\" + target;
        }
        else
        {
            ntTarget = target;
            relative = true;
        }

        int substituteBytes = (ntTarget.Length + 1) * 2; // includes NUL
        int printBytes = (target.Length + 1) * 2;
        int flagsBytes = isMountPoint ? 0 : 4;
        // ReparseDataLength covers everything after the 8-byte tag+length+reserved
        // header: the four USHORT name fields (8), optional flags, both names.
        int dataLength = 8 + flagsBytes + substituteBytes + printBytes;

        // Every length below is written as a USHORT. Bounded HERE, against the
        // limit the Windows consumer actually enforces, because an unchecked
        // cast would WRAP a long target into a small length and emit a
        // structurally valid but wrong record instead of failing.
        const int MaximumReparseDataBufferSize = 16 * 1024;
        if (8 + dataLength > MaximumReparseDataBufferSize || substituteBytes > ushort.MaxValue || printBytes > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"reparse target is too long to encode: {8 + dataLength} bytes exceeds MAXIMUM_REPARSE_DATA_BUFFER_SIZE ({MaximumReparseDataBufferSize})");
        }

        byte[] buffer = new byte[8 + dataLength];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span, isMountPoint ? MountPointTag : SymlinkTag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)dataLength);
        // [6..8) Reserved = 0
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);                              // SubstituteNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)(ntTarget.Length * 2)); // SubstituteNameLength (no NUL)
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)substituteBytes);       // PrintNameOffset (after subst + NUL)
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)(target.Length * 2));   // PrintNameLength (no NUL)

        int offset = 16;
        if (!isMountPoint)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], relative ? 1u : 0u);
            offset += 4;
        }
        offset += Utf16.WriteWithNul(span[offset..], ntTarget);
        Utf16.WriteWithNul(span[offset..], target);
        return buffer;
    }
}

internal static class ExtendedAttributes
{
    /// <summary>Port of go-winio EncodeExtendedAttributes: FILE_FULL_EA_INFORMATION
    /// records — u32 NextEntryOffset, u8 Flags (always 0; tar cannot carry EA
    /// flags), u8 NameLength, u16 ValueLength, ASCII name, NUL, value, padded to
    /// a 4-byte boundary; the last record's NextEntryOffset is 0.</summary>
    public static byte[] Encode(IReadOnlyList<(string Name, byte[] Value)> eas)
    {
        var buffer = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int i = 0; i < eas.Count; i++)
        {
            (string name, byte[] value) = eas[i];
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            if (nameBytes.Length > byte.MaxValue || value.Length > ushort.MaxValue)
            {
                throw new InvalidOperationException($"EA '{name}' exceeds FILE_FULL_EA_INFORMATION field limits");
            }
            int entrySize = 8 + nameBytes.Length + 1 + value.Length;
            int padded = (entrySize + 3) & ~3;
            bool last = i == eas.Count - 1;

            BinaryPrimitives.WriteUInt32LittleEndian(header, last ? 0u : (uint)padded);
            header[4] = 0; // Flags
            header[5] = (byte)nameBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)value.Length);
            buffer.Write(header);
            buffer.Write(nameBytes);
            buffer.WriteByte(0);
            buffer.Write(value);
            for (int pad = entrySize; pad < padded; pad++)
            {
                buffer.WriteByte(0);
            }
        }
        return buffer.ToArray();
    }
}
