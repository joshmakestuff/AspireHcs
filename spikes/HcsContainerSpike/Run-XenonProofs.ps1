#Requires -RunAsAdministrator
<#
.SYNOPSIS
Elevated proof harness for the xenon (Hyper-V-isolated) container spike (#32).

.DESCRIPTION
Runs the full proof sequence — build, xenon run, orphan/reaping test with
timed absence probes, cleanup — echoing every line to the console AND to a
timestamped log under %TEMP%\AspireHcsXenonProofs, so an unelevated session
can read the record afterwards.

Expected exits: build 0, run 0, orphan 99, both absence probes 0 (eventually;
time-to-absent is part of the data), cleanup 0. Script exit: 0 only if every
step met its expectation.

.PARAMETER Layer
Materialized windowsfilter layer directory to boot. When omitted, the script
scans C:\ProgramData\Docker\windowsfilter for directories carrying both
Files\ and UtilityVM\Files and uses the single candidate; with zero or many
candidates it lists what it found and asks for -Layer explicitly.

.PARAMETER LogPath
Log file to append to. Default: %TEMP%\AspireHcsXenonProofs\xenon-proofs-<stamp>.log

.PARAMETER SkipOrphan
Run only the terminating proof (skip orphan + absence probes + cleanup).
#>
[CmdletBinding()]
param(
    [string]$Layer,
    [string]$LogPath,
    [switch]$SkipOrphan
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.17763.0\HcsContainerSpike.exe'
$containerId = 'AspireHcsContainerSpike'

if (-not $LogPath) {
    $logDir = Join-Path $env:TEMP 'AspireHcsXenonProofs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $LogPath = Join-Path $logDir ("xenon-proofs-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}

function Write-Both([string]$Text) {
    Write-Host $Text
    Add-Content -Path $LogPath -Value $Text
}

# Runs a native command, teeing stdout+stderr, and records the exit verdict.
$script:steps = @()
function Invoke-Step {
    param(
        [string]$Title,
        [int[]]$ExpectedExit,
        [string[]]$CommandArgs
    )
    Write-Both ''
    Write-Both "=== $Title ==="
    Write-Both "> HcsContainerSpike $($CommandArgs -join ' ')"
    & $exe @CommandArgs 2>&1 | Tee-Object -FilePath $LogPath -Append
    $code = $LASTEXITCODE
    $ok = $code -in $ExpectedExit
    $script:steps += [pscustomobject]@{ Step = $Title; Exit = $code; Expected = ($ExpectedExit -join '|'); Ok = $ok }
    Write-Both ("--- {0}: exit {1} (expected {2}) => {3}" -f $Title, $code, ($ExpectedExit -join '|'), ($ok ? 'OK' : 'FAIL'))
    return $ok
}

# Probes `list --absent <id>` until absent or timeout; time-to-absent IS the
# datum (ShouldTerminateOnLastHandleClosed reaping latency for this system).
function Invoke-AbsenceProbe {
    param([string]$Id, [int]$TimeoutSeconds = 30)
    Write-Both ''
    Write-Both "=== AbsenceProbe($Id) ==="
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $attempt = 0
    do {
        $attempt++
        & $exe list --absent $Id 2>&1 | Tee-Object -FilePath $LogPath -Append
        $code = $LASTEXITCODE
        if ($code -eq 0) { break }
        Start-Sleep -Seconds 2
    } while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    $ok = $code -eq 0
    $detail = $ok ? "absent after $([int]$sw.Elapsed.TotalSeconds)s ($attempt probe(s))" : "STILL PRESENT after $([int]$sw.Elapsed.TotalSeconds)s"
    $script:steps += [pscustomobject]@{ Step = "AbsenceProbe($Id)"; Exit = $code; Expected = '0'; Ok = $ok }
    Write-Both "--- AbsenceProbe($Id): $detail => $($ok ? 'OK' : 'FAIL')"
    return $ok
}

Write-Both "xenon proof run $(Get-Date -Format o)"
Write-Both "log: $LogPath"
Write-Both "host: $([Environment]::OSVersion.VersionString) as $(whoami)"
Write-Both "commit: $(git -C $PSScriptRoot rev-parse --short HEAD) ($(git -C $PSScriptRoot branch --show-current))"

# Locate the layer unless given.
if (-not $Layer) {
    $store = 'C:\ProgramData\Docker\windowsfilter'
    $candidates = @(Get-ChildItem $store -Directory | Where-Object {
        (Test-Path (Join-Path $_.FullName 'Files')) -and (Test-Path (Join-Path $_.FullName 'UtilityVM\Files'))
    })
    switch ($candidates.Count) {
        0 { Write-Both "No base layer with Files\ + UtilityVM\Files under $store. Switch Docker Desktop to Windows containers and 'docker pull mcr.microsoft.com/windows/nanoserver:ltsc2025', or pass -Layer."; exit 2 }
        1 { $Layer = $candidates[0].FullName; Write-Both "layer (auto): $Layer" }
        default {
            Write-Both "Multiple UtilityVM-bearing layers found — rerun with -Layer <dir>:"
            $candidates | ForEach-Object { Write-Both "  $($_.FullName)" }
            exit 2
        }
    }
}
else {
    Write-Both "layer (given): $Layer"
}

Write-Both ''
Write-Both '=== Build ==='
dotnet build (Join-Path $PSScriptRoot 'HcsContainerSpike.csproj') -v q 2>&1 | Tee-Object -FilePath $LogPath -Append
$buildOk = $LASTEXITCODE -eq 0
$script:steps += [pscustomobject]@{ Step = 'Build'; Exit = $LASTEXITCODE; Expected = '0'; Ok = $buildOk }
if (-not $buildOk) { Write-Both 'Build failed — aborting.'; exit 1 }

# Argon control in the same record: its ProcessIsolationProof asserts
# HostingSystemId is ABSENT — the other half of the isolation discriminator
# the xenon run exercises.
[void](Invoke-Step -Title 'ArgonControl' -ExpectedExit 0 -CommandArgs @('run', '--layer', $Layer))

$runOk = Invoke-Step -Title 'XenonRun' -ExpectedExit 0 -CommandArgs @('run', '--isolation', 'hyperv', '--layer', $Layer)

if (-not $SkipOrphan) {
    if ($runOk) {
        [void](Invoke-Step -Title 'XenonOrphan' -ExpectedExit 99 -CommandArgs @('orphan', '--isolation', 'hyperv', '--layer', $Layer))
        [void](Invoke-AbsenceProbe -Id $containerId)
        [void](Invoke-AbsenceProbe -Id "$containerId-uvm")
        [void](Invoke-Step -Title 'XenonCleanup' -ExpectedExit 0 -CommandArgs @('cleanup', '--isolation', 'hyperv', '--id', $containerId))
    }
    else {
        Write-Both 'Skipping orphan/cleanup sequence: XenonRun did not pass, nothing proven to orphan.'
    }
}

Write-Both ''
Write-Both '=== Verdict ==='
$script:steps | ForEach-Object { Write-Both ("{0}  {1}  exit={2} expected={3}" -f ($_.Ok ? 'OK  ' : 'FAIL'), $_.Step, $_.Exit, $_.Expected) }
$allOk = -not ($script:steps | Where-Object { -not $_.Ok })
Write-Both ("overall: {0}" -f ($allOk ? 'ALL STEPS MET EXPECTATIONS' : 'AT LEAST ONE STEP FAILED'))
Write-Both "log: $LogPath"
exit ($allOk ? 0 : 1)
