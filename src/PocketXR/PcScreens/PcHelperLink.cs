using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: the Pocket XR PC helper on the computer streamed from (UDP and TCP 47610), as the phone uses it
/// (PcVrHelper.kt): found by "POCKETXR-FIND 1", allowed once there (op pair, a Yes on that computer), then it types
/// the pairing PIN into Apollo or Sunshine for this PC (apollo-pin), says what the computer is (status: os, virtual),
/// and readies a Mac's or Linux's virtual screen before a stream (screen-prepare). Blocking calls.
/// </summary>
public sealed class PcHelperLink
{
    public const int Port = 47610;

    public sealed record Pc(IPAddress Address, int Port, string Name, List<int> SunshinePorts);

    private static string TokenFile => Path.Combine(PcIdentity.Folder, "helper-tokens.json");
    private static readonly object gate = new();

    /// <summary>This PC's name as the other computer's helper shows it ("MIKE").</summary>
    public static string ThisName => Environment.MachineName;

    /// <summary>
    /// This PC's name in Apollo's and Sunshine's client list: its own, so it never matches an older record of the same
    /// computer. Apollo renames a second "MIKE" to "MIKE (2)" while the helper gives permissions to the record named
    /// exactly as asked, the old one (20 Nov 2026).
    /// </summary>
    public static string ClientName => Environment.MachineName + " Pocket XR";

    /// <summary>Every helper answering a broadcast within <paramref name="waitMs"/>, plus the computers asked directly.</summary>
    public static List<Pc> FindAll(int waitMs = 1500, IEnumerable<IPAddress>? alsoAsk = null)
    {
        var found = new List<Pc>();
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.EnableBroadcast = true;
            byte[] ask = Encoding.ASCII.GetBytes("POCKETXR-FIND 1");
            socket.Send(ask, ask.Length, new IPEndPoint(IPAddress.Broadcast, Port));
            if (alsoAsk != null) foreach (var a in alsoAsk) try { socket.Send(ask, ask.Length, new IPEndPoint(a, Port)); } catch (SocketException) { }
            var end = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < end)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) break;
                socket.Client.ReceiveTimeout = left;
                IPEndPoint from = new(IPAddress.Any, 0);
                byte[] data;
                try { data = socket.Receive(ref from); }
                catch (SocketException) { break; }
                var here = PcScreenLogic.HelperHere(Encoding.UTF8.GetString(data));
                if (here is { } h && !found.Exists(p => p.Address.Equals(from.Address)))
                    found.Add(new Pc(from.Address, h.Port, h.Name, h.Ports));
            }
        }
        catch (SocketException e)
        {
            Log.P($"pc screens: helper search failed: {e.Message}");
        }
        return found;
    }

    /// <summary>The helper on <paramref name="host"/> only, asked there directly; null if it does not answer.</summary>
    public static Pc? FindAt(IPAddress host)
    {
        foreach (var pc in FindAll(1200, new[] { host }))
            if (pc.Address.Equals(host) || (IPAddress.IsLoopback(host) && IsThisPc(pc.Address))) return pc;
        return null;
    }

    private static bool IsThisPc(IPAddress a)
    {
        try
        {
            foreach (var mine in Dns.GetHostAddresses(Dns.GetHostName())) if (mine.Equals(a)) return true;
        }
        catch (SocketException) { }
        return false;
    }

    private static Dictionary<string, string> Tokens()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenFile)) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private static string? TokenFor(Pc pc)
    {
        lock (gate) return Tokens().TryGetValue(pc.Name, out var t) ? t : null;
    }

    private static void SaveToken(Pc pc, string? token)
    {
        lock (gate)
        {
            var all = Tokens();
            if (token == null) all.Remove(pc.Name); else all[pc.Name] = token;
            Directory.CreateDirectory(PcIdentity.Folder);
            File.WriteAllText(TokenFile, JsonSerializer.Serialize(all));
        }
    }

    public static bool Knows(Pc pc) => TokenFor(pc) != null;

    private static JsonObject? Ask(Pc pc, JsonObject request, int waitMs)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(pc.Address, pc.Port).Wait(2000)) return null;
            client.ReceiveTimeout = waitMs;
            client.SendTimeout = 2000;
            var stream = client.GetStream();
            byte[] line = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            stream.Write(line, 0, line.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? answer = reader.ReadLine();
            return answer == null ? null : JsonNode.Parse(answer) as JsonObject;
        }
        catch (Exception e)
        {
            Log.P($"pc screens: PC helper on {pc.Name}: {e.Message}");
            return null;
        }
    }

    private static bool Ok(JsonObject? a) => a?["ok"]?.GetValue<bool>() == true;
    private static string? Str(JsonObject? a, string key) => a?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>That computer's user is asked once whether this PC may use its helper. True if allowed.</summary>
    public static bool Pair(Pc pc)
    {
        var a = Ask(pc, new JsonObject { ["op"] = "pair", ["phone"] = ThisName }, 65_000);
        string? token = Str(a, "token");
        if (!Ok(a) || string.IsNullOrEmpty(token)) return false;
        SaveToken(pc, token);
        return true;
    }

    /// <summary>What the helper says of its computer: its system ("", "mac", "linux", "linux-virtual") and whether it
    /// makes a virtual screen per PC screen. Null if not allowed or no answer.</summary>
    public static (string Os, bool Virtual)? HostInfo(Pc pc)
    {
        string? token = TokenFor(pc);
        if (token == null) return null;
        var a = Ask(pc, new JsonObject { ["op"] = "status", ["token"] = token }, 4000);
        if (a == null) return null;
        if (!Ok(a))
        {
            if (Str(a, "why") == "not paired") SaveToken(pc, null);
            return null;
        }
        bool vd = a["virtual"] is JsonValue v && v.TryGetValue(out bool b) && b;
        return (Str(a, "os") ?? "", vd);
    }

    /// <summary>The helper types <paramref name="pin"/> into the Apollo or Sunshine on HTTP port <paramref name="port"/>.
    /// Null when it worked, else why not.</summary>
    public static string? ApolloPin(Pc pc, string pin, int port)
    {
        string? token = TokenFor(pc);
        if (token == null) return "not paired";
        var a = Ask(pc, new JsonObject { ["op"] = "apollo-pin", ["token"] = token, ["pin"] = pin, ["name"] = ClientName, ["port"] = port }, 50_000);
        if (a == null) return "no answer";
        if (Str(a, "why") == "not paired") SaveToken(pc, null);
        return Ok(a) ? null : Str(a, "why") ?? "refused";
    }

    /// <summary>A Mac's or Linux's helper brings the virtual screen of the Sunshine on <paramref name="port"/> out at this
    /// size before the stream launches. True once ready.</summary>
    public static bool ScreenPrepare(Pc pc, int port, int w, int h, int fps)
    {
        string? token = TokenFor(pc);
        if (token == null) return false;
        return Ok(Ask(pc, new JsonObject { ["op"] = "screen-prepare", ["token"] = token, ["port"] = port, ["width"] = w, ["height"] = h, ["fps"] = fps }, 10_000));
    }
}
