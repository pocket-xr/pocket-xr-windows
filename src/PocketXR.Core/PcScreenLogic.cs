using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PocketXR.Core;

/// <summary>
/// PC screens: another computer's desktop streamed into a window here (Moonlight's protocol, from Apollo or Sunshine).
/// The rules worked out apart from Windows so they can be checked, as on the phone (PcScreenShape, PcScreenQuality,
/// PcScreenPick, PcScreenAddress in Pocket XR for Android).
/// </summary>
public static class PcScreenLogic
{
    /// <summary>Apollo's and Sunshine's usual HTTP port. A second Apollo instance has its own (48989, 49989).</summary>
    public const int DefaultPort = 47989;

    /// <summary>At most three PC screens, as on the phone: one host instance each.</summary>
    public const int Count = 3;

    /// <summary>The size streamed when the host makes no virtual display: the usual desktop, sent without stretching.</summary>
    public const int StreamW = 1920, StreamH = 1080;

    public const int MaxSide = 3840;
    public const long MaxArea = 3840L * 2160L;
    public const long MinArea = 640L * 360L;

    /// <summary>
    /// What a PC screen must be allowed on Apollo: launch (0x4000000), mouse (0x800) and keyboard (0x1000).
    /// </summary>
    public const long PermsNeeded = 0x4000000L | 0x800L | 0x1000L;

    /// <summary>
    /// Host and port from an address as typed: <c>192.168.1.20</c>, <c>192.168.1.20:48989</c>, <c>pc.local</c>,
    /// <c>[fe80::1]:48989</c> or a bare IPv6 address. Null for an empty or malformed one.
    /// </summary>
    public static (string Host, int Port)? ParseAddress(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.StartsWith('['))
        {
            int end = t.IndexOf(']');
            if (end < 2) return null;
            string host = t.Substring(1, end - 1);
            string rest = t[(end + 1)..];
            if (rest.Length == 0) return (host, DefaultPort);
            if (!rest.StartsWith(':')) return null;
            int? p6 = Port(rest[1..]);
            return p6 is int q6 ? (host, q6) : null;
        }
        if (t.Count(c => c == ':') > 1) return (t, DefaultPort);
        int colon = t.IndexOf(':');
        if (colon < 0) return (t, DefaultPort);
        string h = t[..colon];
        if (h.Length == 0) return null;
        int? p = Port(t[(colon + 1)..]);
        return p is int q ? (h, q) : null;
    }

    private static int? Port(string s) => int.TryParse(s, out int p) && p >= 1 && p <= 65535 ? p : null;

    /// <summary>The address as saved: the port only when it is not the usual one.</summary>
    public static string Typed(string host, int port)
    {
        string h = host.Contains(':') ? "[" + host + "]" : host;
        return port == DefaultPort ? h : h + ":" + port;
    }

    /// <summary>
    /// The stream's size for a window of <paramref name="w"/> x <paramref name="h"/> pixels when the host makes a virtual
    /// display for it: the window's own shape, a multiple of 8 each side, at most 3840 a side and 3840 x 2160 in all, at
    /// least 640 x 360 in area.
    /// </summary>
    public static (int W, int H) ForWindow(int w, int h)
    {
        if (w <= 0 || h <= 0) return (StreamW, StreamH);
        double s = 1.0;
        s = Math.Min(s, (double)MaxSide / Math.Max(w, h));
        s = Math.Min(s, Math.Sqrt(MaxArea / ((double)w * h)));
        if ((long)w * h < MinArea) s = Math.Sqrt(MinArea / ((double)w * h));
        int sw = Math.Max(8, (int)(w * s) / 8 * 8);
        int sh = Math.Max(8, (int)(h * s) / 8 * 8);
        return (sw, sh);
    }

    /// <summary>Whether a window has moved far enough from the stream's shape to start the stream again at its own.</summary>
    public static bool ReshapeNeeded(int w, int h, int streamW, int streamH)
    {
        if (w <= 0 || h <= 0 || streamW <= 0 || streamH <= 0) return false;
        var (nw, nh) = ForWindow(w, h);
        return Math.Abs(nw - streamW) > 16 || Math.Abs(nh - streamH) > 16;
    }

    /// <summary>
    /// Where a <paramref name="streamW"/> x <paramref name="streamH"/> picture sits in a window of
    /// <paramref name="w"/> x <paramref name="h"/>: as large as fits, its shape kept, centred.
    /// </summary>
    public static (int X, int Y, int W, int H) Fit(int w, int h, int streamW, int streamH)
    {
        if (w <= 0 || h <= 0 || streamW <= 0 || streamH <= 0) return (0, 0, Math.Max(w, 0), Math.Max(h, 0));
        double s = Math.Min((double)w / streamW, (double)h / streamH);
        int fw = Math.Max(1, (int)Math.Round(streamW * s));
        int fh = Math.Max(1, (int)Math.Round(streamH * s));
        return ((w - fw) / 2, (h - fh) / 2, fw, fh);
    }

    /// <summary>
    /// Bitrate in kbps: 40 Mbps at 1080p60, the phone's rate that Mike found sharp (18 Nov 2026), scaled with the
    /// pixels and with the frame rate to the power 0.6. "sharp" is 1.6 times, "saver" half. 8 to 150 Mbps.
    /// </summary>
    public static int BitrateKbps(int w, int h, int fps, string quality)
    {
        double pixels = (double)w * h / (1920.0 * 1080.0);
        double rate = Math.Pow(fps / 60.0, 0.6);
        double factor = quality switch { "sharp" => 1.6, "saver" => 0.5, _ => 1.0 };
        return (int)Math.Clamp(Math.Round(40_000.0 * pixels * rate * factor), 8_000, 150_000);
    }

    /// <summary>The wait before reconnect try <paramref name="attempt"/>: 1, 2, 4, 8 s, then every 10 s; null after 3 minutes lost.</summary>
    public static int? ReconnectDelayMs(int attempt, long lostForMs)
    {
        if (lostForMs >= 3 * 60_000L) return null;
        return attempt < 4 ? 1000 << attempt : 10_000;
    }

    /// <summary>The PC's name from an instance's mDNS or host name: "Mike(2)" and "MIKE screen 2" are both "Mike"'s.</summary>
    public static string PcName(string serviceName)
    {
        string n = Regex.Replace(serviceName.Trim(), @"\(\d+\)$", "");
        n = Regex.Replace(n, @"\s+screen\s+\d+$", "", RegexOptions.IgnoreCase).Trim();
        if (n.Contains('.') && n.Length > 0 && char.IsLetter(n[0])) n = n[..n.IndexOf('.')];
        return n.Length == 0 ? serviceName : n;
    }

    /// <summary>
    /// The instance PC screen <paramref name="slot"/> uses: PC screen 1 the main one (the usual port) first, PC screens 2
    /// and 3 the extra ones first, so the main stays PC screen 1's. Only ports <paramref name="free"/> allows.
    /// </summary>
    public static int? InstanceFor(int slot, IEnumerable<int> ports, Func<int, bool> free)
    {
        var list = ports.Distinct().ToList();
        var main = list.Where(p => p == DefaultPort);
        var extra = list.Where(p => p != DefaultPort).OrderBy(p => p);
        var order = slot == 0 ? main.Concat(extra) : extra.Concat(main);
        foreach (int p in order) if (free(p)) return p;
        return null;
    }

    /// <summary>The text of the first &lt;tag&gt; in a host's XML answer, or null.</summary>
    public static string? Xml(string xml, string tag)
    {
        var m = Regex.Match(xml, "<" + Regex.Escape(tag) + ">([^<]*)</" + Regex.Escape(tag) + ">");
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    /// <summary>The host's status_code (200 when it answered as asked) and status_message.</summary>
    public static (int Code, string Message) XmlStatus(string xml)
    {
        var c = Regex.Match(xml, "status_code=\"(-?\\d+)\"");
        var m = Regex.Match(xml, "status_message=\"([^\"]*)\"");
        return (c.Success && int.TryParse(c.Groups[1].Value, out int code) ? code : 0,
            m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "");
    }

    /// <summary>The apps a host lists (applist): id and name.</summary>
    public static List<(int Id, string Name)> Apps(string xml)
    {
        var apps = new List<(int, string)>();
        foreach (Match m in Regex.Matches(xml, "<App>(.*?)</App>", RegexOptions.Singleline))
        {
            string inner = m.Groups[1].Value;
            string? title = Xml(inner, "AppTitle");
            if (title != null && int.TryParse(Xml(inner, "ID"), out int id)) apps.Add((id, title));
        }
        return apps;
    }

    /// <summary>The app a PC screen launches: "Desktop" if listed, else the first.</summary>
    public static (int Id, string Name)? DesktopApp(List<(int Id, string Name)> apps)
    {
        foreach (var a in apps) if (string.Equals(a.Name, "Desktop", StringComparison.OrdinalIgnoreCase)) return a;
        return apps.Count > 0 ? apps[0] : null;
    }

    /// <summary>Whether a host will make a virtual display at the window's shape: Apollo with its driver ready.</summary>
    public static bool ApolloVirtual(string serverInfo) =>
        Xml(serverInfo, "VirtualDisplayCapable") == "true" && Xml(serverInfo, "VirtualDisplayDriverReady") != "false";

    /// <summary>The first number of the host's appversion ("7.1.431.-1" is 7): pairing hashes with SHA-256 from 7 on.</summary>
    public static int MajorVersion(string serverInfo)
    {
        string v = Xml(serverInfo, "appversion") ?? "";
        int dot = v.IndexOf('.');
        return int.TryParse(dot < 0 ? v : v[..dot], out int major) ? major : 0;
    }

    /// <summary>Upper-case hex, as Moonlight sends bytes.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>
    /// The Pocket XR PC helper's answer to "POCKETXR-FIND 1": "POCKETXR-HERE 1 &lt;port&gt; &lt;name&gt; [ports]".
    /// Its port, the computer's name, and the Sunshine ports the Linux helper lists (none from the others).
    /// </summary>
    public static (int Port, string Name, List<int> Ports)? HelperHere(string text)
    {
        var parts = text.Trim().Split(' ', 4);
        if (parts.Length < 4 || parts[0] != "POCKETXR-HERE") return null;
        int port = int.TryParse(parts[2], out int p) ? p : 47610;
        var m = Regex.Match(parts[3], @"\s+(\d{4,5}(?:,\d{4,5})*)$");
        var ports = m.Success ? m.Groups[1].Value.Split(',').Select(int.Parse).ToList() : new List<int>();
        string name = (m.Success ? parts[3][..m.Index] : parts[3]).Trim();
        return (port, name, ports);
    }
}
