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
        bool changed = false, ok = true;
        foreach ((DisplayInfo d, int x, int y) in places)
        {
            if (d.X == x && d.Y == y) continue;
            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(d.GdiName, -1, ref mode)) { ok = false; continue; } // ENUM_CURRENT_SETTINGS
            mode.dmPositionX = x;
            mode.dmPositionY = y;
            mode.dmFields = 0x20; // DM_POSITION
            int r = ChangeDisplaySettingsExW(d.GdiName, ref mode, IntPtr.Zero, 0x01 | 0x10000000, IntPtr.Zero); // CDS_UPDATEREGISTRY | CDS_NORESET
            if (r != 0) { ok = false; Log.P($"displays: {d.GdiName} not moved: error {r}"); }
            changed = true;
        }
        if (!changed) return ok;
        int applied = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.P($"displays: floating screens placed beside this PC's screen, the glasses' display to its left: {(applied == 0 && ok ? "done" : $"error {applied}")}");
        return applied == 0 && ok;
    }

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
