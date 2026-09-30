param(
    [string]$Destination = ".upstream/STranslate-v2.0.10"
)

$ErrorActionPreference = "Stop"

$Repo = "https://github.com/STranslate/STranslate.git"
$Tag = "v2.0.10"
$ExpectedCommit = "2a75118fe0fabc1135a619f4393eb3a8d936c5cd"

if (Test-Path $Destination) {
    throw "Destination already exists: $Destination"
}

git clone --depth 1 --branch $Tag $Repo $Destination
$actual = (git -C $Destination rev-parse HEAD).Trim()

if ($actual -ne $ExpectedCommit) {
    throw "Unexpected upstream commit. Expected $ExpectedCommit, got $actual"
}

Write-Host "Verified STranslate $Tag at $actual"
Write-Host "This script only obtains the fixed upstream snapshot."
Write-Host "Runtime/module/dependency pruning must be implemented and reviewed under Issue #1."
