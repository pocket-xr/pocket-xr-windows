using System;

namespace PocketXR.Core;

/// <summary>
/// What each model of glasses shows, so the Space's projection matches the real field of view (Pocket XR's
/// GlassesOptics and GlassesProfile).
/// </summary>
public static class GlassesOptics
{
    /// <summary>
    /// The Aura's vertical field per eye, degrees: the number proven on Mike's Aura with the Steam Deck port and the
    /// first Mac run (16 Nov 2026). Do not change it without a new measurement on the glasses.
    ///
    /// It differs from the Android app, which does not use one fixed number for the Aura: there the projection
    /// follows the glasses' own calibration and their Display size (4.122 by 2.319 m at 4 m at size 1.0, which is
    /// 32.3 degrees tall for a 1920 x 1080 picture, 30.8 at the 3D mode's saved 0.95), and before that XREAL's
    /// published 70 degrees diagonal on the 1920 x 1200 panels (36.9 degrees tall for a 1080 picture). The Mac does
    /// not read the glasses' Display size yet, so it keeps the proven 38.
    /// </summary>
    public const double AuraFovYDeg = 38.0;

    /// <summary>XREAL's published diagonal fields of view, degrees, over a 1920 x 1080 picture per eye.</summary>
    public const double OneDiagonalDeg = 50.0;
    public const double OneProDiagonalDeg = 57.0; // the One Pro and the One Pro+
    public const double AirDiagonalDeg = 46.0;
    /// <summary>
    /// Glasses with no published figure known here (the 1S, VITURE, anything else): the One's, the nearest model
    /// the project has run on.
    /// </summary>
    public const double UnknownDiagonalDeg = 50.0;

    /// <summary>Wider than this (width over height) is two pictures side by side: 32:9 is 3.56, 21:9 ultrawide is 2.37.</summary>
    public const double SbsMinAspect = 3.2;

    /// <summary>
    /// The vertical field of a 16:9 picture whose diagonal spans [diagonalDeg], degrees:
    /// 2 atan(tan(diagonal / 2) * 9 / sqrt(16 * 16 + 9 * 9)).
    /// </summary>
    public static double FovYDeg(double diagonalDeg)
    {
        double halfDiagonal = diagonalDeg * Math.PI / 360;
        double share = 9.0 / Math.Sqrt(337.0);
        return 2 * Math.Atan(Math.Tan(halfDiagonal) * share) * 180 / Math.PI;
    }

    /// <summary>
    /// The published diagonal field for these glasses, degrees; null for the Aura (it has its own number) and for no
    /// glasses.
    /// </summary>
    public static double? DiagonalDeg(Glasses? glasses)
    {
        if (glasses is not Glasses g) return null;
        switch (g.Family)
        {
            case GlassesFamily.Aura:
                return null;
            case GlassesFamily.One:
                // The One Pro and One Pro+ by their USB product, else by name; the One, the 1S (not published: the
                // One's) and any other XREAL glasses taken as One family are 50.
                string name = Glasses.OneFamily.TryGetValue(g.Product, out string? known) ? known : g.Name;
                return name.Contains("Pro") ? OneProDiagonalDeg : OneDiagonalDeg;
            case GlassesFamily.Air:
                return AirDiagonalDeg;
            default:
                return UnknownDiagonalDeg;
        }
    }

    /// <summary>
    /// Each eye's vertical field of view in degrees for these glasses. The Aura: 38 (see `AuraFovYDeg`). Other
    /// models: from their published diagonal field over a 16:9 picture (One 50, One Pro and Pro+ 57, Air family 46,
    /// unknown, 1S and VITURE 50 degrees). No glasses: 38. Only the Aura's has been seen on a Mac; the others are
    /// from published figures, untested.
    /// </summary>
    public static double FovYDeg(Glasses? glasses)
    {
        if (DiagonalDeg(glasses: glasses) is not double diagonal) return AuraFovYDeg;
        return FovYDeg(diagonalDeg: diagonal);
    }

    /// <summary>
    /// Side by side when the picture is at least 3.2 times as wide as tall (the Android rule): 3840 x 1080 is, a
    /// 21:9 ultrawide is not.
    /// </summary>
    public static bool IsSideBySide(int width, int height)
    {
        if (height <= 0) return false;
        return (double)width / (double)height >= SbsMinAspect;
    }
}
