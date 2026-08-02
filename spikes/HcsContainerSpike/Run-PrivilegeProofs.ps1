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
    1. grant        make the layer readable by the current user IN PLACE
    2. CONTROL      boot that layer elevated, once per isolation mode
    3. matrix       record the storage-call matrix at full elevation

Granting in place rather than copying the layer elsewhere is deliberate, and
was arrived at empirically: the first design exported the layer into a store
the developer owns, and ExportLayer returned 0x80070057 against a base layer.
hcsshim says why — NewLayerReader branches away from ExportLayer entirely when
there are no parent layers ("This is a base layer. It gets exported
differently."). Materializing a base layer into your own store is the OCI-tar
import path, i.e. image-acquisition work, not this question.

Granting in place is also the cleaner experiment: #33 asks whether the gate is
the wclayer API or the store ACL, and this changes exactly that one variable
while the layer bits stay byte-for-byte the ones already proven to boot green
as both argon and xenon on this host.

  main (UNELEVATED, this session)
    4. matrix       the same storage calls, unelevated, against the same layer
    5. argon boot   run --isolation process
    6. xenon boot   run --isolation hyperv                  (CreateSandboxLayer)
    7. xenon boot   run --isolation hyperv --scratch template
                    the #33 experiment-2 hypothesis: the host never Activates or
                    Prepares a xenon scratch, so if a copied blank template
                    substitutes for CreateSandboxLayer, the Hyper-V-isolated path
                    contains no privilege-gated storage call at all.

Three steps have hard expectations, all in the setup phase: the grant itself,
and an elevated control boot of EACH isolation mode that the main phase then
measures unelevated. If the layer will not boot with full privilege, something
is wrong with the layer or the host rather than with privileges, and every
unelevated result below it is meaningless — so the script stops rather than
reporting privilege findings drawn from a bad layer. Controls must cover both
isolation modes: without an elevated argon control, an unelevated argon failure
is indistinguishable from an argon-path defect in the layer itself.

The template-scratch path is run at BOTH privilege levels, but neither run is a
control — it tests the #33 experiment-2 hypothesis, and a hypothesis that turns
out false is a finding, not an invalid experiment. Both runs are needed to tell
the two failure meanings apart:

  elevated OK, unelevated fails  -> a privilege gate, which is the finding sought
  both fail                      -> template substitution does not work at all
  both OK                        -> the xenon path needs no privileged storage call

With only the unelevated run, those first two are indistinguishable.

Every other step is MEASURED, not asserted: an unelevated failure is the datum,
not a bug. Measured steps are reported as MEAS with their exit code and never as
OK or FAIL, because a measured nonzero exit is a result, not a failure — and
showing it as OK would misreport the very thing being measured.

.PARAMETER Layer
The layer to grant access to and then measure, in Docker's windowsfilter store.
REQUIRED: the layer is measured where it lives, so the unelevated session must
know which one it will probe. Pass a base image directory carrying both Files\
and UtilityVM\Files.

.PARAMETER LogPath
Shared log. Default %TEMP%\AspireHcsPrivilegeProofs\privilege-proofs-<stamp>.log

.PARAMETER Phase
Internal. 'setup' is what the elevated child runs; leave unset.

.PARAMETER SkipSetup
Reuse a layer already granted by an earlier run — no UAC prompt, no elevated control.
The record is then missing its control and its elevated comparison; the verdict
says so.
#>
[CmdletBinding()]
param(
    [string]$Layer,
    [string]$LogPath,
    [ValidateSet('setup')][string]$Phase,
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.17763.0\HcsContainerSpike.exe'
$containerId = 'AspireHcsPrivilegeProbe'

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
    # Out-Host, not a bare Tee-Object: Tee-Object PASSES ITS INPUT THROUGH, so the
    # command's output would become part of this function's return value. Callers
    # do `if (-not (Invoke-Step ...))`, and a non-empty array is truthy in
    # PowerShell — so every such guard silently never fired. Observed live: the
    # setup phase ran all three control boots after the export had already failed.
    # Out-Host writes to the console and emits nothing, leaving the boolean below
    # as the only pipeline output.
    & $exe @CommandArgs 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Host
    $code = $LASTEXITCODE
    $script:steps += [pscustomobject]@{
        Step     = $Title
        Elevated = $isElevated
        Exit     = $code
        MustPass = [bool]$MustPass
        # Only required steps carry a pass/fail verdict. Measured steps get $null
        # so the verdict table can render them as MEAS: a measured nonzero exit is
        # the finding, and printing it as OK (or as FAIL) would misreport it.
        Ok       = $MustPass ? ($code -eq 0) : $null
    }
    Write-Both ("--- {0}: exit {1}{2}" -f $Title, $code, ($MustPass ? " (required 0)" : " (measured, no expectation)"))
    return ($code -eq 0)
}

Write-Both "privilege proof run $(Get-Date -Format o)"
Write-Both "log:   $LogPath"
Write-Both "host:  $([Environment]::OSVersion.VersionString) as $(whoami)"
Write-Both "phase: $($Phase ? $Phase : 'main')  elevated: $isElevated  hyperVAdmin: $($principal.IsInRole((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-578'))))"
try { Write-Both "commit: $(git -C $PSScriptRoot rev-parse --short HEAD) ($(git -C $PSScriptRoot branch --show-current))" } catch { }

# ---------------------------------------------------------------- setup phase --
# Runs elevated, launched by the main phase below (or directly, for debugging).
if ($Phase -eq 'setup') {
    if (-not $isElevated) { Write-Both 'setup phase requires elevation.'; exit 2 }

    # Deliberately does NOT build: the unelevated parent already did, and building
    # here would leave Administrator-owned obj\/bin\ artifacts that break the next
    # unelevated build. Assert the binary the parent produced is present instead.
    if (-not (Test-Path $exe)) {
        Write-Both "setup phase: $exe is missing — the unelevated phase should have built it. Stopping."
        exit 2
    }
    Write-Both "exe:   $exe (built $((Get-Item $exe).LastWriteTime.ToString('o')))"

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

    if (-not (Invoke-Step -Title 'Grant' -MustPass -CommandArgs @('grant', '--layer', $Layer))) {
        Write-Both 'Grant failed — nothing downstream would mean anything. Stopping.'
        exit 1
    }

    # The layer stays where it is; only its ACL changed. That is the single
    # variable #33 asks about, and these are bits already proven to boot green.
    $exported = $Layer

    # THE CONTROL. A freshly exported layer that will not boot with full
    # privilege invalidates every unelevated result, so this is the one step
    # allowed to abort the experiment.
    # One control per isolation mode the main phase measures. Without the argon
    # control an unelevated argon failure could equally be an argon-path defect in
    # the exported layer, and the record could not tell the two apart.
    foreach ($mode in @('hyperv', 'process')) {
        if (-not (Invoke-Step -Title "ElevatedControlBoot($mode)" -MustPass -CommandArgs @('run', '--isolation', $mode, '--layer', $exported, '--id', $containerId))) {
            Write-Both "CONTROL FAILED: the layer does not boot as --isolation $mode even elevated."
            Write-Both 'Something is wrong with the layer or the host, not with privileges; no conclusion may'
            Write-Both 'be drawn about privileges from this run. Stopping.'
            exit 1
        }
    }

    # Measured, not a control: its failure would mean the hypothesis is false,
    # not that the experiment is invalid. It runs here so the unelevated result
    # below it has an elevated counterpart to be read against.
    [void](Invoke-Step -Title 'ElevatedXenonBoot(template scratch)' -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'template', '--layer', $exported, '--id', $containerId))

    [void](Invoke-Step -Title 'ElevatedMatrix' -CommandArgs @('privilege', '--layer', $exported, '--id', $containerId))

    Write-Both ''
    Write-Both '=== Setup phase verdict ==='
    $script:steps | ForEach-Object { Write-Both ("  {0}  elevated={1} exit={2}" -f $_.Step, $_.Elevated, $_.Exit) }
    exit ((@($script:steps | Where-Object { $_.MustPass -and -not $_.Ok }).Count -gt 0) ? 1 : 0)
}

# ----------------------------------------------------------------- main phase --
if ($isElevated) {
    Write-Both ''
    Write-Both 'REFUSING TO RUN: this harness must start UNELEVATED — an unelevated session is the'
    Write-Both 'condition under test. Start it from a normal shell; it will prompt for elevation'
    Write-Both 'once, for the setup phase only.'
    exit 2
}

# Build BEFORE anything is measured. The log records the source commit, so a run
# against a stale bin\Debug binary would attribute results to code that never ran
# — the exact class of unverified claim this harness exists to avoid. Built here,
# unelevated, so no Administrator-owned artifacts land in obj\ or bin\.
Write-Both ''
Write-Both '=== Build ==='
dotnet build (Join-Path $PSScriptRoot 'HcsContainerSpike.csproj') -v q --nologo 2>&1 | Tee-Object -FilePath $LogPath -Append
if ($LASTEXITCODE -ne 0) {
    Write-Both 'Build failed — aborting rather than measuring a stale binary.'
    exit 1
}
if (-not (Test-Path $exe)) {
    Write-Both "Build reported success but $exe is missing — aborting."
    exit 1
}
Write-Both "exe: $exe (built $((Get-Item $exe).LastWriteTime.ToString('o')))"

# The layer is measured where it lives; setup only changes its ACL.
$exported = $Layer

if ($SkipSetup) {
    Write-Both ''
    Write-Both "Skipping setup: reusing $exported as-is. No control boot and no elevated comparison in this record."
    if (-not $exported) {
        Write-Both '…but no -Layer was given, so there is nothing to probe. Pass -Layer.'
        exit 2
    }
    if (-not (Test-Path (Join-Path $exported 'Files'))) {
        Write-Both "…but $exported has no readable Files\ — nothing to probe. Rerun without -SkipSetup to grant access."
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
        '-LogPath', (& $q $LogPath)
    )
    if ($Layer) { $childArgs += @('-Layer', (& $q $Layer)) }

    if (-not $Layer) {
        Write-Both 'ERROR: -Layer is required. The setup phase grants access to a layer IN PLACE, so the'
        Write-Both 'unelevated session must know which layer it will then measure. Pass the Docker'
        Write-Both 'windowsfilter directory of a base image carrying Files\ and UtilityVM\Files.'
        exit 2
    }

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
    $tag = ($null -eq $_.Ok) ? 'MEAS' : ($_.Ok ? 'OK  ' : 'FAIL')
    Write-Both ("{0}  {1,-40} elevated={2,-5} exit={3}" -f $tag, $_.Step, $_.Elevated, $_.Exit)
}
Write-Both ''
Write-Both 'Reading this record:'
Write-Both '  - OK/FAIL mark REQUIRED steps (export, elevated controls). A FAIL voids the run.'
Write-Both '  - MEAS marks MEASURED steps, which have no expected exit code. exit 0 means that'
Write-Both '    path needs no elevation; a nonzero exit names the first call that gated it —'
Write-Both '    read the matrix rows above. Neither outcome is a pass or a failure.'
Write-Both '  - SKIP rows in a matrix were never attempted and prove nothing either way.'
Write-Both ''
Write-Both "log: $LogPath"

# Only required steps can fail the run; a measured nonzero exit is the finding.
$requiredFailed = @($script:steps | Where-Object { $_.MustPass -and -not $_.Ok })
exit ($requiredFailed.Count -gt 0 ? 1 : 0)
