using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PocketXR.Core;
using M = PocketXR.Windows.TourMath;
using D = PocketXR.Windows.TourDraw;
using T = PocketXR.Windows.TourTone;

namespace PocketXR.Windows;

// The tour's pictures, one per step (Pocket XR for Mac's TourScenes.swift, for a PC). Each is drawn from the seconds
// since it came up, so it is the same picture every time and a still at any moment. Each has one thing the eye
// follows, what answers it, and slow life behind; nothing moves that the words do not say. The beats the words follow
// (which line is lit when) are the core's Tour.Phase; change both together.
//
// This file: the arc of screens, setup, the screens as real displays, looking around. TourScenes2.cs has the rest.

/// <summary>What the Arrange picture shows: the Space's own values, or (until the first touch) its own demonstration.</summary>
public struct TourArrangeInput
{
    public double SizeDeg, Distance, Curve;
    /// <summary>The layout being left, the one arrived at, and how far between them, 0 to 1.</summary>
    public ArrangeLayout From, To;
    public double Mix;
    public int Screens;
    /// <summary>Nobody has touched a control yet: the picture shows bigger, nearer and curved by itself.</summary>
    public bool Ghost;

    public static TourArrangeInput Plain => new()
    {
        SizeDeg = 40, Distance = 1.4, Curve = 0, From = ArrangeLayout.Row, To = ArrangeLayout.Row, Mix = 1, Screens = 2, Ghost = true,
    };
}

/// <summary>What a picture needs beside the time.</summary>
public struct TourSceneInput
{
    /// <summary>0 to 1: the glasses are really seen (the cable lights).</summary>
    public double Link;
    /// <summary>Seconds since everything needed was set up, or null while something is still to do (the last step's tick).</summary>
    public double? CompleteFor;
    public TourArrangeInput Arrange;

    public static TourSceneInput Plain => new() { Arrange = TourArrangeInput.Plain };
}

public static partial class TourScenes
{
    /// <summary>Draws [step]'s picture as it is [t] seconds after it came up.</summary>
    public static void Draw(TourStep step, DrawingContext g, Size size, double t, TourSceneInput input, double pixelsPerDip)
    {
        var s = new TourStage(size.Width, size.Height);
        if (s.W < 8 || s.H < 8) return;
        D.Dip = pixelsPerDip;
        double time = Math.Max(t, 0);
        switch (step)
        {
            case TourStep.Welcome: Welcome(g, s, time); break;
            case TourStep.Setup: Setup(g, s, time, input.Link); break;
            case TourStep.Screens: Screens(g, s, time); break;
            case TourStep.Look: LookAround(g, s, time); break;
            case TourStep.Arrange: Arrange(g, s, time, input.Arrange); break;
            case TourStep.Depth: Depth(g, s, time); break;
            case TourStep.Menu: Menu(g, s, time); break;
            default: Done(g, s, time, input.CompleteFor); break;
        }
    }

    // ---- 1. Welcome: the PC's screens all around, the view drifting as a head would ----

    /// <summary>The arc: degrees to the right, metres up, what each screen shows. The middle one first.</summary>
    public static readonly (double Theta, double Y, TourKind Kind)[] Arc =
    {
        (0, 0, TourKind.Media),
        (-31, 0.03, TourKind.Chat),
        (31, -0.02, TourKind.Text),
        (-62, -0.02, TourKind.List),
        (62, 0.03, TourKind.Text),
    };

    public static void Welcome(DrawingContext g, TourStage s, double t)
    {
        TourCamera cam = s.Cam(yaw: 6 * Math.Sin(t * 0.5), cyShare: 0.4, zoom: 0.8);
        double breathe = 0.82 + 0.18 * Math.Sin(t * 1.3);
        PcUnderArc(g, s, t, glow: breathe, yShare: 0.8, lightTo: 0.53);
        for (int i = 0; i < Arc.Length; i++)
        {
            double delay = 0.14 * ((i + 1) / 2);
            double a = M.Span(t, delay, delay + 0.55);
            var win = new TourPanel(Arc[i].Theta, Arc[i].Y - (1 - a) * 0.05, 0.92, 0.58);
            double focus = i == 0 ? breathe : 0;
            double fade = i == 0 ? 1 : 0.82;
            D.Screen(g, cam, win, Arc[i].Kind, focus: focus, alpha: a * fade);
        }
    }

    /// <summary>The small PC the screens belong to, at the bottom centre, with its light going up to them.</summary>
    public static void PcUnderArc(DrawingContext g, TourStage s, double t, double glow, double yShare, double lightTo)
    {
        double a = M.Span(t, 0.3, 0.85);
        double mw = Math.Min(s.W * 0.26, 150), mh = mw * 0.64;
        var box = new TourBox(s.W / 2 - mw / 2, s.H * yShare - mh / 2 + (1 - a) * 10, mw, mh);
        double topY = s.H * lightTo;
        // Light rising from the lid: strongest at the PC, gone where the screens begin. (The Mac blurs a cone. Here
        // it is one gradient that starts at the lid, near the bottom of a tall oval: it reaches far upward and only
        // a little way to the sides, so it widens as it rises and has no edge anywhere.)
        double reach = box.Y - topY;
        if (reach > 1 && glow * a > 0.004)
        {
            var light = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(box.MidX, box.Y - reach * 0.44), RadiusX = mw * 1.2, RadiusY = reach * 0.56,
                GradientOrigin = new Point(box.MidX, box.Y),
            };
            light.GradientStops.Add(new GradientStop(Ui.Theme.C(T.Accent.Alpha(0.30 * glow * a)), 0));
            light.GradientStops.Add(new GradientStop(Ui.Theme.C(T.Accent.Alpha(0.12 * glow * a)), 0.4));
            light.GradientStops.Add(new GradientStop(Ui.Theme.C(T.Accent.Alpha(0)), 1));
            light.Freeze();
            g.DrawRectangle(light, null, new Rect(box.MidX - mw * 1.2, topY, mw * 2.4, reach * 1.12));
        }
        TourBox inner = D.Pc(g, box, alpha: a);
        D.PcDesktop(g, inner, alpha: a);
        TourBox picture = inner.Part(0.2, 0.16, 0.8, 0.72);
        D.Fill(g, D.RoundRect(picture, 3), T.Media, a);
    }

    // ---- 2. Get set up: a PC, glasses, and the cable between them ----

    public static void Setup(DrawingContext g, TourStage s, double t, double link)
    {
        double unit = Math.Min(s.W, s.H * 1.5);
        double enter = M.Span(t, 0, 0.5);
        double glassesEnter = M.Span(t, 0.08, 0.58);
        double cableAlpha = M.Span(t, 0.2, 0.7);

        double mw = unit * 0.44, mh = mw * 0.64;
        var box = new TourBox(s.W / 2 - unit * 0.05 - mw / 2, s.H * 0.6 - mh / 2 + (1 - enter) * 10, mw, mh);
        TourBox inner = D.Pc(g, box, alpha: enter);
        D.PcDesktop(g, inner, alpha: enter);

        var centre = new TourPoint(s.W / 2 - unit * 0.02, s.H * 0.25 + (1 - glassesEnter) * 10);
        TourBox glassesBox = D.Glasses(g, centre, width: unit * 0.4, lit: link, alpha: glassesEnter);

        // The cable: from the PC's port, out to the right and up to the end of the glasses' arm.
        TourPoint port = D.PcPort(box);
        TourPoint arm = D.GlassesArmEnd(glassesBox);
        // Unplugged, the plug waits a little way from the port and nudges toward it: this is the thing to do.
        double swing = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 1.8);
        double nudge = (1 - link) * (16 - 6 * swing);
        var plug = new TourPoint(port.X + 4 + nudge, port.Y);
        Geometry cable = Cable(plug,
            new TourPoint(plug.X + unit * 0.22, plug.Y), new TourPoint(arm.X + unit * 0.2, arm.Y + s.H * 0.16),
            new TourPoint(arm.X + unit * 0.05, arm.Y + s.H * 0.03), new TourPoint(arm.X + unit * 0.03, arm.Y + s.H * 0.008), arm);
        D.Stroke(g, cable, M.MixC(T.Ink3, T.Ink2, link), 2, cableAlpha);
        var plugBox = new TourBox(plug.X - 4, plug.Y - 3.5, 11, 7);
        D.Fill(g, D.RoundRect(plugBox, 2.5), M.MixC(T.Ink2, T.White, link), cableAlpha);

        if (link < 0.5)
        {
            // Two small chevrons pointing at the port.
            double k = t / 1.8 % 1;
            double a = (1 - link * 2) * cableAlpha * (0.25 + 0.55 * Math.Sin(Math.PI * k));
            double x = port.X + 34 - 4 * k;
            const double c = 4;
            foreach (double dx in new[] { 0.0, 7.0 })
            {
                var tip = new TourPoint(x + dx, port.Y - 15);
                Geometry chevron = D.Poly(new[] { new TourPoint(tip.X + c, tip.Y - c), tip, new TourPoint(tip.X + c, tip.Y + c) }, closed: false);
                D.Stroke(g, chevron, T.Accent, 2, a);
            }
        }
        if (link > 0.01)
        {
            // Light running along the cable to the glasses, again and again while they are connected.
            double k = t / 2.2 % 1;
            RunLight(g, cable, k, link * cableAlpha);
        }
    }

    /// <summary>A cable: a curve from [from] (by [c1] and [c2]) to [mid], then a short bend (by [c3]) into [to].</summary>
    public static Geometry Cable(TourPoint from, TourPoint c1, TourPoint c2, TourPoint mid, TourPoint c3, TourPoint to)
    {
        var path = new StreamGeometry();
        using (StreamGeometryContext p = path.Open())
        {
            p.BeginFigure(from.P, true, false);
            p.BezierTo(c1.P, c2.P, mid.P, true, true);
            p.QuadraticBezierTo(c3.P, to.P, true, true);
        }
        path.Freeze();
        return path;
    }

    /// <summary>A segment of light at [k] 0 to 1 of its way along [cable].</summary>
    public static void RunLight(DrawingContext g, Geometry cable, double k, double alpha)
    {
        double head = M.Ease(TourCurve.InOut, k) * 1.2;
        double from = Math.Max(head - 0.2, 0), to = Math.Min(head, 1);
        if (to <= from) return;
        Geometry lit = D.Trim(cable, from, to);
        D.Stroke(g, lit, T.Accent.Alpha(0.3), 8, alpha);
        D.Stroke(g, lit, T.Accent, 3, alpha);
    }

    // ---- 3. Your screens are real displays: the pointer leaves the PC's screen, a window is carried across ----

    public static void Screens(DrawingContext g, TourStage s, double t)
    {
        double loop = Tour.ScreensLoop;
        double lt = Tour.LoopTime(t, loop);
        double alpha = M.LoopAlpha(lt, loop) * M.Span(t, 0, 0.4);
        TourCamera cam = s.Cam(cyShare: 0.3, zoom: 0.8);
        var first = new TourPanel(9, 0, 1.04, 0.65);
        var second = new TourPanel(51, 0, 1.04, 0.65);

        // The PC, lower left; the floating screens are to its right, as Windows has them.
        double mw = Math.Min(s.W * 0.4, s.H * 0.4), mh = mw * 0.64;
        var box = new TourBox(s.W * 0.07, s.H * 0.66, mw, mh);
        TourBox inner = D.PcScreen(box);

        // Places are on one desktop: x 0 to 1 is the PC's screen, 1 to 2 the first floating screen.
        const double edgeY = 0.72;
        double k1 = M.Span(lt, 0.5, 1.5, TourCurve.InOut);
        double k3 = M.Span(lt, 1.8, 2.4);
        double k4 = M.Span(lt, 2.7, 3.1, TourCurve.InOut);
        double k6 = M.Span(lt, 3.3, 3.85, TourCurve.InOut);
        double carry = M.Span(lt, 4.2, 5.9, TourCurve.InOut);
        double held = M.Span(lt, 3.9, 4.02) * (1 - M.Span(lt, 6.1, 6.26, TourCurve.Exit));
        const double ww = 0.36, wh = 0.42;
        double wx = M.Mix(0.26, 1.3, carry), wy = M.Mix(0.32, 0.26, carry);
        const double grabX = 0.18, grabY = 0.05;

        // The pointer: off the right edge and onto the floating screen, back for the window, then across with it.
        double px, py;
        if (lt < 1.65)
        {
            px = M.Mix(0.3, 1.0, k1);
            py = M.Mix(0.5, edgeY, k1);
        }
        else if (lt < 3.2)
        {
            double away = k3 - k4;
            px = 1.0001 + 0.4 * away;
            py = M.Mix(edgeY, 0.6, away);
        }
        else if (lt < 4.2)
        {
            px = M.Mix(1.0, 0.26 + grabX, k6);
            py = M.Mix(edgeY, 0.32 + grabY, k6);
        }
        else
        {
            px = wx + grabX;
            py = wy + grabY;
        }
        double visit = M.Span(lt, 1.65, 1.8) * (1 - M.Span(lt, 3.1, 3.25));
        double focus = Math.Max(visit, M.Span(lt, 4.8, 4.95));

        // The join between the two: a dotted line light runs along each time something crosses.
        var start = new TourPoint(inner.MaxX + 9, inner.At(1, edgeY).Y);
        TourPoint join = first.At(cam, 0, edgeY) ?? new TourPoint(s.W * 0.4, s.H * 0.36);
        var end = new TourPoint(join.X - 9, join.Y);
        var link = new StreamGeometry();
        using (StreamGeometryContext p = link.Open())
        {
            p.BeginFigure(start.P, true, false);
            p.QuadraticBezierTo(new Point(start.X + 26, end.Y + (start.Y - end.Y) * 0.3), end.P, true, true);
        }
        link.Freeze();
        D.Dashed(g, link, T.RimFocus, 1.5, 1, 6, alpha * 0.8);
        (double At, bool Back)[] crossings = { (1.5, false), (3.05, true), (4.68, false) };
        double edge = 0;
        foreach ((double at, bool back) in crossings)
        {
            if (M.Burst(lt, at, 0.35) is not double k) continue;
            edge = Math.Max(edge, Math.Sin(Math.PI * k));
            double head = M.Ease(TourCurve.InOut, k) * 1.25;
            double a0 = Math.Max(head - 0.25, 0), a1 = Math.Min(head, 1);
            if (a1 > a0)
            {
                double lo = back ? 1 - a1 : a0, hi = back ? 1 - a0 : a1;
                D.Stroke(g, D.Trim(link, lo, hi), T.Accent, 2.5, alpha);
            }
        }

        // The floating screens: a second one waits further right; the first lights while the pointer is on it.
        D.Screen(g, cam, second, TourKind.Blank, alpha: alpha * 0.7);
        if (D.Glass(g, cam, first, focus: focus, alpha: alpha) is Geometry outline)
        {
            g.PushClip(outline);
            D.TaskStrip(g, cam, first, alpha);
            if (wx + ww > 1)
            {
                TourPanel part = first.Part(wx - 1, wy, wx - 1 + ww, wy + wh);
                D.MiniWindow(g, cam, part, TourKind.Text, held: held, alpha: alpha);
            }
            g.Pop();
            D.Rim(g, outline, focus: focus, alpha: alpha);
        }
        if (edge > 0.01 && first.At(cam, 0, 0.08) is TourPoint ea && first.At(cam, 0, 0.92) is TourPoint eb)
            D.Line(g, ea, eb, T.Accent, 2.5, alpha * edge);

        // The PC's own screen, with the window while it is still there.
        D.Pc(g, box, alpha: alpha);
        D.PcDesktop(g, inner, alpha: alpha);
        if (wx < 1)
        {
            g.PushClip(D.RoundRect(inner, 3));
            var flat = new TourBox(inner.X + inner.W * wx, inner.Y + inner.H * wy, inner.W * ww, inner.H * wh);
            D.MiniWindowFlat(g, flat, held: held, alpha: alpha);
            g.Pop();
        }
        if (edge > 0.01) D.Line(g, inner.At(1, 0.08), inner.At(1, 0.92), T.Accent, 2.5, alpha * edge);

        // The pointer goes out at one edge and is at the other at once, as on any second display.
        double hop1 = M.Span(lt, 1.5, 1.6) * (1 - M.Span(lt, 1.72, 1.82));
        double hop2 = M.Span(lt, 3.05, 3.15) * (1 - M.Span(lt, 3.27, 3.37));
        double pointerAlpha = alpha * (1 - hop1) * (1 - hop2);
        TourPoint? tip = px > 1 ? first.At(cam, px - 1, py) : inner.At(px, py);
        if (tip is TourPoint at2)
        {
            if (M.Burst(lt, 3.9, 0.45) is double r1) D.Ripple(g, at2, r1, alpha);
            if (M.Burst(lt, 6.1, 0.45) is double r2) D.Ripple(g, at2, r2, alpha);
            D.Pointer(g, at2, alpha: pointerAlpha);
        }
    }

    // ---- 4. Look around, and Recentre ----

    public static void LookAround(DrawingContext g, TourStage s, double t)
    {
        double loop = Tour.LookLoop;
        double lt = Tour.LoopTime(t, loop);
        double alpha = M.LoopAlpha(lt, loop) * M.Span(t, 0, 0.4);
        double yaw = 46 * M.Span(lt, 0.9, 2.9, TourCurve.InOut);
        double centre = 46 * M.Span(lt, 4.25, 5.0);
        TourCamera cam = s.Cam(yaw: yaw, cyShare: 0.45, zoom: 0.82);
        (double Theta, double Y, TourKind Kind)[] row =
        {
            (-39, 0.02, TourKind.Chat),
            (0, 0, TourKind.Media),
            (39, -0.02, TourKind.Text),
        };
        for (int i = 0; i < row.Length; i++)
        {
            var win = new TourPanel(row[i].Theta + centre, row[i].Y, 0.92, 0.58);
            double focus = i == 1 ? 1 : 0;
            double fade = i == 1 ? 1 : 0.85;
            D.Screen(g, cam, win, row[i].Kind, focus: focus, alpha: alpha * fade);
        }

        // Over it, which way the head points (the white mark) and where the screens are centred (the blue one).
        double rulerW = Math.Min(s.W * 0.56, 240);
        double rulerY = s.H * 0.07 + 4;
        double rulerX = s.W / 2;
        double perDeg = rulerW / 120;
        D.Line(g, new TourPoint(rulerX - rulerW / 2, rulerY), new TourPoint(rulerX + rulerW / 2, rulerY), T.Rim, 1.5, alpha);
        for (double degrees = -60; degrees <= 60; degrees += 15)
        {
            double x = rulerX + degrees * perDeg;
            D.Line(g, new TourPoint(x, rulerY - 3), new TourPoint(x, rulerY + 3), T.RimFocus, 1, alpha);
        }
        D.Disc(g, new TourPoint(rulerX + centre * perDeg, rulerY), 4.5, T.Accent, alpha);
        double headX = rulerX + yaw * perDeg;
        Geometry head = D.Poly(new[] { new TourPoint(headX, rulerY + 5), new TourPoint(headX - 5, rulerY + 13), new TourPoint(headX + 5, rulerY + 13) }, closed: true);
        D.Fill(g, head, T.Ink, alpha);

        // Ctrl, Alt and R: they light as the press comes, go down together, and the screens glide back.
        double press = M.Pulse(lt, 4.05, 0.5);
        double ready = M.Span(lt, 3.5, 4.0) * (1 - M.Span(lt, 5.0, 5.5)) * 0.5;
        double lit = Math.Max(press, ready);
        double keyW = Math.Min(s.W * 0.11, 50);
        double keyH = keyW * 0.9;
        double gap = keyW * 0.18;
        double keyY = s.H * 0.88;
        string[] labels = { "Ctrl", "Alt", "R" };
        // Ctrl and Alt are wider keys than a letter, as on a keyboard.
        double[] widths = { keyW * 1.32, keyW * 1.16, keyW };
        double total = widths[0] + widths[1] + widths[2] + 2 * gap;
        double left = s.W / 2 - total / 2;
        for (int i = 0; i < labels.Length; i++)
        {
            D.KeyCap(g, new TourPoint(left + widths[i] / 2, keyY), widths[i], keyH, labels[i], pressed: press, lit: lit, alpha: alpha);
            left += widths[i] + gap;
        }
    }
}
