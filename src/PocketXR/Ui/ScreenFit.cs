using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PocketXR.Core;

namespace PocketXR.Ui;

/// <summary>
/// Keeps a window inside the work area of the screen it is on: its height no more than that screen's (so its content
/// scrolls instead), and the whole window on that screen. Checked when it opens, when it moves to a screen with another
/// scale, and when the PC's screens change. SystemParameters.WorkArea, used before, is the main screen's, measured at
/// the main screen's scale: with "Second screen only" or screens of different scales the control panel's bottom (from
/// Launcher down) ran off the screen it was on (Pierre Zammit, 22 Nov 2026).
/// </summary>
public static class ScreenFit
{
    /// <summary>Keeps [w] fitted. With [height] it also caps the window's height at its screen's work area.</summary>
    public static void Keep(Window w, bool height = true)
    {
        void Fit(bool move)
        {
            try { FitNow(w, height, move); }
            catch (Exception e) when (e is InvalidOperationException or COMException) { }
        }
        EventHandler screens = (_, _) => w.Dispatcher.BeginInvoke(() => Fit(true));
        // After the window's other Loaded handlers (which place it), so the fit has the last word.
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() => Fit(true));
        w.DpiChanged += (_, _) => w.Dispatcher.BeginInvoke(() => Fit(true));
        w.LocationChanged += (_, _) => Fit(false);
        w.SizeChanged += (_, _) => Fit(false);
        SystemEvents.DisplaySettingsChanged += screens;
        w.Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= screens;
    }

    private static void FitNow(Window w, bool height, bool move)
    {
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || w.WindowState != WindowState.Normal) return;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        Box work = info.work;
        double scale = VisualTreeHelper.GetDpi(w).DpiScaleY;
        if (height)
        {
            double most = WindowFit.MaxHeight(work.Top, work.Bottom, scale);
            if (Math.Abs(w.MaxHeight - most) > 0.5) w.MaxHeight = most;
        }
        if (!move || !GetWindowRect(hwnd, out Box r)) return;
        int width = r.Right - r.Left, tall = r.Bottom - r.Top;
        var (left, top) = WindowFit.Place(r.Left, r.Top, width, tall, work.Left, work.Top, work.Right, work.Bottom);
        if (left == r.Left && top == r.Top) return;
        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010); // no size, no z-order, no activate
        Log.P($"window \"{w.Title}\": kept on its screen, moved from {r.Left},{r.Top} to {left},{top}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Box { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public Box monitor, work; public uint flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Box rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
