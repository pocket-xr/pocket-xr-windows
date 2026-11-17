using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Ui;

namespace PocketXR.Windows;

// Pocket mode. On the phone it is a black, locked screen for walking about with the phone in a pocket. On a PC the
// same idea is: everything lives in the glasses, and the PC's own screens go dark and out of the way, for privacy on
// a train or a plane and so the pointer never wanders onto a screen nobody is looking at.
//
// The black is only a picture: a window covers each screen. Nothing touches the brightness, so an LCD's backlight
// stays on (an OLED screen does go dark where it is black).
//
// Ways out, none of which needs a permission: the shortcut, the tray menu, the launcher, the switch in the settings
// (all through IAppActions.SetPocketMode), and from the PC's own screen holding the mouse button on the black for a
// second. The pointer is kept off that screen, but not while the button is down, so it can be dragged there: press on
// a floating screen, move back onto the PC's screen, keep holding. Nothing listens for keys; the button and the
// pointer's place are only looked at, 30 times a second.
//
// It cannot be left stuck: it ends by itself when the floating screens go, and when the glasses' display has been
// gone for 5 seconds.
//
// Not tried on a PC yet: it needs floating screens, which need the glasses' picture. The watch's arithmetic is
// checked (ControllerChecks).

/// <summary>Pocket mode: the PC's own screens black and locked, everything in the glasses.</summary>
public sealed class PocketMode
{
    private readonly AppState state;
    private readonly IAppActions actions;
    /// <summary>One black window for each of the PC's own screens (every real monitor), by its Windows name.</summary>
    private readonly Dictionary<string, Cover> covers = new();
    private DispatcherTimer? timer;
    private PocketWatch watch = new();
    private int ticks;
    private bool glassesPresent = true;
    private double progress;
    private bool hintShown;

    public PocketMode(AppState state, IAppActions actions)
    {
        this.state = state;
        this.actions = actions;
    }

    public bool IsOn => timer != null || covers.Count > 0;

    public void Set(bool on)
    {
        if (on) Enter(); else Leave();
    }

    private void Enter()
    {
        if (IsOn) return;
        // With no floating screen there would be a black PC and nothing to look at.
        if (state.VirtualScreens <= 0)
        {
            Log.P("Pocket mode: not turned on, there are no floating screens");
            return;
        }
        FitCovers(arriving: true);
        if (covers.Count == 0)
        {
            Log.P("Pocket mode: not turned on, the PC's own screen was not found");
            return;
        }
        watch = new PocketWatch(state.PointerScreen ?? 0);
        glassesPresent = Displays.Glasses(Displays.List()) != null;
        ticks = 0;
        progress = 0;
        hintShown = false;
        state.PocketMode = true;
        state.Notify();
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    /// <summary>Always takes every black window away and stops the timer, whatever state it was in.</summary>
    private void Leave()
    {
        timer?.Stop();
        timer = null;
        foreach (Cover c in covers.Values) c.Shut();
        covers.Clear();
        progress = 0;
        hintShown = false;
        if (state.PocketMode)
        {
            state.PocketMode = false;
            state.Notify();
        }
    }

    /// <summary>Leaves from the inside (the hold, or nothing left to look at): through the app, so it is logged and
    /// everything that shows Pocket mode hears of it, and then directly in case the app did not.</summary>
    private void LeaveByItself(string why)
    {
        Log.P($"Pocket mode: {why}");
        actions.SetPocketMode(false);
        if (IsOn || state.PocketMode) Leave();
    }

    /// <summary>The PC's own screens: every display that is neither the glasses nor a floating screen. Never the
    /// glasses: a cover over them would black out the one thing there is to look at.</summary>
    private static List<DisplayInfo> OwnScreens() =>
        Displays.List().Where(d => !Glasses.IsGlassesScreenName(d.Name) && !Displays.IsVirtual(d)).ToList();

    /// <summary>One black window on each of the PC's own screens, as they are now (screens come, go and change size).</summary>
    private void FitCovers(bool arriving = false)
    {
        Dictionary<string, DisplayInfo> wanted = OwnScreens().ToDictionary(d => d.GdiName);
        foreach (string name in covers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            covers[name].Shut();
            covers.Remove(name);
        }
        foreach ((string name, DisplayInfo screen) in wanted)
        {
            if (covers.TryGetValue(name, out Cover? cover)) cover.Fit(screen);
            else covers[name] = new Cover(screen, arriving);
        }
    }

    // 30 times a second
    private void Tick()
    {
        if (timer == null) return;
        double now = HeadFusion.SystemUptime;
        ticks++;
        if (ticks % 15 == 0)
        {
            glassesPresent = Displays.Glasses(Displays.List()) != null;
            FitCovers();
        }
        List<DisplayTile> floating = state.DisplayTiles.Where(t => !t.IsOwn).ToList();
        bool onCover = false;
        int? onScreen;
        if (Pointer.Position is (int x, int y))
        {
            onCover = covers.Values.Any(c => c.Screen.Contains(x, y));
            onScreen = floating.FirstOrDefault(t => t.Frame.Contains(new Point(x, y)))?.Id;
            // Before the app has published where the floating screens are, only a black screen is known not to be one.
            if (floating.Count == 0 && !onCover) onScreen = watch.LastScreen;
        }
        else
        {
            onScreen = watch.LastScreen;
        }
        bool buttonDown = (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0; // the main button, swapped or not
        int count = state.VirtualScreens > 0 ? Math.Max(floating.Count, 1) : 0;
        PocketWatch.StepResult step = watch.Step(now, buttonDown, onCover, onScreen, count, glassesPresent);
        switch (step.Kind)
        {
            case PocketWatch.StepKind.PushBack:
                actions.MovePointer(step.Screen);
                break;
            case PocketWatch.StepKind.Leave:
                LeaveByItself(step.Reason switch
                {
                    PocketWatch.Leave.Held => "left by holding the button on the PC's screen",
                    PocketWatch.Leave.NoScreens => "left by itself, the floating screens are gone",
                    _ => "left by itself, the glasses' display has been gone for 5 s",
                });
                return;
        }
        double p = watch.Progress(now);
        bool hint = watch.HintShown(now);
        if (p != progress || hint != hintShown)
        {
            progress = p;
            hintShown = hint;
            foreach (Cover c in covers.Values) c.Face.Set(p, hint);
        }
    }

    /// <summary>A borderless black window over one of the PC's own screens, above the taskbar. It takes clicks (so
    /// nothing under it is clicked by mistake) without bringing Pocket XR to the front.</summary>
    private sealed class Cover
    {
        public DisplayInfo Screen { get; private set; }
        public readonly CoverFace Face = new();
        private readonly Window window;

        public Cover(DisplayInfo screen, bool faded)
        {
            Screen = screen;
            window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                Background = Brushes.Black, Content = Face, ShowActivated = false, Focusable = false, Title = "Pocket mode",
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 100, Height = 100,
            };
            window.SourceInitialized += (_, _) =>
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                // No activation and no place in Alt+Tab.
                SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000L | 0x00000080L));
                Place();
            };
            bool fade = faded && !Theme.ReduceMotion;
            window.Opacity = 1;
            Face.Opacity = fade ? 0 : 1;
            window.Show();
            if (fade) Theme.Animate(Face, UIElement.OpacityProperty, 1);
        }

        public void Fit(DisplayInfo screen)
        {
            if (screen == Screen) return;
            Screen = screen;
            Place();
        }

        private void Place()
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, new IntPtr(-1), Screen.X, Screen.Y, Screen.Width, Screen.Height, 0x0010 | 0x0040); // topmost, no activate, shown
        }

        /// <summary>Goes at once: leaving must never wait on an animation.</summary>
        public void Shut() => window.Close();
    }

    /// <summary>Black, and in the middle, very faint, a small lock and "Pocket mode". While the button is held on it a
    /// ring fills round the lock and "Hold to leave Pocket mode" is up.</summary>
    public sealed class CoverFace : FrameworkElement
    {
        private double progress;
        private bool hintShown;
        private const double Ring = 64, Faint = 0.08;

        public CoverFace()
        {
            AutomationProperties.SetName(this, "Pocket mode");
            AutomationProperties.SetHelpText(this, "Hold the mouse button here for a second to leave");
        }

        public void Set(double progress, bool hintShown)
        {
            this.progress = progress;
            this.hintShown = hintShown;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 960 : available.Width, double.IsInfinity(available.Height) ? 540 : available.Height);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            var centre = new Point(w / 2, h / 2);
            bool held = progress > 0;
            if (hintShown) dc.DrawEllipse(null, new Pen(Theme.White(0.12), 2), centre, Ring / 2, Ring / 2);
            if (held)
            {
                double sweep = Math.Clamp(progress, 0, 1) * 360;
                var pen = new Pen(Theme.Held, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (sweep >= 359.9)
                {
                    dc.DrawEllipse(null, pen, centre, Ring / 2, Ring / 2);
                }
                else
                {
                    double a = (sweep - 90) * Math.PI / 180;
                    var g = new StreamGeometry();
                    using (StreamGeometryContext c = g.Open())
                    {
                        c.BeginFigure(new Point(centre.X, centre.Y - Ring / 2), false, false);
                        c.ArcTo(new Point(centre.X + Ring / 2 * Math.Cos(a), centre.Y + Ring / 2 * Math.Sin(a)), new Size(Ring / 2, Ring / 2), 0,
                            sweep > 180, SweepDirection.Clockwise, true, true);
                    }
                    dc.DrawGeometry(null, pen, g);
                }
            }
            FormattedText glyph = Draw.Icon(Glyph.Lock, 21, Theme.White(held ? 0.64 : Faint), dip);
            dc.DrawText(glyph, new Point(centre.X - glyph.Width / 2, centre.Y - glyph.Height / 2));
            // Under the ring, so the lock itself is the screen's exact middle.
            FormattedText name = Draw.Text("Pocket mode", 13, Theme.White(Faint), FontWeights.Medium, dip);
            double y = centre.Y + Ring / 2 + 22;
            dc.DrawText(name, new Point(centre.X - name.Width / 2, y));
            if (hintShown)
            {
                FormattedText hint = Draw.Text("Hold to leave Pocket mode", Theme.NoteSize, Theme.White(0.56), FontWeights.Normal, dip);
                dc.DrawText(hint, new Point(centre.X - hint.Width / 2, y + name.Height + 6));
            }
        }
    }

    public static void Pictures(string folder)
    {
        var resting = new CoverFace { Width = 960, Height = 540 };
        UiRender.Png(Path.Combine(folder, "pocket-mode-1.png"), resting);
        var held = new CoverFace { Width = 960, Height = 540 };
        held.Set(0.6, true);
        UiRender.Png(Path.Combine(folder, "pocket-mode-2-held.png"), held);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
