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
        Check.That(s.PortraitScreens == 0b100 && !s.IsPortrait(7), "a screen past 3 is not kept");
        Check.That(SpaceSettings.Decode(s.Encode()).IsPortrait(2), "portrait screens are saved");
        Check.That(new SpaceSettings { PortraitScreens = 0xFF }.Clamped.PortraitScreens == 0b111, "only three screens can be portrait");

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
    }
}
