# Verifies that the packed AspireHcs.nupkg contains what its metadata advertises.
param(
    [Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'

$package = Get-ChildItem $PackageDirectory -Filter 'AspireHcs.*.nupkg' | Select-Object -First 1
if (-not $package) {
    throw "No AspireHcs.*.nupkg found in '$PackageDirectory'."
}
Write-Host "Verifying $($package.Name)"

$extractDir = Join-Path ([IO.Path]::GetTempPath()) 'aspirehcs-package-verify'
if (Test-Path $extractDir) {
    Remove-Item -Recurse -Force $extractDir
}
Expand-Archive $package.FullName $extractDir

$failures = @()
function Assert-Claim([string]$Claim, [bool]$Ok) {
    if ($Ok) {
        Write-Host "  OK   $Claim"
    }
    else {
        Write-Host "  FAIL $Claim"
        $script:failures += $Claim
    }
}

Assert-Claim 'lib/net10.0/AspireHcs.dll is present' (Test-Path (Join-Path $extractDir 'lib/net10.0/AspireHcs.dll'))
Assert-Claim 'package README.md is present' (Test-Path (Join-Path $extractDir 'README.md'))

[xml]$nuspec = Get-Content (Join-Path $extractDir 'AspireHcs.nuspec')
$meta = $nuspec.package.metadata
Assert-Claim "package id is 'AspireHcs' (was '$($meta.id)')" ($meta.id -eq 'AspireHcs')
Assert-Claim "license expression is 'MIT' (was '$($meta.license.'#text')')" ($meta.license.'#text' -eq 'MIT')
Assert-Claim "readme metadata points at 'README.md'" ($meta.readme -eq 'README.md')
Assert-Claim 'repository url is set' ($meta.repository.url -like 'https://github.com/*/AspireHcs')

# Deterministic/source-link claim: the nuspec must carry the exact commit this
# build came from. In CI, pin it to the commit being built.
$commit = $meta.repository.commit
Assert-Claim "repository commit is recorded (was '$commit')" ($commit -match '^[0-9a-f]{40}$')
if ($env:GITHUB_SHA) {
    Assert-Claim "repository commit matches GITHUB_SHA" ($commit -eq $env:GITHUB_SHA)
}

if ($failures.Count -gt 0) {
    throw "Package verification failed: $($failures.Count) claim(s) did not hold."
}
Write-Host 'All package claims verified.'
