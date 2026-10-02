$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot 'dist\CodexQuotaTray.exe'
$taskData = Join-Path $taskRoot 'data'
function Check-Resident($condition, $message) { if (-not $condition) { throw $message } }
$taskProcesses = @(Get-CimInstance Win32_Process -Filter "Name='CodexQuotaTray.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe -and $_.CommandLine -match '--worker' })
Check-Resident ($taskProcesses.Count -eq 1) 'Expected exactly one installed resident worker.'
$taskWorker = $taskProcesses[0]
$taskAncestors = @()
$taskParent = $taskWorker.ParentProcessId
for ($taskDepth = 0; $taskParent -gt 0 -and $taskDepth -lt 32; $taskDepth++) {
    $taskAncestor = Get-CimInstance Win32_Process -Filter "ProcessId=$taskParent"
    if (-not $taskAncestor) { break }
    $taskAncestors += $taskAncestor.Name
    $taskParent = $taskAncestor.ParentProcessId
}
Check-Resident (-not ($taskAncestors | Where-Object { $_ -match '^(codex.*|ChatGPT)\.exe$' })) 'The worker is still in the Codex process tree.'
$taskSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskScheduled = Get-ScheduledTask -TaskName ('CodexQuotaTray-' + $taskSid)
Check-Resident ([string]$taskScheduled.State -eq 'Running') 'Resident task is not running.'
Check-Resident ($taskScheduled.Actions.Execute -eq $taskExe -and $taskScheduled.Actions.Arguments -eq '--worker --background') 'Wrong scheduled action.'
Check-Resident ($taskScheduled.Principal.LogonType -eq 'Interactive' -and $taskScheduled.Principal.RunLevel -eq 'Limited') 'Task must run on the current desktop without elevation.'
Check-Resident ($taskScheduled.Settings.ExecutionTimeLimit -eq 'PT0S') 'Unexpected task time limit.'
$taskDiagnostic = Start-Process -FilePath $taskExe -ArgumentList '--layout-check' -WindowStyle Hidden -PassThru
Check-Resident ($taskDiagnostic.WaitForExit(15000)) 'Layout diagnostic timed out.'
$taskLayout = Get-Content -LiteralPath (Join-Path $taskRoot 'dist\layout-result.json') -Raw | ConvertFrom-Json
Check-Resident ($taskLayout.residents.Count -eq 1 -and $taskLayout.Embedded -and -not $taskLayout.ResidentCollisions) 'Native taskbar mount or collision check failed.'
Check-Resident ($taskLayout.rendering.Count -eq 1 -and @($taskLayout.rendering[0].RingPixels | Where-Object { $_ -lt 8 }).Count -eq 0) 'Rings are not visible on the displayed taskbar.'
Check-Resident ($taskLayout.rendering[0].TransparentBackground) 'Taskbar ring background must be transparent.'
$taskHealth = Get-Content -LiteralPath (Join-Path $taskData 'health.json') -Raw | ConvertFrom-Json
Check-Resident ($taskHealth.pid -eq $taskWorker.ProcessId -and $taskHealth.phase -eq 'ready') 'Worker initialization is incomplete.'
$taskCache = Get-Content -LiteralPath (Join-Path $taskData 'state.json') -Raw | ConvertFrom-Json
Check-Resident ($taskCache.Accounts.Count -eq 2 -and @($taskCache.Accounts | Where-Object { -not $_.Fresh -or (Get-Date) - [datetime]$_.Updated -gt [timespan]::FromMinutes(5) }).Count -eq 0) 'Account quotas are not fresh.'
foreach ($taskSlot in 'A','B') {
    Check-Resident (Test-Path -LiteralPath (Join-Path $taskData "accounts\$taskSlot\auth.dpapi")) 'Missing encrypted account.'
    Check-Resident (-not (Test-Path -LiteralPath (Join-Path $taskData "accounts\$taskSlot\auth.json"))) 'Query is active or working credentials have not been sealed. Retry when idle.'
}
$taskReport = [ordered]@{
    Result='PASS'; Version=(Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion; WorkerId=$taskWorker.ProcessId
    WorkerParent=$taskAncestors[0]; IndependentOfCodex=$true; TaskRunning=([string]$taskScheduled.State -eq 'Running')
    LoginStartup=(@($taskScheduled.Triggers).Count -gt 0); UnlimitedRuntime=$true; NativeTaskbarChild=$taskLayout.Embedded
    Collision=$taskLayout.ResidentCollisions; Bounds=$taskLayout.residents; DataDirectory=$taskData
    ScreenRingPixels=$taskLayout.rendering[0].RingPixels
    TransparentBackground=$taskLayout.rendering[0].TransparentBackground
    Accounts=@($taskCache.Accounts | ForEach-Object { @{Name=$_.Name; Fresh=$_.Fresh; Updated=$_.Updated; WindowCount=$_.Windows.Count} })
    CredentialsSealed=$true; CheckedAt=(Get-Date).ToString('o')
}
$taskReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ('resident-verification-v' + $taskReport.Version + '.json')) -Encoding utf8
$taskReport | ConvertTo-Json -Depth 6
