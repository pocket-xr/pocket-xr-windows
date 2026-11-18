using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// Hand tracking's settings, as cards in the settings window's look: on and off with where it stands, which camera
// (with a picture of what a camera that faces you is for), how the pointer is aimed, what the hands do (each with a
// small drawing), the camera's permission, the aim's calibration or reach, and a plain note that none of this has
// been tried on a PC. The buttons that start it stay off until AppState.HandsAvailable is true (Hands/HandTracking.cs
// sets it).

/// <summary>The lines hand tracking puts in AppState.HandsStatus, word for word as on the Mac, so the pane can say
/// what to do about each.</summary>
public static class HandLines
{
    public const string Looking = "Looking for the glasses' camera";
    public const string NoCamera = "No camera found on these glasses";
    public const string LookingAny = "Looking for the camera";
    public const string NoCameraAny = "No camera found";
    public const string NotAllowed = "The camera is not allowed yet";
    public const string Watching = "Watching for your hands";
    public const string Dark = "The camera's picture is black. Is its lens covered, or the room dark?";
    public const string Steady = "Hands do not aim in Steady mode";
    public const string NoOpen = "The camera could not be opened";
}

public static class SettingsHands
{
    public static IEnumerable<FrameworkElement> Cards(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        yield return TrackingCard(store, state, actions, b);
        yield return CameraCard(state, actions, b);
        yield return GesturesCard(store, state, b);
        yield return Kit.Card("Permission",
            new SettingsRow("Camera", "Lets Pocket XR see your hands. The picture is used on this PC only and is never kept.",
                new PxButton("Open Windows Settings", Glyph.Camera, ButtonKind.Glass, actions.OpenCameraSettings)));
        yield return AimCard(store, state, actions, b);
        yield return Kit.Card("What has been tried",
            Kit.Note("The air trackpad has been tried on a PC with a Logitech C920 webcam. The hand ray has not been tried on a PC yet: it needs glasses with a camera Windows can open: the XREAL Aura on firmware 16.1.03 or older. Newer Aura firmware turns the camera off.", warn: true),
            Kit.Divider(),
            Kit.Note("Lighting and the camera's angle matter: have the light on your hand, not behind it, and turn the camera until your hand sits in the box."),
            Kit.Divider(),
            Kit.Note("The camera's light is on while hands are running."));
    }

    // MARK: on and off

    private static string StatusNote(SettingsStore store, AppState state)
    {
        string line = state.HandsStatus;
        bool look = store.S.HandAim == HandAim.LookAndPinch;
        if (line is HandLines.Looking or HandLines.LookingAny) return "Trying for 30 seconds, then it stops.";
        if (line is HandLines.NoCamera or HandLines.NoCameraAny) return "Windows shows no such camera. Choose another under Camera.";
        if (line == HandLines.NotAllowed) return "Allow the camera under Permission, then start hands again.";
        if (line == HandLines.NoOpen) return "Another app may be using it.";
        if (line == HandLines.Steady) return "A pinch still clicks where the pointer is, and the palm gestures still work.";
        if (state.HandsRunning)
        {
            if (state.HandsSeen == 0)
                return state.HandsFacing ? "Hold a hand up where the camera can see it." : "Hold a hand up in front of the glasses.";
            return look ? "The pointer follows where you look." : "The pointer follows the first hand it saw.";
        }
        return state.HandsFacing ? "Sees your hands through a camera that faces you." : "Sees your hands through the camera in your glasses.";
    }

    private static FrameworkElement TrackingCard(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        // The dot sits in a box as wide as the choices' rings below, so every row of the card starts at one line.
        Grid dot = Kit.Dot(false);
        var mark = new Grid { Width = 24 };
        mark.Children.Add(dot);
        // The button says what a click does, and is the main one only while hands are off.
        var button = new Border();
        bool? builtFor = null;
        var status = new SettingsRow("", " ", button, mark);
        b.Add(() =>
        {
            Kit.SetDot(dot, state.HandsRunning && state.HandsSeen > 0);
            status.Title.Text = state.HandsStatus.Length > 0 ? state.HandsStatus : state.HandsRunning ? "Starting" : "Off";
            status.SetNote(StatusNote(store, state));
            if (builtFor != state.HandsRunning)
            {
                builtFor = state.HandsRunning;
                bool running = state.HandsRunning;
                button.Child = new PxButton(running ? "Stop hands" : "Start hands", null,
                    running ? ButtonKind.Glass : ButtonKind.Primary, () => actions.SetHands(!state.HandsRunning));
            }
            ((PxButton)button.Child).IsEnabled = state.HandsAvailable;
        });
        return Kit.Card("Hand tracking",
            status,
            Kit.Divider(),
            new SettingsRow("Start with the glasses", "Hand tracking starts when your glasses are plugged in.",
                new Toggle(b, "Start with the glasses", () => store.S.HandsOn, v => store.Update(s => s.HandsOn = v)),
                new Border { Width = 24 }),
            Kit.Divider(),
            new ChoiceRow(b, () => state.HandsFacing ? "Air trackpad" : "Hand ray",
                () => state.HandsFacing ? "Your hand moves the pointer, a pinch clicks." : "The hand aims, a pinch clicks.",
                () => store.S.HandAim == HandAim.HandRay, () => store.Update(s => s.HandAim = HandAim.HandRay)),
            Kit.Divider(),
            new ChoiceRow(b, "Look and pinch", "Where you look aims, a pinch anywhere clicks.",
                () => store.S.HandAim == HandAim.LookAndPinch, () => store.Update(s => s.HandAim = HandAim.LookAndPinch)));
    }

    // MARK: camera

    private static FrameworkElement CameraCard(AppState state, IAppActions actions, Binder b)
    {
        IEnumerable<UIElement?> Rows(Binder own)
        {
            yield return new ChoiceRow(own, () => "Glasses' camera",
                () => state.HandsCameraFound ? "Looks where you look, so your hand aims like a ray."
                    : state.HandsCamera < 0 && state.HandsFellBack
                        ? "These glasses have no camera Windows can open, so another camera is used: turn it to face you."
                        : "These glasses have no camera Windows can open.",
                () => state.HandsCamera < 0, () => actions.ChooseHandsCamera(-1));
            for (int i = 0; i < state.HandsCameras.Count; i++)
            {
                int index = i;
                yield return Kit.Divider();
                yield return new ChoiceRow(own, state.HandsCameras[i], "Point it at you, with your hand in view.",
                    () => state.HandsCamera == index, () => actions.ChooseHandsCamera(index));
            }
            if (state.HandsFacing)
            {
                yield return Kit.Divider();
                yield return PreviewRow(state, own);
            }
        }
        return SettingsParts.LiveCard(b, "Camera", () => string.Join("|", state.HandsCameras) + "#" + state.HandsFacing, Rows);
    }

    /// <summary>What a camera that faces you sees, as in a mirror, beside a few words on how to use it.</summary>
    private static FrameworkElement PreviewRow(AppState state, Binder own)
    {
        var view = new HandsPreviewView(() => state.HandsRunning) { VerticalAlignment = VerticalAlignment.Center };
        TextBlock title = Kit.Line("What the camera sees");
        TextBlock how = Kit.Small("As in a mirror. The box is the whole screen: keep your hand inside it. The dot is the spot on your hand that moves the pointer.");
        how.Margin = new Thickness(0, 6, 0, 0);
        TextBlock light = Kit.Text("", Theme.NoteSize, Theme.Ink3);
        light.Margin = new Thickness(0, 6, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        words.Children.Add(title);
        words.Children.Add(how);
        words.Children.Add(light);
        var row = new DockPanel { Margin = new Thickness(Theme.Inset, 14, Theme.Inset, 14) };
        DockPanel.SetDock(view, Dock.Left);
        row.Children.Add(view);
        row.Children.Add(words);
        own.Add(() =>
        {
            light.Text = state.HandsRunning ? "The camera's light is on while hands are running." : "Start hands to see the picture.";
            view.InvalidateVisual();
        });
        return row;
    }

    // MARK: gestures

    private static FrameworkElement GesturesCard(SettingsStore store, AppState state, Binder b)
    {
        static UIElement Gesture(HandsGlyphKind kind, string title, string note)
        {
            var row = new SettingsRow(title, note, null, new HandsGlyph(kind));
            return row;
        }
        IEnumerable<UIElement?> Rows(Binder own)
        {
            bool facing = state.HandsFacing, look = store.S.HandAim == HandAim.LookAndPinch, air = facing && !look;
            string palm = facing ? "Palm to the camera" : "Palm to your face";
            yield return Gesture(HandsGlyphKind.Point, look ? "Look" : air ? "Move your hand" : "Point",
                look ? "The pointer goes where you look while a hand is in view."
                : air ? "The pointer moves over the screen you are looking at, as on a trackpad in the air."
                : "The pointer goes where your hand is, as you see it through the glasses.");
            yield return Kit.Divider();
            yield return Gesture(HandsGlyphKind.Pinch, "Pinch", "Thumb and first finger together is a press; apart again is a click.");
            yield return Kit.Divider();
            yield return Gesture(HandsGlyphKind.Drag, "Pinch and move",
                look ? "Drags. Keep the pinch and turn your head; let go to drop." : "Drags. Keep the pinch and move your hand; let go to drop.");
            yield return Kit.Divider();
            yield return Gesture(HandsGlyphKind.PalmPinch, $"{palm}, then a short pinch", "Opens or closes the launcher.");
            yield return Kit.Divider();
            yield return Gesture(HandsGlyphKind.PalmHold, $"{palm}, pinch and hold", "Recentres the screens in front of you.");
            yield return Kit.Divider();
            yield return Kit.Note("There is no scroll gesture yet. A pinch is a press, moving is a drag, letting go lets go.");
        }
        return SettingsParts.LiveCard(b, "What your hands do", () => (state.HandsFacing, store.S.HandAim), Rows);
    }

    // MARK: calibration (the glasses' camera) and reach (a camera that faces you)

    /// <summary>Why the hand aim cannot be calibrated now, null when it can.</summary>
    public static string? CalibrationBlocker(SettingsStore store, AppState state)
    {
        if (!state.HandsRunning) return "Start hands first.";
        if (state.Steady) return "Hands do not aim in Steady mode, so there is nothing to calibrate.";
        if (store.S.HandAim != HandAim.HandRay) return "Look and pinch aims with your head and needs no calibration.";
        if (state.Panels.Count == 0) return "It needs a floating screen to show its dots on.";
        if (state.HandsSeen == 0) return "Hold a hand up where the glasses can see it.";
        return null;
    }

    private static string? ReachBlocker(SettingsStore store, AppState state)
    {
        if (!state.HandsRunning) return "Start hands first.";
        if (store.S.HandAim == HandAim.LookAndPinch) return "Look and pinch aims with your head, so there is no reach to set.";
        if (state.HandsSeen == 0) return "Hold a hand up where the camera can see it.";
        return null;
    }

    private static FrameworkElement AimCard(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        // The card's name follows the camera, so it is made whole each time: a heading and its rows.
        var holder = new Border();
        (bool Facing, bool Busy)? builtFor = null;
        var own = new Binder();
        b.Add(() =>
        {
            var key = (state.HandsFacing, state.HandsFacing ? state.HandsSettingReach : state.HandsCalibrating);
            if (builtFor == key)
            {
                own.Sync();
                return;
            }
            builtFor = key;
            own = new Binder();
            holder.Child = state.HandsFacing ? ReachCard(store, state, actions, own) : CalibrationCard(store, state, actions, own);
        });
        return holder;
    }

    private static FrameworkElement CalibrationCard(SettingsStore store, AppState state, IAppActions actions, Binder own)
    {
        var begin = new PxButton(state.HandsCalibrating ? "Calibrating" : "Calibrate hand aim", null, ButtonKind.Primary, actions.BeginHandCalibration);
        var teach = new SettingsRow("Teach it where you point", " ", begin);
        var reset = new PxButton("Reset calibration", null, ButtonKind.Glass, actions.ResetHandCalibration);
        TextBlock result = Kit.Note("");
        own.Add(() =>
        {
            string? blocker = CalibrationBlocker(store, state);
            teach.SetNote(blocker ?? "Five dots on the floating screen your pointer is on. Hold your hand over each one and pinch.");
            begin.IsEnabled = state.HandsAvailable && blocker == null && !state.HandsCalibrating;
            reset.IsEnabled = state.HandsAvailable && state.HandsCalibrated && !state.HandsCalibrating;
            result.Text = state.HandsCalibrationResult ?? "Not calibrated. The aim uses an estimate of where the camera points.";
        });
        return Kit.Card("Calibration",
            teach,
            Kit.Divider(),
            new SettingsRow("Start over", "Goes back to the estimate of where the camera points.", reset),
            Kit.Divider(),
            result);
    }

    private static FrameworkElement ReachCard(SettingsStore store, AppState state, IAppActions actions, Binder own)
    {
        var begin = new PxButton(state.HandsSettingReach ? "Pinch now" : "Set your reach", null, ButtonKind.Primary, actions.BeginHandReach);
        var set = new SettingsRow("Set your reach", " ", begin);
        var reset = new PxButton("Reset reach", null, ButtonKind.Glass, actions.ResetHandReach);
        TextBlock result = Kit.Note("");
        own.Add(() =>
        {
            string? blocker = ReachBlocker(store, state);
            set.SetNote(state.HandsSettingReach ? "Hold your hand where it is comfortable and pinch."
                : blocker ?? "Hold your hand where it is comfortable and pinch. That spot becomes the middle of the screen.");
            begin.IsEnabled = state.HandsAvailable && blocker == null && !state.HandsSettingReach;
            reset.IsEnabled = state.HandsAvailable && state.HandsReachSet && !state.HandsSettingReach;
            result.Text = state.HandsReachSet ? "Set: the box is centred where you pinched."
                : "Not set. The box rests in the middle of the picture, a little low.";
        });
        return Kit.Card("Your reach",
            set,
            Kit.Divider(),
            new SettingsRow("Start over", "Puts the box back in the middle of the picture, a little low.", reset),
            Kit.Divider(),
            result);
    }
}

public enum HandsGlyphKind { Point, Pinch, Drag, PalmPinch, PalmHold }

/// <summary>
/// A small drawing of a hand doing one gesture, on a glass tile: the hand in the ink colour, what matters in the
/// accent (where the pointer is, where the fingers meet, which way a drag goes, the palm, the hold).
/// </summary>
public sealed class HandsGlyph : FrameworkElement
{
    private readonly HandsGlyphKind kind;
    public const double Side = 52;

    private static readonly LinearGradientBrush Tile = SettingsParts.Frozen(new LinearGradientBrush(Theme.C(Look.GlassTop), Theme.C(Look.GlassBottom), 90));
    private static readonly Pen TileRim = SettingsParts.Frozen(new Pen(Theme.Rim, 1));
    // Opaque, so the strokes that make up the hand join into one shape with no seams.
    private static readonly SolidColorBrush Ink = SettingsParts.Frozen(new SolidColorBrush(Color.FromRgb(230, 232, 237)));

    public HandsGlyph(HandsGlyphKind kind)
    {
        this.kind = kind;
        Width = Side;
        Height = Side;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Tile, TileRim, new Rect(0.5, 0.5, Side - 1, Side - 1), 12, 12);
        DrawHand(dc, kind);
    }

    private static Pen Round(Brush brush, double width) =>
        new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static Geometry Path(params Point[] points)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++) c.LineTo(points[i], true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The accent's soft light round a mark.</summary>
    private static void Halo(DrawingContext dc, Point p, double radius) =>
        dc.DrawEllipse(Theme.B(Look.Accent.Alpha(0.22)), null, p, radius, radius);

    /// <summary>Where the fingers meet: a dot of the accent with its halo.</summary>
    private static void Spark(DrawingContext dc, Point p)
    {
        Halo(dc, p, 5.6);
        dc.DrawEllipse(Theme.Accent, null, p, 2.7, 2.7);
    }

    /// <summary>Where the thumb and first finger meet in a pinch, on the 52 by 52 grid.</summary>
    public static readonly Point Meet = new(17.5, 24.5);

    /// <summary>The hand and its marks on a 52 by 52 grid (scale the drawing to make it larger).</summary>
    public static void DrawHand(DrawingContext dc, HandsGlyphKind kind)
    {
        bool pinched = kind != HandsGlyphKind.Point;
        bool palm = kind is HandsGlyphKind.PalmPinch or HandsGlyphKind.PalmHold;

        // The hand: a palm and five fingers as round ended strokes, one silhouette. A right hand from behind (or a
        // left one from the front): the thumb is on the left. It sits to the right, leaving the left for the marks.
        Pen finger = Round(Ink, 4.6), small = Round(Ink, 4.0), thumb = Round(Ink, 5.0);
        var palmRect = new Rect(24.5, 27, 18.5, 16.5);
        dc.DrawRoundedRectangle(Ink, null, palmRect, 6.5, 6.5);
        // Middle, ring and little fingers are up in every drawing.
        dc.DrawGeometry(null, finger, Path(new Point(32.2, 30), new Point(32.2, 11.5)));
        dc.DrawGeometry(null, finger, Path(new Point(37.4, 30), new Point(37.6, 13.5)));
        dc.DrawGeometry(null, small, Path(new Point(41.6, 31), new Point(42.4, 19)));

        if (pinched)
        {
            dc.DrawGeometry(null, finger, Path(new Point(27, 30), new Point(25.2, 19.5), new Point(19.3, 22.6)));
            dc.DrawGeometry(null, thumb, Path(new Point(26.5, 38.5), new Point(19.6, 27.2)));
        }
        else
        {
            dc.DrawGeometry(null, finger, Path(new Point(27, 30), new Point(26.6, 13.5)));
            dc.DrawGeometry(null, thumb, Path(new Point(26.5, 38.5), new Point(18.2, 30.5)));
        }

        if (palm)
        {
            // The palm is shown: a ring of light in the middle of it.
            var centre = new Point(palmRect.X + palmRect.Width / 2 + 0.6, palmRect.Y + palmRect.Height / 2 + 0.8);
            dc.DrawEllipse(Theme.Card, new Pen(Theme.Accent, 1.8), centre, 4.2, 4.2);
        }

        switch (kind)
        {
            case HandsGlyphKind.Point:
            {
                // The pointer, off where the hand aims, and the line of sight to it.
                var pointer = new Point(9.5, 12.5);
                Pen sight = Round(Theme.B(Look.Accent.Alpha(0.75)), 1.5);
                sight.DashStyle = new DashStyle(new[] { 0.1 / 1.5, 3.6 / 1.5 }, 0);
                sight.DashCap = PenLineCap.Round;
                dc.DrawGeometry(null, sight, Path(new Point(20.5, 24.5), new Point(12.4, 15.6)));
                Halo(dc, pointer, 6.2);
                dc.DrawEllipse(Theme.Accent, null, pointer, 3.1, 3.1);
                break;
            }
            case HandsGlyphKind.Drag:
            {
                // The pinch, carried to the left.
                var tip = new Point(5.5, Meet.Y);
                Pen shaft = Round(Theme.Accent, 1.8);
                dc.DrawGeometry(null, shaft, Path(new Point(Meet.X - 5.5, Meet.Y), tip));
                dc.DrawGeometry(null, shaft, Path(new Point(tip.X + 3.6, tip.Y - 3.4), tip, new Point(tip.X + 3.6, tip.Y + 3.4)));
                Spark(dc, Meet);
                break;
            }
            case HandsGlyphKind.PalmHold:
            {
                // Held: three quarters of a circle round the pinch, as a clock filling.
                const double r = 8.2;
                Point top = new(Meet.X, Meet.Y - r), left = new(Meet.X - r, Meet.Y);
                var held = new StreamGeometry();
                using (StreamGeometryContext c = held.Open())
                {
                    c.BeginFigure(top, false, false);
                    c.ArcTo(left, new Size(r, r), 0, true, SweepDirection.Clockwise, true, true);
                }
                held.Freeze();
                var rest = new StreamGeometry();
                using (StreamGeometryContext c = rest.Open())
                {
                    c.BeginFigure(left, false, false);
                    c.ArcTo(top, new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                }
                rest.Freeze();
                dc.DrawGeometry(null, Round(Theme.White(0.16), 1.8), rest);
                dc.DrawGeometry(null, Round(Theme.Accent, 1.8), held);
                Spark(dc, Meet);
                break;
            }
            default:
                Spark(dc, Meet);
                break;
        }
    }
}

/// <summary>
/// What a camera that faces you sees, as a panel: the camera's picture as in a mirror (Hands.HandsPreview), the air
/// trackpad's box and the tracked point on it. With no picture, plain and dark with a hand drawn in it.
/// </summary>
public sealed class HandsPreviewView : FrameworkElement
{
    private readonly Func<bool> running;
    private const double Wide = 216, High = 162;

    public HandsPreviewView(Func<bool> running)
    {
        this.running = running;
        Width = Wide;
        Height = High;
        AutomationProperties.SetName(this, "What the camera sees");
        // While it shows, hand tracking sends the camera's picture about 10 times a second.
        IsVisibleChanged += (_, _) =>
        {
            Hands.HandsPreview.Watching = IsVisible && !Theme.Static;
            if (IsVisible) Hands.HandsPreview.Changed += InvalidateVisual;
            else Hands.HandsPreview.Changed -= InvalidateVisual;
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var whole = new Rect(0, 0, Wide, High);
        dc.PushClip(new RectangleGeometry(whole, 10, 10));
        dc.DrawRectangle(new LinearGradientBrush(Theme.C(Look.GlassBottom), Theme.C(Look.Page), 90), null, whole);
        // In a picture drawn to a file: a hand a little left of the box's middle, as if it were being tracked.
        System.Windows.Media.Imaging.BitmapSource? picture = Theme.Static ? null : Hands.HandsPreview.Picture;
        HandPoint? point = Theme.Static ? new HandPoint(0.47, 0.55) : running() ? Hands.HandsPreview.Point : null;
        if (picture != null && running())
        {
            // The picture fills the panel, cut to its shape.
            double k = Math.Max(Wide / picture.PixelWidth, High / picture.PixelHeight);
            double pw = picture.PixelWidth * k, ph = picture.PixelHeight * k;
            dc.DrawImage(picture, new Rect((Wide - pw) / 2, (High - ph) / 2, pw, ph));
        }
        else if (Theme.Static || !running())
        {
            const double length = 96, k = length / HandsGlyph.Side;
            double px = (point?.X ?? 0.5) * Wide, py = (point?.Y ?? 0.5) * High;
            // The drawing's pinch is put on the tracked point.
            dc.PushOpacity(Theme.Static ? 0.8 : 0.35);
            dc.PushTransform(new TranslateTransform(px - HandsGlyph.Meet.X * k, py - HandsGlyph.Meet.Y * k));
            dc.PushTransform(new ScaleTransform(k, k));
            HandsGlyph.DrawHand(dc, HandsGlyphKind.Pinch);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
        var box = new HandAirBox();
        double bw = HandAirBox.WidthShare * Wide, bh = HandAirBox.HeightShare * High;
        var dashed = new Pen(Theme.B(Look.Accent.Alpha(0.9)), 1.5) { DashStyle = new DashStyle(new[] { 5 / 1.5, 4 / 1.5 }, 0) };
        dc.DrawRoundedRectangle(null, dashed, new Rect(box.Centre.X * Wide - bw / 2, box.Centre.Y * High - bh / 2, bw, bh), 6, 6);
        if (point is HandPoint p)
        {
            var at = new Point(p.X * Wide, p.Y * High);
            dc.DrawEllipse(Theme.B(Look.Accent.Alpha(0.3)), null, at, 9, 9);
            dc.DrawEllipse(Theme.Accent, null, at, 4.5, 4.5);
            dc.DrawEllipse(Brushes.White, null, at, 1.5, 1.5);
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Theme.Rim, 1), new Rect(0.5, 0.5, Wide - 1, High - 1), 10, 10);
    }
}
