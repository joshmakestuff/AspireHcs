#Requires -Version 7.0
<#
.SYNOPSIS
    Fetches the pinned hcsctl preview drop into tools/hcsctl.

.DESCRIPTION
    AspireHcs drives hcsctl rather than calling HCS directly (see issue #30), and hcsctl's
    packaging story is undecided (hcsctl#35). This script is the interim answer: a fixed,
    hash-verified artifact kept local to the repo.

    The binary is deliberately NOT committed. It is ~10 MB of build output that would live in
    git history forever, and it is reproducible from the release. What is committed is this
    script and the hash it pins — which is what makes the artifact identifiable, since the
    preview binary cannot report its own version (hcsctl#25/#29).

    A hash mismatch aborts and installs nothing. A binary that is not the one this repo was
    tested against is worse than no binary: it fails in ways the test suite has never seen.

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
    [string] $Version = 'v0.1.0-preview.1',

    # Where to put hcsctl.exe. Defaults to tools/hcsctl beside this script's repository.
    [string] $Destination,

    # Re-download even if a verified binary is already in place.
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Published in the release notes for v0.1.0-preview.1. This is the artifact's only identity:
# the preview binary has no version command.
$ExpectedSha256 = '5A74CA1474B1D8B175450C2BC7CE6B2922055B43549D0A6759F65AA72C323350'
$Repository = 'joshmakestuff/hcsctl'
$Asset = "hcsctl-$Version-windows-amd64.zip"

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

    # Verify before installing, never after: a binary that fails this check must not be left
    # somewhere the test suite or an AppHost could pick it up.
    $actual = Get-Sha256 $extracted
    if ($actual -ne $ExpectedSha256) {
        throw "SHA256 mismatch for hcsctl.exe from $Version. Nothing was installed.`n" +
              "  expected $ExpectedSha256`n" +
              "  actual   $actual"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Move-Item -Path $extracted -Destination $target -Force

    Write-Host ""
    Write-Host "Installed hcsctl $Version to $target"
    Write-Host "  SHA256 $actual"
    Write-Host ""
    Write-Host "The test suite finds this automatically. To use it from an AppHost or a shell:"
    Write-Host "  `$env:ASPIREHCS_HCSCTL = '$target'"
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
