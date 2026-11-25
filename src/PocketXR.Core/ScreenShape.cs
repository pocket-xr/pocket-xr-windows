using System;
using System.Collections.Generic;

namespace PocketXR.Core;

/// <summary>
/// A floating screen is drawn the shape Windows shows it, taken from the picture captured of it, not from what Pocket
/// XR last read of the display. A tester (Pierre Zammit, 0.4.9, 24 Nov 2026) turned a screen to portrait and back: the
/// panel kept the shape it was laid out with whenever the glasses' display did not move too, so a landscape desktop
/// was squeezed into a tall panel, or a portrait one into a wide panel.
/// </summary>
public static class ScreenShape
{
    /// <summary>How far apart two shapes may be (width / height, as a fraction) and still be the same.</summary>
    public const double Tolerance = 0.005;

    /// <summary>
    /// The size a picture captured as its panel is wired ([width] x [height]) has on the desktop, with DXGI's
    /// [rotation] (1 upright, 2 a quarter turn, 3 half, 4 three quarters): a quarter turn swaps the sides.
    /// </summary>
    public static (int Width, int Height) AsShown(int width, int height, int rotation) =>
        rotation is 2 or 4 ? (height, width) : (width, height);

    /// <summary>Whether two shapes (width / height) differ by more than <see cref="Tolerance"/>.</summary>
    public static bool Differs(double aspect, double other) =>
        aspect > 0 && other > 0 && Math.Abs(aspect / other - 1) > Tolerance;

    /// <summary>
    /// [panels] with each one's shape set to its picture's ([shown], desktop sizes, by panel; null or empty where there is
    /// no picture yet), its width as seen kept. [changed] says which panels differed from their picture.
    /// </summary>
    public static PanelPlacement[] Follow(IReadOnlyList<PanelPlacement> panels, IReadOnlyList<(int Width, int Height)?> shown, out bool[] changed)
    {
        var result = new PanelPlacement[panels.Count];
        changed = new bool[panels.Count];
        for (int i = 0; i < panels.Count; i++)
        {
            result[i] = panels[i];
            if (i >= shown.Count || shown[i] is not (int w, int h) || w <= 0 || h <= 0) continue;
            double aspect = (double)w / h;
            if (!Differs(panels[i].Aspect, aspect)) continue;
            result[i].Aspect = aspect;
            changed[i] = true;
        }
        return result;
    }
}

/// <summary>
/// Which display is Windows' main one (the taskbar's and new windows' home), as chosen in Pocket XR's Settings: this
/// PC's own screen, or floating screen 1 to 6 (1 to 3 up to 0.4.10). Asked by a tester (Pierre Zammit, 0.4.9). Windows makes main the
/// display whose top left is at 0,0, so choosing one moves every display by the same amount, keeping how they join.
/// </summary>
public static class MainDisplay
{
    /// <summary>0 this PC's screen; 1 to 6 that floating screen.</summary>
    public const int ThisPc = 0, Max = ScreenCount.Max;

    /// <summary>The picker's words, for [screens] floating screens: this PC first. Past 3 screens the numbers alone
    /// ("This PC", "1" ... "6"), so the picker still fits the Settings window.</summary>
    public static List<string> Titles(int screens)
    {
        int n = Math.Clamp(screens, 0, Max);
        var titles = new List<string> { "This PC" };
        for (int i = 1; i <= n; i++) titles.Add(n > ScreenCount.Earlier ? $"{i}" : $"Screen {i}");
        return titles;
    }

    /// <summary>The words for a choice, for the log.</summary>
    public static string Title(int choice) => choice is >= 1 and <= Max ? $"floating screen {choice}" : "this PC's screen";

    /// <summary>
    /// Which floating screen (from 0) should be main for [choice] with [floating] of them on the desktop, or -1 for this
    /// PC's screen: a floating screen that is not there leaves this PC's screen main.
    /// </summary>
    public static int FloatingIndex(int choice, int floating) => choice >= 1 && choice <= Math.Min(floating, Max) ? choice - 1 : -1;

    /// <summary>The choice that says what Windows has now: [mainFloating] the main floating screen (from 0) or -1, and
    /// [ownIsMain] whether this PC's screen is. Null when something else is main (the glasses' display, another monitor).</summary>
    public static int? ChoiceNow(int mainFloating, bool ownIsMain) =>
        mainFloating is >= 0 and < Max ? mainFloating + 1 : ownIsMain ? ThisPc : null;

    /// <summary>
    /// [places] (top left corners, one per display) moved together so the one at [main] is at 0,0, which is what makes
    /// it Windows' main display. With [main] out of range, unchanged.
    /// </summary>
    public static (int X, int Y)[] AtOrigin(IReadOnlyList<(int X, int Y)> places, int main)
    {
        var result = new (int X, int Y)[places.Count];
        (int dx, int dy) = main >= 0 && main < places.Count ? (-places[main].X, -places[main].Y) : (0, 0);
        for (int i = 0; i < places.Count; i++) result[i] = (places[i].X + dx, places[i].Y + dy);
        return result;
    }
}
