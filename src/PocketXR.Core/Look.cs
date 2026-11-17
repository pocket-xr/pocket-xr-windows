using System;

namespace PocketXR.Core;

/// <summary>
/// Pocket XR's look, "Light on glass", as numbers (the Android app's Look.kt): always dark, one accent, light for
/// focus and never shadow. Colours are red, green, blue, alpha, each 0 to 1.
/// </summary>
public record struct LookColor
{
    public double R, G, B, A;

    public LookColor(double r, double g, double b, double a = 1)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>From 0xRRGGBB, with [alpha] 0 to 1.</summary>
    public LookColor(uint hex, double alpha = 1)
        : this((double)((hex >> 16) & 0xFF) / 255, (double)((hex >> 8) & 0xFF) / 255, (double)(hex & 0xFF) / 255, alpha)
    {
    }

    public readonly LookColor Alpha(double a) => new LookColor(R, G, B, a);
}

public static class Look
{
    public static readonly LookColor Page = new(hex: 0x0C0D10);
    public static readonly LookColor Pad = new(hex: 0x15171C);
    public static readonly LookColor Card = new(hex: 0x1A1D24);
    /// <summary>The only accent: progress, the main button, ticks, the focus halo.</summary>
    public static readonly LookColor Accent = new(hex: 0x5A93FF);
    public static readonly LookColor AccentDeep = new(hex: 0x3A6FD8);
    /// <summary>"You are holding this."</summary>
    public static readonly LookColor Held = new(hex: 0x8FB6FF);
    public static readonly LookColor Ink = new(1, 1, 1, 0.92);
    public static readonly LookColor Ink2 = new(1, 1, 1, 0.64);
    public static readonly LookColor Ink3 = new(1, 1, 1, 0.40);
    /// <summary>Hairlines.</summary>
    public static readonly LookColor Rim = new(1, 1, 1, 0.14);
    public static readonly LookColor RimFocus = new(1, 1, 1, 0.32);
    public static readonly LookColor GlassHover = new(hex: 0x2A3142, alpha: 0.94);
    // Glass in the pictures: a gradient top to bottom, raised glass, neutral controls, a media fill.
    public static readonly LookColor GlassTop = new(hex: 0x242A39);
    public static readonly LookColor GlassBottom = new(hex: 0x181C26);
    public static readonly LookColor GlassRaised = new(hex: 0x1E2330);
    public static readonly LookColor Control = new(hex: 0x2A3142);
    public static readonly LookColor Media = new(hex: 0x2B3550);
    public static readonly LookColor Good = new(hex: 0x4CD98A);
    public static readonly LookColor Warn = new(hex: 0xF2B84B);

    // Motion: calm and exact, no overshoot anywhere. Seconds.
    public const double Quick = 0.12;
    public const double Enter = 0.28;
    public const double Exit = 0.16;
    public const double Beat = 0.60;
    /// <summary>Cubic bezier control points (x1, y1, x2, y2).</summary>
    public static readonly (double, double, double, double) EaseEnter = (0.2, 0.0, 0.0, 1.0);
    public static readonly (double, double, double, double) EaseExit = (0.3, 0.0, 1.0, 1.0);
    public static readonly (double, double, double, double) EaseInOut = (0.4, 0.0, 0.2, 1.0);
}

public static partial class GlowColorExt
{
    /// <summary>The focused screen's outline colour.</summary>
    public static LookColor Color(this GlowColor value) => value switch
    {
        GlowColor.Blue => Look.Accent,
        GlowColor.White => new LookColor(1, 1, 1),
        GlowColor.Purple => new LookColor(hex: 0xA77BFF),
        GlowColor.Pink => new LookColor(hex: 0xFF7AB8),
        GlowColor.Green => new LookColor(hex: 0x4CD98A),
        GlowColor.Amber => new LookColor(hex: 0xF2B84B),
        _ => new LookColor(hex: 0xFF6B5E),
    };

    public static string Title(this GlowColor value) => LookText.Capitalised(value.RawValue());
}

public static partial class EdgeFadeExt
{
    /// <summary>
    /// Fade widths in pixels of a 1920 wide eye: the outer side, the inner side (toward the nose in 3D), top and bottom.
    /// </summary>
    public static (double Outer, double Inner, double Vertical) Widths(this EdgeFade value) => value switch
    {
        EdgeFade.Off => (0, 0, 0),
        EdgeFade.Soft => (96, 48, 48),
        _ => (192, 96, 96),
    };

    public static string Title(this EdgeFade value) => LookText.Capitalised(value.RawValue());
}

public static partial class TrackingModeExt
{
    public static string Title(this TrackingMode value) => value == TrackingMode.BodyAnchor ? "Body Anchor" : "Smooth Follow";

    public static string Detail(this TrackingMode value) =>
        value == TrackingMode.BodyAnchor
            ? "Screens stay where they are in the room as you turn your head."
            : "Screens drift after your head, so they are always nearly in front of you.";
}

internal static class LookText
{
    /// <summary>rawValue.prefix(1).uppercased() + rawValue.dropFirst()</summary>
    internal static string Capitalised(string raw) =>
        raw.Length == 0 ? raw : raw.Substring(0, 1).ToUpperInvariant() + raw.Substring(1);
}
