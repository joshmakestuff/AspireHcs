// Spike for issue #1: boot a VHDX via the Host Compute System API and record,
// per privilege level, the actual HRESULT of every HCS call. Modes:
//
//   run       --base <vhdx>   boot a differencing child of <vhdx>, probe for guest
//                             boot (serial console + HcsModifyComputeSystem), terminate
//   orphan    --base <vhdx>   boot, then exit abruptly WITHOUT terminating, to test
//                             ShouldTerminateOnLastHandleClosed
//   list                      enumerate HCS compute systems (verifies orphan teardown)
//   terminate [--id <id>]     open + terminate a leftover spike VM
//
// Options: --id <vmId> --memory <MB> --seconds <probe budget> --work <dir>

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Vhd;

namespace HcsBootSpike;

internal static class Program
{
    private const string DefaultVmId = "AspireHcsSpike";
    private const string ComPipeName = "aspirehcs-spike-com1";

    private static readonly List<(string Step, HRESULT Hr, string Detail)> Results = [];

    private static int Main(string[] args)
    {
        string mode = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
        string vmId = Opt(args, "--id") ?? DefaultVmId;

        PrintIdentity();

        try
        {
            return mode switch
            {
                "run" => Run(args, vmId, orphan: false),
                "orphan" => Run(args, vmId, orphan: true),
                "list" => List(),
                "terminate" => Terminate(vmId),
                _ => Usage(),
            };
        }
        finally
        {
            PrintSummary();
        }
    }

    private static int Run(string[] args, string vmId, bool orphan)
    {
        string basePath = Opt(args, "--base")
            ?? throw new ArgumentException("--base <path to base vhdx> is required");
        int memoryMb = int.TryParse(Opt(args, "--memory"), out int m) ? m : 2048;
        int probeSeconds = int.TryParse(Opt(args, "--seconds"), out int s) ? s : 120;
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsSpike");

        Directory.CreateDirectory(workDir);
        string diffPath = Path.Combine(workDir, $"{vmId}-diff.vhdx");
        if (File.Exists(diffPath))
        {
            File.Delete(diffPath);
        }

        Step("CreateVirtualDisk(differencing)", CreateDifferencingDisk(basePath, diffPath), diffPath);

        try
        {
            Step("HcsGrantVmAccess(diff)", PInvoke.HcsGrantVmAccess(vmId, diffPath), diffPath);
            Step("HcsGrantVmAccess(base)", PInvoke.HcsGrantVmAccess(vmId, basePath), basePath);

            string config = BuildVmConfig(diffPath, memoryMb);
            Console.WriteLine($"--- VM configuration document ---\n{config}\n---------------------------------");

            using var op = new HcsOperation();

            HRESULT hr = PInvoke.HcsCreateComputeSystem(vmId, config, op.Handle, null, out HcsCloseComputeSystemSafeHandle system);
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

            using (system)
            {
                using var serialCts = new CancellationTokenSource();
                Task<long> serialTask = Task.Run(() => ReadSerialAsync(serialCts.Token));

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

                hr = PInvoke.HcsGetComputeSystemProperties(system, op.Handle, """{"PropertyTypes":["Memory"]}""");
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait();
                }
                Step("HcsGetComputeSystemProperties", hr, doc ?? "");

                if (orphan)
                {
                    Console.WriteLine();
                    Console.WriteLine($"VM '{vmId}' is running. Exiting abruptly WITHOUT terminate/close " +
                                      "(ShouldTerminateOnLastHandleClosed test). Run 'list' next to verify the VM died.");
                    PrintSummary();
                    Environment.Exit(99);
                }

                // Guest-boot probe: per the official HCS Quick Start, HcsModifyComputeSystem
                // only succeeds once the guest OS has finished booting.
                string modifyDoc = $$"""
                    {
                        "ResourcePath": "VirtualMachine/ComputeTopology/Memory/SizeInMB",
                        "RequestType": "Update",
                        "Settings": {{memoryMb + 512}}
                    }
                    """;
                HRESULT probeHr = default;
                int attempts = 0;
                DateTime deadline = DateTime.UtcNow.AddSeconds(probeSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    attempts++;
                    using var modifyOp = new HcsOperation();
                    probeHr = PInvoke.HcsModifyComputeSystem(system, modifyOp.Handle, modifyDoc, null);
                    if (probeHr.Succeeded)
                    {
                        (probeHr, doc) = modifyOp.Wait();
                    }
                    Console.WriteLine($"[probe] attempt {attempts}: 0x{(uint)probeHr.Value:X8} {Truncate(doc)}");
                    if (probeHr.Succeeded)
                    {
                        break;
                    }
                    Thread.Sleep(5000);
                }
                Step("BootProbe(HcsModifyComputeSystem)", probeHr, $"{attempts} attempt(s)");

                serialCts.CancelAfter(2000);
                long serialBytes = serialTask.GetAwaiter().GetResult();
                Console.WriteLine($"[serial] {serialBytes} byte(s) received on COM1");

                hr = PInvoke.HcsTerminateComputeSystem(system, op.Handle, null);
                if (hr.Succeeded)
                {
                    (hr, doc) = op.Wait();
                }
                Step("HcsTerminateComputeSystem", hr, doc ?? "");

                return probeHr.Succeeded ? 0 : 3;
            }
        }
        finally
        {
            // The grants are persistent ACEs on the files (issue #16); a spike run must not
            // permanently mutate the operator's base image. Orphan mode never reaches this —
            // Environment.Exit simulates a crash, leaked grants included.
            Step("HcsRevokeVmAccess(diff)", PInvoke.HcsRevokeVmAccess(vmId, diffPath), diffPath);
            Step("HcsRevokeVmAccess(base)", PInvoke.HcsRevokeVmAccess(vmId, basePath), basePath);
        }
    }

    private static int List()
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
        return hr.Succeeded ? 0 : 2;
    }

    private static int Terminate(string vmId)
    {
        const uint GenericAll = 0x10000000;
        HRESULT hr = PInvoke.HcsOpenComputeSystem(vmId, GenericAll, out HcsCloseComputeSystemSafeHandle system);
        Step("HcsOpenComputeSystem", hr, vmId);
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

    private static string BuildVmConfig(string vhdxPath, int memoryMb) => new JsonObject
    {
        ["SchemaVersion"] = new JsonObject { ["Major"] = 2, ["Minor"] = 1 },
        ["Owner"] = "AspireHcs",
        ["ShouldTerminateOnLastHandleClosed"] = true,
        ["VirtualMachine"] = new JsonObject
        {
            ["Chipset"] = new JsonObject
            {
                ["Uefi"] = new JsonObject
                {
                    ["BootThis"] = new JsonObject
                    {
                        ["DevicePath"] = "Primary disk",
                        ["DiskNumber"] = 0,
                        ["DeviceType"] = "ScsiDrive",
                    },
                },
            },
            ["ComputeTopology"] = new JsonObject
            {
                ["Memory"] = new JsonObject { ["Backing"] = "Virtual", ["SizeInMB"] = memoryMb },
                ["Processor"] = new JsonObject { ["Count"] = 2 },
            },
            ["Devices"] = new JsonObject
            {
                ["Scsi"] = new JsonObject
                {
                    ["Primary disk"] = new JsonObject
                    {
                        ["Attachments"] = new JsonObject
                        {
                            ["0"] = new JsonObject { ["Type"] = "VirtualDisk", ["Path"] = vhdxPath },
                        },
                    },
                },
                ["ComPorts"] = new JsonObject
                {
                    ["0"] = new JsonObject { ["NamedPipe"] = @"\\.\pipe\" + ComPipeName },
                },
            },
        },
    }.ToJsonString(new() { WriteIndented = true });

    private static unsafe HRESULT CreateDifferencingDisk(string basePath, string diffPath)
    {
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
                out Microsoft.Win32.SafeHandles.SafeFileHandle handle);

            if (error == WIN32_ERROR.NO_ERROR)
            {
                handle.Dispose();
                return default;
            }
            return new HRESULT(unchecked((int)(0x80070000u | (uint)error)));
        }
    }

    private static async Task<long> ReadSerialAsync(CancellationToken ct)
    {
        long total = 0;
        try
        {
            using var pipe = new NamedPipeClientStream(".", ComPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            for (int attempt = 0; !pipe.IsConnected && attempt < 60 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await pipe.ConnectAsync(1000, ct);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    await Task.Delay(500, ct);
                }
            }
            if (!pipe.IsConnected)
            {
                Console.WriteLine("[serial] could not connect to COM1 pipe");
                return 0;
            }
            Console.WriteLine("[serial] connected to COM1 pipe");

            byte[] buffer = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                int read = await pipe.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }
                total += read;
                Console.Write(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[serial] error: {ex.Message}");
        }
        return total;
    }

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

    private static int Usage()
    {
        Console.WriteLine("""
            usage: HcsBootSpike <run|orphan|list|terminate> [options]
              run       --base <vhdx> [--id <vmId>] [--memory <MB>] [--seconds <n>] [--work <dir>]
              orphan    --base <vhdx> ...   boot then exit without terminating
              list                          enumerate HCS compute systems
              terminate [--id <vmId>]       terminate a leftover spike VM
            """);
        return 64;
    }
}

/// <summary>RAII wrapper for an HCS operation handle (the tutorial's unique_hcs_operation).</summary>
internal sealed unsafe class HcsOperation : IDisposable
{
    public HcsCloseOperationSafeHandle Handle { get; } = PInvoke.HcsCreateOperation_SafeHandle(null, null);

    public (HRESULT Hr, string? Doc) Wait(uint timeoutMs = 30_000)
    {
        HRESULT hr = PInvoke.HcsWaitForOperationResult(Handle, timeoutMs, out PWSTR doc);
        string? text = doc.Value == null ? null : doc.ToString();
        if (doc.Value != null)
        {
            PInvoke.LocalFree(new HLOCAL(doc.Value));
        }
        return (hr, text);
    }

    public void Dispose() => Handle.Dispose();
}
