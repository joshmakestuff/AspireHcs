// `import` / `finalize` (issue #30): turn a pulled OCI layer tar into a
// windowsfilter-format base layer directory in a store AspireHcs owns.
//
// This is a C# port of hcsshim v0.14.1's base-layer path — pkg/ociwclayer
// writeLayerFromTar + internal/wclayer baselayerwriter.go + go-winio backuptar.
// Base layers cannot travel through ExportLayer/ImportLayer (measured:
// 0x80070057; hcsshim branches to a backup-stream reader when there are no
// parents), so the supported route is exactly this: replay the tar as Win32
// backup streams and let vmcompute finalize the tree.
//
// Extraction and finalization are separable ON PURPOSE (--skip-finalize plus a
// standalone `finalize`): they are two different privilege questions, and a
// single combined step would report one gate's result for both.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace HcsContainerSpike;

internal static partial class Program
{
    /// <summary>Written LAST, after finalize, as the completion sentinel. An
    /// entry without it is a torn import — the directory is created before the
    /// first file lands, so existence alone proves nothing.</summary>
    private const string ProvenanceFileName = "aspirehcs-acquisition.json";

    private static int Import(string[] args)
    {
        string metadataPath = Path.GetFullPath(Opt(args, "--metadata")
            ?? throw new ArgumentException("--metadata <image metadata json from pull> is required"));
        bool noSecurity = args.Contains("--no-security");
        bool skipFinalize = args.Contains("--skip-finalize");

        JsonNode metadata = ReadImageMetadata(metadataPath, out string blobPath);
        string store = Path.GetDirectoryName(Path.GetDirectoryName(metadataPath))!;
        string expectedDiffId = (string?)metadata["expectedDiffId"]
            ?? throw new InvalidOperationException($"{metadataPath} has no expectedDiffId");
        string entryPath = Path.Combine(store, OciDigest.RequireSha256(expectedDiffId));

        Console.WriteLine($"[import] image={(string?)metadata["image"]} osVersion={(string?)metadata["osVersion"]}");
        Console.WriteLine($"[import] blob={blobPath}");
        Console.WriteLine($"[import] entry={entryPath}");
        Console.WriteLine($"[import] security={(noSecurity ? "SKIPPED (--no-security: no SDs restored, no privileges taken)" : "full fidelity")}" +
                          $" finalize={(skipFinalize ? "SKIPPED (--skip-finalize)" : "in-line")}");

        // Privileges: hcsshim's NewLayerWriter contract. In --no-security mode
        // they are deliberately not requested — that mode exists to measure
        // whether extraction without them is possible at all.
        if (!noSecurity)
        {
            HRESULT privHr = TokenPrivileges.EnableBackupRestorePrivileges(out string privDetail);
            Step("EnableBackupRestorePrivileges", privHr, privDetail);
            if (privHr.Failed)
            {
                Console.WriteLine("Import needs SeBackupPrivilege + SeRestorePrivilege to restore security descriptors and " +
                                  "open with backup intent. Rerun elevated, or use --no-security to measure what an " +
                                  "unprivileged extraction can do.");
                return 2;
            }
        }

        // A leftover entry is only reusable if it carries the completion
        // sentinel; anything else is torn and gets destroyed rather than
        // silently extended (FILE_CREATE would fail on the first duplicate and
        // report a confusing collision).
        if (Directory.Exists(entryPath))
        {
            if (File.Exists(Path.Combine(entryPath, ProvenanceFileName)))
            {
                Step("EntryAlreadyComplete", default, $"{entryPath} carries {ProvenanceFileName} — nothing to do");
                Console.WriteLine($"Already imported. Delete the directory to force a re-import.");
                return 0;
            }
            Step("DestroyTornEntry", DestroyEntry(entryPath), entryPath);
        }

        bool completed = false;
        try
        {
            HRESULT hr = ExtractLayer(blobPath, entryPath, expectedDiffId, noSecurity, out bool hasUtilityVm);
            if (hr.Failed)
            {
                return 2;
            }

            if (!skipFinalize)
            {
                hr = FinalizeEntry(entryPath, hasUtilityVm);
                if (hr.Failed)
                {
                    return 2;
                }
                WriteCompletionRecords(entryPath, metadata, noSecurity);
                completed = true;
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"Extracted WITHOUT finalizing. The entry is not bootable yet — no layerchain.json, " +
                                  $"no blank-base.vhdx, no SystemTemplate.vhdx. Finalize it with:");
                Console.WriteLine($"  HcsContainerSpike finalize --entry {entryPath}");
                completed = true; // extraction succeeded; the caller asked to stop here
            }
            return Results.Any(r => r.Hr.Failed) ? 2 : 0;
        }
        finally
        {
            if (!completed && Directory.Exists(entryPath))
            {
                // Partial trees carry restored DACLs that defeat Directory.Delete,
                // so the layer driver removes them (it is what DestroyLayer is for).
                Step("CleanupPartialEntry", DestroyEntry(entryPath), entryPath);
            }
        }
    }

    private static int Finalize(string[] args)
    {
        string entryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Opt(args, "--entry")
            ?? throw new ArgumentException("--entry <store layer dir> is required")));
        if (!Directory.Exists(entryPath))
        {
            throw new ArgumentException($"--entry {entryPath} does not exist");
        }

        Console.WriteLine($"[finalize] entry={entryPath}");
        HRESULT privHr = TokenPrivileges.EnableBackupRestorePrivileges(out string privDetail);
        // MEASURED, not required: whether finalize needs these privileges is one
        // of the open questions this command exists to answer, so a failure here
        // must not stop the attempt.
        Console.WriteLine($"[finalize] privileges: {privDetail}");

        bool hasUtilityVm = Directory.Exists(Path.Combine(entryPath, "UtilityVM", "Files"));
        HRESULT hr = FinalizeEntry(entryPath, hasUtilityVm);
        if (hr.Failed)
        {
            return 2;
        }

        string metadataPath = Path.Combine(entryPath, ProvenanceFileName);
        if (!File.Exists(metadataPath))
        {
            // A standalone finalize completes an entry an earlier --skip-finalize
            // left; the provenance it can write is thinner (no image metadata in
            // hand) and says so.
            WriteCompletionRecords(entryPath, new JsonObject { ["image"] = "(unknown — finalized standalone)" },
                noSecurity: privHr.Failed);
        }
        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }

    // ------------------------------------------------------------ extraction --

    private static HRESULT ExtractLayer(
        string blobPath, string entryPath, string expectedDiffId, bool noSecurity, out bool hasUtilityVm)
    {
        hasUtilityVm = false;
        Directory.CreateDirectory(entryPath);

        HRESULT hr = NtFile.OpenRoot(entryPath, out SafeFileHandle root);
        Step("OpenLayerRoot", hr, entryPath);
        if (hr.Failed)
        {
            return hr;
        }

        // Directory times are reapplied at the end, children first: creating a
        // child mutates its parent's timestamps, so the parent must be restamped
        // after all of its descendants exist.
        var directoryTimes = new List<(string Path, FILE_BASIC_INFO Info)>();
        long entryCount = 0;
        long fileBytes = 0;

        using (root)
        {
            using FileStream compressed = File.OpenRead(blobPath);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            // The diffID is the hash of the WHOLE uncompressed tar, including the
            // end-of-archive zero blocks TarReader stops before — hence the
            // explicit drain after the loop.
            using var sha = SHA256.Create();
            using var hashing = new CryptoStream(gzip, sha, CryptoStreamMode.Read, leaveOpen: true);
            using var reader = new TarReader(hashing, leaveOpen: true);

            try
            {
                TarEntry? entry = reader.GetNextEntry(copyData: false);
                while (entry is not null)
                {
                    entryCount++;
                    // ProcessEntry returns the NEXT unconsumed header, because the
                    // ADS lookahead has to read ahead to know where a file's
                    // streams end. Calling GetNextEntry here as well would
                    // double-advance and silently drop every entry after an ADS.
                    entry = ProcessEntry(reader, entry, root, noSecurity, directoryTimes, ref hasUtilityVm, ref fileBytes);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
            {
                Step("ExtractEntries", MapManagedFailure(ex), $"after {entryCount} entries: {ex.Message}");
                return MapManagedFailure(ex);
            }

            // Drain the end-of-archive blocks so the diffID covers the whole
            // stream. Reading a CryptoStream to EOF finalizes the hash itself —
            // calling FlushFinalBlock as well throws "called twice".
            hashing.CopyTo(Stream.Null);
            string actualDiffId = "sha256:" + Convert.ToHexStringLower(
                sha.Hash ?? throw new InvalidOperationException("hash unavailable after draining the stream"));
            bool diffIdMatch = string.Equals(actualDiffId, expectedDiffId, StringComparison.Ordinal);
            Step("VerifyDiffId", diffIdMatch ? default : ProbeFailed,
                diffIdMatch ? $"{actualDiffId} matches the image config" : $"expected {expectedDiffId}, got {actualDiffId}");
            if (!diffIdMatch)
            {
                return ProbeFailed;
            }
            Step("ExtractEntries", default, $"{entryCount} entries, {fileBytes / (1024 * 1024)} MB of file content");

            hr = ReapplyDirectoryTimes(root, directoryTimes);
            Step("ReapplyDirectoryTimes", hr, $"{directoryTimes.Count} directories, children first");
            if (hr.Failed)
            {
                return hr;
            }
        }
        return default;
    }

    /// <summary>Writes one tar entry and returns the next UNCONSUMED header.
    /// Mirrors hcsshim writeLayerFromTar + backuptar.WriteBackupStreamFromTarFile.</summary>
    private static TarEntry? ProcessEntry(
        TarReader reader, TarEntry entry, SafeFileHandle root, bool noSecurity,
        List<(string Path, FILE_BASIC_INFO Info)> directoryTimes, ref bool hasUtilityVm, ref long fileBytes)
    {
        string rawName = entry.Name;
        if (Path.GetFileName(rawName.Replace('\\', '/')).StartsWith(".wh.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"base layer cannot have tombstones, found whiteout '{rawName}'");
        }
        if (rawName.Replace('\\', '/').Equals("UtilityVM/Files", StringComparison.OrdinalIgnoreCase))
        {
            hasUtilityVm = true;
        }

        if (entry.EntryType == TarEntryType.HardLink)
        {
            string linkPath = NtFile.CleanRelativePath(rawName);
            string targetPath = NtFile.CleanRelativePath(entry.LinkName);
            HRESULT linkHr = NtFile.LinkRelative(targetPath, linkPath, root, out string failedStep);
            if (linkHr.Failed)
            {
                throw new InvalidOperationException($"hard link '{rawName}' -> '{entry.LinkName}' failed at {failedStep}: 0x{(uint)linkHr.Value:X8}");
            }
            return reader.GetNextEntry(copyData: false);
        }

        IReadOnlyDictionary<string, string> pax =
            entry is PaxTarEntry paxEntry ? paxEntry.ExtendedAttributes : new Dictionary<string, string>();
        FILE_BASIC_INFO info = BuildBasicInfo(entry, pax);
        bool isDirectory = (info.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY) != 0;
        string relative = NtFile.CleanRelativePath(rawName);

        uint access = NtFile.GenericRead | NtFile.GenericWrite;
        if (!noSecurity)
        {
            // WRITE_DAC/WRITE_OWNER/ACCESS_SYSTEM_SECURITY are what let
            // BackupWrite restore the DACL, owner and SACL. Backup intent plus
            // SeBackup/SeRestore covers ACCESS_SYSTEM_SECURITY — SeSecurityPrivilege
            // is genuinely not required (hcsshim takes only the two).
            access |= NtFile.WriteDac | NtFile.WriteOwner | NtFile.AccessSystemSecurity;
        }
        HRESULT hr = NtFile.OpenRelative(
            relative, root, access, NtFile.ShareRead, NtFile.FileCreate,
            isDirectory ? NtFile.FileDirectoryFile : 0, out SafeFileHandle file);
        if (hr.Failed)
        {
            throw new InvalidOperationException($"create '{rawName}' failed: 0x{(uint)hr.Value:X8}");
        }

        using (file)
        {
            hr = NtFile.SetBasicInfo(file, in info);
            if (hr.Failed)
            {
                throw new InvalidOperationException($"SetFileBasicInfo '{rawName}' failed: 0x{(uint)hr.Value:X8}");
            }
            if (isDirectory)
            {
                directoryTimes.Add((relative, info));
            }

            using var writer = new BackupStreamWriter(file, processSecurity: !noSecurity);

            // Record order is fixed (go-winio WriteBackupStreamFromTarFile):
            // security, EAs, reparse data, file data, then alternate streams.
            if (!noSecurity && pax.TryGetValue("MSWINDOWS.rawsd", out string? rawSd))
            {
                byte[] sd = Convert.FromBase64String(rawSd);
                Throw(writer.WriteHeader(BackupStreamId.Security, 0, (ulong)sd.Length), rawName, "security header");
                Throw(writer.Write(sd), rawName, "security payload");
            }

            List<(string Name, byte[] Value)> eas = [];
            foreach ((string key, string value) in pax)
            {
                if (key.StartsWith("MSWINDOWS.xattr.", StringComparison.Ordinal))
                {
                    eas.Add((key["MSWINDOWS.xattr.".Length..], Convert.FromBase64String(value)));
                }
            }
            if (eas.Count > 0)
            {
                byte[] buffer = ExtendedAttributes.Encode(eas);
                Throw(writer.WriteHeader(BackupStreamId.EaData, 0, (ulong)buffer.Length), rawName, "EA header");
                Throw(writer.Write(buffer), rawName, "EA payload");
            }

            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                // Junction vs symlink is decided by the PRESENCE of the
                // mountpoint key, not its value (go-winio tar.go:170).
                byte[] reparse = ReparseBuffer.Encode(
                    entry.LinkName.Replace('/', '\\'), isMountPoint: pax.ContainsKey("MSWINDOWS.mountpoint"));
                Throw(writer.WriteHeader(BackupStreamId.ReparseData, 0, (ulong)reparse.Length), rawName, "reparse header");
                Throw(writer.Write(reparse), rawName, "reparse payload");
            }

            if (entry.EntryType == TarEntryType.RegularFile)
            {
                // Written for EVERY regular file, including zero-length ones.
                long length = entry.Length;
                Throw(writer.WriteHeader(BackupStreamId.Data, 0, (ulong)length), rawName, "data header");
                if (length > 0)
                {
                    Stream? data = entry.DataStream
                        ?? throw new InvalidOperationException($"'{rawName}' declares {length} bytes but exposes no data stream");
                    Throw(writer.CopyFrom(data, length), rawName, "data payload");
                    fileBytes += length;
                }
            }

            // Alternate data streams follow their parent as separate entries
            // named "<parent>:<stream>". Reading ahead is why this method owns
            // the reader's position and returns the next header.
            TarEntry? next = reader.GetNextEntry(copyData: false);
            while (next is { EntryType: TarEntryType.RegularFile }
                   && next.Name.StartsWith(rawName + ":", StringComparison.Ordinal))
            {
                string streamName = next.Name[rawName.Length..] + ":$DATA"; // keeps the leading colon
                Throw(writer.WriteHeader(BackupStreamId.AlternateData, 0, (ulong)next.Length, streamName),
                    rawName, $"ADS header {streamName}");
                if (next.Length > 0)
                {
                    Stream? data = next.DataStream
                        ?? throw new InvalidOperationException($"ADS '{next.Name}' exposes no data stream");
                    Throw(writer.CopyFrom(data, next.Length), rawName, $"ADS payload {streamName}");
                }
                next = reader.GetNextEntry(copyData: false);
            }
            return next;
        }
    }

    private static void Throw(HRESULT hr, string name, string what)
    {
        if (hr.Failed)
        {
            throw new InvalidOperationException($"{what} for '{name}' failed: 0x{(uint)hr.Value:X8}");
        }
    }

    /// <summary>Maps a tar header to FILE_BASIC_INFO. Absent times stay ZERO,
    /// which Windows reads as "do not change" — the real nanoserver tars carry
    /// no ctime record at all and omit mtime on some entries, so fabricating a
    /// value would be inventing metadata the image never had.</summary>
    private static FILE_BASIC_INFO BuildBasicInfo(TarEntry entry, IReadOnlyDictionary<string, string> pax)
    {
        var info = new FILE_BASIC_INFO
        {
            LastWriteTime = pax.ContainsKey("mtime") || entry.ModificationTime != default
                ? entry.ModificationTime.UtcDateTime.ToFileTimeUtc()
                : 0,
            LastAccessTime = ParsePaxTime(pax, "atime"),
            ChangeTime = ParsePaxTime(pax, "ctime"),
            CreationTime = ParsePaxTime(pax, "LIBARCHIVE.creationtime"),
        };
        if (pax.TryGetValue("MSWINDOWS.fileattr", out string? attr) && uint.TryParse(attr, out uint attributes))
        {
            info.FileAttributes = attributes;
        }
        else if (entry.EntryType == TarEntryType.Directory)
        {
            info.FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY;
        }
        if (info.CreationTime == 0)
        {
            info.CreationTime = info.LastWriteTime;
        }
        return info;
    }

    /// <summary>Parses a PAX "seconds.nanoseconds" timestamp to a FILETIME.
    /// Ported from go-winio's parsePAXTime rather than using double parsing:
    /// FILETIME is 100 ns and a double loses that precision outright.</summary>
    private static long ParsePaxTime(IReadOnlyDictionary<string, string> pax, string key)
    {
        if (!pax.TryGetValue(key, out string? raw) || raw.Length == 0)
        {
            return 0;
        }
        string secondsText = raw;
        long ticksFromFraction = 0;
        int dot = raw.IndexOf('.');
        if (dot >= 0)
        {
            secondsText = raw[..dot];
            string fraction = raw[(dot + 1)..].PadRight(9, '0')[..9];
            if (long.TryParse(fraction, out long nanoseconds))
            {
                ticksFromFraction = nanoseconds / 100; // 100 ns per tick
            }
        }
        if (!long.TryParse(secondsText, out long seconds))
        {
            return 0;
        }
        // FILETIME epoch is 1601-01-01; Unix epoch is 11644473600 seconds later.
        long ticks = (seconds + 11644473600L) * 10_000_000L;
        if (secondsText.StartsWith('-'))
        {
            ticksFromFraction = -ticksFromFraction;
        }
        long total = ticks + ticksFromFraction;
        return total > 0 ? total : 0;
    }

    private static HRESULT ReapplyDirectoryTimes(SafeFileHandle root, List<(string Path, FILE_BASIC_INFO Info)> directories)
    {
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            (string path, FILE_BASIC_INFO info) = directories[i];
            // FILE_OPEN_REPARSE_POINT matters here: a junction directory cannot
            // be reopened under OBJ_DONT_REPARSE without it.
            HRESULT hr = NtFile.OpenRelative(
                path, root, NtFile.GenericRead | NtFile.GenericWrite, NtFile.ShareRead, NtFile.FileOpen,
                NtFile.FileDirectoryFile | NtFile.FileOpenReparsePoint, out SafeFileHandle dir);
            if (hr.Failed)
            {
                return hr;
            }
            using (dir)
            {
                hr = NtFile.SetBasicInfo(dir, in info);
                if (hr.Failed)
                {
                    return hr;
                }
            }
        }
        return default;
    }

    // ---------------------------------------------------------- finalization --

    private static HRESULT FinalizeEntry(string entryPath, bool hasUtilityVm)
    {
        HRESULT hr = WcLayer.ProcessBase(entryPath);
        Step("ProcessBaseImage", hr, entryPath);
        if (hr.Failed)
        {
            return hr;
        }

        if (hasUtilityVm)
        {
            // Guard the path before handing it to vmcompute: if UtilityVM were a
            // reparse point, ProcessUtilityImage would follow it out of the layer.
            HRESULT guardHr = NtFile.OpenRoot(entryPath, out SafeFileHandle root);
            if (guardHr.Succeeded)
            {
                using (root)
                {
                    guardHr = NtFile.EnsureNotReparsePoint("UtilityVM", root);
                }
            }
            Step("EnsureUtilityVmNotReparsePoint", guardHr, "UtilityVM");
            if (guardHr.Failed)
            {
                return guardHr;
            }

            hr = WcLayer.ProcessUtilityVm(Path.Combine(entryPath, "UtilityVM"));
            Step("ProcessUtilityImage", hr, Path.Combine(entryPath, "UtilityVM"));
            if (hr.Failed)
            {
                return hr;
            }
        }

        // What these two calls PRODUCE is asserted, not assumed: hcsshim never
        // states the mapping, and a finalize that silently produced nothing
        // would otherwise surface much later as an unexplained boot failure.
        string? scratchTemplate = FindScratchTemplate(entryPath, out string searched, out _);
        Step("FinalizeProducedScratchTemplate", scratchTemplate is not null ? default : ProbeFailed,
            scratchTemplate ?? $"none of the expected blank VHDX names appeared: {searched}");

        if (hasUtilityVm)
        {
            string systemTemplate = Path.Combine(entryPath, "UtilityVM", "SystemTemplate.vhdx");
            bool present = File.Exists(systemTemplate);
            Step("FinalizeProducedSystemTemplate", present ? default : ProbeFailed,
                present ? $"{systemTemplate} ({new FileInfo(systemTemplate).Length / (1024 * 1024)} MB)" : $"{systemTemplate} absent");
        }
        return Results.Any(r => r.Hr.Failed) ? ProbeFailed : default;
    }

    private static void WriteCompletionRecords(string entryPath, JsonNode metadata, bool noSecurity)
    {
        // moby's encoding for "base layer, no parents" — the spike's own
        // ReadParentChain already treats JSON null as authoritative.
        File.WriteAllText(Path.Combine(entryPath, "layerchain.json"), "null");

        var provenance = new JsonObject
        {
            ["image"] = (string?)metadata["image"],
            ["layerDigest"] = (string?)metadata["layerDigest"],
            ["diffId"] = (string?)metadata["expectedDiffId"],
            ["osVersion"] = (string?)metadata["osVersion"],
            ["importedUtc"] = DateTime.UtcNow.ToString("o"),
            ["importedElevated"] = IsElevated(),
            ["securityDescriptorsRestored"] = !noSecurity,
            ["hostOs"] = Environment.OSVersion.VersionString,
            ["spikeCommit"] = TryGitCommit(),
        };
        File.WriteAllText(Path.Combine(entryPath, ProvenanceFileName),
            provenance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Step("WriteCompletionRecords", default, $"layerchain.json + {ProvenanceFileName}");
    }

    private static string TryGitCommit()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return "(unknown)";
            }
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return output.Length > 0 ? output : "(unknown)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "(unknown)";
        }
    }

    /// <summary>Removes a store entry. The layer driver goes first because it
    /// handles trees ordinary file I/O cannot: layer content carries
    /// FILE_ATTRIBUTE_READONLY (observed — Directory.Delete throws on those) and,
    /// after a full-fidelity import, restored DACLs that name only
    /// SYSTEM/TrustedInstaller. The managed fallback therefore clears attributes
    /// as it descends rather than assuming a plain delete can work.</summary>
    private static HRESULT DestroyEntry(string path)
    {
        HRESULT hr = WcLayer.Destroy(path);
        if (hr.Succeeded && !Directory.Exists(path))
        {
            return hr;
        }
        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Report BOTH attempts: "DestroyLayer said X and the fallback said Y"
            // is the datum; either alone invites the wrong conclusion.
            Console.WriteLine($"[cleanup] DestroyLayer=0x{(uint)hr.Value:X8}, managed delete: {ex.GetType().Name}: {ex.Message}");
            return hr.Failed ? hr : MapManagedFailure(ex);
        }
    }

    /// <summary>Clears ReadOnly on every entry, DIRECTORIES INCLUDED — a
    /// read-only directory cannot be removed (RemoveDirectory returns
    /// ERROR_ACCESS_DENIED), and layer trees are full of them: measured on
    /// nanoserver, Files\Users\Default arrives ReadOnly+Hidden and defeated an
    /// otherwise-permitted delete despite being empty and owned by the caller.</summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                     .Append(directory))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the delete below reports the real failure.
            }
        }
    }
}
