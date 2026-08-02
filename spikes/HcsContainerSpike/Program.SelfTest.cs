// Micro self-tests for the native building blocks of the import path (issue
// #30): each new P/Invoke surface earns a runnable probe here BEFORE the import
// loop composes them, so a marshalling bug (UNICODE_STRING byte lengths,
// packed WIN32_STREAM_ID headers, token privilege plumbing) dies in a 1-second
// run against scratch state instead of surfacing mid-way through a 400 MB
// layer import.
//
// Outcomes are recorded through the same Step/Results machinery as every other
// mode. Privilege-dependent rows (token enablement) record what the CURRENT
// token produced — run both elevated and unelevated to see both sides.

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace HcsContainerSpike;

internal static partial class Program
{
    private static int SelfTest(string[] args)
    {
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsSelfTest");

        // Token privileges: the outcome depends on the token, so the row states
        // which result THIS run proves. Elevated: both enable. Unelevated: the
        // distinct ERROR_NOT_ALL_ASSIGNED path, which import relies on to fail
        // fast instead of dying confusingly mid-import.
        HRESULT hr = TokenPrivileges.EnableBackupRestorePrivileges(out string detail);
        bool elevated = IsElevated();
        bool expected = elevated ? hr.Succeeded : hr.Value == unchecked((int)0x80070514);
        Step($"SelfTest(token privileges, elevated={elevated})",
            expected ? default : hr.Succeeded ? ProbeFailed : hr,
            $"{detail}{(expected ? "" : " — UNEXPECTED for this elevation")}");

        NtFileSelfTests(workDir);
        BackupStreamSelfTests(workDir);

        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }

    /// <summary>Round-trips a real file's backup stream: BackupRead what Windows
    /// itself serializes for a file carrying an explicit DACL and an alternate
    /// data stream, replay those exact bytes through BackupStreamWriter onto a
    /// fresh file, and compare what came back. If the WIN32_STREAM_ID header
    /// were marshalled with padding, or the context freed in the wrong order,
    /// the replayed file would not reproduce the source — this is the cheapest
    /// place to find that out.
    ///
    /// The security half of the round trip needs SeBackup/SeRestore, so it is
    /// MEASURED, not asserted, when the token lacks them: the row records what
    /// this privilege level produced.</summary>
    private static void BackupStreamSelfTests(string workDir)
    {
        string root = Path.Combine(workDir, "backup");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);

        string sourcePath = Path.Combine(root, "source.txt");
        File.WriteAllText(sourcePath, "primary-stream-content");
        File.WriteAllText(sourcePath + ":extra", "alternate-stream-content");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(sourcePath).SetAccessControl(acl);
        string sourceSddl = new FileInfo(sourcePath).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        bool processSecurity = TokenPrivileges.EnableBackupRestorePrivileges(out _).Succeeded;

        byte[] stream;
        HRESULT hr;
        using (SafeFileHandle source = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BackupStreamReader(source, processSecurity))
        {
            hr = reader.ReadAll(out stream);
        }
        Step($"SelfTest(backup: BackupRead source, security={processSecurity})", hr, $"{stream.Length} bytes of backup stream");
        if (hr.Failed)
        {
            return;
        }

        // Replay in deliberately awkward chunks: BackupWrite must tolerate a
        // split anywhere, including mid-header, exactly as the import's
        // 64 KB copy loop will split large file payloads.
        string replayPath = Path.Combine(root, "replay.txt");
        using (SafeFileHandle target = File.OpenHandle(
            replayPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            FileOptions.WriteThrough | (FileOptions)0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */))
        using (var writer = new BackupStreamWriter(target, processSecurity))
        {
            for (int offset = 0; offset < stream.Length && hr.Succeeded; offset += 7)
            {
                hr = writer.Write(stream.AsSpan(offset, Math.Min(7, stream.Length - offset)));
            }
        }
        Step("SelfTest(backup: replay in 7-byte chunks)", hr, replayPath);
        if (hr.Failed)
        {
            return;
        }

        string primary = File.Exists(replayPath) ? File.ReadAllText(replayPath) : "(absent)";
        Step("SelfTest(backup: primary content survived)",
            primary == "primary-stream-content" ? default : ProbeFailed, $"'{primary}'");

        string alternate;
        try
        {
            alternate = File.ReadAllText(replayPath + ":extra");
        }
        catch (IOException ex)
        {
            alternate = $"({ex.GetType().Name})";
        }
        Step("SelfTest(backup: alternate data stream survived)",
            alternate == "alternate-stream-content" ? default : ProbeFailed, $"'{alternate}'");

        string replaySddl = new FileInfo(replayPath).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        bool sddlMatch = replaySddl == sourceSddl;
        // Without the privileges the SD is not part of the stream at all, so a
        // mismatch there is the expected measurement, not a defect.
        Step($"SelfTest(backup: security descriptor{(processSecurity ? "" : ", MEASURED — no privileges")})",
            sddlMatch || !processSecurity ? default : ProbeFailed,
            sddlMatch ? $"identical: {replaySddl}" : $"source={sourceSddl} replay={replaySddl}");

        // Encoder shapes: assert the wire layout the import depends on rather
        // than trusting the code that produced it.
        byte[] junction = ReparseBuffer.Encode(@"C:\target", isMountPoint: true);
        uint junctionTag = BitConverter.ToUInt32(junction, 0);
        ushort junctionDataLength = BitConverter.ToUInt16(junction, 4);
        bool junctionOk = junctionTag == ReparseBuffer.MountPointTag
            && junctionDataLength == junction.Length - 8
            && BitConverter.ToUInt16(junction, 10) == @"\??\C:\target".Length * 2;
        Step("SelfTest(backup: junction reparse buffer shape)", junctionOk ? default : ProbeFailed,
            $"tag=0x{junctionTag:X8} dataLength={junctionDataLength} total={junction.Length}");

        byte[] symlink = ReparseBuffer.Encode(@"..\relative", isMountPoint: false);
        bool symlinkOk = BitConverter.ToUInt32(symlink, 0) == ReparseBuffer.SymlinkTag
            && BitConverter.ToUInt16(symlink, 4) == symlink.Length - 8
            && BitConverter.ToUInt32(symlink, 16) == 1; // relative flag
        Step("SelfTest(backup: relative symlink reparse buffer shape)", symlinkOk ? default : ProbeFailed,
            $"tag=0x{BitConverter.ToUInt32(symlink, 0):X8} flags={BitConverter.ToUInt32(symlink, 16)} total={symlink.Length}");

        byte[] eas = ExtendedAttributes.Encode([("FIRST", [1, 2, 3]), ("SECOND", [4])]);
        uint firstNext = BitConverter.ToUInt32(eas, 0);
        bool easOk = firstNext % 4 == 0 && firstNext == 8 + 5 + 1 + 3 + 3 // padded to 4
            && eas[5] == 5 && BitConverter.ToUInt16(eas, 6) == 3
            && BitConverter.ToUInt32(eas, (int)firstNext) == 0; // last record terminates
        Step("SelfTest(backup: EA buffer shape)", easOk ? default : ProbeFailed,
            $"firstNextOffset={firstNext} total={eas.Length}");
    }

    private static void NtFileSelfTests(string workDir)
    {
        string root = Path.Combine(workDir, "ntfile");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);

        // Sanitizer: each hostile shape must throw, and a messy-but-benign name
        // must normalize. These die here or they die inside a 400 MB import.
        foreach ((string bad, string why) in (ReadOnlySpan<(string, string)>)
                 [("a:ads", "colon/ADS"), ("../x", "leading ..)"), (@"\abs", "root-relative"), ("a/../../b", "nested ..)"), ("", "empty"), ("./.", "dot-only")])
        {
            bool rejected;
            try
            {
                NtFile.CleanRelativePath(bad);
                rejected = false;
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Step($"SelfTest(nt: sanitizer rejects {why})", rejected ? default : ProbeFailed, $"input '{bad}'");
        }
        string cleaned = NtFile.CleanRelativePath("Files//./Windows/");
        Step("SelfTest(nt: sanitizer normalizes)", cleaned == @"Files\Windows" ? default : ProbeFailed, $"'Files//./Windows/' → '{cleaned}'");

        HRESULT hr = NtFile.OpenRoot(root, out SafeFileHandle rootHandle);
        Step("SelfTest(nt: open root)", hr, root);
        if (hr.Failed)
        {
            return;
        }
        using (rootHandle)
        {
            // Directory create, then a multi-component relative create beneath it
            // (the import loop trusts NT to walk 'd1\d2' in one relative open).
            hr = CreateAndClose(@"d1", rootHandle, NtFile.FileDirectoryFile);
            Step("SelfTest(nt: create dir)", hr, @"d1");
            hr = CreateAndClose(@"d1\d2", rootHandle, NtFile.FileDirectoryFile);
            Step("SelfTest(nt: create nested dir, multi-component)", hr, @"d1\d2");

            // File create + write through the NT handle, read back via Win32 path.
            hr = NtFile.OpenRelative(@"d1\d2\f1.txt", rootHandle,
                NtFile.GenericRead | NtFile.GenericWrite, NtFile.ShareRead, NtFile.FileCreate, 0, out SafeFileHandle f1);
            if (hr.Succeeded)
            {
                using (f1)
                using (var stream = new FileStream(f1, FileAccess.Write))
                {
                    stream.Write("hello-import"u8);
                }
            }
            Step("SelfTest(nt: create file)", hr, @"d1\d2\f1.txt");

            // FILE_CREATE must refuse an existing entry (base layers have no duplicates).
            HRESULT dupe = NtFile.OpenRelative(@"d1\d2\f1.txt", rootHandle,
                NtFile.GenericWrite, NtFile.ShareRead, NtFile.FileCreate, 0, out SafeFileHandle dupeHandle);
            if (dupe.Succeeded)
            {
                dupeHandle.Dispose();
            }
            Step("SelfTest(nt: FILE_CREATE refuses existing)", dupe.Failed ? default : ProbeFailed,
                $"hr=0x{(uint)dupe.Value:X8} (STATUS_OBJECT_NAME_COLLISION → ERROR_ALREADY_EXISTS)");

            // Hard link both ends relative; prove it is the same file by content.
            hr = NtFile.LinkRelative(@"d1\d2\f1.txt", @"d1\link.txt", rootHandle, out string failedStep);
            string linkPath = Path.Combine(root, "d1", "link.txt");
            string linkContent = hr.Succeeded && File.Exists(linkPath) ? File.ReadAllText(linkPath) : "(unreadable)";
            Step("SelfTest(nt: hard link)",
                hr.Succeeded && linkContent == "hello-import" ? default : hr.Failed ? hr : ProbeFailed,
                hr.Failed ? failedStep : $"content via link: '{linkContent}'");

            // Basic-info round trip: the import sets times+attributes on every entry.
            hr = NtFile.OpenRelative(@"d1\d2\f1.txt", rootHandle,
                NtFile.GenericRead | NtFile.GenericWrite, NtFile.ShareRead, NtFile.FileOpen, 0, out SafeFileHandle f1Again);
            if (hr.Succeeded)
            {
                using (f1Again)
                {
                    var want = new FILE_BASIC_INFO
                    {
                        CreationTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc(),
                        LastWriteTime = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc).ToFileTimeUtc(),
                        FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_HIDDEN,
                    };
                    HRESULT setHr = NtFile.SetBasicInfo(f1Again, in want);
                    HRESULT getHr = NtFile.GetBasicInfo(f1Again, out FILE_BASIC_INFO got);
                    bool match = setHr.Succeeded && getHr.Succeeded
                        && got.CreationTime == want.CreationTime
                        && got.LastWriteTime == want.LastWriteTime
                        && (got.FileAttributes & (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_HIDDEN) != 0;
                    Step("SelfTest(nt: basic-info round trip)", match ? default : setHr.Failed ? setHr : getHr.Failed ? getHr : ProbeFailed,
                        $"creation={got.CreationTime} write={got.LastWriteTime} attrs=0x{got.FileAttributes:X}");
                }
            }
            else
            {
                Step("SelfTest(nt: basic-info round trip)", hr, "reopen failed");
            }

            // A plain directory is not a reparse point; the junction-negative
            // case joins once BackupWrite can create one (import creates
            // junctions only through BackupReparseData).
            hr = NtFile.EnsureNotReparsePoint(@"d1", rootHandle);
            Step("SelfTest(nt: EnsureNotReparsePoint on plain dir)", hr, @"d1");
        }
    }

    private static HRESULT CreateAndClose(string relative, SafeFileHandle root, uint options)
    {
        HRESULT hr = NtFile.OpenRelative(relative, root,
            NtFile.GenericRead | NtFile.GenericWrite, NtFile.ShareRead, NtFile.FileCreate, options, out SafeFileHandle handle);
        if (hr.Succeeded)
        {
            handle.Dispose();
        }
        return hr;
    }
}
