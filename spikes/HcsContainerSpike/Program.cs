// Spike for issue #30: boot a process-isolated Windows container from a
// hand-materialized layer directory via HcsCreateComputeSystem, run a process
// in it, and record, per privilege level, the actual HRESULT of every layer
// and HCS call. Modes:
//
//   run     --layer <dir>  create a scratch layer over <dir>, prepare the layer
//                          stack, create + start the container, exec --command,
//                          capture stdio, terminate, clean up
//   orphan  --layer <dir>  create + start, then exit abruptly WITHOUT terminating,
//                          to test ShouldTerminateOnLastHandleClosed for containers
//   cleanup [--work <dir>] unprepare/deactivate/destroy a leftover sandbox layer
//   list                   enumerate HCS compute systems
//   terminate [--id <id>]  open + terminate a leftover spike container
//
// Options: --id <containerId> --command <cmdline> --seconds <io/exit budget> --work <dir>
// Exit codes: 0 success; 2 layer/create/start failure; 3 exec failure; 4 a step
// after an otherwise-successful run failed (teardown or post-teardown probe);
// 5 list --absent found the id still enumerable; 64 usage.
//
// The layer directory is expected in windowsfilter (wclayer) format — e.g. a
// base image materialized by a one-time `docker pull` under
// C:\ProgramData\Docker\windowsfilter\<sha>. Docker is not involved at runtime.

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.HostComputeSystem;

namespace HcsContainerSpike;

internal static class Program
{
    private const string DefaultContainerId = "AspireHcsContainerSpike";
    private static readonly HRESULT ProbeFailed = new(unchecked((int)0x80004005)); // E_FAIL for locally-judged proof steps

    private static readonly List<(string Step, HRESULT Hr, string Detail)> Results = [];

    private static int Main(string[] args)
    {
        string mode = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
        string containerId = Opt(args, "--id") ?? DefaultContainerId;

        PrintIdentity();

        try
        {
            int code;
            try
            {
                code = mode switch
                {
                    "run" => Run(args, containerId, orphan: false),
                    "orphan" => Run(args, containerId, orphan: true),
                    "cleanup" => Cleanup(args, containerId),
                    "list" => List(args),
                    "terminate" => Terminate(containerId),
                    _ => Usage(),
                };
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"error: {ex.Message}");
                return Usage();
            }
            // Single sink for the exit verdict: a "successful" run whose teardown
            // or post-teardown probes failed must not report success.
            if (code == 0 && Results.Any(r => r.Hr.Failed))
            {
                code = 4;
            }
            return code;
        }
        finally
        {
            PrintSummary();
        }
    }

    private static int Run(string[] args, string containerId, bool orphan)
    {
        string layerPath = Path.TrimEndingDirectorySeparator(Opt(args, "--layer")
            ?? throw new ArgumentException("--layer <materialized base layer dir> is required"));
        string command = Opt(args, "--command") ?? "cmd /c ver & whoami";
        int budgetSeconds = OptInt(args, "--seconds", 60);
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsContainerSpike");
        string sandboxPath = Path.Combine(workDir, containerId);

        // The read-only layer chain, topmost first. Docker's windowsfilter store
        // records parents in layerchain.json; a base image has none. Reading it
        // may fail without elevation (the store is ACLed to Administrators) —
        // File.Exists reports false on access-denied, so say what we assumed.
        List<string> chain = [layerPath];
        string chainFile = Path.Combine(layerPath, "layerchain.json");
        string chainNote;
        try
        {
            if (File.Exists(chainFile))
            {
                chain.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(chainFile)) ?? []);
                chainNote = $"layerchain.json read, {chain.Count} layer(s) total";
            }
            else
            {
                chainNote = "no layerchain.json visible (treating --layer as a base layer; " +
                            "also true when the caller cannot read the store)";
            }
        }
        catch (Exception ex)
        {
            chainNote = $"layerchain.json unreadable ({ex.GetType().Name}: {ex.Message}); treating as base layer";
        }
        Console.WriteLine($"[layers] {chainNote}");

        var layerIds = new List<(string Path, Guid Id)>();
        foreach (string layer in chain)
        {
            HRESULT idHr = WcLayer.LayerId(layer, out Guid guid);
            Step($"NameToGuid({Path.GetFileName(layer)[..Math.Min(12, Path.GetFileName(layer).Length)]}…)", idHr, guid.ToString());
            if (idHr.Failed)
            {
                return 2;
            }
            layerIds.Add((layer, guid));
        }

        PrecleanSandbox(sandboxPath);
        Directory.CreateDirectory(sandboxPath);

        bool prepared = false;
        bool created = false;
        try
        {
            Step("CreateSandboxLayer", WcLayer.CreateScratchLayer(sandboxPath, chain), sandboxPath);
            Step("ActivateLayer", WcLayer.Activate(sandboxPath), "");
            HRESULT hr = WcLayer.Prepare(sandboxPath, chain);
            Step("PrepareLayer", hr, $"{chain.Count} parent layer(s)");
            prepared = hr.Succeeded;

            hr = WcLayer.GetMountPath(sandboxPath, out string volumePath);
            Step("GetLayerMountPath", hr, volumePath);
            if (hr.Failed || Results.Any(r => r.Hr.Failed))
            {
                return 2;
            }

            string config = BuildContainerConfig(layerIds, volumePath);
            Console.WriteLine($"--- Container configuration document ---\n{config}\n----------------------------------------");

            using var op = new HcsOperation();

            hr = PInvoke.HcsCreateComputeSystem(containerId, config, op.Handle, null, out HcsCloseComputeSystemSafeHandle system);
            string? doc = null;
            if (hr.Succeeded)
            {
                (hr, doc) = op.Wait();
            }
            Step("HcsCreateComputeSystem", hr, doc ?? "");
            if (hr.Failed)
            {
                return 2;
            }
            created = true;

            using (system)
            {
                hr = PInvoke.HcsStartComputeSystem(system, op.Handle, null);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait(60_000);
                }
                Step("HcsStartComputeSystem", hr, doc ?? "");
                if (hr.Failed)
                {
                    return 2;
                }

                hr = PInvoke.HcsGetComputeSystemProperties(system, op.Handle, "{}");
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait();
                }
                Step("HcsGetComputeSystemProperties", hr, doc ?? "");

                // Runtime witness for "process-isolated container": the properties
                // must report SystemType Container AND a host-silo object root.
                // A Hyper-V-isolated container lives in a utility VM and cannot
                // have an ObRoot under \Silos\ on the host.
                ProveProcessIsolation(hr, doc);

                if (orphan)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Container '{containerId}' is running. Exiting abruptly WITHOUT terminate/close " +
                                      $"(ShouldTerminateOnLastHandleClosed test). Run 'list --absent {containerId}' next to " +
                                      $"verify the container died, then 'cleanup --work {workDir}' to release the sandbox layer.");
                    PrintSummary();
                    Environment.Exit(99);
                }

                int execResult = Exec(system, command, budgetSeconds);

                hr = PInvoke.HcsTerminateComputeSystem(system, op.Handle, null);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait();
                }
                Step("HcsTerminateComputeSystem", hr, doc ?? "");

                return execResult;
            }
        }
        finally
        {
            if (prepared)
            {
                Step("UnprepareLayer", WcLayer.Unprepare(sandboxPath), "");
            }
            Step("DeactivateLayer", WcLayer.Deactivate(sandboxPath), "");
            Step("DestroyLayer", WcLayer.Destroy(sandboxPath), sandboxPath);

            // Probes that encode the teardown claims as runtime assertions.
            Step("SandboxRemovedProbe", Directory.Exists(sandboxPath) ? ProbeFailed : default,
                Directory.Exists(sandboxPath) ? $"{sandboxPath} still exists" : "sandbox directory removed");
            if (created)
            {
                using var probeOp = new HcsOperation();
                HRESULT probeHr = PInvoke.HcsEnumerateComputeSystems("{}", probeOp.Handle);
                string? enumDoc = null;
                if (probeHr.Succeeded)
                {
                    (probeHr, enumDoc) = probeOp.Wait();
                }
                bool? present = ContainsComputeSystemId(enumDoc, containerId);
                Step("ComputeSystemGoneProbe",
                    probeHr.Failed ? probeHr : present is null or true ? ProbeFailed : default,
                    present switch
                    {
                        null => "enumeration document missing or unparseable — cannot judge",
                        true => $"'{containerId}' still enumerable",
                        false => $"'{containerId}' absent from enumeration",
                    });
            }
        }
    }

    private static void ProveProcessIsolation(HRESULT propertiesHr, string? propertiesDoc)
    {
        string detail;
        bool proved = false;
        if (propertiesHr.Failed || propertiesDoc is null)
        {
            detail = "no properties document to judge";
        }
        else
        {
            try
            {
                JsonNode? props = JsonNode.Parse(propertiesDoc);
                string? systemType = (string?)props?["SystemType"];
                string? obRoot = (string?)props?["ObRoot"];
                proved = string.Equals(systemType, "Container", StringComparison.OrdinalIgnoreCase)
                    && obRoot?.Contains(@"\Silos\", StringComparison.OrdinalIgnoreCase) == true;
                detail = $"SystemType={systemType ?? "(null)"} ObRoot={obRoot ?? "(null)"}";
            }
            catch (System.Text.Json.JsonException ex)
            {
                detail = $"unparseable properties document: {ex.Message}";
            }
        }
        Step("ProcessIsolationProof(properties)", proved ? default : ProbeFailed, detail);
    }

    /// <summary>Null means "cannot judge" (missing/unparseable doc) — callers must
    /// treat that as a failed probe, never as absence.</summary>
    private static bool? ContainsComputeSystemId(string? enumerationDoc, string id)
    {
        if (enumerationDoc is null)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(enumerationDoc) is JsonArray systems
                ? systems.Any(s => string.Equals((string?)s?["Id"], id, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static int Exec(HcsCloseComputeSystemSafeHandle system, string command, int budgetSeconds)
    {
        string processParams = new JsonObject
        {
            ["CommandLine"] = command,
            ["WorkingDirectory"] = @"C:\",
            ["Environment"] = new JsonObject
            {
                ["PATH"] = @"C:\Windows\system32;C:\Windows",
                ["SystemRoot"] = @"C:\Windows",
            },
            ["CreateStdInPipe"] = false,
            ["CreateStdOutPipe"] = true,
            ["CreateStdErrPipe"] = true,
        }.ToJsonString();
        Console.WriteLine($"[exec] {command}");

        using var op = new HcsOperation();
        HRESULT hr = PInvoke.HcsCreateProcess(system, processParams, op.Handle, null, out HcsCloseProcessSafeHandle process);
        HCS_PROCESS_INFORMATION info = default;
        string? doc = null;
        if (hr.Succeeded)
        {
            (hr, info, doc) = op.WaitProcessInfo((uint)(budgetSeconds * 1000));
        }
        Step("HcsCreateProcess", hr, doc ?? $"pid={info.ProcessId}");
        if (hr.Failed)
        {
            return 3;
        }

        using (process)
        {
            Task<string> stdout = Task.Run(() => ReadAllFromHandle(info.StdOutput));
            Task<string> stderr = Task.Run(() => ReadAllFromHandle(info.StdError));
            bool ioDone = Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(budgetSeconds));
            string outText = ioDone ? stdout.Result.Trim() : "(stdout read timed out)";
            string errText = ioDone ? stderr.Result.Trim() : "(stderr read timed out)";
            Console.WriteLine($"[exec] pid={info.ProcessId}\n[stdout]\n{outText}\n[stderr]\n{(errText.Length == 0 ? "(empty)" : errText)}");

            using var propsOp = new HcsOperation();
            hr = PInvoke.HcsGetProcessProperties(process, propsOp.Handle, null);
            if (hr.Succeeded)
            {
                (hr, doc) = propsOp.Wait();
            }
            Step("HcsGetProcessProperties", hr, doc ?? "");

            // The `ver` banner carries the build of the OS binaries the guest is
            // actually running — i.e. the image's build, not necessarily the
            // host's. Report both so cross-build runs are visible in the record.
            System.Text.RegularExpressions.Match banner = System.Text.RegularExpressions.Regex.Match(
                outText, @"Microsoft Windows \[Version (?<build>10\.\d+\.\d+\.\d+)\]");
            string hostBuild = $"{Environment.OSVersion.Version}";
            bool proved = ioDone && banner.Success;
            Step("GuestExecProof(stdout)", proved ? default : ProbeFailed,
                proved
                    ? $"guest build={banner.Groups["build"].Value} host build={hostBuild}"
                    : $"expected 'Microsoft Windows [Version ...]' banner not captured (host build={hostBuild})");
            return proved ? 0 : 3;
        }
    }

    private static string ReadAllFromHandle(HANDLE handle)
    {
        if (handle == HANDLE.Null)
        {
            return "";
        }
        using var safeHandle = new SafeFileHandle(handle, ownsHandle: true);
        using var stream = new FileStream(safeHandle, FileAccess.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void PrecleanSandbox(string sandboxPath)
    {
        if (!Directory.Exists(sandboxPath))
        {
            return;
        }
        // Best-effort by design: these HRESULTs are printed but deliberately kept
        // out of the summary, so a no-op preclean cannot fail the run's verdict.
        HRESULT unprepare = WcLayer.Unprepare(sandboxPath);
        HRESULT deactivate = WcLayer.Deactivate(sandboxPath);
        HRESULT destroy = WcLayer.Destroy(sandboxPath);
        Console.WriteLine($"[preclean] leftover sandbox at {sandboxPath}: " +
                          $"unprepare=0x{(uint)unprepare.Value:X8} deactivate=0x{(uint)deactivate.Value:X8} destroy=0x{(uint)destroy.Value:X8}");
        if (Directory.Exists(sandboxPath))
        {
            Directory.Delete(sandboxPath, recursive: true);
        }
    }

    private static int Cleanup(string[] args, string containerId)
    {
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsContainerSpike");
        string sandboxPath = Path.Combine(workDir, containerId);
        Step("UnprepareLayer", WcLayer.Unprepare(sandboxPath), "");
        Step("DeactivateLayer", WcLayer.Deactivate(sandboxPath), "");
        Step("DestroyLayer", WcLayer.Destroy(sandboxPath), sandboxPath);
        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }

    private static int List(string[] args)
    {
        using var op = new HcsOperation();
        HRESULT hr = PInvoke.HcsEnumerateComputeSystems("{}", op.Handle);
        string? doc = null;
        if (hr.Succeeded)
        {
            (hr, doc) = op.Wait();
        }
        Step("HcsEnumerateComputeSystems", hr, "");
        Console.WriteLine(doc ?? "(no result document)");
        if (hr.Failed)
        {
            return 2;
        }

        // list --absent <id>: a verifier that can actually fail — used after an
        // orphan run to prove ShouldTerminateOnLastHandleClosed reaped the container.
        string? absentId = Opt(args, "--absent");
        if (absentId is not null)
        {
            bool? present = ContainsComputeSystemId(doc, absentId);
            Step("AbsenceProbe", present is null or true ? ProbeFailed : default,
                present switch
                {
                    null => "enumeration document missing or unparseable — cannot judge",
                    true => $"'{absentId}' is still enumerable",
                    false => $"'{absentId}' absent from enumeration",
                });
            return present is false ? 0 : 5;
        }
        return 0;
    }

    private static int Terminate(string containerId)
    {
        const uint GenericAll = 0x10000000;
        HRESULT hr = PInvoke.HcsOpenComputeSystem(containerId, GenericAll, out HcsCloseComputeSystemSafeHandle system);
        Step("HcsOpenComputeSystem", hr, containerId);
        if (hr.Failed)
        {
            return 2;
        }

        using (system)
        {
            using var op = new HcsOperation();
            hr = PInvoke.HcsTerminateComputeSystem(system, op.Handle, null);
            string? doc = null;
            if (hr.Succeeded)
            {
                (hr, doc) = op.Wait();
            }
            Step("HcsTerminateComputeSystem", hr, doc ?? "");
            return hr.Succeeded ? 0 : 2;
        }
    }

    private static string BuildContainerConfig(IReadOnlyList<(string Path, Guid Id)> layers, string volumePath) => new JsonObject
    {
        ["SchemaVersion"] = new JsonObject { ["Major"] = 2, ["Minor"] = 1 },
        ["Owner"] = "AspireHcs",
        ["ShouldTerminateOnLastHandleClosed"] = true,
        ["Container"] = new JsonObject
        {
            ["Storage"] = new JsonObject
            {
                ["Layers"] = new JsonArray([.. layers.Select(l => (JsonNode)new JsonObject
                {
                    ["Id"] = l.Id.ToString(),
                    ["Path"] = l.Path,
                })]),
                ["Path"] = volumePath,
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static void PrintIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        bool hyperVAdmin = principal.IsInRole(new SecurityIdentifier("S-1-5-32-578"));
        Console.WriteLine($"identity={identity.Name} elevated={elevated} hyperVAdministrators={hyperVAdmin} " +
                          $"os={Environment.OSVersion.VersionString}");
    }

    private static void Step(string name, HRESULT hr, string detail)
    {
        Results.Add((name, hr, detail));
        string status = hr.Succeeded ? " OK " : "FAIL";
        Console.WriteLine($"[{status}] {name}: hr=0x{(uint)hr.Value:X8}{DescribeHr(hr)} {Truncate(detail)}");
    }

    private static string DescribeHr(HRESULT hr)
    {
        if (hr.Succeeded)
        {
            return "";
        }
        string? message = Marshal.GetExceptionForHR(hr.Value)?.Message;
        return message is null ? "" : $" ({message})";
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        foreach ((string step, HRESULT hr, string detail) in Results)
        {
            Console.WriteLine($"{(hr.Succeeded ? " OK " : "FAIL")}  0x{(uint)hr.Value:X8}  {step}  {Truncate(detail)}");
        }
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        string flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int OptInt(string[] args, string name, int fallback)
    {
        string? raw = Opt(args, name);
        if (raw is null)
        {
            return fallback;
        }
        return int.TryParse(raw, out int value) && value > 0
            ? value
            : throw new ArgumentException($"{name} must be a positive integer, got '{raw}'");
    }

    private static int Usage()
    {
        Console.WriteLine("""
            usage: HcsContainerSpike <run|orphan|cleanup|list|terminate> [options]
              run       --layer <dir> [--id <containerId>] [--command <cmdline>] [--seconds <n>] [--work <dir>]
              orphan    --layer <dir> ...   create+start then exit without terminating
              cleanup   [--work <dir>] [--id <containerId>]   release a leftover sandbox layer
              list      [--absent <id>]      enumerate HCS compute systems; with --absent,
                                             fail (exit 5) if <id> is still enumerable
              terminate [--id <containerId>]   terminate a leftover spike container
            """);
        return 64;
    }
}

/// <summary>RAII wrapper for an HCS operation handle (same shape as HcsBootSpike).</summary>
internal sealed unsafe class HcsOperation : IDisposable
{
    public HcsCloseOperationSafeHandle Handle { get; } = PInvoke.HcsCreateOperation_SafeHandle(null, null);

    public (HRESULT Hr, string? Doc) Wait(uint timeoutMs = 30_000)
    {
        HRESULT hr = PInvoke.HcsWaitForOperationResult(Handle, timeoutMs, out PWSTR doc);
        return (hr, ConsumeDocument(doc));
    }

    public (HRESULT Hr, HCS_PROCESS_INFORMATION Info, string? Doc) WaitProcessInfo(uint timeoutMs = 30_000)
    {
        HRESULT hr = PInvoke.HcsWaitForOperationResultAndProcessInfo(
            Handle, timeoutMs, out HCS_PROCESS_INFORMATION info, out PWSTR doc);
        return (hr, info, ConsumeDocument(doc));
    }

    private static string? ConsumeDocument(PWSTR doc)
    {
        string? text = doc.Value == null ? null : doc.ToString();
        if (doc.Value != null)
        {
            PInvoke.LocalFree(new HLOCAL(doc.Value));
        }
        return text;
    }

    public void Dispose() => Handle.Dispose();
}
