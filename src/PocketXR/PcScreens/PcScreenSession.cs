using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: one PC screen, run as its own process (<c>PocketXR.exe --pc-screen N</c>): Moonlight's core keeps one
/// stream per process. It finds the computer (or uses the one it had), pairs once with no PIN to type when that
/// computer has the Pocket XR PC helper, asks the host for a virtual display at the window's shape (Apollo:
/// virtualDisplay; a Mac's or Linux's helper: screen-prepare first), streams, and reconnects by itself after a drop.
/// Closing the window ends the session on the host, so its virtual display goes. As the phone's PcScreenActivity.
/// </summary>
internal sealed class PcScreenSession : Moonlight.IListener
{
    private readonly int slot;
    private readonly PcScreenStore store;
    private PcScreenWindow window = null!;
    private PcScreenVideo video = null!;
    private volatile bool closing;
    private volatile bool wantStream;
    private volatile bool streaming;
    private string? activeHost;
    private int activePort;
    private bool shapedToWindow;
    private int streamW, streamH;
    private long lostAt;
    private int attempt;
    private readonly object connectGate = new();
    private Timer? reshapeTimer;
    private long startedAt;

    private PcScreenSession(int slot)
    {
        this.slot = slot;
        store = new PcScreenStore(slot);
    }

    /// <summary>
    /// PocketXR --pc-screen N [--host ADDRESS[:PORT]] [--at X Y W H] [--fps 60|120]
    /// --at: a borderless window over that rectangle (a floating screen); else an ordinary 1280 x 720 window.
    /// </summary>
    public static int Run(string[] args)
    {
        int slot = Math.Clamp((args.Length > 1 && int.TryParse(args[1], out int n) ? n : 1) - 1, 0, PcScreenLogic.Count - 1);
        SetProcessDpiAwarenessContext(-4); // per-monitor v2: the window's pixels are the screen's
        var s = new PcScreenSession(slot);
        PcScreenStats.Tag = $"pc screen {slot + 1}";
        Log.Shared.UseFile($"pc-screen-{slot + 1}");
        string? host = Arg(args, "--host");
        if (host != null && PcScreenLogic.ParseAddress(host) is { } parsed)
        {
            string typed = PcScreenLogic.Typed(parsed.Host, parsed.Port);
            if (typed != s.store.D.Address)
            {
                s.store.D.Address = typed;
                s.store.Unpair();
            }
        }
        if (Arg(args, "--fps") is string f) { s.store.D.Fps = f; }
        int at = Array.IndexOf(args, "--at");
        bool borderless = at >= 0 && at + 4 < args.Length;
        int x = 120 + 40 * slot, y = 120 + 40 * slot, w = 1280, h = 720;
        if (borderless)
        {
            x = int.Parse(args[at + 1]); y = int.Parse(args[at + 2]); w = int.Parse(args[at + 3]); h = int.Parse(args[at + 4]);
        }
        Log.P($"pc screen {slot + 1}: opening, {(borderless ? $"over {x},{y} {w}x{h}" : "as a window")}, host {s.store.D.Address}");
        s.window = PcScreenWindow.Create($"PC screen {slot + 1}", x, y, w, h, borderless);
        s.video = new PcScreenVideo(s.window.Hwnd);
        var (cw, ch) = s.window.ClientSize;
        s.video.Resize(cw, ch);
        s.window.Resized = s.OnResized;
        s.window.Closing = () => s.closing = true;
        s.window.Say("Starting…");
        s.wantStream = true;
        new Thread(s.Connect) { IsBackground = true, Name = "PcScreen-connect" }.Start();
        PcScreenWindow.Run();
        s.End();
        return 0;
    }

    /// <summary>PocketXR --pc-screens-find: the computers found, their hosts, this PC's decoders and identity. Opens nothing.</summary>
    public static int FindReport()
    {
        var id = PcIdentity.Shared;
        Console.WriteLine($"This PC: client id {id.UniqueId}, certificate {id.PemCert.Length} bytes");
        try { Console.WriteLine($"Moonlight's core: loaded, launch query \"{Moonlight.LaunchQuery}\""); }
        catch (DllNotFoundException) { Console.WriteLine("Moonlight's core: moonlight-pxr.dll is missing"); }
        int formats = PcScreenVideo.DecodableFormats();
        Console.WriteLine($"Decoders: {(formats & Moonlight.VideoH264) != 0} H.264, {(formats & Moonlight.VideoH265) != 0} HEVC, {(formats & Moonlight.VideoAv1) != 0} AV1");
        var sw = Stopwatch.StartNew();
        var helpers = PcHelperLink.FindAll();
        Console.WriteLine($"PC helpers answering ({sw.ElapsedMilliseconds} ms): {helpers.Count}");
        foreach (var pc in helpers)
        {
            Console.WriteLine($"  {pc.Name} at {pc.Address}:{pc.Port}, this PC allowed: {PcHelperLink.Knows(pc)}");
            var ports = pc.SunshinePorts.Count > 0 ? pc.SunshinePorts : PcScreenLogic.InstancePorts();
            foreach (int p in ports)
            {
                try
                {
                    using var h = new NvHttp(pc.Address.ToString(), p, 0, null);
                    string info = h.ServerInfo(1500);
                    Console.WriteLine($"    port {p}: {PcScreenLogic.Xml(info, "hostname")}, appversion {PcScreenLogic.Xml(info, "appversion")}, " +
                        $"virtual display {PcScreenLogic.ApolloVirtual(info)}, codecs 0x{PcScreenLogic.Xml(info, "ServerCodecModeSupport")}, " +
                        $"state {PcScreenLogic.Xml(info, "state")}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"    port {p}: no answer ({e.GetType().Name})");
                }
            }
        }
        for (int s = 0; s < PcScreenLogic.Count; s++)
        {
            var st = new PcScreenStore(s);
            Console.WriteLine($"PC screen {s + 1}: {(st.D.Address.Length == 0 ? "no computer yet" : $"{st.D.Address} ({st.D.PcName}), paired {st.Paired}")}");
        }
        return 0;
    }

    /// <summary>PocketXR --pc-screen-tls N: HTTPS to PC screen N's host with each TLS version, to see which works.</summary>
    public static int TlsReport(string[] args)
    {
        int slot = Math.Clamp((args.Length > 1 && int.TryParse(args[1], out int n) ? n : 1) - 1, 0, PcScreenLogic.Count - 1);
        var store = new PcScreenStore(slot);
        var (host, port) = PcScreenLogic.ParseAddress(store.D.Address) ?? ("", 0);
        foreach (var proto in new[] { System.Security.Authentication.SslProtocols.Tls12, System.Security.Authentication.SslProtocols.Tls13,
            System.Security.Authentication.SslProtocols.None })
        {
            for (int i = 0; i < 3; i++)
            {
                NvHttp.Protocols = proto;
                using var h = new NvHttp(host, port, store.D.HttpsPort, store.ServerCert);
                try
                {
                    string info = h.ServerInfo(3000);
                    Console.WriteLine($"{proto} #{i}: PairStatus {PcScreenLogic.Xml(info, "PairStatus")}, Permission {PcScreenLogic.Xml(info, "Permission")}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{proto} #{i}: {e.InnerException?.InnerException?.Message ?? e.InnerException?.Message ?? e.Message}");
                }
            }
        }
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnResized(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        video.Resize(w, h);
        // A window much changed in shape gets a stream of its new shape, once the resizing has stopped.
        if (shapedToWindow && streaming)
        {
            reshapeTimer?.Dispose();
            reshapeTimer = new Timer(_ =>
            {
                var (cw, ch) = window.ClientSize;
                if (streaming && PcScreenLogic.ReshapeNeeded(cw, ch, streamW, streamH))
                {
                    Log.P($"pc screen {slot + 1}: window now {cw}x{ch}, stream {streamW}x{streamH}: starting again at the new shape");
                    Restart(fresh: true);
                }
            }, null, 800, Timeout.Infinite);
        }
    }

    // ---- connecting ----

    private void Connect()
    {
        lock (connectGate)
        {
            try
            {
                if (closing || !wantStream) return;
                // This PC never streams itself (a computer saved before this rule, or typed by hand).
                if (store.D.Address.Length > 0 && PcScreenLogic.ParseAddress(store.D.Address) is { } saved && ThisPc.Is(saved.Host))
                {
                    Log.P($"pc screen {slot + 1}: {store.D.Address} is this PC: forgotten, looking for another computer");
                    store.D.Address = "";
                    store.D.PcName = null;
                    store.Unpair();
                }
                if (store.D.Address.Length == 0 && !Find()) return;
                if (!store.Paired && !Pair()) return;
                Stream(fresh: lostAt == 0);
            }
            catch (Exception e)
            {
                Log.P($"pc screen {slot + 1}: connect: {e}");
                Lost(Trouble(e));
            }
        }
    }

    /// <summary>A computer for this PC screen: the Pocket XR PC helpers that answer, and their hosts' instances. The
    /// first instance no other PC screen uses, PC screen 1 the main one.</summary>
    private bool Find()
    {
        window.Say("Looking for other computers…");
        var taken = new HashSet<string>();
        for (int other = 0; other < PcScreenLogic.Count; other++)
            if (other != slot) taken.Add(new PcScreenStore(other).D.Address);
        foreach (var pc in PcHelperLink.FindAll())
        {
            if (ThisPc.Is(pc.Address.ToString())) continue; // never this PC itself
            // Up to six instances' ports (PC screens 1 to 6), asked at once: one that is not there costs its 1.5 s
            // wait only once, not once per port.
            var ports = pc.SunshinePorts.Count > 0 ? pc.SunshinePorts : PcScreenLogic.InstancePorts();
            var asks = ports.Select(p => System.Threading.Tasks.Task.Run(() => ServerName(pc.Address.ToString(), p))).ToArray();
            var answering = ports.Where((p, i) => asks[i].Result != null).ToList();
            int? port = PcScreenLogic.InstanceFor(slot, answering, p => !taken.Contains(PcScreenLogic.Typed(pc.Address.ToString(), p)));
            if (port is int chosen)
            {
                store.D.Address = PcScreenLogic.Typed(pc.Address.ToString(), chosen);
                store.D.PcName = pc.Name;
                store.Save();
                Log.P($"pc screen {slot + 1}: found {pc.Name} at {store.D.Address} through its PC helper");
                return true;
            }
        }
        window.Say("No other computer found. On the computer to show, turn on Let my phone stream this PC in Pocket XR for Windows, " +
            "or install Apollo or Sunshine with the Pocket XR PC helper.");
        return false;
    }

    private static string? ServerName(string host, int port)
    {
        try
        {
            using var h = new NvHttp(host, port, 0, null);
            return PcScreenLogic.Xml(h.ServerInfo(1500), "hostname");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private (string Host, int Port) Address => PcScreenLogic.ParseAddress(store.D.Address) ?? ("", PcScreenLogic.DefaultPort);

    /// <summary>Pairs with the host: the Pocket XR PC helper types the PIN there; without one, the user does.</summary>
    private bool Pair()
    {
        var (host, port) = Address;
        string pin = NvHttp.NewPin();
        using var http = new NvHttp(host, port, 0, null);
        string info = http.ServerInfo();
        string name = PcScreenLogic.Xml(info, "hostname") ?? host;
        using var stop = new CancellationTokenSource();
        PcHelperLink.Pc? helper = IPAddress.TryParse(host, out var ip) ? PcHelperLink.FindAt(ip) : null;
        if (helper != null)
        {
            new Thread(() =>
            {
                if (!PcHelperLink.Knows(helper))
                {
                    window.Say($"Pairing with {name}. On {helper.Name}, say Yes to let this PC use it (once).");
                    if (!PcHelperLink.Pair(helper))
                    {
                        window.Say($"{helper.Name} did not allow this PC. Type {pin} in Apollo's or Sunshine's PIN page there instead.");
                        return;
                    }
                }
                window.Say($"Pairing with {name}…");
                string? why = PcHelperLink.ApolloPin(helper, pin, port);
                if (why != null)
                {
                    Log.P($"pc screen {slot + 1}: the PC helper could not type the PIN: {why}");
                    window.Say($"Type {pin} in Apollo's or Sunshine's PIN page on {name} (https://{host}:{port + 1}/pin). The PC helper said: {why}");
                }
            }) { IsBackground = true, Name = "PcScreen-helper" }.Start();
        }
        else
        {
            window.Say($"Type {pin} in Apollo's or Sunshine's PIN page on {name}: https://{host}:{port + 1}/pin");
        }
        var state = http.Pair(info, pin, stop.Token);
        Log.P($"pc screen {slot + 1}: pair with {name} at {store.D.Address}: {state}");
        if (state != NvHttp.PairState.Paired || http.ServerCert == null)
        {
            window.Say(state == NvHttp.PairState.PinWrong ? $"The PIN was wrong. Close this PC screen and open it again to try again."
                : $"Pairing with {name} did not work ({state}).");
            return false;
        }
        string? mac = null;
        try
        {
            using var secure = new NvHttp(host, port, NvHttp.HttpsPortOf(info), http.ServerCert);
            mac = PcScreenLogic.Xml(secure.ServerInfo(), "mac");
        }
        catch (Exception) { }
        store.SavePairing(store.D.Address, NvHttp.HttpsPortOf(info), http.ServerCert, PcScreenLogic.PcName(name), mac);
        return true;
    }

    private void Stream(bool fresh)
    {
        var (host, port) = Address;
        window.Say($"Connecting to {store.D.PcName ?? host}…");
        using var http = new NvHttp(host, port, store.D.HttpsPort, store.ServerCert);
        string info = http.ServerInfo();
        if (PcScreenLogic.Xml(info, "PairStatus") != "1")
        {
            Log.P($"pc screen {slot + 1}: the host does not know this PC any more: pairing again");
            store.Unpair();
            if (!Pair()) return;
            Stream(fresh);
            return;
        }
        int https = NvHttp.HttpsPortOf(info);
        if (https != store.D.HttpsPort) { store.D.HttpsPort = https; store.Save(); }
        if (long.TryParse(PcScreenLogic.Xml(info, "Permission"), out long perm) && (perm & PcScreenLogic.PermsNeeded) != PcScreenLogic.PermsNeeded)
        {
            window.Say($"Apollo on {store.D.PcName ?? host} does not let this PC launch, or use the mouse and keyboard. " +
                $"In Apollo's web page, Clients, give \"{PcHelperLink.ClientName}\" those permissions.");
            wantStream = false;
            return;
        }
        bool apolloVirtual = PcScreenLogic.ApolloVirtual(info);
        PcHelperLink.Pc? helper = !apolloVirtual && IPAddress.TryParse(host, out var ip) ? PcHelperLink.FindAt(ip) : null;
        var says = helper != null ? PcHelperLink.HostInfo(helper) : null;
        window.RelativeMouse = says?.Os == "linux";
        bool vd = apolloVirtual || says?.Virtual == true;
        var (cw, ch) = window.ClientSize;
        var (w, h) = vd ? PcScreenLogic.ForWindow(cw, ch) : (PcScreenLogic.StreamW, PcScreenLogic.StreamH);
        int fps = store.D.Fps == "120" ? 120 : 60;
        int kbps = PcScreenLogic.BitrateKbps(w, h, fps, store.D.Quality);
        var app = PcScreenLogic.DesktopApp(PcScreenLogic.Apps(http.AppList())) ?? throw new InvalidOperationException("the host lists no apps");
        int running = NvHttp.CurrentGame(info);
        string verb = "launch";
        if (running != 0)
        {
            if (fresh)
            {
                try { http.Quit(); Log.P($"pc screen {slot + 1}: a session was running (app {running}): ended it for a fresh one"); }
                catch (Exception e) { Log.P($"pc screen {slot + 1}: ending the running session: {e.Message}"); }
            }
            else if (running == app.Id)
            {
                verb = "resume";
            }
        }
        if (says?.Virtual == true && helper != null)
        {
            var sw = Stopwatch.StartNew();
            bool ready = PcHelperLink.ScreenPrepare(helper, port, w, h, fps);
            Log.P($"pc screen {slot + 1}: screen-prepare {w}x{h}@{fps} on port {port}: {(ready ? "ready" : "not ready")} after {sw.ElapsedMilliseconds} ms");
        }
        int formats = PcScreenVideo.DecodableFormats() & (Moonlight.VideoH264 | Moonlight.VideoH265);
        if (formats == 0) formats = Moonlight.VideoH264;
        byte[] riKey = RandomNumberGenerator.GetBytes(16);
        int riKeyId = RandomNumberGenerator.GetInt32(int.MaxValue);
        Log.P($"pc screen {slot + 1}: {verb} {app.Name} on {host}:{port} at {w}x{h}@{fps}, {kbps / 1000} Mbps, window {cw}x{ch}, " +
            $"virtual display {vd} ({(apolloVirtual ? "Apollo" : says?.Virtual == true ? "helper" : "none")}), formats 0x{formats:x}, " +
            $"mouse {(window.RelativeMouse ? "relative" : "positions")}");
        string? rtsp = http.Launch(verb, app.Id, w, h, fps, riKey, riKeyId, virtualDisplay: true, localAudio: false,
            surroundAudioInfo: (0x3 << 16) | 2, Moonlight.LaunchQuery);
        if (closing || !wantStream) return;
        shapedToWindow = vd;
        streamW = w;
        streamH = h;
        window.StreamW = w;
        window.StreamH = h;
        int.TryParse(PcScreenLogic.Xml(info, "ServerCodecModeSupport"), out int codecSupport);
        activeHost = host;
        activePort = port;
        startedAt = Stopwatch.GetTimestamp();
        int ret = Moonlight.Start(this, host, PcScreenLogic.Xml(info, "appversion") ?? "7.1.431.-1", PcScreenLogic.Xml(info, "GfeVersion"),
            rtsp, codecSupport, w, h, fps, kbps, 1392, Moonlight.StreamAuto, formats, riKey, riKeyId);
        if (ret != 0)
        {
            Log.P($"pc screen {slot + 1}: the stream did not start ({ret})");
            Lost($"The stream did not start ({ret}).");
        }
    }

    private static string Trouble(Exception e) => e switch
    {
        HostException h when h.Code == 403 => "The computer refused this PC (403). In Apollo's web page, Clients, allow it to launch.",
        System.Net.Http.HttpRequestException => "The computer did not answer. Is it on, with Apollo or Sunshine running?",
        OperationCanceledException or TimeoutException => "The computer did not answer in time.",
        _ => e.Message,
    };

    private void Lost(string why)
    {
        streaming = false;
        window.Streaming = false;
        video.ReleaseOutput();
        if (closing || !wantStream)
        {
            window.Say(why);
            return;
        }
        if (lostAt == 0) { lostAt = Stopwatch.GetTimestamp(); attempt = 0; }
        long lostForMs = (Stopwatch.GetTimestamp() - lostAt) * 1000 / Stopwatch.Frequency;
        int? wait = PcScreenLogic.ReconnectDelayMs(attempt++, lostForMs);
        if (wait == null)
        {
            window.Say(why + " Close this PC screen and open it again to try again.");
            return;
        }
        window.Say($"{why} Trying again in {wait / 1000} s…");
        new Timer(_ => new Thread(Connect) { IsBackground = true, Name = "PcScreen-connect" }.Start(), null, wait.Value, Timeout.Infinite);
    }

    /// <summary>The stream stopped and started again (a new shape): a fresh session for Apollo's new virtual display.</summary>
    private void Restart(bool fresh)
    {
        new Thread(() =>
        {
            Moonlight.PxrStop();
            streaming = false;
            window.Streaming = false;
            video.StopDecoder();
            video.ReleaseOutput();
            if (fresh) EndHostSession();
            lostAt = 0;
            Connect();
        }) { IsBackground = true, Name = "PcScreen-restart" }.Start();
    }

    private void EndHostSession()
    {
        if (activeHost == null || store.ServerCert == null) return;
        try
        {
            using var http = new NvHttp(activeHost, activePort, store.D.HttpsPort, store.ServerCert);
            http.Quit();
            Log.P($"pc screen {slot + 1}: ended the session on the host");
        }
        catch (Exception e)
        {
            Log.P($"pc screen {slot + 1}: ending the session: {e.Message}");
        }
    }

    private void End()
    {
        closing = true;
        wantStream = false;
        var stop = new Thread(() =>
        {
            Moonlight.PxrStop();
            EndHostSession();
        }) { IsBackground = true };
        stop.Start();
        stop.Join(6000);
        try { video.Dispose(); } catch (Exception) { }
        try { sound?.Dispose(); } catch (Exception) { }
        Log.P($"pc screen {slot + 1}: closed");
    }

    // ---- Moonlight.IListener ----

    public void Stage(int kind, string name, int error)
    {
        if (kind == 0) window.Say($"Connecting: {name}…");
        else if (kind == 2) Log.P($"pc screen {slot + 1}: stage {name} failed ({error})");
    }

    public void Started()
    {
        streaming = true;
        window.Streaming = true;
        lostAt = 0;
        attempt = 0;
        Log.P($"pc screen {slot + 1}: streaming, {(Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency} ms after launch");
        window.SetTitle($"PC screen {slot + 1}: {store.D.PcName ?? activeHost}");
    }

    public void Terminated(int error)
    {
        Log.P($"pc screen {slot + 1}: stream ended ({error})");
        new Thread(() =>
        {
            Moonlight.PxrStop();
            video.StopDecoder();
            Lost(error == 0 ? "The computer ended the stream." : $"The stream was lost ({error}).");
        }) { IsBackground = true, Name = "PcScreen-lost" }.Start();
    }

    public void Status(int status) => Log.P($"pc screen {slot + 1}: connection {(status == 0 ? "good" : "poor")}");

    public int VideoSetup(int format, int width, int height, int fps) => video.Setup(format, width, height, fps);

    public int VideoFrame(ReadOnlySpan<byte> data, bool idr, int frameNumber, ulong receiveUs, ulong enqueueUs, int hostLatencyTenthsMs) =>
        video.Frame(data, idr, receiveUs, enqueueUs);

    private PcScreenSound? sound;

    public int AudioInit(int sampleRate, int channels, int streams, int coupled, int samplesPerFrame, ReadOnlySpan<byte> mapping)
    {
        sound?.Dispose();
        bool play = slot == 0 && store.D.Sound && Environment.GetEnvironmentVariable("PXR_PCSCREEN_MUTE") != "1";
        sound = new PcScreenSound(sampleRate, channels, samplesPerFrame, play);
        Log.P($"pc screen {slot + 1}: sound {sampleRate} Hz, {channels} channels, {samplesPerFrame} samples a packet, {(play ? "played here" : "not played")}");
        return 0;
    }

    public void AudioSample(ReadOnlySpan<byte> opus)
    {
        var s = sound;
        if (s == null) return;
        s.Sample(opus);
        if (s.Decoded == 200 || s.Errors == 1) Log.P($"pc screen {slot + 1}: sound: {s.Decoded} packets decoded, {s.Errors} bad");
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
}
