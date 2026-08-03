# Guest images for AspireHcs testing

Build-time tooling for the guest images the integration suite boots. Nothing here ships in
the NuGet package, and no image or ISO lives in this repo — multi-GB artifacts and OS
licensing make that impossible, so the contract is **bring your own input image** and build
variants from it reproducibly.

## Ground rules

- **The base image is never modified.** Every tool copies first and edits the copy. The
  suite's base (`HCS_TEST_VHDX`) is usually the only bootable image on a machine; mutating
  it would invalidate every result after that point.
- **Provenance or it didn't happen.** Every built image gets a `.provenance.json` beside it:
  source image and its SHA-256, edits applied, script commit, UTC build time. The one exception
  is `-SkipIsoHashCheck`, for iterating on the bootstrap: it records `isoSha256: null` and
  `isoHashVerified: false` rather than a hash nothing verified, so an unpinned image is
  self-identifying and must not be published as a fixture. An image that
  can't be traced to its inputs can't back a test result.
- **Verify the result, not the step.** Build scripts assert the post-condition of each edit
  (the config line is present) rather than trusting that the edit command matched anything.

## kali/ — probe variants (issue #5/#11 test instruments)

`New-KaliProbeVariant.ps1` derives probe images from the Kali Hyper-V base:

| Variant | What it is | What it proved / probes |
|---|---|---|
| `Serial` | `console=ttyS0,115200n8` on the kernel cmdline, `quiet splash` removed | First guest-side readiness reference: the full kernel+systemd log streams to COM1 (58 KB/boot observed vs 0 for the base). Validated the balloon probe — balloon `S_OK` lands at the ttyS0 login prompt (~9.2 s guest time), i.e. at full userland, not merely kernel-up. |
| `StaticNoDhcp` | Serial + NetworkManager masked + static `eth0` (ifupdown) | The never-leases path: a guest that boots healthy with a visible NIC but never DHCPs. Witnessed manually 2026-08-01 — `WaitForLeasedIpAsync` timed out at 90 s with an actionable `TimeoutException` naming DHCP, the resource ended `FailedToStart`, and the guest's `networking.service` finished OK (eth0 raised static). Not yet wired into the suite; that lands with the Windows fixture wiring (issue #11 phase 3). |

Requirements: WSL 2 with a default Linux distro (edits run via `wsl --mount` against the
copy's ext4 root — the Kali root filesystem is not mountable from Windows directly).

## windows/ — Server 2025 base image builder

**Choosing an edition.** A Server ISO carries several — Server Core and Desktop Experience
variants of each SKU — and the index differs between ISOs, so ask the ISO rather than guessing:

```powershell
.\New-WindowsGuestImage.ps1 -IsoPath E:\isos\server2025.iso -ListImages
```

Then select by exact name, `-ImageName 'Windows Server 2025 Standard (Desktop Experience)'`, or
by `-ImageIndex`. Passing both is refused rather than resolved, since the ignored one would look
honoured. Names are matched exactly because `Windows Server 2025 Standard` is a *prefix* of the
Desktop Experience name. Desktop Experience needs more room than Core, so raise `-SizeGB`; the
VHDX is dynamic, so a generous ceiling costs nothing until used.

Both editions have been built on the reference host (2026-08-03): Core at index 1 and Desktop
Experience at index 2 (~14 GB), each recording its own edition in its provenance sidecar. Those
images are local build artifacts, not repo contents, so treat the specifics as a build log rather
than something this repository can prove. SSH served on every image generated that day; RDP did
**not** reach the guest from the host — see [docs/connect-ux.md](../../docs/connect-ux.md). The
bootstrap has been changed, but no image has been built from the change.

`New-WindowsGuestImage.ps1` (requires an elevated shell and the Hyper-V module — build-time
only): pins the ISO by SHA-256, provisions offline (GPT EFI/MSR/NTFS → `Expand-WindowsImage`
→ unattend → `dism.exe` capability servicing → `bcdboot` → EMS on COM1 in the BCD), runs one
self-terminating burn-in boot so specialize/first-logon churn lands in the base instead of
every child diff, then seals: `Optimize-VHD` full compaction, file marked read-only,
provenance JSON with the final image's SHA-256. Deliberately not sysprep-generalized —
ephemeral per-run children on an isolated NAT network boot faster from a specialized base
(witnessed: 9–10 s to Running-and-serving through the product path).

Verified empirically on the first sealed build (2026-08-01): OpenSSH Server is **in-box** on
Server 2025 (`Installed` straight from the WIM; the LOF ISO path remains as fallback), EMS
streams the SAC console over the pumped COM1 pipe, the guest DHCPs on the Default Switch,
and sshd accepts — the suite's first positive health-check fixture.

## How the integration suite consumes these images

| Env var | Image | What it unlocks |
|---|---|---|
| `HCS_TEST_VHDX` | any bootable Gen2/UEFI VHDX (the Kali base) | the core suite |
| `HCS_TEST_WINDOWS_VHDX` | `windows/` sealed Server 2025 image | positive fixture: health check goes Healthy, TCP accept required, EMS serial asserted (`WindowsGuestFixtureTests`) |
| `HCS_TEST_NOLEASE_VHDX` | `kali/` `StaticNoDhcp` variant | never-leases pin: `FailedToStart` + DHCP cause named (`NoLeaseFailureModeTests`, ~2 min by design) |

Unset vars skip their tests; nothing here runs on the hosted CI lane.
