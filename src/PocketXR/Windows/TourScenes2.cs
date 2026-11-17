using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PocketXR.Core;
using M = PocketXR.Windows.TourMath;
using D = PocketXR.Windows.TourDraw;
using T = PocketXR.Windows.TourTone;

namespace PocketXR.Windows;

// The tour's pictures, continued: arranging the Space, 3D and Steady mode, the menu, and the last step.

/// <summary>
/// Where one screen goes in a layout, for the Arrange picture: degrees to the right, how far up in its own heights,
/// its size against the others, and how much further away than the Space's distance.
/// </summary>
public readonly record struct TourSpot(double Yaw, double Lift, double Scale, double Far);

public static partial class TourScenes
{
    // ---- 5. Make the Space yours: the screens seen from behind your seat, moving with the controls ----

    /// <summary>
    /// A sketch of each layout for 1 to 3 screens. The Space itself is laid out by the app's own layout code; this
    /// only has to read as the same arrangement.
    /// </summary>
    public static TourSpot[] Spots(ArrangeLayout layout, int count, double sizeDeg)
    {
        int n = Math.Min(Math.Max(count, 1), 3);
        double step = sizeDeg + 2;
        double middle = (n - 1) / 2.0;
        var spots = new List<TourSpot>();
        switch (layout)
        {
            case ArrangeLayout.Row:
                for (int i = 0; i < n; i++) spots.Add(new TourSpot((i - middle) * step, 0, 1, 1));
                break;
            case ArrangeLayout.Theater:
                for (int i = 0; i < n; i++) spots.Add(new TourSpot((i - middle) * step, 0, 1, 1.7));
                break;
            case ArrangeLayout.MainSide:
            {
                const double side = 0.6;
                double yaw = sizeDeg / 2 + 2 + sizeDeg * side / 2;
                spots.Add(new TourSpot(0, 0, 1, 1));
                if (n == 2)
                {
                    spots.Add(new TourSpot(yaw, 0, side, 1));
                }
                else if (n == 3)
                {
                    spots.Add(new TourSpot(yaw, 0.54, side, 1));
                    spots.Add(new TourSpot(yaw, -0.54, side, 1));
                }
                break;
            }
            default:
                for (int i = 0; i < n; i++) spots.Add(new TourSpot(0, (middle - i) * 1.08, 1, 1));
                break;
        }
        return spots.ToArray();
    }

    /// <summary>The picture's own demonstration at [t]: bigger, then nearer, then curved.</summary>
    public static (double SizeDeg, double Distance, double Curve) ArrangeGhost(double t)
    {
        (double bigger, double nearer, double curved) = Tour.ArrangeDemo(t);
        double size = M.Mix(40, 56, M.Ease(TourCurve.InOut, bigger));
        double distance = M.Mix(1.4, 1.0, M.Ease(TourCurve.InOut, nearer));
        double curve = M.Mix(0, 1, M.Ease(TourCurve.InOut, curved));
        return (size, distance, curve);
    }

    public static void Arrange(DrawingContext g, TourStage s, double t, TourArrangeInput input)
    {
        double loop = Tour.ArrangeLoop;
        double lt = Tour.LoopTime(t, loop);
        double alpha = M.Span(t, 0, 0.4);
        double sizeDeg = input.SizeDeg, distance = input.Distance, curve = input.Curve;
        ArrangeLayout layoutFrom = input.From, layoutTo = input.To;
        double blend = input.Mix;
        if (input.Ghost)
        {
            alpha *= M.LoopAlpha(lt, loop);
            (sizeDeg, distance, curve) = ArrangeGhost(t);
            layoutFrom = ArrangeLayout.Row;
            layoutTo = ArrangeLayout.Row;
            blend = 1;
        }
        int n = Math.Min(Math.Max(input.Screens, 1), 3);
        // Far settings are drawn closer than they are, so the picture stays a picture.
        double d = distance <= 2 ? distance : 2 + (distance - 2) * 0.35;

        // The eye is behind and above the viewer, looking down a little.
        TourCamera cam = s.Cam(cyShare: 0.55, zoom: n == 3 ? 1.45 : 1.75);
        cam.Back = 2.6;
        cam.Up = 1.25;
        cam.PitchDeg = -21;

        TourSpot[] a = Spots(layoutFrom, n, sizeDeg), b = Spots(layoutTo, n, sizeDeg);
        var panels = new List<TourPanel>();
        for (int i = 0; i < n; i++)
        {
            double yaw = M.Mix(a[i].Yaw, b[i].Yaw, blend);
            double lift = M.Mix(a[i].Lift, b[i].Lift, blend);
            double scale = M.Mix(a[i].Scale, b[i].Scale, blend);
            double far = M.Mix(a[i].Far, b[i].Far, blend);
            double r = d * far;
            double angle = Math.Min(sizeDeg * scale, 150);
            double w = 2 * r * Math.Tan(M.Radians(angle / 2));
            double h = w / 1.6;
            panels.Add(new TourPanel(yaw, lift * h, w, h, r, curve));
        }
        if (panels.Count == 0) return;
        TourPanel main = panels[0];

        // Slow life behind: the circle at the screens' distance, at eye height, and the angle the first screen fills.
        var ring = new StreamGeometry();
        using (StreamGeometryContext p = ring.Open())
        {
            bool drawing = false;
            for (double degrees = -110; degrees <= 110; degrees += 5)
            {
                double angle = M.Radians(degrees);
                if (cam.Project(main.R * Math.Sin(angle), 0, main.R * Math.Cos(angle)) is TourPoint q)
                {
                    if (drawing)
                    {
                        p.LineTo(q.P, true, true);
                    }
                    else
                    {
                        p.BeginFigure(q.P, false, false);
                        drawing = true;
                    }
                }
                else
                {
                    drawing = false;
                }
            }
        }
        ring.Freeze();
        D.Stroke(g, ring, T.Rim, 1.2, alpha);
        TourPoint? head = cam.Project(0, 0, 0);
        if (head is TourPoint eye && main.At(cam, 0, 0.5) is TourPoint left && main.At(cam, 1, 0.5) is TourPoint right)
        {
            D.Line(g, eye, left, T.RimFocus, 1, alpha * 0.6);
            D.Line(g, eye, right, T.RimFocus, 1, alpha * 0.6);
        }

        // The screens, far ones first.
        TourKind[] kinds = { TourKind.Media, TourKind.Text, TourKind.Chat };
        foreach (int i in Enumerable.Range(0, panels.Count).OrderByDescending(j => panels[j].Depth(cam)))
        {
            double focus = i == 0 ? 1 : 0;
            double fade = i == 0 ? 1 : 0.9;
            D.Screen(g, cam, panels[i], kinds[i % kinds.Length], focus: focus, alpha: alpha * fade);
        }

        // You, in the middle of it.
        if (head is TourPoint you) D.Glasses(g, you, width: 52, lit: 1, alpha: alpha, weight: 2);
    }

    // ---- 6. 3D and Steady mode ----

    public static void Depth(DrawingContext g, TourStage s, double t)
    {
        double loop = Tour.DepthLoop;
        double rowAt = Tour.DepthRowAt;
        double lt = Tour.LoopTime(t, loop);
        double alpha = M.LoopAlpha(lt, loop) * M.Span(t, 0, 0.4);
        double row = M.Span(lt, rowAt, rowAt + 0.9, TourCurve.InOut);
        // 3D: the two eyes' pictures come together, then a small sway shows near screens moving more than far ones.
        double apart = (1 - M.Span(lt, 0.3, 1.5, TourCurve.InOut)) * 0.11;
        double swing = Math.Sin(Math.Max(lt - 1.5, 0) * 1.5);
        double sway = 0.13 * swing * M.Span(lt, 1.5, 2.1) * (1 - row);
        var near = new TourPanel(M.Mix(-24, -27, row), M.Mix(-0.05, 0, row), M.Mix(0.4, 0.7, row), M.Mix(0.27, 0.46, row), M.Mix(1.0, 1.6, row));
        var mid = new TourPanel(M.Mix(1, 0, row), 0, M.Mix(0.74, 0.7, row), M.Mix(0.48, 0.46, row), 1.6);
        var far = new TourPanel(M.Mix(25, 27, row), M.Mix(0.12, 0, row), M.Mix(1.02, 0.7, row), M.Mix(0.66, 0.46, row), M.Mix(2.5, 1.6, row));
        double locked = M.Span(lt, rowAt + 0.9, rowAt + 1.3);
        double zoom = M.Mix(0.8, 0.62, row);
        double midFocus = 1 - row;

        void Pass(double shift, double a)
        {
            TourCamera cam = s.Cam(shiftX: shift, cyShare: 0.46, zoom: zoom);
            D.Screen(g, cam, far, TourKind.Text, alpha: a);
            D.Screen(g, cam, mid, TourKind.Media, focus: midFocus, alpha: a);
            D.Screen(g, cam, near, TourKind.Chat, alpha: a);
        }
        if (apart > 0.004)
        {
            Pass(sway - apart, alpha * 0.55);
            Pass(sway + apart, alpha * 0.55);
        }
        else
        {
            Pass(sway, alpha);
        }
        if (locked > 0.01)
        {
            // Steady mode: one brighter rim round the whole row, the wide screen holding still.
            TourCamera cam = s.Cam(cyShare: 0.46, zoom: 0.62);
            if (near.At(cam, 0, 0) is TourPoint a && far.At(cam, 1, 1) is TourPoint b)
            {
                const double pad = 9;
                var around = new TourBox(a.X - pad, a.Y - pad, b.X - a.X + 2 * pad, b.Y - a.Y + 2 * pad);
                D.Stroke(g, D.RoundRect(around, 16), T.RimFocus, 1.5, alpha * locked);
            }
        }
    }

    // ---- 7. Always in the notification area ----

    /// <summary>The menu's rows as the picture shows them, in the order Pocket XR's menu has them (the last is the
    /// one the words name).</summary>
    public static readonly string[] MenuRows = { "Recentre", "Layout", "Settings…", "Tour of Pocket XR", "Send logs to Mike", "Quit and put the glasses back" };

    public static void Menu(DrawingContext g, TourStage s, double t)
    {
        double loop = Tour.MenuLoop;
        double lt = Tour.LoopTime(t, loop);
        double alpha = M.LoopAlpha(lt, loop) * M.Span(t, 0, 0.4);

        // The second half: the cable comes out, the floating screen goes, the windows come home.
        double unplug = M.Span(lt, 5.7, 6.3, TourCurve.InOut);
        double link = 1 - M.Span(lt, 5.9, 6.4);
        double gone = M.Span(lt, 6.4, 7.1, TourCurve.Exit);
        double home = M.Span(lt, 6.3, 7.5, TourCurve.InOut);

        // The floating screen, straight ahead and above.
        TourCamera cam = s.Cam(cyShare: 0.21, zoom: 0.8);
        var panel = new TourPanel(0, 0, 1.0, 0.6);
        double screenAlpha = alpha * (1 - gone);
        if (D.Glass(g, cam, panel, focus: link, alpha: screenAlpha) is Geometry outline)
        {
            g.PushClip(outline);
            D.TaskStrip(g, cam, panel, screenAlpha);
            g.Pop();
            D.Rim(g, outline, focus: link, alpha: screenAlpha);
        }

        // The PC below it, the glasses beside, and the cable between them.
        double mw = Math.Min(s.W * 0.62, s.H * 0.56), mh = mw * 0.62;
        var box = new TourBox(s.W * 0.1, s.H * 0.5, mw, mh);
        TourBox inner = D.PcScreen(box);
        TourBox glassesBox = D.Glasses(g, new TourPoint(s.W * 0.8, s.H * 0.4), width: s.W * 0.2, lit: link, alpha: alpha, weight: 2);
        TourPoint port = D.PcPort(box);
        TourPoint arm = D.GlassesArmEnd(glassesBox);
        var plug = new TourPoint(port.X + 4 + 14 * unplug, port.Y);
        Geometry cable = Cable(plug,
            new TourPoint(plug.X + 70, plug.Y), new TourPoint(arm.X + 46, arm.Y + 130),
            new TourPoint(arm.X + 12, arm.Y + 18), new TourPoint(arm.X + 8, arm.Y + 4), arm);
        D.Stroke(g, cable, M.MixC(T.Ink3, T.Ink2, link), 2, alpha);
        var plugBox = new TourBox(plug.X - 4, plug.Y - 3.5, 11, 7);
        D.Fill(g, D.RoundRect(plugBox, 2.5), M.MixC(T.Ink2, T.White, link), alpha);
        if (link > 0.01)
        {
            double k = lt / 2.2 % 1;
            RunLight(g, cable, k, link * alpha);
        }

        D.Pc(g, box, alpha: alpha);
        D.PcDesktop(g, inner, alpha: alpha);

        // The notification area, at the right of the PC's taskbar: the arrow that shows hidden icons, the glasses
        // icon, another app's icon, the clock. Pocket XR's menu rises from the glasses icon.
        string[] rows = MenuRows;
        int last = rows.Length - 1;
        double open = M.Span(lt, 0.9, 1.1) * (1 - M.Span(lt, 5.0, 5.16, TourCurve.Exit));
        double barV = 1 - D.TaskbarShare / 2;
        TourPoint icon = inner.At(0.8, barV);
        double iconW = Math.Max(inner.H * 0.09, 12);
        TourPoint arrow = inner.At(0.715, barV);
        D.Stroke(g, D.Poly(new[] { new TourPoint(arrow.X - 3, arrow.Y + 1.5), new TourPoint(arrow.X, arrow.Y - 1.5), new TourPoint(arrow.X + 3, arrow.Y + 1.5) }, closed: false),
            T.Tint(0.34), 1.3, alpha);
        D.Disc(g, inner.At(0.86, barV), Math.Max(inner.W * 0.0075, 1.2), T.Tint(0.2), alpha);
        if (open > 0.01)
        {
            // The icon's own square lights while its menu is open, as Windows lights the icon under the pointer.
            var lit = new TourBox(icon.X - iconW * 0.72, icon.Y - inner.H * 0.04, iconW * 1.44, inner.H * 0.08);
            D.Fill(g, D.RoundRect(lit, 3), T.Tint(0.1), alpha * open);
        }
        D.Glasses(g, icon, width: iconW, lit: open, alpha: alpha, weight: 1.2);

        double rowH = Math.Min(inner.H * 0.118, 23);
        double menuW = Math.Min(inner.W * 0.62, 196);
        double menuH = rowH * rows.Length + 10;
        double menuX = Math.Min(icon.X + iconW - menuW, inner.MaxX - menuW - 5);
        double menuTop = inner.Y + inner.H * (1 - D.TaskbarShare) - 4 - menuH;
        // It rises a little as it opens.
        var menuBox = new TourBox(menuX, menuTop + (1 - open) * 6, menuW, menuH);
        // The highlight walks down three rows, then goes to the last one.
        double walk = M.Span(lt, 1.9, 2.15, TourCurve.InOut) + M.Span(lt, 2.5, 2.75, TourCurve.InOut);
        double pos = walk + (last - 2) * M.Span(lt, 3.4, 3.8, TourCurve.InOut);
        double highlight = M.Span(lt, 1.3, 1.45) * open;
        if (open > 0.01)
        {
            // Pocket XR's menu as it looks: the card colour, a hairline rim, round corners.
            Geometry sheet = D.RoundRect(menuBox, 8);
            D.Halo(g, sheet, focus: 0.6 * open, held: 0, alpha: alpha);
            D.Fill(g, sheet, T.Card, alpha * open);
            D.Stroke(g, sheet, T.RimFocus, 1.2, alpha * open);
            var mark = new TourBox(menuBox.X + 4, menuBox.Y + 5 + rowH * pos, menuW - 8, rowH);
            D.Fill(g, D.RoundRect(mark, 5), T.Accent, alpha * highlight);
            double ruleY = menuBox.Y + 5 + rowH * last;
            D.Line(g, new TourPoint(menuBox.X + 10, ruleY), new TourPoint(menuBox.MaxX - 10, ruleY), T.Rim, 1, alpha * open);
            for (int i = 0; i < rows.Length; i++)
            {
                // Rows arrive 40 ms apart.
                double arrive = M.Span(lt, 0.95 + 0.04 * i, 1.15 + 0.04 * i) * open;
                bool on = highlight > 0.5 && Math.Abs(pos - i) < 0.5;
                LookColor tone = on ? T.Page : T.Tint(0.86);
                var at = new TourPoint(menuBox.X + 14, menuBox.Y + 5 + rowH * (i + 0.5));
                D.Label(g, rows[i], at, Math.Min(rowH * 0.5, 11), tone, alpha * arrive, leading: true);
            }
        }

        // The windows: on the floating screen while the glasses are plugged in, back on the PC's screen after.
        if (panel.At(cam, 0, 0) is TourPoint topLeft && panel.At(cam, 1, 1) is TourPoint bottomRight)
        {
            var floating = new TourBox(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            TourBox one = M.MixB(floating.Part(0.07, 0.12, 0.5, 0.74), inner.Part(0.07, 0.12, 0.47, 0.64), home);
            TourBox two = M.MixB(floating.Part(0.55, 0.22, 0.93, 0.8), inner.Part(0.52, 0.26, 0.9, 0.78), home);
            D.MiniWindowFlat(g, one, alpha: alpha);
            D.MiniWindowFlat(g, two, alpha: alpha);
        }

        // The pointer: to the icon, a click, up the menu to its first row and down with the highlight; it leaves when
        // the menu closes.
        double toIcon = M.Span(lt, 0.3, 0.85, TourCurve.InOut);
        double toRow = M.Span(lt, 1.15, 1.45, TourCurve.InOut);
        TourPoint start = inner.At(0.42, 0.5);
        var onIcon = new TourPoint(icon.X, icon.Y - 1);
        var onRow = new TourPoint(menuX + menuW * 0.88, menuTop + 5 + rowH * (pos + 0.5));
        TourPoint tip = M.MixP(M.MixP(start, onIcon, toIcon), onRow, toRow);
        double pointerAlpha = alpha * (1 - M.Span(lt, 5.2, 5.5, TourCurve.Exit));
        if (M.Burst(lt, 0.88, 0.45) is double click) D.Ripple(g, tip, click, alpha);
        D.Pointer(g, tip, alpha: pointerAlpha);
    }

    // ---- 8. You're set ----

    public static void Done(DrawingContext g, TourStage s, double t, double? completeFor)
    {
        TourCamera cam = s.Cam(yaw: 5 * Math.Sin(t * 0.45), cyShare: 0.4, zoom: 0.72);
        PcUnderArc(g, s, t, glow: 1, yShare: 0.8, lightTo: 0.52);
        for (int i = 0; i < Arc.Length; i++)
        {
            double delay = 0.1 * ((i + 1) / 2);
            double a = M.Span(t, delay, delay + 0.55);
            var win = new TourPanel(Arc[i].Theta * 0.92, Arc[i].Y - (1 - a) * 0.05, 0.86, 0.54);
            double focus = i == 0 ? 1 : 0;
            double fade = i == 0 ? 1 : 0.8;
            D.Screen(g, cam, win, Arc[i].Kind, focus: focus, alpha: a * fade);
        }
        if (completeFor is double since)
        {
            // The tick is drawn only when everything needed really is set up.
            var centre = new TourPoint(s.W / 2, s.H * 0.4);
            double radius = Math.Min(s.H * 0.1, 34);
            D.Disc(g, centre, radius + 9, T.Page.Alpha(0.82), M.Span(since, 0.5, 0.8));
            D.TickRing(g, centre, radius, ring: M.Span(since, 0.6, 1.1, TourCurve.InOut), tick: M.Span(since, 1.0, 1.4), width: 3);
        }
    }
}
