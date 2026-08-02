# Container privilege matrix

Empirical answer to [#33](https://github.com/joshmakestuff/AspireHcs/issues/33): what a
developer actually needs in order to run a Windows container through HCS, and which calls
are genuinely privilege-gated.

**Every row below is a recorded HRESULT from a run on the reference host.** Nothing here is
inferred from documentation.

- Host: Windows 11 Enterprise 10.0.26200, Hyper-V enabled
- Account: a normal user in **Hyper-V Administrators**, running **unelevated**
- Layer: `nanoserver:ltsc2025` base layer (image build 10.0.26100.6584), materialized by a
  one-time `docker pull` into `C:\ProgramData\Docker\windowsfilter\aa2449ff…`
- Spike: `spikes/HcsContainerSpike`, commit `8a1fd13`+ on `container-privilege-model`
- Date: 2026-08-02

## Headline

| Isolation mode | Unelevated result |
|---|---|
| **Hyper-V-isolated (xenon)** | **Boots end to end. Zero privilege-gated storage calls.** |
| Process-isolated (argon) | Blocked at `ActivateLayer` — `0x80070522 ERROR_PRIVILEGE_NOT_HELD` |

Xenon is the mode that matters on developer machines — on client SKUs process isolation is
officially dev/test-only and version-locked, while Hyper-V isolation is the supported mode.
So the answer for AspireHcs is: **a container resource does not require an elevated AppHost.**

## The #30 finding was the store ACL, not the API

The [#30](https://github.com/joshmakestuff/AspireHcs/issues/30) spike recorded
`CreateSandboxLayer` failing `E_ACCESSDENIED` unelevated even with Hyper-V Administrators
membership, and flagged that it could not tell whether the gate was the wclayer API or the
ACL on Docker's store. **It was the ACL.**

With the layer made readable in place, the same call succeeds unelevated:

```
OK  legacy  CreateSandboxLayer  0x00000000
```

That finding is now retired. It should not be cited as evidence that layer storage requires
elevation.

## Storage-call matrix

Unelevated, Hyper-V Administrators, against a readable layer.
`SKIP` means the call was never attempted because a precondition failed — it is not a pass.

| Surface | Call | Result | HRESULT |
|---|---|---|---|
| store | `EnumerateLayerDir` | OK | `0x00000000` |
| store | `EnumerateFiles\` | OK | `0x00000000` |
| store | `ReadLayerChain` | OK | `0x00000000` |
| legacy | `LayerExists` | OK | `0x00000000` |
| both | `NameToGuid` | OK | `0x00000000` |
| legacy | `CreateSandboxLayer` | **OK** | `0x00000000` |
| legacy | `ActivateLayer` | **FAILED** | `0x80070522` ERROR_PRIVILEGE_NOT_HELD |
| legacy | `PrepareLayer` | SKIP / cascade | `0x80070037` |
| legacy | `GetLayerMountPath` | SKIP / cascade | `0x80070037` |
| legacy | `DeactivateLayer` | OK | `0x00000000` |
| legacy | `DestroyLayer` | OK | `0x00000000` |
| modern | `HcsInitializeWritableLayer` | FAILED | `0x80070003` |
| modern | `HcsAttachLayerStorageFilter` | SKIP | — |
| modern | `HcsDetachLayerStorageFilter` | SKIP | — |
| modern | `HcsDestroyLayer` | SKIP | — |
| xenon | `FindScratchTemplate` | OK | `0x00000000` |
| xenon | `CopyScratchTemplate` | OK | `0x00000000` |
| xenon | `HcsGrantVmAccess` | **OK** | `0x00000000` |

### `ActivateLayer` is a privilege, not an ACL

`0x80070522` is `ERROR_PRIVILEGE_NOT_HELD` — a *privilege* check, distinct from the
`E_ACCESSDENIED` an ACL produces. This is the real argon gate, and no amount of file-system
permission fixes it. Which specific privilege (`SeBackupPrivilege` / `SeRestorePrivilege` /
`SeSecurityPrivilege`) is **not yet identified** — the follow-up would be to enumerate the
token's privileges and try enabling each with `AdjustTokenPrivileges`, since a token can
enable a privilege it *holds but has disabled*.

`PrepareLayer` and `GetLayerMountPath` returning `0x80070037` afterwards are cascade
failures from the un-activated layer, not independent results.

### The modern surface diverges from legacy

`HcsInitializeWritableLayer` returned `0x80070003` (ERROR_PATH_NOT_FOUND) where legacy
`CreateSandboxLayer` succeeded against the same inputs. That is a **difference in call
shape, not a privilege result** — the modern API takes parent layers as a JSON `LayerData`
document rather than a descriptor array, and this spike has not yet established the correct
document for it. Do not read this row as "the modern surface needs more privilege"; it is
untested at this point, and that is the honest status.

## The xenon path needs no privileged storage call

Confirming #33's experiment-2 hypothesis. The host never Activates or Prepares a xenon
scratch — the guest consumes the VHDX — so the only storage work left is a file copy:

1. `blank-base.vhdx` ships **inside the base layer** (alongside `Files\` and `UtilityVM\`).
2. Copy it to the run's scratch directory — an ordinary unprivileged `File.Copy`.
3. `HcsGrantVmAccess` on the copy — succeeds unelevated.

Both scratch strategies boot unelevated, verified separately:

| Unelevated boot | Exit | Cold-to-exec |
|---|---|---|
| `--isolation hyperv` (CreateSandboxLayer) | 0 | 6673 ms |
| `--isolation hyperv --scratch template` (copied blank) | 0 | 6746 ms |
| `--isolation process` (argon) | 2 — `ActivateLayer` | — |

The unelevated xenon run is a complete proof, not just a storage result: UVM boot, VSMB
layer share, SCSI hot-add, guest mount, combined layers, hosted container create/start,
`cmd /c ver` exec returning the image build, terminate, and both compute systems verified
absent from enumeration afterwards.

## What the one-time setup actually has to do

The only thing standing between an unelevated developer and a running xenon was **read
access to the layer directory**. Docker's store is ACLed to Administrators, and
`C:\ProgramData\Docker` denies traverse, which is why nothing under it is reachable.

Two honest caveats about how that access was obtained here:

- Access was granted **in place** with `icacls`, additively, and is reversible
  (`grant --revoke`). This modifies another product's store, which is acceptable for a
  spike on a dev box and **is not a shipping design**.
- The grant was **partial**, and always is: one run reported `Successfully processed 554
  files; Failed processing 9613 files`, another `553` / `9614`. Most layer files reject the
  ACE because an elevated Administrator still lacks `WRITE_DAC` on files whose DACLs name
  only SYSTEM/TrustedInstaller. Directory traverse plus the handful of files that matter
  (`blank-base.vhdx`, `UtilityVM\SystemTemplate.vhdx`) was sufficient — the layer's bulk
  content is read by the VM worker process under its own identity via VSMB, not by the
  developer's token, which is why a partial grant is enough.

  So the rejection count is **not** the verdict, in either direction: `icacls` exits 0 even
  when it fails on every file, and a run with thousands of rejections still boots. `grant`
  therefore decides by opening the files a boot actually needs (`SufficiencyProbe` rows) and
  reports the count as diagnostics only.

For a shipping design the right answer is a store AspireHcs owns, populated by pulling and
materializing layers directly rather than borrowing Docker's — i.e. the image-acquisition
work in #30. That store would be user-owned from creation and need no ACL surgery at all.

## Why the layer could not simply be copied out

The first design exported the layer into a developer-owned store via
`ExportLayer`/`ImportLayer`. Measured result: `ExportLayer` → `0x80070057`.

hcsshim explains it (`internal/wclayer/exportlayer.go`, tag v0.14.1) — `NewLayerReader`
branches away from `ExportLayer` entirely when there are no parent layers:

```go
if len(parentLayerPaths) == 0 {
    // This is a base layer. It gets exported differently.
    return newBaseLayerReader(path, span), nil
}
```

Base layers move through a separate backup-stream reader/writer, and the supported route for
materializing one into your own store is the OCI-tar import path. A plain recursive file copy
is **not** a substitute: the transport format carries Win32 backup streams, security
descriptors and hard links that ordinary file I/O drops.

## Still open

- **Which privilege `ActivateLayer` wants.** Named above; not yet identified.
- **The modern `computestorage.dll` surface.** Bound and callable, but its `LayerData`
  document shape is unverified, so its privilege model remains genuinely untested.
- **Build-mismatch under xenon.** Still unverified — the second base layer in this store
  (`3c49902a…`) has not been identified or booted. The documented claim that Hyper-V
  isolation lifts the host/image build-match constraint remains **untested, not confirmed**.
- **Unelevated argon.** Would need the missing privilege granted, and whether that is
  reasonable to ask of a developer is a separate question from whether it is possible.

## Reproducing

```powershell
# One-time, elevated: make the layer readable (reversible with --revoke).
# --account matters when UAC elevates by credential rather than consent — the
# elevated identity is then not the developer's.
HcsContainerSpike grant --layer C:\ProgramData\Docker\windowsfilter\<sha> --account DOMAIN\you

# Everything below runs UNELEVATED
HcsContainerSpike verify    --layer C:\ProgramData\Docker\windowsfilter\<sha>
HcsContainerSpike privilege --layer C:\ProgramData\Docker\windowsfilter\<sha>
HcsContainerSpike run --isolation hyperv --scratch template --layer C:\ProgramData\Docker\windowsfilter\<sha>
```

`verify` must run unelevated: elevated it passes whether or not the grant did
anything, since an elevated token reads the layer regardless.

`Run-PrivilegeProofs.ps1` drives the whole sequence, self-elevating only the grant, and
**asserts** the results above rather than merely measuring them — including asserting that
argon *fails* at `ActivateLayer` with `0x80070522`. An unexpected pass there means the
privilege boundary moved and the document you are reading is stale.
