$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskScratch = Join-Path $taskTempRoot ('CodexQuotaTray-demo-check-' + [guid]::NewGuid())
$taskProcess = $null
try {
    New-Item -ItemType Directory -Path (Join-Path $taskScratch 'dist') | Out-Null
    foreach ($taskFile in 'CodexQuotaTray.exe','CodexQuotaTray.dll','CodexQuotaTray.deps.json','CodexQuotaTray.runtimeconfig.json') {
        Copy-Item -LiteralPath (Join-Path $taskRoot "dist\$taskFile") -Destination (Join-Path $taskScratch 'dist')
    }
    $taskSentinel = '{"tokens":{"access_token":"demo-check-not-a-real-token"}}'
    foreach ($taskSlot in 'A','B') {
        $taskAccount = Join-Path $taskScratch "data\accounts\$taskSlot"
        New-Item -ItemType Directory -Path $taskAccount -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $taskAccount 'auth.json'),$taskSentinel)
    }
    $taskImage = Join-Path $taskScratch 'panel.png'
    $taskProcess = Start-Process -FilePath (Join-Path $taskScratch 'dist\CodexQuotaTray.exe') -ArgumentList "--render-preview `"$taskImage`"" -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(30000)) { throw 'Demo preview timed out.' }
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskImage)) { throw 'Demo preview did not complete.' }
    foreach ($taskSlot in 'A','B') {
        $taskAccount = Join-Path $taskScratch "data\accounts\$taskSlot"
        if (-not (Test-Path -LiteralPath (Join-Path $taskAccount 'auth.json')) -or
            [IO.File]::ReadAllText((Join-Path $taskAccount 'auth.json')) -ne $taskSentinel -or
            (Test-Path -LiteralPath (Join-Path $taskAccount 'auth.dpapi'))) { throw 'Demo touched an account working file.' }
    }
    Write-Output 'Demo isolation: PASS (sample preview; both synthetic account files unchanged).'
}
finally {
    if ($taskProcess -and -not $taskProcess.HasExited) { $taskProcess.Kill(); $taskProcess.WaitForExit() }
    if (Test-Path -LiteralPath $taskScratch) {
        $taskResolved = (Resolve-Path -LiteralPath $taskScratch).ProviderPath
        if ($taskResolved -ne [IO.Path]::GetFullPath($taskScratch) -or -not $taskResolved.StartsWith($taskTempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup path.' }
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    }
}
