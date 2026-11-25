using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The live picture of the Space at the top of the Space pane: the screens as glass slabs on their arc around a small
// head, seen from above and a little behind, with the field of view as a wedge of light that swings with the head.
// Also the head gauge of the Head tracking pane. Both are drawn from the same numbers the glasses get.

/// <summary>A point or direction in the Space: x right, y up, z back (forward is -z), metres.</summary>
internal readonly struct SettingsVec
{
    public readonly double X, Y, Z;

    public SettingsVec(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public SettingsVec Plus(SettingsVec o) => new(X + o.X, Y + o.Y, Z + o.Z);
    public SettingsVec Minus(SettingsVec o) => new(X - o.X, Y - o.Y, Z - o.Z);
    public SettingsVec Times(double k) => new(X * k, Y * k, Z * k);
    public double Dot(SettingsVec o) => X * o.X + Y * o.Y + Z * o.Z;
    public SettingsVec Cross(SettingsVec o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public SettingsVec Unit
    {
        get
        {
            double l = Math.Sqrt(X * X + Y * Y + Z * Z);
            return l > 1e-9 ? Times(1 / l) : new SettingsVec(0, 0, -1);
        }
    }
}

internal readonly record struct SettingsFlat(double X, double Y, double Depth);

/// <summary>The preview's camera: above and behind the head, looking down at the screens.</summary>
internal sealed class SettingsPreviewCamera
{
    public readonly SettingsVec Position, Right, Up, Forward;

    /// <summary>[reach] is how far the furthest screen is; the camera backs off as the screens do, but less, so far
    /// screens still look further away.</summary>
    public SettingsPreviewCamera(double reach)
    {
        double rho = 1.4 * Math.Pow(Math.Max(reach, 0.3) / 1.4, 0.85);
        var pos = new SettingsVec(0, 1.15 * rho, 1.55 * rho);
        var target = new SettingsVec(0, 0.02 * rho, -0.55 * rho);
        SettingsVec f = target.Minus(pos).Unit;
        SettingsVec r = f.Cross(new SettingsVec(0, 1, 0)).Unit;
        Position = pos;
        Forward = f;
        Right = r;
        Up = r.Cross(f).Unit;
    }

    /// <summary>Where a point lands for a focal length of 1 (x right, y up), null when it is behind the camera.</summary>
    public SettingsFlat? Flat(SettingsVec p)
    {
        SettingsVec v = p.Minus(Position);
        double depth = v.Dot(Forward);
        if (depth <= 0.08) return null;
        return new SettingsFlat(v.Dot(Right) / depth, v.Dot(Up) / depth, depth);
    }
}

/// <summary>One screen as a strip across its width: flat, or bent round the head.</summary>
internal sealed class SettingsPreviewSlab
{
    public readonly List<SettingsVec> Top = new(), Bottom = new();
    public readonly SettingsVec Centre;
    public readonly bool Focused;

    public SettingsPreviewSlab(PanelPlacement p, bool focused)
    {
        double yaw = p.YawDeg * Math.PI / 180, pitch = p.PitchDeg * Math.PI / 180, d = p.Distance;
        var dir = new SettingsVec(-Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        var right = new SettingsVec(Math.Cos(yaw), 0, -Math.Sin(yaw));
        var up = new SettingsVec(Math.Sin(yaw) * Math.Sin(pitch), Math.Cos(pitch), Math.Cos(yaw) * Math.Sin(pitch));
        SettingsVec mid = dir.Times(d);
        double halfWidth = Math.Min(Math.Max(p.WidthDeg, 1), 170) * Math.PI / 360;
        double w = 2 * d * Math.Tan(halfWidth), h = w / Math.Max(p.Aspect, 0.1);
        bool bent = p.Curve > 0.001;
        int pieces = bent ? 14 : 1;
        for (int i = 0; i <= pieces; i++)
        {
            double u = (double)i / pieces - 0.5;
            SettingsVec point;
            if (bent)
            {
                // On a circle of radius distance / curve that touches the flat screen's middle and bends towards the head.
                double radius = d / p.Curve, sweep = Math.Min(w / radius, 3.0), a = u * sweep;
                point = mid.Plus(right.Times(radius * Math.Sin(a))).Plus(dir.Times(-radius * (1 - Math.Cos(a))));
            }
            else
            {
                point = mid.Plus(right.Times(u * w));
            }
            Top.Add(point.Plus(up.Times(h / 2)));
            Bottom.Add(point.Minus(up.Times(h / 2)));
        }
        Centre = mid;
        Focused = focused;
    }
}

/// <summary>The scale and place that fit the whole Space into the picture, never larger than a comfortable size.</summary>
internal sealed class SettingsPreviewFit
{
    public readonly double Focal, Cx, Cy;

    public SettingsPreviewFit(SettingsPreviewCamera camera, IReadOnlyList<SettingsPreviewSlab> slabs, Size size)
    {
        double maxX = 0.05, top = 0, bottom = 0;
        bool first = true;
        var points = new List<SettingsVec> { new(0, 0, 0) };
        foreach (SettingsPreviewSlab s in slabs)
        {
            points.AddRange(s.Top);
            points.AddRange(s.Bottom);
        }
        foreach (SettingsVec p in points)
        {
            if (camera.Flat(p) is not SettingsFlat f) continue;
            maxX = Math.Max(maxX, Math.Abs(f.X));
            if (first)
            {
                top = f.Y;
                bottom = f.Y;
                first = false;
            }
            else
            {
                top = Math.Max(top, f.Y);
                bottom = Math.Min(bottom, f.Y);
            }
        }
        double w = size.Width, h = size.Height;
        // The top margin clears the caption that lies over the picture.
        const double marginX = 28, marginTop = 44, marginBottom = 26;
        double spanY = Math.Max(top - bottom, 0.05);
        double fitX = (w - 2 * marginX) / (2 * maxX), fitY = (h - marginTop - marginBottom) / spanY;
        double cap = h * 2.2;
        double focal = Math.Max(Math.Min(cap, Math.Min(fitX, fitY)), 1);
        Focal = focal;
        Cx = w / 2;
        // The Space's top and bottom sit evenly inside the margins.
        double free = (h - marginTop - marginBottom) - focal * spanY;
        Cy = marginTop + free / 2 + focal * top;
    }

    public Point Point(SettingsFlat f) => new(Cx + Focal * f.X, Cy - Focal * f.Y);
}

/// <summary>The picture itself. Size, distance and curve glide to their new values as the sliders move.</summary>
public sealed class SettingsSpaceCanvas : FrameworkElement
{
    private readonly SettingsStore store;
    private readonly AppState state;
    // What is drawn now, on its way to what the settings say.
    private double widthDeg, distance, curve;
    private double fromWidth, fromDistance, fromCurve;
    private long glideStart;
    private bool gliding;

    /// <summary>Half of the glasses' field of view across, degrees (38 degrees up and down at 16:9).</summary>
    public const double HalfFieldDeg = 31.5;

    private static readonly LinearGradientBrush Ground = SettingsParts.Frozen(new LinearGradientBrush(Theme.C(Look.Pad), Theme.C(Look.Page), 90));
    private static readonly Pen RingPen = SettingsParts.Frozen(new Pen(Theme.White(0.07), 1));
    private static readonly Brush RingInk = Theme.White(0.32);
    private static readonly Pen TopLight = SettingsParts.Frozen(new Pen(Theme.White(0.30), 1));
    private static readonly Pen RimPen = SettingsParts.Frozen(new Pen(Theme.Rim, 1));
    private static readonly Pen RimFocusPen = SettingsParts.Frozen(new Pen(Theme.RimFocus, 1));
    private static readonly Pen HeadPen = SettingsParts.Frozen(new Pen(Theme.RimFocus, 1));

    public SettingsSpaceCanvas(SettingsStore store, AppState state)
    {
        this.store = store;
        this.state = state;
        widthDeg = store.S.WidthDeg;
        distance = store.S.Distance;
        curve = store.S.Curve;
        Unloaded += (_, _) => StopGlide();
    }

    /// <summary>The settings or the state changed: draw again, gliding to a new size, distance or curve.</summary>
    public void Sync()
    {
        SpaceSettings s = store.S;
        bool moved = Math.Abs(s.WidthDeg - widthDeg) > 1e-6 || Math.Abs(s.Distance - distance) > 1e-6 || Math.Abs(s.Curve - curve) > 1e-6;
        if (moved)
        {
            if (Theme.Static || Theme.ReduceMotion || !IsLoaded)
            {
                StopGlide();
                widthDeg = s.WidthDeg;
                distance = s.Distance;
                curve = s.Curve;
            }
            else
            {
                fromWidth = widthDeg;
                fromDistance = distance;
                fromCurve = curve;
                glideStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!gliding)
                {
                    gliding = true;
                    CompositionTarget.Rendering += Glide;
                }
            }
        }
        InvalidateVisual();
    }

    private void StopGlide()
    {
        if (!gliding) return;
        gliding = false;
        CompositionTarget.Rendering -= Glide;
    }

    private void Glide(object? sender, EventArgs e)
    {
        double t = (System.Diagnostics.Stopwatch.GetTimestamp() - glideStart) / (double)System.Diagnostics.Stopwatch.Frequency / Look.Enter;
        SpaceSettings s = store.S;
        if (t >= 1)
        {
            widthDeg = s.WidthDeg;
            distance = s.Distance;
            curve = s.Curve;
            StopGlide();
        }
        else
        {
            // Fast at first and settling without overshoot, as everything that arrives.
            double k = 1 - Math.Pow(1 - t, 3);
            widthDeg = fromWidth + (s.WidthDeg - fromWidth) * k;
            distance = fromDistance + (s.Distance - fromDistance) * k;
            curve = fromCurve + (s.Curve - fromCurve) * k;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Size size = new(ActualWidth, ActualHeight);
        if (size.Width <= 20 || size.Height <= 20) return;
        double r = Theme.CardRadius;
        dc.PushClip(new RectangleGeometry(new Rect(size), r, r));
        dc.DrawRectangle(Ground, null, new Rect(size));

        SpaceSettings s = store.S;
        int count = Math.Min(Math.Max(s.Screens, 1), ScreenCount.Max);
        var aspects = Enumerable.Repeat(16.0 / 9.0, count).ToList();
        PanelPlacement[] placements = SpaceLayout.Arrange(s.Layout, aspects, widthDeg, distance, Math.Max(curve, 0));
        bool tracking = state.TrackingOn && s.HeadTracking;
        double headYaw = state.YawDeg;
        double reach = Math.Max(placements.Length > 0 ? placements.Max(p => p.Distance) : SpaceLayout.DefaultDistance, 0.3);

        var camera = new SettingsPreviewCamera(reach);
        int? focus = FocusedIndex(placements, headYaw);
        var slabs = new List<SettingsPreviewSlab>();
        for (int i = 0; i < placements.Length; i++) slabs.Add(new SettingsPreviewSlab(placements[i], focus == i));
        var fit = new SettingsPreviewFit(camera, slabs, size);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var labels = DrawRings(dc, camera, fit, reach, dip);
        DrawField(dc, camera, fit, reach, tracking, headYaw);
        var drawn = new List<Geometry>();
        foreach (SettingsPreviewSlab slab in slabs.OrderByDescending(a => a.Centre.Minus(camera.Position).Dot(camera.Forward)))
        {
            if (DrawSlab(dc, camera, fit, slab, Theme.C(s.GlowColor.Color()), s.Glow) is Geometry shape) drawn.Add(shape);
        }
        // Each ring's distance, unless a screen stands in front of the words.
        foreach ((FormattedText text, Point at) in labels)
        {
            var box = new Rect(at.X - 4, at.Y - 2, text.Width + 8, text.Height + 4);
            Point[] corners = { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight, new(box.X + box.Width / 2, box.Y + box.Height / 2) };
            if (!drawn.Any(g => corners.Any(g.FillContains))) dc.DrawText(text, at);
        }
        DrawHead(dc, camera, fit, tracking, headYaw);
        dc.Pop();
        dc.DrawRoundedRectangle(null, RimPen, new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), r, r);
    }

    /// <summary>The screen the head points at, if any.</summary>
    private static int? FocusedIndex(IReadOnlyList<PanelPlacement> placements, double headYawDeg)
    {
        int? best = null;
        double bestOff = 1e9;
        for (int i = 0; i < placements.Count; i++)
        {
            PanelPlacement p = placements[i];
            double off = Math.IEEERemainder(p.YawDeg - headYawDeg, 360);
            double a = Math.Abs(off);
            if (a <= p.WidthDeg / 2 + 1 && a < bestOff)
            {
                best = i;
                bestOff = a;
            }
        }
        return best;
    }

    // A ring on the floor of the picture at each metre from the head, so distance can be read.
    private static List<(FormattedText Text, Point At)> DrawRings(DrawingContext dc, SettingsPreviewCamera camera, SettingsPreviewFit fit, double reach, double dip)
    {
        int count = Math.Min((int)Math.Ceiling(reach), 6);
        var labels = new List<(FormattedText Text, Point At)>();
        for (int metre = 1; metre <= count; metre++)
        {
            double r = metre;
            var g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                bool started = false;
                for (int i = 0; i <= 72; i++)
                {
                    double a = i * Math.PI / 36;
                    if (camera.Flat(new SettingsVec(r * Math.Sin(a), 0, -r * Math.Cos(a))) is SettingsFlat f)
                    {
                        Point point = fit.Point(f);
                        if (started) c.LineTo(point, true, true);
                        else
                        {
                            c.BeginFigure(point, false, false);
                            started = true;
                        }
                    }
                    else
                    {
                        started = false;
                    }
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, RingPen, g);
            if (camera.Flat(new SettingsVec(r * Math.Sin(1.15), 0, -r * Math.Cos(1.15))) is SettingsFlat at)
            {
                FormattedText text = Draw.Text($"{metre} m", 10, RingInk, FontWeights.Normal, dip);
                Point p = fit.Point(at);
                labels.Add((text, new Point(p.X + 12 - text.Width / 2, p.Y - text.Height / 2)));
            }
        }
        return labels;
    }

    // The field of view: a wedge of light from the head, turned with it while tracking.
    private static void DrawField(DrawingContext dc, SettingsPreviewCamera camera, SettingsPreviewFit fit, double reach,
        bool tracking, double headYawDeg)
    {
        double yaw = (tracking ? headYawDeg : 0) * Math.PI / 180, half = HalfFieldDeg * Math.PI / 180, length = reach * 1.08;
        if (camera.Flat(new SettingsVec(0, 0, 0)) is not SettingsFlat origin) return;
        Point start = fit.Point(origin), tip = start;
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(start, true, true);
            for (int i = 0; i <= 16; i++)
            {
                double a = yaw - half + 2 * half * i / 16;
                // Left positive: a turn to the left moves the wedge towards -x.
                if (camera.Flat(new SettingsVec(-length * Math.Sin(a), 0, -length * Math.Cos(a))) is not SettingsFlat f) continue;
                Point point = fit.Point(f);
                c.LineTo(point, true, true);
                if (i == 8) tip = point;
            }
        }
        g.Freeze();
        double strength = tracking ? 1 : 0.55;
        var light = new LinearGradientBrush(Theme.C(Look.Accent.Alpha(0.26 * strength)), Theme.C(Look.Accent.Alpha(0.03 * strength)), start, tip)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        dc.DrawGeometry(light, new Pen(Theme.B(Look.Accent.Alpha(0.22 * strength)), 1), g);
    }

    private static Geometry? DrawSlab(DrawingContext dc, SettingsPreviewCamera camera, SettingsPreviewFit fit, SettingsPreviewSlab slab,
        Color glow, double glowAmount)
    {
        var top = new List<Point>();
        var bottom = new List<Point>();
        foreach (SettingsVec v in slab.Top)
        {
            if (camera.Flat(v) is not SettingsFlat f) return null;
            top.Add(fit.Point(f));
        }
        foreach (SettingsVec v in slab.Bottom)
        {
            if (camera.Flat(v) is not SettingsFlat f) return null;
            bottom.Add(fit.Point(f));
        }
        if (top.Count == 0 || bottom.Count == 0) return null;
        var path = new StreamGeometry();
        using (StreamGeometryContext c = path.Open())
        {
            c.BeginFigure(top[0], true, true);
            for (int i = 1; i < top.Count; i++) c.LineTo(top[i], true, true);
            for (int i = bottom.Count - 1; i >= 0; i--) c.LineTo(bottom[i], true, true);
        }
        path.Freeze();
        var midTop = new Point((top[0].X + top[^1].X) / 2, (top[0].Y + top[^1].Y) / 2);
        var midBottom = new Point((bottom[0].X + bottom[^1].X) / 2, (bottom[0].Y + bottom[^1].Y) / 2);
        var glass = new LinearGradientBrush(Theme.C(Look.GlassTop.Alpha(0.94)), Theme.C(Look.GlassBottom.Alpha(0.94)), midTop, midBottom)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        dc.DrawGeometry(glass, null, path);

        // A line of light along the top edge, as glass catches it.
        var edge = new StreamGeometry();
        using (StreamGeometryContext c = edge.Open())
        {
            c.BeginFigure(top[0], false, false);
            for (int i = 1; i < top.Count; i++) c.LineTo(top[i], true, true);
        }
        edge.Freeze();
        dc.DrawGeometry(null, TopLight, edge);

        if (slab.Focused && glowAmount > 0.0001)
        {
            dc.DrawGeometry(null, new Pen(Theme.B(glow, 0.22 * glowAmount), 6) { LineJoin = PenLineJoin.Round }, path);
            dc.DrawGeometry(null, new Pen(Theme.B(glow, 0.35 + 0.6 * glowAmount), 1.5) { LineJoin = PenLineJoin.Round }, path);
        }
        else
        {
            dc.DrawGeometry(null, slab.Focused ? RimFocusPen : RimPen, path);
        }
        return path;
    }

    // The head: a small disc with the glasses as a bar of accent across its front.
    private static void DrawHead(DrawingContext dc, SettingsPreviewCamera camera, SettingsPreviewFit fit, bool tracking, double headYawDeg)
    {
        if (camera.Flat(new SettingsVec(0, 0, 0)) is not SettingsFlat origin) return;
        Point centre = fit.Point(origin);
        double radius = Math.Min(Math.Max(fit.Focal * 0.10 / origin.Depth, 5), 13);
        dc.DrawEllipse(Theme.Control, HeadPen, centre, radius, radius * 0.82);

        double yaw = (tracking ? headYawDeg : 0) * Math.PI / 180;
        double reachOut = radius / Math.Max(fit.Focal, 1) * origin.Depth;
        var front = new SettingsVec(-reachOut * Math.Sin(yaw), 0, -reachOut * Math.Cos(yaw));
        SettingsVec side = new SettingsVec(Math.Cos(yaw), 0, -Math.Sin(yaw)).Times(reachOut * 0.95);
        if (camera.Flat(front.Minus(side)) is not SettingsFlat a || camera.Flat(front.Plus(side)) is not SettingsFlat b) return;
        Point from = fit.Point(a), to = fit.Point(b);
        dc.DrawLine(new Pen(Theme.B(Look.Accent.Alpha(0.30)), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, to);
        dc.DrawLine(new Pen(tracking ? Theme.Held : Theme.Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, to);
    }
}

/// <summary>The preview card: the centrepiece of the Space pane.</summary>
public static class SettingsSpacePreview
{
    public const double Height = 260;

    public static FrameworkElement Card(SettingsStore store, AppState state, Binder binder)
    {
        var canvas = new SettingsSpaceCanvas(store, state);
        TextBlock summary = Kit.Text("", Theme.NoteSize, Theme.Ink2, wrap: false, digits: true);
        summary.VerticalAlignment = VerticalAlignment.Center;
        Border follows = Kit.Chip("");
        var caption = new DockPanel { Margin = new Thickness(14, 12, 14, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        DockPanel.SetDock(follows, Dock.Right);
        caption.Children.Add(follows);
        caption.Children.Add(summary);
        var card = new Grid { Height = Height };
        card.Children.Add(canvas);
        card.Children.Add(caption);
        AutomationProperties.SetName(card, "Your Space, seen from above");
        binder.Add(() =>
        {
            SpaceSettings s = store.S;
            string screens = s.Screens == 1 ? "1 screen" : $"{s.Screens} screens";
            summary.Text = $"{screens} · {SettingsFormat.Degrees(s.WidthDeg)} wide · {SettingsFormat.Metres(s.Distance)} away";
            AutomationProperties.SetHelpText(card, summary.Text);
            ((TextBlock)follows.Child).Text = state.TrackingOn ? "Follows your head" : "Head tracking is off";
            Kit.SetChip(follows, state.TrackingOn);
            canvas.Sync();
        });
        return card;
    }
}

/// <summary>Where the head points: a horizon that drops as you look up, and a ring of marks that turns with your
/// head, the recentred forward marked in the accent.</summary>
public sealed class HeadGauge : FrameworkElement
{
    private readonly AppState state;

    public HeadGauge(AppState state)
    {
        this.state = state;
        AutomationProperties.SetName(this, "Head direction");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double yawDeg = state.YawDeg, pitchDeg = state.PitchDeg;
        bool live = state.TrackingOn;
        double side = Math.Min(ActualWidth, ActualHeight);
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = side / 2 - 2, inner = outer - 12;
        double strength = live ? 1 : 0.4;

        // The horizon, inside the face.
        dc.PushClip(new EllipseGeometry(centre, inner, inner));
        dc.DrawRectangle(Theme.Pad, null, new Rect(centre.X - inner, centre.Y - inner, inner * 2, inner * 2));
        double limited = Math.Min(Math.Max(pitchDeg, -60), 60);
        double horizonY = centre.Y + limited / 60 * inner;
        dc.DrawRectangle(Theme.B(Look.Accent.Alpha(0.14 * strength)), null, new Rect(centre.X - inner, horizonY, inner * 2, inner * 4));
        dc.DrawLine(new Pen(Theme.B(Look.Accent.Alpha(0.8 * strength)), 1.5), new Point(centre.X - inner, horizonY), new Point(centre.X + inner, horizonY));
        dc.Pop();
        dc.DrawEllipse(null, new Pen(Theme.Rim, 1), centre, inner, inner);

        // The fixed mark: where the glasses point.
        var mark = new Pen(Theme.White(0.9), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(mark, new Point(centre.X - 14, centre.Y), new Point(centre.X - 5, centre.Y));
        dc.DrawLine(mark, new Point(centre.X + 5, centre.Y), new Point(centre.X + 14, centre.Y));
        dc.DrawEllipse(Theme.White(0.9), null, centre, 2, 2);

        // The ring of marks: forward sits at the top when you face it, and moves right as you turn left.
        for (int i = 0; i < 12; i++)
        {
            double a = (i * 30 + yawDeg) * Math.PI / 180;
            double sx = Math.Sin(a), cy = Math.Cos(a);
            bool forward = i == 0;
            double from = forward ? inner + 2 : inner + 5;
            var pen = new Pen(forward ? Theme.B(Look.Accent.Alpha(strength)) : Theme.White(0.28), forward ? 3 : 1)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            };
            dc.DrawLine(pen, new Point(centre.X + sx * from, centre.Y - cy * from), new Point(centre.X + sx * outer, centre.Y - cy * outer));
        }
    }
}
