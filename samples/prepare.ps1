<#
.SYNOPSIS
One-time setup for the sample: publishes HcsSample.GuestApi and imports the container image.

.DESCRIPTION
Two steps, both idempotent:

1. dotnet publish HcsSample.GuestApi (self-contained win-x64) into the directory the AppHost
   bind-mounts into the container.
2. hcsctl image pull + import for the sample's default image. The import is the one step that
   needs elevation; when this script runs unelevated it relaunches just that step elevated.

After this, `aspire run` (or `dotnet run`) in HcsSample.AppHost works with nothing else set.

.PARAMETER Image
Image reference to pull and import. Default: the sample's default image.

.PARAMETER Store
hcsctl store directory. Default: $env:ASPIREHCS_STORE if set, otherwise hcsctl's per-user store.
#>
[CmdletBinding()]
param(
    [string]$Image = 'mcr.microsoft.com/windows/nanoserver:ltsc2025',
    [string]$Store = $env:ASPIREHCS_STORE
)

$ErrorActionPreference = 'Stop'
$samples = $PSScriptRoot

# ---- 1. Publish the guest app ----------------------------------------------------------------
$publishDir = Join-Path $samples 'HcsSample.GuestApi\bin\publish'
Write-Host "Publishing HcsSample.GuestApi to $publishDir ..."
dotnet publish (Join-Path $samples 'HcsSample.GuestApi') -c Release -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ---- 2. Pull and import the image ------------------------------------------------------------
# Resolution mirrors the AppHost: ASPIREHCS_HCSCTL, then PATH, then the repo's pinned drop in
# tools\hcsctl — fetched (hash-verified) on demand, so a fresh clone needs no setup at all.
$hcsctl = if ($env:ASPIREHCS_HCSCTL) {
    if (Test-Path $env:ASPIREHCS_HCSCTL -PathType Container) { Join-Path $env:ASPIREHCS_HCSCTL 'hcsctl.exe' }
    else { $env:ASPIREHCS_HCSCTL }
} else {
    (Get-Command hcsctl.exe -ErrorAction SilentlyContinue)?.Source
}
if (-not $hcsctl -or -not (Test-Path $hcsctl)) {
    $pinned = Join-Path (Split-Path $samples -Parent) 'tools\hcsctl\hcsctl.exe'
    if (-not (Test-Path $pinned)) {
        Write-Host 'Fetching the pinned hcsctl drop into tools\hcsctl ...'
        & (Join-Path (Split-Path $samples -Parent) 'eng\Get-HcsCtl.ps1')
    }
    $hcsctl = $pinned
}
if (-not (Test-Path $hcsctl)) {
    throw 'hcsctl.exe was not found. Put it on PATH, point ASPIREHCS_HCSCTL at it, or run ' +
          'eng\Get-HcsCtl.ps1. Releases: https://github.com/joshmakestuff/hcsctl/releases'
}

$storeArgs = if ($Store) { @('--store', $Store) } else { @() }

Write-Host "Pulling $Image ..."
& $hcsctl image pull --ref $Image @storeArgs
if ($LASTEXITCODE -ne 0) { throw "hcsctl image pull failed ($LASTEXITCODE)." }

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "Importing $Image (elevated) ..."
if ($elevated) {
    & $hcsctl image import --ref $Image @storeArgs
    if ($LASTEXITCODE -ne 0) { throw "hcsctl image import failed ($LASTEXITCODE)." }
} else {
    # Only the import needs elevation; relaunch exactly that command and wait for it.
    $importArgs = @('image', 'import', '--ref', $Image) + $storeArgs
    $process = Start-Process -FilePath $hcsctl -ArgumentList $importArgs -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "hcsctl image import failed ($($process.ExitCode))." }
}

Write-Host ''
Write-Host 'Done. Run the sample:'
Write-Host "  cd $(Join-Path $samples 'HcsSample.AppHost')"
Write-Host '  aspire run   # or: dotnet run'
if ($Store) {
    Write-Host "The AppHost finds the store through ASPIREHCS_STORE; keep it set to '$Store'."
}
