using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace PocketXR.PcScreens;

/// <summary>This PC itself, which a PC screen never streams: since the app answers phones as a host too, it finds
/// itself among the computers on the network.</summary>
internal static class ThisPc
{
    /// <summary>[host] (an address or a name) is this PC: a loopback address, one of its own addresses, or its name.</summary>
    public static bool Is(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().TrimStart('[').TrimEnd(']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(h, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(ip));
        }
        catch (NetworkInformationException) { return false; }
    }
}
