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
        // The screen's size, as on the Mac: 1440 x 810, then 16:9 sizes Parsec's driver offers.
        Check.That(ScreenLook.FloatingSizes(true)[0] == (1440, 810), "Readability's screen is 1440 x 810 first");
        Check.That(ScreenLook.FloatingSizes(true).All(z => z.Width * 9 == z.Height * 16), "every Readability size is 16:9");
        Check.That(ScreenLook.FloatingSizes(false).SequenceEqual(new[] { (1920, 1080) }), "otherwise 1920 x 1080");

        // The width, as on the Mac: 1440 across 34 degrees, about 42 pixels a degree (the Air's own); 48 usually.
        Check.That(ReadabilityMode.WidthDegrees == 34, "34 degrees wide, the Mac's");
        Check.That(Math.Abs(ReadabilityMode.PixelsPerDegree - 42.35) < 0.1, "about 42 pixels a degree");
        Check.That(ReadabilityMode.WidthFor(1440) == 34, "1440 made: 34 degrees");
        Check.That(ReadabilityMode.WidthFor(1600) == 37.8 && ReadabilityMode.WidthFor(1280) == 30.2, "another size made: as wide again for its size");

        // On, then off: exactly what was there before.
        var s = new SpaceSettings { Screens = 3, WidthDeg = 44, Layout = ArrangeLayout.Stacked, Distance = 1.7, Curve = 0.4 };
        s.ScreenYawDeg = new List<double> { 0, 5.5, -3 };
        s.ScreenPitchDeg = new List<double> { 0, 2, 0 };
        SpaceSettings before = s.Copy();
        ReadabilityMode.TurnOn(s);
        Check.That(s.Readability && s.Screens == 1 && s.WidthDeg == 34 && !s.ScreensMoved, "on: one screen, 34 degrees, in the middle");
        Check.That(s.Layout == ArrangeLayout.Stacked && s.Distance == 1.7 && s.Curve == 0.4, "on: the layout, distance and curve are left alone");
        ReadabilityMode.TurnOn(s, 50);
        Check.That(s.WidthDeg == 34 && s.ReadabilityBeforeScreens == 3, "turning it on twice keeps the first 'before'");

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

        // A file that says on but lost what it kept turns off without changing the screens (the Mac's check).
        var lost = new SpaceSettings { Readability = true, Screens = 1, WidthDeg = 34 };
        ReadabilityMode.TurnOff(lost);
        Check.That(!lost.Readability && lost.Screens == 1 && lost.WidthDeg == 34, "off with nothing kept leaves the screens as they are");

        // Text size, as on the Mac: Standard, 125% and 150%, the screens as wide; on Windows each screen stays 1920 x
        // 1080 pixels and Windows draws it at that scale (the Mac's points: 1536 x 864, 1280 x 720).
        Check.That(new SpaceSettings().TextScale == 100, "text size is Standard by default");
        Check.That(ScreenLook.TextScales.SequenceEqual(new[] { 100, 125, 150 }), "Standard, 125% and 150%");
        Check.That(ScreenLook.Title(100) == "Standard" && ScreenLook.Title(150) == "150%", "their names, as the Mac's");
        Check.That(ScreenLook.PointSize(100) == (1920, 1080) && ScreenLook.PointSize(125) == (1536, 864) && ScreenLook.PointSize(150) == (1280, 720),
                   "the room each gives: 1920 x 1080, 1536 x 864, 1280 x 720 (the Mac's sizes)");
        Check.That(ScreenLook.Scale(false, 150) == 150 && ScreenLook.Scale(true, 150) == 100, "Readability keeps its own size, at 100");
        var odd = new SpaceSettings { TextScale = 140 };
        Check.That(odd.Clamped.TextScale == 150, "a size not offered goes to the nearest");
        odd.TextScale = 10;
        Check.That(odd.Clamped.TextScale == 100, "too small a size goes to Standard");
        Check.That(SpaceSettings.Decode(new SpaceSettings { TextScale = 125 }.Encode()).TextScale == 125, "text size is saved and read back");
        var textOn = new SpaceSettings { TextScale = 150 };
        ReadabilityMode.TurnOn(textOn);
        ReadabilityMode.TurnOff(textOn);
        Check.That(textOn.TextScale == 150, "Readability on and off leaves the text size alone");

        // Windows' scale steps, counted from the display's recommended scale (a virtual display: 100%, steps 0 to 3).
        Check.That(ScreenLook.RelFor(125, 0, 3) == 1 && ScreenLook.RelFor(150, 0, 3) == 2 && ScreenLook.RelFor(100, 0, 3) == 0, "steps from a 100% recommendation");
        Check.That(ScreenLook.RelFor(100, -1, 4) == -1 && ScreenLook.RelFor(150, -1, 4) == 1, "steps from a 125% recommendation");
        Check.That(ScreenLook.RelFor(500, 0, 3) == 3, "held to the display's highest step");
        Check.That(ScreenLook.RelFor(110, 0, 3) == null, "a scale Windows does not offer is not asked for");
        Check.That(ScreenLook.PercentFor(2, 0) == 150 && ScreenLook.PercentFor(0, -1) == 125, "a step back to its percent");

        // The screens a phone streams: Readability makes them three quarters of the phone's size, else as asked.
        Check.That(ScreenLook.HostSize(1784, 976, false) == (1784, 976), "a phone's screen as asked");
        Check.That(ScreenLook.HostSize(1784, 976, true) == (1338, 732), "with Readability, three quarters of it");
        Check.That(ScreenLook.HostSize(1920, 1080, true) == (1440, 810), "1920 x 1080 becomes 1440 x 810");

        // Windows kept on their screen (the control panel on a 1080p screen at 125 %, its taskbar 60 px: Pierre Zammit's
        // "Second screen only" case, 22 Nov 2026).
        Check.That(Math.Abs(WindowFit.MaxHeight(0, 1020, 1.25) - (816 - 12)) < 0.01, "the control panel's height is held to its own screen at its scale");
        Check.That(WindowFit.MaxHeight(0, 300, 1.0) == 320, "never under 320");
        Check.That(WindowFit.Place(100, 400, 550, 1100, 0, 0, 1920, 1020) == (100, 0), "a window taller than the screen starts at its top");
        Check.That(WindowFit.Place(100, 500, 550, 700, 0, 0, 1920, 1020) == (100, 320), "a window running off the bottom is moved up");
        Check.That(WindowFit.Place(1700, 100, 550, 700, 0, 0, 1920, 1020) == (1370, 100), "and off the right, moved left");
        Check.That(WindowFit.Place(-1500, 100, 550, 700, -1920, 0, 0, 1080) == (-1500, 100), "a window already on a screen left of the main one stays");
        Check.That(WindowFit.Place(2000, 1200, 550, 700, 1920, 0, 3840, 1080) == (2000, 380), "on a second screen, kept on that screen");
    }
}
