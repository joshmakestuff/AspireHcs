#Requires -Version 7.0
<#
.SYNOPSIS
    Fetches the pinned hcsctl preview drop into tools/hcsctl.

.DESCRIPTION
    AspireHcs drives hcsctl and does not call HCS directly. This script installs a fixed,
    hash-verified hcsctl binary local to the repo.

    The binary is NOT committed. This script and the hash it pins are.

    A hash mismatch aborts and installs nothing.

.EXAMPLE
    ./eng/Get-HcsCtl.ps1
    Downloads, verifies and installs the pinned version if it is not already there.

.EXAMPLE
    ./eng/Get-HcsCtl.ps1 -Force
    Re-downloads even when a matching binary is already installed.
#>
[CmdletBinding()]
param(
    # The hcsctl release tag to fetch. Changing this requires changing ExpectedSha256 too.
    [string] $Version = 'v0.3.0',

    # Where to put hcsctl.exe. Defaults to tools/hcsctl beside this script's repository.
    [string] $Destination,

    # Re-download even if a verified binary is already in place.
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The hash of hcsctl.exe ITSELF, not of the zip around it. The release's SHA256SUMS covers the
# zip, so the two numbers differ; do not paste one where the other belongs.
#
# The binary also reports its own version; the install is checked both ways.
$ExpectedSha256 = '259E59E945D796B70636F935BE537C9E288150A702C2A82D026AE631CCC09FCD'
$Repository = 'joshmakestuff/hcsctl'

# The asset name carries no version. Check it when changing $Version.
$Asset = 'hcsctl-windows-amd64.zip'

if (-not $Destination) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $Destination = Join-Path $repoRoot 'tools' 'hcsctl'
}

$target = Join-Path $Destination 'hcsctl.exe'

function Get-Sha256($path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

if ((Test-Path $target) -and -not $Force) {
    $actual = Get-Sha256 $target
    if ($actual -eq $ExpectedSha256) {
        Write-Host "hcsctl $Version already installed and verified: $target"
        Write-Host "  SHA256 $actual"
        exit 0
    }

    Write-Host "Replacing the binary at $target -- its hash does not match $Version."
    Write-Host "  found    $actual"
    Write-Host "  expected $ExpectedSha256"
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI (gh) is required to download from $Repository, which is private. " +
          "Install it and run 'gh auth login', or download $Asset by hand and extract hcsctl.exe to $Destination."
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "aspirehcs-hcsctl-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    Write-Host "Downloading $Asset from $Repository@$Version ..."
    & gh release download $Version --repo $Repository --pattern $Asset --dir $staging
    if ($LASTEXITCODE -ne 0) {
        throw "gh release download failed with exit code $LASTEXITCODE."
    }

    $zip = Join-Path $staging $Asset
    if (-not (Test-Path $zip)) {
        throw "gh reported success but $Asset is not in $staging."
    }

    Expand-Archive -Path $zip -DestinationPath $staging -Force

    $extracted = Join-Path $staging 'hcsctl.exe'
    if (-not (Test-Path $extracted)) {
        throw "$Asset did not contain hcsctl.exe."
    }

    # Verify before installing: a binary that fails this check must not be left where the test
    # suite or an AppHost could pick it up.
    $actual = Get-Sha256 $extracted
    if ($actual -ne $ExpectedSha256) {
        throw "SHA256 mismatch for hcsctl.exe from $Version. Nothing was installed.`n" +
              "  expected $ExpectedSha256`n" +
              "  actual   $actual"
    }

    # The hash proves this is the pinned artifact. The reported version proves the pin names the
    # version it says it does: a mismatch means a release built from the wrong commit or an
    # unstamped build.
    $reported = (& $extracted version --json | ConvertFrom-Json).toolVersion
    if ($reported -ne $Version) {
        throw "hcsctl from $Version reports its version as '$reported'. Nothing was installed."
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Move-Item -Path $extracted -Destination $target -Force

    Write-Host ""
    Write-Host "Installed hcsctl $reported to $target"
    Write-Host "  SHA256 $actual"
    Write-Host ""
    Write-Host "The test suite finds this automatically. To use it from an AppHost or a shell:"
    Write-Host "  `$env:ASPIREHCS_HCSCTL = '$target'"
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
