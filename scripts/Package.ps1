param(
    [string]$CertificatePath,
    [string]$CertificateThumbprint,
    [string]$Publisher = "CN=VantreLingo Development",
    [string]$WindowsSdkBin
)
$ErrorActionPreference = "Stop"
$TaskRoot = Split-Path $PSScriptRoot -Parent
Set-Location $TaskRoot
# 不生成/安装证书、不自动信任发布者、不上传或发布包。
if ($CertificatePath -and $CertificateThumbprint) { throw "Choose a PFX path or certificate thumbprint." }
if (-not $WindowsSdkBin) {
    $SdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    $WindowsSdkBin = Get-ChildItem $SdkRoot -Directory | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64" } | Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } | Select-Object -First 1
}
if (-not $WindowsSdkBin) { throw "Windows SDK makeappx/signtool are required for MSIX packaging." }
& "$PSScriptRoot/Build.ps1" Publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
$Stage = Join-Path $TaskRoot ".tmp/msix"
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item $Stage -ItemType Directory -Force | Out-Null
Copy-Item "$TaskRoot/artifacts/win-x64/*" $Stage -Recurse
Copy-Item "$TaskRoot/packaging/Assets" $Stage -Recurse
[xml]$Manifest = Get-Content "$TaskRoot/packaging/AppxManifest.xml" -Raw
$Manifest.Package.Identity.SetAttribute("Publisher", $Publisher)
$Manifest.Save((Join-Path $Stage "AppxManifest.xml"))
$Output = Join-Path $TaskRoot "artifacts/VantreLingo-x64.msix"
& (Join-Path $WindowsSdkBin "makeappx.exe") pack /d $Stage /p $Output /o
if ($LASTEXITCODE -ne 0) { throw "MSIX creation/manifest validation failed." }
if ($CertificatePath -or $CertificateThumbprint) {
    $SignArgs = @("sign", "/fd", "SHA256")
    if ($CertificatePath) { $SignArgs += @("/f", (Resolve-Path $CertificatePath).Path) }
    else { $SignArgs += @("/sha1", $CertificateThumbprint) }
    # 密码由 signtool/证书存储处理，不把明文密码加入命令行或仓库。
    & (Join-Path $WindowsSdkBin "signtool.exe") @SignArgs $Output
    if ($LASTEXITCODE -ne 0) { throw "Signing failed; certificate subject must match Publisher." }
    & (Join-Path $WindowsSdkBin "signtool.exe") verify /pa $Output
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }
} else { Write-Warning "Unsigned MSIX created for validation only. It must be signed with a trusted matching certificate before installation." }
Write-Host "MSIX: $Output"
