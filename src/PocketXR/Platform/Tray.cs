using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Platform;

/// <summary>
/// Pocket XR's hidden window: it owns the icon in the notification area and the shortcuts that work in any app.
/// Shortcuts use Windows' RegisterHotKey, which needs no permission and no administrator rights; a shortcut another
/// app already holds is reported and left alone.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const int WmHotKey = 0x0312, WmApp = 0x8000, WmTray = WmApp + 1;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B, NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint ModAlt = 1, ModControl = 2, ModNoRepeat = 0x4000;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;

    private readonly HwndSource source;
    private readonly Dictionary<int, Action> hotKeys = new();
    private readonly uint taskbarCreated;
    private IntPtr icon;
    private string tip = "Pocket XR";
    private bool trayShown;

    /// <summary>The icon was clicked (open the control panel).</summary>
    public event Action? TrayClick;
    /// <summary>The icon was right-clicked at this screen position, in pixels (open the menu).</summary>
    public event Action<int, int>? TrayMenu;

    public IntPtr Handle => source.Handle;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("Pocket XR") { Width = 0, Height = 0, WindowStyle = 0 };
        source = new HwndSource(p);
        source.AddHook(Hook);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Registers Ctrl+Alt+[virtualKey]. False when another app already has it.</summary>
    public bool AddHotKey(int id, uint virtualKey, bool repeat, Action action) =>
        AddHotKey(id, ModAlt | ModControl, virtualKey, repeat, action);

    /// <summary>Registers [modifiers] (1 Alt, 2 Ctrl, 4 Shift, 8 Win) and [virtualKey]. False when another app already has it.</summary>
    public bool AddHotKey(int id, uint modifiers, uint virtualKey, bool repeat, Action action)
    {
        uint mods = modifiers | (repeat ? 0 : ModNoRepeat);
        if (!RegisterHotKey(source.Handle, id, mods, virtualKey))
        {
            Log.P($"shortcut {id} (vk 0x{virtualKey:X2}) is taken by another app: error {Marshal.GetLastWin32Error()}");
            return false;
        }
        hotKeys[id] = action;
        return true;
    }

    /// <summary>Whether Windows would give these keys to Pocket XR now: registered for a moment and let go again.</summary>
    public bool KeysFree(uint modifiers, uint virtualKey)
    {
        const int probe = 0xBFFF;
        if (!RegisterHotKey(source.Handle, probe, modifiers | ModNoRepeat, virtualKey)) return false;
        UnregisterHotKey(source.Handle, probe);
        return true;
    }

    public void RemoveHotKeys()
    {
        foreach (int id in hotKeys.Keys) UnregisterHotKey(source.Handle, id);
        hotKeys.Clear();
    }

    /// <summary>Shows the icon in the notification area, or changes its picture and the words under the pointer.</summary>
    public void ShowTray(IntPtr hIcon, string text)
    {
        icon = hIcon;
        tip = text.Length > 120 ? text[..120] : text;
        var d = Data();
        d.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        if (!trayShown)
        {
            trayShown = Shell_NotifyIcon(NimAdd, ref d);
            d.uVersion = 4; // NOTIFYICON_VERSION_4: clicks arrive with their screen position
            Shell_NotifyIcon(NimSetVersion, ref d);
        }
        else
        {
            Shell_NotifyIcon(NimModify, ref d);
        }
    }

    public void SetTip(string text)
    {
        if (trayShown && icon != IntPtr.Zero) ShowTray(icon, text);
    }

    private NotifyIconData Data() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = source.Handle, uID = 1, uCallbackMessage = WmTray,
        hIcon = icon, szTip = tip, szInfo = "", szInfoTitle = "",
    };

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && hotKeys.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            action();
            handled = true;
        }
        else if (msg == WmTray)
        {
            int what = (int)(lParam.ToInt64() & 0xFFFF);
            int x = (short)(wParam.ToInt64() & 0xFFFF), y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (what is NinSelect or NinKeySelect or WmLButtonUp) TrayClick?.Invoke();
            else if (what is WmContextMenu or WmRButtonUp) TrayMenu?.Invoke(x, y);
            handled = true;
        }
        else if (taskbarCreated != 0 && (uint)msg == taskbarCreated && icon != IntPtr.Zero)
        {
            // Explorer started again: the icon has to be added again.
            trayShown = false;
            ShowTray(icon, tip);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveHotKeys();
        if (trayShown)
        {
            var d = Data();
            Shell_NotifyIcon(NimDelete, ref d);
            trayShown = false;
        }
        source.Dispose();
    }

    /// <summary>Pocket XR's mark, a pair of glasses, drawn at [size] pixels for the notification area.</summary>
    public static IntPtr GlassesIcon(int size, bool lit)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double s = size / 16.0;
            Brush ink = lit ? Theme.B(Look.Held) : Brushes.White;
            var pen = new Pen(ink, 1.5 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(1.25 * s, 5 * s, 5.5 * s, 5 * s), 1.8 * s, 1.8 * s);
            dc.DrawRoundedRectangle(null, pen, new Rect(9.25 * s, 5 * s, 5.5 * s, 5 * s), 1.8 * s, 1.8 * s);
            dc.DrawLine(pen, new Point(6.75 * s, 7 * s), new Point(9.25 * s, 7 * s));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        IntPtr colour = CreateBitmap(size, size, 1, 32, pixels);
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[size * ((size + 15) / 16 * 2)]);
        var info = new IconInfo { fIcon = true, hbmColor = colour, hbmMask = mask };
        IntPtr h = CreateIconIndirect(ref info);
        DeleteObject(colour);
        DeleteObject(mask);
        return h;
    }

    /// <summary>The notification area's icon size on this PC, in pixels.</summary>
    public static int SmallIconSize => Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
