# AspireHcs

An experimental [Aspire](https://aspire.dev) hosting integration built on the Windows [Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview), starting with **Hyper-V virtual machines as Aspire resources**.

The goal is a normal "Aspire feel" for VMs in the local dev loop:

```csharp
var vm = builder.AddHcsVm("appliance")
    .WithVhdx(@"d:\images\appliance.vhdx", copyOnWrite: true)
    .WithMemory(gigabytes: 4)
    .WithProcessorCount(2)
    .WithNatNetwork()
    .WithEndpoint(name: "api", targetPort: 8080);

builder.AddProject<Projects.Web>("web")
    .WithReference(vm.GetEndpoint("api"))
    .WaitFor(vm);
```

## Design

- **HCS, not WMI**: HCS compute systems are ephemeral — created on `aspire run`, destroyed on exit — which matches Aspire's container-like dev-loop semantics. `ShouldTerminateOnLastHandleClosed` gives crash-safe teardown (no orphaned VMs after a killed debug session).
- **Custom resource, not `ExecutableResource`**: an `HcsVirtualMachineResource` (`IResource` + `IResourceWithEndpoints`) driven by Aspire's eventing pipeline (`InitializeResourceEvent` → `ResourceReadyEvent`), publishing state via `ResourceNotificationService` and streaming the VM serial console to the dashboard via `ResourceLoggerService`.
- **Networking via HCN**: NAT network + endpoint from the Host Compute Network API, surfaced as non-proxied Aspire endpoints.
- **Interop**: CsWin32-generated P/Invoke for `ComputeCore.dll` / `ComputeNetwork.dll`; HCS JSON schema (v2.x) config documents.

## Status / roadmap

### Virtual machines (the shipping path)

1. [x] **Spike**: minimal console app that boots a VHDX via `HcsCreateComputeSystem`/`HcsStartComputeSystem`; verify whether Hyper-V Administrators membership suffices or full elevation is required. *(Result: it does — see [#1](https://github.com/joshmakestuff/AspireHcs/issues/1); elevation is not needed.)*
2. [x] Internal `HcsClient` (schema POCOs + CsWin32 bindings).
3. [x] Custom resource: lifecycle events, dashboard state, serial-console logs.
4. [x] HCN NAT networking + endpoint publishing, with run-scoped endpoint ownership so scavenging cannot race a live AppHost ([#13](https://github.com/joshmakestuff/AspireHcs/pull/13), [#19](https://github.com/joshmakestuff/AspireHcs/pull/19), [#21](https://github.com/joshmakestuff/AspireHcs/pull/21)).
5. [x] Polish: connection strings (`IResourceWithConnectionString`), `WithTcpHealthCheck`, copy-on-write diff disks (`WithVhdx(copyOnWrite: true)`), Start/Stop/Restart dashboard commands.
6. [x] Reproducible Windows guest base image builder — ISO to sealed, CoW-friendly VHDX ([#11](https://github.com/joshmakestuff/AspireHcs/issues/11)). Moved to hcsimgtool, which is now **archived**; where guest VM images come from next is [#23](https://github.com/joshmakestuff/AspireHcs/issues/23) / [#28](https://github.com/joshmakestuff/AspireHcs/issues/28), with Packer as the leading candidate. The sealed VHDX fixtures the integration tests consume are unaffected — they are supplied by path, not built here.
7. [x] Connect-to-VM UX ([#26](https://github.com/joshmakestuff/AspireHcs/issues/26)): `WithSshCommand` / `WithRdpCommand` add a **Connect** button to the resource in the dashboard, live only once the guest is running *and* its lease has surfaced. The client launches host-side — in run mode the AppHost and the browser share a machine, so the guest cooperates with nothing beyond serving SSH or RDP. Proven live against the Server 2025 fixture: the command line the product builds, `ssh.exe -p 22 -l Administrator <leased-ip>`, reaches authentication on the guest's sshd. RDP is **not** working end to end yet: the image builder now enables Remote Desktop and two images were built with it, both sealing with TermService observed listening on 3389 — but neither is reachable on that port from the host, while SSH on 22 from the same guest is. The live test retries for two minutes and every attempt times out rather than refusing, which is a dropped packet, not a closed port. Fix in the bootstrap, unproven until an image is built from it. Full record: [docs/connect-ux.md](docs/connect-ux.md).

Open VM-side work: individualized guests for domain scenarios ([#23](https://github.com/joshmakestuff/AspireHcs/issues/23)), guest OS logs to OTel ([#27](https://github.com/joshmakestuff/AspireHcs/issues/27)), a Linux image builder ([#28](https://github.com/joshmakestuff/AspireHcs/issues/28)), tighter networking integration ([#29](https://github.com/joshmakestuff/AspireHcs/issues/29)), and enabling Remote Desktop in the guest image ([#26](https://github.com/joshmakestuff/AspireHcs/issues/26)).

### Windows containers (exploratory)

Aspire's container story is Docker/Podman via DCP, and Windows containers are not on its roadmap — but HCS runs them through the same compute-system surface this repo already wraps. Tracked in [#30](https://github.com/joshmakestuff/AspireHcs/issues/30). Feasibility is settled by the spikes below; the resource itself is being built now ([#39](https://github.com/joshmakestuff/AspireHcs/issues/39)) and nothing container-side ships yet.

- [x] **Argon spike** ([#31](https://github.com/joshmakestuff/AspireHcs/pull/31)): process-isolated container booted from a hand-materialized windowsfilter layer directory, `cmd /c ver` exec'd inside it, crash-safe teardown verified. Docker is not involved at runtime.
- [x] **Xenon spike** ([#32](https://github.com/joshmakestuff/AspireHcs/issues/32), [#34](https://github.com/joshmakestuff/AspireHcs/pull/34)): Hyper-V-isolated boot of the same layer directory. A xenon is *two* compute systems (no inline UtilityVM section exists in v2) — a utility VM booted from the `UtilityVM` directory that ships inside every base layer, plus a hosted container referencing it by `HostingSystemId`. Cold-to-exec ~6.5 s vs argon's ~instant.
- [x] **Privilege model** ([#33](https://github.com/joshmakestuff/AspireHcs/issues/33), [#47](https://github.com/joshmakestuff/AspireHcs/issues/47)): a Hyper-V-isolated container boots with Hyper-V Administrators membership and no elevation. The #30 `E_ACCESSDENIED` on `CreateSandboxLayer` was Docker's store ACL, not the API; the call succeeds unelevated against a readable layer. **Process isolation is permanently out of scope**, and the reason moved: the gate is not `ActivateLayer` — that call wanted `SeManageVolumePrivilege`, which is grantable and survives UAC filtering, and it now succeeds unelevated. The blocker is one call downstream at `PrepareLayer`, which needs an enabled `BUILTIN\Administrators` SID — a group check no user-rights assignment satisfies in a filtered token, on a call that runs at **every container start**. Posture and full basis: [docs/containers.md](docs/containers.md).
- [x] **Image acquisition** ([#30](https://github.com/joshmakestuff/AspireHcs/issues/30)): **Docker is gone from the picture entirely.** `pull` + `import` acquire a base image from an anonymous registry into a store AspireHcs owns, via the OCI-tar path (base layers cannot travel through `ExportLayer` at all), and it boots: `nanoserver:ltsc2025` pulled, materialized and run as a Hyper-V-isolated container **unelevated, from our own store, with no ACL surgery** — `cmd /c ver` reporting `10.0.26100.33158` from inside, ~2.1 s cold-to-exec. A full-fidelity import needs elevation — `SeBackupPrivilege`/`SeRestorePrivilege` for the extraction that replays security descriptors, and again for `ProcessBaseImage` at finalize. What the run pins down is that those are *two independent gates*: with `--no-security`, extracting all 10 288 entries takes 5.2 s at **no privilege at all**, and finalize still fails `0x80070522` on its own. So only the acquisition step needs a prompt; running the resulting container does not — and that prompt is **inherent, not incidental**: finalize rewrites the UVM's BCD so it cannot be skipped, and UAC token filtering means no group membership (Backup Operators included — measured, "used for deny only") can give an unelevated session those privileges. Docker avoids it only by keeping a LocalSystem service permanently privileged. Image acquisition is [hcsctl](https://github.com/joshmakestuff/hcsctl)'s job now — `hcsctl image pull` + `hcsctl image import` — and the per-call elevation table lives in its [findings.md](https://github.com/joshmakestuff/hcsctl/blob/main/docs/findings.md#elevation).
- [x] **Build-match constraint lifted under Hyper-V isolation** — previously carried here as *untested, not true*. A Windows Server 2022 guest (build 20348.5386) boots on this build 26200 host and prints its own version from inside the container, with `HostingSystemId` naming our utility VM so it is a genuine xenon. One mismatched pair is not a support matrix, but it falsifies the claim that the constraint applies to Hyper-V-isolated containers.

**The container runtime is not written here.** As of 2026-08-07 ([#30](https://github.com/joshmakestuff/AspireHcs/issues/30)) AspireHcs drives
[hcsctl](https://github.com/joshmakestuff/hcsctl) — a CLI over HCS — rather than writing a second C# interop layer over the same API. hcsctl
already runs Hyper-V-isolated containers end to end, with environment, mounts, networking, scratch sizing, stats and lifecycle, and its
`docs/findings.md` carries the measurements. The seam is its `--json` contract: **exactly one document on stdout, progress on stderr, exit `0`
ran / `1` ran and failed / `64` bad arguments with nothing attempted.** AspireHcs supplies the Aspire half — resource types, the builder surface,
the eventing pipeline, ownership and scavenging — and `src/AspireHcs/Cli` is the only place that speaks to the tool.

The intended end state is that **all** HCS access goes through hcsctl, virtual machines included, at which point `src/AspireHcs/Hcs`,
`src/AspireHcs/Hcn` and the CsWin32 dependency retire ([hcsctl#34](https://github.com/joshmakestuff/hcsctl/issues/34)). The VM path migrates last;
it is the shipping one.

Image and disk preparation is hcsctl's too — the hcsimgtool extraction ([#43](https://github.com/joshmakestuff/AspireHcs/issues/43)) was
superseded the same week and that repo is archived. Multi-layer chains work: `servercore:ltsc2022` (2 layers) and
`dotnet/runtime:8.0-nanoserver-ltsc2022` (6) are materialized and readable unelevated, so the earlier single-layer limitation is gone.

Spike code under `spikes/` is not part of the shipped package. Setup and the xenon-only posture: [docs/containers.md](docs/containers.md).

## Requirements

- Windows 10 1809 / Windows Server 2019 or later, Hyper-V feature enabled.
- Membership in **Hyper-V Administrators** — sufficient for the VM path; elevation is not required ([#1](https://github.com/joshmakestuff/AspireHcs/issues/1)). The same membership boots a **Hyper-V-isolated container** unelevated ([#33](https://github.com/joshmakestuff/AspireHcs/issues/33)), given a readable layer store. Process isolation is out of scope permanently and is refused rather than attempted ([#46](https://github.com/joshmakestuff/AspireHcs/issues/46)).
- Windows-only by nature; the package fails fast on other platforms.
- **Containers additionally need `hcsctl`** and a store with the image already imported. Both are one-time setup: `./eng/Get-HcsCtl.ps1`, then `hcsctl image pull` and an elevated `hcsctl image import`. See [docs/containers.md](docs/containers.md).
