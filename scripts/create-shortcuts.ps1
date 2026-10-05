$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot 'dist\CodexQuotaTray.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Build or install the app before creating shortcuts.' }
$taskLinks = @(
    (Join-Path $taskRoot 'CodexQuotaTray.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Quota Tray.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Quota Tray.lnk')
)
$taskShell = New-Object -ComObject WScript.Shell
try {
    foreach ($taskLink in $taskLinks) {
        $taskShortcut = $taskShell.CreateShortcut($taskLink)
        try {
            $taskShortcut.TargetPath = $taskExe
            $taskShortcut.Arguments = '--open'
            $taskShortcut.WorkingDirectory = Split-Path -Parent $taskExe
            $taskShortcut.IconLocation = $taskExe + ',0'
            $taskShortcut.Description = 'Codex account quota monitor - open account panel'
            $taskShortcut.Save()
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShortcut) }
        Write-Output $taskLink
    }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) }
