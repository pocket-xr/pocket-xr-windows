using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// What the Pocket XR PC helper did, inside the app (the helper was its own program with its own tray icon,
/// pocket-xr-pcscreen/pc-helper/PocketXrPcHelper.cs; merged 20 Nov 2026). The same words on the same port, so phones
/// already released need nothing new:
///   UDP 47610  "POCKETXR-FIND 1"  ->  "POCKETXR-HERE 1 47610 &lt;PC name&gt;"
///   TCP 47610  one JSON line in, one JSON line out:
///     {"op":"pair","phone"}                       the PC's user is asked; {"ok":true,"token","pc"} or {"ok":false,"why"}
///     {"op":"apollo-pin","token","pin","name","port","virtual"}  the phone's PIN, given to this app's own host when
///                                                 [port] is its port, else typed into Apollo's web page
///     {"op":"screens-setup","token","phone"}      the PC's user is asked, then the Apollo PC screens setup runs
///     {"op":"screen-prepare","token",...}         {"ok":true}: this app's host makes each stream's display itself
///     {"op":"status"|"start","token","alvr"}      PC VR: Steam, SteamVR and ALVR's streamer; start gets them ready
///     {"op":"idr","token"}                        asks ALVR for a fresh full frame
/// Tokens stay in %APPDATA%\PocketXR\pc-helper.json and Apollo's saved login in apollo-login.json, where the helper kept
/// them, so phones paired with the helper stay paired.
/// </summary>
internal sealed class PhoneLink : IDisposable
{
    public const int Port = 47610;
    private readonly Func<string, string, bool> ask;
    private readonly HostServer? host;
    private UdpClient? udp;
    private TcpListener? tcp;
    private volatile bool running;
    private readonly object askGate = new();

    /// <param name="ask">Asks the PC's user a yes or no question (title, text) on the app's thread; false after a minute.</param>
    public PhoneLink(Func<string, string, bool> ask, HostServer? host)
    {
        this.ask = ask;
        this.host = host;
    }

    public void Start()
    {
        running = true;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            new Thread(Discovery) { IsBackground = true, Name = "Phone-find" }.Start();
            tcp = new TcpListener(IPAddress.Any, Port);
            tcp.Start();
            new Thread(Requests) { IsBackground = true, Name = "Phone-requests" }.Start();
            Log.P($"phone link: listening on {Port}");
        }
        catch (SocketException e)
        {
            Log.P($"phone link: port {Port} is taken ({e.SocketErrorCode}): another Pocket XR PC helper running?");
        }
    }

    public void Dispose()
    {
        running = false;
        try { udp?.Close(); } catch (Exception) { }
        try { tcp?.Stop(); } catch (Exception) { }
    }

    private void Discovery()
    {
        while (running && udp != null)
        {
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref from);
                if (Encoding.ASCII.GetString(data).StartsWith("POCKETXR-FIND", StringComparison.Ordinal))
                {
                    byte[] reply = Encoding.UTF8.GetBytes($"POCKETXR-HERE 1 {Port} {Environment.MachineName}");
                    udp.Send(reply, reply.Length, from);
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (!running) return;
                Thread.Sleep(500);
            }
        }
    }

    private void Requests()
    {
        while (running && tcp != null)
        {
            TcpClient c;
            try { c = tcp.AcceptTcpClient(); }
            catch (Exception) { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(_ => Serve(c));
        }
    }

    private void Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = 3000;
                var stream = c.GetStream();
                var reader = new StreamReader(stream);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                JsonObject request = JsonNode.Parse(reader.ReadLine() ?? "{}") as JsonObject ?? new JsonObject();
                writer.WriteLine(Answer(request).ToJsonString());
            }
            catch (Exception e) { Log.P($"phone link: {e.Message}"); }
        }
    }

    private static string? Text(JsonObject r, string key) => r[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int Number(JsonObject r, string key, int otherwise)
    {
        if (r[key] is not JsonValue v) return otherwise;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (int)d;
        return v.TryGetValue(out string? s) && int.TryParse(s, out int t) ? t : otherwise;
    }

    private static JsonObject No(string why) => new() { ["ok"] = false, ["why"] = why };
    private static JsonObject Yes() => new() { ["ok"] = true };

    public JsonObject Answer(JsonObject request)
    {
        string? op = Text(request, "op");
        if (op == "pair") return Pair(Text(request, "phone") ?? "A phone");
        if (!PhoneTokens.Known(Text(request, "token"))) return No("not paired");
        switch (op)
        {
            case "apollo-pin":
            {
                int port = Number(request, "port", 47989);
                string name = Text(request, "name") ?? "Pocket XR";
                bool wantsVirtual = request["virtual"] is JsonValue vv && vv.TryGetValue(out bool b) && b;
                if (host != null && port == host.HttpPort) return HostPin(Text(request, "pin"), name);
                return ApolloWeb.Pin(Text(request, "pin"), name, port, wantsVirtual);
            }
            case "screens-setup":
                return ScreensSetup(Text(request, "phone") ?? "A phone");
            case "screen-prepare":
                // The Mac helper sizes its virtual screen here; this app's host makes each stream's display at the
                // stream's size itself, and Apollo does the same.
                return Yes();
            case "idr":
                PcVr.Api("POST", "/api/insert-idr", "null");
                return Yes();
        }
        bool steam = PcVr.SteamExe != null;
        bool alvr = AlvrStreamer.Installed || AlvrStreamer.Matches(PcVr.AlvrDir);
        if ((op == "start" || op == "status") && steam && alvr && PcVr.SteamVrRunning) PcVr.StartAlvrDashboard();
        if (op == "start" && steam && alvr)
        {
            if (!PcVr.SteamVrRunning) AlvrStreamer.UseOurs(Path.GetDirectoryName(PcVr.SteamExe)!);
            if (PcVr.Prepare(Text(request, "alvr"))) PcVr.RestartSteamVr();
            PcVr.StartSteamVr();
        }
        else if (op == "status" && steam && alvr && PcVr.SteamVrRunning)
        {
            PcVr.Prepare(Text(request, "alvr"));
        }
        return new JsonObject
        {
            ["ok"] = true, ["pc"] = Environment.MachineName, ["steam"] = steam, ["alvr"] = alvr, ["steamvr"] = PcVr.SteamVrState,
        };
    }

    /// <summary>
    /// The phone's PIN for this app's own host: older records of the same phone go first (so it does not end up with
    /// two), then the PIN is given once the host waits for it (the phone may still be on its way to that step).
    /// </summary>
    private JsonObject HostPin(string? pin, string name)
    {
        if (host == null) return No("this PC's own host is off");
        if (string.IsNullOrEmpty(pin)) return No("no PIN");
        foreach (var c in host.Clients.All())
            if (ApolloWeb.SameDevice(c.Name, name) && host.Clients.Remove(c.UniqueId))
                Log.P($"phone link: older record \"{c.Name}\" removed before {name} pairs again");
        for (int i = 0; i < 20; i++)
        {
            if (host.SubmitPin(pin, name))
            {
                Log.P($"phone link: PIN given to this PC's host for {name}");
                return Yes();
            }
            Thread.Sleep(500);
        }
        return No("the host was not waiting for a PIN");
    }

    private JsonObject ScreensSetup(string phone)
    {
        lock (askGate)
        {
            if (!ask("Pocket XR", $"{phone} asks to set up PC screens on this PC with Apollo: more screens from this PC in your glasses, and optionally use away from home.\n\nSet it up now? Windows asks for permission next."))
                return No("not now");
            ApolloSetup.Run();
            return Yes();
        }
    }

    private JsonObject Pair(string phone)
    {
        lock (askGate)
        {
            if (!ask("Pocket XR", $"{phone} wants to use this PC with Pocket XR (PC screens and PC VR).\n\nAllow it?"))
            {
                Log.P($"phone link: pairing not allowed for {phone}");
                return No("not allowed on the PC");
            }
            string token = PhoneTokens.Add(phone);
            Log.P($"phone link: paired {phone}");
            return new JsonObject { ["ok"] = true, ["token"] = token, ["pc"] = Environment.MachineName };
        }
    }
}

/// <summary>The phones allowed on this PC: tokens in %APPDATA%\PocketXR\pc-helper.json, where the PC helper kept them.</summary>
internal static class PhoneTokens
{
    private static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketXR", "pc-helper.json");
    private static readonly object Gate = new();

    private static JsonArray Load()
    {
        try
        {
            if (!File.Exists(File_)) return new JsonArray();
            return (JsonNode.Parse(File.ReadAllText(File_)) as JsonObject)?["phones"] is JsonArray a ? (JsonArray)a.DeepClone() : new JsonArray();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new JsonArray(); }
    }

    public static bool Known(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (Gate) return Load().Any(p => p is JsonObject o && o["token"]?.GetValue<string>() == token);
    }

    public static int Count { get { lock (Gate) return Load().Count; } }

    public static string Add(string name)
    {
        lock (Gate)
        {
            var list = Load();
            string token = Guid.NewGuid().ToString("N");
            list.Add(new JsonObject { ["name"] = name, ["token"] = token });
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, new JsonObject { ["phones"] = list }.ToJsonString());
            return token;
        }
    }

    public static void Forget() { lock (Gate) if (File.Exists(File_)) File.Delete(File_); }
}

/// <summary>
/// PC screens with Apollo: its web API on this PC, signed in with the login the PC screens setup saved, to type a
/// phone's pairing PIN and give it the permissions a PC screen needs (as the PC helper's Apollo.Pin).
/// </summary>
internal static class ApolloWeb
{
    private static readonly string LoginFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketXR", "apollo-login.json");
    /// <summary>Launch, view, list, all input and clipboard: what a PC screen needs.</summary>
    private const long PcScreenPerms = 0x07031F00;

    /// <summary>A record of the device named [name]: that name, or Apollo's numbered copy of it ("MIKE (2)").</summary>
    public static bool SameDevice(string? recordName, string name)
    {
        if (recordName == null) return false;
        if (recordName == name) return true;
        return recordName.StartsWith(name + " (", StringComparison.Ordinal) && recordName.EndsWith(')') &&
            recordName.Substring(name.Length + 2, recordName.Length - name.Length - 3).All(char.IsDigit) &&
            recordName.Length > name.Length + 3;
    }

    /// <summary>The saved login: the user name, and the password protected by Windows for this user (PowerShell's
    /// ConvertFrom-SecureString: DPAPI, hex).</summary>
    private static bool Login(out string user, out string password)
    {
        user = password = "";
        try
        {
            if (!File.Exists(LoginFile)) return false;
            var d = JsonNode.Parse(File.ReadAllText(LoginFile)) as JsonObject;
            user = d?["username"]?.GetValue<string>() ?? "";
            string hex = d?["password"]?.GetValue<string>() ?? "";
            password = Encoding.Unicode.GetString(Dpapi.Unprotect(Convert.FromHexString(hex)));
            return user.Length > 0;
        }
        catch (Exception e) { Log.P($"apollo: login file: {e.Message}"); return false; }
    }

    private static HttpClient Client(CookieContainer cookies) => new(new HttpClientHandler
    {
        CookieContainer = cookies,
        // Apollo's web page has its own self-signed certificate; trusted for this PC's own address only.
        ServerCertificateCustomValidationCallback = (req, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || (req.RequestUri?.IsLoopback ?? false),
    }) { Timeout = TimeSpan.FromSeconds(5) };

    private static JsonNode? Call(HttpClient http, string method, string url, JsonNode? body)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = http.Send(msg);
        resp.EnsureSuccessStatusCode();
        using var r = new StreamReader(resp.Content.ReadAsStream());
        string text = r.ReadToEnd();
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    private static JsonObject No(string why) { Log.P($"apollo: {why}"); return new JsonObject { ["ok"] = false, ["why"] = why }; }

    private static IEnumerable<JsonObject> Clients(JsonNode? list) =>
        (list?["named_certs"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    /// <summary>Types [pin] into the Apollo on HTTP port [port] (its web page is port + 1), then sets the new client's permissions.</summary>
    public static JsonObject Pin(string? pin, string name, int port, bool virtualDisplay)
    {
        if (string.IsNullOrEmpty(pin)) return No("no PIN");
        if (!Login(out string user, out string password)) return No("no Apollo login saved: run Set up PC screens with Apollo");
        string web = $"https://localhost:{port + 1}";
        var cookies = new CookieContainer();
        using var http = Client(cookies);
        try { Call(http, "POST", web + "/api/login", new JsonObject { ["username"] = user, ["password"] = password }); }
        catch (Exception e) { return No($"Apollo sign-in failed: {e.Message}"); }
        // A device pairing again: its old records go first, or Apollo names the new one "MIKE (2)" and the permissions
        // below go to the old record (20 Nov 2026).
        try
        {
            foreach (var d in Clients(Call(http, "GET", web + "/api/clients/list", null)))
            {
                string? n = d["name"]?.GetValue<string>();
                if (!SameDevice(n, name) || d["uuid"] == null) continue;
                Call(http, "POST", web + "/api/clients/unpair", new JsonObject { ["uuid"] = d["uuid"]!.DeepClone() });
                Log.P($"apollo: old record \"{n}\" removed before pairing {name} again");
            }
        }
        catch (Exception e) { Log.P($"apollo: old records: {e.Message}"); }
        bool typed = false;
        for (int i = 0; i < 10 && !typed; i++)
        {
            try
            {
                var answer = Call(http, "POST", web + "/api/pin", new JsonObject { ["pin"] = pin, ["name"] = name });
                typed = answer?["status"] is JsonValue s && s.TryGetValue(out bool ok) && ok;
            }
            catch (Exception e) { Log.P($"apollo: pin: {e.Message}"); }
            if (!typed) Thread.Sleep(1000);
        }
        if (!typed) return No("Apollo did not take the PIN");
        Log.P($"apollo: PIN typed for {name} on port {port}");
        for (int i = 0; i < 10; i++)
        {
            try
            {
                JsonObject? mine = Clients(Call(http, "GET", web + "/api/clients/list", null)).LastOrDefault(d => SameDevice(d["name"]?.GetValue<string>(), name));
                if (mine != null)
                {
                    long perm = mine["perm"] is JsonValue pv ? (pv.TryGetValue(out long l) ? l : pv.TryGetValue(out string? ps) && long.TryParse(ps, out long lp) ? lp : 0) : 0;
                    bool vdOn = mine["always_use_virtual_display"] is JsonValue vd && vd.TryGetValue(out bool vdb) && vdb;
                    if ((perm & PcScreenPerms) != PcScreenPerms || (virtualDisplay && !vdOn))
                    {
                        var update = new JsonObject();
                        foreach (string k in new[] { "uuid", "name", "display_mode", "do", "undo", "enable_legacy_ordering", "allow_client_commands", "always_use_virtual_display" })
                            if (mine[k] != null) update[k] = mine[k]!.DeepClone();
                        update["perm"] = perm | PcScreenPerms;
                        if (virtualDisplay) update["always_use_virtual_display"] = true;
                        Call(http, "POST", web + "/api/clients/update", update);
                        Log.P($"apollo: permissions for {name} set to 0x{perm | PcScreenPerms:X}{(virtualDisplay ? ", always a virtual display" : "")}");
                    }
                    return new JsonObject { ["ok"] = true };
                }
            }
            catch (Exception e) { Log.P($"apollo: clients: {e.Message}"); }
            Thread.Sleep(1000);
        }
        return new JsonObject { ["ok"] = true, ["why"] = "paired; permissions not checked" };
    }
}

/// <summary>Windows' DPAPI for this user, as PowerShell's ConvertFrom-SecureString uses it.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    public static byte[] Unprotect(byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var output = new Blob();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output))
                throw new InvalidOperationException($"Windows could not unprotect it ({Marshal.GetLastWin32Error()})");
            try
            {
                var plain = new byte[output.Size];
                Marshal.Copy(output.Data, plain, 0, output.Size);
                return plain;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}

/// <summary>PC screens with Apollo: the setup script built into the app, run with admin rights (Windows asks).</summary>
internal static class ApolloSetup
{
    public static void Run()
    {
        try
        {
            string script = Path.Combine(Path.GetTempPath(), "PocketXR-PC-Screens.ps1");
            using (var res = typeof(ApolloSetup).Assembly.GetManifestResourceStream("PocketXR.PcScreens.ps1")!)
            using (var file = File.Create(script)) res.CopyTo(file);
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = true, Verb = "runas" });
            Log.P("PC screens with Apollo: setup started");
        }
        catch (Exception e) { Log.P($"PC screens with Apollo: setup: {e.Message}"); }
    }
}

/// <summary>
/// The Pocket XR PC helper as its own program, from before it was part of the app: stopped, and kept from starting
/// with Windows, when the app takes its jobs over. Its files and its saved phones are left where they are (the app
/// reads the same phones file).
/// </summary>
internal static class OldHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Pocket XR PC helper";

    public static bool Present => StartsWithWindows || Process.GetProcessesByName("PocketXrPcHelper").Length > 0;

    /// <summary>The helper is in Windows' start list (so the app should start with Windows in its place).</summary>
    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) != null;
        }
    }

    /// <summary>Stops a running helper and takes it out of Windows' start list. What was done, for the log.</summary>
    public static string? MoveOver()
    {
        var done = new List<string>();
        foreach (var p in Process.GetProcessesByName("PocketXrPcHelper"))
        {
            try { p.Kill(); p.WaitForExit(3000); done.Add("stopped it"); }
            catch (Exception e) { Log.P($"old PC helper: could not stop it: {e.Message}"); }
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(RunName) != null) { k.DeleteValue(RunName); done.Add("it no longer starts with Windows"); }
        }
        catch (Exception e) { Log.P($"old PC helper: start list: {e.Message}"); }
        return done.Count > 0 ? string.Join(", ", done) : null;
    }
}
