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

# The edits run inside WSL against the mounted ext4 root, located by its 'root' label with a
# retry loop (a cold-started distro can need a moment before block devices are queryable).
$mountPreamble = @'
set -eu
dev=''
i=0
while [ "$i" -lt 15 ]; do
  name=$(lsblk -o NAME,LABEL -nr | awk '$2=="root"{print $1}' | head -1)
  if [ -n "$name" ] && [ -b "/dev/$name" ]; then
    dev="/dev/$name"
    break
  fi
  i=$((i+1))
  sleep 1
done
echo "root partition: $dev"
[ -b "$dev" ]
mkdir -p /mnt/aspirehcs-probe
mount "$dev" /mnt/aspirehcs-probe
trap 'umount /mnt/aspirehcs-probe 2>/dev/null || true' EXIT
R=/mnt/aspirehcs-probe
'@

$serialEdit = @'
sed -i 's|ro  quiet splash|ro console=tty0 console=ttyS0,115200n8|' "$R/boot/grub/grub.cfg"
sed -i 's|GRUB_CMDLINE_LINUX_DEFAULT="quiet"|GRUB_CMDLINE_LINUX_DEFAULT="console=tty0 console=ttyS0,115200n8"|' "$R/etc/default/grub"
# Assert the RESULT, not that sed matched: a silently no-oping sed must fail here.
grep -q 'console=ttyS0' "$R/boot/grub/grub.cfg" || { echo 'FAIL: console=ttyS0 not present in grub.cfg' >&2; exit 1; }
echo 'serial edit verified'
'@

$staticEdit = @'
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

$mounted = $false
try {
    Write-Host "Attaching to WSL..."
    wsl --mount --vhd $OutputVhdx --bare
    if ($LASTEXITCODE -ne 0) { throw "wsl --mount failed (exit $LASTEXITCODE)." }
    $mounted = $true

    Write-Host "Applying '$Variant' edits..."
    # LF-only: sh chokes on CRLF.
    $payloadLf = $payload -replace "`r`n", "`n"
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

$provenance = [ordered]@{
    variant       = $Variant
    baseVhdx      = (Resolve-Path $BaseVhdx).Path
    baseSha256    = $baseHash
    builtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    builtBy       = "$env:USERDOMAIN\$env:USERNAME"
    scriptCommit  = (git -C $PSScriptRoot rev-parse HEAD 2>$null)
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
