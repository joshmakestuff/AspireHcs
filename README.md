# AspireHcs

An experimental [Aspire](https://aspire.dev) hosting integration for the Windows
[Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview).
It adds two resource types to the Aspire local dev loop, both ephemeral — created on
`aspire run`, torn down on exit, with state and logs in the dashboard:

- **Hyper-V virtual machines** — boot a VHDX as an HCS compute system, with serial-console
  logs, NAT endpoints, TCP health checks, and Connect (SSH/RDP) buttons in the dashboard.
- **Hyper-V-isolated Windows containers** — run images from an
  [hcsctl](https://github.com/joshmakestuff/hcsctl) store. Process isolation is permanently
  out of scope; it is refused up front rather than attempted.

**Consumer documentation** — the builder API, requirements, and setup — is the package README:
[src/AspireHcs/README.md](src/AspireHcs/README.md).

## How it is put together

AspireHcs supplies the Aspire half: the resource types, the builder surface, the eventing
pipeline, endpoint ownership and scavenging. It does not own the HCS interop long term —
**all HCS access is migrating to hcsctl**, driven through hcsctl's `--json` contract: exactly
one document on stdout, progress on stderr, exit `0` ran, `1` ran and failed, `64` bad
arguments with nothing attempted. Containers already go through hcsctl, and
`src/AspireHcs/Cli` is the only place that speaks to the tool. The VM path still uses the
in-repo `Hcs`/`Hcn` CsWin32 interop and migrates last, because it is the shipping path
([hcsctl#34](https://github.com/joshmakestuff/hcsctl/issues/34)); when it does, that interop
retires.

Guest VM images come from [hcs-images](https://github.com/joshmakestuff/hcs-images) (Packer,
ISO to VHDX). Container images come from a registry through `hcsctl image pull` and an
elevated `hcsctl image import` — elevation that is inherent, not incidental.

Code under `spikes/` is not part of the shipped package.

## Where things are

- **Work in progress** lives in the [issues](https://github.com/joshmakestuff/AspireHcs/issues),
  not in this file.
- **Measured facts and standing decisions** live in the workspace's `docs/findings.md` and
  `docs/decisions.md`, beside this repo rather than inside it. This repo's earlier
  `docs/containers.md` and `docs/connect-ux.md` are preserved verbatim in the workspace under
  `docs/old/AspireHcs/`.

## Status

Pre-alpha. The VM path ships and is exercised live; the container resource boots end to end
through hcsctl and is still being built
([#39](https://github.com/joshmakestuff/AspireHcs/issues/39)).
