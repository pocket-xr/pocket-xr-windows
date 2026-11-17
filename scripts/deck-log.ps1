# Pocket XR's log on this PC, the lines whose time starts with [From] (for example "07:4"), or the last [Tail] lines.
param([string]$From = "", [int]$Tail = 60)
$log = Join-Path $env:LOCALAPPDATA 'Pocket XR\pocket-xr.log'
$lines = Get-Content $log
if ($From) { $lines = $lines | Where-Object { $_.Length -gt 11 -and $_.Substring(11).StartsWith($From) } }
$lines | Select-Object -Last $Tail | ForEach-Object { if ($_.Length -gt 11) { $_.Substring(11) } else { $_ } }
