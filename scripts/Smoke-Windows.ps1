$ErrorActionPreference = "Stop"
$TaskRoot = Split-Path $PSScriptRoot -Parent
$TaskExe = Join-Path $TaskRoot "artifacts/win-x64/VantreLingo.exe"
if (-not (Test-Path $TaskExe)) { throw "Publish the application before running the smoke check." }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class VantreWindowInspector {
    [DllImport("user32.dll", EntryPoint="GetClassNameW", CharSet=CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder text, int length);
}
'@
# 仅启动本次发布结果；不配置 Provider、不发请求、不操作任何外部用户控件。
$TaskProcess = Start-Process $TaskExe -PassThru
try {
    $TaskReady = $false
    for ($TaskAttempt = 0; $TaskAttempt -lt 100; $TaskAttempt++) {
        $TaskProcess.Refresh()
        if ($TaskProcess.HasExited) { throw "Application exited before startup completed; close existing instances before testing." }
        if ($TaskProcess.MainWindowHandle -ne [IntPtr]::Zero -and $TaskProcess.MainWindowTitle -eq "VantreLingo") {
            $TaskClass = [System.Text.StringBuilder]::new(256)
            [void][VantreWindowInspector]::GetClassName($TaskProcess.MainWindowHandle, $TaskClass, $TaskClass.Capacity)
            if ($TaskClass.ToString().StartsWith("HwndWrapper")) { $TaskReady = $true; break }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $TaskReady) { throw "The actual WPF tool window did not become available." }
    Write-Host "PASS: Published WPF application starts with the expected tool window."
} finally {
    if (-not $TaskProcess.HasExited) { Stop-Process -Id $TaskProcess.Id -Force }
    $TaskProcess.Dispose()
}
