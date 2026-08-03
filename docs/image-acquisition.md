# Image acquisition

How AspireHcs gets a Windows container base image **without Docker** and without
borrowing another product's layer store — the remaining bulk of
[#30](https://github.com/joshmakestuff/AspireHcs/issues/30).

**Every result below is a recorded outcome from a run on the reference host.**
Where a step has not been run yet, it says so; nothing here is inferred from
documentation, and the pending rows are pending, not assumed.

- Host: Windows 11 Enterprise 10.0.26200, Hyper-V enabled
- Account: a normal user in **Hyper-V Administrators**
- Images: `mcr.microsoft.com/windows/nanoserver:ltsc2025` (build 26100.33158)
  and `:ltsc2022` (build 20348.5386)
- Spike: `spikes/HcsContainerSpike`, commit `4c858da`+ on `image-acquisition`
- Date: 2026-08-02
- Full run: `Run-AcquisitionProofs.ps1`, log `acquisition-proofs-20260802-211720.log`

## Headline

**A developer with no elevation and no Docker can acquire a Windows base image
and run it.** Pull from a registry, materialize it into a store AspireHcs owns,
and boot it as a Hyper-V-isolated container — verified end to end, with the
container reporting the image's own build from inside.

**And Hyper-V isolation really does lift the host/image build-match constraint.**
A Windows Server 2022 guest (build 20348.5386) booted on this build 26200 host
and printed its own version from inside the container. That answers
[#32](https://github.com/joshmakestuff/AspireHcs/issues/32)'s stretch question,
which had been carried as *untested, not true* since the xenon spike.

| Claim | Verdict |
|---|---|
| Acquire + boot, unelevated, no Docker | **PROVEN** — `cmd /c ver` → `10.0.26100.33158`, cold-to-exec 2124 ms |
| Build-mismatched image under Hyper-V isolation | **PROVEN** — 20348 guest on 26200 host, cold-to-exec 1903 ms |
| A store AspireHcs owns needs no ACL surgery | **PROVEN** — unelevated `verify` passes on both entries, no grant |
| Elevation needed for | **import only** — never for pulling, verifying, or running the container |

## Why not just copy the layer out of Docker's store

Because a base layer cannot be moved that way. Measured in #33: `ExportLayer`
returns `0x80070057` on a base layer, and hcsshim explains why — `NewLayerReader`
branches to a separate backup-stream reader when there are no parent layers. A
plain recursive file copy is not a substitute either: the transport format
carries Win32 backup streams, security descriptors and hard links that ordinary
file I/O drops.

The supported route for materializing a base layer is the **OCI-tar import
path**, which is what this work implements: pull the layer tar from a registry
and replay it as Win32 backup streams into a directory we own, then let
`vmcompute` finalize it.

## The pipeline

```
pull      registry manifest -> platform manifest -> config -> layer blob
          (digest-verified while streaming)          %LOCALAPPDATA%\AspireHcs\layers\blobs\
import    gunzip -> tar -> per-entry NtCreateFile + BackupWrite      ...\layers\<diffID>\
finalize  ProcessBaseImage (+ProcessUtilityImage)    -> blank-base.vhdx, SystemTemplate.vhdx
run       existing xenon path, unchanged
```

`import` and `finalize` are separable on purpose (`--skip-finalize`, plus a
standalone `finalize`): they turned out to be **two different privilege gates**,
and a single combined step would have reported one gate's result for both.

## Results

### Acquisition, unelevated — works

| Step | Result | Detail |
|---|---|---|
| `pull` ltsc2025 | **OK** | 191 485 431 bytes, sha256 verified while streaming |
| `pull` ltsc2022 | **OK** | 120 277 280 bytes, sha256 verified while streaming |
| `pull` re-run | **OK** | existing blob re-hashed and kept (content-addressed store) |
| `pull` servercore | **refused, exit 2** | 2 layers — chain import is out of scope, by design |
| `import --no-security --skip-finalize` ltsc2025 | **OK** | **10 288 entries, 458 MB, 5.2 s**; compressed blob matches the manifest digest AND the unpacked stream matches the config's diffID |

MCR needs no token: `GET /v2/` answers 200 anonymously, and both nanoserver tags
serve plain `application/vnd.docker.image.rootfs.diff.tar.gzip` layers with no
foreign-layer `urls` indirection.

Digests are re-checked at every hand-off rather than once: a manifest fetched by
tag is bound to the registry's `Docker-Content-Digest` (and the fetch fails
closed if that header is absent or not sha256), a manifest fetched by digest must
hash to it, and `import` re-hashes the compressed blob against the manifest
before trusting a file it did not download itself.

`--image` values are validated against the OCI reference grammar before any of
them reach a URL — `..`, `?`, `#`, spaces and uppercase repository names are
rejected at parse time rather than being interpolated into a request.

### Where elevation is actually needed

**`import` needs elevation. Nothing else does.** Being precise about *why*
matters, because there are two independent gates inside `import` and it is easy
to state one of them as if it were the whole story:

| Call | Unelevated result | HRESULT |
|---|---|---|
| `AdjustTokenPrivileges` (SeBackup/SeRestore) | NOT HELD | `0x80070514` ERROR_NOT_ALL_ASSIGNED |
| extraction **with** descriptors (full fidelity) | never reached — fails fast above | — |
| extraction **without** descriptors (`--no-security`, 10 288 entries) | **OK** | `0x00000000` |
| `ProcessBaseImage` (finalize), on that extracted tree | **FAILED** | `0x80070522` ERROR_PRIVILEGE_NOT_HELD |

Read together: full-fidelity extraction needs `SeBackupPrivilege` and
`SeRestorePrivilege` because it replays security descriptors through
`BackupWrite`, and it fails fast at the token rather than part-way through a
458 MB import. Finalize is gated **separately** — proven by getting past
extraction via `--no-security` and watching `ProcessBaseImage` fail anyway. So
neither gate is a consequence of the other, and it would be wrong to say "only
finalize needs elevation".

`0x80070522` is the same privilege-class gate that stops process-isolated
containers at `ActivateLayer` (see
[container-privilege-matrix.md](container-privilege-matrix.md)). A filtered
token holds neither privilege at all, so they cannot simply be enabled.

What this buys in practice: acquisition is a one-time elevated step, and
everything a developer does afterwards — verifying, running containers — is
unprivileged.

### What the images actually contain

From `inspect`, which reads the tar without any privilege:

| | ltsc2025 | ltsc2022 |
|---|---|---|
| entries | 10 288 | 4 088 |
| directories / regular files / hard links | 1 350 / 5 164 / 3 774 | 484 / 2 134 / 1 470 |
| `MSWINDOWS.rawsd` (security descriptors) | 6 513 | 2 617 |
| `MSWINDOWS.xattr.*` (EAs) | 981 | 838 |
| symlinks / junctions / alternate data streams | **0 / 0 / 0** | **0 / 0 / 0** |
| whiteouts | 0 (valid base layer) | 0 (valid base layer) |

Two consequences worth recording, because both contradicted a prior expectation:

- A predicted failure point for unprivileged extraction — the first symlink,
  which needs `SeCreateSymbolicLinkPrivilege` — **does not exist in these
  images**. That is why unelevated extraction succeeds outright.
- The reparse-point and ADS paths in the importer are therefore **exercised by
  no fixture here**. They are ported and unit-probed (`selftest` asserts the
  REPARSE_DATA_BUFFER and ADS record shapes), but they have not run against a
  real image. Treat them as implemented-and-untested, not proven.

There is also **no `ctime` PAX record at all**, and `mtime` is missing on 149 of
10 288 ltsc2025 entries. Absent timestamps are therefore left at zero, which
Windows reads as "do not change", rather than fabricated from another field.

### The elevated half, and what it settled

| Step | Result |
|---|---|
| `import` ltsc2025, full fidelity, elevated | **OK** |
| `import` ltsc2022, full fidelity, elevated | **OK** |
| `ProcessBaseImage` produced `blank-base.vhdx` | **OK — 38 MB**, absent before the call |
| `ProcessUtilityImage` produced `UtilityVM\SystemTemplate.vhdx` | **OK — 4 MB**, absent before the call |
| split extract → standalone `finalize`, full fidelity | **OK**, provenance records `securityDescriptorsRestored=True` |
| unelevated `verify` on both imported entries | **OK — no grant, no ACL work** |
| unelevated xenon boot, ltsc2025, from our store | **OK**, guest `10.0.26100.33158`, 2124 ms |
| unelevated xenon boot, **ltsc2022 (build-mismatched)** | **OK**, guest `10.0.20348.5386` on a 26200 host, 1903 ms |
| unelevated argon boot, from our store | **FAILED at `ActivateLayer` `0x80070522`** — as on Docker's store |

The finalize products are now witnessed rather than believed: the spike probes
for them *before* the calls too, so "produced" means absent-then-present, not
merely present. hcsshim never documents this mapping; it is now measured.

Argon failing identically on a store we own is itself a result: it confirms the
process-isolation gate is a **privilege**, with nothing to do with where the
layer lives or who owns it.

### Build mismatch: answered

The container printed, from inside:

```
Microsoft Windows [Version 10.0.20348.5386]
```

on a host running 10.0.26200. `HostingSystemId` names our utility VM, so this is
a genuine xenon and not a silently-substituted argon. Hyper-V isolation supplies
the guest's own kernel, and the host/image build-match rule that constrains
process isolation does not apply.

One caveat kept explicit: this is **one** mismatched pair (Server 2022 guest,
Win11 25H2 host), not a general claim that any image runs on any host. It
falsifies "the constraint applies to xenons"; it does not establish a support
matrix.

### Descriptor-free finalize: INCONCLUSIVE, and why

The run tried to finalize a tree extracted with `--no-security`. It failed
`0x80070050 ERROR_FILE_EXISTS` — **but not because of the missing descriptors.**

The unelevated `finalize` earlier in the same run (asserted to fail at the
privilege gate) had already gotten far enough to create a **partial**
`blank-base.vhdx`: 4 MB, where a complete one is 38 MB. The later elevated
attempt then tripped over those leftovers.

Two things follow, and the second is the more useful:

1. The descriptor-free question is still open. The harness now re-extracts a
   clean tree before finalizing it, so the next run measures the question
   rather than the debris.
2. **`ProcessBaseImage` is neither idempotent nor atomic.** A failed call leaves
   partial output behind, and the retry dies `ERROR_FILE_EXISTS` in a way that
   blames the retry. `finalize` now refuses up front when outputs pre-exist and
   names the remedy (re-import; `import` destroys a torn entry automatically).

This was only visible because the pre-existence probe was added during review —
without it the run would have read as "descriptor-free finalize fails" and a
wrong conclusion would have gone into this document.

## Not attempted

- **Chain import** (multi-layer images such as `servercore`). A child layer uses
  a different on-disk transport format entirely — `vmcompute!ImportLayer` over a
  staging directory, with tombstones and hard links deferred to the end and the
  UtilityVM subtree cloned from the parent. `pull` refuses multi-layer images
  loudly rather than half-supporting them.
- **Authenticated registries** (ACR, GHCR), proxies, resumable downloads. MCR is
  anonymous, which covers the spike.
- **The modern `computestorage.dll` surface.** Still untested, unchanged by this
  work; the legacy `ProcessBaseImage` path is what hcsshim itself uses.

## Reproducing

```powershell
# From a NORMAL (unelevated) shell. Prompts for elevation once, for import only.
.\spikes\HcsContainerSpike\Run-AcquisitionProofs.ps1
```

The harness refuses to start elevated, because an unelevated session is the
condition under test. Individual commands:

```powershell
HcsContainerSpike pull     --image mcr.microsoft.com/windows/nanoserver:ltsc2025
HcsContainerSpike inspect  --metadata <store>\images\<name>.json
HcsContainerSpike import   --metadata <store>\images\<name>.json          # needs elevation
HcsContainerSpike import   --metadata <...> --no-security --skip-finalize # unelevated
HcsContainerSpike finalize --entry <store>\<diffID>
HcsContainerSpike run --isolation hyperv --scratch template --layer <store>\<diffID>
```

## Can the UAC prompt be removed? No — and the reason is structural

Docker gets away with an unelevated `docker pull` because `dockerd` runs as a
**LocalSystem service** (`dockerd.exe --run-service -G docker-users`, verified on
this host) and does the layer work with privileges it acquired at boot; the CLI
just talks to it over `\\.\pipe\docker_engine`. The elevation did not disappear,
it moved into a daemon and became permanent, gated by an ACL on that pipe.

Short of building such a service, two routes were examined and both are closed.

**Skipping finalization: impossible.** A layer that is extracted but not
finalized fails `CreateSandboxLayer` with `0x80070003`, and grafting in
`blank-base.vhdx` + `SystemTemplate.vhdx` still fails at UVM start with
`0x80370106` (guest-initiated exit). `ProcessUtilityImage` **rewrites the UVM's
BCD** — which is why hcsshim backs those exact files up before import. Finalize
also builds `Hives\*_BASE` from the image's registry and writes `layout`. It is
load-bearing, not bookkeeping.

**Holding the privilege without elevating: impossible for an interactive user.**
`SeBackupPrivilege`/`SeRestorePrivilege` are granted by the Backup Operators
group, so the obvious hypothesis was that membership would make `import` work
unelevated — a one-time grant like the Hyper-V Administrators prerequisite.
Measured directly, with a purpose-made account holding Backup Operators and
**not** Administrators:

```
identity=EKAJATI\hcsbackuptest elevated=False hyperVAdministrators=True
[FAIL] EnableBackupRestorePrivileges: hr=0x80070514 (ERROR_NOT_ALL_ASSIGNED)
```

```
whoami /groups
BUILTIN\Backup Operators   Alias  S-1-5-32-551  Group used for deny only
```

That is **UAC token filtering**. Windows issues a filtered standard-user token
to members of privileged groups, and the filtering strips precisely these
privileges. The same token is the control that makes the reading airtight:
`Hyper-V Administrators` (S-1-5-32-578) is *not* a filtered group and came
through enabled in that very token — so the logon was fresh and group
membership did apply. Only the sensitive group was neutered.

Because filtering removes the privileges from any interactive standard token
regardless of which group granted them, **no group membership can give an
unelevated session `SeBackupPrivilege`.** Backup Operators would hold them only
in an *elevated* token, which buys nothing over elevating directly while adding
a standing ACL-bypass capability — a worse trade, not a better one.

**Conclusion, recorded as a decision rather than a limitation:** acquisition
costs one UAC prompt per image. Everything else — pulling, verifying, creating
per-container scratch layers, running containers — is unprivileged. The only
way to remove that prompt is a privileged service on Docker's model, which is
deliberately not pursued: it converts a visible, attributable, per-image
elevation into a permanent one, and membership in the group that reaches such a
service is effectively administrator-equivalent.

## Still open

- **Descriptor-free finalize** — see above; the question survived the run, the
  obstacle did not.
- **Chain import** (multi-layer images). Unchanged and unstarted.
- **Which privilege `ProcessBaseImage` wants** — narrowed, not settled. The
  `identify` command runs the call four times over identical pristine trees
  varying only which privileges are enabled; elevated, **all four arms
  succeeded, including with both disabled**. So the call does not require them
  to be *enabled by the caller*. That is weaker than it sounds: disabling does
  not remove a privilege from the token, and a callee holding one may enable it
  for itself, so enabled-state cannot discriminate. Settling it would need a
  token that does not *hold* them (`CreateRestrictedToken` with
  `PrivilegesToDelete`). Largely moot in practice — per the section above, an
  interactive standard token cannot hold them at all.
- **Whether `--no-security` layers behave correctly in a guest** even if they do
  finalize. Host-side descriptors are not replayed, so guest services that check
  specific SIDs might misbehave; nothing here tests that.

## Retiring the Docker dependency

The elevated run confirmed a boot from our own store, so the `icacls` grant
against Docker's store described in
[container-privilege-matrix.md](container-privilege-matrix.md) is **no longer
needed** and should be reversed:

```powershell
HcsContainerSpike grant --layer C:\ProgramData\Docker\windowsfilter\<sha> --account DOMAIN\you --revoke
```

That is a manual step on purpose — it modifies another product's store, and
this repo should not be undoing that automatically any more than it should have
been doing it automatically.
