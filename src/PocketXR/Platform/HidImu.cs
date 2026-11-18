using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// Head tracking over USB HID: the XREAL Air family and VITURE glasses, through Windows' own HID driver (hid.dll and
/// plain file reads and writes). After Pocket XR's AirImuLink and VitureImuLink on Android, the Deck's hidimu.py and
/// the Mac's HidImuReader. UNTESTED: the project has no Air or VITURE glasses, and no part of this has run with any.
///
/// Samples come out as the network stream's do, so HeadFusion takes them unchanged: gyro in rad/s, accelerometer in
/// m/s², time in ns. `glasses.Axes` says which axes they are in.
///
/// Which HID device: the Air's IMU is USB interface 3 (2 on the Air 2 Ultra). On Windows each USB interface of the
/// glasses is its own HID device, and its path says which interface it is ("&amp;mi_03"), so the IMU's own interface is
/// found by that and the glasses' other HID interfaces are left alone. VITURE: the Beast's interface 5 first, then
/// each other HID device of the glasses in turn, as the Deck does. Every candidate seen is logged, so a tester's log
/// shows what the PC listed.
///
/// What is written to the glasses: only the Air's stream command (0x19, start or stop) and VITURE's raw stream
/// command (0x0301, start or stop), exactly as AirImuProtocol and VitureImuProtocol build them. Nothing else.
///
/// Needs no administrator rights and no permission. [onSample] is called on the reader's own thread.
/// </summary>
public sealed class HidImuReader
{
    private sealed record Candidate(string Path, int Vendor, int Product, int? Interface, int UsagePage, int Usage, int MaxInput, int MaxOutput)
    {
        public string Line =>
            $"{Vendor:X4}:{Product:X4} {(Interface is int i ? $"interface {i}" : "interface not known")}, usage page 0x{UsagePage:X4} usage 0x{Usage:X2}, reports in {MaxInput} B out {MaxOutput} B";
    }

    private readonly Glasses glasses;
    private readonly Action<ImuSample> onSample;
    private readonly object gate = new();
    private volatile bool stopped;
    private bool started;
    private string? connectedVia;
    private int samples;
    private string loggedCandidates = "";

    /// <summary>Samples read since Start().</summary>
    public int Samples => Volatile.Read(ref samples);

    /// <summary>"USB HID (XREAL Air 2)" once reports arrive, else null.</summary>
    public string? ConnectedVia { get { lock (gate) return connectedVia; } }

    public HidImuReader(Glasses glasses, Action<ImuSample> onSample)
    {
        this.glasses = glasses;
        this.onSample = onSample;
    }

    /// <summary>Opens the device's IMU interface, sends the start command and reads, on its own thread. Does nothing
    /// for glasses without a HID stream.</summary>
    public void Start()
    {
        if (!glasses.HidImu)
        {
            Log.P($"hid imu: {glasses.Name} has no HID motion stream Pocket XR knows; not started");
            return;
        }
        lock (gate)
        {
            if (started) return;
            started = true;
        }
        new Thread(Run) { Name = "Pocket XR HID head tracking", IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>Sends stop and closes. The reader's thread does both within about a quarter of a second.</summary>
    public void Stop() => stopped = true;

    private void Wait(double seconds)
    {
        for (double waited = 0; waited < seconds && !stopped; waited += 0.1) Thread.Sleep(100);
    }

    private void Run()
    {
        Log.P($"hid imu: looking for {glasses.Name}'s motion stream over USB HID (untested on a PC)");
        while (!stopped)
        {
            List<Candidate> found = Candidates();
            string text = found.Count == 0 ? "none" : string.Join("; ", found.Select(c => c.Line));
            if (text != loggedCandidates)
            {
                loggedCandidates = text;
                Log.P($"hid imu: HID devices of {glasses.Name} the PC lists: {text}");
            }
            List<Candidate> order = Ordered(found);
            if (order.Count == 0)
            {
                Wait(2.0);
                continue;
            }
            bool streamed = false;
            foreach (Candidate c in order)
            {
                if (stopped) break;
                if (Session(c))
                {
                    streamed = true;
                    break;
                }
            }
            Wait(streamed ? 1.0 : 3.0);
        }
        Log.P("hid imu: stopped");
    }

    // MARK: finding the device

    private static readonly Regex PathIds = new(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&mi_([0-9a-f]{2}))?", RegexOptions.IgnoreCase);

    private List<Candidate> Candidates()
    {
        var found = new List<Candidate>();
        HidD_GetHidGuid(out Guid hid);
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref hid, null, 0) != 0 || size <= 1) return found;
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref hid, null, buffer, size, 0) != 0) return found;
        foreach (string path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = PathIds.Match(path);
            if (!m.Success) continue;
            int vendor = Convert.ToInt32(m.Groups[1].Value, 16), product = Convert.ToInt32(m.Groups[2].Value, 16);
            if (vendor != glasses.Vendor) continue;
            if (glasses.Family == GlassesFamily.Air && product != glasses.Product) continue;
            if (glasses.Family == GlassesFamily.Viture && product == Glasses.VitureMicrophone) continue;
            int? intf = m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : null;
            int usagePage = 0, usage = 0, maxIn = 0, maxOut = 0;
            // Opened with no access at all, only to ask what its reports are.
            using (SafeFileHandle h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (!h.IsInvalid && HidD_GetPreparsedData(h, out IntPtr data))
                {
                    if (HidP_GetCaps(data, out HidCaps caps) == 0x00110000)
                    {
                        usagePage = caps.UsagePage;
                        usage = caps.Usage;
                        // Windows counts the report id's byte in these lengths.
                        maxIn = Math.Max(caps.InputReportByteLength - 1, 0);
                        maxOut = Math.Max(caps.OutputReportByteLength - 1, 0);
                    }
                    HidD_FreePreparsedData(data);
                }
            }
            found.Add(new Candidate(path, vendor, product, intf, usagePage, usage, maxIn, maxOut));
        }
        // A fixed order: by interface number, unknown ones last.
        return found.OrderBy(c => c.Interface ?? 1000).ThenByDescending(c => c.UsagePage).ToList();
    }

    /// <summary>The candidates to try, best first.</summary>
    private List<Candidate> Ordered(List<Candidate> found)
    {
        if (glasses.Family == GlassesFamily.Air)
        {
            int wanted = AirImuProtocol.ImuInterface(glasses.Product);
            List<Candidate> known = found.Where(c => c.Interface != null).ToList();
            // Only the IMU's own interface is ever written to; the glasses' other HID interfaces are left alone.
            if (known.Count > 0) return known.Where(c => c.Interface == wanted).ToList();
            // No interface numbers at all: the 64 byte report devices, vendor defined usage pages first.
            return found.Where(c => c.MaxInput >= AirImuProtocol.PacketBytes)
                .OrderByDescending(c => c.UsagePage >= 0xFF00).ThenByDescending(c => c.UsagePage).ToList();
        }
        // VITURE: the Beast's motion sensor is interface 5; other models are tried interface by interface.
        return found
            .OrderByDescending(c => Glasses.IsBeastProduct(c.Product) && c.Interface == VitureImuProtocol.BeastImuInterface)
            .ThenBy(c => c.Interface ?? 1000).ToList();
    }

    // MARK: one open device

    private byte[] StartCommand() => glasses.Family == GlassesFamily.Air
        ? AirImuProtocol.StreamCommand(true) : VitureImuProtocol.Padded(VitureImuProtocol.StartRaw());

    private byte[] StopCommand() => glasses.Family == GlassesFamily.Air
        ? AirImuProtocol.StreamCommand(false) : VitureImuProtocol.Padded(VitureImuProtocol.Stop());

    /// <summary>Opens [c], starts its stream and reads until the stream ends or Stop(). True if samples came.</summary>
    private bool Session(Candidate c)
    {
        // Read and write, shared, with reads that can be waited on (so Stop() is seen while none comes).
        using SafeFileHandle device = CreateFileW(c.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (device.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            Log.P($"hid imu: could not open {c.Line}: {(error == 5 ? "Windows did not allow it (another app may hold the device)" : $"error {error}")}");
            return false;
        }
        int inLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxInput) + 1, outLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxOutput) + 1;
        var airClock = new AirTimestamp();
        var vitureClock = new VitureClock();
        int sessionSamples = 0, sessionOther = 0;
        bool removed = false;
        using var done = new ManualResetEvent(false);
        IntPtr buffer = Marshal.AllocHGlobal(inLength), overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        long lastSample = Stopwatch.GetTimestamp();

        // Reads one report, waiting up to [ms]. Null: nothing came. Empty: the device is gone.
        byte[]? Read(int ms)
        {
            done.Reset();
            var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
            Marshal.StructureToPtr(overlapped, overlappedPtr, false);
            bool ok = ReadFile(device, buffer, (uint)inLength, out uint read, overlappedPtr);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 997) return Array.Empty<byte>(); // not "pending": the device went
                if (!done.WaitOne(ms))
                {
                    CancelIo(device);
                    done.WaitOne(200);
                    return null;
                }
                if (!GetOverlappedResult(device, overlappedPtr, out read, false)) return Array.Empty<byte>();
            }
            if (read <= 1) return null;
            // The first byte is the report id (0: neither protocol numbers its reports).
            var report = new byte[read - 1];
            Marshal.Copy(buffer + 1, report, 0, report.Length);
            return report;
        }

        bool Send(byte[] bytes, string what)
        {
            var report = new byte[outLength];
            Array.Copy(bytes, 0, report, 1, Math.Min(bytes.Length, outLength - 1));
            using var sent = new ManualResetEvent(false);
            IntPtr o = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            try
            {
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = sent.SafeWaitHandle.DangerousGetHandle() }, o, false);
                bool ok = WriteFile(device, report, (uint)report.Length, out _, o);
                if (!ok && Marshal.GetLastWin32Error() == 997) ok = sent.WaitOne(1000) && GetOverlappedResult(device, o, out _, false);
                if (!ok) Log.P($"hid imu: the {what} command was not written: error {Marshal.GetLastWin32Error()}");
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(o);
            }
        }

        void Received(byte[] bytes)
        {
            ImuSample? sample = null;
            if (glasses.Family == GlassesFamily.Air)
            {
                if (AirImuProtocol.Parse(bytes) is ImuSample s)
                {
                    s.TimestampNs = airClock.ToNs(s.TimestampNs);
                    sample = s;
                }
            }
            else if (VitureImuProtocol.ParseReport(bytes) is VitureImuProtocol.RawSample raw)
            {
                ulong hostNs = (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
                sample = VitureImuProtocol.Sample(raw, vitureClock.ToNs(raw.Counter, hostNs));
            }
            if (sample is not ImuSample got)
            {
                sessionOther++;
                return;
            }
            sessionSamples++;
            Interlocked.Increment(ref samples);
            lastSample = Stopwatch.GetTimestamp();
            onSample(got);
        }

        try
        {
            // What is waiting is read first: the Beast takes no new command while an answer of its own is unread.
            long drainUntil = Stopwatch.GetTimestamp() + (long)(0.3 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < drainUntil && Read(50) is { Length: > 0 }) { }
            if (!Send(StartCommand(), "start"))
            {
                Log.P($"hid imu: the start command was not accepted by {c.Line}");
                return false;
            }
            Log.P($"hid imu: motion stream asked for on {c.Line}");
            long askedAt = Stopwatch.GetTimestamp();
            int asks = 1;
            bool announced = false;
            while (!stopped && !removed)
            {
                byte[]? report = Read(250);
                if (report is { Length: 0 }) removed = true;
                else if (report != null) Received(report);
                if (sessionSamples > 0 && !announced)
                {
                    announced = true;
                    string model = glasses.Family == GlassesFamily.Air ? AirImuProtocol.ModelName(glasses.Product) ?? glasses.Name : glasses.Name;
                    lock (gate) connectedVia = $"USB HID ({model})";
                    Log.P($"hid imu: samples arriving from {c.Line}");
                }
                double quietFor = Stopwatch.GetElapsedTime(sessionSamples > 0 ? lastSample : askedAt).TotalSeconds;
                if (quietFor < 2.0) continue;
                if (sessionSamples > 0)
                {
                    Log.P($"hid imu: no samples for 2 s ({sessionSamples} read, {(glasses.Family == GlassesFamily.Air ? airClock.Line : vitureClock.Line)}); starting over");
                    return true;
                }
                if (glasses.Family == GlassesFamily.Air && asks < 3)
                {
                    // The Deck asks an Air again when nothing comes.
                    asks++;
                    askedAt = Stopwatch.GetTimestamp();
                    Log.P($"hid imu: no reports for 2 s ({sessionOther} other reports); asking again ({asks})");
                    if (!Send(StartCommand(), "start")) return false;
                    continue;
                }
                Log.P($"hid imu: no samples from {c.Line} ({sessionOther} other reports)");
                return false;
            }
            if (removed) Log.P("hid imu: the glasses were unplugged");
            return sessionSamples > 0;
        }
        finally
        {
            // The stop command, then the device closes.
            if (!removed) Send(StopCommand(), "stop");
            CancelIo(device);
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(overlappedPtr);
            lock (gate) connectedVia = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll")]
    private static extern bool CancelIo(SafeFileHandle file);
}
