<#
.SYNOPSIS
    Builds a probe variant of the Kali Hyper-V base VHDX for AspireHcs testing.

.DESCRIPTION
    Copies the base image (the base is never modified) and applies offline edits via
    wsl --mount. Two variants exist, both test instruments for guest readiness (#5/#11):

    Serial:       adds console=ttyS0 to the kernel cmdline and drops 'quiet splash', so the
                  guest streams its full kernel + systemd log to COM1, which AspireHcs pumps.
                  This is the guest-side reference signal the balloon readiness probe was
                  validated against (balloon S_OK lands at the ttyS0 login prompt, ~9.2 s).

    StaticNoDhcp: Serial, plus NetworkManager masked (the image's only DHCP client) and a
                  self-consistent static config on eth0. The guest boots healthy with a
                  visible NIC and never leases — the probe for WaitForLeasedIpAsync's
                  failure mode.

    Requires WSL 2 with a default distro (uses lsblk/mount/sed inside it) and enough disk
    for a full copy of the base. Writes a provenance JSON next to the output VHDX.

.EXAMPLE
    .\New-KaliProbeVariant.ps1 -BaseVhdx D:\HV\VHD\kali\kali.vhdx `
        -OutputVhdx D:\HV\VHD\probes\kali-serial.vhdx -Variant Serial
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$BaseVhdx,

    [Parameter(Mandatory)]
    [string]$OutputVhdx,

    [Parameter(Mandatory)]
    [ValidateSet('Serial', 'StaticNoDhcp')]
    [string]$Variant
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Test-Path $OutputVhdx) {
    throw "Output already exists: $OutputVhdx. Delete it first; this script never overwrites."
}

# The edits run inside WSL against the mounted ext4 root of OUR attached disk. The disk is
# identified by diffing the device list before/after wsl --mount — never by filesystem label
# alone, which could match an unrelated disk (another attached image, a distro's own volume)
# and would let every post-edit check pass against the wrong filesystem. __TARGET_DISK__ is
# substituted by the PowerShell driver; the payload still verifies the partition looks right.
$mountPreamble = @'
set -eu
disk="__TARGET_DISK__"
part=''
i=0
while [ "$i" -lt 15 ]; do
  name=$(lsblk "/dev/$disk" -nr -o NAME,LABEL,FSTYPE | awk '$2=="root" && $3=="ext4"{print $1}' | head -1)
  if [ -n "$name" ] && [ -b "/dev/$name" ]; then
    part="/dev/$name"
    break
  fi
  i=$((i+1))
  sleep 1
done
echo "root partition: $part (on /dev/$disk)"
[ -b "$part" ] || { echo "FAIL: no ext4 partition labeled 'root' on /dev/$disk" >&2; exit 1; }
mkdir -p /mnt/aspirehcs-probe
mount "$part" /mnt/aspirehcs-probe
trap 'umount /mnt/aspirehcs-probe 2>/dev/null || true' EXIT
R=/mnt/aspirehcs-probe
'@

$serialEdit = @'
sed -i 's|ro  quiet splash|ro console=tty0 console=ttyS0,115200n8|' "$R/boot/grub/grub.cfg"
sed -i 's|GRUB_CMDLINE_LINUX_DEFAULT="quiet"|GRUB_CMDLINE_LINUX_DEFAULT="console=tty0 console=ttyS0,115200n8"|' "$R/etc/default/grub"
# Assert the RESULT, not that sed matched: a silently no-oping sed must fail here. The
# DEFAULT entry (first linux line) must carry the console, and no bootable entry may
# retain 'quiet splash' — console=ttyS0 merely appearing somewhere is not enough.
firstlinux=$(grep -m1 '^\s*linux\s*/boot' "$R/boot/grub/grub.cfg" || true)
case "$firstlinux" in
  *console=ttyS0*) : ;;
  *) echo 'FAIL: default boot entry lacks console=ttyS0' >&2; exit 1 ;;
esac
if grep -q 'quiet splash' "$R/boot/grub/grub.cfg"; then
  echo 'FAIL: an entry still carries quiet splash' >&2; exit 1
fi
echo 'serial edit verified'
'@

$staticEdit = @'
# The static config is only consumed if the image's ifupdown actually sources the
# drop-in directory — verify against the consumer, not our assumption of it.
grep -q 'source /etc/network/interfaces.d' "$R/etc/network/interfaces" \
  || { echo 'FAIL: image does not source interfaces.d; static config would be ignored' >&2; exit 1; }
ln -sf /dev/null "$R/etc/systemd/system/NetworkManager.service"
ln -sf /dev/null "$R/etc/systemd/system/NetworkManager-dispatcher.service"
ln -sf /dev/null "$R/etc/systemd/system/NetworkManager-wait-online.service"
cat > "$R/etc/network/interfaces.d/eth0" <<'EOF'
# AspireHcs never-leases probe: static address, deliberately no DHCP.
auto eth0
iface eth0 inet static
    address 192.168.250.10
    netmask 255.255.255.0
EOF
[ -L "$R/etc/systemd/system/NetworkManager.service" ] || { echo 'FAIL: NetworkManager not masked' >&2; exit 1; }
grep -q 'inet static' "$R/etc/network/interfaces.d/eth0" || { echo 'FAIL: static eth0 config missing' >&2; exit 1; }
echo 'static/no-dhcp edit verified'
'@

$closing = @'
sync
umount /mnt/aspirehcs-probe
trap - EXIT
echo edits-complete
'@

$payload = switch ($Variant) {
    'Serial' { $mountPreamble, $serialEdit, $closing -join "`n" }
    'StaticNoDhcp' { $mountPreamble, $serialEdit, $staticEdit, $closing -join "`n" }
}

Write-Host "Hashing base image (provenance)..."
$baseHash = (Get-FileHash -Algorithm SHA256 -Path $BaseVhdx).Hash

Write-Host "Copying base -> $OutputVhdx ..."
$outputDir = Split-Path $OutputVhdx -Parent
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}
Copy-Item -Path $BaseVhdx -Destination $OutputVhdx

function Get-WslDiskNames {
    $names = wsl -u root -- lsblk -dnr -o NAME 2>$null
    if ($LASTEXITCODE -ne 0) { throw "lsblk inside WSL failed (exit $LASTEXITCODE)." }
    @($names | Where-Object { $_ })
}

$mounted = $false
try {
    $disksBefore = Get-WslDiskNames

    Write-Host "Attaching to WSL..."
    wsl --mount --vhd $OutputVhdx --bare
    if ($LASTEXITCODE -ne 0) { throw "wsl --mount failed (exit $LASTEXITCODE)." }
    $mounted = $true

    # Identify OUR disk by set difference, never by filesystem label — a label match could
    # target an unrelated attached disk and every downstream check would pass against it.
    $newDisks = @(Get-WslDiskNames | Where-Object { $_ -notin $disksBefore })
    if ($newDisks.Count -ne 1) {
        throw "Expected exactly one new WSL block device after attach, found $($newDisks.Count) ($($newDisks -join ', '))."
    }
    $targetDisk = $newDisks[0]
    Write-Host "Attached as /dev/$targetDisk"

    Write-Host "Applying '$Variant' edits..."
    # LF-only: sh chokes on CRLF.
    $payloadLf = ($payload -replace '__TARGET_DISK__', $targetDisk) -replace "`r`n", "`n"
    $payloadLf | wsl -u root -- sh -s
    if ($LASTEXITCODE -ne 0) { throw "in-guest edits failed (exit $LASTEXITCODE)." }
}
finally {
    if ($mounted) {
        wsl --unmount $OutputVhdx
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "wsl --unmount failed (exit $LASTEXITCODE); run 'wsl --unmount' manually before booting the image."
        }
    }
}

# Provenance is the contract this tooling exists for — a build that cannot record it fails
# rather than silently writing nulls.
$scriptCommit = git -C $PSScriptRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or -not $scriptCommit) {
    throw "Cannot record provenance: 'git rev-parse HEAD' failed in $PSScriptRoot."
}
$dirty = git -C $PSScriptRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw "Cannot record provenance: 'git status' failed in $PSScriptRoot." }

$provenance = [ordered]@{
    variant       = $Variant
    baseVhdx      = (Resolve-Path $BaseVhdx).Path
    baseSha256    = $baseHash
    builtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    builtBy       = "$env:USERDOMAIN\$env:USERNAME"
    scriptCommit  = $scriptCommit
    worktreeDirty = [bool]$dirty
    edits         = switch ($Variant) {
        'Serial' { @('kernel cmdline: +console=tty0 +console=ttyS0,115200n8 -quiet -splash') }
        'StaticNoDhcp' {
            @(
                'kernel cmdline: +console=tty0 +console=ttyS0,115200n8 -quiet -splash',
                'NetworkManager (+dispatcher, +wait-online) masked',
                'eth0: ifupdown static 192.168.250.10/24'
            )
        }
    }
}
$provenancePath = [IO.Path]::ChangeExtension($OutputVhdx, '.provenance.json')
$provenance | ConvertTo-Json | Set-Content -Path $provenancePath -Encoding UTF8

Write-Host "Done. Variant: $OutputVhdx"
Write-Host "Provenance: $provenancePath"
