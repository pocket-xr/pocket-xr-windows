using System;
using System.Collections.Generic;
using System.Linq;

namespace PocketXR.Core;

/// <summary>A USB device as the PC lists it.</summary>
public struct UsbDeviceInfo : IEquatable<UsbDeviceInfo>
{
    public int Vendor;
    public int Product;
    public int[] InterfaceClasses;
    public string Name;

    public UsbDeviceInfo(int vendor, int product, IReadOnlyList<int>? interfaceClasses = null, string name = "")
    {
        Vendor = vendor;
        Product = product;
        InterfaceClasses = interfaceClasses != null ? interfaceClasses.ToArray() : Array.Empty<int>();
        Name = name;
    }

    public static bool operator ==(UsbDeviceInfo a, UsbDeviceInfo b) =>
        a.Vendor == b.Vendor && a.Product == b.Product && (a.Name ?? "") == (b.Name ?? "")
        && (a.InterfaceClasses ?? Array.Empty<int>()).SequenceEqual(b.InterfaceClasses ?? Array.Empty<int>());

    public static bool operator !=(UsbDeviceInfo a, UsbDeviceInfo b) => !(a == b);
    public readonly bool Equals(UsbDeviceInfo other) => this == other;
    public override readonly bool Equals(object? obj) => obj is UsbDeviceInfo d && this == d;
    public override readonly int GetHashCode() => HashCode.Combine(Vendor, Product, Name ?? "");
}

/// <summary>
/// Which family glasses belong to. (Glasses.Family on the Mac, an enum inside Glasses; C# cannot have a type and a
/// member of one name in a struct, so it stands beside it.)
/// </summary>
public enum GlassesFamily
{
    Aura,
    One,
    Air,
    Viture,
    Other,
}

public static class GlassesFamilyExt
{
    public static string RawValue(this GlassesFamily value) => value switch
    {
        GlassesFamily.Aura => "aura",
        GlassesFamily.One => "one",
        GlassesFamily.Air => "air",
        GlassesFamily.Viture => "viture",
        _ => "other",
    };

    public static GlassesFamily? FromRawValue(string? raw) => raw switch
    {
        "aura" => GlassesFamily.Aura,
        "one" => GlassesFamily.One,
        "air" => GlassesFamily.Air,
        "viture" => GlassesFamily.Viture,
        "other" => GlassesFamily.Other,
        _ => null,
    };
}

/// <summary>
/// Which glasses are plugged in, and what Pocket XR can do with them. The model rules are the Steam Deck's
/// glasses.py, which follows Pocket XR's GlassesProfile, HeadImuTransport and SteadyMode.
/// </summary>
public record struct Glasses
{
    public const int Xreal = 0x3318;
    public const int Viture = 0x35CA;
    public static readonly IReadOnlySet<int> Aura = new HashSet<int> { 0x0439, 0x043A };
    public static readonly IReadOnlyDictionary<int, string> OneFamily = new Dictionary<int, string>
    {
        [0x0435] = "XREAL One Pro", [0x0436] = "XREAL One Pro", [0x0437] = "XREAL One", [0x0438] = "XREAL One",
        [0x043D] = "XREAL 1S", [0x043E] = "XREAL 1S", [0x0443] = "XREAL One Pro+", [0x0444] = "XREAL One Pro+",
    };
    public const int VitureBeast = 0x1211;
    public const int VitureMicrophone = 0x1102;
    public static readonly IReadOnlyDictionary<int, string> AirImu = new Dictionary<int, string>
    {
        [0x0424] = "XREAL Air", [0x0428] = "XREAL Air 2", [0x0432] = "XREAL Air 2 Pro", [0x0426] = "XREAL Air 2 Ultra",
    };
    public static readonly IReadOnlySet<int> AirFamily = new HashSet<int>
    {
        0x0423, 0x0424, 0x0425, 0x0426, 0x0427, 0x0428, 0x0431, 0x0432, 0x043F, 0x0440, 0x0441, 0x0442,
    };

    public string Name;
    public GlassesFamily Family;
    public int Vendor;
    public int Product;
    /// <summary>An XREAL Eye (or any XREAL camera) seen beside One-family glasses.</summary>
    public bool Eye;

    public Glasses(string name, GlassesFamily family, int vendor, int product, bool eye = false)
    {
        Name = name;
        Family = family;
        Vendor = vendor;
        Product = product;
        Eye = eye;
    }

    /// <summary>Head tracking over the glasses' USB network (169.254.2.1:52998): the Aura and the One family.</summary>
    public readonly bool NetworkImu => Family == GlassesFamily.Aura || Family == GlassesFamily.One;

    /// <summary>XREAL's control link on 52999: settings Pocket XR can read and set.</summary>
    public readonly bool ControlLink => Family == GlassesFamily.Aura || Family == GlassesFamily.One;

    /// <summary>Glasses whose menu settings Pocket XR reads and puts right on every link: the Aura only.</summary>
    public readonly bool Managed => Family == GlassesFamily.Aura;

    public readonly bool IsBeast => Family == GlassesFamily.Viture && Product == VitureBeast;

    /// <summary>The One family and the VITURE Beast send x right, y down, z front; the Aura x right, y up, z back.</summary>
    public readonly HeadFusion.Axes Axes => Family == GlassesFamily.One || IsBeast ? HeadFusion.Axes.One : HeadFusion.Axes.Aura;

    /// <summary>
    /// Head tracking the Mac app reads without asking: the network stream. The Air family and VITURE (USB HID, [hidImu])
    /// are read only when the app's own setting says so, as they are untested on a Mac. (The Mac's name, kept so the
    /// two read side by side.)
    /// </summary>
    public readonly bool TrackingOnMac => NetworkImu;

    /// <summary>A One family model Pocket XR knows by its USB product id (not other XREAL glasses merely taken as One family).</summary>
    public readonly bool KnownOne => Family == GlassesFamily.One && OneFamily.ContainsKey(Product);

    /// <summary>
    /// Glasses that are offered Steady mode: the Aura and the One series (Pocket XR's SteadyMode.offeredOn). Tested
    /// on the Aura only; on the One series a refusal only ends the change. Other XREAL glasses taken as One family
    /// are left out: their settings are not known.
    /// </summary>
    public readonly bool SteadyCapable => Family == GlassesFamily.Aura || KnownOne;

    /// <summary>Glasses that can hold Steady mode in 6DoF: the Aura, or the One series with the XREAL Eye (SteadyMode.sixDofOn).</summary>
    public readonly bool SixDofCapable => Family == GlassesFamily.Aura || (KnownOne && Eye);

    /// <summary>
    /// Glasses whose 6DoF hold Pocket XR turns on and off itself (Spatial Anchor, 10405): the Aura only. On the One
    /// series with the Eye the user turns 6DoF on in the glasses' own menu (SteadyMode.appAnchor).
    /// </summary>
    public readonly bool AppAnchor => Family == GlassesFamily.Aura;

    /// <summary>
    /// Head tracking over USB HID: the Air family models whose stream is known (the Air, Air 2, Air 2 Pro and Air 2
    /// Ultra), and VITURE glasses (experimental). None of them has been run on a Mac.
    /// </summary>
    public readonly bool HidImu => (Family == GlassesFamily.Air && AirImu.ContainsKey(Product)) || Family == GlassesFamily.Viture;

    /// <summary>
    /// Finds the glasses among [devices], in the Deck's order: the Aura, the One family, the Air family, VITURE, then any
    /// other XREAL device with a network interface (taken as One family, as Pocket XR does).
    /// </summary>
    public static Glasses? Detect(IReadOnlyList<UsbDeviceInfo> devices)
    {
        bool eye = devices.Any(d => d.Vendor == Xreal && d.InterfaceClasses.Contains(0x0E) && !Aura.Contains(d.Product));
        if (First(devices, d => d.Vendor == Xreal && Aura.Contains(d.Product)) is UsbDeviceInfo aura)
        {
            return new Glasses(name: "XREAL Aura", family: GlassesFamily.Aura, vendor: aura.Vendor, product: aura.Product);
        }
        if (First(devices, d => d.Vendor == Xreal && OneFamily.ContainsKey(d.Product)) is UsbDeviceInfo one)
        {
            return new Glasses(name: OneFamily[one.Product], family: GlassesFamily.One, vendor: one.Vendor, product: one.Product, eye: eye);
        }
        if (First(devices, d => d.Vendor == Xreal && AirFamily.Contains(d.Product)) is UsbDeviceInfo air)
        {
            string airName = AirImu.TryGetValue(air.Product, out string? known)
                ? known
                : (air.Name.Length == 0 ? "XREAL Air" : air.Name);
            return new Glasses(name: airName, family: GlassesFamily.Air, vendor: air.Vendor, product: air.Product);
        }
        List<UsbDeviceInfo> vit = devices.Where(d => d.Vendor == Viture && d.Product != VitureMicrophone).ToList();
        if ((First(vit, d => d.Product == VitureBeast) ?? First(vit, _ => true)) is UsbDeviceInfo viture)
        {
            return new Glasses(
                name: viture.Name.Length == 0 ? "VITURE glasses" : viture.Name, family: GlassesFamily.Viture,
                vendor: viture.Vendor, product: viture.Product);
        }
        if (First(devices, d => d.Vendor == Xreal && d.InterfaceClasses.Contains(0x02)) is UsbDeviceInfo other)
        {
            return new Glasses(
                name: other.Name.Length == 0 ? "XREAL glasses" : other.Name, family: GlassesFamily.One,
                vendor: other.Vendor, product: other.Product, eye: eye);
        }
        return null;
    }

    /// <summary>Swift's first(where:): the first device that passes, or null.</summary>
    private static UsbDeviceInfo? First(IReadOnlyList<UsbDeviceInfo> devices, Func<UsbDeviceInfo, bool> passes)
    {
        foreach (UsbDeviceInfo d in devices)
        {
            if (passes(d)) return d;
        }
        return null;
    }

    /// <summary>The glasses' display among the PC's screens, by name ("XREAL AURA", "XREAL One", "VITURE ..."), else null.</summary>
    public static bool IsGlassesScreenName(string name)
    {
        string n = name.ToUpperInvariant();
        return n.Contains("XREAL") || n.Contains("NREAL") || n.Contains("VITURE") || n.Contains("ROKID")
            || n.Contains("RAYNEO");
    }
}
