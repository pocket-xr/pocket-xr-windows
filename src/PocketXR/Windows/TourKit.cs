using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// What the tour's pictures are drawn with (Pocket XR for Mac's TourKit.swift, for WPF). Everything is "Light on
// glass": smoked glass with a hairline rim, one accent, depth shown with light and never shadow. The colours and
// motion times are Look's.
//
// All of the pictures' arithmetic is in double (seconds, points, metres), and turned into WPF values only in the few
// drawing functions at the bottom of TourDraw.

/// <summary>A point of a picture, in the canvas's points.</summary>
public readonly struct TourPoint
{
    public readonly double X, Y;

    public TourPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public Point P => new(X, Y);
}

/// <summary>A rectangle of a picture.</summary>
public readonly struct TourBox
{
    public readonly double X, Y, W, H;

    public TourBox(double x, double y, double w, double h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;
    public double MaxX => X + W;
    public double MaxY => Y + H;
    public TourPoint Centre => new(MidX, MidY);
    public Rect R => new(X, Y, Math.Max(W, 0), Math.Max(H, 0));

    /// <summary>The point ([u], [v]) of the box: 0, 0 its top left and 1, 1 its bottom right.</summary>
    public TourPoint At(double u, double v) => new(X + W * u, Y + H * v);

    /// <summary>The part of the box from ([u0], [v0]) to ([u1], [v1]).</summary>
    public TourBox Part(double u0, double v0, double u1, double v1) => new(X + W * u0, Y + H * v0, W * (u1 - u0), H * (v1 - v0));

    public TourBox Inset(double d) => new(X + d, Y + d, Math.Max(W - 2 * d, 0), Math.Max(H - 2 * d, 0));
}

/// <summary>The pictures' colours, as numbers so they can be mixed and faded.</summary>
public static class TourTone
{
    public static readonly LookColor Accent = Look.Accent;
    public static readonly LookColor AccentDeep = Look.AccentDeep;
    public static readonly LookColor Held = Look.Held;
    public static readonly LookColor Ink = Look.Ink;
    public static readonly LookColor Ink2 = Look.Ink2;
    public static readonly LookColor Ink3 = Look.Ink3;
    public static readonly LookColor Rim = Look.Rim;
    public static readonly LookColor RimFocus = Look.RimFocus;
    public static readonly LookColor Page = Look.Page;
    public static readonly LookColor Pad = Look.Pad;
    public static readonly LookColor Card = Look.Card;
    public static readonly LookColor GlassTop = Look.GlassTop;
    public static readonly LookColor GlassBottom = Look.GlassBottom;
    public static readonly LookColor GlassRaised = Look.GlassRaised;
    public static readonly LookColor Control = Look.Control;
    public static readonly LookColor Media = Look.Media;
    public static readonly LookColor White = new(1, 1, 1);
    /// <summary>A lens with a picture in it.</summary>
    public static readonly LookColor Lens = new(hex: 0x24365E);
    /// <summary>A small window on a screen: glass a step lighter than the screen it is on.</summary>
    public static readonly LookColor Window = new(hex: 0x2F3850);
    /// <summary>A key's lower edge.</summary>
    public static readonly LookColor KeyEdge = new(hex: 0x12151C);

    /// <summary>White at [alpha]: the ink of a sketch.</summary>
    public static LookColor Tint(double alpha) => new(1, 1, 1, alpha);
}

/// <summary>The look's curves as functions of time (LookEase on the Mac).</summary>
public static class TourEase
{
    public static double Bezier((double, double, double, double) p, double t)
    {
        t = Math.Min(Math.Max(t, 0), 1);
        // Solve x(s) = t for s by Newton's method, then return y(s).
        static double F(double a, double b, double s)
        {
            double u = 1 - s;
            return 3 * u * u * s * a + 3 * u * s * s * b + s * s * s;
        }
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = F(p.Item1, p.Item3, s) - t;
            double u = 1 - s;
            double dx = 3 * u * u * p.Item1 + 6 * u * s * (p.Item3 - p.Item1) + 3 * s * s * (1 - p.Item3);
            if (Math.Abs(dx) < 1e-6) break;
            s = Math.Min(Math.Max(s - x / dx, 0), 1);
        }
        return F(p.Item2, p.Item4, s);
    }

    public static double Enter(double t) => Bezier(Look.EaseEnter, t);
    public static double Exit(double t) => Bezier(Look.EaseExit, t);
    public static double InOut(double t) => Bezier(Look.EaseInOut, t);
    /// <summary>Slow drift.</summary>
    public static double Sine(double t) => 0.5 - 0.5 * Math.Cos(Math.PI * Math.Min(Math.Max(t, 0), 1));
    /// <summary>0 before [from], 1 after [to], linear between.</summary>
    public static double Span(double t, double from, double to) =>
        to <= from ? (t >= to ? 1 : 0) : Math.Min(Math.Max((t - from) / (to - from), 0), 1);
}

public enum TourCurve
{
    /// <summary>Everything that arrives or moves.</summary>
    Enter,
    /// <summary>Everything that leaves.</summary>
    Exit,
    InOut,
    /// <summary>Slow drifting.</summary>
    Sine,
    /// <summary>Only for a hold filling.</summary>
    Linear,
}

/// <summary>Time and mixing for pictures drawn as a function of the seconds since they came up.</summary>
public static class TourMath
{
    public static double Ease(TourCurve e, double k) => e switch
    {
        TourCurve.Enter => TourEase.Enter(k),
        TourCurve.Exit => TourEase.Exit(k),
        TourCurve.InOut => TourEase.InOut(k),
        TourCurve.Sine => TourEase.Sine(k),
        _ => Math.Min(Math.Max(k, 0), 1),
    };

    /// <summary>How far [t] is from [from] to [to], 0 to 1, eased.</summary>
    public static double Span(double t, double from, double to, TourCurve e = TourCurve.Enter) => Ease(e, TourEase.Span(t, from, to));

    public static double Mix(double a, double b, double k) => a + (b - a) * k;

    public static TourPoint MixP(TourPoint a, TourPoint b, double k) => new(Mix(a.X, b.X, k), Mix(a.Y, b.Y, k));

    public static TourBox MixB(TourBox a, TourBox b, double k) => new(Mix(a.X, b.X, k), Mix(a.Y, b.Y, k), Mix(a.W, b.W, k), Mix(a.H, b.H, k));

    public static LookColor MixC(LookColor a, LookColor b, double k)
    {
        double c = Math.Min(Math.Max(k, 0), 1);
        return new LookColor(Mix(a.R, b.R, c), Mix(a.G, b.G, c), Mix(a.B, b.B, c), Mix(a.A, b.A, c));
    }

    /// <summary>A one-off between [at] and [at] + [length]: its progress 0 to 1, or null outside.</summary>
    public static double? Burst(double t, double at, double length)
    {
        if (!(length > 0)) return null;
        double k = (t - at) / length;
        return k >= 0 && k <= 1 ? k : null;
    }

    /// <summary>A one-off that rises and falls back, 0 outside: a press, an edge lighting as something crosses it.</summary>
    public static double Pulse(double t, double at, double length) => Burst(t, at, length) is double k ? Math.Sin(Math.PI * k) : 0;

    /// <summary>Fades a looping picture in at its start and out before [length], so the loop never jumps.</summary>
    public static double LoopAlpha(double t, double length) =>
        Span(t, 0, Tour.LoopFadeIn) * (1 - Span(t, length - Tour.LoopFadeOut, length, TourCurve.Exit));

    public static double Radians(double degrees) => degrees * Math.PI / 180;
}

// ---- the Space, seen through the glasses ----

/// <summary>
/// The eye a picture is seen from: turned [YawDeg] to the right and [PitchDeg] up, standing [ShiftX] metres to the
/// right, [Back] metres behind the viewer's own place and [Up] metres above it, drawing [Focal] points at 1 m about
/// ([Cx], [Cy]).
/// </summary>
public struct TourCamera
{
    public double YawDeg, Focal, Cx, Cy, ShiftX, PitchDeg, Back, Up;

    public TourCamera(double yawDeg, double focal, double cx, double cy, double shiftX = 0)
    {
        YawDeg = yawDeg;
        Focal = focal;
        Cx = cx;
        Cy = cy;
        ShiftX = shiftX;
        PitchDeg = 0;
        Back = 0;
        Up = 0;
    }

    /// <summary>The canvas point of a place in the Space ([wx] metres right, [wy] up, [wz] ahead), or null behind the eye.</summary>
    public readonly TourPoint? Project(double wx, double wy, double wz)
    {
        double c = TourMath.Radians(YawDeg), p = TourMath.Radians(PitchDeg);
        double dx = wx - ShiftX, dy = wy - Up, dz = wz + Back;
        double x = dx * Math.Cos(c) - dz * Math.Sin(c);
        double z1 = dx * Math.Sin(c) + dz * Math.Cos(c);
        double z = z1 * Math.Cos(p) + dy * Math.Sin(p);
        double y = dy * Math.Cos(p) - z1 * Math.Sin(p);
        if (z < 0.2) return null;
        return new TourPoint(Cx + Focal * x / z, Cy - Focal * y / z);
    }

    /// <summary>How far ahead of the eye a place is (for drawing far things first).</summary>
    public readonly double Depth(double wx, double wy, double wz)
    {
        double c = TourMath.Radians(YawDeg), p = TourMath.Radians(PitchDeg);
        double z1 = (wx - ShiftX) * Math.Sin(c) + (wz + Back) * Math.Cos(c);
        return z1 * Math.Cos(p) + (wy - Up) * Math.Sin(p);
    }
}

/// <summary>
/// A screen in the Space: its middle [Theta] degrees to the right at [R] metres and [Y] metres up, [W] by [H] metres,
/// facing the viewer's place (as Pocket XR sets screens around you). [Curve] bends it round the viewer: 0 flat, 1 an
/// arc centred on the viewer. [U0]...[V1] pick a part of it, so a small window on a screen is drawn the same way.
/// </summary>
public struct TourPanel
{
    public double Theta, Y, W, H, R, Curve, U0, V0, U1, V1;

    public TourPanel(double theta, double y, double w, double h, double r = 1.5, double curve = 0)
    {
        Theta = theta;
        Y = y;
        W = w;
        H = h;
        R = r;
        Curve = curve;
        U0 = 0;
        V0 = 0;
        U1 = 1;
        V1 = 1;
    }

    /// <summary>The part of this panel from ([a], [b]) to ([c], [d]) of what it shows now.</summary>
    public readonly TourPanel Part(double a, double b, double c, double d)
    {
        TourPanel p = this;
        p.U0 = U0 + (U1 - U0) * a;
        p.V0 = V0 + (V1 - V0) * b;
        p.U1 = U0 + (U1 - U0) * c;
        p.V1 = V0 + (V1 - V0) * d;
        return p;
    }

    /// <summary>Where ([u], [v]) of the panel is in the Space: metres right, up and ahead.</summary>
    public readonly (double X, double Y, double Z) Place(double u, double v)
    {
        double uu = U0 + (U1 - U0) * u, vv = V0 + (V1 - V0) * v;
        double a = TourMath.Radians(Theta);
        double along = (uu - 0.5) * W;
        double lx = along, lz = R;
        if (Curve > 0.001)
        {
            double radius = R / Curve;
            double phi = along / radius;
            lx = radius * Math.Sin(phi);
            lz = R - radius * (1 - Math.Cos(phi));
        }
        double wx = lz * Math.Sin(a) + lx * Math.Cos(a);
        double wz = lz * Math.Cos(a) - lx * Math.Sin(a);
        double wy = Y + (0.5 - vv) * H;
        return (wx, wy, wz);
    }

    /// <summary>The canvas point of ([u], [v]) on the panel (0, 0 top left; 1, 1 bottom right), or null behind the eye.</summary>
    public readonly TourPoint? At(TourCamera cam, double u, double v)
    {
        (double x, double y, double z) = Place(u, v);
        return cam.Project(x, y, z);
    }

    /// <summary>How far ahead of the eye the panel's middle is.</summary>
    public readonly double Depth(TourCamera cam)
    {
        (double x, double y, double z) = Place(0.5, 0.5);
        return cam.Depth(x, y, z);
    }

    /// <summary>The panel's outline with rounded corners ([radius] metres), or null when any of it is behind the eye.</summary>
    public readonly Geometry? Outline(TourCamera cam, double radius = 0.035)
    {
        double partW = Math.Max(W * Math.Abs(U1 - U0), 0.0001), partH = Math.Max(H * Math.Abs(V1 - V0), 0.0001);
        double ru = Math.Min(radius / partW, 0.5), rv = Math.Min(radius / partH, 0.5);
        // A bent panel's long edges are drawn as several short ones.
        int steps = Curve > 0.001 ? 12 : 1;
        var top = new TourPoint[steps + 1];
        var bottom = new TourPoint[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double u = ru + (1 - 2 * ru) * i / steps;
            if (At(cam, u, 0) is not TourPoint a || At(cam, u, 1) is not TourPoint b) return null;
            top[i] = a;
            bottom[i] = b;
        }
        if (At(cam, 0, 0) is not TourPoint c00 || At(cam, 1, 0) is not TourPoint c10 || At(cam, 1, 1) is not TourPoint c11
            || At(cam, 0, 1) is not TourPoint c01 || At(cam, 1, rv) is not TourPoint r0 || At(cam, 1, 1 - rv) is not TourPoint r1
            || At(cam, 0, rv) is not TourPoint l0 || At(cam, 0, 1 - rv) is not TourPoint l1)
            return null;
        var path = new StreamGeometry();
        using (StreamGeometryContext g = path.Open())
        {
            g.BeginFigure(top[0].P, true, true);
            for (int i = 1; i <= steps; i++) g.LineTo(top[i].P, true, true);
            g.QuadraticBezierTo(c10.P, r0.P, true, true);
            g.LineTo(r1.P, true, true);
            g.QuadraticBezierTo(c11.P, bottom[steps].P, true, true);
            for (int i = steps - 1; i >= 0; i--) g.LineTo(bottom[i].P, true, true);
            g.QuadraticBezierTo(c01.P, l1.P, true, true);
            g.LineTo(l0.P, true, true);
            g.QuadraticBezierTo(c00.P, top[0].P, true, true);
        }
        path.Freeze();
        return path;
    }

    /// <summary>A four cornered patch of the panel, for a block of its content.</summary>
    public readonly Geometry? Patch(TourCamera cam, double a, double b, double c, double d)
    {
        // On a bent panel the patch's long edges bend with it.
        int steps = Curve > 0.001 ? 8 : 1;
        var points = new List<TourPoint>(2 * steps + 2);
        for (int i = 0; i <= steps; i++)
        {
            double u = a + (c - a) * i / steps;
            if (At(cam, u, b) is not TourPoint p) return null;
            points.Add(p);
        }
        for (int i = 0; i <= steps; i++)
        {
            double u = c - (c - a) * i / steps;
            if (At(cam, u, d) is not TourPoint p) return null;
            points.Add(p);
        }
        return TourDraw.Poly(points, closed: true);
    }
}

/// <summary>What a screen shows: a sketch of an app, never a real one.</summary>
public enum TourKind { Text, Media, Chat, List, Blank }

/// <summary>The canvas a picture is drawn on, and the eye it is usually seen from.</summary>
public readonly struct TourStage
{
    public readonly double W, H;

    public TourStage(double w, double h)
    {
        W = w;
        H = h;
    }

    /// <summary>Points at 1 m for a picture that fills the canvas.</summary>
    public double Focal => Math.Min(W, H * 1.35) * 0.92;

    public TourCamera Cam(double yaw = 0, double shiftX = 0, double cyShare = 0.5, double zoom = 1) =>
        new(yaw, Focal * zoom, W / 2, H * cyShare, shiftX);
}

/// <summary>
/// The drawing primitives, on a WPF DrawingContext: geometry, gradients and opacity only, no bitmaps. WPF's drawing
/// has no blur, so soft light (a halo, a glow) is a few wide strokes of falling strength laid over each other.
/// </summary>
public static class TourDraw
{
    /// <summary>Pixels per point of the element being drawn, for text. Set before a picture is drawn.</summary>
    public static double Dip = 1;

    // ---- screens and windows ----

    /// <summary>
    /// A screen's glass: the focus halo just outside its edge (light, not shadow) and the smoked fill. Returns the
    /// outline for its content to be clipped to and its rim drawn on, or null when it is out of sight.
    /// </summary>
    public static Geometry? Glass(DrawingContext g, TourCamera cam, TourPanel win, double focus = 0, double held = 0, double alpha = 1)
    {
        if (alpha <= 0.01) return null;
        if (win.Outline(cam) is not Geometry outline) return null;
        Halo(g, outline, focus, held, alpha);
        g.DrawGeometry(GlassBrush(alpha), null, outline);
        return outline;
    }

    /// <summary>The halo: soft light of the accent outside an edge. [held] turns it to the "you are holding this" colour.</summary>
    public static void Halo(DrawingContext g, Geometry outline, double focus, double held, double alpha, double reach = 1)
    {
        double glow = Math.Max(focus, held);
        if (glow <= 0.01) return;
        LookColor tone = TourMath.MixC(TourTone.Accent, TourTone.Held, held);
        double k = glow * alpha;
        Stroke(g, outline, tone.Alpha(0.030), 30 * reach, k);
        Stroke(g, outline, tone.Alpha(0.045), 21 * reach, k);
        Stroke(g, outline, tone.Alpha(0.065), 13 * reach, k);
        Stroke(g, outline, tone.Alpha(0.10), 6 * reach, k);
    }

    /// <summary>The hairline rim: brighter in focus, the held colour while carried.</summary>
    public static void Rim(DrawingContext g, Geometry outline, double focus = 0, double held = 0, double alpha = 1)
    {
        LookColor tone = TourMath.MixC(TourMath.MixC(TourTone.Rim, TourTone.RimFocus, focus), TourTone.Held, held);
        Stroke(g, outline, tone, 1.5, alpha);
    }

    /// <summary>A window as the glasses show it: glass, a sketch of an app, the rim.</summary>
    public static void Window(DrawingContext g, TourCamera cam, TourPanel win, TourKind kind, double focus = 0, double held = 0, double alpha = 1)
    {
        if (Glass(g, cam, win, focus, held, alpha) is not Geometry outline) return;
        Content(g, cam, win, kind, alpha);
        Rim(g, outline, focus, held, alpha);
    }

    /// <summary>One of the PC's floating screens: glass with the taskbar's strip along its bottom and an app's window on it.</summary>
    public static void Screen(DrawingContext g, TourCamera cam, TourPanel win, TourKind kind, double focus = 0, double held = 0, double alpha = 1)
    {
        if (Glass(g, cam, win, focus, held, alpha) is not Geometry outline) return;
        g.PushClip(outline);
        TaskStrip(g, cam, win, alpha);
        if (kind != TourKind.Blank)
        {
            TourPanel app = win.Part(0.09, 0.1, 0.91, 0.82);
            MiniWindow(g, cam, app, kind, alpha: alpha);
        }
        g.Pop();
        Rim(g, outline, focus, held, alpha);
    }

    /// <summary>The taskbar along a screen's bottom: a lighter strip with a few marks in its middle and the clock at
    /// its right, enough to say "this is a PC's display".</summary>
    public static void TaskStrip(DrawingContext g, TourCamera cam, TourPanel win, double alpha)
    {
        Block(g, cam, win, 0, 0.915, 1, 1, TourTone.Tint(0.06), alpha);
        WinLine(g, cam, win, 0.435, 0.45, 0.958, TourTone.Tint(0.5), alpha, 2.5);
        WinLine(g, cam, win, 0.475, 0.49, 0.958, TourTone.Tint(0.2), alpha, 2.5);
        WinLine(g, cam, win, 0.515, 0.53, 0.958, TourTone.Tint(0.2), alpha, 2.5);
        WinLine(g, cam, win, 0.555, 0.57, 0.958, TourTone.Tint(0.2), alpha, 2.5);
        WinLine(g, cam, win, 0.9, 0.96, 0.958, TourTone.Tint(0.2), alpha, 2);
    }

    /// <summary>An app's window on a screen: its own glass, the three marks of its corner, a sketch of what it shows.
    /// [win] is the part of the screen it covers.</summary>
    public static void MiniWindow(DrawingContext g, TourCamera cam, TourPanel win, TourKind kind, double held = 0, double alpha = 1)
    {
        if (win.Outline(cam, radius: 0.022) is not Geometry outline) return;
        Halo(g, outline, 0, held, alpha);
        Fill(g, outline, TourTone.Window, alpha);
        // Minimise, maximise, close: at the right, as Windows has them.
        double dotU = 0.95;
        for (int i = 0; i < 3; i++)
        {
            if (win.At(cam, dotU, 0.085) is TourPoint p) Disc(g, p, 1.8, TourTone.Tint(0.4), alpha);
            dotU -= 0.045;
        }
        Content(g, cam, win.Part(0, 0.12, 1, 1), kind, alpha);
        LookColor tone = TourMath.MixC(TourTone.RimFocus, TourTone.Held, held);
        Stroke(g, outline, tone, 1.2, alpha);
    }

    /// <summary>The same window flat on the PC's own screen.</summary>
    public static void MiniWindowFlat(DrawingContext g, TourBox box, double held = 0, double alpha = 1)
    {
        double radius = Math.Min(box.W, box.H) * 0.08;
        Geometry outline = RoundRect(box, radius);
        Halo(g, outline, 0, held, alpha);
        Fill(g, outline, TourTone.Window, alpha);
        for (int i = 0; i < 3; i++)
            Disc(g, box.At(0.93 - 0.065 * i, 0.12), Math.Max(box.W * 0.016, 1.2), TourTone.Tint(0.4), alpha);
        double[] ends = { 0.86, 0.7, 0.8 };
        for (int i = 0; i < ends.Length; i++)
        {
            double v = 0.38 + 0.2 * i;
            Line(g, box.At(0.1, v), box.At(ends[i], v), TourTone.Tint(0.16), 2.5, alpha);
        }
        LookColor tone = TourMath.MixC(TourTone.RimFocus, TourTone.Held, held);
        Stroke(g, outline, tone, 1.2, alpha);
    }

    /// <summary>A line of a screen's content from ([a], [v]) to ([b], [v]).</summary>
    public static void WinLine(DrawingContext g, TourCamera cam, TourPanel win, double a, double b, double v, LookColor c, double alpha, double width = 3)
    {
        if (win.At(cam, a, v) is not TourPoint p || win.At(cam, b, v) is not TourPoint q) return;
        Line(g, p, q, c, width, alpha);
    }

    public static void Block(DrawingContext g, TourCamera cam, TourPanel win, double a, double b, double c, double d, LookColor tone, double alpha)
    {
        if (win.Patch(cam, a, b, c, d) is not Geometry path) return;
        Fill(g, path, tone, alpha);
    }

    public static void Content(DrawingContext g, TourCamera cam, TourPanel win, TourKind kind, double alpha)
    {
        LookColor soft = TourTone.Tint(0.16), softer = TourTone.Tint(0.09);
        switch (kind)
        {
            case TourKind.Blank:
                break;
            case TourKind.Text:
            {
                WinLine(g, cam, win, 0.1, 0.5, 0.16, TourTone.Tint(0.34), alpha, 4);
                double[] ends = { 0.9, 0.84, 0.88, 0.6, 0.86, 0.72 };
                double v = 0.32;
                foreach (double end in ends)
                {
                    WinLine(g, cam, win, 0.1, end, v, soft, alpha);
                    v += 0.105;
                }
                break;
            }
            case TourKind.Media:
            {
                Block(g, cam, win, 0.07, 0.1, 0.93, 0.7, TourTone.Media, alpha);
                // A play mark in the picture's middle.
                if (win.At(cam, 0.46, 0.31) is TourPoint a && win.At(cam, 0.46, 0.49) is TourPoint b && win.At(cam, 0.56, 0.4) is TourPoint c)
                    Fill(g, Poly(new[] { a, b, c }, closed: true), TourTone.Tint(0.7), alpha);
                WinLine(g, cam, win, 0.07, 0.55, 0.8, TourTone.Tint(0.34), alpha, 4);
                WinLine(g, cam, win, 0.07, 0.38, 0.9, softer, alpha);
                break;
            }
            case TourKind.Chat:
            {
                LookColor mine = TourTone.AccentDeep.Alpha(0.75);
                Block(g, cam, win, 0.08, 0.12, 0.62, 0.26, soft, alpha);
                Block(g, cam, win, 0.4, 0.33, 0.92, 0.47, mine, alpha);
                Block(g, cam, win, 0.08, 0.54, 0.7, 0.68, soft, alpha);
                Block(g, cam, win, 0.52, 0.75, 0.92, 0.89, mine, alpha);
                break;
            }
            case TourKind.List:
            {
                double v = 0.16;
                for (int i = 0; i < 5; i++)
                {
                    Block(g, cam, win, 0.08, v - 0.045, 0.2, v + 0.045, softer, alpha);
                    WinLine(g, cam, win, 0.27, i % 2 == 0 ? 0.86 : 0.7, v, soft, alpha);
                    v += 0.17;
                }
                break;
            }
        }
    }

    // ---- the pointer ----

    private static readonly (double, double)[] Arrow = { (0, 0), (0, 16), (4.2, 12.3), (7, 18.3), (9.7, 17.1), (6.9, 11.3), (12, 11.3) };

    /// <summary>The PC's pointer, its tip at [at]: a light arrow with a dark hairline and a little glow.</summary>
    public static void Pointer(DrawingContext g, TourPoint at, double alpha = 1, double scale = 1)
    {
        if (alpha <= 0.01) return;
        Disc(g, new TourPoint(at.X + 4 * scale, at.Y + 7 * scale), 13 * scale, TourTone.Tint(0.1), alpha);
        var points = new TourPoint[Arrow.Length];
        for (int i = 0; i < Arrow.Length; i++) points[i] = new TourPoint(at.X + Arrow[i].Item1 * scale, at.Y + Arrow[i].Item2 * scale);
        Geometry arrow = Poly(points, closed: true);
        Fill(g, arrow, TourTone.White, alpha);
        Stroke(g, arrow, TourTone.Page, 1.1, alpha * 0.9);
    }

    /// <summary>A click answering: a ring going out, [k] 0 to 1.</summary>
    public static void Ripple(DrawingContext g, TourPoint at, double k, double alpha = 1)
    {
        double e = TourEase.Enter(k);
        Ring(g, at, 7 + 20 * e, TourTone.Accent.Alpha(0.7 * (1 - e)), 2, alpha);
    }

    // ---- the things themselves ----

    /// <summary>
    /// A pair of glasses, front on, [width] wide about [centre]. [lit] 0 to 1 lights the lenses in the accent.
    /// Returns the box they take, arms included.
    /// </summary>
    public static TourBox Glasses(DrawingContext g, TourPoint centre, double width, double lit = 0, double alpha = 1, double weight = 2.5)
    {
        double lensW = width * 0.4, lensH = width * 0.24, gap = width * 0.1;
        double top = centre.Y - lensH / 2;
        double leftX = centre.X - gap / 2 - lensW, rightX = centre.X + gap / 2;
        double radius = lensH * 0.34;
        LookColor frame = TourMath.MixC(TourTone.Ink, TourTone.White, lit);
        double grow = Math.Min(6, width * 0.05);
        foreach (double x in new[] { leftX, rightX })
        {
            var lens = new TourBox(x, top, lensW, lensH);
            if (lit > 0.01)
            {
                var glow = new TourBox(x - grow, top - grow, lensW + 2 * grow, lensH + 2 * grow);
                Fill(g, RoundRect(glow, radius + grow), TourTone.Accent.Alpha(0.22 * lit), alpha);
            }
            Fill(g, RoundRect(lens, radius), TourMath.MixC(TourTone.GlassRaised, TourTone.Lens, lit), alpha);
            Stroke(g, RoundRect(lens, radius), frame, weight, alpha);
        }
        double bridgeY = top + lensH * 0.3;
        Line(g, new TourPoint(centre.X - gap / 2, bridgeY), new TourPoint(centre.X + gap / 2, bridgeY), frame, weight, alpha);
        double arm = width * 0.09;
        Line(g, new TourPoint(leftX, bridgeY), new TourPoint(leftX - arm, bridgeY - arm * 0.45), frame, weight, alpha);
        Line(g, new TourPoint(rightX + lensW, bridgeY), new TourPoint(rightX + lensW + arm, bridgeY - arm * 0.45), frame, weight, alpha);
        return new TourBox(leftX - arm, top, 2 * lensW + gap + 2 * arm, lensH);
    }

    /// <summary>Where the right arm of glasses drawn in [box] ends (a cable goes in there).</summary>
    public static TourPoint GlassesArmEnd(TourBox box) => new(box.MaxX, box.Y + box.H * 0.13);

    /// <summary>
    /// A PC (a laptop), lid open, seen from the front: [box] is its screen's lid; the base is drawn under it. Returns
    /// the screen inside the lid, for the picture to draw on.
    /// </summary>
    public static TourBox Pc(DrawingContext g, TourBox box, double alpha = 1)
    {
        double radius = box.W * 0.045;
        Geometry lid = RoundRect(box, radius);
        Fill(g, lid, TourTone.GlassRaised, alpha);
        TourBox inner = PcScreen(box);
        Fill(g, RoundRect(inner, Math.Max(radius - box.W * 0.03, 1)), TourTone.Pad, alpha);
        Stroke(g, lid, TourTone.RimFocus, 1.5, alpha);
        TourBox baseBox = PcBase(box);
        Geometry slab = RoundRect(baseBox, baseBox.H / 2);
        Fill(g, slab, TourTone.GlassRaised, alpha);
        Stroke(g, slab, TourTone.RimFocus, 1.2, alpha);
        // The dip a thumb opens the lid by.
        Line(g, new TourPoint(box.MidX - box.W * 0.06, baseBox.Y + 1), new TourPoint(box.MidX + box.W * 0.06, baseBox.Y + 1), TourTone.RimFocus, 1.5, alpha);
        return inner;
    }

    public static TourBox PcScreen(TourBox box) => box.Inset(box.W * 0.03);

    public static TourBox PcBase(TourBox box)
    {
        double bw = box.W * 1.18, bh = Math.Max(box.W * 0.035, 4);
        return new TourBox(box.MidX - bw / 2, box.MaxY + 1.5, bw, bh);
    }

    /// <summary>The USB-C port on the PC's right side.</summary>
    public static TourPoint PcPort(TourBox box)
    {
        TourBox b = PcBase(box);
        return new TourPoint(b.MaxX, b.MidY);
    }

    /// <summary>How much of the PC's screen its taskbar takes, from the bottom.</summary>
    public const double TaskbarShare = 0.1;

    /// <summary>The PC's own desktop on its screen: the taskbar along the bottom with a few marks in its middle and
    /// the clock at its right, faint.</summary>
    public static void PcDesktop(DrawingContext g, TourBox inner, double alpha = 1)
    {
        TourBox strip = inner.Part(0, 1 - TaskbarShare, 1, 1);
        double r = Math.Max(inner.W * 0.016, 1);
        g.PushClip(RoundRect(inner, r));
        Fill(g, new RectangleGeometry(strip.R), TourTone.Tint(0.06), alpha);
        g.Pop();
        double v = 1 - TaskbarShare / 2, w = Math.Max(inner.W * 0.0075, 1.2);
        double[] marks = { 0.43, 0.465, 0.5, 0.535, 0.57 };
        for (int i = 0; i < marks.Length; i++)
            Disc(g, inner.At(marks[i], v), w, TourTone.Tint(i == 0 ? 0.5 : 0.2), alpha);
        Line(g, inner.At(0.915, v), inner.At(0.965, v), TourTone.Tint(0.2), 2, alpha);
    }

    /// <summary>A key: [pressed] 0 to 1 pushes it down, [lit] lights it in the accent.</summary>
    public static void KeyCap(DrawingContext g, TourPoint centre, double w, double h, string label, double pressed = 0, double lit = 0, double alpha = 1)
    {
        if (alpha <= 0.01) return;
        double radius = Math.Min(w, h) * 0.24;
        var edge = new TourBox(centre.X - w / 2, centre.Y - h / 2 + 3, w, h);
        Fill(g, RoundRect(edge, radius), TourTone.KeyEdge, alpha);
        Stroke(g, RoundRect(edge, radius), TourTone.Rim, 1, alpha);
        var top = new TourBox(centre.X - w / 2, centre.Y - h / 2 + 2.5 * pressed, w, h);
        if (lit > 0.01) Halo(g, RoundRect(top, radius), lit, 0, alpha, reach: 0.8);
        Fill(g, RoundRect(top, radius), TourMath.MixC(TourTone.Control, TourTone.Accent, lit), alpha);
        Stroke(g, RoundRect(top, radius), TourMath.MixC(TourTone.RimFocus, TourTone.White, lit), 1.2, alpha);
        // A letter fills the key; a word ("Ctrl") is set smaller.
        double size = label.Length > 1 ? h * 0.3 : h * 0.42;
        Label(g, label, new TourPoint(top.MidX, top.MidY), size, TourTone.White, alpha * 0.95);
    }

    /// <summary>A tick drawing itself inside a ring: [ring] and [tick] 0 to 1. The tick is two strokes.</summary>
    public static void TickRing(DrawingContext g, TourPoint centre, double radius, double ring, double tick, double alpha = 1, double width = 2.5)
    {
        LookColor tone = TourTone.Accent;
        if (ring > 0.001)
        {
            double sweep = 360 * Math.Min(ring, 1);
            int n = Math.Max((int)Math.Ceiling(sweep / 6), 2);
            var points = new TourPoint[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = TourMath.Radians(-90 + sweep * i / n);
                points[i] = new TourPoint(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a));
            }
            Stroke(g, Poly(points, closed: ring >= 0.999), tone, width, alpha);
        }
        if (tick > 0.001)
        {
            var a = new TourPoint(centre.X - radius * 0.42, centre.Y + radius * 0.02);
            var b = new TourPoint(centre.X - radius * 0.1, centre.Y + radius * 0.34);
            var c = new TourPoint(centre.X + radius * 0.46, centre.Y - radius * 0.3);
            double first = Math.Min(Math.Max(tick / 0.4, 0), 1), second = Math.Min(Math.Max((tick - 0.4) / 0.6, 0), 1);
            var points = new List<TourPoint> { a, TourMath.MixP(a, b, first) };
            if (second > 0) points.Add(TourMath.MixP(b, c, second));
            Stroke(g, Poly(points, closed: false), tone, width, alpha);
        }
    }

    // ---- WPF from here down ----

    private static readonly Dictionary<uint, SolidColorBrush> Brushes = new();
    private static readonly Dictionary<(uint, int, int), Pen> Pens = new();
    private static readonly Dictionary<int, LinearGradientBrush> GlassBrushes = new();

    private static uint Key(LookColor c, double alpha)
    {
        double a = c.A * Math.Min(Math.Max(alpha, 0), 1);
        static uint B(double v) => (uint)Math.Round(Math.Min(Math.Max(v, 0), 1) * 255);
        return B(a) << 24 | B(c.R) << 16 | B(c.G) << 8 | B(c.B);
    }

    public static SolidColorBrush Brush(LookColor c, double alpha = 1)
    {
        uint key = Key(c, alpha);
        if (Brushes.TryGetValue(key, out SolidColorBrush? b)) return b;
        if (Brushes.Count > 6000) Brushes.Clear();
        b = new SolidColorBrush(Color.FromArgb((byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key));
        b.Freeze();
        Brushes[key] = b;
        return b;
    }

    /// <summary>A pen with round ends and joins; [dash] and [gap] in points make it dotted.</summary>
    public static Pen PenFor(LookColor c, double width, double alpha = 1, double dash = 0, double gap = 0)
    {
        var key = (Key(c, alpha), (int)Math.Round(width * 100), (int)Math.Round(dash * 100) * 1000 + (int)Math.Round(gap * 10));
        if (Pens.TryGetValue(key, out Pen? p)) return p;
        if (Pens.Count > 6000) Pens.Clear();
        p = new Pen(Brush(c, alpha), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash > 0 && width > 0)
        {
            p.DashStyle = new DashStyle(new[] { dash / width, gap / width }, 0);
            p.DashCap = PenLineCap.Round;
        }
        p.Freeze();
        Pens[key] = p;
        return p;
    }

    private static LinearGradientBrush GlassBrush(double alpha)
    {
        int key = (int)Math.Round(Math.Min(Math.Max(alpha, 0), 1) * 255);
        if (GlassBrushes.TryGetValue(key, out LinearGradientBrush? b)) return b;
        Color top = Theme.C(TourTone.GlassTop.Alpha(key / 255.0)), bottom = Theme.C(TourTone.GlassBottom.Alpha(key / 255.0));
        b = new LinearGradientBrush(top, bottom, 90);
        b.Freeze();
        GlassBrushes[key] = b;
        return b;
    }

    public static Geometry RoundRect(TourBox box, double radius)
    {
        double r = Math.Max(Math.Min(radius, Math.Min(box.W, box.H) / 2), 0);
        var g = new RectangleGeometry(box.R, r, r);
        g.Freeze();
        return g;
    }

    public static Geometry Poly(IReadOnlyList<TourPoint> points, bool closed)
    {
        var path = new StreamGeometry();
        if (points.Count > 0)
        {
            using StreamGeometryContext g = path.Open();
            g.BeginFigure(points[0].P, closed, closed);
            for (int i = 1; i < points.Count; i++) g.LineTo(points[i].P, true, true);
        }
        path.Freeze();
        return path;
    }

    /// <summary>The part of [path] from [from] to [to] of its length (0 to 1), as short straight pieces.</summary>
    public static Geometry Trim(Geometry path, double from, double to)
    {
        PathGeometry flat = PathGeometry.CreateFromGeometry(path).GetFlattenedPathGeometry();
        from = Math.Min(Math.Max(from, 0), 1);
        to = Math.Min(Math.Max(to, 0), 1);
        const int steps = 20;
        var points = new TourPoint[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            flat.GetPointAtFractionLength(from + (to - from) * i / steps, out Point p, out _);
            points[i] = new TourPoint(p.X, p.Y);
        }
        return Poly(points, closed: false);
    }

    public static void Fill(DrawingContext g, Geometry path, LookColor c, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawGeometry(Brush(c, alpha), null, path);
    }

    /// <summary>A stroke with round ends and joins.</summary>
    public static void Stroke(DrawingContext g, Geometry path, LookColor c, double width, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawGeometry(null, PenFor(c, width, alpha), path);
    }

    public static void Dashed(DrawingContext g, Geometry path, LookColor c, double width, double dash, double gap, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawGeometry(null, PenFor(c, width, alpha, dash, gap), path);
    }

    public static void Line(DrawingContext g, TourPoint a, TourPoint b, LookColor c, double width, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawLine(PenFor(c, width, alpha), a.P, b.P);
    }

    public static void Disc(DrawingContext g, TourPoint at, double radius, LookColor c, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawEllipse(Brush(c, alpha), null, at.P, radius, radius);
    }

    public static void Ring(DrawingContext g, TourPoint at, double radius, LookColor c, double width, double alpha = 1)
    {
        if (alpha <= 0.004) return;
        g.DrawEllipse(null, PenFor(c, width, alpha), at.P, radius, radius);
    }

    /// <summary>A few words in a picture (a key's letter, a menu's row), in Windows' own face. [at] is their middle, or
    /// the middle of their left end with [leading].</summary>
    public static void Label(DrawingContext g, string words, TourPoint at, double size, LookColor c, double alpha = 1, bool leading = false)
    {
        if (alpha <= 0.01 || size < 1) return;
        FormattedText text = Draw.Text(words, size, Brush(c, alpha), FontWeights.Medium, Dip);
        g.DrawText(text, new Point(leading ? at.X : at.X - text.Width / 2, at.Y - text.Height / 2));
    }
}
