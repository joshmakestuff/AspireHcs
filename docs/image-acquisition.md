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
- Spike: `spikes/HcsContainerSpike`, commit `8463963`+ on `image-acquisition`
- Date: 2026-08-02

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

### The privilege boundary is the FINALIZE call, not extraction

| Call | Unelevated result | HRESULT |
|---|---|---|
| extraction (`NtCreateFile` + `BackupWrite`, 10 288 entries) | **OK** | `0x00000000` |
| `AdjustTokenPrivileges` (SeBackup/SeRestore) | NOT HELD | `0x80070514` ERROR_NOT_ALL_ASSIGNED |
| `ProcessBaseImage` | **FAILED** | `0x80070522` ERROR_PRIVILEGE_NOT_HELD |

So a developer can *download and unpack* an image with no elevation at all. What
they cannot do unelevated is **finalize** it — and `0x80070522` is the same
privilege-class gate that stops process-isolated containers at `ActivateLayer`
(see [container-privilege-matrix.md](container-privilege-matrix.md)). A filtered
token does not hold `SeBackupPrivilege`/`SeRestorePrivilege` at all, so they
cannot simply be enabled.

Note that unelevated extraction is only possible in `--no-security` mode, which
does not replay security descriptors. Full-fidelity extraction needs the same
two privileges, and fails fast at `AdjustTokenPrivileges` rather than part-way
through a 458 MB import.

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

## Still pending — the elevated half has not run

`Run-AcquisitionProofs.ps1` needs one UAC click, which no automated session can
supply. Until it runs, these remain open and must not be quoted as results:

- **Full-fidelity import, elevated.** Extraction with security descriptors
  restored has not been run end to end.
- **What finalize actually produces.** hcsshim never states that
  `ProcessBaseImage`/`ProcessUtilityImage` create `blank-base.vhdx` and
  `UtilityVM\SystemTemplate.vhdx`; the spike asserts it as a transition
  (absent before the call, present after) rather than assuming it, but the
  assertion has not been evaluated yet.
- **Whether the imported entry is readable unelevated.** The entry root inherits
  from `%LOCALAPPDATA%`, but `UtilityVM`'s descriptor is restored verbatim from
  the image and `SystemTemplate.vhdx` inherits it. If that descriptor does not
  grant the developer read/traverse, the store still needs an explicit grant —
  which would weaken, though not defeat, the "user-owned from birth" claim.
- **Boot from our own store.** The whole point. Unproven until it runs.
- **Build mismatch (the #32 stretch).** Booting ltsc2022 (build 20348) on this
  26200 host is the first real test of the claim that Hyper-V isolation lifts
  the host/image build-match constraint. The fixture now exists; the answer
  does not.
- **Descriptor-free finalize.** Whether `ProcessBaseImage` tolerates a tree
  whose descriptors were never restored, and whether the result boots.

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

## Retiring the Docker dependency

Once the elevated run confirms a boot from our own store, the `icacls` grant
against Docker's store described in
[container-privilege-matrix.md](container-privilege-matrix.md) is no longer
needed and should be reversed:

```powershell
HcsContainerSpike grant --layer C:\ProgramData\Docker\windowsfilter\<sha> --account DOMAIN\you --revoke
```

That is a manual step on purpose — it modifies another product's store, and
this repo should not be undoing that automatically any more than it should have
been doing it automatically.
