# A picture of this PC's main screen (its own, real pixels), saved next to this script as shot.png, and its scaling.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class D {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
"@
[void][D]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) # per monitor v2: real pixels
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$s = [System.Windows.Forms.Screen]::PrimaryScreen
$b = $s.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $b.Size)
$file = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'shot.png'
$bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
"main screen $($b.Width)x$($b.Height), work area $($s.WorkingArea.Width)x$($s.WorkingArea.Height), scaling $([math]::Round([D]::GetDpiForSystem() / 96 * 100)) %"
