using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>One display as Windows has it now.</summary>
public sealed record DisplayInfo(
    /// <summary>Windows' own name for the source, such as \\.\DISPLAY3 (what capture and windows are matched by).</summary>
    string GdiName,
    /// <summary>The monitor's name from its EDID, such as "Sceptre M24"; empty when it gives none.</summary>
    string Name,
    /// <summary>Where it sits on the desktop, in pixels: x right, y down.</summary>
    int X, int Y, int Width, int Height,
    double RefreshHz,
    bool Primary,
    /// <summary>The monitor's device path, which names the driver that made it (virtual displays are told by it).</summary>
    string DevicePath,
    long AdapterLuid,
    uint TargetId)
{
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
    public override string ToString() =>
        FormattableString.Invariant($"\"{Name}\" {GdiName} {Width}x{Height} at {X},{Y}, {RefreshHz:0.#} Hz{(Primary ? ", main" : "")}");
}

/// <summary>The PC's displays, from Windows' display configuration (QueryDisplayConfig): names, places and refresh rates.</summary>
public static class Displays
{
    private const uint QdcOnlyActivePaths = 2;
    private const int PathSize = 72, ModeSize = 64;
    private const uint ModeInfoSource = 1;
    private const int GetSourceName = 1, GetTargetName = 2;

    public static List<DisplayInfo> List()
    {
        var found = new List<DisplayInfo>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return found;
            var paths = new byte[Math.Max(pathCount, 1) * PathSize];
            var modes = new byte[Math.Max(modeCount, 1) * ModeSize];
            int result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the set changed between the two calls
            if (result != 0) return found;
            for (int i = 0; i < pathCount; i++)
            {
                var path = new ReadOnlySpan<byte>(paths, i * PathSize, PathSize);
                long sourceAdapter = BitConverter.ToInt64(path[..8]);
                uint sourceId = BitConverter.ToUInt32(path[8..12]);
                uint sourceMode = BitConverter.ToUInt32(path[12..16]);
                long targetAdapter = BitConverter.ToInt64(path[20..28]);
                uint targetId = BitConverter.ToUInt32(path[28..32]);
                // DISPLAYCONFIG_ROTATION: 1 none, 2 a quarter turn, 3 half, 4 three quarters. The Steam Deck's panel is
                // mounted turned (800x1280 to Windows, 1280x800 on the desktop), and the mode below is the panel's own.
                uint rotation = BitConverter.ToUInt32(path[40..44]);
                uint refreshNum = BitConverter.ToUInt32(path[48..52]), refreshDen = BitConverter.ToUInt32(path[52..56]);

                int x = 0, y = 0, w = 0, h = 0;
                if (sourceMode < modeCount)
                {
                    var mode = new ReadOnlySpan<byte>(modes, (int)sourceMode * ModeSize, ModeSize);
                    if (BitConverter.ToUInt32(mode[..4]) == ModeInfoSource)
                    {
                        w = (int)BitConverter.ToUInt32(mode[16..20]);
                        h = (int)BitConverter.ToUInt32(mode[20..24]);
                        x = BitConverter.ToInt32(mode[28..32]);
                        y = BitConverter.ToInt32(mode[32..36]);
                        if (rotation is 2 or 4) (w, h) = (h, w);
                    }
                }
                string gdi = DeviceString(GetSourceName, 84, sourceAdapter, sourceId, 20, 32);
                byte[]? target = DeviceInfo(GetTargetName, 420, targetAdapter, targetId);
                string name = target == null ? "" : Wide(target, 36, 64);
                string devicePath = target == null ? "" : Wide(target, 164, 128);
                double hz = refreshDen == 0 ? 0 : (double)refreshNum / refreshDen;
                found.Add(new DisplayInfo(gdi, name, x, y, w, h, hz, x == 0 && y == 0, devicePath, targetAdapter, targetId));
            }
            return found;
        }
        return found;
    }

    /// <summary>
    /// The glasses' display: one whose name says XREAL, VITURE and the like. Nil while the glasses have no picture
    /// going to them (a PC with no USB-C port that carries video never has one).
    /// </summary>
    public static DisplayInfo? Glasses(List<DisplayInfo> all) =>
        all.Find(d => Core.Glasses.IsGlassesScreenName(d.Name) && !IsVirtual(d) && !Mirrored(all, d));

    /// <summary>
    /// The glasses show the same desktop as another screen (Windows' "Duplicate", Win+P): they are one display to
    /// Windows, so a window over them would cover that screen too. Seen on the Steam Deck, 17 Nov 2026: the Aura came
    /// up mirroring the Deck's own screen.
    /// </summary>
    public static bool GlassesMirrored(List<DisplayInfo> all) =>
        all.Exists(d => Core.Glasses.IsGlassesScreenName(d.Name) && !IsVirtual(d) && Mirrored(all, d));

    /// <summary>
    /// Asks Windows to extend the desktop across every screen, as Win+P, Extend does. Changes how the screens are
    /// joined and nothing else; needs no administrator rights. Pocket XR calls it only when the glasses came back
    /// mirrored: the Aura's 3D and Steady modes look like a new monitor to Windows, which then picks Duplicate
    /// (seen on the Steam Deck, 17 Nov 2026). Mike said yes to this on 17 Nov 2026. True when Windows took it.
    /// </summary>
    public static bool Extend()
    {
        const uint SdcApply = 0x80, SdcTopologyExtend = 0x04;
        int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcApply | SdcTopologyExtend);
        Log.P($"displays: asked Windows to extend the desktop onto the glasses: {(result == 0 ? "done" : $"refused, error {result}")}");
        return result == 0;
    }

    /// <summary>
    /// Puts the floating screens where the pointer should find them, as Pocket XR for Mac does: to the right of the
    /// PC's own screen, laid out as they sit in the glasses ([origins] from DisplayGrid, the first column's top at the
    /// own screen's top), and the glasses' own display to the left of the own screen, out of the pointer's way. Moves only those
    /// displays; the PC's own screen stays where it is. Needs no administrator rights. True when Windows took it.
    /// </summary>
    public static bool Arrange(DisplayInfo own, DisplayInfo? glasses, IReadOnlyList<DisplayInfo> floating, IReadOnlyList<(int X, int Y)> origins)
    {
        var places = new List<(DisplayInfo Display, int X, int Y)>();
        int bottom = own.Y + own.Height;
        for (int i = 0; i < floating.Count && i < origins.Count; i++)
        {
            int x = own.X + own.Width + origins[i].X, y = own.Y + origins[i].Y;
            places.Add((floating[i], x, y));
            bottom = Math.Max(bottom, y + floating[i].Height);
        }
        // The glasses' display to the left of this PC's screen, its top level with it: away from the floating screens,
        // so the pointer leaving them by any edge finds nothing there. (Below them, as on the Mac, was found in the way
        // on the Steam Deck, 17 Nov 2026: going down off a floating screen landed on it.)
        if (glasses != null) places.Add((glasses, own.X - glasses.Width, own.Y));
        if (places.TrueForAll(p => p.Display.X == p.X && p.Display.Y == p.Y)) return true;
        bool ok = MoveDisplays(places);
        Log.P($"displays: floating screens placed beside this PC's screen, the glasses' display to its left: {(ok ? "done" : "not done")}");
        return ok;
    }

    /// <summary>
    /// Moves displays to new places on the desktop through Windows' display configuration (the paths and modes that
    /// are on now, with new positions, applied together). The older way (ChangeDisplaySettingsEx) applies every
    /// display's saved place from the registry, and a display added a moment before has none yet: on the Steam Deck
    /// (17 Nov 2026) a new floating screen went 1.5 s after each first move.
    /// </summary>
    private static bool MoveDisplays(List<(DisplayInfo Display, int X, int Y)> places)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return false;
        var paths = new byte[Math.Max(pathCount, 1) * PathSize];
        var modes = new byte[Math.Max(modeCount, 1) * ModeSize];
        int result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != 0)
        {
            Log.P($"displays: the display configuration could not be read: error {result}");
            return false;
        }
        int moved = 0;
        for (int i = 0; i < pathCount; i++)
        {
            var path = new ReadOnlySpan<byte>(paths, i * PathSize, PathSize);
            long sourceAdapter = BitConverter.ToInt64(path[..8]);
            uint sourceId = BitConverter.ToUInt32(path[8..12]);
            uint sourceMode = BitConverter.ToUInt32(path[12..16]);
            if (sourceMode >= modeCount) continue;
            string gdi = DeviceString(GetSourceName, 84, sourceAdapter, sourceId, 20, 32);
            int at = places.FindIndex(p => p.Display.GdiName == gdi);
            if (at < 0) continue;
            int offset = (int)sourceMode * ModeSize;
            if (BitConverter.ToUInt32(modes, offset) != ModeInfoSource) continue;
            BitConverter.TryWriteBytes(modes.AsSpan(offset + 28, 4), places[at].X);
            BitConverter.TryWriteBytes(modes.AsSpan(offset + 32, 4), places[at].Y);
            moved++;
        }
        if (moved == 0) return false;
        // Apply what is given, keep it for next time, and let Windows adjust where it must.
        const uint SdcApply = 0x80, SdcUseSuppliedDisplayConfig = 0x20, SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;
        int set = SetDisplayConfig(pathCount, paths, modeCount, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges);
        if (set != 0) Log.P($"displays: Windows would not move the displays: error {set}");
        return set == 0;
    }

    /// <summary>
    /// Sets a display's size (and refresh rate where it has it). A virtual display made again with the same id comes
    /// back at the size Windows remembered for it, not the size asked for (the host's displays, 20 Nov 2026: made at
    /// 1784x976, shown at 1280x720), so the size is set after it appears. Kept by Windows for next time. 0 on success.
    /// </summary>
    public static int SetMode(string gdiName, int width, int height, int hz)
    {
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>(), dmDeviceName = "", dmFormName = "" };
        if (!EnumDisplaySettingsW(gdiName, -1, ref dm)) return -1;
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmFields = 0x80000 | 0x100000 | 0x400000; // width, height, frequency
        const uint CdsUpdateRegistry = 0x1;
        int r = ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (r != 0)
        {
            // That rate not offered: the size alone.
            dm.dmFields = 0x80000 | 0x100000;
            r = ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        }
        return r;
    }

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfig(uint pathCount, [In] byte[] paths, uint modeCount, [In] byte[] modes, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent,
            dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, IntPtr paths, uint modeCount, IntPtr modes, uint flags);

    private static bool Mirrored(List<DisplayInfo> all, DisplayInfo d) =>
        all.Exists(o => !ReferenceEquals(o, d) && o.GdiName == d.GdiName && !Core.Glasses.IsGlassesScreenName(o.Name));

    /// <summary>A display made by a virtual display driver and not by a monitor: only the glasses can show it.</summary>
    public static bool IsVirtual(DisplayInfo d) => VirtualDisplays.IsVirtualDevicePath(d.DevicePath);

    /// <summary>
    /// The PC's own screen, where Pocket XR's windows belong: the main one if it is a real monitor, else the first
    /// that is neither the glasses nor a floating screen.
    /// </summary>
    public static DisplayInfo? Own(List<DisplayInfo> all)
    {
        var real = all.FindAll(d => !Core.Glasses.IsGlassesScreenName(d.Name) && !IsVirtual(d));
        return real.Find(d => d.Primary) ?? (real.Count > 0 ? real[0] : all.Find(d => d.Primary));
    }

    public static string Describe(List<DisplayInfo> all) => all.Count == 0 ? "none" : string.Join("; ", all);

    private static byte[]? DeviceInfo(int type, int size, long adapter, uint id)
    {
        var buffer = new byte[size];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), type);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), size);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), adapter);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), id);
        return DisplayConfigGetDeviceInfo(buffer) == 0 ? buffer : null;
    }

    private static string DeviceString(int type, int size, long adapter, uint id, int offset, int chars)
    {
        byte[]? b = DeviceInfo(type, size, adapter, id);
        return b == null ? "" : Wide(b, offset, chars);
    }

    private static string Wide(byte[] buffer, int offset, int chars)
    {
        string s = Encoding.Unicode.GetString(buffer, offset, chars * 2);
        int end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [In, Out] byte[] paths, ref uint modeCount,
        [In, Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);
}
