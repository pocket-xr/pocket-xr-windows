using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// PC VR, as the Pocket XR PC helper did it: Steam, SteamVR and ALVR's streamer on this PC; the phone trusted in ALVR,
/// the glasses' view and frame rate set, SteamVR started, and the phone told when it is ready.
/// </summary>
internal static class PcVr
{
    private static DateTime startedAt = DateTime.MinValue;
    private static readonly HashSet<string> readyFor = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    public static string? SteamExe
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("SteamExe") is string p && File.Exists(p) ? p : null;
        }
    }

    /// <summary>Every SteamVR driver folder named alvr_server in openvrpaths.vrpath.</summary>
    public static List<string> AlvrDirs()
    {
        var found = new List<string>();
        try
        {
            string paths = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            if (!File.Exists(paths)) return found;
            if ((JsonNode.Parse(File.ReadAllText(paths)) as JsonObject)?["external_drivers"] is not JsonArray drivers) return found;
            foreach (var d in drivers)
            {
                if (d is not JsonValue v || !v.TryGetValue(out string? dir)) continue;
                string manifest = Path.Combine(dir, "driver.vrdrivermanifest");
                if (File.Exists(manifest) && File.ReadAllText(manifest).Contains("alvr_server")) found.Add(dir);
            }
        }
        catch (Exception e) { Log.P($"PC VR: drivers: {e.Message}"); }
        return found;
    }

    /// <summary>ALVR's streamer folder: the SteamVR driver named alvr_server.</summary>
    public static string? AlvrDir => AlvrDirs().FirstOrDefault();

    public static bool SteamVrRunning => Process.GetProcessesByName("vrserver").Length > 0;

    /// <summary>off: SteamVR isn't running. starting: it is, but ALVR doesn't answer yet. ready: ALVR answers.</summary>
    public static string SteamVrState
    {
        get
        {
            if (!SteamVrRunning) return (DateTime.Now - startedAt).TotalSeconds < 30 ? "starting" : "off";
            return Api("GET", "/api/version", null) != null ? "ready" : "starting";
        }
    }

    /// <summary>ALVR's own web API on this PC, while SteamVR runs. Null if it doesn't answer.</summary>
    public static string? Api(string method, string path, string? body)
    {
        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:8082" + path);
            msg.Headers.Add("X-ALVR", "true");
            if (body != null) msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = Http.Send(msg);
            if (!resp.IsSuccessStatusCode) return null;
            using var r = new StreamReader(resp.Content.ReadAsStream());
            return r.ReadToEnd();
        }
        catch (Exception) { return null; }
    }

    private static JsonObject? O(JsonNode? n, string key) => n?[key] as JsonObject;

    /// <summary>
    /// What the glasses need from ALVR: the phone trusted, and the view sized by the phone at 60 frames a second.
    /// True if a setting changed that only applies after SteamVR restarts.
    /// </summary>
    public static bool Prepare(string? phoneAlvrName)
    {
        if (SteamVrRunning)
        {
            // ALVR's API takes changes but gives the session back empty (21.0.0-dev14): its file is up to date while it runs.
            string? dirNow = AlvrDir;
            string? sessionFile = dirNow == null ? null : Path.Combine(dirNow, "session.json");
            if (sessionFile == null || !File.Exists(sessionFile) || Api("GET", "/api/version", null) == null) return false;
            if (!string.IsNullOrEmpty(phoneAlvrName) && readyFor.Add(phoneAlvrName)) TrustOverApi(phoneAlvrName);
            var root = JsonNode.Parse(File.ReadAllText(sessionFile));
            var settings = O(root, "session_settings");
            var video = O(settings, "video");
            if (settings == null || video == null) return false;
            if (HandGesturesOn(settings))
            {
                Log.P("PC VR: ALVR's own hand gestures off over its API (Pocket XR sends the pinch)");
                Api("POST", "/api/session/values", new JsonArray(PathValue(new[] { "headset", "controllers", "content", "hand_tracking_interaction", "enabled" }, false)).ToJsonString());
            }
            if (VideoIsRight(video)) return false;
            Log.P("PC VR: setting the glasses' view and frame rate over ALVR's API");
            Api("POST", "/api/session/values", new JsonArray(
                PathValue(new[] { "video", "transcoding_view_resolution", "variant" }, "Scale"),
                PathValue(new[] { "video", "transcoding_view_resolution", "Scale" }, 1.0),
                PathValue(new[] { "video", "emulated_headset_view_resolution", "variant" }, "Scale"),
                PathValue(new[] { "video", "emulated_headset_view_resolution", "Scale" }, 1.0),
                PathValue(new[] { "video", "preferred_fps" }, 60.0)).ToJsonString());
            return true;
        }
        string? dir = AlvrDir;
        if (dir == null) return false;
        string file = Path.Combine(dir, "session.json");
        if (!File.Exists(file)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            var settings = O(root, "session_settings");
            var video = O(settings, "video");
            if (root == null || settings == null || video == null) return false;
            bool changed = false;
            if (HandGesturesOn(settings))
            {
                HandGestures(settings)!["enabled"] = false;
                changed = true;
            }
            if (!VideoIsRight(video))
            {
                foreach (string key in new[] { "transcoding_view_resolution", "emulated_headset_view_resolution" })
                {
                    if (O(video, key) is not JsonObject res) continue;
                    res["variant"] = "Scale";
                    res["Scale"] = 1.0;
                }
                video["preferred_fps"] = 60.0;
                changed = true;
            }
            if (!string.IsNullOrEmpty(phoneAlvrName))
            {
                var list = O(root, "client_connections") ?? new JsonObject();
                bool trusted = O(list, phoneAlvrName)?["trusted"] is JsonValue t && t.TryGetValue(out bool tb) && tb;
                if (!trusted)
                {
                    list[phoneAlvrName] = new JsonObject
                    {
                        ["display_name"] = "Pocket XR", ["current_ip"] = null, ["manual_ips"] = new JsonArray(),
                        ["trusted"] = true, ["connection_state"] = "Disconnected",
                    };
                    root["client_connections"] = list;
                    changed = true;
                }
            }
            if (changed)
            {
                File.Copy(file, file + ".before-pocketxr", true);
                File.WriteAllText(file, root.ToJsonString());
                Log.P("PC VR: trusted the phone and set the glasses' view in ALVR's settings file");
            }
        }
        catch (Exception e) { Log.P($"PC VR: ALVR settings: {e.Message}"); }
        return false;
    }

    /// <summary>ALVR's own hand gestures, off: Pocket XR sends the tracked hands' pinch as the trigger itself.</summary>
    private static JsonObject? HandGestures(JsonObject settings) =>
        O(O(O(O(settings, "headset"), "controllers"), "content"), "hand_tracking_interaction");

    private static bool HandGesturesOn(JsonObject settings) =>
        HandGestures(settings)?["enabled"] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;

    private static bool VideoIsRight(JsonObject video)
    {
        foreach (string key in new[] { "transcoding_view_resolution", "emulated_headset_view_resolution" })
        {
            var res = O(video, key);
            if (res?["variant"]?.GetValue<string>() != "Scale" || Math.Abs(Num(res["Scale"]) - 1.0) > 1e-6) return false;
        }
        return Math.Abs(Num(video["preferred_fps"]) - 60.0) < 0.5;
    }

    private static JsonObject PathValue(string[] path, JsonNode value)
    {
        var segments = new JsonArray(new JsonObject { ["Name"] = "session_settings" });
        foreach (string p in path) segments.Add(new JsonObject { ["Name"] = p });
        return new JsonObject { ["path"] = segments, ["value"] = value };
    }

    private static void TrustOverApi(string host)
    {
        Api("POST", "/api/session/client-connections", new JsonArray(host,
            new JsonObject { ["AddIfMissing"] = new JsonObject { ["trusted"] = true, ["manual_ips"] = new JsonArray() } }).ToJsonString());
        Api("POST", "/api/session/client-connections", new JsonArray(host, "Trust").ToJsonString());
        Api("POST", "/api/session/client-connections", new JsonArray(host, new JsonObject { ["SetDisplayName"] = "Pocket XR" }).ToJsonString());
        Log.P($"PC VR: trusted {host} over ALVR's API");
    }

    /// <summary>ALVR's dashboard, which ALVR needs beside SteamVR to restart it when a setting needs that. Minimised.</summary>
    public static void StartAlvrDashboard()
    {
        if (Process.GetProcessesByName("ALVR Dashboard").Length > 0) return;
        string? dir = AlvrDir;
        if (dir == null) return;
        string exe = Path.Combine(dir, "ALVR Dashboard.exe");
        if (!File.Exists(exe)) return;
        Log.P("PC VR: starting ALVR's dashboard");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir, WindowStyle = ProcessWindowStyle.Minimized });
    }

    /// <summary>SteamVR blocks a driver after it crashed a few times ("blocked by a previous safe mode event"); with
    /// SteamVR closed, ALVR's block is lifted.</summary>
    private static void UnblockAlvr()
    {
        try
        {
            string? steam = SteamExe == null ? null : Path.GetDirectoryName(SteamExe);
            if (steam == null) return;
            string file = Path.Combine(steam, "config", "steamvr.vrsettings");
            if (!File.Exists(file)) return;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || O(root, "driver_alvr_server") is not JsonObject d) return;
            if (!d.Remove("blocked_by_safe_mode")) return;
            if (d.Count == 0) root.Remove("driver_alvr_server");
            File.Copy(file, file + ".before-pocketxr", true);
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log.P("PC VR: SteamVR had blocked ALVR (safe mode): unblocked");
        }
        catch (Exception e) { Log.P($"PC VR: unblock: {e.Message}"); }
    }

    public static void StartSteamVr()
    {
        if (SteamVrRunning || (DateTime.Now - startedAt).TotalSeconds < 30) return;
        startedAt = DateTime.Now;
        string? steamDir = SteamExe == null ? null : Path.GetDirectoryName(SteamExe);
        if (steamDir != null) AlvrStreamer.UseOurs(steamDir);
        UnblockAlvr();
        StartAlvrDashboard();
        Log.P("PC VR: starting SteamVR for the phone");
        Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true });
    }

    /// <summary>ALVR's dashboard restarts SteamVR when a setting needs it; it only has to be running.</summary>
    public static void RestartSteamVr()
    {
        Log.P("PC VR: ALVR's view changed: its dashboard restarts SteamVR");
        startedAt = DateTime.Now;
        StartAlvrDashboard();
        Api("POST", "/api/steamvr/restart", "null");
    }
}

/// <summary>
/// ALVR's streamer for Windows, the version Pocket XR's built-in client speaks (ALVR only connects to its own version),
/// in %LOCALAPPDATA%\PocketXR\ALVR where the PC helper installed it, registered with SteamVR in place of another ALVR.
/// </summary>
internal static class AlvrStreamer
{
    public const string Version = "21.0.0-dev14";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketXR", "ALVR");

    public static bool Installed => File.Exists(Path.Combine(Dir, "driver.vrdrivermanifest"));

    /// <summary>A streamer that came beside the app (alvr_streamer_windows), copied in; its own settings kept.</summary>
    public static void InstallFrom(string source)
    {
        try
        {
            string session = Path.Combine(Dir, "session.json");
            string? keep = File.Exists(session) ? File.ReadAllText(session) : null;
            CopyTree(source, Dir);
            if (keep != null) File.WriteAllText(session, keep);
            Log.P($"PC VR: ALVR streamer {Version} installed to {Dir}");
        }
        catch (Exception e) { Log.P($"PC VR: ALVR streamer install: {e.Message}"); }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>The ALVR in [dir] is the version Pocket XR speaks: ours, or one whose settings say so.</summary>
    public static bool Matches(string? dir)
    {
        if (dir == null) return false;
        if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))?["server_version"] is JsonValue v &&
                v.TryGetValue(out string? s) && s.StartsWith(Version, StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    /// <summary>With SteamVR closed: ours registered with SteamVR in place of another version's. True when it changed.</summary>
    public static bool UseOurs(string steamDir)
    {
        string? registered = PcVr.AlvrDir;
        if (Matches(registered) || !Installed) return false;
        string reg = Path.Combine(steamDir, "steamapps", "common", "SteamVR", "bin", "win64", "vrpathreg.exe");
        if (!File.Exists(reg)) { Log.P("PC VR: SteamVR's vrpathreg not found"); return false; }
        foreach (var other in PcVr.AlvrDirs()) Run(reg, $"removedriver \"{other}\"");
        Run(reg, $"adddriver \"{Dir}\"");
        Log.P($"PC VR: ALVR {(registered == null ? "was not set up" : $"in {registered} is another version")}; registered Pocket XR's {Version}");
        return true;
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(15000);
    }
}

/// <summary>
/// The PC's own sound device, back after PC screens. Apollo switches the PC to Steam Streaming Speakers while it streams
/// and switches back after, but a second Apollo started meanwhile remembers Steam Streaming Speakers as the PC's own
/// (Mike's PC, 19 Nov 2026). Every 2 s: the default device that is not Steam Streaming Speakers is remembered; once Steam
/// Streaming Speakers is the default with no PC screen streaming for 4 s, after one was, the remembered one is set back.
/// </summary>
internal sealed class SoundKeeper : IDisposable
{
    private static readonly string SavedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketXR", "sound-device.txt");
    private volatile bool running = true;

    public SoundKeeper() => new Thread(Run) { IsBackground = true, Name = "Sound-keeper" }.Start();

    public void Dispose() => running = false;

    private void Run()
    {
        string? saved = null;
        try { if (File.Exists(SavedFile)) saved = File.ReadAllText(SavedFile).Trim(); } catch (IOException) { }
        bool streamed = false;
        int quiet = 0;
        while (running)
        {
            try
            {
                string? now = DefaultDevice();
                bool streaming = Streaming();
                if (streaming) streamed = true;
                if (now != null && !IsStreamingSpeakers(now))
                {
                    if (now != saved)
                    {
                        saved = now;
                        try { Directory.CreateDirectory(Path.GetDirectoryName(SavedFile)!); File.WriteAllText(SavedFile, saved); } catch (IOException) { }
                    }
                    if (!streaming) streamed = false;
                    quiet = 0;
                }
                else if (now != null && !streaming && streamed && saved != null)
                {
                    if (++quiet >= 2)
                    {
                        SetDefault(saved);
                        Log.P($"sound: PC screens ended with Steam Streaming Speakers chosen; the PC's own device is back ({saved})");
                        streamed = false;
                        quiet = 0;
                    }
                }
                else quiet = 0;
            }
            catch (Exception e) { Log.P($"sound: {e.Message}"); }
            Thread.Sleep(2000);
        }
    }

    private static bool IsStreamingSpeakers(string id)
    {
        int dot = id.LastIndexOf('.');
        string guid = dot >= 0 ? id[(dot + 1)..] : id;
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid + @"\Properties");
        return k?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") is string adapter && adapter.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A PC screen streams: a SudoMaker virtual display (Apollo's, or this app's host's) is on the desktop.</summary>
    private static bool Streaming()
    {
        var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
        {
            if ((d.StateFlags & 1) != 0 && d.DeviceString?.Contains("SudoMaker", StringComparison.OrdinalIgnoreCase) == true) return true;
            d.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return false;
    }

    private static string? DefaultDevice()
    {
        // Made by its class id: inside the app, NAudio's own class for the same id came back and the cast failed.
        var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            if (e.GetDefaultAudioEndpoint(0, 0, out IMMDevice dev) != 0 || dev == null) return null;
            dev.GetId(out string id);
            Marshal.ReleaseComObject(dev);
            return id;
        }
        finally { Marshal.ReleaseComObject(e); }
    }

    private static void SetDefault(string id)
    {
        var p = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
        try { for (int role = 0; role < 3; role++) p.SetDefaultEndpoint(id, role); }
        finally { Marshal.ReleaseComObject(p); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    // Windows' own (undocumented, stable since Windows 7) way to choose the default device, as the Sound settings do.

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }
}
