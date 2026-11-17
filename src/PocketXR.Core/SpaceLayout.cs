using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PocketXR.Core;

/// <summary>
/// Where one screen sits in the Space: a panel facing the head, at [distance] metres, turned [yawDeg] (left
/// positive) and [pitchDeg] (up positive), [widthDeg] wide. Its height follows the screen's aspect. [curve] bends it
/// round a vertical cylinder as a curved monitor (0 flat, 1 every column as far from the head, up to 2.5; PanelCurve).
/// </summary>
public partial record struct PanelPlacement
{
    public double YawDeg;
    public double PitchDeg;
    public double WidthDeg;
    public double Aspect; // width / height
    public double Distance;
    /// <summary>0 flat ... 2.5 (PanelCurve.MaxCurve). Its angular width stays [widthDeg] at any curve.</summary>
    public double Curve;

    /// <summary>
    /// The Mac's order and defaults: [pitchDeg] may be left out (0) when the arguments after it are given by name,
    /// as in new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: a, distance: 1.4).
    /// </summary>
    public PanelPlacement(
        double yawDeg, [Optional, DefaultParameterValue(0.0)] double pitchDeg, double widthDeg, double aspect,
        double distance, double curve = 0)
    {
        YawDeg = yawDeg;
        PitchDeg = pitchDeg;
        WidthDeg = widthDeg;
        Aspect = aspect;
        Distance = distance;
        Curve = curve;
    }

    /// <summary>Width and height in metres at its distance (the flat screen's; a curved one keeps the height).</summary>
    public readonly Vec2 SizeMetres
    {
        get
        {
            double w = 2 * Distance * Math.Tan(WidthDeg * Math.PI / 360);
            return new Vec2(w, w / Aspect);
        }
    }

    /// <summary>Its height as seen from the head, degrees (up its middle).</summary>
    public readonly double HeightDeg => SpaceLayout.HeightDeg(widthDeg: WidthDeg, aspect: Aspect);

    /// <summary>The turn that puts the screen in its place: yaw about the up axis, then pitch about its own right axis.</summary>
    public readonly Quat Turn => Quat.AboutY(YawDeg * Math.PI / 180) * Quat.AboutX(PitchDeg * Math.PI / 180);

    /// <summary>
    /// Model matrix: a unit quad (-0.5...0.5) scaled to the panel, pushed out to its distance, turned into place.
    /// This is the flat screen; a curved one is drawn from `Mesh(columns)`.
    /// </summary>
    public readonly Mat4 ModelMatrix
    {
        get
        {
            Vec2 s = SizeMetres;
            Mat4 scale = Mat4.Diagonal(new Vec4f((float)s.X, (float)s.Y, 1, 1));
            Mat4 push = Mat4.Identity;
            push.C3 = new Vec4f(0, 0, (float)(-Distance), 1);
            Mat4 turn = SpaceMath.Rotation(Quat.AboutY(YawDeg * Math.PI / 180) * Quat.AboutX(PitchDeg * Math.PI / 180));
            return turn * push * scale;
        }
    }
}

/// <summary>
/// The Space's layout and settings, as Pocket XR's PanelLayout has them (numbers from the Deck's space.py), and the
/// premade arrangements of Pocket XR's ArrangeLayouts for 1 to 3 screens.
/// </summary>
public static class SpaceLayout
{
    public const double DefaultDistance = 1.4;
    public const double DefaultWidthDeg = 40.0;
    public const double GapDeg = 2.0;
    public const double MinDistance = 0.5;
    public const double MaxDistance = 6.0;
    public const double MinWidthDeg = 15.0;
    public const double MaxWidthDeg = 90.0;

    /// <summary>No arrangement reaches further up or down than this, degrees, so every screen stays in a comfortable range.</summary>
    public const double MaxPitchDeg = 35.0;
    /// <summary>Main + side: the one side screen's width as a share of the main screen's (Pocket XR: 1.0 m beside 1.8 m).</summary>
    public const double SideShare = 0.6;
    /// <summary>
    /// Main + two stacked: each side screen's width as a share of the main screen's (Pocket XR: 0.9 m beside 1.8 m),
    /// so the column of two 16:9 screens is about as tall as the main screen.
    /// </summary>
    public const double SideShareTwo = 0.5;
    /// <summary>
    /// Theater: Pocket XR's big screen is 4.2 m wide, 5 m away, for the default width of 40 degrees. It grows and
    /// shrinks with the width setting (times widthDeg / 40).
    /// </summary>
    public const double TheaterWidthMetres = 4.2;
    public const double TheaterDistance = 5.0;
    /// <summary>Theater: the other screens' width as a share of the width setting, at the user's distance.</summary>
    public const double TheaterFlankShare = 0.6;

    /// <summary>
    /// [count] screens side by side on an arc around the head, the middle one straight ahead. Each angular width is
    /// [widthDeg]; [aspects] gives each screen's width / height.
    /// </summary>
    public static PanelPlacement[] Row(IReadOnlyList<double> aspects, double widthDeg, double distance, double gapDeg = GapDeg)
    {
        int n = aspects.Count;
        if (n <= 0) return Array.Empty<PanelPlacement>();
        double step = widthDeg + gapDeg;
        double first = step * (double)(n - 1) / 2; // the leftmost, left positive
        var result = new PanelPlacement[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = new PanelPlacement(yawDeg: first - step * (double)i, widthDeg: widthDeg, aspect: aspects[i], distance: distance);
        }
        return result;
    }

    /// <summary>The middle of the row, for a recentre: 0 with the middle screen ahead.</summary>
    public static double MiddleYawDeg(IReadOnlyList<PanelPlacement> panels)
    {
        if (panels.Count == 0) return 0;
        double a = panels[0].YawDeg;
        double b = panels[panels.Count - 1].YawDeg;
        return (a + b) / 2;
    }

    // MARK: - The premade arrangements

    /// <summary>
    /// The layouts offered for [count] screens (1...3), in menu order. With one screen "Main + side" and "Stacked"
    /// would be the same as "Center", so they are left out.
    /// </summary>
    public static ArrangeLayout[] Choices(int count)
    {
        if (count <= 0) return Array.Empty<ArrangeLayout>();
        if (count == 1) return new[] { ArrangeLayout.Row, ArrangeLayout.Theater };
        return new[] { ArrangeLayout.Row, ArrangeLayout.MainSide, ArrangeLayout.Stacked, ArrangeLayout.Theater };
    }

    /// <summary>The layout's name for that count, as Pocket XR names them.</summary>
    public static string Title(ArrangeLayout layout, int count)
    {
        switch (layout)
        {
            case ArrangeLayout.Row:
                if (count <= 1) return "Center";
                if (count == 2) return "Side by side";
                return "Three across";
            case ArrangeLayout.MainSide:
                if (count <= 1) return "Center"; // one screen: the same as the row
                if (count == 2) return "Main + side";
                return "Main + two stacked";
            case ArrangeLayout.Stacked:
                return "Stacked";
            default:
                return "Theater";
        }
    }

    /// <summary>
    /// Where each screen goes. [aspects] are width / height per screen; [widthDeg] the main screen's angular width,
    /// [distance] metres, [curve] 0...2.5 applied to every screen. Anything else than 1 to 3 screens, and the two
    /// "main" layouts with one screen, fall back to the row.
    /// </summary>
    public static PanelPlacement[] Arrange(ArrangeLayout layout, IReadOnlyList<double> aspects, double widthDeg, double distance, double curve)
    {
        int n = aspects.Count;
        PanelPlacement[] result;
        if (n < 1 || n > 3)
        {
            result = Row(aspects: aspects, widthDeg: widthDeg, distance: distance);
        }
        else
        {
            switch (layout)
            {
                case ArrangeLayout.Row:
                    result = Row(aspects: aspects, widthDeg: widthDeg, distance: distance);
                    break;
                case ArrangeLayout.MainSide:
                    result = n == 1
                        ? Row(aspects: aspects, widthDeg: widthDeg, distance: distance)
                        : MainSide(aspects: aspects, widthDeg: widthDeg, distance: distance);
                    break;
                case ArrangeLayout.Stacked:
                    result = n == 1
                        ? Row(aspects: aspects, widthDeg: widthDeg, distance: distance)
                        : Stacked(aspects: aspects, widthDeg: widthDeg, distance: distance);
                    break;
                default:
                    result = Theater(aspects: aspects, widthDeg: widthDeg, distance: distance);
                    break;
            }
        }
        double c = HeadMath.Min(HeadMath.Max(curve, 0), PanelCurve.MaxCurve);
        for (int i = 0; i < result.Length; i++)
        {
            result[i].Curve = c;
        }
        return result;
    }

    /// <summary>
    /// The yaw (left positive) and pitch of the middle of all the screens, for a recentre: the middle of the box
    /// round every screen's angular rectangle (Pocket XR's RecentreContent, with pitch added). (0, 0) with none.
    /// </summary>
    public static (double YawDeg, double PitchDeg) Middle(IReadOnlyList<PanelPlacement> panels)
    {
        if (panels.Count == 0) return (YawDeg: 0, PitchDeg: 0);
        PanelPlacement p0 = panels[0];
        double yawLo = p0.YawDeg - p0.WidthDeg / 2;
        double yawHi = p0.YawDeg + p0.WidthDeg / 2;
        double pitchLo = p0.PitchDeg - p0.HeightDeg / 2;
        double pitchHi = p0.PitchDeg + p0.HeightDeg / 2;
        for (int i = 1; i < panels.Count; i++)
        {
            PanelPlacement p = panels[i];
            double h = p.HeightDeg;
            yawLo = HeadMath.Min(yawLo, p.YawDeg - p.WidthDeg / 2);
            yawHi = HeadMath.Max(yawHi, p.YawDeg + p.WidthDeg / 2);
            pitchLo = HeadMath.Min(pitchLo, p.PitchDeg - h / 2);
            pitchHi = HeadMath.Max(pitchHi, p.PitchDeg + h / 2);
        }
        return (YawDeg: (yawLo + yawHi) / 2, PitchDeg: (pitchLo + pitchHi) / 2);
    }

    /// <summary>A flat screen's height as seen from the head, degrees: [widthDeg] wide with [aspect] width / height.</summary>
    public static double HeightDeg(double widthDeg, double aspect)
    {
        double w = HeadMath.Min(HeadMath.Max(widthDeg, 0.1), 179);
        double a = HeadMath.Max(aspect, 0.01);
        return 2 * Math.Atan(Math.Tan(w * Math.PI / 360) / a) * 180 / Math.PI;
    }

    // MARK: - Each arrangement

    /// <summary>
    /// The first screen [widthDeg] wide, the others in a column to its right: one at 0.6 of the main screen's width
    /// level with its middle, or two at 0.5 one above the other, 2 degrees apart, the column's middle level with
    /// the main screen's. The whole block is centred. All shrink together if the column would pass 35 degrees up.
    /// </summary>
    private static PanelPlacement[] MainSide(IReadOnlyList<double> aspects, double widthDeg, double distance)
    {
        double[] sides = DropFirst(aspects);
        double share = sides.Length <= 1 ? SideShare : SideShareTwo;
        double mainAspect = aspects[0];
        double s = FitScale(k =>
        {
            double mainH = HeightDeg(widthDeg: widthDeg * k, aspect: mainAspect);
            double columnH = ColumnHeight(aspects: sides, widthDeg: widthDeg * share * k);
            return HeadMath.Max(mainH, columnH);
        });
        double mainW = widthDeg * s;
        double sideW = widthDeg * share * s;
        double[] pitches = ColumnPitches(aspects: sides, widthDeg: sideW);
        // A screen tipped up or down reaches a little further sideways at its far corners than half its width:
        // the column stands off by that much, so its corners stay clear of the main screen.
        double sideHalf = sideW / 2;
        for (int i = 0; i < sides.Length; i++)
        {
            double h = HeightDeg(widthDeg: sideW, aspect: sides[i]);
            sideHalf = HeadMath.Max(sideHalf, CornerReachDeg(widthDeg: sideW, heightDeg: h, pitchDeg: pitches[i]));
        }
        // Along the arc, right positive: the main screen's centre, then the column's, the block's middle at 0.
        double mainX = -(GapDeg + sideHalf + sideW / 2) / 2;
        double columnX = mainX + mainW / 2 + GapDeg + sideHalf;
        var result = new List<PanelPlacement>
        {
            new PanelPlacement(yawDeg: -mainX, widthDeg: mainW, aspect: mainAspect, distance: distance),
        };
        for (int i = 0; i < sides.Length; i++)
        {
            result.Add(new PanelPlacement(yawDeg: -columnX, pitchDeg: pitches[i], widthDeg: sideW, aspect: sides[i], distance: distance));
        }
        return result.ToArray();
    }

    /// <summary>
    /// The screens one above another, the first on top, 2 degrees apart, the column centred on the horizon. Each is
    /// [widthDeg] wide unless the column would pass 35 degrees up or down: then all shrink together to fit.
    /// </summary>
    private static PanelPlacement[] Stacked(IReadOnlyList<double> aspects, double widthDeg, double distance)
    {
        double s = FitScale(k => ColumnHeight(aspects: aspects, widthDeg: widthDeg * k));
        double w = widthDeg * s;
        double[] pitches = ColumnPitches(aspects: aspects, widthDeg: w);
        var result = new PanelPlacement[aspects.Count];
        for (int i = 0; i < aspects.Count; i++)
        {
            result[i] = new PanelPlacement(yawDeg: 0, pitchDeg: pitches[i], widthDeg: w, aspect: aspects[i], distance: distance);
        }
        return result;
    }

    /// <summary>
    /// The first screen far away and large: 4.2 m wide at 5 m for a width setting of 40 degrees (45.6 degrees as
    /// seen), wider or narrower with the setting. Any other screens flank it at the user's distance, 0.6 of the
    /// width setting each: one to its right, or the second to its left and the third to its right. The block is
    /// centred, so with two screens the big one sits a little left of straight ahead.
    /// </summary>
    private static PanelPlacement[] Theater(IReadOnlyList<double> aspects, double widthDeg, double distance)
    {
        double mainAspect = aspects[0];
        double[] others = DropFirst(aspects);
        double s = FitScale(k =>
        {
            double h = HeightDeg(widthDeg: TheaterWidthDeg(widthDeg: widthDeg * k), aspect: mainAspect);
            foreach (double a in others)
            {
                h = HeadMath.Max(h, HeightDeg(widthDeg: widthDeg * TheaterFlankShare * k, aspect: a));
            }
            return h;
        });
        double mainW = TheaterWidthDeg(widthDeg: widthDeg * s);
        double flankW = widthDeg * TheaterFlankShare * s;
        double step = mainW / 2 + GapDeg + flankW / 2; // from the big screen's centre to a flank's
        if (others.Length == 0)
        {
            return new[] { new PanelPlacement(yawDeg: 0, widthDeg: mainW, aspect: mainAspect, distance: TheaterDistance) };
        }
        if (others.Length == 1)
        {
            // Big screen and one flank: the pair's middle straight ahead.
            double mainX = -(GapDeg + flankW) / 2;
            return new[]
            {
                new PanelPlacement(yawDeg: -mainX, widthDeg: mainW, aspect: mainAspect, distance: TheaterDistance),
                new PanelPlacement(yawDeg: -(mainX + step), widthDeg: flankW, aspect: others[0], distance: distance),
            };
        }
        return new[]
        {
            new PanelPlacement(yawDeg: 0, widthDeg: mainW, aspect: mainAspect, distance: TheaterDistance),
            new PanelPlacement(yawDeg: step, widthDeg: flankW, aspect: others[0], distance: distance),
            new PanelPlacement(yawDeg: -step, widthDeg: flankW, aspect: others[1], distance: distance),
        };
    }

    // MARK: - Helpers

    /// <summary>Swift's Array(aspects.dropFirst()).</summary>
    private static double[] DropFirst(IReadOnlyList<double> aspects)
    {
        var rest = new double[Math.Max(aspects.Count - 1, 0)];
        for (int i = 0; i < rest.Length; i++) rest[i] = aspects[i + 1];
        return rest;
    }

    /// <summary>The big screen's width as seen, degrees, for a width setting of [widthDeg].</summary>
    private static double TheaterWidthDeg(double widthDeg)
    {
        double metres = TheaterWidthMetres * widthDeg / DefaultWidthDeg;
        return 2 * Math.Atan(metres / 2 / TheaterDistance) * 180 / Math.PI;
    }

    /// <summary>The height of a column of screens, each [widthDeg] wide, 2 degrees apart, degrees.</summary>
    private static double ColumnHeight(IReadOnlyList<double> aspects, double widthDeg)
    {
        double total = GapDeg * (double)Math.Max(aspects.Count - 1, 0);
        foreach (double a in aspects)
        {
            total += HeightDeg(widthDeg: widthDeg, aspect: a);
        }
        return total;
    }

    /// <summary>The pitch of each screen of a column centred on the horizon, the first on top.</summary>
    private static double[] ColumnPitches(IReadOnlyList<double> aspects, double widthDeg)
    {
        double top = ColumnHeight(aspects: aspects, widthDeg: widthDeg) / 2;
        var result = new double[aspects.Count];
        for (int i = 0; i < aspects.Count; i++)
        {
            double h = HeightDeg(widthDeg: widthDeg, aspect: aspects[i]);
            result[i] = top - h / 2;
            top -= h + GapDeg;
        }
        return result;
    }

    /// <summary>
    /// How far sideways from its centre, degrees of yaw, the far corners of a flat screen reach once it is tipped
    /// [pitchDeg] up or down. Level, that is half its width.
    /// </summary>
    private static double CornerReachDeg(double widthDeg, double heightDeg, double pitchDeg)
    {
        double tx = Math.Tan(widthDeg * Math.PI / 360);
        double ty = Math.Tan(heightDeg * Math.PI / 360);
        double p = Math.Abs(pitchDeg) * Math.PI / 180;
        double depth = Math.Cos(p) - ty * Math.Sin(p);
        if (depth <= 1e-6) return 90;
        return Math.Atan(tx / depth) * 180 / Math.PI;
    }

    /// <summary>
    /// The largest scale, at most 1, at which [extent] (the arrangement's whole height in degrees at that scale)
    /// stays inside 35 degrees up and down.
    /// </summary>
    private static double FitScale(Func<double, double> extent)
    {
        double limit = 2 * MaxPitchDeg;
        if (extent(1) <= limit) return 1;
        double lo = 0.0;
        double hi = 1.0;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (extent(mid) <= limit)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
}

public static class SpaceMath
{
    /// <summary>A rotation matrix from a quaternion.</summary>
    public static Mat4 Rotation(Quat q)
    {
        float[] m = q.Matrix;
        return new Mat4(
            new Vec4f(m[0], m[1], m[2], m[3]),
            new Vec4f(m[4], m[5], m[6], m[7]),
            new Vec4f(m[8], m[9], m[10], m[11]),
            new Vec4f(m[12], m[13], m[14], m[15]));
    }

    /// <summary>
    /// The perspective projection the Mac gives Metal (clip z 0...1), vertical field [fovYDeg], [aspect] width /
    /// height.
    /// </summary>
    public static Mat4 Perspective(double fovYDeg, double aspect, double near = 0.05, double far = 100)
    {
        float y = (float)(1 / Math.Tan(fovYDeg * Math.PI / 360));
        float x = y / (float)aspect;
        float n = (float)near, f = (float)far;
        float z = f / (n - f);
        return new Mat4(
            new Vec4f(x, 0, 0, 0),
            new Vec4f(0, y, 0, 0),
            new Vec4f(0, 0, z, -1),
            new Vec4f(0, 0, z * n, 0));
    }

    /// <summary>The view matrix for one eye: the head's rotation undone, then the eye's offset ([eyeX] metres, right positive).</summary>
    public static Mat4 View(Quat head, double eyeX)
    {
        Mat4 shift = Mat4.Identity;
        shift.C3 = new Vec4f((float)(-eyeX), 0, 0, 1);
        return shift * Rotation(head.Conjugate);
    }
}
