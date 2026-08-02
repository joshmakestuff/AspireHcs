#Requires -Version 7
<#
.SYNOPSIS
Privilege-model proof harness for Windows containers (#33).

.DESCRIPTION
Answers the question the #30 spike could not: is the gate on container layer
storage the wclayer API itself, or merely the ACL on Docker's store?

Unlike Run-XenonProofs.ps1 this script is started UNELEVATED — that is the
condition under test, and it mirrors the developer story being proposed
(a one-time elevated setup, then unelevated `aspire run`). It self-elevates
exactly one phase, via Start-Process -Verb RunAs, and that phase is the setup:

  setup (ELEVATED, one UAC prompt)
    1. export       lift a base layer out of Docker's Administrators-ACLed
                    store into a store the developer owns
    2. CONTROL      boot the exported layer elevated
    3. matrix       record the storage-call matrix at full elevation

  main (UNELEVATED, this session)
    4. matrix       the same storage calls, unelevated, against the same layer
    5. argon boot   run --isolation process
    6. xenon boot   run --isolation hyperv                  (CreateSandboxLayer)
    7. xenon boot   run --isolation hyperv --scratch template
                    the #33 experiment-2 hypothesis: the host never Activates or
                    Prepares a xenon scratch, so if a copied blank template
                    substitutes for CreateSandboxLayer, the Hyper-V-isolated path
                    contains no privilege-gated storage call at all.

Exactly ONE step has a hard expectation: the elevated control (step 2). If a
freshly exported layer will not boot even with full privilege, the export is
broken and every unelevated result below it is meaningless — so the script stops
rather than reporting privilege findings drawn from a bad layer. Every other
step is MEASURED, not asserted: an unelevated failure is the datum, not a bug.

.PARAMETER Layer
Source layer in Docker's store. Omitted, the elevated phase auto-detects the
single windowsfilter directory carrying both Files\ and UtilityVM\Files.

.PARAMETER Store
Root of the developer-owned layer store. Default %LOCALAPPDATA%\AspireHcs\layers.
Resolved in the UNELEVATED session and passed explicitly to the elevated phase,
so the two can never disagree about which profile they mean.

.PARAMETER LogPath
Shared log. Default %TEMP%\AspireHcsPrivilegeProofs\privilege-proofs-<stamp>.log

.PARAMETER Phase
Internal. 'setup' is what the elevated child runs; leave unset.

.PARAMETER SkipSetup
Reuse a layer exported by an earlier run — no UAC prompt, no elevated control.
The record is then missing its control and its elevated comparison; the verdict
says so.
#>
[CmdletBinding()]
param(
    [string]$Layer,
    [string]$Store,
    [string]$LogPath,
    [ValidateSet('setup')][string]$Phase,
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.17763.0\HcsContainerSpike.exe'
$containerId = 'AspireHcsPrivilegeProbe'

if (-not $Store) {
    $Store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AspireHcs\layers'
}
if (-not $LogPath) {
    $logDir = Join-Path $env:TEMP 'AspireHcsPrivilegeProofs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $LogPath = Join-Path $logDir ("privilege-proofs-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Write-Both([string]$Text) {
    Write-Host $Text
    Add-Content -Path $LogPath -Value $Text
}

# Records what a step DID rather than whether it matched an expectation. Only
# steps passed -MustPass are allowed to abort the run.
$script:steps = @()
function Invoke-Step {
    param(
        [string]$Title,
        [string[]]$CommandArgs,
        [switch]$MustPass
    )
    Write-Both ''
    Write-Both "=== $Title (elevated=$isElevated) ==="
    Write-Both "> HcsContainerSpike $($CommandArgs -join ' ')"
    & $exe @CommandArgs 2>&1 | Tee-Object -FilePath $LogPath -Append
    $code = $LASTEXITCODE
    $script:steps += [pscustomobject]@{
        Step     = $Title
        Elevated = $isElevated
        Exit     = $code
        MustPass = [bool]$MustPass
        Ok       = (-not $MustPass) -or ($code -eq 0)
    }
    Write-Both ("--- {0}: exit {1}{2}" -f $Title, $code, ($MustPass ? " (required 0)" : " (measured, no expectation)"))
    return ($code -eq 0)
}

Write-Both "privilege proof run $(Get-Date -Format o)"
Write-Both "log:   $LogPath"
Write-Both "host:  $([Environment]::OSVersion.VersionString) as $(whoami)"
Write-Both "phase: $($Phase ? $Phase : 'main')  elevated: $isElevated  hyperVAdmin: $($principal.IsInRole((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-578'))))"
Write-Both "store: $Store"
try { Write-Both "commit: $(git -C $PSScriptRoot rev-parse --short HEAD) ($(git -C $PSScriptRoot branch --show-current))" } catch { }

# ---------------------------------------------------------------- setup phase --
# Runs elevated, launched by the main phase below (or directly, for debugging).
if ($Phase -eq 'setup') {
    if (-not $isElevated) { Write-Both 'setup phase requires elevation.'; exit 2 }

    if (-not $Layer) {
        $dockerStore = 'C:\ProgramData\Docker\windowsfilter'
        $candidates = @(Get-ChildItem $dockerStore -Directory -ErrorAction SilentlyContinue | Where-Object {
            (Test-Path (Join-Path $_.FullName 'Files')) -and (Test-Path (Join-Path $_.FullName 'UtilityVM\Files'))
        })
        switch ($candidates.Count) {
            0 { Write-Both "No base layer with Files\ + UtilityVM\Files under $dockerStore. Switch Docker Desktop to Windows containers and 'docker pull mcr.microsoft.com/windows/nanoserver:ltsc2025', or pass -Layer."; exit 2 }
            1 { $Layer = $candidates[0].FullName; Write-Both "source layer (auto): $Layer" }
            default {
                Write-Both 'Multiple UtilityVM-bearing layers found — rerun with -Layer <dir>:'
                $candidates | ForEach-Object { Write-Both "  $($_.FullName)" }
                exit 2
            }
        }
    }
    else { Write-Both "source layer (given): $Layer" }

    if (-not (Invoke-Step -Title 'Export' -MustPass -CommandArgs @('export', '--layer', $Layer, '--store', $Store, '--name', 'base'))) {
        Write-Both 'Export failed — nothing downstream would mean anything. Stopping.'
        exit 1
    }

    $exported = Join-Path $Store 'base'

    # THE CONTROL. A freshly exported layer that will not boot with full
    # privilege invalidates every unelevated result, so this is the one step
    # allowed to abort the experiment.
    if (-not (Invoke-Step -Title 'ElevatedControlBoot(xenon)' -MustPass -CommandArgs @('run', '--isolation', 'hyperv', '--layer', $exported, '--id', $containerId))) {
        Write-Both 'CONTROL FAILED: the exported layer does not boot even elevated. The export is broken;'
        Write-Both 'no conclusion may be drawn about privileges from this run. Stopping.'
        exit 1
    }

    [void](Invoke-Step -Title 'ElevatedMatrix' -CommandArgs @('privilege', '--layer', $exported, '--id', $containerId))

    Write-Both ''
    Write-Both '=== Setup phase verdict ==='
    $script:steps | ForEach-Object { Write-Both ("  {0}  elevated={1} exit={2}" -f $_.Step, $_.Elevated, $_.Exit) }
    exit (($script:steps | Where-Object { -not $_.Ok }) ? 1 : 0)
}

# ----------------------------------------------------------------- main phase --
if ($isElevated) {
    Write-Both ''
    Write-Both 'REFUSING TO RUN: this harness must start UNELEVATED — an unelevated session is the'
    Write-Both 'condition under test. Start it from a normal shell; it will prompt for elevation'
    Write-Both 'once, for the setup phase only.'
    exit 2
}

$exported = Join-Path $Store 'base'

if ($SkipSetup) {
    Write-Both ''
    Write-Both "Skipping setup: reusing $exported. No control boot and no elevated comparison in this record."
    if (-not (Test-Path (Join-Path $exported 'Files'))) {
        Write-Both "…but $exported has no Files\ — nothing to probe. Rerun without -SkipSetup."
        exit 2
    }
}
else {
    Write-Both ''
    Write-Both '=== Setup (elevating: expect one UAC prompt) ==='
    # Start-Process joins -ArgumentList with spaces and does NOT quote, so every
    # path is quoted here; an unquoted "C:\Program Files\..." would silently
    # become two arguments and the child would misparse its own store path.
    $q = { param($v) '"' + $v + '"' }
    $childArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (& $q $PSCommandPath),
        '-Phase', 'setup',
        '-Store', (& $q $Store),
        '-LogPath', (& $q $LogPath)
    )
    if ($Layer) { $childArgs += @('-Layer', (& $q $Layer)) }

    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $childArgs -Verb RunAs -Wait -PassThru
    Write-Both "--- setup phase exit: $($child.ExitCode)"
    if ($child.ExitCode -ne 0) {
        Write-Both 'Setup phase failed (see its output above, written to the shared log). Stopping:'
        Write-Both 'without a successful export and control boot there is nothing valid to measure.'
        exit 1
    }
    $script:steps += [pscustomobject]@{ Step = 'SetupPhase(elevated)'; Elevated = $true; Exit = $child.ExitCode; MustPass = $true; Ok = $true }
}

# Everything below is MEASUREMENT. Failures here are the finding.
[void](Invoke-Step -Title 'UnelevatedMatrix' -CommandArgs @('privilege', '--layer', $exported, '--id', $containerId))
[void](Invoke-Step -Title 'UnelevatedArgonBoot' -CommandArgs @('run', '--isolation', 'process', '--layer', $exported, '--id', $containerId))
[void](Invoke-Step -Title 'UnelevatedXenonBoot(api scratch)' -CommandArgs @('run', '--isolation', 'hyperv', '--layer', $exported, '--id', $containerId))
[void](Invoke-Step -Title 'UnelevatedXenonBoot(template scratch)' -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'template', '--layer', $exported, '--id', $containerId))

Write-Both ''
Write-Both '=== Verdict ==='
$script:steps | ForEach-Object {
    Write-Both ("{0}  {1,-38} elevated={2,-5} exit={3}" -f ($_.Ok ? 'OK  ' : 'FAIL'), $_.Step, $_.Elevated, $_.Exit)
}
Write-Both ''
Write-Both 'Reading this record:'
Write-Both '  - Required steps (export, elevated control) must be exit 0 or the run is void.'
Write-Both '  - Unelevated steps are measurements. exit 0 means that path needs no elevation;'
Write-Both '    a nonzero exit names the first call that gated it — read the matrix rows above.'
Write-Both '  - SKIP rows in a matrix were never attempted and prove nothing either way.'
Write-Both ''
Write-Both "log: $LogPath"

$requiredFailed = @($script:steps | Where-Object { -not $_.Ok })
exit ($requiredFailed.Count -gt 0 ? 1 : 0)
