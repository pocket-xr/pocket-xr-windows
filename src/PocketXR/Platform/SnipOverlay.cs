using System;
using System.Runtime.InteropServices;
using System.Text;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// Win+Shift+S on a floating screen. Windows' snipping overlay puts a still, dimmed window on every monitor, the glasses'
/// display too, above Pocket XR's picture of the Space there: the glasses then showed a still picture, so a snip on a
/// floating screen could not be seen to be made (a tester's XBX A01+, 24 Nov 2026). The overlay is one window per
/// monitor ("Snipping Tool Overlay", class XamlWindow) under a root one; hiding only the glasses' display's own leaves
/// the rest working (measured on a PC with two monitors: that monitor live again, the other still dimmed and
/// snipping, Escape closed it as usual). So that window is hidden while Pocket XR draws on the glasses.
/// </summary>
internal static class SnipOverlay
{
    private const string Title = "Snipping Tool Overlay";

    /// <summary>Hides the snipping overlay's window that covers [display]; how many were hidden.</summary>
    public static int HideOn(DisplayInfo display)
    {
        int hidden = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(64);
            GetWindowTextW(hwnd, title, title.Capacity);
            if (title.ToString() != Title) return true;
            var cls = new StringBuilder(64);
            GetClassNameW(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "XamlWindow" || !GetWindowRect(hwnd, out Box r)) return true;
            // Its own window for this display: inside it (the root window spans every monitor and is left alone).
            if (r.Left >= display.X && r.Top >= display.Y && r.Right <= display.X + display.Width && r.Bottom <= display.Y + display.Height)
            {
                ShowWindow(hwnd, 0); // SW_HIDE
                hidden++;
            }
            return true;
        }, IntPtr.Zero);
        return hidden;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Box { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Box rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
