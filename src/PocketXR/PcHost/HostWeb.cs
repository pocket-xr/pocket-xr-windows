using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// The host's web side, as Sunshine and Apollo answer it (GameStream's protocol, ported from Apollo's nvhttp.cpp,
/// GPLv3): plain HTTP on the base port and HTTPS (a client certificate required) five below it. serverinfo, the
/// five-step pairing, the app list (one app, "Desktop"), launch, resume and cancel. One request per connection.
/// </summary>
internal sealed class HostWeb
{
    private readonly HostServer host;
    private TcpListener? http, https;
    private volatile bool stopping;

    /// <summary>One pairing in progress per client id (Apollo's map_id_sess).</summary>
    private sealed class Pairing
    {
        public int Phase;
        public byte[] AesKey = Array.Empty<byte>();
        public byte[] ClientCertDer = Array.Empty<byte>();
        public string Name = "";
        public byte[] ServerSecret = Array.Empty<byte>();
        public byte[] ServerChallenge = Array.Empty<byte>();
        public byte[] ClientHash = Array.Empty<byte>();
    }

    private readonly ConcurrentDictionary<string, Pairing> pairings = new();

    public HostWeb(HostServer host) => this.host = host;

    public void Start()
    {
        http = new TcpListener(IPAddress.Any, host.HttpPort);
        https = new TcpListener(IPAddress.Any, host.HttpsPort);
        http.Start();
        https.Start();
        _ = Accept(http, secure: false);
        _ = Accept(https, secure: true);
        Log.P($"host: web on HTTP {host.HttpPort} and HTTPS {host.HttpsPort}");
    }

    public void Stop()
    {
        stopping = true;
        try { http?.Stop(); } catch (Exception) { }
        try { https?.Stop(); } catch (Exception) { }
    }

    private async Task Accept(TcpListener listener, bool secure)
    {
        while (!stopping)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c, secure));
        }
    }

    private async Task Serve(TcpClient c, bool secure)
    {
        using (c)
        {
            try
            {
                c.NoDelay = true;
                Stream stream = c.GetStream();
                X509Certificate2? clientCert = null;
                SslStream? ssl = null;
                if (secure)
                {
                    ssl = new SslStream(stream, false, (_, cert, _, _) => true);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = host.Identity.Certificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    });
                    if (ssl.RemoteCertificate != null) clientCert = new X509Certificate2(ssl.RemoteCertificate);
                    stream = ssl;
                }
                var request = await ReadRequest(stream);
                if (request == null) return;
                string target = request.Value.Target;
                var local = (IPEndPoint)c.Client.LocalEndPoint!;
                var remote = (IPEndPoint)c.Client.RemoteEndPoint!;
                string body;
                int status = 200;
                string type = "application/xml";
                try
                {
                    if (target.Split('?')[0] == "/actions/clipboard")
                    {
                        (status, body) = Clipboard(request.Value.Method, request.Value.Body, secure && clientCert != null && host.Clients.Knows(clientCert.RawData));
                        type = "text/plain; charset=utf-8";
                    }
                    else
                    {
                        body = Handle(target, secure, clientCert, local, remote);
                    }
                }
                catch (Exception e)
                {
                    Log.P($"host: {target.Split('?')[0]} failed: {e.Message}");
                    body = Xml(400, e.Message, "");
                }
                if (body.Length == 0 && type == "application/xml") { status = 404; body = Xml(404, "Not Found", ""); }
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                string reason = status switch { 200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found" };
                string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\n" +
                    $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                ssl?.Dispose();
            }
            catch (Exception e) when (e is IOException or AuthenticationException or SocketException or ObjectDisposedException)
            {
                // A client that went away mid-request.
            }
        }
    }

    /// <summary>The request: its method, target ("/serverinfo?uniqueid=...") and body; null if none came.</summary>
    private static async Task<(string Method, string Target, string Body)?> ReadRequest(Stream s)
    {
        var buf = new byte[1 << 20];
        int n = 0, headEnd = -1, length = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n, buf.Length - n));
            if (r <= 0) break;
            n += r;
            if (headEnd < 0)
            {
                string text = Encoding.ASCII.GetString(buf, 0, n);
                headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headEnd >= 0)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text[..headEnd], @"(?im)^Content-Length:\s*(\d+)");
                    length = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                }
            }
            if (headEnd >= 0 && n >= headEnd + 4 + length) break;
        }
        if (headEnd < 0) return null;
        string head = Encoding.ASCII.GetString(buf, 0, headEnd);
        var parts = head[..(head.IndexOf("\r\n", StringComparison.Ordinal) is int e && e >= 0 ? e : head.Length)].Split(' ');
        if (parts.Length < 2) return null;
        string body = Encoding.UTF8.GetString(buf, headEnd + 4, Math.Max(0, Math.Min(length, n - headEnd - 4)));
        return (parts[0], parts[1], body);
    }

    /// <summary>Apollo's clipboard request, text only, for a paired phone with a stream running: GET reads this PC's
    /// clipboard, POST sets it.</summary>
    private (int Status, string Body) Clipboard(string method, string body, bool paired)
    {
        if (!paired) return (401, "");
        if (!host.SessionRunning) return (403, "");
        if (method == "POST")
        {
            bool ok = HostClipboard.Set(body);
            Log.P($"host: clipboard from the phone, {body.Length} characters{(ok ? "" : ": not set")}");
            return (ok ? 200 : 403, "");
        }
        return (200, HostClipboard.Get() ?? "");
    }

    private static Dictionary<string, string> Query(string target)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = target.IndexOf('?');
        if (i < 0) return q;
        foreach (string kv in target[(i + 1)..].Split('&'))
        {
            int e = kv.IndexOf('=');
            if (e > 0) q[Uri.UnescapeDataString(kv[..e])] = Uri.UnescapeDataString(kv[(e + 1)..]);
        }
        return q;
    }

    private static string Xml(int code, string message, string inner) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><root status_code=\"{code}\"" +
        (message.Length > 0 ? $" status_message=\"{WebUtility.HtmlEncode(message)}\"" : "") + $">{inner}</root>";

    private static string E(string tag, object value) => $"<{tag}>{WebUtility.HtmlEncode(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))}</{tag}>";

    private string Handle(string target, bool secure, X509Certificate2? clientCert, IPEndPoint local, IPEndPoint remote)
    {
        string path = target.Split('?')[0].TrimStart('/');
        var q = Query(target);
        bool paired = secure && clientCert != null && host.Clients.Knows(clientCert.RawData);
        if (path != "serverinfo") Log.P($"host: {(secure ? "HTTPS" : "HTTP")} /{path} from {remote.Address}{(secure ? paired ? " (paired)" : " (not paired)" : "")}");
        switch (path)
        {
            case "serverinfo": return ServerInfo(secure && paired, local);
            case "pair": return Pair(q, secure, paired);
            case "unpair": return Xml(200, "", "");
        }
        if (!paired) return Xml(401, "The client is not authorized. Certificate verification failed.", "");
        switch (path)
        {
            case "applist":
                return Xml(200, "", $"<App><IsHdrSupported>0</IsHdrSupported><AppTitle>Desktop</AppTitle><UUID></UUID><ID>{HostServer.DesktopAppId}</ID></App>");
            case "launch":
            case "resume":
                string? url = host.Launch(q, local, path == "resume");
                if (url == null) return Xml(200, "", $"<{(path == "launch" ? "gamesession" : "resume")}>0</{(path == "launch" ? "gamesession" : "resume")}>");
                return Xml(200, "", E("sessionUrl0", url) + (path == "launch" ? E("gamesession", 1) : E("resume", 1)));
            case "cancel":
                host.EndSession("the phone ended it");
                return Xml(200, "", E("cancel", 1));
            case "appasset":
                return "";
        }
        return "";
    }

    /// <summary>The MAC address of the card that has [address], as aa:bb:cc:dd:ee:ff; zeros if none has it.</summary>
    internal static string MacFor(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address))) continue;
            byte[] b = nic.GetPhysicalAddress().GetAddressBytes();
            if (b.Length == 6) return string.Join(":", b.Select(x => x.ToString("x2")));
        }
        return "00:00:00:00:00:00";
    }

    private string ServerInfo(bool paired, IPEndPoint local)
    {
        var sb = new StringBuilder();
        sb.Append(E("hostname", host.Name));
        sb.Append(E("appversion", "7.1.431.-1"));
        sb.Append(E("GfeVersion", "3.23.0.74"));
        sb.Append(E("uniqueid", host.Identity.UniqueId));
        sb.Append(E("HttpsPort", host.HttpsPort));
        sb.Append(E("ExternalPort", host.HttpPort));
        sb.Append(E("MaxLumaPixelsHEVC", host.HasHevc ? "1869449984" : "0"));
        // The network card's address, for Wake-on-LAN (the phone wakes a sleeping PC, or asks its wake relay at home to):
        // to a paired phone only, as Apollo gives it.
        sb.Append(E("mac", paired ? MacFor(local.Address) : "00:00:00:00:00:00"));
        // Launch, the mouse and the keyboard: what the phone checks before it streams.
        // Apollo's bits as the PC helper gives a phone: input, clipboard both ways, launch, view (0x07031F00).
        sb.Append(E("Permission", paired ? 0x07031F00 | 0x4001800 : 0));
        if (paired)
        {
            sb.Append(E("VirtualDisplayCapable", host.MakesScreens ? "true" : "false"));
            sb.Append(E("VirtualDisplayDriverReady", host.MakesScreens ? "true" : "false"));
        }
        sb.Append(E("LocalIP", local.Address.ToString()));
        sb.Append(E("ServerCodecModeSupport", host.CodecSupport));
        sb.Append(E("PairStatus", paired ? 1 : 0));
        int game = paired && host.SessionRunning ? HostServer.DesktopAppId : 0;
        sb.Append(E("currentgame", game));
        sb.Append(E("currentgameuuid", ""));
        sb.Append(E("state", game != 0 ? "SUNSHINE_SERVER_BUSY" : "SUNSHINE_SERVER_FREE"));
        return Xml(200, "", sb.ToString());
    }

    // ---- pairing (Apollo nvhttp.cpp getservercert ... clientpairingsecret) ----

    private string Fail(string id, string why)
    {
        pairings.TryRemove(id, out _);
        Log.P($"host: pairing failed: {why}");
        return Xml(400, why, E("paired", 0));
    }

    private string Pair(Dictionary<string, string> q, bool secure, bool paired)
    {
        string id = q.GetValueOrDefault("uniqueid") ?? "";
        if (q.TryGetValue("phrase", out string? phrase) && phrase == "pairchallenge")
            return Xml(200, "", E("paired", secure && paired ? 1 : 0));
        if (q.TryGetValue("phrase", out phrase) && phrase == "getservercert")
        {
            string salt = q.GetValueOrDefault("salt") ?? "";
            if (salt.Length < 32) return Fail(id, "Salt too short");
            byte[] clientPem = Convert.FromHexString(q.GetValueOrDefault("clientcert") ?? "");
            byte[] der;
            try { using var c = new X509Certificate2(clientPem); der = c.RawData; }
            catch (Exception) { return Fail(id, "Invalid client certificate"); }
            string name = q.GetValueOrDefault("devicename") ?? "Phone";
            Log.P($"host: {name} ({id}) asks to pair: waiting for its PIN");
            string? pin = host.WaitForPin(TimeSpan.FromSeconds(120));
            if (pin == null) return Fail(id, "No PIN was given");
            var p = new Pairing
            {
                Phase = 1,
                AesKey = SHA256.HashData(Convert.FromHexString(salt[..32]).Concat(Encoding.UTF8.GetBytes(pin)).ToArray())[..16],
                ClientCertDer = der,
                Name = name,
            };
            pairings[id] = p;
            return Xml(200, "", E("paired", 1) + E("plaincert", PcScreenLogic.Hex(host.Identity.PemCert)));
        }
        if (!pairings.TryGetValue(id, out var s)) return Fail(id, "Out of order pairing call");
        if (q.TryGetValue("clientchallenge", out string? challenge))
        {
            if (s.Phase != 1) return Fail(id, "Out of order call to clientchallenge");
            s.Phase = 2;
            byte[] dec = Ecb(Convert.FromHexString(challenge), s.AesKey, false);
            s.ServerSecret = RandomNumberGenerator.GetBytes(16);
            byte[] hash = SHA256.HashData(dec.Concat(host.Identity.CertSignature).Concat(s.ServerSecret).ToArray());
            s.ServerChallenge = RandomNumberGenerator.GetBytes(16);
            byte[] enc = Ecb(hash.Concat(s.ServerChallenge).ToArray(), s.AesKey, true);
            return Xml(200, "", E("paired", 1) + E("challengeresponse", PcScreenLogic.Hex(enc)));
        }
        if (q.TryGetValue("serverchallengeresp", out string? resp))
        {
            if (s.Phase != 2) return Fail(id, "Out of order call to serverchallengeresp");
            s.Phase = 3;
            s.ClientHash = Ecb(Convert.FromHexString(resp), s.AesKey, false);
            byte[] secret = s.ServerSecret.Concat(host.Identity.Sign(s.ServerSecret)).ToArray();
            return Xml(200, "", E("pairingsecret", PcScreenLogic.Hex(secret)) + E("paired", 1));
        }
        if (q.TryGetValue("clientpairingsecret", out string? cps))
        {
            if (s.Phase != 3) return Fail(id, "Out of order call to clientpairingsecret");
            byte[] all = Convert.FromHexString(cps);
            if (all.Length <= 16) return Fail(id, "Client pairing secret too short");
            byte[] secret = all[..16], sign = all[16..];
            using var cert = new X509Certificate2(s.ClientCertDer);
            byte[] hash = SHA256.HashData(s.ServerChallenge.Concat(PcScreens.PcIdentity.SignatureOf(s.ClientCertDer)).Concat(secret).ToArray());
            using var pub = cert.GetRSAPublicKey();
            bool sameHash = hash.AsSpan().SequenceEqual(s.ClientHash.AsSpan(0, Math.Min(s.ClientHash.Length, 32)));
            bool verified = pub != null && pub.VerifyData(secret, sign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            pairings.TryRemove(id, out _);
            if (!sameHash || !verified)
            {
                Log.P($"host: pairing refused (same hash {sameHash}, signature {verified}): a wrong PIN?");
                return Xml(200, "", E("paired", 0));
            }
            string name = host.PinName ?? s.Name;
            host.Clients.Add(name, id, s.ClientCertDer);
            Log.P($"host: paired with {name} ({id})");
            return Xml(200, "", E("paired", 1));
        }
        return Fail(id, "Unknown pairing step");
    }

    private static byte[] Ecb(byte[] data, byte[] key, bool encrypt)
    {
        byte[] padded = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = key;
        return encrypt ? aes.EncryptEcb(padded, PaddingMode.None) : aes.DecryptEcb(padded, PaddingMode.None);
    }
}

/// <summary>
/// The host's RTSP side (Sunshine's rtsp.cpp, GPLv3): OPTIONS, DESCRIBE, SETUP of audio, video and control, ANNOUNCE
/// (the client's stream settings) and PLAY, each on its own TCP connection, answered and closed.
/// </summary>
internal sealed class HostRtsp
{
    private readonly HostServer host;
    private TcpListener? listener;
    private volatile bool stopping;

    public HostRtsp(HostServer host) => this.host = host;

    public void Start()
    {
        listener = new TcpListener(IPAddress.Any, host.RtspPort);
        listener.Start();
        _ = Accept();
        Log.P($"host: RTSP on {host.RtspPort}");
    }

    public void Stop()
    {
        stopping = true;
        try { listener?.Stop(); } catch (Exception) { }
    }

    private async Task Accept()
    {
        while (!stopping)
        {
            TcpClient c;
            try { c = await listener!.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.NoDelay = true;
                var s = c.GetStream();
                var buf = new byte[65536];
                int n = 0, headEnd = -1, contentLength = 0;
                while (n < buf.Length)
                {
                    int r = await s.ReadAsync(buf.AsMemory(n, buf.Length - n));
                    if (r <= 0) break;
                    n += r;
                    string t = Encoding.ASCII.GetString(buf, 0, n);
                    if (headEnd < 0 && (headEnd = t.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(t[..headEnd], @"(?im)^Content-length:\s*(\d+)");
                        contentLength = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    }
                    if (headEnd >= 0 && n >= headEnd + 4 + contentLength) break;
                }
                if (headEnd < 0) return;
                string head = Encoding.ASCII.GetString(buf, 0, headEnd);
                string body = Encoding.UTF8.GetString(buf, headEnd + 4, Math.Max(0, n - headEnd - 4));
                string[] lines = head.Split("\r\n");
                string[] first = lines[0].Split(' ');
                string verb = first[0], target = first.Length > 1 ? first[1] : "";
                string cseq = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("CSeq", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim()).FirstOrDefault() ?? "0";
                var (options, payload, status) = host.Rtsp(verb, target, body);
                var sb = new StringBuilder();
                sb.Append($"RTSP/1.0 {status} {(status == 200 ? "OK" : "NOT FOUND")}\r\nCSeq: {cseq}\r\n");
                foreach (var (k, v) in options) sb.Append($"{k}: {v}\r\n");
                if (payload.Length > 0) sb.Append($"Content-length: {Encoding.UTF8.GetByteCount(payload)}\r\n");
                sb.Append("\r\n").Append(payload);
                await s.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
                await s.FlushAsync();
                c.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
