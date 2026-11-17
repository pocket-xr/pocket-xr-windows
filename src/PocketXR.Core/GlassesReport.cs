using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PocketXR.Core;

/// <summary>
/// The "Glasses report": what the PC and Pocket XR know about the glasses now, as plain text a tester can paste into
/// a GitHub issue. After Pocket XR's GlassesReport on Android.
///
/// Nothing personal goes in: no serial numbers, no user names, and no network address other than the glasses' own
/// link (169.254.x.x). USB devices that are not glasses are listed by their ids and interface classes only, as their
/// names can carry a person's name ("Sam's iPhone"). Every line handed in is cleaned again ([Scrub]) before it is
/// written.
/// </summary>
public static class GlassesReport
{
    /// <summary>
    /// A plain-text report a tester can send: the glasses found, every USB device's ids and interface classes, the
    /// network links, the settings state read, the PC's displays (passed in as lines), app version and Windows version
    /// (passed in). No serial numbers, no user names, no addresses other than 169.254.x.x.
    /// </summary>
    public static string Text(string appVersion, string system, Glasses? glasses, IReadOnlyList<UsbDeviceInfo> usb,
                              IReadOnlyList<string> links, IReadOnlyList<string> displays, SettingsControl.State? state,
                              string trackingLine)
    {
        var @out = new List<string>();
        @out.Add("Pocket XR for Windows glasses report");
        @out.Add($"Pocket XR for Windows {Scrub(appVersion)}, {Scrub(system)}");
        @out.Add("");
        @out.AddRange(ModelLines(glasses));
        @out.Add("");
        @out.Add("Displays:");
        if (displays.Count == 0) @out.Add("- none listed");
        foreach (string d in displays) @out.Add($"- {Scrub(d)}");
        @out.Add("");
        @out.Add("USB devices (every one the PC lists; names only for glasses):");
        if (usb.Count == 0) @out.Add("- none");
        foreach (UsbDeviceInfo u in usb) @out.Add(UsbLine(u));
        @out.Add("");
        @out.Add("The glasses' network links:");
        if (links.Count == 0) @out.Add("- none (no interface with a 169.254.x.x address)");
        foreach (string l in links) @out.Add($"- {Scrub(l)}");
        @out.Add("");
        @out.Add("What Pocket XR read:");
        @out.Add($"- Head tracking: {Scrub(trackingLine)}");
        @out.AddRange(StateLines(state, glasses: glasses));
        return string.Join("\n", @out) + "\n";
    }

    internal static List<string> ModelLines(Glasses? glasses)
    {
        if (glasses is not Glasses g) return new List<string> { "Model: no glasses found on USB" };
        var @out = new List<string>();
        @out.Add($"Model: {Scrub(g.Name)} ({g.Family.RawValue()} family), USB {Hex4(g.Vendor)}:{Hex4(g.Product)}");
        string tracking;
        if (g.NetworkImu)
        {
            tracking = "the glasses' USB network (TCP 169.254.2.1:52998)";
        }
        else if (g.Family == GlassesFamily.Air && g.HidImu)
        {
            tracking = $"USB HID, the Air family's stream on interface {AirImuProtocol.ImuInterface(g.Product)} (untested on a PC)";
        }
        else if (g.Family == GlassesFamily.Viture)
        {
            tracking = "USB HID, VITURE's own protocol (experimental, untested on a PC)";
        }
        else
        {
            tracking = "not known for this model: the screens are shown flat";
        }
        @out.Add($"Head tracking link: {tracking}");
        @out.Add($"Head axes: {(g.Axes == HeadFusion.Axes.One ? "x right, y down, z front" : "x right, y up, z back")}");
        @out.Add($"Control link (settings): {(g.ControlLink ? "yes, TCP 169.254.2.1:52999" : "no")}");
        if (g.SteadyCapable)
        {
            string kind = g.SixDofCapable ? (g.AppAnchor ? "in 6DoF (Spatial Anchor)" : "in 6DoF (the user turns 6DoF on in the glasses' menu)") : "in 3DoF";
            @out.Add($"Steady mode: offered, {kind}");
        }
        else
        {
            @out.Add("Steady mode: not offered on this model");
        }
        if (g.KnownOne)
        {
            @out.Add(g.Eye ? "XREAL Eye: seen (a camera on the glasses' USB), so Steady mode in 6DoF is offered"
                           : "XREAL Eye: not seen (no camera on the glasses' USB), so Steady mode in 6DoF is not offered");
        }
        if (g.Family != GlassesFamily.Aura)
        {
            @out.Add("Note: only the XREAL Aura has been run with Pocket XR on a PC; this model is untested here.");
        }
        return @out;
    }

    internal static string UsbLine(UsbDeviceInfo u)
    {
        string? maker = VendorName(u.Vendor);
        string classes = u.InterfaceClasses.Length == 0 ? "no interfaces listed" : string.Join(", ", u.InterfaceClasses.Select(ClassLabel));
        string line = $"- {Hex4(u.Vendor)}:{Hex4(u.Product)}";
        if (maker is string m)
        {
            // Only glasses are named: other devices' names can hold a person's name.
            line += $" \"{Scrub(u.Name)}\": {classes} [{m} glasses]";
            line += $"; head tracking link: {Transport(u)}";
        }
        else
        {
            line += $": {classes}";
        }
        return line;
    }

    internal static List<string> StateLines(SettingsControl.State? state, Glasses? glasses)
    {
        if (state is not SettingsControl.State s)
        {
            string why = (glasses?.ControlLink ?? false) ? "not read yet" : "these glasses have none";
            return new List<string> { $"- Control link: {why}" };
        }
        var @out = new List<string>();
        @out.Add($"- Control link: {(s.Link ? "open" : "not open")}");
        @out.Add($"- 2D / 3D state: {FlagWords(s.Stereo, on: "3D side by side (1)", off: "2D (0)")}");
        @out.Add($"- Spatial Anchor: {FlagWords(s.Anchor, on: "on (1)", off: "off (0)")}");
        @out.Add($"- Ultrawide: {FlagWords(s.Ultrawide, on: "on (1)", off: "off (0)")}");
        string size = s.SizeFactor is double f ? f.ToString("F2", CultureInfo.InvariantCulture) : "not read";
        string top = s.SizeMax is double t ? t.ToString("F2", CultureInfo.InvariantCulture) : "not read";
        @out.Add($"- Screen size: {size} (largest {top})");
        string space = s.SpaceFollow is bool follow ? (follow ? "follow" : "hover") : "not read";
        @out.Add($"- Space mode: {space}");
        @out.Add($"- Steady mode: {(s.Steady ? "on" : "off")}{(s.Busy ? " (settings changing now)" : "")}");
        if (s.SteadyNote is string note) @out.Add($"- To do in the glasses' menu: {note}");
        return @out;
    }

    internal static string FlagWords(int? v, string on, string off)
    {
        if (v is not int x) return "not read";
        if (x == 1) return on;
        if (x == 0) return off;
        return x.ToString(CultureInfo.InvariantCulture);
    }

    internal static string Hex4(int v) => v.ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>The glasses makers Pocket XR names (Pocket XR's GlassesVendors).</summary>
    internal static string? VendorName(int vendor)
    {
        if (vendor == Glasses.Xreal) return "XREAL";
        if (vendor == Glasses.Viture) return "VITURE";
        return null;
    }

    /// <summary>A USB interface class in words (Pocket XR's UsbInterfaceInfo.label).</summary>
    internal static string ClassLabel(int cls)
    {
        switch (cls)
        {
            case 0x01: return "audio";
            case 0x02: return "CDC (network)";
            case 0x03: return "HID";
            case 0x08: return "mass storage";
            case 0x0A: return "CDC data";
            case 0x0E: return "video";
            case 0xFF: return "vendor specific";
            default: return $"class {cls.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>How a glasses maker's USB device carries head tracking (Pocket XR's HeadImuTransport.choose).</summary>
    internal static string Transport(UsbDeviceInfo u)
    {
        if (u.Vendor == Glasses.Viture) return "USB HID, VITURE's own protocol (experimental)";
        if (u.Vendor != Glasses.Xreal) return "not known from the USB device";
        if (Glasses.Aura.Contains(u.Product) || Glasses.OneFamily.ContainsKey(u.Product) || u.InterfaceClasses.Contains(0x02))
        {
            return "USB network (NCM / ECM), the One family's link";
        }
        if (Glasses.AirFamily.Contains(u.Product) || u.InterfaceClasses.Contains(0x03))
        {
            return "USB HID, like XREAL's Air family";
        }
        return "not known from the USB device";
    }

    // MARK: cleaning

    /// <summary>
    /// [text] with anything personal taken out: IPv4 addresses other than 169.254.x.x, hardware (MAC) addresses, email
    /// addresses, the user's name in a home folder path, and anything that names itself a serial number.
    /// </summary>
    public static string Scrub(string text)
    {
        string t = text;
        t = Replace(t, @"(?i)\b(serial(?:\s*(?:number|no\.?))?\s*[:=#]?\s*)[A-Za-z0-9_-]{4,}", "$1[removed]");
        t = Replace(t, @"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b", "[hardware address removed]");
        t = Replace(t, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "[email removed]");
        t = Replace(t, @"(?<![0-9.])(?!169\.254\.)[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(?![0-9.])", "[address removed]");
        t = Replace(t, @"/Users/[^/\s""]+", "/Users/[name]");
        // Not in the Mac's list: a PC's home folder is C:\Users\name, so that name is taken out the same way.
        t = Replace(t, @"(?i)\\Users\\[^\\/\s""]+", @"\Users\[name]");
        return t;
    }

    private static string Replace(string text, string pattern, string with)
    {
        try
        {
            return Regex.Replace(text, pattern, with, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
