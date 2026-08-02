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
// Two modes remove the confound and then measure the real gate:
//
//   export     ONE-TIME, ELEVATED. Lifts a layer out of Docker's store into a
//              store the developer owns, using ExportLayer/ImportLayer (the
//              transport format — a plain recursive copy silently drops the
//              backup streams, security descriptors and hard links the layer
//              format depends on, which would produce a broken layer and, worse,
//              a *plausible* privilege result from a layer that never worked).
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

    // ---------------------------------------------------------------- export --

    private static int Export(string[] args)
    {
        string source = Path.TrimEndingDirectorySeparator(Opt(args, "--layer")
            ?? throw new ArgumentException("--layer <source layer dir, e.g. under Docker's windowsfilter store> is required"));
        string storeRoot = Opt(args, "--store") ?? DefaultStoreRoot();
        string name = Opt(args, "--name") ?? Path.GetFileName(source);
        string dest = Path.Combine(storeRoot, name);
        string transport = Path.Combine(storeRoot, ".transport-" + name);

        Console.WriteLine($"[export] source={source}");
        Console.WriteLine($"[export] dest={dest}");

        if (!IsElevated())
        {
            // Refusing beats producing a half-copied layer that would later be
            // mistaken for evidence about the API's privilege model.
            Console.WriteLine("error: export must run ELEVATED — it reads Docker's Administrators-ACLed store. " +
                              "This is the one-time setup step the whole exercise is trying to isolate.");
            return 2;
        }

        // Source-side visibility. Recorded because "the elevated session could
        // read it" is half of the confound being isolated.
        Step("SourceEnumerate", TryEnumerate(source, out int entryCount, out string enumDetail), enumDetail);
        Step("SourceLayerExists(driver)", WcLayer.Exists(source, out bool driverSaysExists), $"driver reports exists={driverSaysExists}");

        List<string> parents = ReadParentChain(source, out string chainNote);
        Console.WriteLine($"[export] {chainNote}");
        if (parents.Count > 0)
        {
            // ImportLayer can only interpret the transport format with every
            // parent present at its recorded path; a partial store would import
            // "successfully" and boot wrong.
            Console.WriteLine("error: --layer has parent layers. This spike exports single base layers only; " +
                              "exporting a chain needs every parent exported first, at paths the import can still resolve.");
            return 2;
        }

        Directory.CreateDirectory(storeRoot);
        PrecleanDirectory(transport, "transport folder");
        PrecleanDirectory(dest, "destination layer");
        Directory.CreateDirectory(transport);

        try
        {
            Step("ExportLayer", WcLayer.Export(source, transport, parents), $"{source} -> {transport}");
            if (Results.Any(r => r.Hr.Failed))
            {
                return 2;
            }

            Step("ImportLayer", WcLayer.Import(dest, transport, parents), $"{transport} -> {dest}");
            if (Results.Any(r => r.Hr.Failed))
            {
                return 2;
            }
        }
        finally
        {
            PrecleanDirectory(transport, "transport folder");
        }

        // A base layer's scratch template lives at the STORE root, not inside the
        // layer (moby's windowsfilter driver generates blank.vhdx once and copies
        // it per scratch), so the export has to carry it across explicitly or the
        // zero-privileged-call hypothesis has nothing to test with.
        CopyScratchTemplateIfPresent(Path.GetDirectoryName(source)!, storeRoot);

        ReportLayerShape(dest);
        ReportAcl(dest);

        Console.WriteLine();
        Console.WriteLine($"Exported. Run the unelevated matrix against it with:");
        Console.WriteLine($"  HcsContainerSpike privilege --layer {dest}");
        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }

    private static void CopyScratchTemplateIfPresent(string sourceStoreRoot, string destStoreRoot)
    {
        foreach (string candidate in ScratchTemplateNames)
        {
            string from = Path.Combine(sourceStoreRoot, candidate);
            if (!File.Exists(from))
            {
                continue;
            }
            string to = Path.Combine(destStoreRoot, candidate);
            try
            {
                File.Copy(from, to, overwrite: true);
                Step($"CopyScratchTemplate({candidate})", default, $"{from} -> {to} ({new FileInfo(to).Length / (1024 * 1024)} MB)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Step($"CopyScratchTemplate({candidate})", ProbeFailed, $"{from}: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        // Not a failure: CreateSandboxLayer generates the template on first use,
        // so a store that has never produced a scratch simply has none yet.
        Console.WriteLine($"[export] no scratch template ({string.Join(" / ", ScratchTemplateNames)}) at {sourceStoreRoot} — " +
                          "the template-copy hypothesis will report SKIP until one exists");
    }

    private static void ReportLayerShape(string layerPath)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Imported layer shape: {layerPath} ---");
        foreach (string relative in (string[])["Files", @"UtilityVM\Files", @"UtilityVM\SystemTemplate.vhdx", "layerchain.json"])
        {
            string full = Path.Combine(layerPath, relative);
            bool isDir = Directory.Exists(full);
            bool isFile = File.Exists(full);
            string what = isDir ? "directory" : isFile ? $"file, {new FileInfo(full).Length / (1024 * 1024)} MB" : "ABSENT";
            Console.WriteLine($"  {relative,-32} {what}");
        }
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
                              "Export the layer to a store you own first (`export` mode) and rerun.");
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

        chain = ReadParentChain(layerPath, out string chainNote);
        chain.Insert(0, layerPath);
        Probe("store", "ReadLayerChain", default, chainNote);

        readable = enumHr.Succeeded && filesHr.Succeeded;
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
            string? template = FindScratchTemplate(layerPath, out string searched);
            if (template is null)
            {
                Skip("xenon", "CopyScratchTemplate", $"no blank template found ({searched})");
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
    /// driver prefers them. These live at the STORE root, not inside a layer.</summary>
    private static readonly string[] ScratchTemplateNames = ["blank-base.vhdx", "blank.vhdx"];

    private static string? FindScratchTemplate(string layerPath, out string searched)
    {
        string storeRoot = Path.GetDirectoryName(layerPath) ?? layerPath;
        List<string> candidates = [
            .. ScratchTemplateNames.Select(n => Path.Combine(storeRoot, n)),
            .. ScratchTemplateNames.Select(n => Path.Combine(layerPath, n)),
        ];
        searched = string.Join("; ", candidates);
        return candidates.FirstOrDefault(File.Exists);
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

    private static List<string> ReadParentChain(string layerPath, out string note)
    {
        string chainFile = Path.Combine(layerPath, "layerchain.json");
        try
        {
            if (!File.Exists(chainFile))
            {
                note = "no layerchain.json visible (base layer, or the caller cannot read it — File.Exists cannot tell them apart)";
                return [];
            }
            string[] parents = JsonSerializer.Deserialize<string[]>(File.ReadAllText(chainFile)) ?? [];
            note = $"layerchain.json: {parents.Length} parent layer(s)";
            return [.. parents];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            note = $"layerchain.json unreadable ({ex.GetType().Name}: {ex.Message}); treating as base layer";
            return [];
        }
    }

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
