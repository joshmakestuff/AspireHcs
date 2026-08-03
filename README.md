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
6. [x] Reproducible Windows guest base image builder — ISO to sealed, CoW-friendly VHDX ([#11](https://github.com/joshmakestuff/AspireHcs/issues/11), `tools/guest-images/windows`).

Open VM-side work: individualized guests for domain scenarios ([#23](https://github.com/joshmakestuff/AspireHcs/issues/23)), connect-to-VM UX ([#26](https://github.com/joshmakestuff/AspireHcs/issues/26)), guest OS logs to OTel ([#27](https://github.com/joshmakestuff/AspireHcs/issues/27)), a Linux image builder ([#28](https://github.com/joshmakestuff/AspireHcs/issues/28)), and tighter networking integration ([#29](https://github.com/joshmakestuff/AspireHcs/issues/29)).

### Windows containers (exploratory)

Aspire's container story is Docker/Podman via DCP, and Windows containers are not on its roadmap — but HCS runs them through the same compute-system surface this repo already wraps. Tracked as a roadmap item in [#30](https://github.com/joshmakestuff/AspireHcs/issues/30); no commitment yet.

- [x] **Argon spike** ([#31](https://github.com/joshmakestuff/AspireHcs/pull/31)): process-isolated container booted from a hand-materialized windowsfilter layer directory, `cmd /c ver` exec'd inside it, crash-safe teardown verified. Docker is not involved at runtime.
- [x] **Xenon spike** ([#32](https://github.com/joshmakestuff/AspireHcs/issues/32), [#34](https://github.com/joshmakestuff/AspireHcs/pull/34)): Hyper-V-isolated boot of the same layer directory. A xenon is *two* compute systems (no inline UtilityVM section exists in v2) — a utility VM booted from the `UtilityVM` directory that ships inside every base layer, plus a hosted container referencing it by `HostingSystemId`. Cold-to-exec ~6.5 s vs argon's ~instant.
- [x] **Privilege model** ([#33](https://github.com/joshmakestuff/AspireHcs/issues/33)): a Hyper-V-isolated container **boots completely unelevated** with Hyper-V Administrators membership alone — the same prerequisite the VM path already documents. The #30 `E_ACCESSDENIED` on `CreateSandboxLayer` was Docker's store ACL, not the API; the call succeeds unelevated against a readable layer. Process isolation is still gated, at `ActivateLayer` (`0x80070522 ERROR_PRIVILEGE_NOT_HELD` — a privilege, not an ACL). Full per-call results: [docs/container-privilege-matrix.md](docs/container-privilege-matrix.md).
- [x] **Image acquisition** ([#30](https://github.com/joshmakestuff/AspireHcs/issues/30)): **Docker is gone from the picture entirely.** `pull` + `import` acquire a base image from an anonymous registry into a store AspireHcs owns, via the OCI-tar path (base layers cannot travel through `ExportLayer` at all), and it boots: `nanoserver:ltsc2025` pulled, materialized and run as a Hyper-V-isolated container **unelevated, from our own store, with no ACL surgery** — `cmd /c ver` reporting `10.0.26100.33158` from inside, ~2.1 s cold-to-exec. A full-fidelity import needs elevation — `SeBackupPrivilege`/`SeRestorePrivilege` for the extraction that replays security descriptors, and again for `ProcessBaseImage` at finalize. What the run pins down is that those are *two independent gates*: with `--no-security`, extracting all 10 288 entries takes 5.2 s at **no privilege at all**, and finalize still fails `0x80070522` on its own. So only the acquisition step needs a prompt; running the resulting container does not. Full record: [docs/image-acquisition.md](docs/image-acquisition.md).
- [x] **Build-match constraint lifted under Hyper-V isolation** — previously carried here as *untested, not true*. A Windows Server 2022 guest (build 20348.5386) boots on this build 26200 host and prints its own version from inside the container, with `HostingSystemId` naming our utility VM so it is a genuine xenon. One mismatched pair is not a support matrix, but it falsifies the claim that the constraint applies to Hyper-V-isolated containers.

Spike code lives under `spikes/` and is not part of the shipped package.

## Requirements

- Windows 10 1809 / Windows Server 2019 or later, Hyper-V feature enabled.
- Membership in **Hyper-V Administrators** — sufficient for the VM path; elevation is not required ([#1](https://github.com/joshmakestuff/AspireHcs/issues/1)). The same membership is also sufficient to boot a **Hyper-V-isolated container** unelevated ([#33](https://github.com/joshmakestuff/AspireHcs/issues/33)), given a readable layer store; process-isolated containers additionally need a privilege that Hyper-V Administrators does not confer ([details](docs/container-privilege-matrix.md)).
- Windows-only by nature; the package fails fast on other platforms.
