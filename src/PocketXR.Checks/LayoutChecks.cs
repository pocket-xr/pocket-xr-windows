using System;
using System.Globalization;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

public static class LayoutChecks
{
    /// <summary>A number in a check's message, written as Swift writes a Double (40.0, not 40).</summary>
    private static string D(double v)
    {
        string text = v.ToString("R", CultureInfo.InvariantCulture);
        return double.IsFinite(v) && !text.Contains('.') && !text.Contains('E') ? text + ".0" : text;
    }

    /// <summary>Do two screens' angular rectangles (yaw by pitch, degrees) share any area?</summary>
    private static bool LayoutRectsOverlap(PanelPlacement a, PanelPlacement b)
    {
        double eps = 1e-9;
        bool yawApart = a.YawDeg - a.WidthDeg / 2 >= b.YawDeg + b.WidthDeg / 2 - eps
            || b.YawDeg - b.WidthDeg / 2 >= a.YawDeg + a.WidthDeg / 2 - eps;
        bool pitchApart = a.PitchDeg - a.HeightDeg / 2 >= b.PitchDeg + b.HeightDeg / 2 - eps
            || b.PitchDeg - b.HeightDeg / 2 >= a.PitchDeg + a.HeightDeg / 2 - eps;
        return !(yawApart || pitchApart);
    }

    /// <summary>The angle of a mesh vertex to the right of straight ahead, degrees.</summary>
    private static double LayoutAzimuthDeg(PanelVertex v)
    {
        return Math.Atan2((double)v.Position.X, (double)(-v.Position.Z)) * 180 / Math.PI;
    }

    public static void Run()
    {
        double wide = 16.0 / 9.0;

        // The displays are laid out as the screens look, so the pointer crosses the way the eye expects.
        (int Width, int Height)[] hd = { (Width: 1920, Height: 1080), (Width: 1920, Height: 1080) };
        (int X, int Y)[] gridRow = DisplayGrid.Origins(panels: SpaceLayout.Arrange(layout: ArrangeLayout.Row, aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4, curve: 0), sizes: hd);
        Check.That(gridRow[0].X == 0 && gridRow[0].Y == 0 && gridRow[1].X == 1920 && gridRow[1].Y == 0, "display grid: a row is left to right");
        (int X, int Y)[] gridStack = DisplayGrid.Origins(panels: SpaceLayout.Arrange(layout: ArrangeLayout.Stacked, aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4, curve: 0), sizes: hd);
        Check.That(gridStack[0].X == 0 && gridStack[1].X == 0 && Math.Abs(gridStack[0].Y - gridStack[1].Y) == 1080, "display grid: stacked is above and below");
        PanelPlacement[] stackPanels = SpaceLayout.Arrange(layout: ArrangeLayout.Stacked, aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        int upper = stackPanels[0].PitchDeg > stackPanels[1].PitchDeg ? 0 : 1;
        Check.That(gridStack[upper].Y == 0 && gridStack[1 - upper].Y == 1080, "display grid: the upper screen is the upper display");
        (int Width, int Height)[] hd3 = hd.Concat(new[] { (Width: 1920, Height: 1080) }).ToArray();
        (int X, int Y)[] gridSide = DisplayGrid.Origins(panels: SpaceLayout.Arrange(layout: ArrangeLayout.MainSide, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 0), sizes: hd3);
        Check.That(gridSide[0].X == 0 && gridSide[0].Y == 0 && gridSide[1].X == 1920 && gridSide[2].X == 1920, "display grid: main + side has the sides in a column to the right");
        Check.That(Math.Abs(gridSide[1].Y - gridSide[2].Y) == 1080 && Math.Min(gridSide[1].Y, gridSide[2].Y) == -540, "display grid: the side column is centred on the main screen");
        (int X, int Y)[] gridOdd = DisplayGrid.Origins(panels: Array.Empty<PanelPlacement>(), sizes: hd);
        Check.That(gridOdd[0].X == 0 && gridOdd[1].X == 1920 && gridOdd[1].Y == 0, "display grid: a plain row when the counts differ");

        // The row is as it was: symmetric, 2 degrees between screens, its middle straight ahead.
        PanelPlacement[] row3 = SpaceLayout.Row(aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4);
        Check.That(row3.Length == 3, "row: three screens");
        Check.That(Check.Near(row3[0].YawDeg, 42) && Check.Near(row3[1].YawDeg, 0) && Check.Near(row3[2].YawDeg, -42), "row: 42, 0, -42 degrees");
        double rowGap = (row3[0].YawDeg - row3[0].WidthDeg / 2) - (row3[1].YawDeg + row3[1].WidthDeg / 2);
        Check.That(Check.Near(rowGap, 2), "row: 2 degrees between screens");
        Check.That(Check.Near(SpaceLayout.MiddleYawDeg(row3), 0), "row: middle yaw 0");
        Check.That(row3.All(p => p.Curve == 0 && p.PitchDeg == 0 && p.Distance == 1.4), "row: flat, level, at its distance");
        PanelPlacement[] row2 = SpaceLayout.Row(aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4);
        Check.That(Check.Near(row2[0].YawDeg, 21) && Check.Near(row2[1].YawDeg, -21), "row: two screens at 21 and -21 degrees");
        Check.That(SpaceLayout.Row(aspects: Array.Empty<double>(), widthDeg: 40, distance: 1.4).Length == 0, "row: no screens, no placements");
        Check.That(new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4).Curve == 0, "a placement is flat by default");
        PanelPlacement[] arrangedRow = SpaceLayout.Arrange(layout: ArrangeLayout.Row, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(arrangedRow.SequenceEqual(row3), "arrange: the row layout is the row");

        // Every layout, 1 to 3 screens, narrow to wide: the right count, no overlap, centred, inside 35 degrees.
        foreach (ArrangeLayout layout in ArrangeLayoutExt.AllCases)
        {
            for (int count = 1; count <= 3; count++)
            {
                foreach (double width in new[] { 15.0, 40.0, 70.0, 100.0 })
                {
                    double[] aspects = Enumerable.Repeat(wide, count).ToArray();
                    PanelPlacement[] panels = SpaceLayout.Arrange(layout: layout, aspects: aspects, widthDeg: width, distance: 1.4, curve: 0);
                    string what = $"{layout.RawValue()} x{count} at {D(width)} degrees";
                    Check.That(panels.Length == count, $"{what}: one placement per screen");
                    bool overlap = false;
                    for (int i = 0; i < panels.Length; i++)
                    {
                        for (int j = i + 1; j < Math.Max(panels.Length, i + 1); j++)
                        {
                            if (LayoutRectsOverlap(panels[i], panels[j])) overlap = true;
                        }
                    }
                    Check.That(!overlap, $"{what}: no two screens overlap");
                    (double YawDeg, double PitchDeg) mid = SpaceLayout.Middle(panels);
                    Check.That(Check.Near(mid.YawDeg, 0, 1e-6) && Check.Near(mid.PitchDeg, 0, 1e-6), $"{what}: its middle is straight ahead");
                    double limit = SpaceLayout.MaxPitchDeg + 1e-6;
                    bool inside = panels.All(p => Math.Abs(p.PitchDeg) + p.HeightDeg / 2 <= limit);
                    Check.That(inside, $"{what}: inside 35 degrees up and down");
                }
            }
        }

        // The layouts' own proportions.
        PanelPlacement[] ms2 = SpaceLayout.Arrange(layout: ArrangeLayout.MainSide, aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(Check.Near(ms2[0].WidthDeg, 40) && Check.Near(ms2[1].WidthDeg, 24), "main + side: 40 and 24 degrees wide");
        Check.That(Check.Near(ms2[0].YawDeg, 13) && Check.Near(ms2[1].YawDeg, -21), "main + side: the main screen left, the side right");
        PanelPlacement[] ms3 = SpaceLayout.Arrange(layout: ArrangeLayout.MainSide, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(Check.Near(ms3[1].WidthDeg, 20) && Check.Near(ms3[2].WidthDeg, 20), "main + two stacked: sides 20 degrees wide");
        Check.That(Check.Near(ms3[1].YawDeg, ms3[2].YawDeg) && ms3[1].PitchDeg > 0 && Check.Near(ms3[1].PitchDeg, -ms3[2].PitchDeg),
            "main + two stacked: one column, the second screen above the third");
        double sideGap = (ms3[1].PitchDeg - ms3[1].HeightDeg / 2) - (ms3[2].PitchDeg + ms3[2].HeightDeg / 2);
        Check.That(Check.Near(sideGap, 2), "main + two stacked: 2 degrees between the side screens");
        Check.That(ms3[0].YawDeg - ms3[0].WidthDeg / 2 - (ms3[1].YawDeg + ms3[1].WidthDeg / 2) >= 2 - 1e-9,
            "main + two stacked: at least 2 degrees between main and column");

        PanelPlacement[] st2 = SpaceLayout.Arrange(layout: ArrangeLayout.Stacked, aspects: new[] { wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(Check.Near(st2[0].WidthDeg, 40) && Check.Near(st2[0].YawDeg, 0) && Check.Near(st2[1].YawDeg, 0), "stacked: full width, one column");
        Check.That(st2[0].PitchDeg > 0 && Check.Near(st2[0].PitchDeg, -st2[1].PitchDeg), "stacked: the first screen on top");
        double stGap = (st2[0].PitchDeg - st2[0].HeightDeg / 2) - (st2[1].PitchDeg + st2[1].HeightDeg / 2);
        Check.That(Check.Near(stGap, 2), "stacked: 2 degrees between screens");
        PanelPlacement[] st3 = SpaceLayout.Arrange(layout: ArrangeLayout.Stacked, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(st3[0].WidthDeg < 40 && Check.Near(st3[0].WidthDeg, st3[2].WidthDeg), "stacked: three shrink together to fit");
        Check.That(Check.Near(st3[0].PitchDeg + st3[0].HeightDeg / 2, 35, 1e-4), "stacked: three reach just 35 degrees up");

        PanelPlacement[] th1 = SpaceLayout.Arrange(layout: ArrangeLayout.Theater, aspects: new[] { wide }, widthDeg: 40, distance: 1.4, curve: 0);
        double theaterDeg = 2 * Math.Atan(2.1 / 5.0) * 180 / Math.PI;
        Check.That(Check.Near(th1[0].Distance, 5) && Check.Near(th1[0].WidthDeg, theaterDeg), "theater: 4.2 m wide at 5 m");
        Check.That(Check.Near(th1[0].SizeMetres.X, 4.2, 1e-9), "theater: 4.2 m across");
        PanelPlacement[] th3 = SpaceLayout.Arrange(layout: ArrangeLayout.Theater, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 0);
        Check.That(Check.Near(th3[0].YawDeg, 0) && th3[1].YawDeg > 0 && Check.Near(th3[1].YawDeg, -th3[2].YawDeg), "theater: flanked left and right");
        Check.That(Check.Near(th3[1].Distance, 1.4) && Check.Near(th3[1].WidthDeg, 24), "theater: flanks at the user's distance, 24 degrees");

        // Fallbacks, curve, names.
        PanelPlacement[] one = SpaceLayout.Arrange(layout: ArrangeLayout.MainSide, aspects: new[] { wide }, widthDeg: 40, distance: 1.4, curve: 1);
        Check.That(one.Length == 1 && Check.Near(one[0].YawDeg, 0) && one[0].Curve == 1, "main + side with one screen is the row, curved");
        // Past six (never asked since 0.4.11's six; past three before) the row is the fallback.
        PanelPlacement[] seven = SpaceLayout.Arrange(layout: ArrangeLayout.Stacked, aspects: Enumerable.Repeat(wide, 7).ToArray(), widthDeg: 40, distance: 1.4, curve: 9);
        Check.That(seven.Length == 7 && seven.All(p => p.PitchDeg == 0 && p.Curve == 2.5), "seven screens fall back to the row; curve held to 2.5");
        Check.That(SpaceLayout.Arrange(layout: ArrangeLayout.Theater, aspects: Array.Empty<double>(), widthDeg: 40, distance: 1.4, curve: 0).Length == 0, "no screens, no placements");
        Check.That(SpaceLayout.Choices(count: 1).SequenceEqual(new[] { ArrangeLayout.Row, ArrangeLayout.Theater }), "choices for one screen");
        Check.That(SpaceLayout.Choices(count: 2).SequenceEqual(new[] { ArrangeLayout.Row, ArrangeLayout.MainSide, ArrangeLayout.Stacked, ArrangeLayout.Theater }), "choices for two screens");
        Check.That(SpaceLayout.Choices(count: 3).SequenceEqual(new[] { ArrangeLayout.Row, ArrangeLayout.MainSide, ArrangeLayout.Stacked, ArrangeLayout.Theater }), "choices for three screens");
        Check.That(SpaceLayout.Title(ArrangeLayout.Row, count: 1) == "Center" && SpaceLayout.Title(ArrangeLayout.Row, count: 2) == "Side by side"
            && SpaceLayout.Title(ArrangeLayout.Row, count: 3) == "Three across", "row titles");
        Check.That(SpaceLayout.Title(ArrangeLayout.MainSide, count: 2) == "Main + side" && SpaceLayout.Title(ArrangeLayout.MainSide, count: 3) == "Main + two stacked",
            "main + side titles");
        Check.That(SpaceLayout.Title(ArrangeLayout.Stacked, count: 2) == "Stacked" && SpaceLayout.Title(ArrangeLayout.Theater, count: 1) == "Theater", "other titles");
        PanelPlacement[] offCentre = { new PanelPlacement(yawDeg: 30, pitchDeg: 10, widthDeg: 20, aspect: wide, distance: 1.4) };
        (double YawDeg, double PitchDeg) offMid = SpaceLayout.Middle(offCentre);
        Check.That(Check.Near(offMid.YawDeg, 30) && Check.Near(offMid.PitchDeg, 10), "middle: one screen's own centre");
        (double YawDeg, double PitchDeg) none = SpaceLayout.Middle(Array.Empty<PanelPlacement>());
        Check.That(none.YawDeg == 0 && none.PitchDeg == 0, "middle: (0, 0) with no screens");

        // A flat mesh is the unit quad times the model matrix.
        var tilted = new PanelPlacement(yawDeg: 25, pitchDeg: -10, widthDeg: 40, aspect: wide, distance: 1.4);
        PanelVertex[] flat = tilted.Mesh();
        Check.That(flat.Length == 4, "flat mesh: 4 vertices");
        Vec2f[] corners = { new Vec2f(-0.5f, -0.5f), new Vec2f(0.5f, -0.5f), new Vec2f(-0.5f, 0.5f), new Vec2f(0.5f, 0.5f) };
        Vec2f[] uvs = { new Vec2f(0, 1), new Vec2f(1, 1), new Vec2f(0, 0), new Vec2f(1, 0) };
        Mat4 model = tilted.ModelMatrix;
        if (flat.Length == 4)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec4f q = model * new Vec4f(corners[i].X, corners[i].Y, 0, 1);
                double dx = (double)MathF.Abs(q.X - flat[i].Position.X);
                double dy = (double)MathF.Abs(q.Y - flat[i].Position.Y);
                double dz = (double)MathF.Abs(q.Z - flat[i].Position.Z);
                Check.That(dx < 1e-5 && dy < 1e-5 && dz < 1e-5, $"flat mesh: corner {i} is the model matrix's");
                Check.That(flat[i].Uv == uvs[i], $"flat mesh: corner {i} uv");
                // The curved surface's own point agrees with the flat quad.
                double fx = (double)corners[i].X;
                double fy = (double)corners[i].Y;
                Vec3 pt = PanelCurve.Point(tilted, x: fx, y: fy);
                Check.That(Check.Near(pt.X, (double)q.X, 1e-5) && Check.Near(pt.Y, (double)q.Y, 1e-5) && Check.Near(pt.Z, (double)q.Z, 1e-5),
                    $"flat point {i} agrees with the model matrix");
            }
        }

        // Curve 1: every column as far from the head, the same angular width as flat, uv 0...1.
        var curved = new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4, curve: 1);
        PanelVertex[] mesh = curved.Mesh();
        Check.That(mesh.Length == 2 * 65, "curved mesh: 2 * (64 + 1) vertices");
        Check.That(curved.Mesh(columns: 8).Length == 18, "curved mesh: 8 columns, 18 vertices");
        bool allAtDistance = true;
        bool uvInside = true;
        foreach (PanelVertex v in mesh)
        {
            double r = Math.Sqrt((double)v.Position.X * (double)v.Position.X + (double)v.Position.Z * (double)v.Position.Z);
            if (!Check.Near(r, 1.4, 1e-4)) allAtDistance = false;
            if (v.Uv.X < 0 || v.Uv.X > 1 || v.Uv.Y < 0 || v.Uv.Y > 1) uvInside = false;
        }
        Check.That(allAtDistance, "curve 1: every vertex 1.4 m from the head in the horizontal plane");
        Check.That(uvInside, "curved mesh: uv inside 0...1");
        if (mesh.Length >= 4)
        {
            PanelVertex first = mesh[0];
            PanelVertex last = mesh[mesh.Length - 1];
            Check.That(Check.Near(LayoutAzimuthDeg(first), -20, 1e-3) && Check.Near(LayoutAzimuthDeg(last), 20, 1e-3), "curve 1: spans 40 degrees, as flat");
            Check.That(first.Uv == new Vec2f(0, 0) && mesh[1].Uv == new Vec2f(0, 1), "curved mesh: starts top left, then bottom left");
            Check.That(last.Uv == new Vec2f(1, 1) && mesh[mesh.Length - 2].Uv == new Vec2f(1, 0), "curved mesh: ends top right, bottom right");
            double halfHeight = curved.SizeMetres.Y / 2;
            Check.That(Check.Near((double)first.Position.Y, halfHeight, 1e-5) && Check.Near((double)mesh[1].Position.Y, -halfHeight, 1e-5),
                "curved mesh: the flat screen's height");
        }
        PanelVertex[] flatMesh = new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4).Mesh();
        if (flatMesh.Length == 4)
        {
            Check.That(Check.Near(LayoutAzimuthDeg(flatMesh[0]), -20, 1e-3) && Check.Near(LayoutAzimuthDeg(flatMesh[1]), 20, 1e-3), "flat: spans 40 degrees");
        }

        // The strongest curve: the same angular width, the edges nearer than the middle.
        var strong = new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4, curve: 2.5);
        PanelVertex[] strongMesh = strong.Mesh();
        if (strongMesh.Length > 0)
        {
            PanelVertex first = strongMesh[0];
            PanelVertex last = strongMesh[strongMesh.Length - 1];
            Check.That(Check.Near(LayoutAzimuthDeg(first), -20, 1e-3) && Check.Near(LayoutAzimuthDeg(last), 20, 1e-3), "curve 2.5: still spans 40 degrees");
            double edge = Math.Sqrt((double)first.Position.X * (double)first.Position.X + (double)first.Position.Z * (double)first.Position.Z);
            Check.That(edge < 1.4 - 0.1, "curve 2.5: the edges come clearly forward");
        }
        PanelVertex? middleColumn = strongMesh.Length > 64 ? strongMesh[64] : null;
        Check.That(middleColumn != null && Check.Near((double)middleColumn.Value.Position.Z, -1.4, 1e-5) && Check.Near((double)middleColumn.Value.Position.X, 0, 1e-5),
            "curve 2.5: the middle stays at its distance");
        Check.That(Check.Near(PanelCurve.Radius(strong), 1.4 / 2.5), "curve 2.5: radius is distance / curve");
        // A wide screen is held so its half arc stays at 85 degrees.
        var wideCurved = new PanelPlacement(yawDeg: 0, widthDeg: 100, aspect: wide, distance: 1.4, curve: 2.5);
        Check.That(Check.Near(PanelCurve.HalfArc(wideCurved) * 180 / Math.PI, 85, 1e-6), "a wide screen's half arc is held to 85 degrees");
        Check.That(Check.Near(PanelCurve.Effective(curve: 0.6, widthDeg: 100), 0.6) && Check.Near(PanelCurve.Effective(curve: 9, widthDeg: 40), 2.5),
            "effective curve: kept under 1, held to 2.5");
        Check.That(!PanelCurve.IsCurved(new PanelPlacement(yawDeg: 0, widthDeg: 40, aspect: wide, distance: 1.4, curve: 0.004)), "a tiny curve is flat");

        // Which screen the head points at.
        (int Index, double U, double V)? ahead = LookTarget.Hit(head: Quat.Identity, panels: row3);
        Check.That(ahead != null && ahead.Value.Index == 1 && Check.Near(ahead.Value.U, 0.5) && Check.Near(ahead.Value.V, 0.5), "looking ahead: the centre screen's middle");
        (int Index, double U, double V)? left = LookTarget.Hit(head: Quat.AboutY(42 * Math.PI / 180), panels: row3);
        Check.That(left != null && left.Value.Index == 0 && Check.Near(left.Value.U, 0.5) && Check.Near(left.Value.V, 0.5), "one step left: the left screen's middle");
        (int Index, double U, double V)? right = LookTarget.Hit(head: Quat.AboutY(-42 * Math.PI / 180), panels: row3);
        Check.That(right != null && right.Value.Index == 2, "one step right: the right screen");
        Check.That(LookTarget.Hit(head: Quat.AboutY(Math.PI), panels: row3) == null, "looking behind: no screen");
        Check.That(LookTarget.Hit(head: Quat.AboutY(21 * Math.PI / 180), panels: row3) == null, "looking at the gap: no screen");
        Check.That(LookTarget.Hit(head: Quat.AboutX(60 * Math.PI / 180), panels: row3) == null, "looking far up: no screen");
        Check.That(LookTarget.Hit(head: Quat.Identity, panels: Array.Empty<PanelPlacement>()) == null, "no screens: nothing");
        // Where on the screen: a little left is u under a half, a little up is v under a half.
        (int Index, double U, double V)? flatLeft = LookTarget.Hit(head: Quat.AboutY(10 * Math.PI / 180), panels: row3);
        double expectU = 0.5 - Math.Tan(10 * Math.PI / 180) / (2 * Math.Tan(20 * Math.PI / 180));
        Check.That(flatLeft != null && flatLeft.Value.Index == 1 && Check.Near(flatLeft.Value.U, expectU, 1e-9), "flat: 10 degrees left on the centre screen");
        (int Index, double U, double V)? flatUp = LookTarget.Hit(head: Quat.AboutX(5 * Math.PI / 180), panels: row3);
        double expectV = 0.5 - Math.Tan(5 * Math.PI / 180) * wide / (2 * Math.Tan(20 * Math.PI / 180));
        Check.That(flatUp != null && flatUp.Value.Index == 1 && Check.Near(flatUp.Value.V, expectV, 1e-9) && Check.Near(flatUp.Value.U, 0.5), "flat: 5 degrees up on the centre screen");
        // Curved at 1, across the screen goes by angle.
        PanelPlacement[] curvedRow = SpaceLayout.Arrange(layout: ArrangeLayout.Row, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 1);
        (int Index, double U, double V)? curvedLeft = LookTarget.Hit(head: Quat.AboutY(10 * Math.PI / 180), panels: curvedRow);
        Check.That(curvedLeft != null && curvedLeft.Value.Index == 1 && Check.Near(curvedLeft.Value.U, 0.25, 1e-9) && Check.Near(curvedLeft.Value.V, 0.5), "curved: 10 of 40 degrees left is u 0.25");
        (int Index, double U, double V)? curvedEdge = LookTarget.Hit(head: Quat.AboutY(19.9 * Math.PI / 180), panels: curvedRow);
        Check.That(curvedEdge != null && curvedEdge.Value.Index == 1, "curved: just inside the edge still hits");
        Check.That(LookTarget.Hit(head: Quat.AboutY(21 * Math.PI / 180), panels: curvedRow) == null, "curved: the gap is still a gap");
        PanelPlacement[] strongRow = SpaceLayout.Arrange(layout: ArrangeLayout.Row, aspects: new[] { wide, wide, wide }, widthDeg: 40, distance: 1.4, curve: 2.5);
        (int Index, double U, double V)? strongLeft = LookTarget.Hit(head: Quat.AboutY(42 * Math.PI / 180), panels: strongRow);
        Check.That(strongLeft != null && strongLeft.Value.Index == 0 && Check.Near(strongLeft.Value.U, 0.5, 1e-9), "curve 2.5: one step left is the left screen");
        Check.That(LookTarget.Hit(head: Quat.AboutY(19.9 * Math.PI / 180), panels: strongRow)?.Index == 1, "curve 2.5: just inside the edge still hits");
        Check.That(LookTarget.Hit(head: Quat.AboutY(20.1 * Math.PI / 180), panels: strongRow) == null, "curve 2.5: just outside the edge misses");
        // The stacked layout: looking up finds the top screen.
        (int Index, double U, double V)? upAt = LookTarget.Hit(head: Quat.AboutX(st2[0].PitchDeg * Math.PI / 180), panels: st2);
        Check.That(upAt != null && upAt.Value.Index == 0 && Check.Near(upAt.Value.U, 0.5) && Check.Near(upAt.Value.V, 0.5), "stacked: looking up at the top screen's middle");

        // Each model's vertical field.
        static double Vertical(double diagonal)
        {
            return 2 * Math.Atan(Math.Tan(diagonal / 2 * Math.PI / 180) * 9 / Math.Sqrt(337.0)) * 180 / Math.PI;
        }
        var aura = new Glasses(name: "XREAL Aura", family: GlassesFamily.Aura, vendor: 0x3318, product: 0x043A);
        var oneGlasses = new Glasses(name: "XREAL One", family: GlassesFamily.One, vendor: 0x3318, product: 0x0438);
        var onePro = new Glasses(name: "XREAL One Pro", family: GlassesFamily.One, vendor: 0x3318, product: 0x0436);
        var oneProPlus = new Glasses(name: "XREAL One Pro+", family: GlassesFamily.One, vendor: 0x3318, product: 0x0444);
        var oneS = new Glasses(name: "XREAL 1S", family: GlassesFamily.One, vendor: 0x3318, product: 0x043D);
        var air = new Glasses(name: "XREAL Air 2", family: GlassesFamily.Air, vendor: 0x3318, product: 0x0428);
        var beast = new Glasses(name: "VITURE glasses", family: GlassesFamily.Viture, vendor: 0x35CA, product: 0x1211);
        var other = new Glasses(name: "Some glasses", family: GlassesFamily.Other, vendor: 1, product: 2);
        Check.That(GlassesOptics.FovYDeg(glasses: null) == 38, "no glasses: 38 degrees");
        Check.That(GlassesOptics.FovYDeg(glasses: aura) == 38, "Aura: 38 degrees");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: oneGlasses), Vertical(50), 1e-9), "One: 50 degrees diagonal");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: oneGlasses), 25.75, 0.01), "One: about 25.75 degrees tall");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: onePro), Vertical(57), 1e-9), "One Pro: 57 degrees diagonal");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: oneProPlus), Vertical(57), 1e-9), "One Pro+: 57 degrees diagonal");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: oneS), Vertical(50), 1e-9), "1S: the One's 50 degrees");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: air), Vertical(46), 1e-9), "Air family: 46 degrees diagonal");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: beast), Vertical(50), 1e-9), "VITURE: 50 degrees diagonal");
        Check.That(Check.Near(GlassesOptics.FovYDeg(glasses: other), Vertical(50), 1e-9), "unknown glasses: 50 degrees diagonal");
        Check.That(GlassesOptics.IsSideBySide(width: 3840, height: 1080), "3840 x 1080 is side by side");
        Check.That(!GlassesOptics.IsSideBySide(width: 1920, height: 1080), "1920 x 1080 is not");
        Check.That(!GlassesOptics.IsSideBySide(width: 3440, height: 1440), "a 21:9 ultrawide is not");
        Check.That(!GlassesOptics.IsSideBySide(width: 3840, height: 0), "no height is not");
    }
}
