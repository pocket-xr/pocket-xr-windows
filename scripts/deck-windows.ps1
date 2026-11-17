# Lists Pocket XR's top-level windows on this desktop: title, shown or hidden, where, how big. Only reads.
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    public delegate bool Cb(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
}
"@
$ids = @(Get-Process PocketXR -ErrorAction SilentlyContinue | ForEach-Object Id)
if (-not $ids) { 'Pocket XR is not running.'; exit }
$found = New-Object System.Collections.Generic.List[string]
[W]::EnumWindows({ param($h, $p)
    $pid2 = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($ids -contains $pid2) {
        $sb = New-Object System.Text.StringBuilder 256; [void][W]::GetWindowText($h, $sb, 256)
        $r = New-Object W+R; [void][W]::GetWindowRect($h, [ref]$r)
        if ($sb.Length -gt 0 -or [W]::IsWindowVisible($h)) {
            $found.Add(("{0,-28} {1,-8} {2}" -f ('"' + $sb.ToString() + '"'), $(if ([W]::IsWindowVisible($h)) { if ([W]::IsIconic($h)) { 'minimised' } else { 'shown' } } else { 'hidden' }), "$($r.Ri - $r.L)x$($r.B - $r.T) at $($r.L),$($r.T)"))
        }
    }
    $true }, [IntPtr]::Zero) | Out-Null
if ($found.Count -eq 0) { 'Pocket XR has no windows with a title.' } else { $found }
