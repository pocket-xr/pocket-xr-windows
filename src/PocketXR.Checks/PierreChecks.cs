using System;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>A tester's list on 0.4.8 (Pierre Zammit, 24 Nov 2026, XBX A01+ on Windows): portrait screens and where this
/// PC's own screen sits. (Taps are in TapChecks.)</summary>
public static class PierreChecks
{
    public static void Run()
    {
        // Portrait screens, by number.
        var s = new SpaceSettings();
        Check.That(s.PortraitScreens == 0 && !s.IsPortrait(0), "no screen is portrait at first");
        s.SetPortrait(1, true);
        Check.That(!s.IsPortrait(0) && s.IsPortrait(1) && !s.IsPortrait(2), "screen 2 turned, the others not");
        s.SetPortrait(2, true);
        s.SetPortrait(1, false);
        Check.That(s.PortraitScreens == 0b100, "screen 3 only");
        s.SetPortrait(7, true);
        Check.That(s.PortraitScreens == 0b100 && !s.IsPortrait(7), "a screen past 6 is not kept");
        Check.That(SpaceSettings.Decode(s.Encode()).IsPortrait(2), "portrait screens are saved");
        Check.That(new SpaceSettings { PortraitScreens = 0xFF }.Clamped.PortraitScreens == 0b111111, "only six screens can be portrait (three up to 0.4.10)");

        // Where this PC's own screen sits: Left (as before: the floating screens to its right, tops level), Below (a
        // laptop under the glasses' screens: they go above it, centred), Right, Above.
        var origins = new[] { (0, 0), (1920, 0), (3840, 0) };
        var sizes = new[] { (1920, 1080), (1920, 1080), (1920, 1080) };
        Check.That(DisplayGrid.Block(origins, sizes) == (5760, 1080), "three screens side by side: 5760 x 1080");
        Check.That(DisplayGrid.Block(new[] { (0, 0), (0, 1080) }, new[] { (1920, 1080), (1920, 1080) }) == (1920, 2160), "stacked: 1920 x 2160");
        Check.That(DisplayGrid.Block(new[] { (0, 0) }, new[] { (1080, 1920) }) == (1080, 1920), "a portrait screen: 1080 x 1920");
        Check.That(DisplayGrid.BlockOrigin(PcSide.Left, 0, 0, 1920, 1080, 5760, 1080) == (1920, 0), "Left: the screens to its right, as before");
        Check.That(DisplayGrid.BlockOrigin(PcSide.Below, 0, 0, 1920, 1080, 5760, 1080) == (-1920, -1080), "Below: the screens above it, centred");
        Check.That(DisplayGrid.BlockOrigin(PcSide.Right, 0, 0, 1920, 1080, 5760, 1080) == (-5760, 0), "Right: the screens to its left");
        Check.That(DisplayGrid.BlockOrigin(PcSide.Above, 0, 0, 1920, 1080, 1920, 1080) == (0, 1080), "Above: the screens under it");
        Check.That(DisplayGrid.GlassesOrigin(PcSide.Left, 0, 0, 1920, 1920) == (-1920, 0), "the glasses' display to its left");
        Check.That(DisplayGrid.GlassesOrigin(PcSide.Right, 0, 0, 1920, 1920) == (1920, 0), "and to its right when the screens are on the left");
        foreach (PcSide side in PcSideExt.All)
            Check.That(PcSideExt.FromRaw(side.Raw()) == side, $"{side} reads back");
        Check.That(PcSideExt.FromRaw("nonsense") == PcSide.Left && new SpaceSettings().PcSide == PcSide.Left, "Left at first and for anything unknown");
        Check.That(SpaceSettings.Decode(new SpaceSettings { PcSide = PcSide.Below }.Encode()).PcSide == PcSide.Below, "the side is saved");

        ShapeFollowsWindows();
        MainDisplayChoice();
    }

    /// <summary>
    /// 0.4.10: a panel takes the shape of the picture Windows gives, not the shape it was laid out with. From Pierre's
    /// log on 0.4.9: Portrait off at 20:39:16 and on again at 20:47:16, the glasses' display not moving either time, so
    /// the Space kept its old layout while the capture changed (1920x1080 upright, then 1920x1080 wired at Rotate90).
    /// </summary>
    private static void ShapeFollowsWindows()
    {
        Check.That(ScreenShape.AsShown(1920, 1080, 1) == (1920, 1080), "an upright picture stands as it is");
        Check.That(ScreenShape.AsShown(1920, 1080, 2) == (1080, 1920), "a quarter-turned picture (Rotate90) stands 1080 x 1920");
        Check.That(ScreenShape.AsShown(1920, 1080, 3) == (1920, 1080), "a half turn keeps its shape");
        Check.That(ScreenShape.AsShown(1920, 1080, 4) == (1080, 1920), "three quarters stands 1080 x 1920");

        double wide = 1920.0 / 1080, tall = 1080.0 / 1920;
        // Laid out tall (portrait on), then Portrait off: Windows gives an upright 1920 x 1080 picture.
        var tallPanel = new[] { new PanelPlacement(yawDeg: 0, widthDeg: 20, aspect: tall, distance: 1.4) };
        PanelPlacement[] drawn = ScreenShape.Follow(tallPanel, new (int, int)?[] { ScreenShape.AsShown(1920, 1080, 1) }, out bool[] changed);
        Check.That(changed[0] && Math.Abs(drawn[0].Aspect - wide) < 1e-9 && drawn[0].WidthDeg == 20,
            "Portrait off with the layout still tall: drawn wide, as Windows has it, at the same width");
        // Laid out wide (portrait off), then Portrait on: Windows gives the picture wired, turned a quarter.
        var widePanel = new[] { new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4) };
        drawn = ScreenShape.Follow(widePanel, new (int, int)?[] { ScreenShape.AsShown(1920, 1080, 2) }, out changed);
        Check.That(changed[0] && Math.Abs(drawn[0].Aspect - tall) < 1e-9, "Portrait on with the layout still wide: drawn tall");
        drawn = ScreenShape.Follow(widePanel, new (int, int)?[] { (1920, 1080) }, out changed);
        Check.That(!changed[0] && drawn[0] == widePanel[0], "the same shape: the panel as laid out");
        drawn = ScreenShape.Follow(widePanel, new (int, int)?[] { null }, out changed);
        Check.That(!changed[0] && drawn[0] == widePanel[0], "no picture yet: the panel as laid out");
        drawn = ScreenShape.Follow(new[] { widePanel[0], widePanel[0] }, new (int, int)?[] { (1920, 1080) }, out changed);
        Check.That(drawn.Length == 2 && !changed[1], "a panel with no capture is left as it is");
        Check.That(!ScreenShape.Differs(wide, 1918.0 / 1080), "a pixel or two is the same shape");
        Check.That(ScreenShape.Differs(wide, 1440.0 / 900), "16:10 is another shape");
    }

    /// <summary>0.4.10: Windows' main display chosen in Pocket XR (asked by Pierre on 0.4.9).</summary>
    private static void MainDisplayChoice()
    {
        Check.That(new SpaceSettings().MainScreen == MainDisplay.ThisPc, "this PC's screen is main at first");
        Check.That(SpaceSettings.Decode(new SpaceSettings { MainScreen = 2 }.Encode()).MainScreen == 2, "the main display is saved");
        Check.That(new SpaceSettings { MainScreen = 9 }.Clamped.MainScreen == 6 && new SpaceSettings { MainScreen = -1 }.Clamped.MainScreen == 0,
            "only this PC or screens 1 to 6");
        Check.That(new SpaceSettings { MainScreen = 1 } != new SpaceSettings(), "a different main display is a change");
        Check.That(string.Join("|", MainDisplay.Titles(2)) == "This PC|Screen 1|Screen 2", "the picker: this PC, then each floating screen there is");
        Check.That(MainDisplay.Titles(9).Count == 7, "at most six floating screens to pick");
        Check.That(MainDisplay.FloatingIndex(0, 3) == -1, "This PC: no floating screen is main");
        Check.That(MainDisplay.FloatingIndex(2, 3) == 1, "Screen 2 is the second floating screen");
        Check.That(MainDisplay.FloatingIndex(3, 1) == -1, "Screen 3 with one floating screen: this PC's screen stays main");
        Check.That(MainDisplay.ChoiceNow(0, false) == 1 && MainDisplay.ChoiceNow(-1, true) == 0 && MainDisplay.ChoiceNow(-1, false) == null,
            "what Windows has, read back: a floating screen, this PC, or something else (the glasses) left alone");

        // Pierre's desktop with Below: the laptop at 0,0 (main), the floating screen above it, the glasses' display out of
        // the way. Making the floating screen main moves every display by the same amount, so they stay joined.
        var places = new[] { (0, 0), (0, -1080), (-1920, 0) }; // laptop, floating screen 1, glasses
        var moved = MainDisplay.AtOrigin(places, 1);
        Check.That(moved[1] == (0, 0), "the chosen screen at 0,0, which makes it main");
        Check.That(moved[0] == (0, 1080) && moved[2] == (-1920, 1080), "the others moved with it, still joined the same way");
        Check.That(MainDisplay.AtOrigin(places, 0).SequenceEqual(places), "already main: nothing moves");
        Check.That(MainDisplay.AtOrigin(places, 7).SequenceEqual(places), "no such display: nothing moves");
        // His 0.4.9 log, 20:46:27: a floating screen was main (set in Windows) and placing the screens beside the laptop
        // left nothing at 0,0, so Windows refused (error 87). Kept at 0,0 the same places are a set Windows takes.
        var arranged = new[] { (0, 1920), (1080 / 2 - 1920 / 2, 0), (-1920, 1920) }; // laptop, the turned screen above it, glasses
        var kept = MainDisplay.AtOrigin(arranged, 1);
        Check.That(kept[1] == (0, 0) && kept.Count(p => p == (0, 0)) == 1, "the main floating screen still at 0,0 after placing");
    }
}
