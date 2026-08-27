# AspireHcs

An experimental, pre-alpha [Aspire](https://aspire.dev) hosting integration for the Windows
[Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview).
It adds two resource types to the Aspire local dev loop, both ephemeral — created on
`aspire run`, torn down on exit, with state and logs in the dashboard:

- **Hyper-V virtual machines** — boot a VHDX as an HCS compute system, with serial-console
  logs, endpoints on the guest's leased address, TCP health checks, and Connect (SSH/RDP)
  buttons in the dashboard.
- **Hyper-V-isolated Windows containers** — run images from an
  [hcsctl](https://github.com/joshmakestuff/hcsctl) store. Process isolation is out of scope
  for now.

Both kinds consume as well as serve: `WithReference(other)` delivers connection strings and
endpoints into the guest, with host-loopback addresses relayed through a hidden Docker socat
container so the guest can reach them.

All HCS access goes through [hcsctl](https://github.com/joshmakestuff/hcsctl)'s `--json`
contract; this package contains no HCS interop of its own.

## Getting started

**Consumer documentation** — the builder API, requirements, and setup — is the package README:
[src/AspireHcs/README.md](src/AspireHcs/README.md).

**Sample** — a Linux VM, a Windows VM and a Windows container in one AppHost, with the steps to
prepare each image: [samples/HcsSample.AppHost](samples/HcsSample.AppHost/README.md).

You supply the guest VM image (a bootable Gen2/UEFI VHDX). AspireHcs does not install operating
systems or bootstrap guests. Container images come from a registry through `hcsctl image pull`
and an elevated `hcsctl image import`.

## Building

```
./eng/Get-HcsCtl.ps1      # fetch a pinned hcsctl release into tools/hcsctl
dotnet build
dotnet test               # unit tests; hcsctl contract tests skip when hcsctl is absent
```

Integration tests need Hyper-V and prepared images. Set `HCS_TEST_VHDX` (Linux VM),
`HCS_TEST_WINDOWS_VHDX` (Windows VM), `ASPIREHCS_TEST_IMAGE` + `ASPIREHCS_TEST_STORE`
(container) to enable them; without those variables they skip.

## Where more details are

Work in progress lives in the [issues](https://github.com/joshmakestuff/AspireHcs/issues), not
in this file.
