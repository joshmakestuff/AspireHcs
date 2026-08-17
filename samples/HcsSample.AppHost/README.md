# HcsSample.AppHost

One AppHost with three opt-in resources: a Linux VM, a Windows VM, and a Hyper-V-isolated
Windows container. Each resource is added only when its environment variable is set, because
each needs a fixture that is not in the repository.

| Variable | Resource | Value |
| --- | --- | --- |
| `HCS_TEST_VHDX` | Linux VM `appliance` | Path to a bootable Gen2/UEFI VHDX |
| `HCS_TEST_VM_USER` | Linux VM | SSH account for the Connect button (default `root`) |
| `HCS_SAMPLE_WINDOWS_VHDX` | Windows VM `winserver` | Path to a bootable Gen2/UEFI VHDX |
| `HCS_SAMPLE_WINDOWS_USER` | Windows VM | RDP account for the Connect button (default `Administrator`) |
| `ASPIREHCS_TEST_IMAGE` | Container `worker` | Image reference already imported into the store |
| `ASPIREHCS_TEST_COMMAND` | Container | Command (default `cmd /c ping -t 127.0.0.1`) |
| `ASPIREHCS_TEST_STORE` | All | hcsctl store directory (default: hcsctl's per-user store) |
| `ASPIREHCS_HCSCTL` | All | Path to `hcsctl.exe` when it is not on `PATH` |

Run with `aspire run` (or `dotnet run`) from this directory. The AppHost must run elevated or as a
member of **Hyper-V Administrators**. All three resources drive
[hcsctl](https://github.com/joshmakestuff/hcsctl); it must be on `PATH` or in `ASPIREHCS_HCSCTL`.

## Preparing a VM image

AspireHcs does not install operating systems and does not bootstrap guests. You supply a prepared
VHDX; AspireHcs boots a differencing child of it, so the file is never written to and can back
several resources at once. Build the image with any tool you like (Hyper-V Manager, Packer,
`New-VM` + an ISO). The image must satisfy these points, and nothing else:

1. **Generation 2 (UEFI) VHDX**, bootable without an attached ISO. Dynamic or fixed size.
2. **The `hcsguest` agent installed and running as a service.** AspireHcs learns the guest's
   address, and therefore readiness, by asking the agent through `hcsctl vm ip`; on a static
   (NAT) network `hcsctl vm start` also programs the guest NIC through it. Without the agent a
   networked VM never reaches Running. hcsctl provides in-guest installer scripts for Windows
   (elevated PowerShell) and Linux (root, systemd) under `install/` in the
   [hcsctl repository](https://github.com/joshmakestuff/hcsctl); pin the version to the
   `hcsctl` release you run on the host.
3. **Hyper-V integration drivers loaded**, including the Hyper-V socket transport the agent uses
   (`hv_sock` on Linux; in-box on Windows). Stock Linux kernels ship `hv_vmbus`, `hv_netvsc`
   and `hv_sock`; no extra packages are needed on current distributions.
4. **NIC configured for DHCP.** `WithNetwork()` attaches a NIC on the Hyper-V Default Switch; the
   agent reports the leased address, which becomes the endpoint address. This is the default for
   stock Windows and Linux images.
5. **The services you declare endpoints for must listen**, and the guest firewall must let them
   in. `WithTcpHealthCheck()` waits for a TCP accept on the endpoint; a refused connection is
   unhealthy.
6. **A known local account** if you use `WithSshCommand` / `WithRdpCommand`.
7. **Shut down cleanly** before using the VHDX. Do not leave the image running or in a saved
   state.

Verify the image from the host before pointing AppHost at it:

```
hcsctl vm create --vhdx <path> --network default      # prints the VM id
hcsctl vm start  --id <id>
hcsctl guest info --vmid <id>                          # must answer: the agent is up
hcsctl vm ip     --id <id>                             # must print an address: DHCP works
hcsctl vm rm     --id <id> --force
```

### Linux VM (example: Rocky Linux 10)

- Install from the distribution ISO into a Gen2 VM. Secure Boot: off, or the "Microsoft UEFI
  Certificate Authority" template.
- Enable `sshd` (`systemctl enable sshd`) and permit the account you will pass as
  `HCS_TEST_VM_USER`. The reference fixture has `root` and no other account; set
  `PermitRootLogin` accordingly, or create a user.
- Leave the NIC on DHCP (NetworkManager default).
- Install the agent as root: `install/install-hcsguest.sh` from the hcsctl repository, pinned to
  the host's hcsctl version. Confirm `systemctl is-active hcsguest`.
- Shut down; point `HCS_TEST_VHDX` at the VHDX.

### Windows VM (reference: Windows Server 2025)

- Install from the ISO into a Gen2 VM. Set the local `Administrator` password (or create the
  account you pass as `HCS_SAMPLE_WINDOWS_USER`).
- Enable Remote Desktop and open its firewall group:

  ```powershell
  Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
  Enable-NetFirewallRule -DisplayGroup 'Remote Desktop'
  ```

- Leave the NIC on DHCP.
- Install the agent from an elevated PowerShell: `install/Install-HcsGuest.ps1` from the hcsctl
  repository, pinned to the host's hcsctl version. Confirm `Get-Service hcsguest` is Running.
- Shut down; point `HCS_SAMPLE_WINDOWS_VHDX` at the VHDX.

## Preparing a container image

Images live in an hcsctl store. Pull and import once; the import is elevated:

```
hcsctl image pull   --ref mcr.microsoft.com/windows/servercore:ltsc2022 --store <dir>
hcsctl image import --ref mcr.microsoft.com/windows/servercore:ltsc2022 --store <dir>   # elevated
```

Then set `ASPIREHCS_TEST_IMAGE` to the reference and `ASPIREHCS_TEST_STORE` to `<dir>`. Only
Hyper-V isolation is supported.
