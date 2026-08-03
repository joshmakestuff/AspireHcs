// Issue #5 investigation, not an assertion of behaviour: WaitForGuestReadyAsync's idempotent
// memory-resize probe returned in ~1.4 ms and never gated on the guest. Modes:
//
//   run            boot one VM and race candidate readiness probes against two ground truths —
//                  the DHCP lease and a TCP answer — logging the elapsed time at which each
//                  first succeeds, and dumping property documents whenever they change so the
//                  guest-visible transition (if any) is identifiable. Costs a full boot.
//                  Options: --base <vhdx> (default: HCS_TEST_VHDX), --memory <MB>, --minutes <N>
//   address-shape  no VM needed. The hvsocket probe returns WSAEINVAL (10022) against a booting
//                  VM from the first millisecond and never changes, which could mean the
//                  SOCKADDR_HV we build is malformed, or that the service GUID must be registered
//                  under HKLM\...\Virtualization\GuestCommunicationServices. This discriminates
//                  the two: connect over HV_GUID_LOOPBACK to a service that IS registered in-box,
//                  and to one that is not. Different errors mean the address shape is fine and
//                  registration is the gate. Identical WSAEINVAL means our struct is wrong.
//
// Exit codes: 0 = ran, and for `run` the recorded finding still holds; 1 = a finding check
// failed; 2 = usage.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AspireHcs.Hcn;
using AspireHcs.Hcs;
using AspireHcs.Hcs.Schema;
using AspireHcs.Storage;

namespace GuestReadinessProbeSpike;

internal static class Program
{
    private static readonly Guid Loopback = new("e0e16197-dd56-4a10-9195-5ee7a155a838");

    // In-box, present in the registry on the reference host.
    private static readonly Guid VmSessionService = new("999E53D4-3D5C-4C3E-8779-BED06EC056E1");

    private static async Task<int> Main(string[] args)
    {
        string mode = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
        return mode switch
        {
            "run" => await RunAsync(args),
            "address-shape" => await AddressShapeAsync(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("usage: GuestReadinessProbeSpike run [--base <vhdx>] [--memory <MB>] [--minutes <N>]");
        Console.WriteLine("       GuestReadinessProbeSpike address-shape");
        Console.WriteLine("run boots a VM from <vhdx> — a bootable Gen2/UEFI image; defaults to HCS_TEST_VHDX.");
        return 2;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? baseVhdx = Opt(args, "--base") ?? Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
        if (string.IsNullOrEmpty(baseVhdx))
        {
            return Usage();
        }

        int memoryMb = int.TryParse(Opt(args, "--memory"), out int m) ? m : 2048;
        int windowMinutes = int.TryParse(Opt(args, "--minutes"), out int w) ? w : 3;

        Guid networkId = HcnClient.FindIcsNetworkId();
        string vmId = $"AspireHcsProbe-{Guid.NewGuid():N}";
        Guid endpointId = Guid.NewGuid();
        string mac = $"02-15-5D-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}";
        string workDir = Path.Combine(Path.GetTempPath(), "AspireHcsProbe", Guid.NewGuid().ToString("N"));
        HcnClient.CreateDhcpEndpoint(networkId, endpointId, mac, owner: "GuestReadinessProbeSpike");

        try
        {
            Directory.CreateDirectory(workDir);
            string diffPath = Path.Combine(workDir, "boot-diff.vhdx");
            VirtualDisk.CreateDifferencing(baseVhdx, diffPath);
            HcsClient.GrantVmAccess(vmId, diffPath);
            HcsClient.GrantVmAccess(vmId, baseVhdx);

            ComputeSystemDocument document = BuildDocument(diffPath, endpointId, mac, memoryMb);
            using HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync(vmId, document);
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                await vm.StartAsync();
                Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] HcsStartComputeSystem returned");

                // The VmId half of SOCKADDR_HV is the compute system's runtime id, which HCS
                // hands us without any guest involvement.
                string? runtime = await vm.GetPropertiesAsync("{}");
                Guid vmRuntimeId = Guid.Parse(JsonNode.Parse(runtime!)!["RuntimeId"]!.GetValue<string>());
                Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] RuntimeId {vmRuntimeId} (hvsocket VmId)");

                Dictionary<string, long> firstSuccess = [];
                Dictionary<string, string> lastDocument = [];
                string? leasedIp = null;
                bool tcpAnswered = false;
                string? tcpOutcome = null;

                // Run until every signal has answered, TCP included. An earlier version stopped as
                // soon as the lease appeared, giving TCP exactly one attempt fired in the same
                // iteration the lease landed.
                //
                // KNOWN DIVERGENCE, image-dependent: on the image this was first run against,
                // even with ~47 attempts over 165 s, every connect here timed out (dropped SYN),
                // while the product path got an instant ConnectionRefused from the same image at
                // the same kind of address — see HealthCheckGatesReadinessTests, which reaches
                // the guest and reads the refusal off the health report. Never bisected. It is
                // not universal: against winserver2025-core (2026-08-03, this spike), TCP
                // connected to the guest's sshd at 5.6 s. The balloon measurements below are
                // unaffected either way.
                while (clock.Elapsed < TimeSpan.FromMinutes(windowMinutes)
                    && !(leasedIp is not null && tcpAnswered && firstSuccess.ContainsKey("modify:memory-grown")))
                {
                    // Candidate 1: the pre-#5 probe — an idempotent resize to the configured size.
                    await ProbeAsync("modify:memory-same", firstSuccess, clock, () =>
                        vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{memoryMb}}}
                            """));

                    // Candidate 2: a resize that actually changes the balloon, which requires
                    // hv_balloon in the guest rather than just the VM worker. Grow rather than
                    // shrink — an earlier run shrank the guest into memory pressure it never
                    // recovered from — and restore the configured size as soon as it lands, so
                    // the measurement does not perturb the rest of the boot.
                    await ProbeAsync("modify:memory-grown", firstSuccess, clock, async () =>
                    {
                        await vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{memoryMb + 256}}}
                            """);
                        await vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{memoryMb}}}
                            """);
                    });

                    // Candidates 3-4: property documents. Success is not interesting on its own
                    // (they answer immediately); the content transition is.
                    await SampleAsync("properties:all", vm, "{}", lastDocument, clock);
                    await SampleAsync("properties:memory", vm, """{"PropertyTypes":["Memory"]}""", lastDocument, clock);
                    await SampleAsync("properties:guestconnection", vm, """{"PropertyTypes":["GuestConnection"]}""", lastDocument, clock);

                    // Candidate 5: host-side hvsocket connect to a port nothing listens on. If the
                    // failure mode changes as the guest's VMBus/hvsocket transport comes up — say
                    // unreachable while booting, then refused once it is up — that is a read-only
                    // guest-readiness signal, with no memory ballooning side effect.
                    string hv = await HvSocketProbe.TryConnectAsync(vmRuntimeId, port: 2761, TimeSpan.FromSeconds(1));
                    if (!lastDocument.TryGetValue("hvsocket:2761", out string? previousHv) || previousHv != hv)
                    {
                        lastDocument["hvsocket:2761"] = hv;
                        Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] hvsocket:2761 → {hv}");
                    }

                    // Ground truth 1: the guest completed a DHCP handshake.
                    if (leasedIp is null)
                    {
                        string? props = HcnClient.QueryEndpointProperties(endpointId);
                        leasedIp = props is null ? null : JsonNode.Parse(props)?["IPAddress"]?.GetValue<string>();
                        if (leasedIp is not null)
                        {
                            Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] GROUND TRUTH: DHCP lease {leasedIp}");
                        }
                    }

                    // Ground truth 2: the guest's TCP stack answered. A refused SYN counts as an
                    // answer — it proves the stack is up — but the two outcomes are recorded
                    // separately, because only "connected" means a health check that demands a
                    // real listener can ever go healthy on this image.
                    if (leasedIp is not null && !tcpAnswered)
                    {
                        using TcpClient client = new();
                        try
                        {
                            await client.ConnectAsync(IPAddress.Parse(leasedIp), 22).WaitAsync(TimeSpan.FromSeconds(2));
                            tcpOutcome = "connected (a listener accepted)";
                            tcpAnswered = true;
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                        {
                            tcpOutcome = "refused (stack up, nothing listening)";
                            tcpAnswered = true;
                        }
                        catch (Exception ex)
                        {
                            // Report the reason rather than swallowing it: the round-trip test gets
                            // a clean refusal at the same kind of address via the same HCN path,
                            // and a bare catch here is why that difference stayed unexplained.
                            string reason = ex is SocketException se ? $"{se.SocketErrorCode} (native {se.NativeErrorCode})" : ex.GetType().Name;
                            if (!lastDocument.TryGetValue("tcp:22", out string? previousTcp) || previousTcp != reason)
                            {
                                lastDocument["tcp:22"] = reason;
                                Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] tcp:22 → {reason}");
                            }
                        }

                        if (tcpAnswered)
                        {
                            firstSuccess["tcp:22"] = clock.ElapsedMilliseconds;
                            Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] GROUND TRUTH: TCP {tcpOutcome} at {leasedIp}:22");
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }

                Console.WriteLine("");
                Console.WriteLine("=== first success, by probe ===");
                foreach ((string name, long ms) in firstSuccess.OrderBy(p => p.Value))
                {
                    Console.WriteLine($"{ms,7} ms  {name}");
                }
                Console.WriteLine($"TCP on 22: {tcpOutcome ?? "never answered within the window"}");

                int failures = 0;
                void Check(bool holds, string claim)
                {
                    Console.WriteLine($"{(holds ? "PASS" : "FAIL")}  {claim}");
                    if (!holds)
                    {
                        failures++;
                    }
                }

                // The finding this exists to protect. A probe that answers instantly is measuring
                // the VM worker, not the guest: the idempotent resize AspireHcs shipped returns in
                // ~40 ms, while a resize that actually moves the balloon is refused with
                // ERROR_NOT_READY until the guest's integration drivers load (~9.3 s on the Kali
                // image, reproducible across runs). If this ever drops to near-zero, the signal has
                // stopped being guest-gated and WaitForGuestReadyAsync is lying again.
                bool balloonAnswered = firstSuccess.TryGetValue("modify:memory-grown", out long balloonMs);
                Check(balloonAnswered, "the guest-gated memory probe succeeded");
                if (balloonAnswered)
                {
                    Check(balloonMs > 1_000,
                        $"the guest-gated memory probe answered in {balloonMs} ms — slow enough to have involved the guest");
                    Check(firstSuccess["modify:memory-same"] < balloonMs,
                        "the idempotent resize answered before the guest was up; it is not a readiness signal");
                }

                return failures == 0 ? 0 : 1;
            }
            finally
            {
                await vm.TerminateAsync();
            }
        }
        finally
        {
            HcnClient.DeleteEndpoint(endpointId);
            // The grants are persistent ACEs on the files; without these the base image
            // accumulates one dead VM identity per run (#16).
            HcsClient.RevokeVmAccess(vmId, Path.Combine(workDir, "boot-diff.vhdx"));
            HcsClient.RevokeVmAccess(vmId, baseVhdx);

            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<int> AddressShapeAsync()
    {
        string registered = await HvSocketProbe.TryConnectRawAsync(Loopback, VmSessionService, TimeSpan.FromSeconds(2));
        string unregistered = await HvSocketProbe.TryConnectRawAsync(
            Loopback, HvSocketProbe.LinuxVSockServiceId(2761), TimeSpan.FromSeconds(2));

        Console.WriteLine($"loopback + registered   (VM Session Service): {registered}");
        Console.WriteLine($"loopback + unregistered (Linux VSOCK 2761)  : {unregistered}");
        Console.WriteLine(registered == unregistered
            ? "SAME -> inconclusive; the address shape itself is suspect."
            : "DIFFERENT -> the address shape is accepted; registration is the gate.");
        return 0;
    }

    private static async Task ProbeAsync(string name, Dictionary<string, long> firstSuccess, Stopwatch clock, Func<Task> probe)
    {
        if (firstSuccess.ContainsKey(name))
        {
            return;
        }

        try
        {
            await probe();
            firstSuccess[name] = clock.ElapsedMilliseconds;
            Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name}: first success");
        }
        catch (HcsException ex)
        {
            Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name}: 0x{ex.HResult:X8}");
        }
    }

    private static async Task SampleAsync(string name, HcsComputeSystem vm, string query, Dictionary<string, string> last, Stopwatch clock)
    {
        string current;
        try
        {
            current = await vm.GetPropertiesAsync(query) ?? "(null)";
        }
        catch (HcsException ex)
        {
            current = $"0x{ex.HResult:X8}";
        }

        if (last.TryGetValue(name, out string? previous) && previous == current)
        {
            return;
        }

        last[name] = current;
        Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name} changed: {Truncate(current)}");
    }

    private static string Truncate(string value) =>
        value.Length <= 600 ? value : value[..600] + "…";

    private static ComputeSystemDocument BuildDocument(string vhdxPath, Guid endpointId, string mac, int memoryMb) => new()
    {
        SchemaVersion = new() { Major = 2, Minor = 5 },
        Owner = "GuestReadinessProbeSpike",
        ShouldTerminateOnLastHandleClosed = true,
        VirtualMachine = new()
        {
            Chipset = new() { Uefi = new() { BootThis = new() { DevicePath = "Primary disk", DiskNumber = 0 } } },
            ComputeTopology = new()
            {
                Memory = new() { SizeInMB = memoryMb },
                Processor = new() { Count = 2 },
            },
            Devices = new()
            {
                Scsi = new()
                {
                    ["Primary disk"] = new() { Attachments = new() { ["0"] = new() { Path = vhdxPath } } },
                },
                NetworkAdapters = new()
                {
                    ["ext"] = new() { EndpointId = endpointId.ToString(), MacAddress = mac },
                },
            },
            Services = new() { Shutdown = new() },
        },
    };

    private static string? Opt(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
