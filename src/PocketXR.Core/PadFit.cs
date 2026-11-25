using System;
using System.Collections.Generic;

namespace PocketXR.Core;

/// <summary>
/// The control panel's Place pad: the floating screens drawn from the front, straight ahead in the pad's middle, at a
/// scale (points per degree) that fits them in the pad and keeps them off the words and buttons drawn over it (the hint
/// at the top, − and + bottom left, Nearer and Further bottom right). Up to 0.4.10 the scale only fitted the screens'
/// span to the whole pad, so a tall layout (three stacked, or 0.4.11's two rows) ran under the hint and the buttons.
/// </summary>
public static class PadFit
{
    /// <summary>A rectangle on the pad, in points, x right and y down.</summary>
    public readonly record struct Box(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;

        /// <summary>Whether the two cross, with [clearance] points kept between them.</summary>
        public bool Crosses(Box o, double clearance = 0) =>
            X < o.Right + clearance && o.X < Right + clearance && Y < o.Bottom + clearance && o.Y < Bottom + clearance;
    }

    /// <summary>The smallest scale used, points per degree, however crowded the pad.</summary>
    public const double MinScale = 0.2;

    /// <summary>Points kept between a screen and a word or button.</summary>
    public const double Clearance = 4;

    /// <summary>Where [p] is drawn on a [width] x [height] pad at [scale]: straight ahead in the middle, left to the
    /// left, up at the top.</summary>
    public static Box Place(double yawDeg, double pitchDeg, double widthDeg, double heightDeg, double width, double height, double scale)
    {
        double cx = width / 2 - yawDeg * scale, cy = height / 2 - pitchDeg * scale;
        double w = widthDeg * scale, h = heightDeg * scale;
        return new Box(cx - w / 2, cy - h / 2, w, h);
    }

    public static Box Place(PanelPlacement p, double width, double height, double scale) =>
        Place(p.YawDeg, p.PitchDeg, p.WidthDeg, p.HeightDeg, width, height, scale);

    /// <summary>
    /// The scale from the screens' span alone (as up to 0.4.10): at most [pitchSpanDeg] up and down in the pad's
    /// height, the screens' whole span inside the pad with 20 points each side and 14 above and below.
    /// </summary>
    public static double SpanScale(IReadOnlyList<PanelPlacement> panels, double width, double height, double pitchSpanDeg)
    {
        double s = height / pitchSpanDeg;
        if (panels.Count > 0)
        {
            double yawLo = double.MaxValue, yawHi = double.MinValue, pitchLo = double.MaxValue, pitchHi = double.MinValue;
            foreach (PanelPlacement p in panels)
            {
                yawLo = Math.Min(yawLo, p.YawDeg - p.WidthDeg / 2);
                yawHi = Math.Max(yawHi, p.YawDeg + p.WidthDeg / 2);
                pitchLo = Math.Min(pitchLo, p.PitchDeg - p.HeightDeg / 2);
                pitchHi = Math.Max(pitchHi, p.PitchDeg + p.HeightDeg / 2);
            }
            s = Math.Min(s, Math.Min((width - 40) / Math.Max(yawHi - yawLo, 1), (height - 28) / Math.Max(pitchHi - pitchLo, 1)));
        }
        return Math.Max(s, MinScale);
    }

    /// <summary>Whether every screen at [scale] is inside the pad (Clearance from its edges) and keeps clear of every
    /// box in [keepOut]. A layout that is not centred (theater with an odd number of flanks, a screen moved by hand)
    /// is fitted too.</summary>
    public static bool Clear(IReadOnlyList<PanelPlacement> panels, double width, double height, double scale, IReadOnlyList<Box> keepOut)
    {
        foreach (PanelPlacement p in panels)
        {
            Box b = Place(p, width, height, scale);
            if (b.X < Clearance || b.Y < Clearance || b.Right > width - Clearance || b.Bottom > height - Clearance) return false;
            foreach (Box k in keepOut)
                if (b.Crosses(k, Clearance)) return false;
        }
        return true;
    }

    /// <summary>
    /// The pad's scale: the span's scale, made smaller (3 % a step) until every screen is inside the pad and none
    /// crosses a box of [keepOut], or MinScale. Screens shrink towards the pad's middle, away from the words and buttons at its edges.
    /// </summary>
    public static double Scale(IReadOnlyList<PanelPlacement> panels, double width, double height, double pitchSpanDeg, IReadOnlyList<Box> keepOut)
    {
        double s = SpanScale(panels, width, height, pitchSpanDeg);
        while (s > MinScale && !Clear(panels, width, height, s, keepOut)) s *= 0.97;
        return Math.Max(s, MinScale);
    }
}
