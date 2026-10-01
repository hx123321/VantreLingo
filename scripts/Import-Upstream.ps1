param(
    [string]$Destination = ".tmp/STranslate-v2.0.10"
)

$ErrorActionPreference = "Stop"

$Repo = "https://github.com/STranslate/STranslate.git"
$Tag = "v2.0.10"
$ExpectedCommit = "2a75118fe0fabc1135a619f4393eb3a8d936c5cd"

Set-Location (Split-Path $PSScriptRoot -Parent)
$TempRoot = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) ".tmp")) + [System.IO.Path]::DirectorySeparatorChar
$ResolvedDestination = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Destination))
if (-not $ResolvedDestination.StartsWith($TempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Upstream inspection snapshots must stay under .tmp."
}
New-Item -ItemType Directory -Force -Path (Split-Path $ResolvedDestination -Parent) | Out-Null
$Destination = $ResolvedDestination

if (Test-Path $Destination) {
    throw "Destination already exists: $Destination"
}

git clone --depth 1 --branch $Tag $Repo $Destination
if ($LASTEXITCODE -ne 0) { throw "Upstream clone failed." }
$actual = (git -C $Destination rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot read upstream commit." }

if ($actual -ne $ExpectedCommit) {
    throw "Unexpected upstream commit. Expected $ExpectedCommit, got $actual"
}

Write-Host "Verified STranslate $Tag at $actual"
Write-Host "This script only obtains the fixed upstream snapshot."
Write-Host "Runtime/module/dependency pruning must be implemented and reviewed under Issue #1."
