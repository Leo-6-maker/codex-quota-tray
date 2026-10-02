$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFiles = @(git -C $taskRoot ls-files)
if ($LASTEXITCODE -ne 0 -or $taskFiles.Count -eq 0) { throw 'No Git-tracked files to inspect.' }
$taskFailures = @()
foreach ($taskFile in $taskFiles) {
    $taskPath = Join-Path $taskRoot $taskFile
    if ($taskFile -match '(^|/)(data|dist|tools|bin|obj|artifacts|\.vs)/|(^|/)(auth\.(json|dpapi.*)|state\.json|health\.json|layout-result\.json)$|\.(exe|dll|pdb|zip|log|tmp)$') {
        $taskFailures += "$taskFile : local data or output"
        continue
    }
    if ((Get-Item -LiteralPath $taskPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { $taskFailures += "$taskFile : filesystem link"; continue }
    if ($taskFile -match '\.png$') {
        if ($taskFile -notmatch '^docs/images/') { $taskFailures += "$taskFile : unreviewed image location" }
        continue # Demo images are visually reviewed before committing.
    }
    $taskText = Get-Content -LiteralPath $taskPath -Raw
    if ($taskText -match '\b(?:gh[pousr]_|github_pat_|sk-)[A-Za-z0-9_=-]{20,}|\bBearer\s+[A-Za-z0-9_.-]{30,}') { $taskFailures += "$taskFile : possible credential" }
    if ($taskText -match '(?i)\b[A-Z]:[\\/](?:Users|AI)[\\/]') { $taskFailures += "$taskFile : personal machine path" }
    foreach ($taskEmail in [regex]::Matches($taskText, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}')) {
        if ($taskFile -in @('SelfTest.cs', 'scripts/check-publish.ps1') -and $taskEmail.Value -eq 'evil.example@auth.openai.com') { continue } # Invalid URL userinfo test, not an account address.
        if ($taskEmail.Value -notmatch '@(example\.(com|org|net)|users\.noreply\.github\.com)$') { $taskFailures += "$taskFile : non-example email" }
    }
}
if ($taskFailures.Count) { throw ($taskFailures -join [Environment]::NewLine) }
Write-Output ("Publish check: PASS ({0} tracked files). Demo images require visual review; this check does not replace a full secret scanner." -f $taskFiles.Count)
