# AspireHcs

An experimental, pre-alpha [Aspire](https://aspire.dev) hosting integration for the Windows
[Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview).
It adds two resource types to the Aspire local dev loop, both ephemeral — created on
`aspire run`, torn down on exit, with state and logs in the dashboard:

- **Hyper-V virtual machines** — boot a VHDX as an HCS compute system, with serial-console
  logs, NAT endpoints, TCP health checks, and Connect (SSH/RDP) buttons in the dashboard.
- **Hyper-V-isolated Windows containers** — run images from an
  [hcsctl](https://github.com/joshmakestuff/hcsctl) store. AspireHcs currently exposes only
  Hyper-V isolation; that consumer choice does not narrow hcsctl's process-isolation scope.

Code under `spikes/` is not part of the shipped package.

## Getting started

**Consumer documentation** — the builder API, requirements, and setup — is the package README:
[src/AspireHcs/README.md](src/AspireHcs/README.md).

Guest VM images come from [hcs-images](https://github.com/joshmakestuff/hcs-images) (Packer,
ISO to VHDX). Container images come from a registry through `hcsctl image pull` and an
elevated `hcsctl image import` — elevation that is inherent, not incidental.

## Where more details are

- **Work in progress** lives in the [issues](https://github.com/joshmakestuff/AspireHcs/issues),
  not in this file.
- **Measured facts and standing decisions** live in the workspace's `docs/findings.md` and
  `docs/decisions.md`, beside this repo rather than inside it. This repo's earlier
  `docs/containers.md` and `docs/connect-ux.md` are preserved verbatim in the workspace under
  `docs/old/AspireHcs/`.
- **The hcsctl boundary** — both resource kinds drive
  [hcsctl](https://github.com/joshmakestuff/hcsctl) through its `--json` contract. AspireHcs no
  longer carries a parallel `Hcs`/`Hcn` interop implementation.

| Repo | Role |
|---|---|
| [hcsctl](https://github.com/joshmakestuff/hcsctl) | Go CLI over HCS. All HCS access goes through it. |
| [hcs-images](https://github.com/joshmakestuff/hcs-images) | Packer templates. Builds the guest VM images. |
