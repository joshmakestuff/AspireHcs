# HcsSample.AppHost

A small showcase of HCS guests as Aspire resources:

- **worker** — a Hyper-V-isolated Windows container running `HcsSample.GuestApi`, a published
  .NET binary bind-mounted from the host into a stock nanoserver image — no Dockerfile, no
  image build.
- **web** — an ordinary Aspire project that consumes the guests through endpoint references and
  shows what the container answers, including the live content of the bind-mounted `data\`
  directory. Edit `data\hello.txt` while it runs; the guest serves the change immediately.
- **pg** — an ordinary Aspire Postgres integration (a Linux Docker container) for contrast with
  the raw-HCS guests. Scripts in `db\` seed the `appdb` database on first start, and **web**
  reads the rows through the injected connection string.
- **appliance** / **winserver** — opt-in VMs (they need a bootable VHDX you provide), with
  Connect (SSH/RDP) buttons on the dashboard.

## Run it

```powershell
# once: publishes the guest app and imports the container image (one elevated step)
.\..\prepare.ps1

# then, from this directory
aspire run     # or: dotnet run
```

The AppHost must run elevated or as a member of **Hyper-V Administrators**. **pg** additionally
needs a running Docker engine (any daemon; the Postgres container is Linux). Nothing else is
required for the container and the web frontend: `prepare.ps1` pins
[hcsctl](https://github.com/joshmakestuff/hcsctl) into `tools\hcsctl`, and the AppHost falls
back to that copy when neither `ASPIREHCS_HCSCTL` nor `PATH` finds one.

Things to try while it runs:

- Edit `data\hello.txt` — the web page picks it up on the next refresh, live over VSMB.
- Pause **worker** from the dashboard — the page shows it stop answering; resume it.
- The dashboard's **worker** details show guest statistics and its process list.
- The **appdb** card on the web page lists the rows seeded from `db\seed.sql`. The container is
  ephemeral: restart the AppHost and seeding runs again from scratch.
- With a VM configured, the web page probes its endpoint at the leased guest address, and the
  resource's **Connect (SSH)** / **Connect (RDP)** command opens a session to it.

## Configuration

All optional, in the `Hcs` section of `appsettings.json` (or user secrets), with environment
variable fallbacks:

| Setting | Env var | Value |
| --- | --- | --- |
| `Store` | `ASPIREHCS_STORE` | hcsctl image store (default `samples\.store` — beside the sample, not in per-user AppData; delete the directory to reclaim everything the sample materialized) |
| `ContainerImage` | `ASPIREHCS_TEST_IMAGE` | Image reference (default `mcr.microsoft.com/windows/nanoserver:ltsc2025`) |
| `LinuxVhdx` | `HCS_TEST_VHDX` | Bootable Gen2/UEFI VHDX for the Linux VM |
| `LinuxUser` | `HCS_TEST_VM_USER` | SSH account (default `root`) |
| `WindowsVhdx` | `HCS_SAMPLE_WINDOWS_VHDX` | Bootable Gen2/UEFI VHDX for the Windows VM |
| `WindowsUser` | `HCS_SAMPLE_WINDOWS_USER` | RDP account (default `Administrator`) |
| `ApplianceVhdx` | `HCS_TEST_APPLIANCE_VHDX` | Boot VHDX of an agentless vendor appliance (no `hcsguest`, fixed in-guest address) |
| `ApplianceAddress` | `HCS_TEST_APPLIANCE_ADDRESS` | The appliance's fixed in-guest IP; required with `ApplianceVhdx` |
| `ApplianceDataVhdx` | `HCS_TEST_APPLIANCE_DATA_VHDX` | Extra data VHDX (SCSI LUN 1) |
| `ApplianceNetwork` | `HCS_TEST_APPLIANCE_NETWORK` | Host compute network (default `Default Switch`) |
| `ApplianceMac` | `HCS_TEST_APPLIANCE_MAC` | Pinned NIC MAC, e.g. `00-15-5D-02-33-0E` |
| `ApplianceVlan` | `HCS_TEST_APPLIANCE_VLAN` | Access VLAN for the NIC's switch port |
| `ApplianceHealthPath` | `HCS_TEST_APPLIANCE_HEALTH_PATH` | Path for the insecure HTTPS health check (default `/`) |
| `ApplianceMemoryGb` | `HCS_TEST_APPLIANCE_MEMORY_GB` | Memory (default 6) |
| `ApplianceCpus` | `HCS_TEST_APPLIANCE_CPUS` | vCPUs (default 4) |
| `ApplianceSshUser` | `HCS_TEST_APPLIANCE_SSH_USER` | SSH account for the Connect button (default `root`) |
| `ConsumeWeb` | `HCS_SAMPLE_CONSUME_WEB` | Any non-empty value enables the consumer-direction demo: worker and the Linux VM consume web's endpoint. Requires Docker for the host-loopback relay |

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
- Enable `sshd` (`systemctl enable sshd`) and permit the account you configure as `LinuxUser`.
  The reference fixture has `root` and no other account; set `PermitRootLogin` accordingly, or
  create a user.
- Leave the NIC on DHCP (NetworkManager default).
- Install the agent as root: `install/install-hcsguest.sh` from the hcsctl repository, pinned to
  the host's hcsctl version. Confirm `systemctl is-active hcsguest`.
- Shut down; point `LinuxVhdx` at the VHDX.

### Windows VM (reference: Windows Server 2025)

- Install from the ISO into a Gen2 VM. Set the local `Administrator` password (or create the
  account you configure as `WindowsUser`).
- Enable Remote Desktop and open its firewall group:

  ```powershell
  Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
  Enable-NetFirewallRule -DisplayGroup 'Remote Desktop'
  ```

- Leave the NIC on DHCP.
- Install the agent from an elevated PowerShell: `install/Install-HcsGuest.ps1` from the hcsctl
  repository, pinned to the host's hcsctl version. Confirm `Get-Service hcsguest` is Running.
- Shut down; point `WindowsVhdx` at the VHDX.

## Preparing a container image by hand

`prepare.ps1` does this for the default image. For another image:

```
hcsctl image pull   --ref <ref> --store <dir>
hcsctl image import --ref <ref> --store <dir>   # elevated, once per image
```

Then set `ContainerImage` (or `ASPIREHCS_TEST_IMAGE`) to the reference and `ASPIREHCS_STORE` to
`<dir>`. Only Hyper-V isolation is supported.
