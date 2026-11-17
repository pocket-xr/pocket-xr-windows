# Pocket XR's helper on a Windows PC reached over SSH (the Steam Deck): an SSH login cannot see the desktop, so this
# starts Pocket XR in the person's own desktop session through a one-off scheduled task ("only when the user is
# logged on": no password stored) and hands back what it printed.
#
#   deck-run.ps1 run   <arguments...>   run PocketXR.exe with these arguments, wait (up to 2 min), print its output
#   deck-run.ps1 start <arguments...>   start the app and return at once
param([Parameter(Mandatory)][ValidateSet('run', 'start', 'script')][string]$Mode, [Parameter(ValueFromRemainingArguments)][string[]]$Rest)

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir 'PocketXR.exe'
$out = Join-Path $dir 'out.txt'
$task = 'PocketXR-dev'
$arguments = ($Rest | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '

if ($Mode -eq 'script') {
    # A PowerShell script next to this one, run in the desktop session (it can see windows), its output handed back.
    Remove-Item $out -ErrorAction SilentlyContinue
    $script = Join-Path $dir $Rest[0]
    # conhost --headless: no console window comes up on the person's screen.
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless cmd.exe /c powershell -NoProfile -ExecutionPolicy Bypass -File `"$script`" > `"$out`" 2>&1"
} elseif ($Mode -eq 'run') {
    Remove-Item $out -ErrorAction SilentlyContinue
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless cmd.exe /c `"`"$exe`" $arguments > `"$out`" 2>&1`""
} else {
    $action = if ($arguments) { New-ScheduledTaskAction -Execute $exe -Argument $arguments } else { New-ScheduledTaskAction -Execute $exe }
}
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 12)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task

if ($Mode -in 'run', 'script') {
    $end = (Get-Date).AddMinutes(2)
    Start-Sleep -Milliseconds 500
    while ((Get-ScheduledTask -TaskName $task).State -eq 'Running' -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 300 }
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    if (Test-Path $out) { Get-Content $out } else { 'No output came back.' }
} else {
    Start-Sleep -Seconds 3
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    if (Get-Process PocketXR -ErrorAction SilentlyContinue) { 'Pocket XR is running on the Deck.' } else { 'Pocket XR did not stay running.' }
}
