# Changes one of Pocket XR's settings on this PC while Pocket XR is not running: deck-setting.ps1 want3D false
param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)
$file = Join-Path $env:LOCALAPPDATA 'Pocket XR\settings.json'
$s = Get-Content $file -Raw | ConvertFrom-Json
$v = if ($Value -in 'true', 'false') { [bool]::Parse($Value) } elseif ($Value -match '^-?[0-9.]+$') { [double]$Value } else { $Value }
$s | Add-Member -NotePropertyName $Name -NotePropertyValue $v -Force
$s | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding UTF8
"$Name is now $v"
