// Hyper-V-isolated (xenon) mode for the container spike — issue #32.
//
// Reference shape: hcsshim's WCOW xenon path (read at tag v0.14.1, plus
// v0.8.26 for the HCS-internal guest-connection variant). Two compute systems:
//
//   1. A utility VM booted from the UtilityVM directory that ships INSIDE the
//      base layer (verified in the #30 spike): UEFI-boots
//      \EFI\Microsoft\Boot\bootmgfw.efi with device type "VmbFs" from a VSMB
//      share of UtilityVM\Files, with a copy of UtilityVM\SystemTemplate.vhdx
//      as the UVM's own scratch on SCSI 0:0. The empty "GuestConnection": {}
//      section asks HCS to run the guest-services (GCS) bridge itself —
//      hcsshim v0.8 shape — which is what lets this spike stay on public
//      computecore.dll calls instead of speaking the GCS wire protocol the way
//      modern hcsshim does.
//
//   2. A hosted container: a second HcsCreateComputeSystem whose document is
//      { HostingSystemId: <uvmId>, HostedSystem: { SchemaVersion, Container } }.
//      Answer to #32's schema question: the v2 schema has NO inline UtilityVM
//      section on a container — the xenon shape is two compute systems plus
//      the HostedSystem wrapper, schema 2.1 throughout (hcsshim
//      internal/uvm/create.go CreateContainer, gc == nil branch).
//
// Storage plumbing before the hosted create, all via HcsModifyComputeSystem on
// the UVM (with an HCS-owned bridge, HCS forwards GuestRequest sections to the
// guest GCS itself — hcsshim internal/uvm/modify.go):
//   - one read-only VSMB share per layer directory; guest-visible path is
//     \\?\VMSMB\VSMB-{dcc079ae-…}\<shareName> (fixed prefix, hcsshim vsmb.go),
//   - SCSI hot-add of the same CreateSandboxLayer sandbox.vhdx the argon path
//     uses — for xenon there is NO host-side Activate/Prepare/GetLayerMountPath;
//     the guest consumes the VHDX directly — plus a MappedVirtualDisk guest
//     request mounting it at c:\mounts\scsi\m0,
//   - a CombinedLayers guest request stacking the VSMB layers over the scratch.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.HostComputeSystem;

namespace HcsContainerSpike;

internal static partial class Program
{
    // hcsshim's deterministic name for SCSI controller 0: guid.NewV5("d422512d-2bf2-4752-809d-7b82b5fcb1b4", "0").
    private const string ScsiController0 = "df6d0690-79e5-55b6-a5ec-c1e2f77f580a";
    // Fixed VSMB redirector prefix inside any WCOW utility VM (hcsshim internal/uvm/vsmb.go).
    private const string VsmbGuestPrefix = @"\\?\VMSMB\VSMB-{dcc079ae-60ba-4d07-847c-3493609c0870}\";
    // First slot of hcsshim's WCOW guest mount format `c:\mounts\scsi\m%d`.
    private const string GuestScratchPath = @"c:\mounts\scsi\m0";

    private static int RunXenon(
        string containerId,
        IReadOnlyList<string> chain,
        IReadOnlyList<(string Path, Guid Id)> layerIds,
        string command,
        int budgetSeconds,
        string workDir,
        bool orphan)
    {
        string sandboxPath = Path.Combine(workDir, containerId);
        string uvmId = containerId + "-uvm";
        string uvmScratchDir = sandboxPath + "-uvm";

        // hcsshim locates the boot environment as the first layer (topmost
        // first) carrying a UtilityVM directory (internal/uvmfolder/locate.go).
        string? uvmLayer = chain.FirstOrDefault(l => Directory.Exists(Path.Combine(l, "UtilityVM")));
        Step("LocateUtilityVM", uvmLayer is null ? ProbeFailed : default,
            uvmLayer is null
                ? "no layer in the chain carries a UtilityVM directory (also true when the caller cannot read the store)"
                : Path.Combine(uvmLayer, "UtilityVM"));
        if (uvmLayer is null)
        {
            return 2;
        }

        // #32 question "does the docker-materialized UtilityVM boot as-is": the
        // windowsfilter import is expected to have produced SystemTemplate.vhdx
        // (the UVM scratch template) next to UtilityVM\Files.
        string template = Path.Combine(uvmLayer, "UtilityVM", "SystemTemplate.vhdx");
        bool templateExists = File.Exists(template);
        Step("UvmTemplateProbe", templateExists ? default : ProbeFailed,
            templateExists ? template : $"{template} missing — the docker import did not leave a UVM scratch template");
        if (!templateExists)
        {
            return 2;
        }

        PrecleanSandbox(sandboxPath);
        PrecleanUvmScratch(uvmScratchDir);
        Directory.CreateDirectory(sandboxPath);
        Directory.CreateDirectory(uvmScratchDir);

        var swTotal = Stopwatch.StartNew();
        bool uvmCreated = false;
        bool hostedCreated = false;
        try
        {
            Step("CreateSandboxLayer", WcLayer.CreateScratchLayer(sandboxPath, chain), sandboxPath);
            string sandboxVhdx = Path.Combine(sandboxPath, "sandbox.vhdx");
            Step("ScratchVhdxProbe", File.Exists(sandboxVhdx) ? default : ProbeFailed, sandboxVhdx);

            string uvmScratchVhdx = Path.Combine(uvmScratchDir, "sandbox.vhdx");
            File.Copy(template, uvmScratchVhdx, overwrite: true);
            Step("HcsGrantVmAccess(uvm scratch)", PInvoke.HcsGrantVmAccess(uvmId, uvmScratchVhdx), uvmScratchVhdx);
            Step("HcsGrantVmAccess(container scratch)", PInvoke.HcsGrantVmAccess(uvmId, sandboxVhdx), sandboxVhdx);
            if (Results.Any(r => r.Hr.Failed))
            {
                return 2;
            }

            string uvmConfig = BuildUvmConfig(uvmLayer, uvmScratchVhdx);
            Console.WriteLine($"--- Utility VM configuration document ---\n{uvmConfig}\n----------------------------------------");

            using var op = new HcsOperation();

            HRESULT hr = PInvoke.HcsCreateComputeSystem(uvmId, uvmConfig, op.Handle, null, out HcsCloseComputeSystemSafeHandle uvm);
            string? doc = null;
            if (hr.Succeeded)
            {
                (hr, doc) = op.Wait(60_000);
            }
            Step("HcsCreateComputeSystem(uvm)", hr, doc ?? "");
            if (hr.Failed)
            {
                return 2;
            }
            uvmCreated = true;

            using (uvm)
            {
                hr = PInvoke.HcsStartComputeSystem(uvm, op.Handle, null);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait(120_000);
                }
                Step("HcsStartComputeSystem(uvm)", hr, $"{doc ?? ""} +{swTotal.ElapsedMilliseconds}ms".Trim());
                if (hr.Failed)
                {
                    return 2;
                }

                var guestLayers = new List<(Guid Id, string GuestPath)>();
                int shareIndex = 0;
                foreach ((string path, Guid id) in layerIds)
                {
                    string shareName = "s" + (++shareIndex).ToString("x");
                    (hr, doc) = Modify(uvm, VsmbAddDoc(shareName, path));
                    Step($"AddVsmbLayer({shareName})", hr, $"{path} -> {VsmbGuestPrefix}{shareName}");
                    if (hr.Failed)
                    {
                        return 2;
                    }
                    guestLayers.Add((id, VsmbGuestPrefix + shareName));
                }

                (hr, doc) = Modify(uvm, ScsiAttachDoc(sandboxVhdx));
                Step("ScsiAttachScratch", hr, $"{sandboxVhdx} -> controller 0 lun 1");
                if (hr.Failed)
                {
                    return 2;
                }

                // First operation that lands in the guest: retried while the
                // GCS comes up, recording how long guest readiness gated us.
                string retryDetail;
                (hr, doc, retryDetail) = ModifyWithRetry(uvm, GuestMountScratchDoc(), 90_000);
                Step("GuestMountScratch", hr, $"{GuestScratchPath} lun 1; {retryDetail} +{swTotal.ElapsedMilliseconds}ms");
                if (hr.Failed)
                {
                    return 2;
                }

                (hr, doc) = Modify(uvm, CombinedLayersDoc(guestLayers));
                Step("CombineLayers", hr, $"{guestLayers.Count} layer(s) over {GuestScratchPath}");
                if (hr.Failed)
                {
                    return 2;
                }

                string hostedConfig = BuildHostedContainerConfig(uvmId, guestLayers);
                Console.WriteLine($"--- Hosted container configuration document ---\n{hostedConfig}\n----------------------------------------");

                hr = PInvoke.HcsCreateComputeSystem(containerId, hostedConfig, op.Handle, null, out HcsCloseComputeSystemSafeHandle hosted);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait(60_000);
                }
                Step("HcsCreateComputeSystem(hosted)", hr, doc ?? "");
                if (hr.Failed)
                {
                    return 2;
                }
                hostedCreated = true;

                int execResult;
                using (hosted)
                {
                    hr = PInvoke.HcsStartComputeSystem(hosted, op.Handle, null);
                    if (hr.Succeeded)
                    {
                        (hr, doc) = op.Wait(60_000);
                    }
                    Step("HcsStartComputeSystem(hosted)", hr, $"{doc ?? ""} +{swTotal.ElapsedMilliseconds}ms cold-to-container-running".Trim());
                    if (hr.Failed)
                    {
                        return 2;
                    }

                    hr = PInvoke.HcsGetComputeSystemProperties(hosted, op.Handle, "{}");
                    if (hr.Succeeded)
                    {
                        (hr, doc) = op.Wait();
                    }
                    Step("HcsGetComputeSystemProperties(hosted)", hr, doc ?? "");
                    ProveHyperVIsolation(hr, doc, uvmId);

                    hr = PInvoke.HcsGetComputeSystemProperties(uvm, op.Handle, "{}");
                    string? uvmDoc = null;
                    if (hr.Succeeded)
                    {
                        (hr, uvmDoc) = op.Wait();
                    }
                    Step("HcsGetComputeSystemProperties(uvm)", hr, uvmDoc ?? "");
                    ProveUvmIsVirtualMachine(hr, uvmDoc);

                    if (orphan)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Xenon '{containerId}' (utility VM '{uvmId}') is running. Exiting abruptly WITHOUT " +
                                          $"terminate/close (ShouldTerminateOnLastHandleClosed test for the hosted container AND its UVM). " +
                                          $"Run 'list --absent {containerId}' and 'list --absent {uvmId}' next, then " +
                                          $"'cleanup --isolation hyperv --work {workDir}' to release the sandbox layer and UVM scratch.");
                        PrintSummary();
                        Environment.Exit(99);
                    }

                    execResult = Exec(hosted, command, budgetSeconds);
                    Step("XenonLatency", default, $"cold-to-exec-complete {swTotal.ElapsedMilliseconds}ms (argon reference: ~instant silo start)");

                    hr = PInvoke.HcsTerminateComputeSystem(hosted, op.Handle, null);
                    if (hr.Succeeded)
                    {
                        (hr, doc) = op.Wait();
                    }
                    Step("HcsTerminateComputeSystem(hosted)", hr, doc ?? "");
                }

                hr = PInvoke.HcsTerminateComputeSystem(uvm, op.Handle, null);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait();
                }
                Step("HcsTerminateComputeSystem(uvm)", hr, doc ?? "");

                return execResult;
            }
        }
        finally
        {
            // No UnprepareLayer/DeactivateLayer: the xenon scratch was never
            // host-prepared. DestroyLayer removes the sandbox dir + VHDX.
            Step("DestroyLayer", WcLayer.Destroy(sandboxPath), sandboxPath);
            Step("SandboxRemovedProbe", Directory.Exists(sandboxPath) ? ProbeFailed : default,
                Directory.Exists(sandboxPath) ? $"{sandboxPath} still exists" : "sandbox directory removed");
            RemoveUvmScratchDir(uvmScratchDir);
            if (hostedCreated)
            {
                ProbeComputeSystemGone(containerId);
            }
            if (uvmCreated)
            {
                ProbeComputeSystemGone(uvmId);
            }
        }
    }

    /// <summary>The negative side of the argon ObRoot discriminator (PR #31 left
    /// it one-sided): a xenon must report SystemType Container with NO host-silo
    /// ObRoot — its silo lives inside the utility VM, not on the host.</summary>
    private static void ProveHyperVIsolation(HRESULT propertiesHr, string? propertiesDoc, string uvmId)
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
                string? hostingSystemId = (string?)props?["HostingSystemId"];
                proved = string.Equals(systemType, "Container", StringComparison.OrdinalIgnoreCase)
                    && obRoot is null;
                detail = $"SystemType={systemType ?? "(null)"} ObRoot={obRoot ?? "(absent)"} " +
                         $"HostingSystemId={hostingSystemId ?? "(absent)"} (expected {uvmId})";
            }
            catch (JsonException ex)
            {
                detail = $"unparseable properties document: {ex.Message}";
            }
        }
        Step("HyperVIsolationProof(properties)", proved ? default : ProbeFailed, detail);
    }

    private static void ProveUvmIsVirtualMachine(HRESULT propertiesHr, string? propertiesDoc)
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
                string? runtimeId = (string?)props?["RuntimeId"];
                proved = string.Equals(systemType, "VirtualMachine", StringComparison.OrdinalIgnoreCase);
                detail = $"SystemType={systemType ?? "(null)"} RuntimeId={runtimeId ?? "(absent)"}";
            }
            catch (JsonException ex)
            {
                detail = $"unparseable properties document: {ex.Message}";
            }
        }
        Step("UvmIsVirtualMachineProof(properties)", proved ? default : ProbeFailed, detail);
    }

    private static (HRESULT Hr, string? Doc) Modify(HcsCloseComputeSystemSafeHandle system, string settingsDocument)
    {
        using var op = new HcsOperation();
        HRESULT hr = PInvoke.HcsModifyComputeSystem(system, op.Handle, settingsDocument, null);
        return hr.Succeeded ? op.Wait() : (hr, null);
    }

    /// <summary>Retries a modification until it succeeds or the budget runs out,
    /// recording the first failure HRESULT so the summary shows what actually
    /// gated guest readiness (there is no documented "GCS ready" signal).</summary>
    private static (HRESULT Hr, string? Doc, string RetryDetail) ModifyWithRetry(
        HcsCloseComputeSystemSafeHandle system, string settingsDocument, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        HRESULT firstFailure = default;
        int attempts = 0;
        while (true)
        {
            attempts++;
            (HRESULT hr, string? doc) = Modify(system, settingsDocument);
            if (hr.Succeeded || sw.ElapsedMilliseconds >= timeoutMs)
            {
                string detail = attempts == 1
                    ? "first attempt"
                    : $"attempts={attempts} first-failure=0x{(uint)firstFailure.Value:X8} waited={sw.ElapsedMilliseconds}ms";
                return (hr, doc, detail);
            }
            if (attempts == 1)
            {
                firstFailure = hr;
            }
            Thread.Sleep(1000);
        }
    }

    private static void PrecleanUvmScratch(string uvmScratchDir)
    {
        if (!Directory.Exists(uvmScratchDir))
        {
            return;
        }
        // Best-effort by design, like PrecleanSandbox: printed but not recorded,
        // so a leftover from a prior crash cannot fail this run's verdict.
        try
        {
            Directory.Delete(uvmScratchDir, recursive: true);
            Console.WriteLine($"[preclean] removed leftover UVM scratch at {uvmScratchDir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[preclean] leftover UVM scratch at {uvmScratchDir} not removable: {ex.Message}");
        }
    }

    private static void RemoveUvmScratchDir(string uvmScratchDir)
    {
        HRESULT hr = default;
        string detail;
        if (!Directory.Exists(uvmScratchDir))
        {
            detail = "already absent";
        }
        else
        {
            try
            {
                Directory.Delete(uvmScratchDir, recursive: true);
                detail = $"{uvmScratchDir} removed";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hr = ProbeFailed;
                detail = $"{uvmScratchDir}: {ex.GetType().Name}: {ex.Message}";
            }
        }
        Step("UvmScratchRemoved", hr, detail);
    }

    private static JsonObject SchemaV21() => new() { ["Major"] = 2, ["Minor"] = 1 };

    // hcsshim DefaultVSMBOptions(readOnly: true) + TakeBackupPrivilege, the
    // options used both for the UVM's "os" share and for container layers.
    private static JsonObject ReadOnlyVsmbOptions() => new()
    {
        ["ReadOnly"] = true,
        ["ShareRead"] = true,
        ["CacheIo"] = true,
        ["PseudoOplocks"] = true,
        ["TakeBackupPrivilege"] = true,
    };

    private static string BuildUvmConfig(string uvmLayerPath, string uvmScratchVhdx) => new JsonObject
    {
        ["SchemaVersion"] = SchemaV21(),
        ["Owner"] = "AspireHcs",
        ["ShouldTerminateOnLastHandleClosed"] = true,
        ["VirtualMachine"] = new JsonObject
        {
            ["StopOnReset"] = true,
            ["Chipset"] = new JsonObject
            {
                ["Uefi"] = new JsonObject
                {
                    ["BootThis"] = new JsonObject
                    {
                        ["DevicePath"] = @"\EFI\Microsoft\Boot\bootmgfw.efi",
                        ["DeviceType"] = "VmbFs",
                    },
                },
            },
            ["ComputeTopology"] = new JsonObject
            {
                ["Memory"] = new JsonObject
                {
                    ["SizeInMB"] = 1024,
                    ["AllowOvercommit"] = true,
                    ["EnableHotHint"] = true,
                },
                ["Processor"] = new JsonObject { ["Count"] = 2 },
            },
            // Empty object = "HCS, own the guest-services connection yourself".
            // Without this section HCS has no bridge to forward GuestRequest
            // modifications or HostedSystem creates to.
            ["GuestConnection"] = new JsonObject(),
            ["Devices"] = new JsonObject
            {
                ["VirtualSmb"] = new JsonObject
                {
                    ["DirectFileMappingInMB"] = 1024,
                    ["Shares"] = new JsonArray(new JsonObject
                    {
                        ["Name"] = "os",
                        ["Path"] = Path.Combine(uvmLayerPath, "UtilityVM", "Files"),
                        ["Options"] = ReadOnlyVsmbOptions(),
                    }),
                },
                ["Scsi"] = new JsonObject
                {
                    [ScsiController0] = new JsonObject
                    {
                        ["Attachments"] = new JsonObject
                        {
                            ["0"] = new JsonObject
                            {
                                ["Path"] = uvmScratchVhdx,
                                ["Type"] = "VirtualDisk",
                            },
                        },
                    },
                },
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string BuildHostedContainerConfig(string uvmId, IReadOnlyList<(Guid Id, string GuestPath)> guestLayers) => new JsonObject
    {
        ["Owner"] = "AspireHcs",
        ["SchemaVersion"] = SchemaV21(),
        ["HostingSystemId"] = uvmId,
        ["ShouldTerminateOnLastHandleClosed"] = true,
        ["HostedSystem"] = new JsonObject
        {
            ["SchemaVersion"] = SchemaV21(),
            ["Container"] = new JsonObject
            {
                ["Storage"] = new JsonObject
                {
                    ["Layers"] = new JsonArray([.. guestLayers.Select(l => (JsonNode)new JsonObject
                    {
                        ["Id"] = l.Id.ToString(),
                        ["Path"] = l.GuestPath,
                    })]),
                    ["Path"] = GuestScratchPath,
                },
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string VsmbAddDoc(string shareName, string layerPath) => new JsonObject
    {
        ["ResourcePath"] = "VirtualMachine/Devices/VirtualSmb/Shares",
        ["RequestType"] = "Add",
        ["Settings"] = new JsonObject
        {
            ["Name"] = shareName,
            ["Path"] = layerPath,
            ["Options"] = ReadOnlyVsmbOptions(),
        },
    }.ToJsonString();

    private static string ScsiAttachDoc(string vhdxPath) => new JsonObject
    {
        ["ResourcePath"] = $"VirtualMachine/Devices/Scsi/{ScsiController0}/Attachments/1",
        ["RequestType"] = "Add",
        ["Settings"] = new JsonObject
        {
            ["Path"] = vhdxPath,
            ["Type"] = "VirtualDisk",
        },
    }.ToJsonString();

    private static string GuestMountScratchDoc() => new JsonObject
    {
        ["GuestRequest"] = new JsonObject
        {
            ["ResourceType"] = "MappedVirtualDisk",
            ["RequestType"] = "Add",
            ["Settings"] = new JsonObject
            {
                ["ContainerPath"] = GuestScratchPath,
                ["Lun"] = 1,
            },
        },
    }.ToJsonString();

    private static string CombinedLayersDoc(IReadOnlyList<(Guid Id, string GuestPath)> guestLayers) => new JsonObject
    {
        ["GuestRequest"] = new JsonObject
        {
            ["ResourceType"] = "CombinedLayers",
            ["RequestType"] = "Add",
            ["Settings"] = new JsonObject
            {
                ["ContainerRootPath"] = GuestScratchPath,
                ["Layers"] = new JsonArray([.. guestLayers.Select(l => (JsonNode)new JsonObject
                {
                    ["Id"] = l.Id.ToString(),
                    ["Path"] = l.GuestPath,
                })]),
            },
        },
    }.ToJsonString();
}
