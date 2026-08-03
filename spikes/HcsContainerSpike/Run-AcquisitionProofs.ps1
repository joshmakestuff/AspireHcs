#Requires -Version 7
<#
.SYNOPSIS
    Proves image acquisition end to end (issue #30): pull a Windows base image
    from an anonymous registry, materialize it into a store AspireHcs owns, and
    boot it as a Hyper-V-isolated container — with NO Docker and NO ACL surgery.

.DESCRIPTION
    Run this from a NORMAL (unelevated) shell. It refuses to start elevated,
    because "what can a developer do without elevation" is the question under
    test. It prompts for elevation ONCE, for the import phase, whose privilege
    requirement is itself one of the measured results.

    What the run settles:

      1. Can a developer acquire a base image without Docker and without
         touching another product's store?  (pull + import, then boot)
      2. Where exactly is the privilege boundary in acquisition?  Extraction
         and finalization are measured SEPARATELY, because they are different
         gates: measured 2026-08-02, unelevated extraction of all 10288
         entries succeeds while ProcessBaseImage returns 0x80070522.
      3. Does Hyper-V isolation really lift the host/image build-match
         constraint?  The ltsc2022 boot (build 20348 on a 26200 host) is the
         first real test of that claim — MEASURED, since it has never run.

    Steps carrying an expectation are asserted (-MustPass / -ExpectedExit);
    everything else is MEASURED and rendered MEAS, because a measured nonzero
    exit is a datum, not a failure.

.PARAMETER Store
    Layer store root. Defaults to %LOCALAPPDATA%\AspireHcs\layers. Passed
    EXPLICITLY across the elevation boundary: an env-derived default inside the
    elevated child would resolve to a different profile whenever UAC elevates
    by credential rather than consent.

.PARAMETER SkipPull
    Reuse already-pulled blobs. The pull steps are then absent from the record.
#>
[CmdletBinding()]
param(
    [string]$Store,
    [string]$LogPath,
    [ValidateSet('import')][string]$Phase,
    [string]$MetadataPath,
    # Internal: the descriptor-free entry left by the unelevated extraction, so
    # the elevated phase can measure the off-diagonal "no SDs + finalize" mode.
    [string]$NoSecurityEntry,
    # Internal: where the elevated phase performs a split
    # extract-then-finalize, the positive control for the extraction record.
    [string]$SplitEntry,
    # Internal: four directories for the privilege-identification arms.
    [string]$IdentifyEntries,
    [switch]$SkipPull
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.17763.0\HcsContainerSpike.exe'
$containerId = 'AspireHcsAcquisitionProbe'

# The two fixtures.
#
# "Compatible" here does NOT mean "same build number as the host", and an
# earlier revision of this harness got that wrong and refused to run: it
# compared 26100 to 26200, called ltsc2025 mislabelled, and stopped. But #31
# already RECORDED a 26100 image booting process-isolated on this 26200 host —
# they are the same servicing family (Server 2025 / Win11 24H2-25H2, where
# 26200 is an enablement package over the 26100 servicing base). The equality
# rule contradicted the measurement, so the measurement wins.
#
# So compatibility is stated from the empirical record, per fixture, and what
# the witness below actually checks is that each image is STILL the build the
# record was made against — which is what would silently rot if MCR retagged.
$images = @(
    [pscustomobject]@{
        Name = 'ltsc2025'; Ref = 'mcr.microsoft.com/windows/nanoserver:ltsc2025'
        ExpectedBuild = 26100; Compatible = $true
        Why = 'same servicing family as the 26xxx host; a 26100 image is already recorded booting on this host (#31)'
    }
    [pscustomobject]@{
        Name = 'ltsc2022'; Ref = 'mcr.microsoft.com/windows/nanoserver:ltsc2022'
        ExpectedBuild = 20348; Compatible = $false
        Why = 'Windows Server 2022 generation — a genuinely different family from this 26xxx host, never booted here'
    }
)

if (-not $Store) {
    $Store = Join-Path $env:LOCALAPPDATA 'AspireHcs\layers'
}
if (-not $LogPath) {
    $logDir = Join-Path $env:TEMP 'AspireHcsAcquisitionProofs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $LogPath = Join-Path $logDir ("acquisition-proofs-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Write-Both([string]$Text) {
    Write-Host $Text
    Add-Content -Path $LogPath -Value $Text
}

# Finds the metadata file by its RECORDED image reference rather than by
# recomputing the spike's file-naming rule here. A second implementation of that
# rule would be free to drift from the first, and the failure would look like a
# missing pull rather than a naming disagreement.
function Get-MetadataPath([string]$ImageRef) {
    $imagesDir = Join-Path $Store 'images'
    if (-not (Test-Path $imagesDir)) { return $null }
    foreach ($file in Get-ChildItem $imagesDir -Filter '*.json' -ErrorAction SilentlyContinue) {
        try {
            if ((Get-Content $file.FullName -Raw | ConvertFrom-Json).image -eq $ImageRef) {
                return $file.FullName
            }
        }
        catch {
            # A torn metadata file is not the one we want, but swallowing it
            # silently would hide a real torn write behind "no such image".
            Write-Both "WARNING: unreadable metadata file $($file.FullName): $($_.Exception.Message)"
            continue
        }
    }
    return $null
}

$script:steps = @()
function Invoke-Step {
    param(
        [string]$Title,
        [string[]]$CommandArgs,
        [switch]$MustPass,
        [int]$ExpectedExit = -1,
        # Exit codes are coarse — `import` returns 2 for a privilege failure and
        # a diffID mismatch alike — so a claim about a SPECIFIC call needs the
        # output to witness it.
        [string]$ExpectOutputMatch
    )
    # An output assertion on a step with no expected exit would be recorded and
    # then ignored: Ok stays $null for MEAS rows, so the regex could fail while
    # the verdict table still printed MEAS. A check that cannot fail is worse
    # than no check, so this combination is refused outright.
    if ($ExpectOutputMatch -and -not ($MustPass -or ($ExpectedExit -ge 0))) {
        throw "Invoke-Step '$Title': -ExpectOutputMatch requires -MustPass or -ExpectedExit, or the assertion cannot fail the run."
    }
    Write-Both ''
    Write-Both "=== $Title (elevated=$isElevated) ==="
    Write-Both "> HcsContainerSpike $($CommandArgs -join ' ')"
    # Out-Host, not a bare Tee-Object: Tee-Object PASSES ITS INPUT THROUGH, so
    # the command's output would join this function's return value and every
    # `if (-not (Invoke-Step ...))` guard would silently never fire (a non-empty
    # array is truthy). Found live in Run-PrivilegeProofs.ps1; do not "simplify".
    & $exe @CommandArgs 2>&1 | Tee-Object -Variable captured | Tee-Object -FilePath $LogPath -Append | Out-Host
    $code = $LASTEXITCODE

    $matched = $true
    if ($ExpectOutputMatch) {
        $text = ($captured | Out-String)
        $matched = $text -match $ExpectOutputMatch
        Write-Both ("--- output assertion /{0}/: {1}" -f $ExpectOutputMatch, ($matched ? 'matched' : 'NOT MATCHED'))
    }
    $asserted = $ExpectedExit -ge 0
    $script:steps += [pscustomobject]@{
        Step     = $Title
        Elevated = $isElevated
        Exit     = $code
        MustPass = ([bool]$MustPass) -or $asserted
        Ok       = $asserted ? (($code -eq $ExpectedExit) -and $matched) : ($MustPass ? (($code -eq 0) -and $matched) : $null)
    }
    $note = $asserted ? " (asserted $ExpectedExit)" : ($MustPass ? " (required 0)" : " (measured, no expectation)")
    Write-Both ("--- {0}: exit {1}{2}" -f $Title, $code, $note)
    return ($code -eq 0)
}

Write-Both "acquisition proof run $(Get-Date -Format o)"
Write-Both "log:   $LogPath"
Write-Both "host:  $([Environment]::OSVersion.VersionString) as $(whoami)"
Write-Both "store: $Store"
Write-Both "phase: $($Phase ? $Phase : 'main')  elevated: $isElevated  hyperVAdmin: $($principal.IsInRole((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-578'))))"
try { Write-Both "commit: $(git -C $PSScriptRoot rev-parse --short HEAD) ($(git -C $PSScriptRoot branch --show-current))" } catch { }

# --------------------------------------------------------------- import phase --
# Elevated, launched by the main phase. Does exactly the one thing that needs
# elevation — nothing else, so the rest of the record stays a genuine
# unelevated measurement.
if ($Phase -eq 'import') {
    if (-not $isElevated) { Write-Both 'import phase requires elevation.'; exit 2 }
    # Deliberately does NOT build: that would leave Administrator-owned obj\/bin\
    # artifacts and break the next unelevated build.
    if (-not (Test-Path $exe)) {
        Write-Both "import phase: $exe is missing — the unelevated phase should have built it. Stopping."
        exit 2
    }
    if (-not $MetadataPath) { Write-Both 'import phase requires -MetadataPath.'; exit 2 }

    # Every path is explicit; nothing is derived from this process's environment,
    # whose %LOCALAPPDATA% may belong to a different account than the developer's.
    foreach ($metadata in ($MetadataPath -split ';')) {
        if (-not (Invoke-Step -Title "ElevatedImport($(Split-Path $metadata -Leaf))" -MustPass `
                    -CommandArgs @('import', '--metadata', $metadata))) {
            Write-Both 'Import failed elevated. Nothing downstream can be interpreted — stopping.'
            exit 1
        }
    }

    # The off-diagonal mode: extract WITHOUT security descriptors, then finalize
    # WITH privileges. Neither half is novel, but the combination is what an
    # unprivileged-extraction design would actually produce, and nothing else in
    # this run exercises it. MEASURED — whether ProcessBaseImage tolerates a
    # descriptor-free tree is exactly the unknown.
    #
    # It finalizes a SECOND, pristine descriptor-free entry — one extracted
    # UNELEVATED and never finalized before — rather than the one the asserted
    # unelevated finalize already touched. That failure gets far enough to leave
    # a partial blank-base.vhdx, and finalizing over it measures the debris
    # (observed exactly that on 2026-08-02: 0x80070050 ERROR_FILE_EXISTS).
    # Re-extracting here instead would also miss the point: the extraction has
    # to be the unelevated one for this to be the off-diagonal it claims to be.
    if ($NoSecurityEntry) {
        [void](Invoke-Step -Title 'ElevatedFinalize(descriptor-free entry, extracted unelevated)' `
                -CommandArgs @('finalize', '--entry', $NoSecurityEntry))
    }

    # Which privilege actually gates finalization (#33's open question). Needs
    # four pristine trees, because ProcessBaseImage is not idempotent and each
    # arm must meet an untouched entry; extraction is unprivileged and fast.
    if ($IdentifyEntries) {
        $armDirs = $IdentifyEntries -split ';'
        $armsReady = $true
        foreach ($dir in $armDirs) {
            if (-not (Invoke-Step -Title "IdentifyArmExtract($(Split-Path $dir -Leaf))" `
                        -CommandArgs @('import', '--metadata', ($MetadataPath -split ';')[0], '--entry', $dir, `
                            '--no-security', '--skip-finalize'))) {
                $armsReady = $false
            }
        }
        if ($armsReady) {
            # MustPass judges the EXPERIMENT's validity, not the arms: `identify`
            # exits 0 whenever all four arms ran under the privilege state they
            # intended, and a failing arm is the datum being collected.
            #
            # The exit code therefore cannot pin the OUTCOME, and the docs now
            # state one ("all four arms succeeded, including both-disabled").
            # The output assertion is what makes that claim falsifiable — if the
            # both-disabled arm ever starts failing, this row goes red instead of
            # the document quietly going stale.
            [void](Invoke-Step -Title 'IdentifyFinalizePrivilege' -MustPass `
                    -ExpectOutputMatch 'does NOT depend on these privileges being ENABLED' `
                    -CommandArgs @('identify', '--entries', ($armDirs -join ',')))
        }
        else {
            Write-Both 'Skipping IdentifyFinalizePrivilege: could not prepare four pristine entries.'
        }
    }

    # The OTHER off-diagonal, and the positive control for the provenance fix:
    # a FULL-FIDELITY extraction finalized by a separate `finalize` call. It is
    # the only path that exercises the extraction record's true branch, i.e. the
    # one that must report securityDescriptorsRestored=true. Without it, the
    # record could report false for every entry and nothing would notice.
    if ($SplitEntry) {
        $firstMetadata = ($MetadataPath -split ';')[0]
        if (Invoke-Step -Title 'ElevatedExtract(full fidelity, --skip-finalize)' -MustPass `
                -CommandArgs @('import', '--metadata', $firstMetadata, '--entry', $SplitEntry, '--skip-finalize')) {
            [void](Invoke-Step -Title 'ElevatedFinalize(full-fidelity entry)' -MustPass `
                    -ExpectOutputMatch 'securityDescriptorsRestored=True' `
                    -CommandArgs @('finalize', '--entry', $SplitEntry))
        }
    }

    Write-Both ''
    Write-Both '=== Import phase verdict ==='
    $script:steps | ForEach-Object { Write-Both ("  {0}  elevated={1} exit={2}" -f $_.Step, $_.Elevated, $_.Exit) }
    exit ((@($script:steps | Where-Object { $_.MustPass -and -not $_.Ok }).Count -gt 0) ? 1 : 0)
}

# ----------------------------------------------------------------- main phase --
if ($isElevated) {
    Write-Both ''
    Write-Both 'REFUSING TO RUN: this harness must start UNELEVATED — an unelevated session is the'
    Write-Both 'condition under test. Start it from a normal shell; it will prompt for elevation'
    Write-Both 'once, for the import phase only.'
    exit 2
}

# Build first, unelevated: the log records the commit, so measuring a stale
# binary would attribute results to code that never ran.
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

# REQUIRED, not measured: docs/image-acquisition.md leans on these probes for the
# reparse/ADS/EA record shapes that no image fixture exercises. If they can fail
# without failing the run, the document is citing a check that never had to pass.
[void](Invoke-Step -Title 'SelfTest(native building blocks)' -MustPass -CommandArgs @('selftest'))

# --- 1. Acquire, unelevated -------------------------------------------------
# REQUIRED: pulling is plain HTTPS plus file writes into the user's own profile.
# If this needs anything more, the premise of a user-owned store is wrong.
if (-not $SkipPull) {
    foreach ($image in $images) {
        if (-not (Invoke-Step -Title "UnelevatedPull($($image.Name))" -MustPass `
                    -ExpectOutputMatch '(digest verified|re-hashed and verified)' `
                    -CommandArgs @('pull', '--image', $image.Ref, '--store', $Store))) {
            Write-Both 'Pull failed unelevated — the acquisition premise fails here. Stopping.'
            exit 1
        }
    }
}
else {
    Write-Both ''
    Write-Both 'Skipping pull: reusing existing blobs. The pull steps are absent from this record.'
}

$metadataPaths = @()
foreach ($image in $images) {
    $found = Get-MetadataPath $image.Ref
    if (-not $found) {
        Write-Both "No metadata recorded for $($image.Ref) under $Store\images — rerun without -SkipPull."
        exit 2
    }
    $metadataPaths += $found
}

# The fixture labels are claims, so the builds behind them are witnessed at
# runtime rather than trusted from the table above. What can silently rot is a
# RETAG: if MCR ever moved :ltsc2022 onto a 26xxx build, the build-mismatch
# experiment below would be testing nothing while still printing its name.
# That is what this checks — NOT build equality with the host, which is not the
# compatibility rule (see the fixture table).
$hostBuild = [Environment]::OSVersion.Version.Build
Write-Both ''
Write-Both "=== Fixture build witness (host build $hostBuild) ==="
$fixtureBuilds = @{}
foreach ($i in 0..($images.Count - 1)) {
    $osVersion = (Get-Content $metadataPaths[$i] -Raw | ConvertFrom-Json).osVersion
    $imageBuild = [int](($osVersion -split '\.')[2])
    $fixtureBuilds[$images[$i].Name] = $imageBuild
    Write-Both ("  {0,-9} osVersion={1,-18} build={2,-6} expected={3,-6} compatible={4}  {5}" -f `
            $images[$i].Name, $osVersion, $imageBuild, $images[$i].ExpectedBuild, $images[$i].Compatible, $images[$i].Why)
    if ($imageBuild -ne $images[$i].ExpectedBuild) {
        Write-Both ''
        Write-Both "FIXTURE DRIFTED: $($images[$i].Name) is build $imageBuild, but this harness's compatibility"
        Write-Both "claim was recorded against $($images[$i].ExpectedBuild). The tag has moved, so the labels below"
        Write-Both 'no longer describe what would actually be booted. Stopping rather than recording a result'
        Write-Both 'under a stale label; re-establish which builds are compatible, then update the fixture table.'
        exit 2
    }
}
# The whole point of the mismatch experiment is that the two fixtures are from
# DIFFERENT generations. If they ever converge, the experiment is vacuous.
if ($fixtureBuilds['ltsc2025'] -eq $fixtureBuilds['ltsc2022']) {
    Write-Both ''
    Write-Both 'FIXTURES CONVERGED: both tags now resolve to the same build, so there is no build-mismatch'
    Write-Both 'case left to test. Stopping.'
    exit 2
}
$script:steps += [pscustomobject]@{ Step = 'FixtureBuildWitness'; Elevated = $isElevated; Exit = 0; MustPass = $true; Ok = $true }

# What the images actually contain, on the record: the port's handling of
# symlinks/junctions/ADS is only exercised if the fixtures carry them. BOTH tags
# are inspected because the document makes a claim about both — inspecting one
# would leave the other's "zero symlinks" quantifier with no durable witness.
foreach ($i in 0..($images.Count - 1)) {
    [void](Invoke-Step -Title "Inspect($($images[$i].Name))" -MustPass `
            -ExpectOutputMatch 'InspectWhiteoutFree: hr=0x00000000' `
            -CommandArgs @('inspect', '--metadata', $metadataPaths[$i]))
}

# The documented scope guard, exercised rather than asserted in prose: a
# multi-layer image must be refused, naming chain import as the follow-up.
[void](Invoke-Step -Title 'MultiLayerImageRefused(servercore)' -ExpectedExit 2 `
        -ExpectOutputMatch 'multi-layer \(chain\) import is out of this spike' `
        -CommandArgs @('pull', '--image', 'mcr.microsoft.com/windows/servercore:ltsc2025', '--store', $Store))

# --- 2. The privilege boundary, measured in three separate places -----------
# Full-fidelity import unelevated: expected to stop at privilege enablement.
[void](Invoke-Step -Title 'UnelevatedImport(full fidelity)' `
        -CommandArgs @('import', '--metadata', $metadataPaths[0]))

# Extraction alone, no security data: measured 2026-08-02 to SUCCEED, which is
# why it is asserted here — the claim "extraction needs no privileges" now has
# a test that can fail. It lands in its OWN directory, not the canonical
# content-addressed entry, so the elevated import below cannot destroy it: the
# descriptor-free tree is itself a fixture for the off-diagonal measurement.
$gateEntry = Join-Path $Store 'experiment-privilege-gate'
[void](Invoke-Step -Title 'UnelevatedExtract(--no-security --skip-finalize)' -ExpectedExit 0 `
        -ExpectOutputMatch 'VerifyDiffId: hr=0x00000000' `
        -CommandArgs @('import', '--metadata', $metadataPaths[0], '--entry', $gateEntry, '--no-security', '--skip-finalize'))

# Finalize alone on that entry: the OTHER gate. Asserted to FAIL with
# ERROR_PRIVILEGE_NOT_HELD — an unexpected pass would mean the boundary moved.
# This CONTAMINATES the entry (the failure leaves a partial blank-base.vhdx),
# which is why it is deliberately not the entry the elevated phase finalizes.
[void](Invoke-Step -Title 'UnelevatedFinalize' -ExpectedExit 2 `
        -ExpectOutputMatch 'ProcessBaseImage: hr=0x80070522' `
        -CommandArgs @('finalize', '--entry', $gateEntry))

# A pristine descriptor-free tree, extracted UNELEVATED and never finalized, for
# the elevated phase to finalize. Keeping it separate is what makes that step a
# real off-diagonal (unelevated extraction + elevated finalize) rather than a
# measurement of the previous step's debris.
$noSecurityEntry = Join-Path $Store 'experiment-no-security'
[void](Invoke-Step -Title 'UnelevatedExtract(descriptor-free, pristine)' -ExpectedExit 0 `
        -ExpectOutputMatch 'VerifyDiffId: hr=0x00000000' `
        -CommandArgs @('import', '--metadata', $metadataPaths[0], '--entry', $noSecurityEntry, '--no-security', '--skip-finalize'))

# --- 3. Import for real, elevated -------------------------------------------
Write-Both ''
Write-Both '=== Import (elevating: expect one UAC prompt) ==='
# Start-Process joins -ArgumentList with spaces and does NOT quote, so every
# path is quoted here; an unquoted "C:\Program Files\..." would silently become
# two arguments and the child would misparse its own store path.
$q = { param($v) '"' + $v + '"' }
$childArgs = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (& $q $PSCommandPath),
    '-Phase', 'import',
    '-Store', (& $q $Store),
    '-MetadataPath', (& $q ($metadataPaths -join ';')),
    '-NoSecurityEntry', (& $q $noSecurityEntry),
    '-SplitEntry', (& $q (Join-Path $Store 'experiment-split-finalize')),
    '-IdentifyEntries', (& $q (($('arm-neither', 'arm-backup', 'arm-restore', 'arm-both') |
                ForEach-Object { Join-Path $Store "experiment-$_" }) -join ';')),
    '-LogPath', (& $q $LogPath)
)
$child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $childArgs -Verb RunAs -Wait -PassThru
Write-Both "--- import phase exit: $($child.ExitCode)"
if ($child.ExitCode -ne 0) {
    Write-Both 'Import phase failed (see its output above, in the shared log). Stopping: without a'
    Write-Both 'materialized store entry there is nothing to boot.'
    exit 1
}
$script:steps += [pscustomobject]@{ Step = 'ImportPhase(elevated)'; Elevated = $true; Exit = $child.ExitCode; MustPass = $true; Ok = $true }

# --- 4. Is the result usable BY THE DEVELOPER, unelevated? ------------------
# The entry root inherits from %LOCALAPPDATA%, but UtilityVM's SD is restored
# verbatim from the image and SystemTemplate.vhdx inherits THAT. If the image's
# descriptor does not grant this user read/traverse, the boots below would fail
# for ACL reasons that have nothing to do with acquisition — so the reachability
# of exactly the boot-consumed paths is checked first, and separately.
$entries = @()
foreach ($i in 0..($images.Count - 1)) {
    $diffId = (Get-Content $metadataPaths[$i] -Raw | ConvertFrom-Json).expectedDiffId -replace '^sha256:', ''
    $entries += [pscustomobject]@{ Image = $images[$i]; Path = (Join-Path $Store $diffId) }
}
foreach ($entry in $entries) {
    if (-not (Invoke-Step -Title "UnelevatedVerify($($entry.Image.Name))" -MustPass `
                -CommandArgs @('verify', '--layer', $entry.Path))) {
        Write-Both ''
        Write-Both 'The imported entry is NOT reachable from this unelevated session. That is a real'
        Write-Both 'finding about restored security descriptors, not a harness error: record which'
        Write-Both 'SufficiencyProbe rows failed. A store AspireHcs owns was supposed to need no ACL'
        Write-Both 'work at all; if it does, the import must grant it explicitly.'
        exit 1
    }
}

# --- 5. Boot what we acquired, unelevated ----------------------------------
# ASSERTED for the build-matched image: acquisition -> boot with no Docker and
# no ACL surgery is the claim this whole spike exists to establish.
$compatible = ($entries | Where-Object { $_.Image.Compatible })[0]
# The output assertion names the SUCCEEDING proof line, not merely the step:
# "GuestExecProof" alone appears on failure too, so matching it would assert
# nothing the exit code had not already decided.
[void](Invoke-Step -Title 'UnelevatedXenonBoot(own store, ltsc2025)' -ExpectedExit 0 `
        -ExpectOutputMatch 'GuestExecProof\(stdout\): hr=0x00000000' `
        -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'template', '--layer', $compatible.Path, '--id', $containerId))

# ASSERTED as of 2026-08-02, when this stopped being an unknown: a 20348 guest
# DID boot on this 26200 host, and README/docs now state that Hyper-V isolation
# lifts the build-match constraint. A documented claim with no failing test is
# just a claim, so the guest's own reported build is asserted too — a boot that
# silently ran the wrong image would otherwise pass on exit code alone.
$mismatched = ($entries | Where-Object { -not $_.Image.Compatible })[0]
[void](Invoke-Step -Title 'UnelevatedXenonBoot(BUILD-MISMATCHED ltsc2022)' -ExpectedExit 0 `
        -ExpectOutputMatch 'guest build=10\.0\.20348\.' `
        -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'template', '--layer', $mismatched.Path, '--id', $containerId))

# The docs claim per-container scratch creation is unprivileged. That was
# measured against DOCKER's store (the #33 matrix), never against ours, so the
# adjacent mode gets its own row: CreateSandboxLayer against an entry we
# imported, unelevated.
[void](Invoke-Step -Title 'UnelevatedXenonBoot(own store, --scratch api)' -ExpectedExit 0 `
        -ExpectOutputMatch 'CreateSandboxLayer: hr=0x00000000' `
        -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'api', '--layer', $compatible.Path, '--id', $containerId))

# MEASURED: argon on our own store. Expected to gate at ActivateLayer exactly as
# it does on Docker's store — which would show the gate is a privilege, not
# anything about where the layer lives.
[void](Invoke-Step -Title 'UnelevatedArgonBoot(own store)' `
        -CommandArgs @('run', '--isolation', 'process', '--layer', $compatible.Path, '--id', $containerId))

# MEASURED: does a layer whose security descriptors were NEVER restored boot?
# Only meaningful if the elevated phase managed to finalize it; the guard keeps
# a skipped step from reading as a passing one.
# Test-Path reports a file the caller cannot READ as absent — the same
# denied-as-absent lie the C# probes were just fixed for, and the class sweep
# has to reach PowerShell too. Opening it distinguishes the two, and a DENIED
# result is reported as itself rather than silently becoming "nothing to boot".
function Test-FileReadable([string]$Path, [ref]$Reason) {
    try {
        $stream = [IO.File]::OpenRead($Path)
        $stream.Dispose()
        $Reason.Value = 'readable'
        return $true
    }
    catch [UnauthorizedAccessException] {
        $Reason.Value = "present but ACCESS DENIED to this session ($($_.Exception.Message))"
        return $false
    }
    catch [IO.FileNotFoundException] { $Reason.Value = 'absent'; return $false }
    catch [IO.DirectoryNotFoundException] { $Reason.Value = 'absent (or a parent denies traverse — Win32 cannot distinguish)'; return $false }
    catch { $Reason.Value = "$($_.Exception.GetType().Name): $($_.Exception.Message)"; return $false }
}

$uvmReason = ''
if (Test-FileReadable (Join-Path $noSecurityEntry 'UtilityVM\SystemTemplate.vhdx') ([ref]$uvmReason)) {
    [void](Invoke-Step -Title 'UnelevatedXenonBoot(descriptor-free entry)' `
            -CommandArgs @('run', '--isolation', 'hyperv', '--scratch', 'template', '--layer', $noSecurityEntry, '--id', $containerId))
}
else {
    Write-Both ''
    Write-Both "SKIPPED UnelevatedXenonBoot(descriptor-free entry): SystemTemplate.vhdx under"
    Write-Both "$noSecurityEntry is $uvmReason."
    Write-Both 'A SKIP is not a pass. If the reason is "absent", read the ElevatedFinalize'
    Write-Both '(descriptor-free entry) row above — that is the finding. If it is ACCESS DENIED,'
    Write-Both 'the descriptor-free tree is unreadable to this session, which is itself a result.'
}

Write-Both ''
Write-Both '=== Verdict ==='
$script:steps | ForEach-Object {
    $tag = ($null -eq $_.Ok) ? 'MEAS' : ($_.Ok ? 'OK  ' : 'FAIL')
    Write-Both ("{0}  {1,-46} elevated={2,-5} exit={3}" -f $tag, $_.Step, $_.Elevated, $_.Exit)
}
Write-Both ''
Write-Both 'Reading this record:'
Write-Both '  - OK/FAIL mark steps carrying an expectation: the unelevated pull and verify, the'
Write-Both '    elevated import, the extraction/finalize privilege boundary, and the ltsc2025'
Write-Both '    boot. A FAIL voids the run OR means a documented result has drifted — including'
Write-Both '    UnelevatedFinalize unexpectedly PASSING, which would mean the boundary moved.'
Write-Both '  - MEAS marks measured steps with no expected exit. The build-mismatched ltsc2022'
Write-Both '    boot is the one that matters: exit 0 answers #32''s stretch question YES, a'
Write-Both '    nonzero exit names the call that stopped it. Neither is a pass or a failure.'
Write-Both ''
Write-Both "log: $LogPath"

$requiredFailed = @($script:steps | Where-Object { $_.MustPass -and -not $_.Ok })
exit ($requiredFailed.Count -gt 0 ? 1 : 0)
