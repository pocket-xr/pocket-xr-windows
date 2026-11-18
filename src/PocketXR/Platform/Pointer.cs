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

    /// <summary>Presses or lets go of the left mouse button where the pointer is (hand tracking's pinch). Windows
    /// does not let it reach a window run as administrator.</summary>
    public static void Button(bool down)
    {
        var input = new MouseInput { Type = 0, Flags = down ? 0x0002u : 0x0004u };
        SendInput(1, new[] { input }, Marshal.SizeOf<MouseInput>());
    }

    // INPUT with its MOUSEINPUT, laid out as on 64-bit Windows (40 bytes: the union starts 8 bytes in).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInput[] inputs, int size);

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
