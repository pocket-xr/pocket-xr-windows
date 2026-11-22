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
/// Which HID device: the Air's IMU is USB interface 3 (2 on the Air 2 Ultra, 1 on the XBX A01 and A01+). On Windows each USB interface of the
/// glasses is its own HID device, and its path says which interface it is ("&amp;mi_03"), so the IMU's own interface is
/// found by that and the glasses' other HID interfaces are left alone. VITURE: the Beast's interface 5 first, then
/// each other HID device of the glasses in turn, as the Deck does. Every candidate seen is logged, so a tester's log
/// shows what the PC listed.
///
/// What is written to the glasses: only the Air's stream command (0x19, start or stop) and VITURE's raw stream
/// command (0x0301, start or stop), exactly as AirImuProtocol and VitureImuProtocol build them; and on the XBX A01 and
/// A01+, only if interface 1 stays silent, the MCU's start-up handshake and heartbeat on interface 0 (AirImuProtocol's
/// MCU part, from Pocket XR for Steam Deck 0.6.1). Nothing else.
///
/// The XBX: interface 1 alone first; if it sends no samples for about 6 s, the MCU on interface 0 is woken and kept
/// awake (0x1A every 100 ms) and interface 1 is asked again, then interface 2. Whichever streams is used and logged;
/// the heartbeat stops when tracking stops. The first reports from each interface are logged in hex.
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
    /// <summary>The XBX's MCU, kept awake while it is needed.</summary>
    private McuLink? mcu;
    /// <summary>What this try is, for the log ("interface 1, with the MCU heartbeat").</summary>
    private string tryNote = "";

    /// <summary>Samples read since Start().</summary>
    public int Samples => Volatile.Read(ref samples);

    private volatile string note = "starting";
    /// <summary>What the reader is doing now, for the glasses report: "trying interface 1, with the MCU heartbeat".</summary>
    public string Note => note;

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
                note = found.Count == 0 ? "no HID device of the glasses listed by Windows" : "none of the glasses' HID devices is the motion sensor";
                Wait(2.0);
                continue;
            }
            bool streamed = false;
            bool air = glasses.Family == GlassesFamily.Air && found.Any(c => c.Interface != null);
            if (air)
            {
                streamed = TryAir(found);
            }
            else
            {
                foreach (Candidate c in order)
                {
                    if (stopped) break;
                    tryNote = c.Interface is int n ? $"interface {n}" : "";
                    if (Session(c))
                    {
                        streamed = true;
                        break;
                    }
                }
            }
            if (!streamed && !stopped) note = $"no interface sent samples ({found.Count} HID devices listed); trying again";
            if (!streamed && !stopped) Log.P($"hid imu: no interface sent samples; trying again in {(air && AirImuProtocol.IsXbx(glasses.Product) ? 10 : 3)} s");
            Wait(streamed ? 1.0 : air && AirImuProtocol.IsXbx(glasses.Product) ? 10.0 : 3.0);
        }
        StopMcu();
        Log.P("hid imu: stopped");
    }

    /// <summary>
    /// The Air family's tries (AirImuProtocol.Tries): the IMU's own interface; on the XBX, if it stays silent, again
    /// with the MCU woken on interface 0, then interface 2. True if one streamed (until it stopped).
    /// </summary>
    private bool TryAir(List<Candidate> found)
    {
        var byNumber = found.Where(c => c.Interface != null).GroupBy(c => c.Interface!.Value).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in byNumber.Values) Log.P($"hid imu: interface {c.Interface} is {c.Path}");
        bool streamed = false;
        foreach (var (number, heartbeat) in AirImuProtocol.Tries(glasses.Product, byNumber.Keys))
        {
            if (stopped) break;
            if (heartbeat && mcu == null)
            {
                mcu = McuLink.Start(byNumber[AirImuProtocol.McuInterface], () => stopped);
                // No MCU: asking interface 1 again would change nothing; other interfaces are still tried.
                if (mcu == null && number == AirImuProtocol.ImuInterface(glasses.Product)) continue;
            }
            tryNote = $"interface {number}{(mcu != null ? ", with the MCU heartbeat" : "")}";
            note = $"trying {tryNote}";
            if (Session(byNumber[number]))
            {
                streamed = true;
                break;
            }
            if (!stopped) Log.P($"hid imu: no samples on {tryNote}");
        }
        StopMcu();
        return streamed;
    }

    private void StopMcu()
    {
        mcu?.Stop();
        mcu = null;
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
            note = $"could not open {(c.Interface is int ni ? $"interface {ni}" : "the device")} (error {error})";
            Log.P($"hid imu: could not open {c.Line}: {(error is 5 or 32 ? "Windows did not allow it (another app, such as the glasses maker's own, may hold the device)" : $"error {error}")}");
            return false;
        }
        int inLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxInput) + 1, outLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxOutput) + 1;
        var airClock = new AirTimestamp();
        var vitureClock = new VitureClock();
        int sessionSamples = 0, sessionOther = 0, sessionLogged = 0;
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
            // The first reports in hex, so a tester's log shows what each interface sends.
            if (sessionLogged++ < 12) Log.P($"hid imu: {tryNote} read {AirImuProtocol.HexStart(bytes)}");
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
                    Log.P($"hid imu: samples arriving from {c.Line}{(tryNote.Length > 0 ? $" ({tryNote})" : "")}");
                    note = $"samples arriving{(tryNote.Length > 0 ? $" on {tryNote}" : "")}";
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

    /// <summary>
    /// The XBX's MCU on interface 0: its start-up handshake, then the heartbeat every 100 ms on a thread of its own, as
    /// wheaney's nrealAirLinuxDriver (MIT) and Pocket XR for Steam Deck 0.6.1 do it. Untested on real glasses.
    /// </summary>
    private sealed class McuLink
    {
        private readonly SafeFileHandle device;
        private readonly Candidate c;
        private readonly int inLength, outLength;
        private readonly ManualResetEvent done = new(false);
        private readonly IntPtr buffer, overlapped;
        private volatile bool running = true;
        private Thread? beat;
        private int logged;

        private McuLink(SafeFileHandle device, Candidate c)
        {
            this.device = device;
            this.c = c;
            inLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxInput) + 1;
            outLength = Math.Max(AirImuProtocol.PacketBytes, c.MaxOutput) + 1;
            buffer = Marshal.AllocHGlobal(inLength);
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        }

        private static ulong NowNs() => (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

        /// <summary>Opens the MCU, sends the handshake, and starts the heartbeat. Null if it did not answer.</summary>
        public static McuLink? Start(Candidate c, Func<bool> stopped)
        {
            SafeFileHandle device = CreateFileW(c.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (device.IsInvalid)
            {
                Log.P($"hid imu: MCU interface {c.Interface} not open: error {Marshal.GetLastWin32Error()}");
                device.Dispose();
                return null;
            }
            var link = new McuLink(device, c);
            try
            {
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
                while (Stopwatch.GetTimestamp() < until && link.Read(50) is { Length: > 0 }) { }
                foreach (var (id, data) in AirImuProtocol.McuBootstrap)
                {
                    if (stopped()) { link.Close(); return null; }
                    bool ok = link.Ask(AirImuProtocol.McuCommand(id, data));
                    Log.P($"hid imu: MCU command 0x{id:x2} on interface {c.Interface}: {(ok ? "answered" : "no answer")}");
                    if (!ok)
                    {
                        link.Close();
                        return null;
                    }
                }
                for (int i = 0; i < 2; i++) link.Ask(AirImuProtocol.McuHeartbeat(NowNs()));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.P($"hid imu: MCU on interface {c.Interface} failed: {e.Message}");
                link.Close();
                return null;
            }
            link.beat = new Thread(link.Beat) { Name = "Pocket XR XBX heartbeat", IsBackground = true };
            link.beat.Start();
            Log.P($"hid imu: MCU heartbeat running on interface {c.Interface}");
            return link;
        }

        public void Stop()
        {
            running = false;
            beat?.Join(1000);
            Close();
            Log.P("hid imu: MCU heartbeat stopped");
        }

        private void Beat()
        {
            while (running)
            {
                if (!Send(AirImuProtocol.McuHeartbeat(NowNs())))
                {
                    if (running) Log.P("hid imu: MCU heartbeat stopped: the write failed");
                    return;
                }
                // Answers and button reports are read so they do not pile up; the first few are logged.
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                while (running && Stopwatch.GetTimestamp() < until)
                {
                    int left = (int)Math.Max(1, (until - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                    if (Read(left) is { Length: 0 }) return; // unplugged
                }
            }
        }

        /// <summary>Sends a frame and waits up to 2 s for any MCU frame back (the library takes any as the answer).</summary>
        private bool Ask(byte[] frame)
        {
            if (!Send(frame)) return false;
            long until = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            while (Stopwatch.GetTimestamp() < until)
            {
                byte[]? r = Read(250);
                if (r is { Length: 0 }) return false;
                if (r != null && r[0] == AirImuProtocol.McuHead) return true;
            }
            return false;
        }

        private byte[]? Read(int ms)
        {
            done.Reset();
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            bool ok = ReadFile(device, buffer, (uint)inLength, out uint read, overlapped);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != 997) return Array.Empty<byte>();
                if (!done.WaitOne(ms))
                {
                    CancelIo(device);
                    done.WaitOne(200);
                    return null;
                }
                if (!GetOverlappedResult(device, overlapped, out read, false)) return Array.Empty<byte>();
            }
            if (read <= 1) return null;
            var report = new byte[read - 1];
            Marshal.Copy(buffer + 1, report, 0, report.Length);
            if (logged++ < 12) Log.P($"hid imu: MCU interface {c.Interface} read {AirImuProtocol.HexStart(report)}");
            return report;
        }

        private bool Send(byte[] frame)
        {
            var report = new byte[outLength];
            Array.Copy(frame, 0, report, 1, Math.Min(frame.Length, outLength - 1));
            using var sent = new ManualResetEvent(false);
            IntPtr o = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            try
            {
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = sent.SafeWaitHandle.DangerousGetHandle() }, o, false);
                bool ok = WriteFile(device, report, (uint)report.Length, out _, o);
                if (!ok && Marshal.GetLastWin32Error() == 997) ok = sent.WaitOne(1000) && GetOverlappedResult(device, o, out _, false);
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(o);
            }
        }

        private void Close()
        {
            try { CancelIo(device); } catch (ObjectDisposedException) { }
            done.WaitOne(200); // a pending read finishes before its buffer goes
            device.Dispose();
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(overlapped);
            done.Dispose();
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
