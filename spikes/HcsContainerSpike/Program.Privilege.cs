// Developer privilege model for containers — issue #33.
//
// The #30 argon spike found CreateSandboxLayer failing E_ACCESSDENIED unelevated
// even with Hyper-V Administrators membership. That result is CONFOUNDED and
// cannot be cited: every unelevated attempt ran against a layer inside
// C:\ProgramData\Docker\windowsfilter, which is ACLed to Administrators. We
// never isolated whether the gate is the wclayer API or simply the store ACL.
// (The confound is easy to reproduce: unelevated, File.Exists / Test-Path on
// that store returns *false* rather than throwing, so a naive probe silently
// reads "not there" instead of "not allowed".)
//
// RESOLVED 2026-08-02: it was the ACL. With the layer made readable in place,
// CreateSandboxLayer succeeds unelevated (0x00000000), and a Hyper-V-isolated
// container boots end to end as a normal user in Hyper-V Administrators. The
// remaining real gate is on the process-isolated path: ActivateLayer returns
// 0x80070522 ERROR_PRIVILEGE_NOT_HELD — a privilege, not an ACL. Full record in
// docs/container-privilege-matrix.md.
//
// Two modes remove the confound and then measure the real gate:
//
//   grant      ONE-TIME, ELEVATED. Makes a layer readable by the current user IN
//              PLACE, then verifies the grant was SUFFICIENT by opening the files
//              a boot actually needs. An earlier design exported the layer into a
//              developer-owned store instead; that was abandoned after ExportLayer
//              returned 0x80070057, because hcsshim does not route base layers
//              through ExportLayer at all. Moving a base layer into our own store
//              is the OCI-tar import path — image-acquisition work, not this
//              question.
//
//   privilege  Runs the storage-call matrix against a given layer, recording
//              each call's own HRESULT and CONTINUING past failures, so the
//              record shows every gate rather than only the first one. Calls
//              whose preconditions were not met are reported SKIP, never OK —
//              a skipped call must never read as a passing one.
//
// The matrix covers both surfaces (#33 experiment 4: legacy wclayer's privilege
// model may not be computestorage.dll's) and the xenon zero-privileged-call
// hypothesis (#33 experiment 2: the host never Activates/Prepares a xenon
// scratch, so if a copied blank template works in place of CreateSandboxLayer,
// the Hyper-V-isolated path contains no privilege-gated storage call at all).
//
// End-to-end confirmation is deliberately NOT duplicated here: `run --isolation
// <process|hyperv>` already records every HCS call and fails on the first one
// that breaks, which is exactly what a boot attempt should do. The harness
// composes the two. `run --isolation hyperv --scratch template` exercises the
// zero-privileged-call path all the way to a running container.
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static partial class Program
{
    private const int AccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED

    private enum Outcome
    {
        Ok,
        Denied,
        Failed,
        Skipped,
    }

    private sealed record MatrixRow(string Surface, string Call, Outcome Outcome, HRESULT Hr, string Detail);

    private static readonly List<MatrixRow> Matrix = [];

    /// <summary>Records one call in the privilege matrix. Unlike <see cref="Step"/>,
    /// a failure here is DATA, not a verdict — the whole point of the mode is to
    /// observe which calls are gated.</summary>
    private static void Probe(string surface, string call, HRESULT hr, string detail)
    {
        Outcome outcome = hr.Succeeded ? Outcome.Ok
            : hr.Value == AccessDenied ? Outcome.Denied
            : Outcome.Failed;
        Record(surface, call, outcome, hr, detail);
    }

    /// <summary>Records a call that was never attempted because a precondition
    /// failed. Kept distinct from every other outcome so the matrix can never be
    /// read as "this call is unprivileged" when it simply did not run.</summary>
    private static void Skip(string surface, string call, string why) =>
        Record(surface, call, Outcome.Skipped, default, $"not attempted: {why}");

    private static void Record(string surface, string call, Outcome outcome, HRESULT hr, string detail)
    {
        Matrix.Add(new MatrixRow(surface, call, outcome, hr, detail));
        string tag = outcome switch
        {
            Outcome.Ok => " OK ",
            Outcome.Denied => "DENY",
            Outcome.Failed => "FAIL",
            _ => "SKIP",
        };
        string hrText = outcome == Outcome.Skipped ? "----------" : $"0x{(uint)hr.Value:X8}";
        Console.WriteLine($"[{tag}] {surface,-7} {call,-30} {hrText} {Truncate(detail)}");
    }

    // ----------------------------------------------------------------- grant --

    /// <summary>Grants the current user read access to a layer directory IN PLACE
    /// (issue #33, one-time elevated setup).
    ///
    /// This replaced the export approach after `export` was run for real and
    /// ExportLayer returned 0x80070057. hcsshim explains why: base layers are not
    /// exported through ExportLayer at all — NewLayerReader branches to a separate
    /// backup-stream reader when parentLayerPaths is empty ("This is a base layer.
    /// It gets exported differently."). Materializing a base layer into a store we
    /// own is the OCI-tar import path, which belongs to the image-acquisition work,
    /// not to this question.
    ///
    /// Granting in place is also the BETTER experiment. #33 asks whether the gate
    /// is the wclayer API or the store ACL; this changes exactly that one variable
    /// and holds the layer bits identical — bits already proven to boot green as
    /// both argon and xenon on this host. An export would have changed the layer's
    /// location AND its byte-for-byte provenance at the same time.
    ///
    /// Additive and reversible: it adds a read ACE for one user and --revoke
    /// removes it. Docker owns this directory, so this is a deliberate, stated
    /// modification of another product's store, done on a dev box for a spike.</summary>
    private static int Grant(string[] args)
    {
        string layer = Path.TrimEndingDirectorySeparator(Opt(args, "--layer")
            ?? throw new ArgumentException("--layer <layer dir> is required"));
        bool revoke = args.Contains("--revoke");
        string account = WindowsIdentity.GetCurrent().Name;

        Console.WriteLine($"[grant] layer={layer}");
        Console.WriteLine($"[grant] account={account} action={(revoke ? "revoke" : "grant")}");

        if (!IsElevated())
        {
            Console.WriteLine("error: grant must run ELEVATED — it edits ACLs on a directory owned by Administrators. " +
                              "This is the one-time setup step whose necessity the matrix then measures.");
            return 2;
        }

        // Traverse rights on each ancestor, or the grant on the leaf is unreachable:
        // denying traverse on a parent makes a child's contents unopenable no matter
        // what the child's own DACL says (and reports it as "not found", per
        // TryEnumerate's note).
        var ancestors = new List<string>();
        for (string? d = Path.GetDirectoryName(layer); d is not null; d = Path.GetDirectoryName(d))
        {
            ancestors.Insert(0, d);
            if (d.TrimEnd('\\').EndsWith(":", StringComparison.Ordinal))
            {
                break;
            }
        }
        foreach (string ancestor in ancestors.Where(a => a.Contains("Docker", StringComparison.OrdinalIgnoreCase)))
        {
            RunIcacls($"\"{ancestor}\" {(revoke ? $"/remove:g \"{account}\"" : $"/grant \"{account}\":(RX)")}", $"ancestor {Path.GetFileName(ancestor)}");
        }

        // The layer itself, with inheritance, applied to everything beneath it.
        RunIcacls($"\"{layer}\" {(revoke ? $"/remove:g \"{account}\"" : $"/grant \"{account}\":(OI)(CI)(RX)")} /T /C /Q",
            revoke ? "layer tree (revoke)" : "layer tree (grant)");

        ReportAcl(layer);

        if (revoke)
        {
            Console.WriteLine();
            Console.WriteLine("Revoked. The unelevated matrix should now report DENIED again.");
            return 0;
        }

        // SUFFICIENCY, not tidiness, is the verdict. icacls routinely fails on
        // most of a layer tree — measured here: 554 processed, 9613 rejected,
        // because an elevated Administrator still lacks WRITE_DAC on files whose
        // DACLs name only SYSTEM/TrustedInstaller — and that partial grant is
        // ENOUGH, because the layer's bulk content is read by the VM worker
        // process under its own identity via VSMB, not by the developer's token.
        // Failing the step on the rejection count would abort the very flow that
        // is proven to work. So the count is recorded as data, and what decides
        // is whether the files the boot actually opens are now reachable.
        bool sufficient = ProbeGrantSufficiency(layer);
        Console.WriteLine();
        Console.WriteLine(sufficient
            ? $"Granted and verified sufficient. Run the unelevated matrix with:{Environment.NewLine}  HcsContainerSpike privilege --layer {layer}"
            : "Granted, but the layer is still NOT sufficiently readable — see the SufficiencyProbe rows above.");
        return sufficient ? 0 : 2;
    }

    private static void ReportAcl(string path)
    {
        try
        {
            DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
            IdentityReference? owner = security.GetOwner(typeof(NTAccount));
            Console.WriteLine($"--- ACL: owner={owner?.Value ?? "(unknown)"} ---");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(NTAccount)))
            {
                Console.WriteLine($"  {rule.AccessControlType,-5} {rule.IdentityReference.Value,-45} {rule.FileSystemRights}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine($"--- ACL unreadable: {ex.GetType().Name}: {ex.Message} ---");
        }
    }

    /// <summary>Checks that the paths an unelevated boot actually opens are
    /// reachable. This is the authoritative post-grant verdict, and it is
    /// deliberately independent of icacls' own reporting: icacls exits 0 even
    /// when it touched nothing, and its summary line is English-only text that a
    /// localized Windows would render differently. Opening the real files cannot
    /// be fooled by either.</summary>
    private static bool ProbeGrantSufficiency(string layer)
    {
        Console.WriteLine();
        Console.WriteLine("--- Grant sufficiency (the paths an unelevated boot opens) ---");

        bool ok = true;
        foreach (string relative in (string[])["", "Files", "UtilityVM"])
        {
            string path = relative.Length == 0 ? layer : Path.Combine(layer, relative);
            HRESULT hr = TryEnumerate(path, out _, out string detail);
            Step($"SufficiencyProbe(dir:{(relative.Length == 0 ? "." : relative)})", hr, detail);
            ok &= hr.Succeeded;
        }

        // layerchain.json is optional (a base layer may legitimately lack it);
        // the two VHDX templates are not — the xenon boot copies one and probes
        // the other, so an unreadable one fails the run later rather than here.
        foreach (string relative in (string[])["blank-base.vhdx", @"UtilityVM\SystemTemplate.vhdx"])
        {
            string path = Path.Combine(layer, relative);
            HRESULT hr;
            string detail;
            try
            {
                using FileStream probe = File.OpenRead(path);
                hr = default;
                detail = $"{path}: readable ({probe.Length / (1024 * 1024)} MB)";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hr = ex.HResult == AccessDenied ? new HRESULT(AccessDenied) : new HRESULT(ex.HResult);
                detail = $"{path}: {ex.GetType().Name}: {ex.Message}";
            }
            Step($"SufficiencyProbe(file:{Path.GetFileName(relative)})", hr, detail);
            ok &= hr.Succeeded;
        }
        return ok;
    }

    /// <summary>icacls rather than DirectorySecurity: the layer tree is ~1 GB of
    /// files whose inheritance state we do not control, and icacls /T is the
    /// documented way to reapply across one. Its exit code is recorded as the
    /// step's result — a partially applied ACL must not read as success.</summary>
    private static void RunIcacls(string arguments, string what)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;

        // Both pipes must be drained CONCURRENTLY. Reading one to completion and
        // then the other deadlocks the moment the second fills its buffer: the
        // child blocks writing, so it never exits, so the first ReadToEnd never
        // returns. Observed live — `icacls /T /C` over a ~1 GB layer tree emits
        // enough stderr to hang the harness indefinitely.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        const int timeoutMs = 10 * 60 * 1000;
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the timeout and the kill.
            }
            Step($"icacls({what})", ProbeFailed, $"timed out after {timeoutMs / 1000}s and was killed");
            return;
        }
        // Only safe once the process has exited: the pipes are closed, so these
        // are already complete and cannot block.
        Task.WaitAll(stdout, stderr);

        string combined = stdout.Result + stderr.Result;
        string summary = combined.ReplaceLineEndings(" ").Trim();

        // icacls exits 0 even when it could not touch a single file, reporting
        // per-file outcomes in a summary line instead — observed here as
        // "Successfully processed 554 files; Failed processing 9613 files" with
        // exit 0. This surfaces that count so the record shows it, but it is
        // DIAGNOSTIC ONLY and deliberately does not decide the step: the pattern
        // is English-only text that a localized Windows would not produce, and a
        // partial grant is frequently sufficient anyway. ProbeGrantSufficiency
        // opens the files that matter and is the authoritative verdict.
        System.Text.RegularExpressions.Match failed = System.Text.RegularExpressions.Regex.Match(
            combined, @"Failed processing (?<n>\d+) files");
        string rejected = failed.Success && failed.Groups["n"].Value != "0"
            ? $" [{failed.Groups["n"].Value} files rejected the ACE — diagnostic only; sufficiency is probed separately]"
            : "";

        Step($"icacls({what})", process.ExitCode == 0 ? default : ProbeFailed,
            $"exit={process.ExitCode}{rejected} {Truncate(summary)}");
    }

    // ------------------------------------------------------------- privilege --

    private static int Privilege(string[] args)
    {
        string layerPath = Path.TrimEndingDirectorySeparator(Opt(args, "--layer")
            ?? throw new ArgumentException("--layer <layer dir> is required"));
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsPrivilegeProbe");
        string containerId = Opt(args, "--id") ?? DefaultContainerId;

        Console.WriteLine();
        Console.WriteLine($"=== Privilege matrix: {layerPath} ===");
        Console.WriteLine($"    elevated={IsElevated()} work={workDir}");
        Console.WriteLine();

        ProbeStoreVisibility(layerPath, out bool layerReadable, out List<string> chain);
        if (!layerReadable)
        {
            // Everything downstream would fail for a reason that has nothing to do
            // with the API's privilege model. Reporting those as DENY is exactly
            // the mistake that confounded the #30 result.
            Console.WriteLine();
            Console.WriteLine("Layer is not readable at this privilege level — the storage calls are NOT attempted, " +
                              "because their failures would be attributable to the store ACL rather than the API. " +
                              "Make the layer readable first (`grant` mode, elevated) and rerun.");
            SkipAllStorageCalls("layer directory unreadable at this privilege level");
            PrintMatrix();
            return 2;
        }

        var layerIds = new List<(string Path, Guid Id)>();
        bool idsOk = true;
        foreach (string layer in chain)
        {
            HRESULT hr = WcLayer.LayerId(layer, out Guid guid);
            Probe("both", "NameToGuid", hr, $"{Path.GetFileName(layer)} -> {guid}");
            idsOk &= hr.Succeeded;
            layerIds.Add((layer, guid));
        }

        ProbeLegacyChain(workDir, containerId, chain, idsOk);
        ProbeModernChain(workDir, containerId, layerIds, idsOk);
        ProbeXenonScratchPath(workDir, containerId, layerPath);

        PrintMatrix();
        return 0;
    }

    private static void ProbeStoreVisibility(string layerPath, out bool readable, out List<string> chain)
    {
        HRESULT enumHr = TryEnumerate(layerPath, out int entryCount, out string enumDetail);
        Probe("store", "EnumerateLayerDir", enumHr, enumDetail);

        HRESULT filesHr = TryEnumerate(Path.Combine(layerPath, "Files"), out int fileCount, out string filesDetail);
        Probe("store", @"EnumerateFiles\", filesHr, filesDetail);

        // The driver's own existence answer, which — unlike File.Exists — can
        // distinguish "absent" from "you may not look".
        HRESULT existsHr = WcLayer.Exists(layerPath, out bool driverSaysExists);
        Probe("legacy", "LayerExists", existsHr, $"driver reports exists={driverSaysExists}");

        chain = ReadParentChain(layerPath, out ChainStatus chainStatus, out string chainNote);
        chain.Insert(0, layerPath);
        Probe("store", "ReadLayerChain", ChainHr(chainStatus), chainNote);

        // A chain we could not read makes every downstream call's parent list a
        // guess, so the layer counts as unreadable even when the directory itself
        // enumerated fine — a probe run against a guessed chain proves nothing.
        readable = enumHr.Succeeded && filesHr.Succeeded && ChainHr(chainStatus).Succeeded;
    }

    private static void ProbeLegacyChain(string workDir, string containerId, IReadOnlyList<string> chain, bool idsOk)
    {
        string sandbox = Path.Combine(workDir, containerId + "-legacy");
        PrecleanDirectory(sandbox, "legacy sandbox");
        Directory.CreateDirectory(sandbox);

        bool created = false, activated = false, prepared = false;
        try
        {
            if (!idsOk)
            {
                Skip("legacy", "CreateSandboxLayer", "NameToGuid failed for at least one layer");
            }
            else
            {
                HRESULT hr = WcLayer.CreateScratchLayer(sandbox, chain);
                Probe("legacy", "CreateSandboxLayer", hr, sandbox);
                created = hr.Succeeded;
            }

            if (!created)
            {
                Skip("legacy", "ActivateLayer", "CreateSandboxLayer did not produce a scratch");
                Skip("legacy", "PrepareLayer", "no scratch to prepare");
                Skip("legacy", "GetLayerMountPath", "no prepared layer to mount");
                return;
            }

            HRESULT activateHr = WcLayer.Activate(sandbox);
            Probe("legacy", "ActivateLayer", activateHr, sandbox);
            activated = activateHr.Succeeded;

            if (!activated)
            {
                Skip("legacy", "PrepareLayer", "ActivateLayer failed");
                Skip("legacy", "GetLayerMountPath", "ActivateLayer failed");
                return;
            }

            HRESULT prepareHr = WcLayer.Prepare(sandbox, chain);
            Probe("legacy", "PrepareLayer", prepareHr, $"{chain.Count} parent layer(s)");
            prepared = prepareHr.Succeeded;

            if (!prepared)
            {
                Skip("legacy", "GetLayerMountPath", "PrepareLayer failed");
                return;
            }

            HRESULT mountHr = WcLayer.GetMountPath(sandbox, out string volumePath);
            Probe("legacy", "GetLayerMountPath", mountHr, volumePath);
        }
        finally
        {
            if (prepared)
            {
                Probe("legacy", "UnprepareLayer", WcLayer.Unprepare(sandbox), sandbox);
            }
            else
            {
                Skip("legacy", "UnprepareLayer", "layer was never prepared");
            }

            if (activated)
            {
                Probe("legacy", "DeactivateLayer", WcLayer.Deactivate(sandbox), sandbox);
            }
            else
            {
                Skip("legacy", "DeactivateLayer", "layer was never activated");
            }

            if (created)
            {
                Probe("legacy", "DestroyLayer", WcLayer.Destroy(sandbox), sandbox);
            }
            else
            {
                Skip("legacy", "DestroyLayer", "no scratch was created");
            }
            PrecleanDirectory(sandbox, "legacy sandbox");
        }
    }

    private static void ProbeModernChain(string workDir, string containerId, IReadOnlyList<(string Path, Guid Id)> layerIds, bool idsOk)
    {
        string sandbox = Path.Combine(workDir, containerId + "-modern");
        PrecleanDirectory(sandbox, "modern sandbox");
        Directory.CreateDirectory(sandbox);

        bool initialized = false, attached = false;
        try
        {
            if (!idsOk)
            {
                Skip("modern", "HcsInitializeWritableLayer", "NameToGuid failed for at least one layer");
            }
            else
            {
                HRESULT hr = ComputeStorage.InitializeWritableLayer(sandbox, layerIds);
                Probe("modern", "HcsInitializeWritableLayer", hr, sandbox);
                initialized = hr.Succeeded;
            }

            if (!initialized)
            {
                Skip("modern", "HcsAttachLayerStorageFilter", "HcsInitializeWritableLayer did not produce a writable layer");
                return;
            }

            HRESULT attachHr = ComputeStorage.AttachLayerStorageFilter(sandbox, layerIds);
            Probe("modern", "HcsAttachLayerStorageFilter", attachHr, sandbox);
            attached = attachHr.Succeeded;
        }
        finally
        {
            if (attached)
            {
                Probe("modern", "HcsDetachLayerStorageFilter", ComputeStorage.DetachLayerStorageFilter(sandbox), sandbox);
            }
            else
            {
                Skip("modern", "HcsDetachLayerStorageFilter", "storage filter was never attached");
            }

            if (initialized)
            {
                Probe("modern", "HcsDestroyLayer", ComputeStorage.DestroyLayer(sandbox), sandbox);
            }
            else
            {
                Skip("modern", "HcsDestroyLayer", "no writable layer was initialized");
            }
            PrecleanDirectory(sandbox, "modern sandbox");
        }
    }

    /// <summary>#33 experiment 2 + 3: the Hyper-V-isolated path never asks the host
    /// to Activate/Prepare a scratch — the guest consumes the VHDX — so the only
    /// storage call left is CreateSandboxLayer. If a plain copy of the store's
    /// blank template can stand in for it, the xenon path has NO privilege-gated
    /// storage call. HcsGrantVmAccess is probed on the copy because that is the
    /// one remaining host-side call the xenon boot makes against the file.</summary>
    private static void ProbeXenonScratchPath(string workDir, string containerId, string layerPath)
    {
        string scratchDir = Path.Combine(workDir, containerId + "-template");
        PrecleanDirectory(scratchDir, "template scratch");
        Directory.CreateDirectory(scratchDir);

        try
        {
            string? template = FindScratchTemplate(layerPath, out string searched, out bool denied);
            if (template is null)
            {
                // A denied template is a privilege result and belongs in the
                // matrix as one; only a genuinely absent template is a SKIP.
                if (denied)
                {
                    Probe("xenon", "FindScratchTemplate", new HRESULT(AccessDenied), searched);
                }
                else
                {
                    Skip("xenon", "FindScratchTemplate", $"no blank template exists ({searched})");
                }
                Skip("xenon", "CopyScratchTemplate", denied ? "template exists but could not be opened" : "no template to copy");
                Skip("xenon", "HcsGrantVmAccess(scratch)", "no scratch to grant access to");
                return;
            }
            Probe("xenon", "FindScratchTemplate", default, template);

            string scratch = Path.Combine(scratchDir, "sandbox.vhdx");
            HRESULT copyHr = default;
            string copyDetail;
            try
            {
                File.Copy(template, scratch, overwrite: true);
                copyDetail = $"{template} -> {scratch} ({new FileInfo(scratch).Length / (1024 * 1024)} MB)";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                copyHr = ex is UnauthorizedAccessException ? new HRESULT(AccessDenied) : ProbeFailed;
                copyDetail = $"{ex.GetType().Name}: {ex.Message}";
            }
            Probe("xenon", "CopyScratchTemplate", copyHr, copyDetail);

            if (copyHr.Failed)
            {
                Skip("xenon", "HcsGrantVmAccess(scratch)", "template copy failed");
                return;
            }

            Probe("xenon", "HcsGrantVmAccess(scratch)", PInvoke.HcsGrantVmAccess(containerId + "-uvm", scratch), scratch);
        }
        finally
        {
            PrecleanDirectory(scratchDir, "template scratch");
        }
    }

    private static void SkipAllStorageCalls(string why)
    {
        foreach ((string surface, string call) in (( string, string )[])[
            ("legacy", "CreateSandboxLayer"), ("legacy", "ActivateLayer"), ("legacy", "PrepareLayer"),
            ("legacy", "GetLayerMountPath"), ("legacy", "UnprepareLayer"), ("legacy", "DeactivateLayer"),
            ("legacy", "DestroyLayer"),
            ("modern", "HcsInitializeWritableLayer"), ("modern", "HcsAttachLayerStorageFilter"),
            ("modern", "HcsDetachLayerStorageFilter"), ("modern", "HcsDestroyLayer"),
            ("xenon", "CopyScratchTemplate"), ("xenon", "HcsGrantVmAccess(scratch)")])
        {
            Skip(surface, call, why);
        }
    }

    private static void PrintMatrix()
    {
        Console.WriteLine();
        Console.WriteLine($"=== Privilege matrix (elevated={IsElevated()}, hyperVAdministrators={IsHyperVAdmin()}) ===");
        Console.WriteLine($"{"outcome",-8}{"surface",-9}{"call",-32}{"hresult",-12}detail");
        foreach (MatrixRow row in Matrix)
        {
            string tag = row.Outcome switch
            {
                Outcome.Ok => "OK",
                Outcome.Denied => "DENIED",
                Outcome.Failed => "FAILED",
                _ => "SKIP",
            };
            string hrText = row.Outcome == Outcome.Skipped ? "-" : $"0x{(uint)row.Hr.Value:X8}";
            Console.WriteLine($"{tag,-8}{row.Surface,-9}{row.Call,-32}{hrText,-12}{Truncate(row.Detail)}");
        }

        int denied = Matrix.Count(r => r.Outcome == Outcome.Denied);
        int failed = Matrix.Count(r => r.Outcome == Outcome.Failed);
        int skipped = Matrix.Count(r => r.Outcome == Outcome.Skipped);
        Console.WriteLine();
        Console.WriteLine($"counts: ok={Matrix.Count(r => r.Outcome == Outcome.Ok)} denied={denied} failed={failed} skipped={skipped}");
        Console.WriteLine(denied == 0 && failed == 0 && skipped == 0
            ? "verdict: every probed call succeeded at this privilege level."
            : "verdict: see DENIED/FAILED rows for the gates; SKIP rows were never attempted and prove nothing.");
    }

    // ----------------------------------------------------------------- shared --

    /// <summary>Base-layer scratch templates, in the order moby's windowsfilter
    /// driver prefers them. OBSERVED 2026-08-02: `blank-base.vhdx` lives INSIDE
    /// the layer directory, alongside Files\ and UtilityVM\ — not at the store
    /// root, as an earlier revision of this comment asserted. Both locations are
    /// searched because the store-root layout is what moby's driver historically
    /// used and this has only been checked against one store.</summary>
    private static readonly string[] ScratchTemplateNames = ["blank-base.vhdx", "blank.vhdx"];

    /// <summary>Locates a blank scratch template, opening each candidate rather
    /// than asking File.Exists — which reports a denied file as absent and would
    /// turn a DENIED result into a "no template found" SKIP, silently converting
    /// a privilege finding into a non-finding. <paramref name="denied"/> reports
    /// that a candidate exists but could not be opened at this privilege level.</summary>
    private static string? FindScratchTemplate(string layerPath, out string searched, out bool denied)
    {
        string storeRoot = Path.GetDirectoryName(layerPath) ?? layerPath;
        List<string> candidates = [
            .. ScratchTemplateNames.Select(n => Path.Combine(storeRoot, n)),
            .. ScratchTemplateNames.Select(n => Path.Combine(layerPath, n)),
        ];

        denied = false;
        var notes = new List<string>();
        foreach (string candidate in candidates)
        {
            try
            {
                using FileStream probe = File.OpenRead(candidate);
                searched = string.Join("; ", notes.Append($"{candidate}: readable"));
                return candidate;
            }
            catch (UnauthorizedAccessException ex)
            {
                denied = true;
                notes.Add($"{candidate}: ERROR_ACCESS_DENIED [0x{(uint)ex.HResult:X8}]");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                notes.Add($"{candidate}: absent");
            }
            catch (IOException ex)
            {
                notes.Add($"{candidate}: {ex.GetType().Name}");
            }
        }
        searched = string.Join("; ", notes);
        return null;
    }

    private static string DefaultStoreRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AspireHcs", "layers");

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsHyperVAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier("S-1-5-32-578"));
    }

    /// <summary>Enumerates a directory, reporting the UNDERLYING Win32 error rather
    /// than the .NET exception type.
    ///
    /// This distinction is load-bearing for the whole spike. The BCL does not map
    /// access failures to UnauthorizedAccessException reliably: when the caller
    /// lacks traverse rights on a PARENT directory, enumerating a child throws
    /// DirectoryNotFoundException — "denied" arriving dressed as "absent", the
    /// same lie File.Exists tells on Docker's store. Classifying by exception type
    /// would file a DENIED row as FAILED and corrupt the matrix this spike exists
    /// to produce, so the exception's HResult (the real Win32 code) decides.</summary>
    private static HRESULT TryEnumerate(string path, out int count, out string detail)
    {
        const int ErrorFileNotFound = unchecked((int)0x80070002);
        const int ErrorPathNotFound = unchecked((int)0x80070003);

        count = 0;
        try
        {
            count = Directory.EnumerateFileSystemEntries(path).Count();
            detail = $"{path}: {count} entr{(count == 1 ? "y" : "ies")}";
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            string win32 = ex.HResult switch
            {
                AccessDenied => "ERROR_ACCESS_DENIED",
                ErrorPathNotFound => "ERROR_PATH_NOT_FOUND (absent, or a parent denies traverse — Win32 cannot distinguish these)",
                ErrorFileNotFound => "ERROR_FILE_NOT_FOUND (absent, or a parent denies traverse — Win32 cannot distinguish these)",
                _ => $"{ex.GetType().Name}",
            };
            detail = $"{path}: {win32} [0x{(uint)ex.HResult:X8}] {ex.Message}";
            return ex.HResult == AccessDenied ? new HRESULT(AccessDenied) : new HRESULT(ex.HResult);
        }
    }

    /// <summary>Why a parent chain came back the way it did. An empty parent list
    /// is produced by four different situations that must never be conflated: a
    /// genuine base layer, a denied read, an I/O error, and a malformed file. The
    /// first is ground truth; the other three are "we do not know", and treating
    /// them as "no parents" is precisely the access-denied-as-absent defect this
    /// spike exists to remove — committed here originally, caught in review.</summary>
    private enum ChainStatus
    {
        Parsed,
        Absent,
        Denied,
        Unreadable,
        Malformed,
    }

    private static List<string> ReadParentChain(string layerPath, out ChainStatus status, out string note)
    {
        string chainFile = Path.Combine(layerPath, "layerchain.json");
        try
        {
            string text;
            try
            {
                text = File.ReadAllText(chainFile);
            }
            catch (FileNotFoundException)
            {
                // "File not found" is only evidence of ABSENCE if we are allowed
                // to look. Win32 returns ERROR_FILE_NOT_FOUND for a file inside a
                // directory the caller cannot list, so trusting it here would
                // report Docker's ACLed store as a clean base layer — which is
                // exactly what it did before this check was added. Corroborate
                // against the containing directory before believing it.
                HRESULT dirHr = TryEnumerate(layerPath, out _, out string dirDetail);
                if (dirHr.Failed)
                {
                    status = dirHr.Value == AccessDenied ? ChainStatus.Denied : ChainStatus.Unreadable;
                    note = $"layerchain.json reported absent, but its directory is not readable — absence " +
                           $"cannot be concluded ({dirDetail})";
                    return [];
                }
                status = ChainStatus.Absent;
                note = "no layerchain.json (base layer; directory is readable, so this is genuine absence)";
                return [];
            }
            catch (DirectoryNotFoundException ex)
            {
                status = ChainStatus.Unreadable;
                note = $"layerchain.json: ERROR_PATH_NOT_FOUND [0x{(uint)ex.HResult:X8}] — absent, " +
                       "or a parent directory denies traverse; Win32 cannot distinguish these";
                return [];
            }

            string[]? parents = JsonSerializer.Deserialize<string[]>(text);
            if (parents is null)
            {
                // Literal `null` is how moby's windowsfilter driver records "no
                // parents": json.Marshal of a nil parent slice. VERIFIED on this
                // host 2026-08-02 — both base layers in the Docker store contain
                // exactly `null`, while their child layer contains a one-element
                // array. An earlier revision treated null as UNKNOWN and failed
                // closed, which would have refused every real base layer; that
                // was reasoning about the format instead of reading it.
                status = ChainStatus.Parsed;
                note = "layerchain.json is JSON null — moby's encoding for a base layer with no parents";
                return [];
            }
            status = ChainStatus.Parsed;
            note = $"layerchain.json: {parents.Length} parent layer(s)";
            return [.. parents];
        }
        catch (UnauthorizedAccessException ex)
        {
            status = ChainStatus.Denied;
            note = $"layerchain.json: ERROR_ACCESS_DENIED [0x{(uint)ex.HResult:X8}] {ex.Message}";
            return [];
        }
        catch (JsonException ex)
        {
            status = ChainStatus.Malformed;
            note = $"layerchain.json is readable but malformed ({ex.Message}) — the parent chain is UNKNOWN, not empty";
            return [];
        }
        catch (IOException ex)
        {
            status = ChainStatus.Unreadable;
            note = $"layerchain.json unreadable ({ex.GetType().Name}: {ex.Message})";
            return [];
        }
    }

    /// <summary>Maps a chain read to a matrix HRESULT. Only Absent and Parsed are
    /// successes; the rest must never show as an OK row.</summary>
    private static HRESULT ChainHr(ChainStatus status) => status switch
    {
        ChainStatus.Parsed or ChainStatus.Absent => default,
        ChainStatus.Denied => new HRESULT(AccessDenied),
        _ => ProbeFailed,
    };

    private static void PrecleanDirectory(string path, string what)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[preclean] {what} at {path} not removable: {ex.Message}");
        }
    }
}
