<#
.SYNOPSIS
    Builds the AspireHcs Windows Server 2025 guest base VHDX from an ISO, reproducibly.

.DESCRIPTION
    Offline ISO-to-VHDX provisioning adapted from SCED's New-ProvisionedVhd, then a single
    self-terminating burn-in boot, then seal. Phases:

    1. Pin:        the ISO's SHA-256 must match -IsoSha256 or the build fails — same inputs,
                   same image; a different ISO must be a loud decision, not a drift.
                   -SkipIsoHashCheck opts out for bootstrap iteration; the resulting image is
                   NOT reproducibly pinned and its provenance records isoHashVerified = false.
    2. Provision:  New-VHD -> GPT (EFI 200 MB FAT32 / MSR 16 MB / NTFS) -> Expand-WindowsImage
                   -> unattend + bootstrap into the image -> ensure OpenSSH.Server capability
                   (in-box on Server 2025; added from the LOF ISO only if absent) -> bcdboot
                   -> EMS on COM1 in the BCD (kernel-level serial output, the Windows analog
                   of console=ttyS0 — see the Phase 0 findings in ../README.md).
    3. Burn-in:    one Hyper-V boot; the unattend's single autologon runs
                   Initialize-AspireHcsGuest.ps1 (sshd auto-start + firewall, sentinel) and
                   shuts the guest down. All specialize/first-boot churn lands in the base,
                   not in every per-run child diff.
    4. Seal:       verify the sentinel offline, compact (Optimize-VHD), mark read-only,
                   write provenance JSON (ISO SHA, WIM build number, edits, script commit,
                   output SHA-256) beside the image.

    Not sysprep-generalized, deliberately: children are ephemeral per-run VMs on an isolated
    NAT network; a specialized base boots faster and keeps child diffs smaller. Requires the
    Hyper-V PowerShell module (build-time tooling; the AspireHcs runtime needs no such thing).

.EXAMPLE
    # See which editions an ISO offers (Core, Desktop Experience, ...) before choosing.
    .\New-WindowsGuestImage.ps1 -IsoPath E:\isos\server2025.iso -ListImages

.EXAMPLE
    # Desktop Experience, selected by name rather than by a per-ISO index.
    $pw = Read-Host -AsSecureString 'Guest Administrator password'
    .\New-WindowsGuestImage.ps1 `
        -IsoPath E:\isos\server2025.iso -SkipIsoHashCheck `
        -ImageName 'Windows Server 2025 Standard (Desktop Experience)' `
        -SizeGB 60 `
        -OutputVhdx D:\HV\VHD\aspirehcs-guests\winserver2025-desktop.vhdx `
        -AdminPassword $pw

.EXAMPLE
    $pw = Read-Host -AsSecureString 'Guest Administrator password'
    .\New-WindowsGuestImage.ps1 `
        -IsoPath E:\isos\en-us_windows_server_2025_updated_sep_2025_x64_dvd_6d1ad20d.iso `
        -IsoSha256 <pinned-hash> `
        -FodIsoPath E:\isos\26100.1.240331-1435.ge_release_amd64fre_SERVER_LOF_PACKAGES_OEM.iso `
        -OutputVhdx D:\HV\VHD\aspirehcs-guests\winserver2025-core.vhdx `
        -AdminPassword $pw
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$IsoPath,

    # SHA-256 the ISO must have. Get it once with Get-FileHash and pin it in your build notes.
    # Not Mandatory only so -SkipIsoHashCheck can stand alone; still required without it.
    [ValidatePattern('^$|^[0-9a-fA-F]{64}$')]
    [string]$IsoSha256,

    # Skips the ~7 GB read that verifies the ISO, for iterating on the bootstrap where the same
    # ISO is rebuilt repeatedly. The resulting image is NOT reproducibly pinned and its
    # provenance records that, so the shortcut cannot silently outlive the iteration.
    [switch]$SkipIsoHashCheck,

    [ValidateScript({ -not $_ -or (Test-Path $_ -PathType Leaf) })]
    [string]$FodIsoPath,

    # Required whenever the FOD fallback actually consumes the ISO — an input that can alter
    # the sealed image is pinned exactly like the primary ISO.
    [ValidatePattern('^$|^[0-9a-fA-F]{64}$')]
    [string]$FodIsoSha256,

    # Not Mandatory only so -ListImages can run without them; still required to build.
    [string]$OutputVhdx,

    # Baked into the image's Administrator account. This is a TEST FIXTURE image for an
    # isolated NAT network, not a production credential store.
    [SecureString]$AdminPassword,

    [int]$SizeGB = 40,

    # Which edition to install. A Server ISO carries several — Server Core and Desktop
    # Experience variants of each SKU — and the index differs between ISOs, so selecting by
    # name is the safer habit. Run -ListImages against your ISO to see what it offers.
    # Matched EXACTLY, because 'Windows Server 2025 Standard' is a prefix of
    # '...Standard (Desktop Experience)' and a substring match would silently pick one of them.
    [string]$ImageName,

    # 1 was 'Windows Server 2025 Standard' (Core) on the reference ISO — recorded in that
    # image's provenance, not assumed. Layout differs between ISOs, so -ListImages rather than
    # trusting this number.
    [int]$ImageIndex = 1,

    # Prints the editions in the ISO and exits. Needs elevation, because reading the WIM means
    # mounting the ISO.
    [switch]$ListImages,

    [int]$BurnInTimeoutMinutes = 20,
    [string]$BurnInVmName = 'AspireHcsImageBurnIn'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Both name the edition. Silently preferring one would make the ignored parameter look
# honoured, which is worse than refusing.
if ($PSBoundParameters.ContainsKey('ImageName') -and $PSBoundParameters.ContainsKey('ImageIndex')) {
    throw "-ImageName and -ImageIndex both select the edition. Pass one, not both."
}
# Supplied-but-empty is a mistake, not "unspecified": without this, -ImageName '' falls through
# to the index path and silently builds whatever index 1 happens to be.
if ($PSBoundParameters.ContainsKey('ImageName') -and [string]::IsNullOrWhiteSpace($ImageName)) {
    throw "-ImageName is empty. Pass an exact edition name (see -ListImages), or omit it to select by index."
}

if ($ListImages) {
    Write-Host "Editions in $IsoPath"
    $listMount = Mount-DiskImage -ImagePath (Resolve-Path $IsoPath).Path -PassThru
    try {
        $listDrive = ($listMount | Get-Volume).DriveLetter
        $listWim = "${listDrive}:\sources\install.wim"
        if (-not (Test-Path $listWim)) { throw "install.wim not found at $listWim" }
        Get-WindowsImage -ImagePath $listWim |
            Select-Object ImageIndex, ImageName, @{n = 'SizeGB'; e = { [math]::Round($_.ImageSize / 1GB, 2) } } |
            Format-Table -AutoSize | Out-String | Write-Host
        Write-Host "Select one with -ImageName '<exact name>' (or -ImageIndex <n>)."
    }
    finally {
        Dismount-DiskImage -ImagePath (Resolve-Path $IsoPath).Path | Out-Null
    }
    return
}

if (-not $SkipIsoHashCheck -and -not $IsoSha256) {
    throw "-IsoSha256 is required. Pass the ISO's pinned SHA-256, or -SkipIsoHashCheck to build an explicitly unpinned iteration image."
}
if (-not $OutputVhdx) { throw "-OutputVhdx is required (omit it only with -ListImages)." }
if (-not $AdminPassword) { throw "-AdminPassword is required (omit it only with -ListImages)." }
if (Test-Path $OutputVhdx) {
    throw "Output already exists: $OutputVhdx. Delete it first; this script never overwrites."
}
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
    throw "The Hyper-V PowerShell module is required for the burn-in boot (build-time only)."
}
if (Get-VM -Name $BurnInVmName -ErrorAction SilentlyContinue) {
    throw "A VM named '$BurnInVmName' already exists. Remove it or pass -BurnInVmName."
}

# Provenance prerequisites fail loud up front, not after a 10-minute build.
$scriptCommit = git -C $PSScriptRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or -not $scriptCommit) {
    throw "Cannot record provenance: 'git rev-parse HEAD' failed in $PSScriptRoot."
}
$dirty = git -C $PSScriptRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw "Cannot record provenance: 'git status' failed in $PSScriptRoot." }

function Write-Step([string]$Message) { Write-Host "  -> $Message" }

# ---------------------------------------------------------------- Phase 1: pin the input
Write-Host "Phase 1: verifying ISO identity"
if ($SkipIsoHashCheck) {
    # Loud, and permanently attached to the artifact: provenance records isoSha256 = null with
    # isoHashVerified = false, so an image built this way can never be mistaken later for one
    # whose input was pinned. Recording the EXPECTED hash here instead would be a lie — nothing
    # checked it.
    Write-Warning "-SkipIsoHashCheck: the ISO is NOT being verified. This image is for iteration only; its provenance will say so."
    $actualIsoHash = $null
}
else {
    Write-Step "Hashing $IsoPath ..."
    $actualIsoHash = (Get-FileHash -Algorithm SHA256 -Path $IsoPath).Hash
    if ($actualIsoHash -ne $IsoSha256.ToUpperInvariant()) {
        throw "ISO SHA-256 mismatch.`n  expected: $($IsoSha256.ToUpperInvariant())`n  actual:   $actualIsoHash`nRefusing to build from an unpinned input."
    }
    Write-Step "ISO hash verified."
}

# ---------------------------------------------------------------- Phase 2: provision offline
Write-Host "Phase 2: offline provisioning"
$plainPwd = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($AdminPassword))

$outputDir = Split-Path $OutputVhdx -Parent
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$isoMount = $null
$fodMount = $null
$vhdMounted = $false
$capabilityAdded = $false
try {
    Write-Step "Mounting Windows ISO..."
    $isoMount = Mount-DiskImage -ImagePath (Resolve-Path $IsoPath).Path -PassThru
    $isoDrive = ($isoMount | Get-Volume).DriveLetter
    $wimPath = "${isoDrive}:\sources\install.wim"
    if (-not (Test-Path $wimPath)) { throw "install.wim not found at $wimPath" }

    # Read once and use for both selection paths, so a bad index gets the same catalogue a bad
    # name does instead of a raw DISM error.
    $available = @(Get-WindowsImage -ImagePath $wimPath)
    $catalogue = ($available | ForEach-Object { "  [$($_.ImageIndex)] $($_.ImageName)" }) -join "`n"

    if ($ImageName) {
        # Exact, not -like: 'Windows Server 2025 Standard' is a PREFIX of the Desktop
        # Experience name, so a substring match would quietly install the wrong edition.
        $selected = @($available | Where-Object { $_.ImageName -eq $ImageName })
        if ($selected.Count -eq 0) {
            throw "No edition named '$ImageName' in $wimPath. Available:`n$catalogue"
        }
        if ($selected.Count -gt 1) {
            throw "'$ImageName' matches $($selected.Count) indexes in $wimPath; select by -ImageIndex instead."
        }
        $ImageIndex = $selected[0].ImageIndex
        Write-Step "Edition '$ImageName' resolved to WIM index $ImageIndex."
    }
    elseif ($ImageIndex -notin $available.ImageIndex) {
        throw "WIM index $ImageIndex does not exist in $wimPath. Available:`n$catalogue"
    }

    $wimInfo = Get-WindowsImage -ImagePath $wimPath -Index $ImageIndex
    Write-Step "WIM index ${ImageIndex}: $($wimInfo.ImageName), version $($wimInfo.Version)"

    # Asserts only what this script actually supports — Server 2025, whose unattend, EMS
    # settings and bootstrap this tooling is written against. It deliberately does NOT insist on
    # Server Core any more: Desktop Experience is a legitimate choice (and the more useful one
    # over RDP), and the output is labelled from the WIM's own ImageName, so whichever edition
    # is selected describes itself honestly in the provenance rather than being mislabelled.
    if ($wimInfo.ImageName -notmatch 'Server 2025') {
        throw "WIM index $ImageIndex is '$($wimInfo.ImageName)', which is not a Windows Server 2025 edition. This script's unattend and EMS configuration are written for Server 2025."
    }

    Write-Step "Creating VHDX ($SizeGB GB dynamic)..."
    New-VHD -Path $OutputVhdx -SizeBytes ($SizeGB * 1GB) -Dynamic | Out-Null
    $disk = Mount-VHD -Path $OutputVhdx -Passthru | Get-Disk
    $vhdMounted = $true
    $diskNumber = $disk.Number

    Write-Step "GPT partitioning..."
    Initialize-Disk -Number $diskNumber -PartitionStyle GPT
    $efiPartition = New-Partition -DiskNumber $diskNumber -Size 200MB `
        -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    Format-Volume -Partition $efiPartition -FileSystem FAT32 `
        -NewFileSystemLabel 'System' -Confirm:$false | Out-Null
    $efiPartition | Add-PartitionAccessPath -AssignDriveLetter
    $efiLetter = ($efiPartition | Get-Partition).DriveLetter

    New-Partition -DiskNumber $diskNumber -Size 16MB `
        -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null

    $winPartition = New-Partition -DiskNumber $diskNumber -UseMaximumSize
    Format-Volume -Partition $winPartition -FileSystem NTFS `
        -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
    $winPartition | Add-PartitionAccessPath -AssignDriveLetter
    $winLetter = ($winPartition | Get-Partition).DriveLetter

    Write-Step "Applying WIM (takes a few minutes)..."
    Expand-WindowsImage -ImagePath $wimPath -Index $ImageIndex `
        -ApplyPath "${winLetter}:\" -CheckIntegrity | Out-Null

    Write-Step "Placing unattend.xml and bootstrap script..."
    # String.Replace, not -replace: regex substitution would silently transform '$$'/'$&'
    # sequences inside the password before it ever reaches the answer file.
    $template = Get-Content (Join-Path $PSScriptRoot 'unattend.template.xml') -Raw
    $unattend = $template.Replace('__ADMIN_PASSWORD__', [System.Security.SecurityElement]::Escape($plainPwd))
    $pantherDir = "${winLetter}:\Windows\Panther"
    New-Item -ItemType Directory -Path $pantherDir -Force | Out-Null
    Set-Content -Path "$pantherDir\unattend.xml" -Value $unattend -Encoding UTF8

    $scriptsDir = "${winLetter}:\Windows\Setup\Scripts"
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'Initialize-AspireHcsGuest.ps1') "$scriptsDir\Initialize-AspireHcsGuest.ps1"

    # OpenSSH: reportedly in-box on Server 2025 — verified against the applied image, not
    # trusted. Only reach for the LOF ISO if the capability is genuinely absent. dism.exe,
    # not the DISM cmdlets: offline -Path servicing throws 'Class not registered' under
    # PowerShell 7 (the same wall SCED's builder shells out to dism.exe for).
    $openSshCapability = 'OpenSSH.Server~~~~0.0.1.0'
    function Get-OfflineCapabilityState([string]$ImageRoot, [string]$CapabilityName) {
        $out = & dism.exe /English /Image:$ImageRoot /Get-CapabilityInfo /CapabilityName:$CapabilityName
        if ($LASTEXITCODE -ne 0) {
            throw "dism /Get-CapabilityInfo failed (exit $LASTEXITCODE):`n$($out -join "`n")"
        }
        $stateLine = @($out | Where-Object { $_ -match '^State : ' })
        if (-not $stateLine) { throw "Could not parse capability state from dism output:`n$($out -join "`n")" }
        ($stateLine[0] -replace '^State : ', '').Trim()
    }

    Write-Step "Checking $openSshCapability state..."
    $capState = Get-OfflineCapabilityState "${winLetter}:\" $openSshCapability
    Write-Step "OpenSSH.Server state: $capState"
    if ($capState -ne 'Installed') {
        if (-not $FodIsoPath) {
            throw "OpenSSH.Server is '$capState' in this image and no -FodIsoPath was provided to install it from."
        }
        # UNVERIFIED BRANCH: Server 2025 ships OpenSSH in-box, so no build on the reference
        # machine has ever taken this path. First exercised when an ISO without the capability
        # appears; until then treat it as best-effort and read the build log closely.
        if (-not $FodIsoSha256) {
            throw "The FOD fallback is about to service the image from $FodIsoPath but no -FodIsoSha256 pin was provided."
        }
        Write-Step "Verifying and mounting LOF ISO, installing OpenSSH.Server offline..."
        $fodIsoHash = (Get-FileHash -Algorithm SHA256 -Path $FodIsoPath).Hash
        if ($fodIsoHash -ne $FodIsoSha256.ToUpperInvariant()) {
            throw "FOD ISO SHA-256 mismatch.`n  expected: $($FodIsoSha256.ToUpperInvariant())`n  actual:   $fodIsoHash"
        }
        $fodMount = Mount-DiskImage -ImagePath (Resolve-Path $FodIsoPath).Path -PassThru
        $fodDrive = ($fodMount | Get-Volume).DriveLetter
        $fodSource = "${fodDrive}:\LanguagesAndOptionalFeatures"
        if (-not (Test-Path $fodSource)) { throw "LanguagesAndOptionalFeatures not found on the FOD ISO." }
        $dismOut = & dism.exe /English /Image:"${winLetter}:\" /Add-Capability /CapabilityName:$openSshCapability /Source:$fodSource /LimitAccess
        if ($LASTEXITCODE -ne 0) {
            throw "dism /Add-Capability failed (exit $LASTEXITCODE):`n$($dismOut -join "`n")"
        }
        $capabilityAdded = $true
    }
    $capState = Get-OfflineCapabilityState "${winLetter}:\" $openSshCapability
    if ($capState -ne 'Installed') {
        throw "OpenSSH.Server is still '$capState' after provisioning — the image cannot serve as the TCP fixture."
    }
    Write-Step "OpenSSH.Server verified Installed."

    Write-Step "Writing UEFI boot files (bcdboot)..."
    $proc = Start-Process -FilePath bcdboot `
        -ArgumentList "${winLetter}:\Windows", '/s', "${efiLetter}:", '/f', 'UEFI' `
        -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "bcdboot failed (exit $($proc.ExitCode))." }

    # EMS on COM1: kernel-level serial output from the first moments of boot, which AspireHcs
    # already pumps — the readiness/debugging channel Phase 0 proved out on Linux.
    Write-Step "Enabling EMS on COM1 in the BCD..."
    $bcdStore = "${efiLetter}:\EFI\Microsoft\Boot\BCD"
    & bcdedit /store $bcdStore /emssettings EMSPORT:1 EMSBAUDRATE:115200 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "bcdedit /emssettings failed (exit $LASTEXITCODE)." }
    & bcdedit /store $bcdStore /ems '{default}' on | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "bcdedit /ems failed (exit $LASTEXITCODE)." }

    # Assert the result, not the step: the default entry must actually carry ems=Yes.
    $emsState = & bcdedit /store $bcdStore /enum '{default}'
    if ($LASTEXITCODE -ne 0 -or -not ($emsState -match '^\s*ems\s+Yes')) {
        throw "BCD does not show ems=Yes on the default entry after editing."
    }
    Write-Step "EMS verified on."
}
finally {
    if ($vhdMounted) { Dismount-VHD -Path $OutputVhdx }
    if ($fodMount) { Dismount-DiskImage -ImagePath (Resolve-Path $FodIsoPath).Path | Out-Null }
    if ($isoMount) { Dismount-DiskImage -ImagePath (Resolve-Path $IsoPath).Path | Out-Null }
    $plainPwd = $null
}

# ---------------------------------------------------------------- Phase 3: burn-in boot
Write-Host "Phase 3: burn-in boot (specialize + first logon, self-terminating)"
$vm = $null
try {
    # No NIC and no secure boot: the burn-in needs neither, and the boot environment should
    # resemble the consumer's (AspireHcs compute systems boot UEFI without secure boot).
    $vm = New-VM -Name $BurnInVmName -Generation 2 -MemoryStartupBytes 2GB -VHDPath $OutputVhdx
    Set-VMFirmware -VM $vm -EnableSecureBoot Off
    Set-VM -VM $vm -ProcessorCount 2 -StaticMemory -AutomaticCheckpointsEnabled $false
    Start-VM -VM $vm

    Write-Step "Waiting for the guest to configure itself and power off (timeout $BurnInTimeoutMinutes min)..."
    $deadline = (Get-Date).AddMinutes($BurnInTimeoutMinutes)
    while ((Get-Date) -lt $deadline -and (Get-VM -Name $BurnInVmName).State -ne 'Off') {
        Start-Sleep -Seconds 10
    }
    if ((Get-VM -Name $BurnInVmName).State -ne 'Off') {
        throw "Burn-in did not complete within $BurnInTimeoutMinutes minutes. Connect with 'vmconnect localhost $BurnInVmName' to inspect, then remove the VM."
    }
    Write-Step "Guest powered itself off."
}
finally {
    if ($vm -and (Get-VM -Name $BurnInVmName -ErrorAction SilentlyContinue)) {
        if ((Get-VM -Name $BurnInVmName).State -eq 'Off') {
            Remove-VM -Name $BurnInVmName -Force
        }
        else {
            Write-Warning "Leaving VM '$BurnInVmName' for inspection; remove it manually."
        }
    }
}

# ---------------------------------------------------------------- Phase 4: verify + seal
Write-Host "Phase 4: verify sentinel, compact, seal"
$sentinel = $null
$disk = Mount-VHD -Path $OutputVhdx -Passthru -ReadOnly | Get-Disk
try {
    $winVolume = ($disk | Get-Partition | Get-Volume | Where-Object FileSystemLabel -eq 'Windows')
    if (-not $winVolume) { throw "Windows volume not found in the built image." }
    $sentinelPath = "$($winVolume.DriveLetter):\aspirehcs-init.json"
    if (-not (Test-Path $sentinelPath)) {
        throw "Burn-in sentinel missing ($sentinelPath) — the bootstrap never ran to completion."
    }
    $sentinel = Get-Content $sentinelPath -Raw | ConvertFrom-Json
    if (-not $sentinel.ok) {
        throw "Bootstrap recorded failure: $($sentinel.error). See bootstrap-transcript.log inside the image."
    }
    if ($sentinel.sshd -ne 'Running') {
        throw "Bootstrap completed but recorded sshd='$($sentinel.sshd)' — the image's advertised SSH fixture was not serving at burn-in."
    }
    # Both fixtures are gated on an observed listener owned by the expected service. A service
    # reporting Running proves it started, not that anything is accepting connections, and a
    # registry value that was set proves less than that.
    if ($sentinel.sshdListening -ne 'Listening') {
        throw "Bootstrap recorded sshdListening='$($sentinel.sshdListening)' — sshd was not accepting connections on 22 at burn-in."
    }
    if ($sentinel.rdp -ne 'Listening') {
        throw "Bootstrap recorded rdp='$($sentinel.rdp)' — TermService was not accepting connections on 3389, so this image cannot back the Connect (RDP) command."
    }
    # A listener inside the guest is NOT reachability: the 2026-08-03 images sealed with
    # rdp='Listening' and were still unreachable from the host, because the firewall rules had
    # been switched back off. Local sockets are visible regardless of the firewall, so the
    # listener probe alone can never catch that.
    if ($sentinel.rdpFirewallEnabled -lt 1 -or $sentinel.rdpFirewallEnabled -ne $sentinel.rdpFirewallRules) {
        throw "Bootstrap recorded $($sentinel.rdpFirewallEnabled)/$($sentinel.rdpFirewallRules) Remote Desktop firewall rules enabled — the guest would listen on 3389 but drop inbound connections."
    }
    Write-Step "Sentinel ok: sshd=$($sentinel.sshd)/$($sentinel.sshdListening), rdp=$($sentinel.rdp), completed $($sentinel.completedUtc)"
}
finally {
    Dismount-VHD -Path $OutputVhdx
}

Write-Step "Compacting..."
Optimize-VHD -Path $OutputVhdx -Mode Full

Write-Step "Hashing final image..."
$outputHash = (Get-FileHash -Algorithm SHA256 -Path $OutputVhdx).Hash

Set-ItemProperty -Path $OutputVhdx -Name IsReadOnly -Value $true

$provenance = [ordered]@{
    # Derived from the WIM's own metadata — whichever edition was selected, so the label cannot
    # drift from what was actually installed. Provisioning asserts Server 2025; the edition
    # within it is the caller's choice.
    image          = "$($wimInfo.ImageName) (AspireHcs guest base)"
    isoPath        = (Resolve-Path $IsoPath).Path
    # Null when unverified. An image whose input was never checked must not carry a hash that
    # looks like evidence.
    isoSha256        = $actualIsoHash
    isoHashVerified  = -not $SkipIsoHashCheck
    wimIndex       = $ImageIndex
    wimImageName   = $wimInfo.ImageName
    wimVersion     = $wimInfo.Version.ToString()
    openSshSource  = if ($capabilityAdded) { 'LOF ISO (offline DISM)' } else { 'in-box' }
    fodIso         = if ($capabilityAdded) {
        @{ path = (Resolve-Path $FodIsoPath).Path; sha256 = $fodIsoHash }
    } else { $null }
    builtUtc       = (Get-Date).ToUniversalTime().ToString('o')
    builtBy        = "$env:USERDOMAIN\$env:USERNAME"
    scriptCommit   = $scriptCommit
    worktreeDirty  = [bool]$dirty
    burnIn         = @{
        sshd                = $sentinel.sshd
        sshdListening       = $sentinel.sshdListening
        rdp                 = $sentinel.rdp
        rdpFirewallEnabled  = $sentinel.rdpFirewallEnabled
        rdpFirewallRules    = $sentinel.rdpFirewallRules
        rdpFirewallProfiles = $sentinel.rdpFirewallProfiles
        completedUtc        = $sentinel.completedUtc
    }
    edits          = @(
        'unattend: specialized, OOBE skipped, single autologon consumed by burn-in',
        'sshd: StartupType Automatic, firewall OpenSSH-Server-In-TCP profile Any',
        'RDP: fDenyTSConnections=0, TermService Automatic, firewall group @FirewallAPI.dll,-28752 enabled profile Any',
        'BCD: ems on, emssettings COM1 115200',
        'sealed: Optimize-VHD Full, file marked read-only'
    )
    outputSha256   = $outputHash
}
$provenancePath = [IO.Path]::ChangeExtension($OutputVhdx, '.provenance.json')
$provenance | ConvertTo-Json | Set-Content -Path $provenancePath -Encoding UTF8

Write-Host "Done."
Write-Host "  Image:      $OutputVhdx (read-only)"
Write-Host "  SHA-256:    $outputHash"
Write-Host "  Provenance: $provenancePath"
