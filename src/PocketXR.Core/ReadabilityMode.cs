using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// Readability, as on the Mac (SpaceSettings.setReadability): one screen at 1440 x 810 (16:9), shown
/// [WidthDegrees] wide, so text is larger and drawn about one screen pixel to one glasses pixel. Glasses with a 1920
/// pixel picture across about 46 degrees (the XREAL Air; the 1S and One are close) show about 42 pixels a degree; 1440
/// across 34 degrees is 42 a degree. The usual 1920 across 40 degrees is 48: text smaller, and squeezed. After an idea
/// by Dmitriy Cherdantsev, a Pocket XR for Mac tester. Untested on glasses.
///
/// On Windows the screen's size depends on the virtual display driver: Parsec's offers a list of sizes, and 1440 x 810
/// is not on it (its list has 1600 x 900 and 1280 x 720), so the first size the driver takes is used, and the width is
/// then set from it, still about 42 pixels a degree (ScreenLook.ReadabilitySizes, WidthFor).
///
/// On, it changes only the screen count (1), the screens' width, and screens moved by hand (back to the middle), and
/// keeps what they were in the settings; off puts exactly those back. Kept in the settings file, so quitting or a
/// crash with it on leaves it fully on, and it comes off cleanly later.
/// </summary>
public static class ReadabilityMode
{
    public const int Width = 1440, Height = 810;
    /// <summary>The Mac's readabilityWidthDeg: 1440 px at about 42 px a degree.</summary>
    public const double WidthDegrees = 34.0;
    /// <summary>Screen pixels a degree with Readability on: 1440 across 34 degrees.</summary>
    public const double PixelsPerDegree = Width / WidthDegrees;

    /// <summary>The width as seen for a Readability screen [widthPx] wide: about 42 pixels a degree.</summary>
    public static double WidthFor(int widthPx) =>
        Math.Clamp(Math.Round(widthPx / PixelsPerDegree, 1), SpaceSettings.WidthRange.LowerBound, SpaceSettings.WidthRange.UpperBound);

    /// <summary>Turns it on at [widthDeg], keeping what it changes. Nothing if it is on already.</summary>
    public static void TurnOn(SpaceSettings s, double widthDeg = WidthDegrees)
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

    /// <summary>Turns it off: the screen count, width and screen moves as they were before it went on. A file that
    /// says on but lost what it kept turns off without changing the screens.</summary>
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

/// <summary>
/// The size each screen is made at and the Windows scale it is shown at: Readability's, or the Text size's. As on the
/// Mac (SpaceSettings.textScale): Text size 100, 125 or 150 percent draws everything larger on each screen, at the same
/// width as seen. The Mac makes each screen smaller in points (1536 x 864, 1280 x 720) and HiDPI; Windows' own way is
/// the same thing: the screen stays 1920 x 1080 pixels and Windows draws it at 125 or 150 percent (its per-display
/// scale), so text is larger and every pixel is still there, so it stays sharp. Readability uses its own size, at 100.
/// </summary>
public static class ScreenLook
{
    /// <summary>The text sizes offered, percent.</summary>
    public static readonly int[] TextScales = { 100, 125, 150 };
    public static readonly (int Width, int Height) NormalSize = (1920, 1080);

    /// <summary>Readability's sizes to try, best first: 1440 x 810, then 16:9 sizes Parsec's driver offers.</summary>
    public static readonly (int Width, int Height)[] ReadabilitySizes = { (1440, 810), (1600, 900), (1280, 720) };

    /// <summary>
    /// The size to use for a screen wanted at [width] x [height] from the sizes its driver [offered]: that size if
    /// offered, else the nearest offered one of the same shape (within 1%, closest in area), else null (keep what it
    /// has; the stream is scaled to the size asked). A phone asks for 1920 x 1080, 1920 x 1200 or 2560 x 1600 (Pocket
    /// XR for Android 1.6.14's Screen size) as well as its own window's shape.
    /// </summary>
    public static (int Width, int Height)? Offered(int width, int height, IEnumerable<(int Width, int Height)> offered)
    {
        var list = offered.ToList();
        if (list.Contains((width, height))) return (width, height);
        double shape = (double)width / Math.Max(1, height);
        long area = (long)width * height;
        var same = list.Where(o => o.Height > 0 && Math.Abs((double)o.Width / o.Height - shape) / shape < 0.01)
            .OrderBy(o => Math.Abs((long)o.Width * o.Height - area)).ThenByDescending(o => o.Width).ToList();
        return same.Count > 0 ? same[0] : null;
    }

    /// <summary>"Standard", "125%", "150%".</summary>
    public static string Title(int scale) => scale == 100 ? "Standard" : $"{scale}%";

    /// <summary>The Mac's size in points for [scale] percent (1920 x 1080 over it, 16:9, a multiple of 16 wide): the
    /// space each screen has for windows, in Windows' own units at that scale.</summary>
    public static (int Width, int Height) PointSize(int scale)
    {
        double s = Math.Clamp(scale, 100, 400) / 100.0;
        int w = (int)Math.Round(NormalSize.Width / s / 16) * 16;
        return (w, w * 9 / 16);
    }

    /// <summary>A saved text size brought to the nearest one offered.</summary>
    public static int Nearest(int scale) => TextScales.OrderBy(t => Math.Abs(t - scale)).First();

    /// <summary>The floating screens' sizes to try, best first.</summary>
    public static (int Width, int Height)[] FloatingSizes(bool readability) =>
        readability ? ReadabilitySizes : new[] { NormalSize };

    /// <summary>The Windows scale a screen is shown at: the text size, or 100 with Readability on.</summary>
    public static int Scale(bool readability, int textScale) => readability ? 100 : Nearest(textScale);

    /// <summary>
    /// A screen made for a phone streaming this PC ([width] x [height], the phone's window): with Readability on,
    /// three quarters of it (as 1440 is of 1920, even sizes), drawn larger and scaled up for the stream; else as asked.
    /// </summary>
    public static (int Width, int Height) HostSize(int width, int height, bool readability)
    {
        if (!readability) return (width, height);
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        return (Even(width * 0.75), Even(height * 0.75));
    }

    // Windows' per-display scale, as its Settings set it: steps of these percents, counted from the display's
    // recommended one (DisplayConfigGetDeviceInfo/SetDeviceInfo, types -3 and -4, not in Windows' headers; as the SetDPI
    // tool and others use them).
    public static readonly int[] WindowsScales = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// <summary>The step to ask for [percent], given the display's lowest step ([minRel], at most 0, so its recommended
    /// scale is WindowsScales[-minRel]) and highest ([maxRel]). Null when [percent] is not a Windows scale.</summary>
    public static int? RelFor(int percent, int minRel, int maxRel)
    {
        int index = Array.IndexOf(WindowsScales, percent);
        if (index < 0) return null;
        return Math.Clamp(index - Math.Abs(minRel), minRel, maxRel);
    }

    /// <summary>The percent of step [rel] on a display whose lowest step is [minRel].</summary>
    public static int PercentFor(int rel, int minRel)
    {
        int index = Math.Clamp(Math.Abs(minRel) + rel, 0, WindowsScales.Length - 1);
        return WindowsScales[index];
    }
}
