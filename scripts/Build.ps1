param([ValidateSet("Verify", "Publish")][string]$Action = "Verify")
$ErrorActionPreference = "Stop"
$TaskRoot = Split-Path $PSScriptRoot -Parent
Set-Location $TaskRoot
foreach ($Directory in @(".tmp/temp", ".tmp/dotnet-home", ".tmp/nuget/http")) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
}
$env:TMP = Join-Path $TaskRoot ".tmp/temp"
$env:TEMP = $env:TMP
$env:TMPDIR = $env:TMP
$env:DOTNET_CLI_HOME = Join-Path $TaskRoot ".tmp/dotnet-home"
$env:NUGET_PACKAGES = Join-Path $TaskRoot ".tmp/nuget/packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $TaskRoot ".tmp/nuget/http"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
function Invoke-TaskDotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败（退出码 $LASTEXITCODE）。" }
}
if ($Action -eq "Verify") {
    Invoke-TaskDotnet restore VantreLingo.slnx --locked-mode --nologo
    Invoke-TaskDotnet build VantreLingo.slnx -c Release --no-restore --nologo
    Invoke-TaskDotnet run --project tests/VantreLingo.Core.Checks -c Release --no-build
} else {
    $PublishTemp = Join-Path $TaskRoot ".tmp/publish/win-x64"
    $PublishOutput = Join-Path $TaskRoot "artifacts/win-x64"
    if (Test-Path $PublishTemp) { Remove-Item -Recurse -Force $PublishTemp }
    Invoke-TaskDotnet publish src/VantreLingo.Desktop -c Release --self-contained false `
        '-p:RestoreLockedMode=true' -o $PublishTemp --nologo
    New-Item -ItemType Directory -Force -Path (Join-Path $TaskRoot "artifacts") | Out-Null
    if (Test-Path $PublishOutput) { Remove-Item -Recurse -Force $PublishOutput }
    Move-Item $PublishTemp $PublishOutput
    Write-Host "运行文件：$PublishOutput/VantreLingo.exe"
}
