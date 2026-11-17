using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The tour's window (Pocket XR for Mac's TourWindow.swift and TourScreen.swift): the picture on the left, bleeding to
// the window's edge, and a few words on the right, one idea per step, with Skip, Back and Next on every step. The
// steps and the pictures' beats come from the core's Tour; this draws them.

/// <summary>A value that glides to each new target on the enter curve, read while drawing.</summary>
public struct TourEased
{
    public double From, To, At, Duration;

    public TourEased(double value, double duration = Look.Enter)
    {
        From = value;
        To = value;
        At = 0;
        Duration = duration;
    }

    public readonly double Value(double now) => From + (To - From) * TourEase.Enter(TourEase.Span(now, At, At + Duration));

    /// <summary>Glide to [target] from wherever the value is at [now].</summary>
    public void Set(double target, double now)
    {
        From = Value(now);
        To = target;
        At = now;
    }

    public void Snap(double value)
    {
        From = value;
        To = value;
    }
}

/// <summary>Which step is showing, and what the pictures need that changes over time.</summary>
public sealed class TourModel
{
    public TourStep Step = TourStep.Welcome;
    /// <summary>The way the last move went, for the steps to slide the same way.</summary>
    public bool Forward = true;
    /// <summary>A control on the Arrange step has been touched: its picture stops showing itself for good.</summary>
    public bool ArrangeTouched;

    private readonly Dictionary<TourStep, double> starts = new();
    private readonly double opened = Now();

    // Read only while drawing.
    public TourEased Link = new(0, Look.Beat);
    public TourEased Size = new(40);
    public TourEased Distance = new(1.4);
    public TourEased Curve = new(0);
    public TourEased LayoutMix = new(1, Look.Beat);
    public ArrangeLayout LayoutFrom = ArrangeLayout.Row, LayoutTo = ArrangeLayout.Row;
    public int Screens = 2;
    /// <summary>When everything needed became set up (seconds on the tour's clock), null while something is to do.</summary>
    public double? CompleteSince;

    /// <summary>The tour's clock, in seconds.</summary>
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>When [step] last came up: its picture's clock starts there.</summary>
    public double Start(TourStep step) => starts.TryGetValue(step, out double at) ? at : opened;

    /// <summary>Opens the tour at [step] with nothing moving.</summary>
    public void Open(TourStep step)
    {
        starts.Clear();
        starts[step] = Now();
        Forward = true;
        Step = step;
    }

    /// <summary>Moves to [to]; false when the tour is already there.</summary>
    public bool Go(TourStep to)
    {
        if (to == Step) return false;
        Forward = to.RawValue() > Step.RawValue();
        starts[to] = Now();
        Step = to;
        return true;
    }

    /// <summary>Takes everything as it is now, with nothing moving.</summary>
    public void Snap(SettingsStore store, AppState state)
    {
        SpaceSettings s = store.S;
        Link.Snap(TourNeeds.Connected(state) ? 1 : 0);
        Size.Snap(s.WidthDeg);
        Distance.Snap(s.Distance);
        Curve.Snap(s.Curve);
        LayoutFrom = s.Layout;
        LayoutTo = s.Layout;
        LayoutMix.Snap(1);
        Screens = Math.Max(state.VirtualScreens, s.Screens);
        CompleteSince = TourNeeds.Ready(state) ? 0 : null;
    }

    public void SetLayout(ArrangeLayout layout)
    {
        if (layout == LayoutTo) return;
        LayoutFrom = LayoutTo;
        LayoutTo = layout;
        LayoutMix = new TourEased(0, Look.Beat);
        LayoutMix.Set(1, Now());
    }

    /// <summary>The first touch of a control on the Arrange step: the picture goes from where its own demonstration
    /// had got to, to the Space's real values, and follows them from then on.</summary>
    public void TouchArrange()
    {
        if (ArrangeTouched) return;
        double now = Now();
        (double sizeDeg, double distance, double curve) = TourScenes.ArrangeGhost(now - Start(TourStep.Arrange));
        double realSize = Size.To, realDistance = Distance.To, realCurve = Curve.To;
        Size = new TourEased(sizeDeg);
        Size.Set(realSize, now);
        Distance = new TourEased(distance);
        Distance.Set(realDistance, now);
        Curve = new TourEased(curve);
        Curve.Set(realCurve, now);
        ArrangeTouched = true;
    }

    /// <summary>What a moving picture needs, [t] seconds after its step came up.</summary>
    public TourSceneInput Input(double t, double now) => new()
    {
        Link = Link.Value(now),
        CompleteFor = CompleteSince is double since ? Math.Max(Math.Min(t, now - since), 0) : null,
        Arrange = new TourArrangeInput
        {
            SizeDeg = Size.Value(now), Distance = Distance.Value(now), Curve = Curve.Value(now),
            From = LayoutFrom, To = LayoutTo, Mix = LayoutMix.Value(now), Screens = Screens, Ghost = !ArrangeTouched,
        },
    };

    /// <summary>What a still picture needs: everything as it is, nothing on its way.</summary>
    public static TourSceneInput StillInput(SettingsStore store, AppState state, double t, bool touched)
    {
        SpaceSettings s = store.S;
        return new TourSceneInput
        {
            Link = TourNeeds.Connected(state) ? 1 : 0,
            CompleteFor = TourNeeds.Ready(state) ? t : null,
            Arrange = new TourArrangeInput
            {
                SizeDeg = s.WidthDeg, Distance = s.Distance, Curve = s.Curve, From = s.Layout, To = s.Layout, Mix = 1,
                Screens = Math.Max(state.VirtualScreens, s.Screens), Ghost = !touched,
            },
        };
    }
}

/// <summary>One step's picture: drawn from the scene clock (seconds since the step came up) each time it is asked to
/// draw. What to draw comes from [Source], so the picture holds no state of its own.</summary>
public sealed class TourPicture : FrameworkElement
{
    public Func<(TourStep Step, double T, TourSceneInput Input)>? Source;
    /// <summary>What the picture shows, for a screen reader.</summary>
    public string Says = "";

    public TourPicture()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    /// <summary>How wide the fade at the picture's right edge is.</summary>
    private const double Fade = 52;
    private static readonly LinearGradientBrush Veil = MakeVeil();

    private static LinearGradientBrush MakeVeil()
    {
        Color page = Theme.C(Look.Page);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, page.R, page.G, page.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(150, page.R, page.G, page.B), 0.5));
        b.GradientStops.Add(new GradientStop(page, 1));
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source == null) return;
        (TourStep step, double t, TourSceneInput input) = Source();
        TourScenes.Draw(step, dc, RenderSize, t, input, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // The picture bleeds to the window's left edge; on the words' side it sinks into the page, so a screen that
        // runs past the picture's edge is never cut by a hard line.
        dc.DrawRectangle(Veil, null, new Rect(RenderSize.Width - Fade, 0, Fade, RenderSize.Height));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PicturePeer(this);

    private sealed class PicturePeer : FrameworkElementAutomationPeer
    {
        private readonly TourPicture owner;
        public PicturePeer(TourPicture owner) : base(owner) { this.owner = owner; }
        protected override string GetNameCore() => owner.Says;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}

/// <summary>
/// The tour: the picture, the words, eight segments that fill as it goes on, Skip, Back and Next. [fixedTime] freezes
/// every picture at that second (for stills drawn to files).
/// </summary>
public sealed class TourView : Grid
{
    private readonly SettingsStore store;
    private readonly AppState state;
    private readonly IAppActions actions;
    private readonly double? fixedTime;
    public readonly TourModel Model = new();
    /// <summary>Done, Skip or Escape: the window's owner marks the tour seen and closes it.</summary>
    public Action Finish = () => { };

    private readonly TourPicture picture = new();
    private readonly Grid pageHost = new() { ClipToBounds = true };
    private readonly List<ScaleTransform> fills = new();
    private readonly Grid segments = new();
    private readonly TourQuietButton skip;
    private readonly PxButton back, next, done;
    private TourPage page;
    private FrameworkElement? shown;
    private int phase = int.MinValue;
    private string signature = "";
    private bool connected, ready, running;

    public TourView(SettingsStore store, AppState state, IAppActions actions, TourStep step = TourStep.Welcome, double? fixedTime = null,
        bool arrangeTouched = false)
    {
        this.store = store;
        this.state = state;
        this.actions = actions;
        this.fixedTime = fixedTime;
        Background = Theme.Page;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TourWindow.PictureWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        picture.Source = Current;
        Children.Add(picture);

        // The words' column: the segments and Skip, the step's words, Back and Next.
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetColumn(column, 1);
        Children.Add(column);

        for (int i = 0; i < Tour.Count; i++)
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fill = new ScaleTransform(0, 1);
            fills.Add(fill);
            var segment = new Grid { Height = 3, Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            segment.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Theme.Rim });
            segment.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Theme.Accent, RenderTransform = fill });
            SetColumn(segment, i);
            segments.Children.Add(segment);
        }
        skip = new TourQuietButton(TourCopy.Skip, () => Finish()) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var top = new DockPanel { Height = 30, Margin = new Thickness(Side, 10, 14, 0) };
        DockPanel.SetDock(skip, Dock.Right);
        top.Children.Add(skip);
        top.Children.Add(segments);
        column.Children.Add(top);

        // The words scroll if a step is ever taller than the window. A still is drawn without the scroll view.
        FrameworkElement words = pageHost;
        if (fixedTime == null)
        {
            words = new ScrollViewer
            {
                Content = pageHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            };
        }
        SetRow(words, 1);
        column.Children.Add(words);

        back = new PxButton(TourCopy.Back, null, ButtonKind.Glass, () => Move(-1)) { MinWidth = 84 };
        next = new PxButton(TourCopy.Next, null, ButtonKind.Primary, Advance) { MinWidth = 124 };
        done = new PxButton(TourCopy.Finish, null, ButtonKind.Primary, Advance) { MinWidth = 124 };
        var bottom = new DockPanel { Margin = new Thickness(Side, 10, Side, 22), LastChildFill = false };
        DockPanel.SetDock(back, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(done, Dock.Right);
        bottom.Children.Add(back);
        bottom.Children.Add(next);
        bottom.Children.Add(done);
        SetRow(bottom, 2);
        column.Children.Add(bottom);

        Model.Snap(store, state);
        Model.ArrangeTouched = arrangeTouched;
        Model.Open(step);
        connected = TourNeeds.Connected(state);
        ready = TourNeeds.Ready(state);
        page = NewPage(arrive: false);
        Show(arrive: !Still, slide: false);
    }

    /// <summary>The space at the sides of the words.</summary>
    public const double Side = 28;

    /// <summary>Stills and plain changes: a still being drawn, or Windows' animation setting off.</summary>
    private bool Still => fixedTime != null || Theme.Static || Theme.ReduceMotion;

    /// <summary>The second the picture is held at, or null while it runs.</summary>
    private double? Frozen => fixedTime ?? (Theme.Static || Theme.ReduceMotion ? Tour.Poster(Model.Step) : null);

    /// <summary>What the picture draws now.</summary>
    private (TourStep, double, TourSceneInput) Current()
    {
        TourStep step = Model.Step;
        if (Frozen is double held) return (step, held, TourModel.StillInput(store, state, held, Model.ArrangeTouched));
        double now = TourModel.Now();
        double t = now - Model.Start(step);
        return (step, t, Model.Input(t, now));
    }

    // ---- moving through the tour ----

    public void Move(int by) => Go(Tour.Move(Model.Step, by));

    public void Go(TourStep to)
    {
        if (!Model.Go(to)) return;
        Show(arrive: !Still, slide: !Still);
    }

    /// <summary>Next, or Done on the last step.</summary>
    public void Advance()
    {
        if (Tour.IsLast(Model.Step)) Finish();
        else Move(1);
    }

    /// <summary>Left and Right go a step back and on, Enter goes on (and finishes on the last step), Escape skips.
    /// True when the tour used the key.</summary>
    public bool OnKey(Key key, ModifierKeys held)
    {
        if ((held & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Left: Move(-1); return true;
            case System.Windows.Input.Key.Right: Move(1); return true;
            case System.Windows.Input.Key.Enter: Advance(); return true;
            case System.Windows.Input.Key.Escape: Finish(); return true;
            default: return false;
        }
    }

    /// <summary>The window came up: the tour starts again from its first step, with everything as it is now.</summary>
    public void Opened()
    {
        Model.Snap(store, state);
        Model.ArrangeTouched = false;
        Model.Open(TourStep.Welcome);
        connected = TourNeeds.Connected(state);
        ready = TourNeeds.Ready(state);
        Show(arrive: !Still, slide: false);
        if (!running)
        {
            running = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>The window went: nothing is drawn while it cannot be seen.</summary>
    public void Closed()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        if (Frozen == null) picture.InvalidateVisual();
        ShowPhase();
    }

    // ---- showing a step ----

    private TourPage NewPage(bool arrive) => new()
    {
        Store = store, State = state, Actions = actions, Model = Model, Arrive = arrive, Go = Go,
    };

    /// <summary>What of the app's state the words are built from: when it changes, they are built again.</summary>
    private string Signature() =>
        $"{state.GlassesName}|{state.Glasses?.Family}|{state.DisplayText}|{state.DriverReady}|{state.VirtualScreens}|{state.Capturing}|{Math.Max(state.VirtualScreens, store.S.Screens)}";

    /// <summary>Puts the step's words up, and the bars round them. [arrive]: the parts fade in one after another;
    /// [slide]: the words come in from the side the tour is moving to, as the old ones leave the other way.</summary>
    private void Show(bool arrive, bool slide)
    {
        TourStep step = Model.Step;
        signature = Signature();
        page = NewPage(arrive);
        FrameworkElement words = TourWords.Build(page, step);
        words.Margin = new Thickness(Side, 16, Side, 12);
        words.VerticalAlignment = VerticalAlignment.Top;

        FrameworkElement? old = shown;
        shown = words;
        if (old != null && slide)
        {
            // The step leaving fades and slides 16 points away in 160 ms; the one arriving slides in over 280 ms.
            double distance = Model.Forward ? 16 : -16;
            old.IsHitTestVisible = false;
            Leave(old, -distance);
            var move = new TranslateTransform(distance, 0);
            words.RenderTransform = move;
            Theme.Animate(move, TranslateTransform.XProperty, 0, Look.Enter);
        }
        else if (old != null)
        {
            pageHost.Children.Remove(old);
        }
        pageHost.Children.Add(words);

        for (int i = 0; i < fills.Count; i++)
        {
            double to = i <= step.RawValue() ? 1 : 0;
            if (Still) fills[i].ScaleX = to;
            else Theme.Animate(fills[i], ScaleTransform.ScaleXProperty, to);
        }
        AutomationProperties.SetName(segments, TourCopy.StepOf(step));
        bool last = Tour.IsLast(step);
        // The last step ends with Done; Skip would only say the same.
        skip.Visibility = last ? Visibility.Hidden : Visibility.Visible;
        back.Visibility = Tour.IsFirst(step) ? Visibility.Hidden : Visibility.Visible;
        next.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        done.Visibility = last ? Visibility.Visible : Visibility.Collapsed;

        // Recentre ticks only when it is really used while its step is showing, so the mark starts clear there.
        if (step == TourStep.Look && fixedTime == null && !Theme.Static) state.Recentred = false;
        page.Binder.Sync();
        phase = int.MinValue;
        ShowPhase();
        picture.Says = TourCopy.Picture(step, connected, ready);
        picture.InvalidateVisual();
    }

    private void Leave(FrameworkElement old, double by)
    {
        var move = new TranslateTransform();
        old.RenderTransform = move;
        var ease = new KeySpline(Look.EaseExit.Item1, Look.EaseExit.Item2, Look.EaseExit.Item3, Look.EaseExit.Item4);
        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(by, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Look.Exit)), ease));
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Look.Exit)), ease));
        fade.Completed += (_, _) => pageHost.Children.Remove(old);
        move.BeginAnimation(TranslateTransform.XProperty, slide);
        old.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Lights the line the picture is showing now.</summary>
    private void ShowPhase()
    {
        TourStep step = Model.Step;
        int now;
        if (step == TourStep.Arrange && Model.ArrangeTouched) now = -1;
        else if (fixedTime is double at) now = Tour.Phase(step, at);
        else if (Frozen != null) now = -1;
        else now = Tour.Phase(step, TourModel.Now() - Model.Start(step));
        if (now == phase) return;
        phase = now;
        foreach (Action<int> light in page.Phased) light(now);
    }

    // ---- keeping in step with the glasses and the settings ----

    public void SyncState()
    {
        double now = TourModel.Now();
        bool nowConnected = TourNeeds.Connected(state), nowReady = TourNeeds.Ready(state);
        if (nowConnected != connected)
        {
            connected = nowConnected;
            Model.Link.Set(connected ? 1 : 0, now);
        }
        if (nowReady != ready)
        {
            ready = nowReady;
            Model.CompleteSince = ready ? now : null;
        }
        Model.Screens = Math.Max(state.VirtualScreens, store.S.Screens);
        if (Signature() != signature && Model.Step is TourStep.Setup or TourStep.Done or TourStep.Arrange) Show(arrive: false, slide: false);
        else page.Binder.Sync();
        if (Frozen != null) picture.InvalidateVisual();
    }

    public void SyncStore()
    {
        double now = TourModel.Now();
        SpaceSettings s = store.S;
        if (s.WidthDeg != Model.Size.To) Model.Size.Set(s.WidthDeg, now);
        if (s.Distance != Model.Distance.To) Model.Distance.Set(s.Distance, now);
        if (s.Curve != Model.Curve.To) Model.Curve.Set(s.Curve, now);
        Model.SetLayout(s.Layout);
        Model.Screens = Math.Max(state.VirtualScreens, s.Screens);
        if (Signature() != signature && Model.Step == TourStep.Arrange) Show(arrive: false, slide: false);
        else page.Binder.Sync();
        if (Frozen != null) picture.InvalidateVisual();
    }
}

public static class TourWindow
{
    /// <summary>The tour under the window's strip: the picture's column and the words'.</summary>
    public const double Width = 960, Height = 680, PictureWidth = 520;

    /// <summary>
    /// The tour's window. It starts from the first step each time it comes up, and counts as seen once it is finished,
    /// skipped or closed by hand (not when Pocket XR quits or starts again with it open, so the tour comes back until
    /// its user has ended it themselves).
    /// </summary>
    public static PxWindow Window(SettingsStore store, AppState state, IAppActions actions)
    {
        var view = new TourView(store, state, actions);
        var w = new PxWindow(TourCopy.WindowTitle, view) { Width = Width, Height = Height + PxWindow.Strip };
        view.Finish = () =>
        {
            store.Update(s => s.TourSeen = true);
            w.Close();
        };
        // The keys that move through the tour. A control that used a key itself (a slider's arrows, a button's Enter)
        // has already taken it.
        w.KeyDown += (_, e) =>
        {
            if (view.OnKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        };
        w.IsVisibleChanged += (_, _) =>
        {
            if (w.IsVisible) view.Opened();
            else view.Closed();
        };
        // Closed by hand: seen. The mark is made a moment later, which never comes when Pocket XR is quitting.
        w.Closing += (_, _) =>
        {
            if (!w.HideOnClose) return;
            w.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => store.Update(s => s.TourSeen = true)));
        };
        store.Changed += (_, _) => view.SyncStore();
        state.Changed += view.SyncState;
        return w;
    }

    // ---- pictures ----

    /// <summary>What the app would know with an XREAL Aura plugged in, its display seen, and [screens] floating
    /// screens showing; or with less than that.</summary>
    private static AppState Sample(SettingsStore store, bool glasses, int screens, bool driver = true, bool display = true)
    {
        AppState state = Windows.Pictures.State(store, glasses: glasses, screens: screens, driver: driver);
        if (glasses)
        {
            state.Glasses = new Glasses(name: "XREAL Aura", family: GlassesFamily.Aura, vendor: Glasses.Xreal, product: 0);
            if (!display) state.DisplayText = null;
            state.PointerVisitedScreen = screens > 0;
        }
        return state;
    }

    private static void Still(string folder, string name, TourStep step, double time, SettingsStore store, AppState state, bool touched = false)
    {
        var view = new TourView(store, state, new NoActions(), step, time, touched);
        UiRender.WindowPng(Path.Combine(folder, name), TourCopy.WindowTitle, view, Width, Height + PxWindow.Strip);
    }

    /// <summary>
    /// The whole window drawn to PNG files, with no window and no glasses: every step at its poster second
    /// (tour-1-welcome-1.png ...) and at each of its other stills (-2, -3, -4), the setup step in each of its states,
    /// the last step with things still to do, and the Arrange step following the settings.
    /// </summary>
    public static void Pictures(string folder)
    {
        var plain = new SettingsStore(persist: false);
        AppState all = Sample(plain, glasses: true, screens: 2);
        foreach (TourStep step in Tour.Steps)
        {
            var moments = new List<double> { Tour.Poster(step) };
            moments.AddRange(Tour.Stills(step));
            for (int k = 0; k < moments.Count; k++)
                Still(folder, $"tour-{step.Number()}-{step.Slug()}-{k + 1}.png", step, moments[k], plain, all);
        }

        double setup = Tour.Poster(TourStep.Setup);
        Still(folder, "tour-2-setup-no-glasses.png", TourStep.Setup, setup, plain, Sample(plain, glasses: false, screens: 0, driver: false));
        Still(folder, "tour-2-setup-no-picture.png", TourStep.Setup, setup, plain, Sample(plain, glasses: true, screens: 0, display: false));
        Still(folder, "tour-2-setup-no-floating-screens.png", TourStep.Setup, setup, plain, Sample(plain, glasses: true, screens: 0, driver: false));
        Still(folder, "tour-2-setup-ready.png", TourStep.Setup, setup, plain, all);

        double end = Tour.Poster(TourStep.Done);
        Still(folder, "tour-8-done-nothing-yet.png", TourStep.Done, end, plain, Sample(plain, glasses: false, screens: 0, driver: false));
        Still(folder, "tour-8-done-not-showing.png", TourStep.Done, end, plain, Sample(plain, glasses: true, screens: 0));

        // The Arrange step once a control has been touched: the picture follows the Space's own values.
        var three = new SettingsStore(persist: false);
        three.Update(s =>
        {
            s.Screens = 3;
            s.Layout = ArrangeLayout.MainSide;
            s.WidthDeg = 46;
            s.Curve = 0.6;
        });
        Still(folder, "tour-5-arrange-yours.png", TourStep.Arrange, Tour.Poster(TourStep.Arrange), three, Sample(three, glasses: true, screens: 3), touched: true);
    }
}
