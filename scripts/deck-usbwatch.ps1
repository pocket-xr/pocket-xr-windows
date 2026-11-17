# Watches whether the XREAL Aura is on USB and whether its display is there, every [Every] seconds for [Seconds].
# Only reads; sends nothing to the glasses.
param([int]$Seconds = 60, [int]$Every = 2)
$end = (Get-Date).AddSeconds($Seconds)
$last = ''
while ((Get-Date) -lt $end) {
    $usb = [bool](Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_3318&PID_043A\*' })
    $net = [bool](Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '169.254.2.*' })
    $mon = [bool](Get-PnpDevice -PresentOnly -Class Monitor -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'XREAL|Air|Aura|MRG' -or $_.InstanceId -match 'MRG|XRE' })
    $now = "USB $(if ($usb) {'on'} else {'off'}), network $(if ($net) {'up'} else {'down'}), display $(if ($mon) {'there'} else {'gone'})"
    if ($now -ne $last) { "$((Get-Date).ToString('HH:mm:ss')) $now"; $last = $now }
    Start-Sleep -Seconds $Every
}
"$((Get-Date).ToString('HH:mm:ss')) end: $last"
