using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Why [d] is or is not taken as the glasses' display, for logs and the glasses report.</summary>
    public static string Why(DisplayInfo d, List<DisplayInfo> all)
    {
        if (!Core.Glasses.IsGlassesScreenName(d.Name))
            return d.Name.Length == 0 ? "no name from the display, so not the glasses" : "not a glasses name";
        if (IsVirtual(d)) return "a virtual display, not the glasses";
        if (Mirrored(all, d)) return "a copy of another screen (Windows is duplicating)";
        return "the glasses' display";
    }

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
    /// <summary>The places of the floating screens and the glasses' display, with this PC's screen on [side].</summary>
    public static List<(DisplayInfo Display, int X, int Y)> Places(DisplayInfo own, DisplayInfo? glasses, IReadOnlyList<DisplayInfo> floating,
        IReadOnlyList<(int X, int Y)> origins, PcSide side)
    {
        var places = new List<(DisplayInfo Display, int X, int Y)>();
        var sizes = floating.Select(d => (d.Width, d.Height)).ToList();
        var (bw, bh) = DisplayGrid.Block(origins, sizes);
        var (bx, by) = DisplayGrid.BlockOrigin(side, own.X, own.Y, own.Width, own.Height, bw, bh);
        for (int i = 0; i < floating.Count && i < origins.Count; i++)
            places.Add((floating[i], bx + origins[i].X, by + origins[i].Y));
        // The glasses' display beside this PC's screen, its top level with it, on the side away from the floating
        // screens, so the pointer leaving them by any edge finds nothing there. (Below them, as on the Mac, was found
        // in the way on the Steam Deck, 17 Nov 2026: going down off a floating screen landed on it.)
        if (glasses != null)
        {
            var (gx, gy) = DisplayGrid.GlassesOrigin(side, own.X, own.Y, own.Width, glasses.Width);
            places.Add((glasses, gx, gy));
        }
        return places;
    }

    /// <summary>
    /// Every display in [all], at its place in [moves] or where it is now, all moved together so [mainGdi] (Windows'
    /// main display) stays at 0,0: Windows makes main whichever display is there, and refuses a set with none there
    /// (error 87, seen in a tester's log on 0.4.9 after he made a floating screen main in Windows' Settings).
    /// </summary>
    public static List<(DisplayInfo Display, int X, int Y)> WithMain(List<DisplayInfo> all,
        IReadOnlyList<(DisplayInfo Display, int X, int Y)> moves, string? mainGdi)
    {
        var list = new List<(DisplayInfo Display, int X, int Y)>();
        foreach (DisplayInfo d in all)
        {
            int at = -1;
            for (int i = 0; i < moves.Count && at < 0; i++)
                if (string.Equals(moves[i].Display.GdiName, d.GdiName, StringComparison.OrdinalIgnoreCase)) at = i;
            list.Add(at >= 0 ? (d, moves[at].X, moves[at].Y) : (d, d.X, d.Y));
        }
        int main = mainGdi == null ? -1 : list.FindIndex(p => string.Equals(p.Display.GdiName, mainGdi, StringComparison.OrdinalIgnoreCase));
        (int X, int Y)[] shifted = MainDisplay.AtOrigin(list.Select(p => (p.X, p.Y)).ToList(), main);
        return list.Select((p, i) => (p.Display, shifted[i].X, shifted[i].Y)).ToList();
    }

    /// <summary>Windows' main display now (the one at 0,0), or null.</summary>
    public static DisplayInfo? MainOf(List<DisplayInfo> all) => all.Find(d => d.Primary);

    /// <summary>
    /// Places the floating screens and the glasses' display (Places), keeping whichever display is Windows' main one
    /// now at 0,0 (WithMain), so a main display chosen in Windows' Settings stays main. True when done or already so.
    /// </summary>
    public static bool Arrange(List<DisplayInfo> all, DisplayInfo own, DisplayInfo? glasses, IReadOnlyList<DisplayInfo> floating, IReadOnlyList<(int X, int Y)> origins, PcSide side = PcSide.Left)
    {
        var places = WithMain(all, Places(own, glasses, floating, origins, side), MainOf(all)?.GdiName);
        if (places.TrueForAll(p => p.Display.X == p.X && p.Display.Y == p.Y)) return true;
        bool ok = MoveDisplays(places);
        Log.P($"displays: floating screens placed with this PC's screen {side.Title().ToLowerInvariant()} of them, the glasses' display out of their way: {(ok ? "done" : "not done")}");
        return ok;
    }

    /// <summary>
    /// Makes [gdiName] Windows' main display (the taskbar's and new windows' home), as "Make this my main display" in
    /// Windows' Settings does: every display moves by the same amount so it is at 0,0, and they stay joined as they
    /// were. Through SetDisplayConfig, as MoveDisplays. True when Windows took it, or it was already main.
    /// </summary>
    public static bool MakeMain(string gdiName)
    {
        List<DisplayInfo> all = List();
        if (MainOf(all) is DisplayInfo now && string.Equals(now.GdiName, gdiName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!all.Exists(d => string.Equals(d.GdiName, gdiName, StringComparison.OrdinalIgnoreCase))) return false;
        return MoveDisplays(WithMain(all, Array.Empty<(DisplayInfo, int, int)>(), gdiName));
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
            int at = places.FindIndex(p => string.Equals(p.Display.GdiName, gdi, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Turns [gdiName] a quarter (portrait: 1920 x 1080 becomes 1080 x 1920 on the desktop) or back upright, as Windows'
    /// Display settings, Orientation, do. 0 when done (or already so), else Windows' answer.
    /// </summary>
    public static int SetPortrait(string gdiName, bool portrait)
    {
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>(), dmDeviceName = "", dmFormName = "" };
        if (!EnumDisplaySettingsW(gdiName, -1, ref dm)) return -1;
        uint want = portrait ? 1u : 0u; // DMDO_90, DMDO_DEFAULT
        bool turnedNow = dm.dmDisplayOrientation is 1 or 3;
        if (dm.dmDisplayOrientation == want) return 0;
        if (turnedNow != portrait) (dm.dmPelsWidth, dm.dmPelsHeight) = (dm.dmPelsHeight, dm.dmPelsWidth);
        dm.dmDisplayOrientation = want;
        dm.dmFields = 0x80 | 0x80000 | 0x100000; // orientation, width, height
        return ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, 0x1, IntPtr.Zero); // CDS_UPDATEREGISTRY
    }

    /// <summary>Whether [gdiName] is turned a quarter now.</summary>
    public static bool IsPortrait(string gdiName)
    {
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>(), dmDeviceName = "", dmFormName = "" };
        return EnumDisplaySettingsW(gdiName, -1, ref dm) && dm.dmDisplayOrientation is 1 or 3;
    }

    /// <summary>The sizes [gdiName] offers (its driver's list of modes), largest first, without repeats.</summary>
    public static List<(int Width, int Height)> Modes(string gdiName)
    {
        var found = new List<(int, int)>();
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>(), dmDeviceName = "", dmFormName = "" };
        for (int i = 0; i < 2000 && EnumDisplaySettingsW(gdiName, i, ref dm); i++)
        {
            var size = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            if (!found.Contains(size)) found.Add(size);
        }
        return found.OrderByDescending(s => s.Item1 * s.Item2).ToList();
    }

    // Windows' per-display scale (Settings, Display, Scale): DisplayConfigGetDeviceInfo and SetDeviceInfo with types
    // -3 and -4, not in Windows' headers but stable since Windows 10 (the SetDPI tool and others use them). Counted in
    // steps from the display's recommended scale (ScreenLook.RelFor).
    private const int GetDpiScale = -3, SetDpiScale = -4;

    /// <summary>The source (adapter and id) of the display Windows calls [gdiName], or null.</summary>
    private static (long Adapter, uint Id)? Source(string gdiName)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return null;
        var paths = new byte[Math.Max(pathCount, 1) * PathSize];
        var modes = new byte[Math.Max(modeCount, 1) * ModeSize];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < pathCount; i++)
        {
            long adapter = BitConverter.ToInt64(paths, i * PathSize);
            uint id = BitConverter.ToUInt32(paths, i * PathSize + 8);
            if (string.Equals(DeviceString(GetSourceName, 84, adapter, id, 20, 32), gdiName, StringComparison.OrdinalIgnoreCase))
                return (adapter, id);
        }
        return null;
    }

    /// <summary>[gdiName]'s scale now, percent, and its lowest and highest steps; null if Windows does not say.</summary>
    public static (int Percent, int MinRel, int MaxRel)? Scale(string gdiName)
    {
        if (Source(gdiName) is not (long adapter, uint id)) return null;
        byte[]? b = DeviceInfo(GetDpiScale, 32, adapter, id);
        if (b == null) return null;
        int min = BitConverter.ToInt32(b, 20), cur = BitConverter.ToInt32(b, 24), max = BitConverter.ToInt32(b, 28);
        return (ScreenLook.PercentFor(cur, min), min, max);
    }

    /// <summary>Sets [gdiName]'s Windows scale to [percent] (100, 125, 150...). The scale it has afterwards, or null.</summary>
    public static int? SetScale(string gdiName, int percent)
    {
        if (Scale(gdiName) is not (int now, int min, int max)) return null;
        if (now == percent) return now;
        if (ScreenLook.RelFor(percent, min, max) is not int rel || Source(gdiName) is not (long adapter, uint id)) return now;
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), SetDpiScale);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), 24);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), adapter);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), id);
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 4), rel);
        DisplayConfigSetDeviceInfo(buffer);
        return Scale(gdiName)?.Percent;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo([In] byte[] packet);

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
