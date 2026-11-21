using System;
using System.Collections.Generic;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>Readability: on changes only the screen count, width and screen moves; off puts exactly those back.</summary>
public static class ReadabilityChecks
{
    public static void Run()
    {
        // The screen's size.
        Check.That(ReadabilityMode.ScreenSize(true) == (1440, 810), "Readability's screen is 1440 x 810");
        Check.That(ReadabilityMode.ScreenSize(false) == (1920, 1080), "otherwise 1920 x 1080");
        Check.That(1440 * 9 == 810 * 16, "the same 16:9 shape");

        // The width: one screen pixel to one glasses pixel.
        Check.That(ReadabilityMode.WidthDeg(null) == 30.0, "with no glasses known, 30 degrees (the tester's)");
        var air = new Glasses("XREAL Air", GlassesFamily.Air, Glasses.Xreal, 0x0424);
        var aura = new Glasses("XREAL Aura", GlassesFamily.Aura, Glasses.Xreal, 0x0439);
        double airW = ReadabilityMode.WidthDeg(air), auraW = ReadabilityMode.WidthDeg(aura);
        Check.That(airW > 30 && airW < 35, $"the Air: about 32 degrees ({airW})");
        Check.That(Math.Abs(auraW - 1440 * GlassesOptics.AuraFovYDeg / 1080) < 0.1, $"the Aura: 1440 px at 1080 px over 38 degrees ({auraW})");
        Check.That(Math.Abs(1080 / GlassesOptics.FovYDeg(air) - 1440 / airW) < 0.2, "a screen pixel covers about one glasses pixel");

        // On, then off: exactly what was there before.
        var s = new SpaceSettings { Screens = 3, WidthDeg = 44, Layout = ArrangeLayout.Stacked, Distance = 1.7, Curve = 0.4 };
        s.ScreenYawDeg = new List<double> { 0, 5.5, -3 };
        s.ScreenPitchDeg = new List<double> { 0, 2, 0 };
        SpaceSettings before = s.Copy();
        ReadabilityMode.TurnOn(s, 32.5);
        Check.That(s.Readability && s.Screens == 1 && s.WidthDeg == 32.5 && !s.ScreensMoved, "on: one screen, its width, in the middle");
        Check.That(s.Layout == ArrangeLayout.Stacked && s.Distance == 1.7 && s.Curve == 0.4, "on: the layout, distance and curve are left alone");
        ReadabilityMode.TurnOn(s, 50);
        Check.That(s.WidthDeg == 32.5 && s.ReadabilityBeforeScreens == 3, "turning it on twice keeps the first 'before'");

        // Kept in the settings file while on (a quit or crash leaves it fully on, and it still comes off cleanly).
        SpaceSettings saved = SpaceSettings.Decode(s.Encode());
        Check.That(saved.Equals(s), "on, it is saved and read back the same");
        Check.That(saved.ReadabilityBeforeScreenYawDeg!.SequenceEqual(new[] { 0, 5.5, -3 }), "with the screens' moves to put back");

        ReadabilityMode.TurnOff(saved);
        Check.That(!saved.Readability && saved.Screens == 3 && saved.WidthDeg == 44, "off: the screen count and width as they were");
        Check.That(saved.ScreenYawDeg.SequenceEqual(new[] { 0, 5.5, -3 }) && saved.ScreenPitchDeg.SequenceEqual(new[] { 0.0, 2, 0 }), "off: the moves as they were");
        Check.That(saved.Equals(before), "off: everything exactly as before it went on");
        Check.That(saved.ReadabilityBeforeScreens == null && saved.ReadabilityBeforeWidthDeg == null, "nothing left over");
        SpaceSettings off = SpaceSettings.Decode(saved.Encode());
        Check.That(off.Equals(before) && !System.Text.Encoding.UTF8.GetString(off.Encode()).Contains("readabilityBefore"), "off, the file holds no 'before'");

        // Off when off does nothing; an older file without the key reads as off.
        var plain = new SpaceSettings();
        ReadabilityMode.TurnOff(plain);
        Check.That(plain.Equals(new SpaceSettings()), "off when off changes nothing");
        Check.That(!new SpaceSettings().Readability, "off by default");
        Check.That(ReadabilityMode.Line(before) == "3 screens, 44° wide, screens moved 0 5.5 -3", $"the log line ({ReadabilityMode.Line(before)})");
    }
}
