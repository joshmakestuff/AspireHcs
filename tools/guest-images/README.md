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
  source image and its SHA-256, edits applied, script commit, UTC build time. An image that
  can't be traced to its inputs can't back a test result.
- **Verify the result, not the step.** Build scripts assert the post-condition of each edit
  (the config line is present) rather than trusting that the edit command matched anything.

## kali/ — probe variants (issue #5/#11 test instruments)

`New-KaliProbeVariant.ps1` derives probe images from the Kali Hyper-V base:

| Variant | What it is | What it proved / probes |
|---|---|---|
| `Serial` | `console=ttyS0,115200n8` on the kernel cmdline, `quiet splash` removed | First guest-side readiness reference: the full kernel+systemd log streams to COM1 (58 KB/boot observed vs 0 for the base). Validated the balloon probe — balloon `S_OK` lands at the ttyS0 login prompt (~9.2 s guest time), i.e. at full userland, not merely kernel-up. |
| `StaticNoDhcp` | Serial + NetworkManager masked + static `eth0` (ifupdown) | The never-leases path: a guest that boots healthy with a visible NIC but never DHCPs, exercising `WaitForLeasedIpAsync`'s 90 s timeout and its error reporting. |

Requirements: WSL 2 with a default Linux distro (edits run via `wsl --mount` against the
copy's ext4 root — the Kali root filesystem is not mountable from Windows directly).

## windows/ — Server 2025 base image builder

See issue #11. Offline ISO→VHDX provisioning adapted from SCED's `New-ProvisionedVhd`:
GPT (EFI/MSR/NTFS) → `Expand-WindowsImage` → unattend → offline servicing → `bcdboot`,
plus AspireHcs-specific readiness instrumentation (EMS on COM1, OpenSSH as the positive
TCP health-check fixture) and a boot acceptance test that asserts the image's claims.
