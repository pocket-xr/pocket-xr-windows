using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>The mouse pointer: where it is, putting it somewhere, and keeping it off the glasses' own display.</summary>
public static class Pointer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    /// <summary>Where the pointer is on the desktop, in pixels; null when Windows will not say (a locked screen).</summary>
    public static (int X, int Y)? Position => GetCursorPos(out NativePoint p) ? (p.X, p.Y) : null;

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    /// <summary>Puts the pointer at [u], [v] (0 to 1 from the left and from the top) of a display.</summary>
    public static void MoveTo(DisplayInfo d, double u, double v) =>
        SetCursorPos(d.X + (int)Math.Round((d.Width - 1) * Math.Clamp(u, 0, 1)), d.Y + (int)Math.Round((d.Height - 1) * Math.Clamp(v, 0, 1)));

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}

/// <summary>"Open when I sign in to Windows": a value under the person's own Run key. No administrator rights.</summary>
public static class SignIn
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Pocket XR";

    public static void Set(bool on)
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (run == null) return;
            string? exe = Environment.ProcessPath;
            if (on && exe != null) run.SetValue(Name, $"\"{exe}\"");
            else run.DeleteValue(Name, throwOnMissingValue: false);
            Log.P($"open at sign-in: {(on ? "on" : "off")}");
        }
        catch (Exception e)
        {
            Log.P($"open at sign-in: Windows refused: {e.Message}");
        }
    }
}
