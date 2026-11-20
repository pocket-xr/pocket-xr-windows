using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// Pocket XR for Windows as a streaming host that Moonlight's protocol reaches (as Sunshine and Apollo are), so the
/// phone can show this PC's screens with no Apollo installed. Ported in part from Sunshine and Apollo (GPLv3).
///
/// Ports, from the base (HTTP) port P, as Sunshine lays them out: HTTPS P-5, video P+9, control P+10, audio P+11,
/// RTSP P+21. The default base is 50989, clear of an Apollo's 47989, 48989 and 49989.
///
/// Milestone 1 (20 Nov 2026): pairing with a PIN given to the host, then one display's picture as H.264. No sound,
/// no input passed on yet, no virtual display of the phone's shape (the display is scaled to the stream's size).
/// </summary>
internal sealed class HostServer : IDisposable
{
    public const int DesktopAppId = 1;
    public const int DefaultBasePort = 50989;

    public readonly int HttpPort, HttpsPort, VideoPort, ControlPort, AudioPort, RtspPort;
    public string Name { get; }
    public HostIdentity Identity { get; }
    public HostClients Clients { get; } = new();
    /// <summary>Whether the host makes a virtual display at the phone's shape for each stream (SudoVDA, the driver Apollo
    /// installs, is on this PC). Without it, the main display is streamed, scaled to the stream's size.</summary>
    public bool MakesScreens { get; }
    /// <summary>The codecs this PC's graphics card encodes beside H.264.</summary>
    public bool HasHevc { get; }
    public bool HasAv1 { get; }
    /// <summary>serverinfo's ServerCodecModeSupport: H.264, plus HEVC Main and AV1 Main 8-bit when encoded here.</summary>
    public int CodecSupport => 0x1 | (HasHevc ? 0x100 : 0) | (HasAv1 ? 0x10000 : 0);
    private HostMdns? mdns;
    private readonly string? displayName;
    private readonly HostWeb web;
    private readonly HostRtsp rtsp;
    private readonly object gate = new();
    private Session? session;

    private sealed class Session
    {
        public int Width, Height, Fps;
        public byte[] Key = Array.Empty<byte>();
        public int KeyId;
        public string PingPayload = "";
        public uint ConnectData;
        public int PacketSize = 1392, BitrateKbps = 20000, MinFec = 2;
        public HostEncoder? Encoder;
        public int Codec;
        public HostAudio? Audio;
        public Platform.VirtualDisplays? Screen;
        public bool VirtualWanted;
        public int AudioMs = 5;
        public bool AudioEncrypted = true;
        public HostVideo? Video;
        public HostControl? Control;
        public long Started;
        public Timer? Stats;
        /// <summary>Held while the display, sound and encoder are made after PLAY, so a stop waits for them.</summary>
        public readonly object Making = new();
        public volatile bool Stopped;
    }

    public HostServer(int basePort, string? displayName, string? name = null)
    {
        HttpPort = basePort;
        HttpsPort = basePort - 5;
        VideoPort = basePort + 9;
        ControlPort = basePort + 10;
        AudioPort = basePort + 11;
        RtspPort = basePort + 21;
        this.displayName = displayName;
        Name = name ?? Environment.MachineName + " Pocket XR";
        Identity = HostIdentity.Load();
        web = new HostWeb(this);
        rtsp = new HostRtsp(this);
        localApi = new HostLocalApi(this);
        HasHevc = HostEncoder.Available(HostEncoder.CodecHevc) && Environment.GetEnvironmentVariable("PXR_HOST_CODEC") is not ("h264");
        HasAv1 = HostEncoder.Available(HostEncoder.CodecAv1) && Environment.GetEnvironmentVariable("PXR_HOST_CODEC") is not ("h264" or "hevc");
        MakesScreens = displayName == null && Platform.VirtualDisplays.Probe(out _) is { } d && d.Name.Contains("Sudo", StringComparison.OrdinalIgnoreCase);
    }

    private readonly HostLocalApi localApi;

    public void Start()
    {
        web.Start();
        rtsp.Start();
        localApi.Start();
        mdns = new HostMdns();
        new Thread(() => mdns.Register(Name, HttpPort)) { IsBackground = true, Name = "Host-mdns" }.Start();
        Log.P($"host: {Name} ready on {HttpPort} (id {Identity.UniqueId}, {Clients.Count} paired, " +
            $"{(MakesScreens ? "a virtual display per stream" : "the main display, scaled")}, H.264{(HasHevc ? ", HEVC" : "")}{(HasAv1 ? ", AV1" : "")})");
    }

    public bool SessionRunning { get { lock (gate) return session != null; } }

    // ---- PIN ----

    public static string PinFile => Path.Combine(HostIdentity.Folder, "pin.txt");

    private volatile string? givenPin;
    /// <summary>A phone waits at the PIN step of pairing.</summary>
    public volatile bool PinWanted;
    /// <summary>The name the PC helper gave with the PIN (the phone's own), for the paired list.</summary>
    public volatile string? PinName;

    /// <summary>The PIN, from the Pocket XR PC helper on this PC (it types it as into Apollo's web page).</summary>
    public bool SubmitPin(string pin, string? name)
    {
        if (!PinWanted || !Regex.IsMatch(pin, @"^\d{4}$")) return false;
        PinName = name;
        givenPin = pin;
        return true;
    }

    /// <summary>Waits for the PIN the phone shows: from the PC helper, or PocketXR.exe --host-pin 1234.</summary>
    public string? WaitForPin(TimeSpan wait)
    {
        givenPin = null;
        PinName = null;
        PinWanted = true;
        try { return WaitForPinInner(wait); }
        finally { PinWanted = false; }
    }

    private string? WaitForPinInner(TimeSpan wait)
    {
        try { File.Delete(PinFile); } catch (IOException) { }
        Log.P($"host: a phone wants to pair: give its PIN with PocketXR.exe --host-pin NNNN within {wait.TotalSeconds:F0} s");
        var end = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < end)
        {
            if (givenPin is string g) return g;
            try
            {
                if (File.Exists(PinFile))
                {
                    string pin = File.ReadAllText(PinFile).Trim();
                    File.Delete(PinFile);
                    if (Regex.IsMatch(pin, @"^\d{4}$")) return pin;
                }
            }
            catch (IOException) { }
            Thread.Sleep(250);
        }
        return null;
    }

    // ---- launch ----

    /// <summary>launch or resume: the stream's settings kept for RTSP; the RTSP URL to give the phone, or null.</summary>
    public string? Launch(Dictionary<string, string> q, IPEndPoint local, bool resume)
    {
        var m = Regex.Match(q.GetValueOrDefault("mode") ?? "", @"^(\d+)x(\d+)x(\d+)$");
        string? keyHex = q.GetValueOrDefault("rikey");
        if (!m.Success || keyHex == null || keyHex.Length != 32 || !int.TryParse(q.GetValueOrDefault("rikeyid"), out int keyId))
        {
            Log.P("host: launch refused: no mode, rikey or rikeyid");
            return null;
        }
        lock (gate)
        {
            if (session != null && !resume) Stop(session, "a new launch");
            session ??= new Session();
            session.Width = int.Parse(m.Groups[1].Value);
            session.Height = int.Parse(m.Groups[2].Value);
            session.Fps = Math.Max(1, int.Parse(m.Groups[3].Value));
            session.Key = Convert.FromHexString(keyHex);
            session.KeyId = keyId;
            session.PingPayload = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            session.ConnectData = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            session.VirtualWanted = q.GetValueOrDefault("virtualDisplay") == "1";
            Log.P($"host: {(resume ? "resume" : "launch")} {session.Width}x{session.Height}@{session.Fps}, virtual display asked " +
                $"{q.GetValueOrDefault("virtualDisplay") ?? "0"}");
        }
        string ip = local.Address.ToString();
        return $"rtsp://{ip}:{RtspPort}";
    }

    public void EndSession(string why)
    {
        lock (gate)
        {
            if (session == null) return;
            Stop(session, why);
            session = null;
        }
    }

    private void Stop(Session s, string why)
    {
        Log.P($"host: session ended ({why})");
        s.Stopped = true;
        lock (s.Making) StopParts(s);
    }

    private static void StopParts(Session s)
    {
        s.Stats?.Dispose();
        s.Control?.Terminate();
        Thread.Sleep(100);
        s.Encoder?.Dispose();
        s.Audio?.Dispose();
        s.Video?.Dispose();
        s.Control?.Dispose();
        s.Screen?.Dispose();
        s.Screen = null;
        s.Audio = null;
        s.Encoder = null;
        s.Video = null;
        s.Control = null;
    }

    // ---- RTSP ----

    public (List<(string, string)> Options, string Payload, int Status) Rtsp(string verb, string target, string body)
    {
        var options = new List<(string, string)>();
        Session? s;
        lock (gate) s = session;
        Log.P($"host: RTSP {verb} {target}");
        switch (verb)
        {
            case "OPTIONS":
                return (options, "", 200);
            case "DESCRIBE":
                // H.264 only (no "sprop-parameter-sets=AAAAAU", no AV1 line), control encryption V2, no video encryption.
                // Control encryption V2 and audio encryption offered (1 | 4), as Sunshine; video unencrypted. HEVC and AV1
                // offered when this PC encodes them (the client takes AV1, then HEVC, then H.264).
                string sdp = (HasHevc ? "sprop-parameter-sets=AAAAAU\n" : "") + (HasAv1 ? "a=rtpmap:98 AV1/90000\n" : "") +
                    "a=x-ss-general.featureFlags:0\n" +
                    "a=x-ss-general.encryptionSupported:5\n" +
                    "a=x-ss-general.encryptionRequested:1\n";
                return (options, sdp, 200);
            case "SETUP":
                if (s == null) return (options, "", 404);
                var t = Regex.Match(target, @"streamid=(\w+)");
                string type = t.Success ? t.Groups[1].Value : "";
                int port = type switch { "audio" => AudioPort, "video" => VideoPort, "control" => ControlPort, _ => 0 };
                if (port == 0) return (options, "", 404);
                options.Add(("Session", "DEADBEEFCAFE;timeout = 90"));
                options.Add(("Transport", $"server_port={port}"));
                if (type == "control") options.Add(("X-SS-Connect-Data", s.ConnectData.ToString()));
                else options.Add(("X-SS-Ping-Payload", s.PingPayload));
                return (options, "", 200);
            case "ANNOUNCE":
                if (s == null) return (options, "", 404);
                ReadAnnounce(s, body);
                return (options, "", 200);
            case "PLAY":
                if (s == null) return (options, "", 404);
                Begin(s);
                return (options, "", 200);
        }
        return (options, "", 404);
    }

    private static int? Sdp(string body, string key)
    {
        var m = Regex.Match(body, Regex.Escape(key) + @":\s*(-?\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : null;
    }

    private static void ReadAnnounce(Session s, string body)
    {
        s.Width = Sdp(body, "x-nv-video[0].clientViewportWd") ?? s.Width;
        s.Height = Sdp(body, "x-nv-video[0].clientViewportHt") ?? s.Height;
        s.Fps = Sdp(body, "x-nv-video[0].maxFPS") ?? s.Fps;
        s.PacketSize = Sdp(body, "x-nv-video[0].packetSize") ?? s.PacketSize;
        s.BitrateKbps = Sdp(body, "x-nv-vqos[0].bw.maximumBitrateKbps") ?? s.BitrateKbps;
        s.MinFec = Sdp(body, "x-nv-vqos[0].fec.minRequiredFecPackets") ?? s.MinFec;
        int enc = Sdp(body, "x-ss-general.encryptionEnabled") ?? 0;
        s.AudioMs = Sdp(body, "x-nv-aqos.packetDuration") ?? 5;
        // Audio is encrypted when the client says so either way (Sunshine's rtsp.cpp: nv feature flag 0x20).
        s.AudioEncrypted = (enc & 4) != 0 || ((Sdp(body, "x-nv-general.featureFlags") ?? 0) & 0x20) != 0;
        int format = Sdp(body, "x-nv-vqos[0].bitStreamFormat") ?? 0;
        s.Codec = format is 1 or 2 ? format : 0;
        Log.P($"host: ANNOUNCE {s.Width}x{s.Height}@{s.Fps}, {s.BitrateKbps / 1000} Mbps, packets {s.PacketSize}, " +
            $"FEC minimum {s.MinFec}, encryption {enc}, bitstream {format}");
    }

    /// <summary>
    /// PLAY: the video and control ports open now, and PLAY is answered at once; the display, sound and encoder are
    /// made afterwards (about half a second), so the phone connects its streams meanwhile. Answering after all of it
    /// held every start back by that time (20 Nov 2026).
    /// </summary>
    private void Begin(Session s)
    {
        if (s.Video != null) return;
        s.Started = Stopwatch.GetTimestamp();
        s.Video = new HostVideo(VideoPort, s.PacketSize, 20, s.MinFec);
        s.Control = new HostControl(ControlPort, s.Key);
        s.Control.Input = m => HostInput.Handle(m.Span);
        s.Control.KeyFrameWanted = () => s.Encoder?.RequestIdr();
        s.Video.FirstClient = () =>
        {
            s.Encoder?.RequestIdr(); // the encoder's first picture is a key frame anyway, if it is not made yet
            HostInput.Centre(); // the pointer starts on the streamed display
        };
        s.Control.Gone = () => new Thread(() => EndSession("the phone went")) { IsBackground = true }.Start();
        new Thread(() =>
        {
            try
            {
                lock (s.Making)
                {
                    if (s.Stopped) return;
                    Make(s);
                }
            }
            catch (Exception e)
            {
                Log.P($"host: the stream could not start: {e.Message}");
                new Thread(() => { lock (gate) if (session == s) EndSession("it could not start"); }) { IsBackground = true }.Start();
            }
        }) { IsBackground = true, Name = "Host-start" }.Start();
    }

    private void Make(Session s)
    {
        string? display = displayName;
        if (display == null && s.VirtualWanted && MakesScreens) display = MakeScreen(s);
        var video = s.Video!;
        long first = 0;
        s.Encoder = new HostEncoder(display, s.Width, s.Height, s.Fps, s.BitrateKbps, (frame, idr, taken) =>
        {
            int latency = (int)((Stopwatch.GetTimestamp() - taken) * 10_000 / Stopwatch.Frequency);
            if (!video.HasClient) return;
            if (first == 0)
            {
                first = Stopwatch.GetTimestamp();
                Log.P($"host: first frame sent ({frame.Length} bytes, key {idr}), {(first - s.Started) * 1000 / Stopwatch.Frequency} ms after PLAY");
            }
            video.Send(frame, idr, latency);
        }, s.Codec);
        s.Audio = new HostAudio(AudioPort, s.AudioMs, s.AudioEncrypted, s.Key, s.KeyId);
        s.Audio.Start();
        s.Encoder.Start();
        Log.P($"host: display, sound and encoder ready {(Stopwatch.GetTimestamp() - s.Started) * 1000 / Stopwatch.Frequency} ms after PLAY");
        long lastFrames = 0, lastBytes = 0, lastNeeds = 0, lastOuts = 0, lastKeys = 0, lastOutBytes = 0;
        double lastMs = 0;
        s.Stats = new Timer(_ =>
        {
            var v = s.Video;
            var e = s.Encoder;
            if (v == null || e == null) return;
            long f = v.FramesSent, b = v.BytesSent;
            long outs = e.Outputs - lastOuts;
            Log.P($"host: encoder {e.NeedInputs - lastNeeds} inputs asked, {outs} frames out ({e.Keys - lastKeys} key), " +
                $"{(outs > 0 ? (e.OutBytes - lastOutBytes) / outs / 1024 : 0)} KB each, submit-to-output {(outs > 0 ? (e.EncodeMsSum - lastMs) / outs : 0):F1} ms (max {e.EncodeMsMax:F1})");
            lastNeeds = e.NeedInputs; lastOuts = e.Outputs; lastKeys = e.Keys; lastOutBytes = e.OutBytes; lastMs = e.EncodeMsSum; e.EncodeMsMax = 0;
            Log.P($"host: {(f - lastFrames) / 10.0:F1} frames/s sent, {(b - lastBytes) * 8 / 10.0 / 1e6:F1} Mbps, " +
                $"{e.Captured} pictures captured, {HostInput.Sent} input events, {e.pointerShapes} pointer shapes, {s.Audio?.PacketsSent ?? 0} sound packets, " +
                $"control {(s.Control?.Connected == true ? "connected" : "not connected")}" +
                (e.Error.Length > 0 ? $", encoder: {e.Error}" : ""));
            lastFrames = f;
            lastBytes = b;
        }, null, 10_000, 10_000);
    }

    /// <summary>A virtual display of the stream's own size (the phone's window shape), made for this stream; its
    /// Windows name once it is on the desktop, or null (the main display is streamed then).</summary>
    private string? MakeScreen(Session s)
    {
        var sw = Stopwatch.StartNew();
        var made = Platform.VirtualDisplays.Make(1, s.Width, s.Height, Math.Max(30, s.Fps), out string why, idOffset: 0x40);
        if (made == null)
        {
            Log.P($"host: no virtual display: {why}");
            return null;
        }
        s.Screen = made;
        for (int i = 0; i < 50; i++)
        {
            var d = Platform.VirtualDisplays.Find(made.Screens[0], Platform.Displays.List());
            if (d != null)
            {
                var first = d;
                if (d.Width != s.Width || d.Height != s.Height)
                {
                    // Windows brought it back at the size it remembered for this display: set the stream's.
                    int r = Platform.Displays.SetMode(d.GdiName, s.Width, s.Height, Math.Max(30, s.Fps));
                    for (int j = 0; j < 30 && r == 0; j++)
                    {
                        var now = Platform.VirtualDisplays.Find(made.Screens[0], Platform.Displays.List());
                        if (now != null && now.Width == s.Width && now.Height == s.Height) { d = now; break; }
                        Thread.Sleep(100);
                    }
                    Log.P($"host: virtual display came at {first.Width}x{first.Height}: set to {s.Width}x{s.Height} ({(r == 0 ? "done" : $"refused, {r}")})");
                }
                Log.P($"host: virtual display {d.GdiName} {d.Width}x{d.Height} at {d.X},{d.Y}, ready after {sw.ElapsedMilliseconds} ms");
                return d.GdiName;
            }
            Thread.Sleep(100);
        }
        Log.P("host: the virtual display did not reach the desktop within 5 s");
        return null;
    }

    public void Dispose()
    {
        EndSession("the host stopped");
        mdns?.Dispose();
        web.Stop();
        rtsp.Stop();
        localApi.Stop();
    }

    /// <summary>
    /// PocketXR --host [--port 50989] [--display \\.\DISPLAY2] [--seconds N]: Pocket XR for Windows as a host, in the
    /// foreground, logging to %LOCALAPPDATA%\Pocket XR\host.log. A phone pairs by its PIN: PocketXR --host-pin 1234.
    /// </summary>
    public static int RunCommand(string[] args)
    {
        Log.Shared.UseFile("host");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.P($"host: unhandled: {e.ExceptionObject}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.P("host: the process is ending");
        SetProcessDpiAwarenessContext(-4); // per-monitor: desktop positions in real pixels, for the phone's mouse
        int port = Arg(args, "--port") is string p && int.TryParse(p, out int pp) ? pp : DefaultBasePort;
        double seconds = Arg(args, "--seconds") is string sec && double.TryParse(sec, out double ss) ? ss : double.PositiveInfinity;
        using var host = new HostServer(port, Arg(args, "--display"));
        host.Start();
        Console.Error.WriteLine($"Pocket XR host on port {port}. The phone adds this PC by its address and port {port}.");
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "PocketXR.host.quit");
        quit.WaitOne(double.IsInfinity(seconds) ? Timeout.Infinite : (int)(seconds * 1000));
        Log.P("host: stopping");
        return 0;
    }

    /// <summary>PocketXR --host-encoder-test [W H FPS KBPS] [--display NAME]: capture and encode alone for 10 s, no network.</summary>
    public static int EncoderTest(string[] args)
    {
        int w = args.Length > 1 && int.TryParse(args[1], out int a) ? a : 1920;
        int h = args.Length > 2 && int.TryParse(args[2], out int b) ? b : 1080;
        int fps = args.Length > 3 && int.TryParse(args[3], out int c) ? c : 60;
        int kbps = args.Length > 4 && int.TryParse(args[4], out int d) ? d : 32000;
        long frames = 0, bytes = 0;
        int codec = Arg(args, "--codec") switch { "hevc" => HostEncoder.CodecHevc, "av1" => HostEncoder.CodecAv1, _ => HostEncoder.CodecH264 };
        using var e = new HostEncoder(Arg(args, "--display"), w, h, fps, kbps, (f, k, t) => { frames++; bytes += f.Length; }, codec);
        e.Start();
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(2000);
            Console.WriteLine($"{e.EncoderName}: {e.Outputs} frames out, {e.NeedInputs} inputs asked, " +
                $"submit-to-output {(e.Outputs > 0 ? e.EncodeMsSum / e.Outputs : 0):F1} ms (max {e.EncodeMsMax:F1}), {e.Captured} captured, {e.pointerShapes} pointer shapes, {(e.Outputs > 0 ? e.OutBytes / e.Outputs / 1024 : 0)} KB a frame");
        }
        return 0;
    }

    /// <summary>PocketXR --host-clipboard-test N: the clipboard read and written back N times from worker threads, as
    /// the host's web requests do (its text is left as it was).</summary>
    public static int ClipboardTest(string[] args)
    {
        int n = args.Length > 1 && int.TryParse(args[1], out int k) ? k : 100;
        int ok = 0, failed = 0;
        var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
        for (int i = 0; i < n; i++)
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                string? t = HostClipboard.Get();
                if (t != null && HostClipboard.Set(t)) Interlocked.Increment(ref ok); else Interlocked.Increment(ref failed);
            }));
        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        Console.WriteLine($"clipboard: {ok} read and written back, {failed} failed");
        return 0;
    }

    /// <summary>PocketXR --host-pin NNNN: gives the running host the PIN the phone shows.</summary>
    public static int GivePin(string[] args)
    {
        string pin = args.Length > 1 ? args[1].Trim() : "";
        if (!Regex.IsMatch(pin, @"^\d{4}$")) { Console.Error.WriteLine("The PIN is four digits."); return 2; }
        Directory.CreateDirectory(HostIdentity.Folder);
        File.WriteAllText(PinFile, pin);
        return 0;
    }

    /// <summary>PocketXR --host-stop: stops a running --host.</summary>
    public static int StopCommand()
    {
        try { using var e = EventWaitHandle.OpenExisting("PocketXR.host.quit"); e.Set(); return 0; }
        catch (WaitHandleCannotBeOpenedException) { return 1; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
