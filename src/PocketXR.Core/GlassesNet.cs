using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PocketXR.Core;

/// <summary>
/// The glasses' USB network on a PC.
///
/// The Aura and the One family show up as a USB network adapter (CDC NCM; Windows 11 has its own driver for it,
/// "UsbNcm Host Device", and none for the glasses' second, ECM function) with the glasses at 169.254.2.1 and the PC on
/// another 169.254.2.x address, handed out by the glasses. A PC often has several adapters with 169.254 addresses at
/// once (an unplugged Ethernet port, Wi-Fi with no lease), so the routing table is not trusted: the adapter that holds
/// a 169.254.2.x (or 169.254.1.x) address is found, and sockets are bound to that address so they go out on it.
/// Seen working on Windows 11 with the Aura on 15 November 2026.
/// </summary>
public static class GlassesNet
{
    /// <summary>One candidate: the adapter's name, its index, the PC's own address on it, and the glasses' address.</summary>
    public readonly record struct Route(string Interface, uint Index, string Local, string Glasses)
    {
        public string Description => $"{Interface} ({Local} -> {Glasses})";
        public override string ToString() => Description;
    }

    /// <summary>The glasses' link for each adapter with a 169.254.2.x address (NCM first), then 169.254.1.x (ECM).</summary>
    public static List<Route> Routes()
    {
        var found = new List<Route>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props = nic.GetIPProperties();
                uint index = 0;
                try { index = (uint)(props.GetIPv4Properties()?.Index ?? 0); } catch (Exception) { }
                foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = a.Address.GetAddressBytes();
                    if (b[0] != 169 || b[1] != 254) continue;
                    if (b[2] != 2 && b[2] != 1) continue;
                    found.Add(new Route(nic.Description, index, $"169.254.{b[2]}.{b[3]}", $"169.254.{b[2]}.1"));
                }
            }
        }
        catch (Exception e)
        {
            Log.P($"glasses link: the adapters could not be listed: {e.Message}");
        }
        return found.OrderBy(r => r.Glasses == "169.254.2.1" ? 0 : 1).ToList();
    }

    /// <summary>Every IPv4 adapter with its address, for the log when the glasses' link is not found.</summary>
    public static string DescribeInterfaces()
    {
        var parts = new List<string>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        parts.Add($"{nic.Description} {a.Address}");
                }
            }
        }
        catch (Exception)
        {
            return "the adapters could not be listed";
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    public sealed class NetError : Exception
    {
        public NetError(string message) : base(message) { }
        public static NetError Connect(string to, string why) => new($"connect to {to}: {why}");
        public static NetError Timeout(string to) => new($"connect to {to}: timed out");
        public static NetError Closed() => new("closed by the glasses");
    }

    /// <summary>
    /// A connected TCP socket to [host]:[port], bound to [route]'s address when given. Blocking reads, with a receive
    /// timeout of [readTimeoutS] so a reader can notice it was asked to stop.
    /// </summary>
    public static Socket Connect(string host, ushort port, Route? route, double timeoutS = 3, double readTimeoutS = 0.5)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        string to = $"{host}:{port}" + (route is Route r ? $" on {r.Interface}" : "");
        try
        {
            if (route is Route bound) s.Bind(new IPEndPoint(IPAddress.Parse(bound.Local), 0));
            // A connect with a limit, so an absent link fails in [timeoutS] rather than Windows' 21 s.
            IAsyncResult pending = s.BeginConnect(new IPEndPoint(IPAddress.Parse(host), port), null, null);
            if (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutS)))
            {
                s.Close();
                throw NetError.Timeout(to);
            }
            s.EndConnect(pending);
            s.ReceiveTimeout = Math.Max(1, (int)(readTimeoutS * 1000));
            return s;
        }
        catch (NetError)
        {
            throw;
        }
        catch (Exception e)
        {
            s.Close();
            throw NetError.Connect(to, e.Message);
        }
    }

    /// <summary>
    /// Opens [port] on the glasses on the first route that answers: each found adapter, then a plain connect to
    /// 169.254.2.1 and 169.254.1.1. Returns the socket and how it was reached.
    /// </summary>
    public static (Socket Socket, string Via) Open(ushort port, Action<string> log)
    {
        var tries = new List<(string Host, Route? Route)>();
        // A wrong link answers nothing, so each bound try is short; the plain connects get the full time.
        foreach (Route r in Routes()) tries.Add((r.Glasses, r));
        tries.Add(("169.254.2.1", null));
        tries.Add(("169.254.1.1", null));
        Exception last = NetError.Closed();
        foreach ((string host, Route? route) in tries)
        {
            try
            {
                Socket s = Connect(host, port, route, timeoutS: route == null ? 3 : 1.5);
                return (s, route?.Description ?? $"{host}, unbound");
            }
            catch (Exception e)
            {
                last = e;
            }
        }
        log($"glasses link: port {port} not reachable ({last.Message}); adapters: {DescribeInterfaces()}");
        throw last;
    }

    /// <summary>Writes all of [bytes]; throws when the link is gone.</summary>
    public static void Send(Socket s, byte[] bytes)
    {
        try
        {
            int sent = 0;
            while (sent < bytes.Length)
            {
                int n = s.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (n <= 0) throw NetError.Closed();
                sent += n;
            }
        }
        catch (NetError)
        {
            throw;
        }
        catch (Exception)
        {
            throw NetError.Closed();
        }
    }

    /// <summary>Reads what has come, up to the buffer's size: an empty array after the read timeout, null when the link closed.</summary>
    public static byte[]? Receive(Socket s, byte[] buf)
    {
        try
        {
            int n = s.Receive(buf, 0, buf.Length, SocketFlags.None);
            if (n > 0) return buf.AsSpan(0, n).ToArray();
            return null;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock or SocketError.Interrupted)
        {
            return Array.Empty<byte>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
