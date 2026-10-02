param(
    [string]$DotnetPath = 'dotnet',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskOutput = Join-Path $PSScriptRoot 'dist'
& $DotnetPath publish (Join-Path $PSScriptRoot 'CodexQuotaTray.csproj') -c Release -o $taskOutput --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Install the .NET 8 SDK or provide -DotnetPath.' }
if ($SelfTest) {
    $taskTest = Start-Process -FilePath (Join-Path $taskOutput 'CodexQuotaTray.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
    if (-not $taskTest.WaitForExit(30000)) { $taskTest.Kill(); throw 'Self-check timed out.' }
    $taskResult = Get-Content -LiteralPath (Join-Path $taskOutput 'test-result.txt') -Raw
    if ($taskTest.ExitCode -ne 0 -or $taskResult.Trim() -ne 'PASS') { throw ('Self-check failed: ' + $taskResult) }
    Write-Output 'Self-check: PASS'
}
