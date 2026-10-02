param(
    [switch]$Demo,
    [string]$DotnetPath = 'dotnet',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot 'dist\CodexQuotaTray.exe'
try {
    if (-not (Test-Path -LiteralPath $taskExe)) {
        if (-not (Get-Command $DotnetPath -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK, then run this launcher again: https://dotnet.microsoft.com/download/dotnet/8.0' }
        Write-Output 'First run: building and running the offline self-check...'
        & (Join-Path $taskRoot 'build.ps1') -DotnetPath $DotnetPath -SelfTest
    }
    if ($CheckOnly) { Write-Output 'Launcher check: PASS (ready; app not started).'; return }
    if ($Demo) {
        Start-Process -FilePath $taskExe -ArgumentList '--demo' -WorkingDirectory $taskRoot -WindowStyle Normal
        Write-Output 'Demo opened with sample data. Exit it from its tray menu before starting another demo.'
    }
    else {
        $taskLauncher = Start-Process -FilePath $taskExe -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
        if (-not $taskLauncher.WaitForExit(30000)) { throw 'Startup is taking longer than expected; check the tray before starting again.' }
        if ($taskLauncher.ExitCode -ne 0) { throw 'Startup failed. Check the application message and run from a writable folder.' }
        Write-Output 'Resident started. Click its tray icon to authorize A and B.'
    }
}
catch { Write-Error $_; exit 1 }
