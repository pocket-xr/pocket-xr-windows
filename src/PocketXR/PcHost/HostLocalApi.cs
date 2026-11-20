using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// The few calls of Apollo's web page that the Pocket XR PC helper makes to type a phone's PIN (its Apollo.Pin): sign
/// in, list the paired clients, forget one, give the PIN, update a client. HTTPS on the base port + 1, where Apollo's
/// page is, answered for this PC only (loopback): the helper on this PC types the PIN, so the phone pairs with nothing
/// to type. The sign-in is accepted as given and never logged: only programs on this PC reach it.
/// </summary>
internal sealed class HostLocalApi
{
    private readonly HostServer host;
    private readonly List<TcpListener> listeners = new();
    private volatile bool stopping;

    public HostLocalApi(HostServer host) => this.host = host;

    public void Start()
    {
        foreach (var a in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var l = new TcpListener(a, host.HttpPort + 1);
                l.Start();
                listeners.Add(l);
                _ = Accept(l);
            }
            catch (SocketException e)
            {
                Log.P($"host: local web on {a}:{host.HttpPort + 1}: {e.SocketErrorCode}");
            }
        }
    }

    public void Stop()
    {
        stopping = true;
        foreach (var l in listeners) try { l.Stop(); } catch (Exception) { }
    }

    private async Task Accept(TcpListener l)
    {
        while (!stopping)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
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
                if (!IPAddress.IsLoopback(((IPEndPoint)c.Client.RemoteEndPoint!).Address)) return;
                using var ssl = new SslStream(c.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = host.Identity.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var buf = new byte[65536];
                int n = 0, headEnd = -1, length = 0;
                while (n < buf.Length)
                {
                    int r = await ssl.ReadAsync(buf.AsMemory(n, buf.Length - n));
                    if (r <= 0) break;
                    n += r;
                    string t = Encoding.ASCII.GetString(buf, 0, n);
                    if (headEnd < 0 && (headEnd = t.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var m = Regex.Match(t[..headEnd], @"(?im)^Content-Length:\s*(\d+)");
                        length = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    }
                    if (headEnd >= 0 && n >= headEnd + 4 + length) break;
                }
                if (headEnd < 0) return;
                string head = Encoding.ASCII.GetString(buf, 0, headEnd);
                string[] parts = head[..head.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
                string path = parts.Length > 1 ? parts[1].Split('?')[0] : "";
                string body = Encoding.UTF8.GetString(buf, headEnd + 4, Math.Max(0, Math.Min(length, n - headEnd - 4)));
                byte[] bytes = Encoding.UTF8.GetBytes(Handle(parts[0], path, body));
                string answer = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n" +
                    "Set-Cookie: auth=pocketxr; Path=/; HttpOnly\r\nConnection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(answer));
                await ssl.WriteAsync(bytes);
                await ssl.FlushAsync();
            }
            catch (Exception e) when (e is IOException or AuthenticationException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private string Handle(string method, string path, string body)
    {
        JsonObject? o = null;
        try { o = JsonNode.Parse(body.Length > 0 ? body : "{}") as JsonObject; }
        catch (JsonException) { }
        string? Str(string k) => o?[k] is JsonValue v && v.TryGetValue(out string? x) ? x : null;
        switch (path)
        {
            case "/api/login":
                return "{\"status\":true}";
            case "/api/clients/list":
                var arr = new JsonArray();
                foreach (var cl in host.Clients.All())
                    arr.Add(new JsonObject { ["name"] = cl.Name, ["uuid"] = cl.UniqueId, ["perm"] = 0x07031F00.ToString() });
                return new JsonObject { ["status"] = true, ["named_certs"] = arr }.ToJsonString();
            case "/api/clients/unpair":
                if (Str("uuid") is string u && host.Clients.Remove(u)) Log.P("host: the PC helper removed an older record of a phone pairing again");
                return "{\"status\":true}";
            case "/api/pin":
                bool ok = host.SubmitPin(Str("pin") ?? "", Str("name"));
                Log.P($"host: the PC helper gave a PIN for {Str("name") ?? "a phone"}: {(ok ? "taken" : "no phone waiting yet")}");
                return ok ? "{\"status\":true}" : "{\"status\":false}";
            case "/api/clients/update":
                return "{\"status\":true}";
        }
        Log.P($"host: local web {method} {path}: not known");
        return "{\"status\":false}";
    }
}
