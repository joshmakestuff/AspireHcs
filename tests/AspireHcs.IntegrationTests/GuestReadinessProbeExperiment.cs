using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using AspireHcs.Hcn;
using AspireHcs.Hcs;
using AspireHcs.Hcs.Schema;
using AspireHcs.Storage;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #5 investigation, not an assertion of behaviour: WaitForGuestReadyAsync's idempotent
// memory-resize probe returns in ~1.4 ms and never gates on the guest. This boots one VM and
// races candidate probes against two ground truths — the DHCP lease and a TCP answer — logging
// the elapsed time at which each first succeeds, and dumping property documents whenever they
// change so the guest-visible transition (if any) is identifiable.
//
// Opt-in: it costs a full boot. Set ASPIREHCS_PROBE_EXPERIMENT=1 alongside HCS_TEST_VHDX.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class GuestReadinessProbeExperiment(ITestOutputHelper output) : IDisposable
{
    private const int MemoryMb = 2048;

    private static string? BaseVhdx => Environment.GetEnvironmentVariable("HCS_TEST_VHDX");

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "AspireHcsProbe", Guid.NewGuid().ToString("N"));

    [SkippableFact]
    public async Task Compare_candidate_guest_ready_probes_against_ground_truth()
    {
        Skip.If(string.IsNullOrEmpty(BaseVhdx), "Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX.");
        Skip.If(Environment.GetEnvironmentVariable("ASPIREHCS_PROBE_EXPERIMENT") != "1",
            "Set ASPIREHCS_PROBE_EXPERIMENT=1 to run the guest-readiness probe experiment (costs a full boot).");

        Guid networkId = HcnClient.FindIcsNetworkId();
        string vmId = $"AspireHcsProbe-{Guid.NewGuid():N}";
        Guid endpointId = Guid.NewGuid();
        string mac = $"02-15-5D-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}-{Random.Shared.Next(0x10, 0xFF):X2}";
        HcnClient.CreateDhcpEndpoint(networkId, endpointId, mac, owner: "AspireHcs.IntegrationTests");

        try
        {
            Directory.CreateDirectory(_workDir);
            string diffPath = Path.Combine(_workDir, "boot-diff.vhdx");
            VirtualDisk.CreateDifferencing(BaseVhdx!, diffPath);
            HcsClient.GrantVmAccess(vmId, diffPath);
            HcsClient.GrantVmAccess(vmId, BaseVhdx!);

            ComputeSystemDocument document = BuildDocument(diffPath, endpointId, mac);
            using HcsComputeSystem vm = await HcsClient.CreateComputeSystemAsync(vmId, document);
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                await vm.StartAsync();
                output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] HcsStartComputeSystem returned");

                // The VmId half of SOCKADDR_HV is the compute system's runtime id, which HCS
                // hands us without any guest involvement.
                string? runtime = await vm.GetPropertiesAsync("{}");
                Guid vmRuntimeId = Guid.Parse(JsonNode.Parse(runtime!)!["RuntimeId"]!.GetValue<string>());
                output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] RuntimeId {vmRuntimeId} (hvsocket VmId)");

                Dictionary<string, long> firstSuccess = [];
                Dictionary<string, string> lastDocument = [];
                string? leasedIp = null;
                bool tcpAnswered = false;
                string? tcpOutcome = null;

                // Run until every signal has answered, TCP included. An earlier version stopped as
                // soon as the lease appeared, giving TCP exactly one attempt fired in the same
                // iteration the lease landed.
                //
                // KNOWN DIVERGENCE, do not trust this harness's TCP result: even with ~47 attempts
                // over 165 s, every connect here times out (dropped SYN), while the product path
                // gets an instant ConnectionRefused from the same image at the same kind of
                // address — see HealthCheckGatesReadinessTests, which reaches the guest and reads
                // the refusal off the health report. Something about this harness's compute system
                // or endpoint differs; the document omits ComPorts and the endpoint owner differs,
                // neither yet bisected. The balloon measurements below are unaffected.
                while (clock.Elapsed < TimeSpan.FromMinutes(3)
                    && !(leasedIp is not null && tcpAnswered && firstSuccess.ContainsKey("modify:memory-grown")))
                {
                    // Candidate 1: today's probe — an idempotent resize to the configured size.
                    await ProbeAsync("modify:memory-same", firstSuccess, clock, () =>
                        vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{MemoryMb}}}
                            """));

                    // Candidate 2: a resize that actually changes the balloon, which requires
                    // hv_balloon in the guest rather than just the VM worker. Grow rather than
                    // shrink — an earlier run shrank the guest into memory pressure it never
                    // recovered from — and restore the configured size as soon as it lands, so
                    // the measurement does not perturb the rest of the boot.
                    await ProbeAsync("modify:memory-grown", firstSuccess, clock, async () =>
                    {
                        await vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{MemoryMb + 256}}}
                            """);
                        await vm.ModifyAsync($$"""
                            {"ResourcePath":"VirtualMachine/ComputeTopology/Memory/SizeInMB","RequestType":"Update","Settings":{{MemoryMb}}}
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
                        output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] hvsocket:2761 → {hv}");
                    }

                    // Ground truth 1: the guest completed a DHCP handshake.
                    if (leasedIp is null)
                    {
                        string? props = HcnClient.QueryEndpointProperties(endpointId);
                        leasedIp = props is null ? null : JsonNode.Parse(props)?["IPAddress"]?.GetValue<string>();
                        if (leasedIp is not null)
                        {
                            output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] GROUND TRUTH: DHCP lease {leasedIp}");
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
                                output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] tcp:22 → {reason}");
                            }
                        }

                        if (tcpAnswered)
                        {
                            firstSuccess["tcp:22"] = clock.ElapsedMilliseconds;
                            output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] GROUND TRUTH: TCP {tcpOutcome} at {leasedIp}:22");
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }

                output.WriteLine("");
                output.WriteLine("=== first success, by probe ===");
                foreach ((string name, long ms) in firstSuccess.OrderBy(p => p.Value))
                {
                    output.WriteLine($"{ms,7} ms  {name}");
                }
                output.WriteLine($"TCP on 22: {tcpOutcome ?? "never answered within the window"}");

                // The finding this exists to protect. A probe that answers instantly is measuring
                // the VM worker, not the guest: the idempotent resize AspireHcs shipped returns in
                // ~40 ms, while a resize that actually moves the balloon is refused with
                // ERROR_NOT_READY until the guest's integration drivers load (~9.3 s on the Kali
                // image, reproducible across runs). If this ever drops to near-zero, the signal has
                // stopped being guest-gated and WaitForGuestReadyAsync is lying again.
                Assert.True(firstSuccess.TryGetValue("modify:memory-grown", out long balloonMs),
                    "the guest-gated memory probe never succeeded");
                Assert.True(balloonMs > 1_000,
                    $"the guest-gated memory probe answered in {balloonMs} ms — too fast to have involved the guest");
                Assert.True(firstSuccess["modify:memory-same"] < balloonMs,
                    "expected the idempotent resize to answer before the guest was up; it is not a readiness signal");
            }
            finally
            {
                await vm.TerminateAsync();
            }
        }
        finally
        {
            HcnClient.DeleteEndpoint(endpointId);
            // The grants are persistent ACEs on the files; without these the runner's base image
            // accumulates one dead VM identity per test run (#16).
            HcsClient.RevokeVmAccess(vmId, Path.Combine(_workDir, "boot-diff.vhdx"));
            HcsClient.RevokeVmAccess(vmId, BaseVhdx!);
        }
    }

    private async Task ProbeAsync(string name, Dictionary<string, long> firstSuccess, Stopwatch clock, Func<Task> probe)
    {
        if (firstSuccess.ContainsKey(name))
        {
            return;
        }

        try
        {
            await probe();
            firstSuccess[name] = clock.ElapsedMilliseconds;
            output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name}: first success");
        }
        catch (HcsException ex)
        {
            output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name}: 0x{ex.HResult:X8}");
        }
    }

    private async Task SampleAsync(string name, HcsComputeSystem vm, string query, Dictionary<string, string> last, Stopwatch clock)
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
        output.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] {name} changed: {Truncate(current)}");
    }

    private static string Truncate(string value) =>
        value.Length <= 600 ? value : value[..600] + "…";

    private static ComputeSystemDocument BuildDocument(string vhdxPath, Guid endpointId, string mac) => new()
    {
        SchemaVersion = new() { Major = 2, Minor = 5 },
        Owner = "AspireHcs.IntegrationTests",
        ShouldTerminateOnLastHandleClosed = true,
        VirtualMachine = new()
        {
            Chipset = new() { Uefi = new() { BootThis = new() { DevicePath = "Primary disk", DiskNumber = 0 } } },
            ComputeTopology = new()
            {
                Memory = new() { SizeInMB = MemoryMb },
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

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
