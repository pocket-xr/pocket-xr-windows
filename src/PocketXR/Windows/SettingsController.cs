using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The Controls pane: a game controller as a remote for the Space (with a drawn controller that says what each
// control does), and Pocket mode.

public static class ControllerModeText
{
    public static string Title(this ControllerMode mode) => mode switch
    {
        ControllerMode.Off => "Off",
        ControllerMode.Held => "While holding the left stick in",
        _ => "Always",
    };

    public static string Detail(this ControllerMode mode) => mode switch
    {
        ControllerMode.Off => "The controller is left to your games.",
        ControllerMode.Held => "Press the left stick in and keep it in to use the controls below. Let go, and the controller is your game's again.",
        _ => "The controls below work all the time. For when you are not playing a game.",
    };
}

public static class SettingsController
{
    public static IEnumerable<FrameworkElement> Cards(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        // Nothing reads a controller in the Windows version yet: said first, in one calm line.
        Border notice = SettingsParts.Notice("Game controllers are not read on this PC (Windows' XInput is missing). Your choice here is kept.");
        b.Add(() => SettingsParts.Show(!state.ControllerAvailable, notice));
        yield return notice;
        yield return ControllerCard(store, state, b);
        yield return PocketCard(store, state, actions, b);
    }

    // MARK: game controller

    private static FrameworkElement ControllerCard(SettingsStore store, AppState state, Binder b)
    {
        TextBlock icon = Kit.Icon(Glyph.Controller, 17, Theme.Ink3);
        icon.Width = 24;
        Border chip = Kit.Chip("Connected", lit: true);
        var connected = new SettingsRow("", " ", chip, icon);
        var diagram = new ControllerDiagram(() => store.S.ControllerMode) { Margin = new Thickness(Theme.Inset, 2, Theme.Inset, 12) };
        b.Add(() =>
        {
            bool has = state.ControllerName != null;
            icon.Foreground = has ? Theme.Held : Theme.Ink3;
            connected.Title.Text = state.ControllerName ?? "No controller connected";
            connected.SetNote(!has ? "Connect a DualSense, Xbox or Switch Pro controller, or a pair of Joy-Cons, with Bluetooth or a cable."
                : store.S.ControllerMode switch
                {
                    ControllerMode.Off => "Left to your games.",
                    ControllerMode.Held => "Hold the left stick in to use it on your screens.",
                    _ => "Changes your screens all the time.",
                });
            SettingsParts.Show(has, chip);
            diagram.Sync();
        });

        var choices = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(choices, "When the controller changes your screens");
        foreach (ControllerMode choice in ControllerModeExt.AllCases)
        {
            ControllerMode mode = choice;
            choices.Children.Add(new ChoiceRow(b, mode.Title(), mode.Detail(), () => store.S.ControllerMode == mode,
                () => store.Update(s => s.ControllerMode = mode), pad: 7));
        }

        return Kit.Card("Game controller",
            connected,
            Kit.Divider(),
            SettingsParts.Caption("Use it to move and resize your screens"),
            choices,
            Kit.Divider(),
            SettingsParts.Caption("What each control does"),
            diagram,
            Kit.Divider(),
            Kit.Note("It moves, resizes and recentres your screens; it does not click for you."));
    }

    // MARK: Pocket mode

    private static FrameworkElement PocketCard(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        TextBlock plug = Kit.Note("Plug in your glasses first", warn: true);
        plug.Margin = new Thickness(Theme.Inset, 0, Theme.Inset, 12);
        StackPanel keys = Kit.Keys(Shortcut.Find("pocket").Keys);
        var keyRow = new SettingsRow("Turn it on and off from the keyboard", null, keys);
        b.Add(() =>
        {
            SettingsParts.Show(state.VirtualScreens <= 0, plug);
            keyRow.SetNote(store.S.Shortcuts ? null : "Keyboard shortcuts are switched off, under Shortcuts.");
            keys.Opacity = store.S.Shortcuts ? 1 : 0.45;
        });
        return Kit.Card("Pocket mode",
            SettingsParts.ToggleRow(b, "Pocket mode",
                "Your PC's own screen goes black and the pointer stays on your floating screens. Everything lives in the glasses: for a train or a plane, and so the pointer never strays onto a screen you are not looking at.",
                () => state.PocketMode, actions.SetPocketMode, () => state.VirtualScreens > 0 || state.PocketMode),
            plug,
            Kit.Divider(),
            keyRow,
            Kit.Divider(),
            new SettingsRow("Leave without the keyboard",
                "Hold the mouse button and move back onto your PC's screen, then keep holding for a second.",
                new PocketHoldGlyph()),
            Kit.Divider(),
            Kit.Note("It ends by itself when the glasses are unplugged. The screen is covered in black, not switched off, so the backlight of an LCD stays on. Not tried with glasses on a PC yet."));
    }
}

/// <summary>The black screen's lock in its ring, small, part filled: what holding to leave looks like.</summary>
public sealed class PocketHoldGlyph : FrameworkElement
{
    public PocketHoldGlyph()
    {
        Width = 28;
        Height = 28;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var centre = new Point(14, 14);
        const double r = 13.25;
        dc.DrawEllipse(null, new Pen(Theme.White(0.14), 1.5), centre, r, r);
        // 62 % of the ring, clockwise from the top.
        double end = 0.62 * 2 * Math.PI;
        var arc = new StreamGeometry();
        using (StreamGeometryContext c = arc.Open())
        {
            c.BeginFigure(new Point(centre.X, centre.Y - r), false, false);
            c.ArcTo(new Point(centre.X + r * Math.Sin(end), centre.Y - r * Math.Cos(end)), new Size(r, r), 0, true, SweepDirection.Clockwise, true, true);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(Theme.Held, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        FormattedText lockGlyph = Draw.Icon(Glyph.Lock, 11, Theme.Ink2, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(lockGlyph, new Point(centre.X - lockGlyph.Width / 2, centre.Y - lockGlyph.Height / 2));
    }
}

/// <summary>
/// A picture of a controller with every control labelled by what it does to the Space, to be learnt in one look. The
/// controller is a plain one of no maker; controls are named by where they sit, as the letters differ by maker. Drawn
/// in a fixed 588 by 262 space and scaled to the width it is given.
/// </summary>
public sealed class ControllerDiagram : FrameworkElement
{
    private readonly Func<ControllerMode> mode;
    private const double DesignWidth = 588, DesignHeight = 262, MaxWide = 660;
    /// <summary>The controller's top left corner in the picture; it is 220 wide and 150 tall.</summary>
    private const double OriginX = 184, OriginY = 66, BodyWidth = 220;

    private static readonly Brush Rim = Theme.White(0.30), Part = Theme.White(0.17);
    private static readonly Pen RimPen = SettingsParts.Frozen(new Pen(Rim, 1)), BodyPen = SettingsParts.Frozen(new Pen(Rim, 1.25));
    private static readonly Pen Leader = SettingsParts.Frozen(new Pen(Theme.White(0.26), 1)
    {
        StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
    });
    private static readonly Point Dpad = new(50, 52), Face = new(170, 52), LeftStick = new(80, 84), RightStick = new(140, 84), Menu = new(110, 40);

    public ControllerDiagram(Func<ControllerMode> mode)
    {
        this.mode = mode;
        AutomationProperties.SetName(this, "What each control does");
        Sync();
    }

    public void Sync()
    {
        ControllerMode now = mode();
        Opacity = now == ControllerMode.Off ? 0.4 : 1;
        AutomationProperties.SetHelpText(this, Spoken(now));
        InvalidateVisual();
    }

    /// <summary>The same as the picture, in words.</summary>
    public static string Spoken(ControllerMode mode)
    {
        var lines = new List<string>();
        if (mode == ControllerMode.Off) lines.Add("The controller is off for Pocket XR.");
        else if (mode == ControllerMode.Held) lines.Add("While the left stick is held in:");
        lines.Add("Right stick: move the screens.");
        lines.Add("Right trigger: bigger. Left trigger: smaller.");
        lines.Add("D-pad up and down: further and nearer. D-pad left and right: less curve and more curve.");
        lines.Add("Bottom button: Recentre. Right button: next layout. Left button: Steady mode. Top button: launcher.");
        lines.Add("Right bumper: pointer to the next screen. Left bumper: pointer to the previous screen.");
        lines.Add("Menu or Options: control panel.");
        return string.Join(" ", lines);
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? DesignWidth : Math.Min(available.Width, MaxWide);
        return new Size(w, Math.Ceiling(w * DesignHeight / DesignWidth));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double wide = Math.Min(ActualWidth, MaxWide);
        if (wide <= 10) return;
        double k = wide / DesignWidth;
        dc.PushTransform(new TranslateTransform((ActualWidth - wide) / 2, 0));
        dc.PushTransform(new ScaleTransform(k, k));
        ControllerMode now = mode();
        DrawController(dc, now);
        DrawLabels(dc, now, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.Pop();
        dc.Pop();
    }

    // MARK: places

    /// <summary>A point of the controller (0...220 across, 0...150 down) as a point of the picture.</summary>
    private static Point At(double x, double y) => new(OriginX + x, OriginY + y);

    private static Rect Box(double x, double y, double w, double h) => new(OriginX + x, OriginY + y, w, h);

    /// <summary>The outline: a rounded body with two grips.</summary>
    private static Geometry Outline()
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(At(50, 12), true, true);
            c.LineTo(At(170, 12), true, true);
            c.BezierTo(At(194, 12), At(203, 22), At(208, 40), true, true);
            c.BezierTo(At(214, 62), At(220, 96), At(218, 118), true, true);
            c.BezierTo(At(218, 140), At(204, 150), At(190, 148), true, true);
            c.BezierTo(At(176, 146), At(168, 112), At(150, 112), true, true);
            c.LineTo(At(70, 112), true, true);
            c.BezierTo(At(52, 112), At(44, 146), At(30, 148), true, true);
            c.BezierTo(At(16, 150), At(2, 140), At(2, 118), true, true);
            c.BezierTo(At(0, 96), At(6, 62), At(12, 40), true, true);
            c.BezierTo(At(17, 22), At(26, 12), At(50, 12), true, true);
        }
        g.Freeze();
        return g;
    }

    // MARK: the controller

    private static void DrawController(DrawingContext dc, ControllerMode mode)
    {
        // Behind the body: the triggers, then the bumpers in front of them.
        foreach (double x in new[] { 36, BodyWidth - 36 - 34 })
            dc.DrawRoundedRectangle(Theme.B(Look.GlassBottom), RimPen, Box(x, -10, 34, 30), 8, 8);
        foreach (double x in new[] { 26, BodyWidth - 26 - 54 })
            dc.DrawRoundedRectangle(Theme.Control, RimPen, Box(x, 3, 54, 24), 9, 9);

        var glass = new LinearGradientBrush(Theme.C(Look.GlassTop), Theme.C(Look.GlassBottom), At(0, 12), At(0, 150))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        dc.DrawGeometry(glass, BodyPen, Outline());

        // The D-pad: a cross.
        Point d = At(Dpad.X, Dpad.Y);
        dc.DrawRoundedRectangle(Part, null, new Rect(d.X - 5.5, d.Y - 15, 11, 30), 3, 3);
        dc.DrawRoundedRectangle(Part, null, new Rect(d.X - 15, d.Y - 5.5, 30, 11), 3, 3);

        // The four face buttons.
        Point f = At(Face.X, Face.Y);
        foreach ((double dx, double dy) in new[] { (0.0, -13.0), (13.0, 0.0), (0.0, 13.0), (-13.0, 0.0) })
            dc.DrawEllipse(Part, null, new Point(f.X + dx, f.Y + dy), 6.5, 6.5);

        // Menu.
        Point m = At(Menu.X, Menu.Y);
        dc.DrawRoundedRectangle(Part, null, new Rect(m.X - 7, m.Y - 3, 14, 6), 3, 3);

        // The sticks. In "while holding" the left one is the key to the rest, so it wears the held light.
        bool held = mode == ControllerMode.Held;
        Point[] sticks = { LeftStick, RightStick };
        for (int index = 0; index < sticks.Length; index++)
        {
            Point c = At(sticks[index].X, sticks[index].Y);
            bool lit = held && index == 0;
            if (lit) dc.DrawEllipse(Theme.B(Look.Accent.Alpha(0.22)), null, c, 20, 20);
            dc.DrawEllipse(Theme.B(Look.Page.Alpha(0.75)), lit ? new Pen(Theme.Held, 1.5) : RimPen, c, 14, 14);
            dc.DrawEllipse(lit ? Theme.Held : Theme.White(0.24), null, c, 9, 9);
        }
    }

    // MARK: the labels

    private static void Line(DrawingContext dc, bool dot, params Point[] points)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++) c.LineTo(points[i], true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, Leader, g);
        if (!dot) return;
        Point last = points[^1];
        dc.DrawEllipse(Theme.B(Look.Accent.Alpha(0.25)), null, last, 4.5, 4.5);
        dc.DrawEllipse(Theme.Accent, null, last, 2.5, 2.5);
    }

    /// <summary>-1 the words end at x, 0 they are centred on it, 1 they start at it.</summary>
    private static void Words(DrawingContext dc, string text, double size, Brush ink, FontWeight weight, double x, double y, int anchor, double dip)
    {
        FormattedText t = Draw.Text(text, size, ink, weight, dip);
        double left = anchor < 0 ? x - t.Width : anchor == 0 ? x - t.Width / 2 : x;
        dc.DrawText(t, new Point(left, y - t.Height / 2));
    }

    /// <summary>Two lines of words: what it does, and under it the control's name. [y] is the middle of the pair.</summary>
    private static void Label(DrawingContext dc, string title, string sub, double x, double y, int anchor, double dip, bool strong = false)
    {
        Words(dc, title, 12.5, strong ? Theme.Held : Theme.Ink, FontWeights.Medium, x, y - 7, anchor, dip);
        Words(dc, sub, 11, Theme.Ink3, FontWeights.Normal, x, y + 8, anchor, dip);
    }

    /// <summary>Four dots as the face buttons sit, one lit: which button a row is about.</summary>
    private static void FaceGlyph(DrawingContext dc, Point centre, int lit)
    {
        (double X, double Y)[] offsets = { (0, -5), (5, 0), (0, 5), (-5, 0) };
        for (int index = 0; index < offsets.Length; index++)
        {
            var c = new Point(centre.X + offsets[index].X, centre.Y + offsets[index].Y);
            if (index == lit) dc.DrawEllipse(Theme.Held, null, c, 3, 3);
            else dc.DrawEllipse(Theme.White(0.24), null, c, 2.2, 2.2);
        }
    }

    private static void DrawLabels(DrawingContext dc, ControllerMode mode, double dip)
    {
        const double leftText = 150, leftLine = 158;
        const double rightLine = DesignWidth - leftLine, rightText = DesignWidth - leftText;

        // The triggers: up and over.
        double leftTrigger = OriginX + 53, rightTrigger = OriginX + BodyWidth - 53;
        Line(dc, true, new Point(leftLine, 30), new Point(leftTrigger, 30), new Point(leftTrigger, OriginY - 5));
        Label(dc, "Smaller", "Left trigger", leftText, 30, -1, dip);
        Line(dc, true, new Point(rightLine, 30), new Point(rightTrigger, 30), new Point(rightTrigger, OriginY - 5));
        Label(dc, "Bigger", "Right trigger", rightText, 30, 1, dip);

        // The bumpers: straight across.
        double bumperY = OriginY + 9;
        Line(dc, true, new Point(leftLine, bumperY), new Point(OriginX + 31, bumperY));
        Label(dc, "Pointer: previous screen", "Left bumper", leftText, bumperY, -1, dip);
        Line(dc, true, new Point(rightLine, bumperY), new Point(OriginX + BodyWidth - 31, bumperY));
        Label(dc, "Pointer: next screen", "Right bumper", rightText, bumperY, 1, dip);

        // The D-pad: two pairs of words beside a bar.
        double midY = OriginY + Dpad.Y;
        Line(dc, false, new Point(leftLine, midY - 22), new Point(leftLine, midY + 44));
        Line(dc, true, new Point(leftLine, midY), new Point(OriginX + Dpad.X - 17, midY));
        Label(dc, "Further, nearer", "D-pad up, down", leftText, midY - 7, -1, dip);
        Label(dc, "Less curve, more curve", "D-pad left, right", leftText, midY + 29, -1, dip);

        // The face buttons: four rows beside a bar, each with the button's place lit.
        Line(dc, false, new Point(rightLine, midY - 22), new Point(rightLine, midY + 52));
        Line(dc, true, new Point(rightLine, midY), new Point(OriginX + Face.X + 22, midY));
        string[] rows = { "Launcher", "Next layout", "Recentre", "Steady mode" };
        for (int index = 0; index < rows.Length; index++)
        {
            double y = midY - 12 + index * 18;
            FaceGlyph(dc, new Point(rightText + 8, y), index);
            Words(dc, rows[index], 12.5, Theme.Ink, FontWeights.Medium, rightText + 24, y, 1, dip);
        }

        // Menu: straight up to the top.
        double menuX = OriginX + Menu.X;
        Line(dc, true, new Point(menuX, 40), new Point(menuX, OriginY + Menu.Y - 5));
        Label(dc, "Control panel", "Menu or Options", menuX, 20, 0, dip);

        // The sticks: straight down, between the grips.
        double stickTop = OriginY + LeftStick.Y + 16, leftX = OriginX + LeftStick.X, rightX = OriginX + RightStick.X;
        Line(dc, false, new Point(leftX, 226), new Point(leftX, stickTop));
        Line(dc, false, new Point(rightX, 226), new Point(rightX, stickTop));
        if (mode == ControllerMode.Held) Label(dc, "Hold in for these controls", "Left stick", leftX + 6, 242, -1, dip, strong: true);
        else Label(dc, "Nothing to hold", "Left stick", leftX + 6, 242, -1, dip);
        Label(dc, "Move the screens", "Right stick", rightX - 6, 242, 1, dip);
    }
}
