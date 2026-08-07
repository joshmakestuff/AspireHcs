# Windows containers in AspireHcs

How the container path is put together, what it needs, and why it runs Hyper-V-isolated containers
only. The "why" is measurement, not preference, and it is written down here so it can be checked
rather than taken on trust — and so nobody re-derives it from an HRESULT and reaches a different
conclusion ([#47](https://github.com/joshmakestuff/AspireHcs/issues/47)).

## The shape

AspireHcs does not call HCS for containers. It drives
[hcsctl](https://github.com/joshmakestuff/hcsctl), a CLI over the same API, and supplies the Aspire
half: resource types, the builder surface, the eventing pipeline, ownership and scavenging.

The seam is hcsctl's `--json` contract:

- stdout carries **exactly one** JSON document, on every path including failure
- stderr carries progress, and is never a result
- exit `0` ran, `1` ran and failed, `64` bad arguments with **nothing attempted**
- a guest process's own exit code is `exitCode` in the document, never hcsctl's exit code

`src/AspireHcs/Cli` is the only code that speaks to the tool, and it does not scrape stderr for an
answer. If an answer is not in the document, the fix belongs in hcsctl.

Exit `1` and exit `64` are kept distinguishable all the way out to the developer. They mean
different things: `64` is a defect in the argv AspireHcs built and promises nothing happened, so
reporting it as an infrastructure failure would send someone to look at their Hyper-V configuration
for a missing option.

## Setup

Three one-time steps. Only the second is elevated, and only once per image.

```powershell
# 1. Fetch the pinned hcsctl drop into tools/hcsctl (verified by SHA256).
./eng/Get-HcsCtl.ps1

# 2. Acquire an image. The import is elevated; the pull is not.
hcsctl image pull   --ref mcr.microsoft.com/windows/nanoserver:ltsc2025 --store E:\hcsctl-store
hcsctl image import --ref mcr.microsoft.com/windows/nanoserver:ltsc2025 --store E:\hcsctl-store   # elevated

# 3. Check what your token can actually do, before an AppHost tries.
hcsctl info --store E:\hcsctl-store --json
```

`hcsctl info` is also AspireHcs's preflight: a resource start reads it and fails with an actionable
message — naming the service, the group, or the exact two commands to acquire a missing image —
rather than letting a knowable condition surface as a bare error later.

The store is not hcsctl's per-user default in normal use, so AspireHcs passes `--store` on every
invocation that accepts it. That matters more than it looks: an invocation that silently omits it
does not fail, it **succeeds against the wrong images**.

**Elevation is not automated, and will not be.** `image import` needs `SeBackupPrivilege` and
`SeRestorePrivilege` — both UAC filtering triggers — plus an enabled `BUILTIN\Administrators` SID
for `ProcessBaseLayer`. An AppHost cannot acquire an image on a developer's behalf. It can only say
precisely what to run.

## Why Hyper-V isolation only

Two gates were found. Only one is grantable, and the difference is what settles this.

**1. `SeManageVolumePrivilege` — grantable.** `0x80070522 ERROR_PRIVILEGE_NOT_HELD` on
`ProcessBaseImage`, `ActivateLayer` and `AttachVirtualDisk` is this privilege. It is **not** a UAC
filtering trigger, so granting "Perform volume maintenance tasks" to a group that is not itself a
filtering trigger puts it in an ordinary non-elevated token. Measured: unelevated
`AttachVirtualDisk` went 1314 → 0, and unelevated `ActivateLayer` now succeeds.

**2. An enabled `BUILTIN\Administrators` SID — not grantable.** `0x80070005 E_ACCESSDENIED` on
`ProcessBaseImage` (import finalize) and on `PrepareLayer` is a **group check**, not a privilege.
Nothing in secpol substitutes for it, and UAC token filtering means no membership — Backup
Operators included, measured as "used for deny only" — supplies it to a filtered token.

**Timing is what makes gate 2 fatal for process isolation.** `import`'s instance of it runs once
per image at install time, so elevating there buys unprivileged use afterwards. `PrepareLayer` runs
at **every container start**. No user-rights grant supplies the SID, and no elevated preparation
step can front-load it, because it is not a preparation-time call. So an unprivileged
process-isolated container is impossible, not merely awkward.

**A xenon never touches that path.** The host does not attach the disk: it produces a scratch,
calls `HcsGrantVmAccess`, and hands the scratch plus the read-only layer directories to a utility VM
whose *guest kernel* does the stacking. There is no `ActivateLayer`, no `PrepareLayer`, and no host
volume path.

Proof the isolation is real rather than nominal: `servercore:ltsc2022` reports `10.0.20348.5386`
from inside a `10.0.26200.8894` host. That mismatch is impossible without a separate VM — and it
also falsifies the claim that the host/guest build-match constraint applies to Hyper-V-isolated
containers.

### The argon gate is narrower than "containers need admin"

`NameToGuid`, `CreateSandboxLayer`, `ActivateLayer`, `GetLayerMountPath`, `DeactivateLayer` and
`DestroyLayer` all succeed unelevated. Only `PrepareLayer` does not.

**Any wording along the lines of "layer storage needs elevation" is wrong as a general claim** and
should be retired wherever it appears — the same way the earlier `CreateSandboxLayer`
`E_ACCESSDENIED` finding was retired once it turned out to be Docker's store ACL rather than the
API. The boundary is not "containers need admin". It is "attaching storage to the *host* needs
specific rights".

### Consequences in the code

Process isolation is refused up front with the measured reason named, not attempted and allowed to
fail at `PrepareLayer` with a bare `E_ACCESSDENIED`
([#46](https://github.com/joshmakestuff/AspireHcs/issues/46)). hcsctl does not implement it at all
([hcsctl#8](https://github.com/joshmakestuff/hcsctl/issues/8)), so there is no isolation switch to
default wrongly.

## Where the measurements live

The per-call elevation table and the HCS behaviours that are not visible from the call site are in
hcsctl's [findings.md](https://github.com/joshmakestuff/hcsctl/blob/main/docs/findings.md). They are
referenced, not copied — a second copy is a second thing to go stale.

**hcsctl owns the privilege question**, and it is the place to check what a given call needs. The
history above is here because it explains why this integration is xenon-only, which is a decision
AspireHcs has to live with; the per-call detail behind it is not this repo's to restate. If you
need to know exactly what your token must hold, run `hcsctl info` and read hcsctl's findings.

Five of those behaviours bind AspireHcs's teardown and scavenging directly
([#48](https://github.com/joshmakestuff/AspireHcs/issues/48)), and they are encoded as tests rather
than restated as prose:

- terminate and shutdown complete asynchronously and must be awaited, never inferred from a call returning
- `S_FALSE` (1) is a success HRESULT — the CLI-layer equivalent is that exit `1` and exit `64` are not the same failure
- a created-but-never-started compute system reports a **blank** state, which is not the same as no system at all
- `DestroyLayer` can return success and leave the tree, so **teardown is verified by absence, never by return code**
- layer directories defeat ordinary file deletion, so `Directory.Delete` is never a fallback for a leftover scratch
