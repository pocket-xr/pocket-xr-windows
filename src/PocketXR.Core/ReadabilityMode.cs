using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// Readability: larger, sharp text on 1080p glasses. One floating screen, drawn at 1440 x 810 (16:9, three quarters of
/// 1920 x 1080, so everything on it is a third bigger), shown just wide enough that one of its pixels covers about one
/// of the glasses' pixels. After a Mac tester's change (Dmitriy Cherdantsev, November 2026: one 1440 x 810 screen about
/// 30 degrees wide), for every model. Untested on glasses.
///
/// Why the width is worked out here rather than 30 degrees: Windows' floating screens are 1920 px across 40 degrees,
/// 48 px a degree, which 1440 px across 30 degrees equals, so on a PC 30 degrees would make text no bigger. The width
/// is 1440 px over the glasses' own pixels a degree (1080 rows over each eye's vertical view, GlassesOptics): about 32
/// degrees on the XREAL Air, about 51 on the Aura, 30 when the glasses are not known.
///
/// On, it changes only the screen count (1), the screens' width, and screens moved by hand (back to the middle), and
/// keeps what they were in the settings; off puts exactly those back. Kept in the settings file, so quitting or a
/// crash with it on leaves it fully on, and it comes off cleanly later.
/// </summary>
public static class ReadabilityMode
{
    public const int Width = 1440, Height = 810;
    /// <summary>The width when the glasses are not known (the tester's).</summary>
    public const double DefaultWidthDeg = 30.0;
    private const double GlassesRows = 1080.0;

    /// <summary>The screen's width in degrees for [glasses]: 1440 px at the glasses' own pixels a degree.</summary>
    public static double WidthDeg(Glasses? glasses)
    {
        if (glasses == null) return DefaultWidthDeg;
        double perDegree = GlassesRows / GlassesOptics.FovYDeg(glasses);
        double w = Math.Round(Width / perDegree, 1);
        return Math.Clamp(w, SpaceSettings.WidthRange.LowerBound, SpaceSettings.WidthRange.UpperBound);
    }

    /// <summary>The floating screens' size: 1440 x 810 with Readability on, else 1920 x 1080.</summary>
    public static (int Width, int Height) ScreenSize(bool on) => on ? (Width, Height) : (1920, 1080);

    /// <summary>Turns it on at [widthDeg], keeping what it changes. Nothing if it is on already.</summary>
    public static void TurnOn(SpaceSettings s, double widthDeg)
    {
        if (s.Readability) return;
        s.ReadabilityBeforeScreens = s.Screens;
        s.ReadabilityBeforeWidthDeg = s.WidthDeg;
        s.ReadabilityBeforeScreenYawDeg = new List<double>(s.ScreenYawDeg);
        s.ReadabilityBeforeScreenPitchDeg = new List<double>(s.ScreenPitchDeg);
        s.Screens = 1;
        s.WidthDeg = Math.Clamp(widthDeg, SpaceSettings.WidthRange.LowerBound, SpaceSettings.WidthRange.UpperBound);
        s.ScreenYawDeg = new List<double>();
        s.ScreenPitchDeg = new List<double>();
        s.Readability = true;
    }

    /// <summary>Turns it off: the screen count, width and screen moves as they were before it went on.</summary>
    public static void TurnOff(SpaceSettings s)
    {
        if (!s.Readability) return;
        if (s.ReadabilityBeforeScreens is int screens) s.Screens = screens;
        if (s.ReadabilityBeforeWidthDeg is double w) s.WidthDeg = w;
        if (s.ReadabilityBeforeScreenYawDeg is List<double> yaw) s.ScreenYawDeg = new List<double>(yaw);
        if (s.ReadabilityBeforeScreenPitchDeg is List<double> pitch) s.ScreenPitchDeg = new List<double>(pitch);
        s.ReadabilityBeforeScreens = null;
        s.ReadabilityBeforeWidthDeg = null;
        s.ReadabilityBeforeScreenYawDeg = null;
        s.ReadabilityBeforeScreenPitchDeg = null;
        s.Readability = false;
    }

    /// <summary>What Readability touches, for the log: "2 screens, 40° wide, screens moved 0/0".</summary>
    public static string Line(SpaceSettings s) =>
        $"{s.Screens} screen{(s.Screens == 1 ? "" : "s")}, {s.WidthDeg.ToString("0.#", CultureInfo.InvariantCulture)}° wide, " +
        $"screens moved {(s.ScreensMoved ? string.Join(" ", s.ScreenYawDeg.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture))) : "none")}";
}
