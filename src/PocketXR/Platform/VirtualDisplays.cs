using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// Pocket XR's floating screens as Windows displays.
///
/// Windows has no way for an app to add a monitor by itself: it takes an indirect display driver, and a driver takes
/// an administrator to install and a signature Windows trusts. So Pocket XR does not carry a driver of its own yet. It
/// talks to one the person has installed, the first found of:
///
/// - SudoVDA (SudoMaker's Virtual Display Adapter, MIT / CC0, the one Apollo installs), through the control calls its
///   public header describes (Common/Include/sudovda-ioctl.h).
/// - Parsec's Virtual Display Adapter (installed with Parsec), through the control calls the open source parsec-vdd
///   project documents (BSD licence; only its published command numbers are used here, none of its code). Found
///   already installed on Mike's Steam Deck, 17 Nov 2026.
///
/// Both add a display when asked and take it away again when asked, or by themselves soon after the app that asked
/// stops answering (SudoVDA's watchdog, Parsec's keep-alive every 100 ms), so a crash never leaves a display behind.
///
/// Everything here is kept in this one file, so another driver (or Pocket XR's own, later) changes one place.
/// </summary>
public sealed class VirtualDisplays : IDisposable
{
    /// <summary>One floating screen's display, as it was made.</summary>
    /// <remarks>SudoVDA names it by adapter and target; Parsec gives an index, and the display is found by its Windows
    /// name ([GdiName]) once it is on the desktop.</remarks>
    public sealed record Screen(Guid Id, int Width, int Height, int RefreshHz, long AdapterLuid, uint TargetId, int Index = -1, string? GdiName = null);

    /// <summary>What the driver said about itself, read without changing anything.</summary>
    public sealed record DriverInfo(string Name, string Version, bool TestBuild, int WatchdogTimeoutS, int WatchdogCountdownS);

    private enum Kind { SudoVda, Parsec }

    // Parsec's Virtual Display Adapter (as the parsec-vdd project documents it): its device interface and commands.
    // Every command sends 32 bytes and gets 4 back, overlapped.
    private static readonly Guid ParsecInterface = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    private const uint ParsecAdd = 0x0022e004, ParsecRemove = 0x0022a008, ParsecUpdate = 0x0022a00c, ParsecVersion = 0x0022e010;
    /// <summary>Parsec's displays go away unless kept alive more often than every 100 ms.</summary>
    private const int ParsecKeepAliveMs = 30;

    private static readonly Guid Interface = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
    private const uint FileDeviceUnknown = 0x22, MethodBuffered = 0, FileAnyAccess = 0;
    private static uint Ctl(uint function) => (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (function << 2) | MethodBuffered;
    private static readonly uint IoctlAdd = Ctl(0x800), IoctlRemove = Ctl(0x801), IoctlGetWatchdog = Ctl(0x803),
        IoctlPing = Ctl(0x888), IoctlGetVersion = Ctl(0x8FF);

    private readonly SafeFileHandle device;
    private readonly Kind kind;
    private readonly List<Screen> screens = new();
    private readonly Thread? watchdog;
    private readonly int pingEveryMs;
    private readonly object gate = new();
    private volatile bool disposed;

    public IReadOnlyList<Screen> Screens => screens;

    public string DriverName => kind == Kind.Parsec ? "Parsec Virtual Display Adapter" : "SudoVDA";

    private VirtualDisplays(SafeFileHandle device, Kind kind, int pingEveryMs)
    {
        this.device = device;
        this.kind = kind;
        // Its own thread, above normal priority: the shared thread pool can stall for longer than Parsec's 100 ms while
        // the app is busy starting (seen on the Steam Deck, 17 Nov 2026: the screens went 6 s after they were made).
        this.pingEveryMs = pingEveryMs;
        if (pingEveryMs > 0)
        {
            watchdog = new Thread(KeepAlive) { IsBackground = true, Name = "Pocket XR display keep-alive", Priority = ThreadPriority.Highest };
            watchdog.Start();
        }
    }

    /// <summary>
    /// A display made by a virtual display driver (SudoVDA, MTT's Virtual Display Driver) and not a monitor, told by
    /// the monitor's device path. The names looked for here are not yet checked against a real virtual display
    /// (`PocketXR --driver-test` prints the path): Pocket XR's own floating screens are known for certain another
    /// way, by the ids the driver gives back when it adds them.
    /// </summary>
    public static bool IsVirtualDevicePath(string devicePath)
    {
        string p = devicePath.ToUpperInvariant();
        return p.Contains("SMKD") || p.Contains("SUDOVDA") || p.Contains("PSCCDD") || p.Contains("MTT1337") || p.Contains("MTTVDD") || p.Contains("ROOT#DISPLAY");
    }

    /// <summary>The driver's own details, or null when none is installed or will open. Changes nothing.</summary>
    public static DriverInfo? Probe(out string why)
    {
        using (SafeFileHandle? h = Open(Interface, false, out why))
        {
            if (h != null)
            {
                var version = new byte[4];
                if (!Io(h, IoctlGetVersion, null, version, out _))
                {
                    why = $"SudoVDA did not answer its version: error {Marshal.GetLastWin32Error()}";
                    return null;
                }
                var dog = new byte[8];
                int timeout = 0, countdown = 0;
                if (Io(h, IoctlGetWatchdog, null, dog, out _))
                {
                    timeout = (int)BitConverter.ToUInt32(dog, 0);
                    countdown = (int)BitConverter.ToUInt32(dog, 4);
                }
                why = "";
                return new DriverInfo("SudoVDA", $"{version[0]}.{version[1]}.{version[2]}", version[3] != 0, timeout, countdown);
            }
        }
        using (SafeFileHandle? h = Open(ParsecInterface, true, out string parsecWhy))
        {
            if (h != null)
            {
                int? minor = ParsecIo(h, ParsecVersion, null);
                if (minor == null)
                {
                    why = $"Parsec's virtual display driver did not answer its version: error {Marshal.GetLastWin32Error()}";
                    return null;
                }
                why = "";
                return new DriverInfo("Parsec Virtual Display Adapter", $"0.{minor}", false, 0, 0);
            }
            if (!parsecWhy.StartsWith("no ", StringComparison.Ordinal)) why = parsecWhy;
        }
        return null;
    }

    /// <summary>
    /// Adds [count] displays of [width] by [height] at [refreshHz]. Null (with the reason in [why]) when the driver
    /// is not there or refuses; whatever was added before a refusal is removed again.
    /// </summary>
    public static VirtualDisplays? Make(int count, int width, int height, int refreshHz, out string why)
    {
        SafeFileHandle? h = Open(Interface, false, out why);
        if (h == null)
        {
            SafeFileHandle? parsec = Open(ParsecInterface, true, out string parsecWhy);
            if (parsec != null) return MakeParsec(parsec, count, out why);
            if (!parsecWhy.StartsWith("no ", StringComparison.Ordinal)) why = parsecWhy;
            return null;
        }
        var dog = new byte[8];
        int timeoutS = Io(h, IoctlGetWatchdog, null, dog, out _) ? (int)BitConverter.ToUInt32(dog, 0) : 0;
        var made = new VirtualDisplays(h, Kind.SudoVda, timeoutS > 0 ? Math.Max(timeoutS * 1000 / 3, 250) : 0);
        for (int i = 0; i < count; i++)
        {
            // The same id each run for the same screen, so Windows remembers where the person put it.
            var id = new Guid(0x504B5852, 0x5343, 0x524E, 0x80, 0, 0, 0, 0, 0, 0, (byte)(i + 1));
            var input = new byte[56];
            BitConverter.TryWriteBytes(input.AsSpan(0, 4), (uint)width);
            BitConverter.TryWriteBytes(input.AsSpan(4, 4), (uint)height);
            BitConverter.TryWriteBytes(input.AsSpan(8, 4), (uint)refreshHz);
            id.TryWriteBytes(input.AsSpan(12, 16));
            Encoding.ASCII.GetBytes($"Pocket XR {i + 1}").CopyTo(input.AsSpan(28, 13));
            Encoding.ASCII.GetBytes($"PXR{i + 1:D4}").CopyTo(input.AsSpan(42, 13));
            var output = new byte[12];
            if (!Io(h, IoctlAdd, input, output, out _))
            {
                why = $"the driver refused screen {i + 1}: error {Marshal.GetLastWin32Error()}";
                made.Dispose();
                return null;
            }
            var s = new Screen(id, width, height, refreshHz, BitConverter.ToInt64(output, 0), BitConverter.ToUInt32(output, 8));
            made.screens.Add(s);
            Log.P($"virtual displays: added screen {i + 1}, {width}x{height} at {refreshHz} Hz, target {s.TargetId}");
        }
        why = "";
        return made;
    }

    /// <summary>
    /// Parsec: adds [count] displays and finds each on the desktop (a display Windows lists that was not there before,
    /// with Parsec's monitor id in its path). Parsec chooses the size and refresh rate itself.
    /// </summary>
    private static VirtualDisplays? MakeParsec(SafeFileHandle h, int count, out string why)
    {
        var made = new VirtualDisplays(h, Kind.Parsec, ParsecKeepAliveMs);
        var known = new HashSet<string>(Displays.List().ConvertAll(d => d.GdiName));
        for (int i = 0; i < count; i++)
        {
            int? index;
            lock (made.gate) index = ParsecIo(h, ParsecAdd, null);
            if (index == null)
            {
                why = $"Parsec's virtual display driver refused screen {i + 1}: error {Marshal.GetLastWin32Error()}";
                made.Dispose();
                return null;
            }
            // Windows takes a moment to put the new display on the desktop.
            DisplayInfo? seen = null;
            for (int wait = 0; wait < 40 && seen == null; wait++)
            {
                Thread.Sleep(100);
                seen = Displays.List().Find(d => !known.Contains(d.GdiName) && d.DevicePath.ToUpperInvariant().Contains("PSCCDD"));
            }
            if (seen != null) known.Add(seen.GdiName);
            var s = new Screen(Guid.Empty, seen?.Width ?? 0, seen?.Height ?? 0, (int)Math.Round(seen?.RefreshHz ?? 0), seen?.AdapterLuid ?? 0,
                seen?.TargetId ?? 0, index.Value, seen?.GdiName);
            made.screens.Add(s);
            Log.P($"virtual displays: Parsec added screen {i + 1} (index {index}): {(seen != null ? seen.ToString() : "not seen on the desktop within 4 s")}");
        }
        why = "";
        return made;
    }

    /// <summary>Windows' display for one of these screens, as Windows has it now; null when it is not on the desktop.</summary>
    public static DisplayInfo? Find(Screen s, List<DisplayInfo> all) =>
        s.GdiName != null
            ? all.Find(d => d.GdiName == s.GdiName && d.DevicePath.ToUpperInvariant().Contains("PSCCDD"))
            : all.Find(d => d.TargetId == s.TargetId && d.AdapterLuid == s.AdapterLuid);

    private void KeepAlive()
    {
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        int late = 0;
        while (!disposed)
        {
            Thread.Sleep(pingEveryMs);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double gapMs = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            last = now;
            if (kind == Kind.Parsec && gapMs > 90 && late++ < 20) Log.P($"virtual displays: keep-alive {gapMs:0} ms late");
            Ping();
        }
    }

    private void Ping()
    {
        lock (gate)
        {
            if (disposed) return;
            if (kind == Kind.Parsec) ParsecIo(device, ParsecUpdate, null);
            else Io(device, IoctlPing, null, null, out _);
        }
    }

    /// <summary>Takes every display away again; Windows moves their windows back to the PC's own screen.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (Screen s in screens)
            {
                bool ok = kind == Kind.Parsec
                    // The index as two bytes, high byte first, as parsec-vdd documents.
                    ? ParsecIo(device, ParsecRemove, new[] { (byte)(s.Index >> 8), (byte)s.Index }) != null
                    : Io(device, IoctlRemove, s.Id.ToByteArray(), null, out _);
                string what = kind == Kind.Parsec ? $"Parsec display {s.Index}" : $"target {s.TargetId}";
                Log.P($"virtual displays: removed {what}{(ok ? "" : $" (error {Marshal.GetLastWin32Error()})")}");
            }
            screens.Clear();
            device.Dispose();
        }
    }

    private static SafeFileHandle? Open(Guid g, bool overlapped, out string why)
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref g, null, 0) != 0 || size <= 1)
        {
            why = "no virtual display driver Pocket XR knows is installed";
            return null;
        }
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref g, null, buffer, size, 0) != 0)
        {
            why = "the virtual display driver could not be listed";
            return null;
        }
        string path = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } list ? list[0] : "";
        if (path.Length == 0)
        {
            why = "the virtual display driver is installed but not running";
            return null;
        }
        SafeFileHandle h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero);
        if (h.IsInvalid)
        {
            why = $"the virtual display driver would not open: error {Marshal.GetLastWin32Error()}";
            h.Dispose();
            return null;
        }
        why = "";
        return h;
    }

    private static bool Io(SafeFileHandle h, uint code, byte[]? input, byte[]? output, out uint returned) =>
        DeviceIoControl(h, code, input, (uint)(input?.Length ?? 0), output, (uint)(output?.Length ?? 0), out returned, IntPtr.Zero);

    /// <summary>One Parsec command: 32 bytes in (data at the start, the rest zero), 4 bytes back, overlapped, waited for
    /// up to 5 s. The 4 bytes as a number, or null when it failed.</summary>
    private static unsafe int? ParsecIo(SafeFileHandle h, uint code, byte[]? data)
    {
        var input = new byte[32];
        if (data != null) Array.Copy(data, input, Math.Min(data.Length, 32));
        var output = new byte[4];
        using var done = new ManualResetEvent(false);
        NativeOverlapped* o = (NativeOverlapped*)Marshal.AllocHGlobal(sizeof(NativeOverlapped));
        try
        {
            *o = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
            fixed (byte* inp = input)
            fixed (byte* outp = output)
            {
                bool ok = DeviceIoControlOverlapped(h, code, inp, 32, outp, 4, out _, o);
                if (!ok && Marshal.GetLastWin32Error() != 997) return null; // not "pending": refused
                if (!ok)
                {
                    if (!done.WaitOne(5000))
                    {
                        CancelIoEx(h, o);
                        GetOverlappedResult(h, o, out _, true);
                        return null;
                    }
                    if (!GetOverlappedResult(h, o, out _, false)) return null;
                }
            }
            return BitConverter.ToInt32(output, 0);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)o);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern unsafe bool DeviceIoControlOverlapped(SafeFileHandle device, uint code, byte* input, uint inputSize, byte* output, uint outputSize,
        out uint returned, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll")]
    private static extern unsafe bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
