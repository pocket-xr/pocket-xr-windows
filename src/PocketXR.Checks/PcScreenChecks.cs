using System.Collections.Generic;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>PC screens' rules: the same cases as the phone's PcScreen tests where they exist.</summary>
public static class PcScreenChecks
{
    public static void Run()
    {
        // Addresses as typed.
        Check.That(PcScreenLogic.ParseAddress("192.168.1.20") == ("192.168.1.20", 47989), "a bare address takes the usual port");
        Check.That(PcScreenLogic.ParseAddress(" 192.168.1.20:48989 ") == ("192.168.1.20", 48989), "an address with a port");
        Check.That(PcScreenLogic.ParseAddress("[fe80::1]:48989") == ("fe80::1", 48989), "IPv6 in brackets with a port");
        Check.That(PcScreenLogic.ParseAddress("fe80::1") == ("fe80::1", 47989), "bare IPv6");
        Check.That(PcScreenLogic.ParseAddress("") == null, "empty is no address");
        Check.That(PcScreenLogic.ParseAddress("pc:0") == null, "port 0 refused");
        Check.That(PcScreenLogic.ParseAddress("pc:70000") == null, "port over 65535 refused");
        Check.That(PcScreenLogic.ParseAddress(":48989") == null, "a port with no host refused");
        Check.That(PcScreenLogic.Typed("192.168.1.11", 47989) == "192.168.1.11", "the usual port is not written");
        Check.That(PcScreenLogic.Typed("192.168.1.11", 48989) == "192.168.1.11:48989", "another port is written");
        Check.That(PcScreenLogic.Typed("fe80::1", 48989) == "[fe80::1]:48989", "IPv6 gets brackets");

        // The stream's shape for a window.
        Check.That(PcScreenLogic.ForWindow(1920, 1080) == (1920, 1080), "1080p stays 1080p");
        Check.That(PcScreenLogic.ForWindow(1976, 1080) == (1976, 1080), "the phone's panel shape, multiples of 8");
        Check.That(PcScreenLogic.ForWindow(1283, 721) == (1280, 720), "rounded down to multiples of 8");
        Check.That(PcScreenLogic.ForWindow(7680, 2160) == (3840, 1080), "32:9 limited to 3840 a side");
        Check.That(PcScreenLogic.ForWindow(5000, 5000) is var (sw, sh) && (long)sw * sh <= PcScreenLogic.MaxArea && sw == sh,
            "a huge square is held to 3840 x 2160 in all, still square");
        Check.That(PcScreenLogic.ForWindow(320, 180) == (640, 360), "a small window is scaled up whole to 640 x 360");
        Check.That(PcScreenLogic.ForWindow(1080, 1920) == (1080, 1920), "portrait stays portrait");
        Check.That(PcScreenLogic.ForWindow(0, 100) == (1920, 1080), "no size: the usual desktop");
        Check.That(!PcScreenLogic.ReshapeNeeded(1930, 1085, 1920, 1080), "a few pixels do not restart the stream");
        Check.That(PcScreenLogic.ReshapeNeeded(1600, 1080, 1920, 1080), "a much narrower window restarts it");

        // Fitting the picture in the window.
        Check.That(PcScreenLogic.Fit(1920, 1080, 1920, 1080) == (0, 0, 1920, 1080), "same shape fills");
        Check.That(PcScreenLogic.Fit(2000, 1080, 1920, 1080) == (40, 0, 1920, 1080), "wider window: bars at the sides");
        Check.That(PcScreenLogic.Fit(1920, 1200, 1920, 1080) == (0, 60, 1920, 1080), "taller window: bars above and below");

        // Bitrate.
        Check.That(PcScreenLogic.BitrateKbps(1920, 1080, 60, "auto") == 40_000, "40 Mbps at 1080p60");
        Check.That(PcScreenLogic.BitrateKbps(1920, 1080, 60, "sharp") == 64_000, "sharp is 1.6 times");
        Check.That(PcScreenLogic.BitrateKbps(1920, 1080, 60, "saver") == 20_000, "saver is half");
        Check.That(PcScreenLogic.BitrateKbps(640, 360, 30, "saver") == 8_000, "at least 8 Mbps");
        Check.That(PcScreenLogic.BitrateKbps(3840, 2160, 120, "sharp") == 150_000, "at most 150 Mbps");
        Check.That(PcScreenLogic.BitrateKbps(1920, 1080, 120, "auto") == 60_629, "120 fps: 2^0.6 times, not twice");

        // Reconnecting.
        Check.That(PcScreenLogic.ReconnectDelayMs(0, 0) == 1000 && PcScreenLogic.ReconnectDelayMs(3, 5000) == 8000, "1 s, then doubling");
        Check.That(PcScreenLogic.ReconnectDelayMs(9, 60_000) == 10_000, "then every 10 s");
        Check.That(PcScreenLogic.ReconnectDelayMs(1, 180_000) == null, "given up after 3 minutes");

        // Names and instances.
        Check.That(PcScreenLogic.PcName("Mike(2)") == "Mike", "mDNS numbering dropped");
        Check.That(PcScreenLogic.PcName("MIKE screen 2") == "MIKE", "instance name dropped");
        Check.That(PcScreenLogic.PcName("Halcyons-Air.mynetworksettings.com") == "Halcyons-Air", "network suffix dropped");
        var ports = new List<int> { 49989, 47989, 48989 };
        Check.That(PcScreenLogic.InstanceFor(0, ports, _ => true) == 47989, "PC screen 1 takes the main instance");
        Check.That(PcScreenLogic.InstanceFor(1, ports, _ => true) == 48989, "PC screen 2 takes the first extra");
        Check.That(PcScreenLogic.InstanceFor(2, ports, p => p != 48989) == 49989, "PC screen 3 the next free extra");
        Check.That(PcScreenLogic.InstanceFor(1, ports, p => p == 47989) == 47989, "the main one when no extra is free");
        Check.That(PcScreenLogic.InstanceFor(0, ports, _ => false) == null, "none free");

        // The host's answers.
        const string info = "<?xml version=\"1.0\" encoding=\"utf-8\"?><root status_code=\"200\"><hostname>MIKE screen 2</hostname>" +
            "<appversion>7.1.431.-1</appversion><HttpsPort>48984</HttpsPort><PairStatus>1</PairStatus>" +
            "<VirtualDisplayCapable>true</VirtualDisplayCapable><VirtualDisplayDriverReady>true</VirtualDisplayDriverReady>" +
            "<state>SUNSHINE_SERVER_BUSY</state><currentgame>881448767</currentgame></root>";
        Check.That(PcScreenLogic.Xml(info, "hostname") == "MIKE screen 2", "hostname read");
        Check.That(PcScreenLogic.Xml(info, "missing") == null, "a missing tag is null");
        Check.That(PcScreenLogic.MajorVersion(info) == 7, "Apollo is generation 7: SHA-256 pairing");
        Check.That(PcScreenLogic.ApolloVirtual(info), "Apollo with its driver ready makes virtual displays");
        Check.That(!PcScreenLogic.ApolloVirtual(info.Replace("<VirtualDisplayDriverReady>true", "<VirtualDisplayDriverReady>false")),
            "not when its driver is not ready");
        Check.That(PcScreenLogic.XmlStatus(info) == (200, ""), "status 200");
        Check.That(PcScreenLogic.XmlStatus("<root status_code=\"401\" status_message=\"The client is not authorized. Certificate verification failed.\"/>")
            .Code == 401, "status 401 read");
        const string apps = "<root status_code=\"200\"><App><IsHdrSupported>0</IsHdrSupported><AppTitle>Virtual Display</AppTitle><ID>1</ID></App>" +
            "<App><AppTitle>Desktop</AppTitle><ID>881448767</ID></App></root>";
        Check.That(PcScreenLogic.DesktopApp(PcScreenLogic.Apps(apps)) == (881448767, "Desktop"), "Desktop chosen");
        Check.That(PcScreenLogic.DesktopApp(PcScreenLogic.Apps("<root><App><AppTitle>Steam</AppTitle><ID>5</ID></App></root>")) == (5, "Steam"),
            "else the first app");
        Check.That(PcScreenLogic.DesktopApp(PcScreenLogic.Apps("<root/>")) == null, "no apps");

        // The PC helper's answer.
        var here = PcScreenLogic.HelperHere("POCKETXR-HERE 1 47610 steamdeck 47989,48989,49989");
        Check.That(here is { } h1 && h1.Name == "steamdeck" && h1.Ports.Count == 3 && h1.Port == 47610, "the Linux helper lists its ports");
        var mac = PcScreenLogic.HelperHere("POCKETXR-HERE 1 47610 Halcyons Air");
        Check.That(mac is { } h2 && h2.Name == "Halcyons Air" && h2.Ports.Count == 0, "a name with a space, no ports");
        Check.That(PcScreenLogic.HelperHere("HELLO") == null, "not a helper");
    }
}
