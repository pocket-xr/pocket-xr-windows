using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The tour's words, step by step, and the small parts they are built from (Pocket XR for Mac's TourWords.swift): the
// numbered title, numbered lines whose circle turns to the accent with a tick, the setup checklist's rows, the Arrange
// step's controls. Everything that arrives fades in and rises 12 points, 60 ms after the thing before it; with
// animation off in Windows it is simply there.

/// <summary>What a step's words are built from and report to.</summary>
public sealed class TourPage
{
    public required SettingsStore Store;
    public required AppState State;
    public required IAppActions Actions;
    public required TourModel Model;
    /// <summary>Shows values again when the settings or the state change.</summary>
    public Binder Binder = new();
    /// <summary>Called with the line the picture is showing now (-1 for none).</summary>
    public List<Action<int>> Phased = new();
    /// <summary>The parts arrive one after another (a step that has just come up), or are simply there.</summary>
    public bool Arrive;
    public Action<TourStep> Go = _ => { };

    public T In<T>(T element, int order) where T : FrameworkElement => Arrive ? Theme.Arrive(element, order) : element;
}

public static class TourWords
{
    /// <summary>One step's words, top to bottom.</summary>
    public static FrameworkElement Build(TourPage c, TourStep step)
    {
        var page = new StackPanel();
        void Add(FrameworkElement e, int order, double top = 0)
        {
            if (top > 0) e.Margin = new Thickness(e.Margin.Left, e.Margin.Top + top, e.Margin.Right, e.Margin.Bottom);
            page.Children.Add(c.In(e, order));
        }
        bool ready = TourNeeds.Ready(c.State);
        Add(Title(step.Number(), TourCopy.Title(step, ready)), 1);
        switch (step)
        {
            case TourStep.Welcome:
                Add(Body(TourCopy.WelcomeBody), 2);
                Add(Note(TourCopy.WelcomeNote), 3);
                Add(Note(TourCopy.WelcomeEarly), 4, -2);
                break;

            case TourStep.Setup:
                Add(Body(TourCopy.SetupBody), 2);
                Add(SetupCard(c), 3, 14);
                break;

            case TourStep.Screens:
            {
                Add(Body(TourCopy.ScreensBody), 2);
                // The first line ticks when the pointer has really been on a floating screen.
                var pointer = new TourLine(1, TourCopy.ScreensPointer);
                var drag = new TourLine(2, TourCopy.ScreensDrag);
                var count = new TourLine(3, TourCopy.ScreensCount);
                c.Phased.Add(p =>
                {
                    pointer.SetLit(p == 0);
                    drag.SetLit(p == 1);
                });
                c.Binder.Add(() => pointer.SetDone(c.State.PointerVisitedScreen));
                Add(Kit.Down(8, pointer, drag, count), 3, 14);
                break;
            }

            case TourStep.Look:
            {
                Add(Body(TourCopy.LookBody), 2);
                Add(Body(TourCopy.LookRecentre()), 3);
                // Ticks only when Recentre is really used while this step is showing.
                var tryIt = new TourLine(1, TourCopy.LookTry());
                c.Binder.Add(() => tryIt.SetDone(c.State.Recentred));
                Add(tryIt, 4, 14);
                var recentre = new PxButton(TourCopy.Recentre, Glyph.Recentre, ButtonKind.Glass, c.Actions.Recentre) { HorizontalAlignment = HorizontalAlignment.Left };
                Add(recentre, 5, 14);
                break;
            }

            case TourStep.Arrange:
                Add(Body(TourCopy.ArrangeBody), 2);
                Add(ArrangeControls(c), 3, 14);
                Add(Note(TourCopy.ArrangeNote), 4);
                break;

            case TourStep.Depth:
            {
                var depth = new TourTopic(TourCopy.Depth3D, TourCopy.Depth3DLine, TourCopy.Depth3DNote());
                var steady = new TourTopic(TourCopy.Steady, TourCopy.SteadyLine(), TourCopy.SteadyNote);
                c.Phased.Add(p =>
                {
                    depth.SetLit(p == 0);
                    steady.SetLit(p == 1);
                });
                Add(Kit.Down(18, depth, steady), 2, 18);
                break;
            }

            case TourStep.Menu:
            {
                Add(Body(TourCopy.MenuBody), 2);
                var open = new TourLine(1, TourCopy.MenuOpen);
                var quit = new TourLine(2, TourCopy.MenuQuit);
                var unplug = new TourLine(3, TourCopy.MenuUnplug);
                c.Phased.Add(p =>
                {
                    open.SetLit(p == 0);
                    quit.SetLit(p == 1);
                    unplug.SetLit(p == 2);
                });
                Add(Kit.Down(8, open, quit, unplug), 3, 14);
                if (PcScreens.PcScreenLauncher.Available) Add(Kit.Small(TourCopy.MenuOtherComputers), 4, 14);
                break;
            }

            default:
            {
                List<TourOpen> open = TourNeeds.Open(c.State);
                if (open.Count == 0)
                {
                    Add(Body(TourCopy.DoneReady), 2);
                }
                else
                {
                    Add(Body(TourCopy.DoneOpen), 2);
                    var lines = new StackPanel();
                    for (int i = 0; i < open.Count; i++)
                    {
                        var line = new TourLine(i + 1, TourCopy.Remaining(open[i]));
                        if (i > 0) line.Margin = new Thickness(0, 8, 0, 0);
                        lines.Children.Add(line);
                    }
                    Add(lines, 3, 14);
                    Add(new PxButton(TourCopy.DoneToSetup, null, ButtonKind.Glass, () => c.Go(TourStep.Setup)) { HorizontalAlignment = HorizontalAlignment.Left }, 3, 14);
                }
                Add(Note(TourCopy.DoneAgain), 4);
                Add(Note(TourCopy.DoneShortcuts), 5, -8);
                break;
            }
        }
        return page;
    }

    // ---- words ----

    /// <summary>Wrapped words with room between the lines.</summary>
    public static TextBlock Para(string text, double size, Brush ink, double lineHeight, FontWeight? weight = null)
    {
        TextBlock t = Kit.Text(text, size, ink, weight);
        t.LineHeight = lineHeight;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        return t;
    }

    /// <summary>The step's number in the accent, and its title: a heading for a screen reader.</summary>
    public static FrameworkElement Title(int number, string text)
    {
        TextBlock n = Kit.Text(number.ToString(System.Globalization.CultureInfo.InvariantCulture), 16, Theme.Page, FontWeights.SemiBold, wrap: false);
        n.HorizontalAlignment = HorizontalAlignment.Center;
        n.VerticalAlignment = VerticalAlignment.Center;
        n.Margin = new Thickness(0, 0, 0, 1);
        var badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Theme.Accent, Child = n,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0),
        };
        TextBlock title = Para(text, Theme.TitleSize, Theme.Ink, 34, FontWeights.Medium);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        AutomationProperties.SetName(title, text.Replace('\n', ' '));
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(title);
        return row;
    }

    public static TextBlock Body(string text)
    {
        TextBlock t = Para(text, Theme.BodySize, Theme.Ink2, 22);
        t.Margin = new Thickness(0, 10, 0, 0);
        return t;
    }

    public static TextBlock Note(string text)
    {
        TextBlock t = Para(text, Theme.NoteSize, Theme.Ink3, 18);
        t.Margin = new Thickness(0, 14, 0, 0);
        return t;
    }

    /// <summary>A tick as two strokes, for a box [size] across.</summary>
    public static Polyline Tick(double size, Brush ink, double weight = 2)
    {
        double r = size / 2;
        return new Polyline
        {
            Points = new PointCollection { new(r - r * 0.42, r + r * 0.02), new(r - r * 0.1, r + r * 0.34), new(r + r * 0.46, r - r * 0.3) },
            Stroke = ink, StrokeThickness = weight, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = size, Height = size,
        };
    }

    /// <summary>A shortcut's keys, as a chip.</summary>
    public static Border KeysChip(string keys)
    {
        TextBlock t = Kit.Text(keys, 11, Theme.Ink2, FontWeights.Medium, wrap: false);
        t.VerticalAlignment = VerticalAlignment.Center;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(5), Background = Theme.GlassHover, Padding = new Thickness(6, 2, 6, 3), Child = t,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(chip, "Shortcut " + keys.Replace("+", " "));
        return chip;
    }

    // ---- 2. the setup checklist ----

    /// <summary>What is needed, top to bottom, each with where it stands now and the button for the next thing to do.</summary>
    private static FrameworkElement SetupCard(TourPage c)
    {
        AppState state = c.State;
        bool connected = TourNeeds.Connected(state), seen = TourNeeds.PictureSeen(state);
        bool floating = TourNeeds.FloatingReady(state), showing = TourNeeds.Showing(state);
        string name = state.GlassesName ?? TourCopy.GlassesTitle;
        var rows = new StackPanel();
        void Rule() => rows.Children.Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(16, 0, 16, 0) });

        // The glasses: plugged in or not, and what is known for them on a PC.
        string? support = connected && Tour.Support(state.Glasses?.Family) is TourSupport s ? TourCopy.Support(s, name) : null;
        rows.Children.Add(SetupRow(connected, false, TourCopy.GlassesTitle,
            connected ? TourCopy.GlassesConnected(name) : TourCopy.GlassesUnplugged, support, out _));
        Rule();

        // A picture reaching them: Windows has a display for the glasses.
        rows.Children.Add(SetupRow(seen, false, TourCopy.PictureTitle,
            seen ? TourCopy.PictureSeen(state.DisplayText ?? "") : connected ? TourCopy.PictureNotSeen : TourCopy.PictureWaits,
            connected && !seen ? TourCopy.PictureNeeds : null, out _));
        Rule();

        // Floating screens: the virtual display driver.
        string floatingLine = !floating ? TourCopy.FloatingNotReady
            : state.VirtualScreens > 0 ? TourCopy.FloatingReady(state.VirtualScreens)
            : connected ? TourCopy.FloatingReadyNone : TourCopy.FloatingReadyNoGlasses;
        PxButton? help = floating ? null : new PxButton(TourCopy.FloatingHelp, Glyph.Help, ButtonKind.Glass, c.Actions.OpenDriverHelp);
        rows.Children.Add(SetupRow(floating, false, TourCopy.FloatingTitle, floatingLine, floating ? null : TourCopy.FloatingNeeds, out _, help));
        Rule();

        // Screens showing in the glasses.
        rows.Children.Add(SetupRow(showing, false, TourCopy.ShowingTitle,
            showing ? TourCopy.ShowingNow(state.Capturing) : TourCopy.ShowingNotYet,
            !showing && seen && floating ? TourCopy.ShowingWhere : null, out _));
        Rule();

        // Optional: open when Windows starts.
        var toggle = new Toggle(c.Binder, TourCopy.SignInTitle, () => c.Store.S.LaunchAtLogin, v => c.Store.Update(x => x.LaunchAtLogin = v));
        rows.Children.Add(SetupRow(c.Store.S.LaunchAtLogin, true, TourCopy.SignInTitle, TourCopy.SignInLine, null, out Action<bool> showLogin, null, toggle));
        c.Binder.Add(() => showLogin(c.Store.S.LaunchAtLogin));
        return Kit.Plate(rows);
    }

    /// <summary>
    /// One thing to set up: a ring that turns to the accent with a tick when it really is done, its name, where it
    /// stands (in the accent when done), a quieter line of what it needs, and its button or switch. A screen reader
    /// hears Done, To do or Optional. [show] shows it done or not again (a switch changes that while it is up).
    /// </summary>
    private static Grid SetupRow(bool done, bool optional, string title, string line, string? detail, out Action<bool> show,
        PxButton? button = null, Toggle? toggle = null)
    {
        var row = new Grid { Margin = new Thickness(16, 9, 14, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = new Grid { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var circle = new Ellipse { StrokeThickness = 2 };
        Polyline tick = Tick(20, Theme.Accent);
        ring.Children.Add(circle);
        ring.Children.Add(tick);
        row.Children.Add(ring);

        var words = new StackPanel();
        TextBlock name = Kit.Text(title, 15, Theme.Ink, FontWeights.Medium, wrap: false);
        Border? chip = null;
        if (optional)
        {
            TextBlock chipText = Kit.Text(TourCopy.StateOptional, Theme.ChipSize, Theme.Ink2, wrap: false);
            chip = new Border
            {
                CornerRadius = new CornerRadius(9), Background = Theme.GlassHover, Padding = new Thickness(8, 1, 8, 2), Child = chipText,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0),
            };
            words.Children.Add(Kit.Across(0, name, chip));
        }
        else
        {
            words.Children.Add(name);
        }
        TextBlock status = Para(line, 13.5, Theme.Ink2, 19);
        status.Margin = new Thickness(0, 1, 0, 0);
        words.Children.Add(status);
        if (detail != null)
        {
            TextBlock more = Para(detail, Theme.NoteSize, Theme.Ink3, 17);
            more.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(more);
        }
        if (button != null)
        {
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 9, 0, 2);
            words.Children.Add(button);
        }
        AutomationProperties.SetName(words, $"{title}. {line} {detail}".Trim());
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        if (toggle != null)
        {
            toggle.VerticalAlignment = VerticalAlignment.Top;
            toggle.Margin = new Thickness(8, -2, 0, 0);
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
        }

        void Show(bool isDone)
        {
            // An optional thing that is off is not a thing left undone: its ring stays quiet, and its line says what
            // it does, on or off, so it never reads as a result.
            circle.Stroke = isDone ? Theme.Accent : optional ? Theme.Rim : Theme.RimFocus;
            tick.Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
            if (chip != null) chip.Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;
            status.Foreground = isDone && !optional ? Theme.Accent : Theme.Ink2;
            AutomationProperties.SetItemStatus(words, isDone ? TourCopy.StateDone : optional ? TourCopy.StateOptional : TourCopy.StateToDo);
        }
        Show(done);
        show = Show;
        return row;
    }

    // ---- 5. the Arrange step's controls ----

    private static FrameworkElement ArrangeControls(TourPage c)
    {
        SettingsStore store = c.Store;
        // A setting of the Space, changed so that the first change stops the picture's own demonstration.
        void Set(Action<SpaceSettings> change)
        {
            c.Model.TouchArrange();
            store.Update(change);
        }
        (TourMark Mark, FrameworkElement Row) size = ControlRow(c.Binder, TourCopy.ArrangeSize, () => store.S.WidthDeg, v => Set(s => s.WidthDeg = v),
            SpaceSettings.WidthRange, 1, TourCopy.SizeValue, "smaller", "bigger");
        (TourMark Mark, FrameworkElement Row) distance = ControlRow(c.Binder, TourCopy.ArrangeDistance, () => store.S.Distance, v => Set(s => s.Distance = v),
            SpaceSettings.DistanceRange, 0.1, TourCopy.DistanceValue, "nearer", "further");
        (TourMark Mark, FrameworkElement Row) curve = ControlRow(c.Binder, TourCopy.ArrangeCurve, () => store.S.Curve, v => Set(s => s.Curve = v),
            SpaceSettings.CurveRange, 0.1, TourCopy.CurveValue, "lessCurve", "moreCurve");
        c.Phased.Add(p =>
        {
            size.Mark.SetLit(p == 0);
            distance.Mark.SetLit(p == 1);
            curve.Mark.SetLit(p == 2);
        });

        // The layouts there are for the screens you have, by the names the menu and the control panel use.
        int count = Math.Max(c.State.VirtualScreens, store.S.Screens);
        List<ArrangeLayout> choices = SpaceLayout.Choices(count).ToList();
        var picker = new Segmented(c.Binder, TourCopy.LayoutTitle, choices.Select(l => SpaceLayout.Title(l, count)).ToList(),
            () => choices.IndexOf(store.S.Layout), i => Set(s => s.Layout = choices[i])).Stretch().SmallText();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        Border keys = KeysChip(TourCopy.Keys("layout"));
        DockPanel.SetDock(keys, Dock.Right);
        head.Children.Add(keys);
        TextBlock layoutName = Kit.Text(TourCopy.LayoutTitle, 15, Theme.Ink, FontWeights.Medium, wrap: false);
        layoutName.VerticalAlignment = VerticalAlignment.Center;
        layoutName.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(Kit.Across(0, new TourMark(), layoutName));
        var layout = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        layout.Children.Add(head);
        layout.Children.Add(picker);
        return Kit.Down(6, size.Row, distance.Row, curve.Row, layout);
    }

    /// <summary>One of the Arrange step's controls: its name, its value now, the keys that do the same, and the slider.</summary>
    private static (TourMark Mark, FrameworkElement Row) ControlRow(Binder binder, string title, Func<double> get, Action<double> set,
        (double LowerBound, double UpperBound) range, double step, Func<double, string> text, string lessKeys, string moreKeys)
    {
        var mark = new TourMark();
        TextBlock name = Kit.Text(title, 15, Theme.Ink, FontWeights.Medium, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(8, 0, 0, 0);
        TextBlock value = Kit.Text("", Theme.LineSize, Theme.Ink2, wrap: false, digits: true);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.Margin = new Thickness(8, 1, 0, 0);
        binder.Add(() => value.Text = text(get()));
        var head = new DockPanel();
        StackPanel chips = Kit.Across(5, KeysChip(TourCopy.Keys(lessKeys)), KeysChip(TourCopy.Keys(moreKeys)));
        DockPanel.SetDock(chips, Dock.Right);
        head.Children.Add(chips);
        head.Children.Add(Kit.Across(0, mark, name, value));
        var slider = new PxSlider(binder, title, get, set, range.LowerBound, range.UpperBound, step, text) { Margin = new Thickness(0, 2, 0, 0) };
        var row = new StackPanel();
        row.Children.Add(head);
        row.Children.Add(slider);
        return (mark, row);
    }
}

/// <summary>A numbered line. Lit while the picture is showing it; done once the thing has really been done, when the
/// number gives way to a tick.</summary>
public sealed class TourLine : Grid
{
    private readonly Ellipse accent;
    private readonly TextBlock number, words;
    private readonly Polyline tick;
    private bool lit, done, first = true;

    public TourLine(int n, string text)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        circle.Children.Add(new Ellipse { Fill = Theme.GlassHover });
        accent = new Ellipse { Fill = Theme.Accent, Opacity = 0 };
        circle.Children.Add(accent);
        number = Kit.Text(n.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, Theme.Ink2, FontWeights.Medium, wrap: false);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        number.Margin = new Thickness(0, 0, 0, 1);
        circle.Children.Add(number);
        tick = TourWords.Tick(16, Theme.Page);
        tick.Visibility = Visibility.Collapsed;
        tick.HorizontalAlignment = HorizontalAlignment.Center;
        tick.VerticalAlignment = VerticalAlignment.Center;
        circle.Children.Add(tick);
        Children.Add(circle);
        words = TourWords.Para(text, Theme.LineSize, Theme.Ink2, 20);
        words.Margin = new Thickness(0, 1, 0, 0);
        SetColumn(words, 1);
        Children.Add(words);
        AutomationProperties.SetName(this, text);
        Restyle();
    }

    public void SetLit(bool on)
    {
        if (on == lit) return;
        lit = on;
        Restyle();
    }

    public void SetDone(bool on)
    {
        if (on == done) return;
        done = on;
        AutomationProperties.SetItemStatus(this, on ? TourCopy.StateDone : "");
        Restyle();
    }

    private void Restyle()
    {
        bool on = lit || done;
        if (first) accent.Opacity = on ? 1 : 0;
        else Theme.Animate(accent, OpacityProperty, on ? 1 : 0, on ? Look.Enter : Look.Exit);
        first = false;
        number.Foreground = on ? Theme.Page : Theme.Ink2;
        number.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        tick.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        words.Foreground = on ? Theme.Ink : Theme.Ink2;
    }
}

/// <summary>The small bar that turns to the accent beside the thing the picture is showing.</summary>
public sealed class TourMark : Grid
{
    private readonly Border accent;
    private bool lit;

    public TourMark()
    {
        Width = 3;
        Height = 18;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Theme.Rim });
        accent = new Border { CornerRadius = new CornerRadius(1.5), Background = Theme.Accent, Opacity = 0 };
        Children.Add(accent);
    }

    public void SetLit(bool on)
    {
        if (on == lit) return;
        lit = on;
        Theme.Animate(accent, OpacityProperty, on ? 1 : 0, on ? Look.Enter : Look.Exit);
    }
}

/// <summary>A named thing with a line about it and, quieter, what holds for it. Lit while the picture shows it.</summary>
public sealed class TourTopic : Grid
{
    private readonly TourMark mark = new();
    private readonly TextBlock name;

    public TourTopic(string title, string line, string? note)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        mark.Margin = new Thickness(0, 3, 0, 0);
        Children.Add(mark);
        var words = new StackPanel();
        name = Kit.Text(title, Theme.HeadingSize, Theme.B(Look.Ink.Alpha(Look.Ink.A * 0.82)), FontWeights.Medium, wrap: false);
        words.Children.Add(name);
        TextBlock about = TourWords.Para(line, Theme.LineSize, Theme.Ink2, 20);
        about.Margin = new Thickness(0, 4, 0, 0);
        words.Children.Add(about);
        if (note != null)
        {
            TextBlock quiet = TourWords.Para(note, Theme.NoteSize, Theme.Ink3, 18);
            quiet.Margin = new Thickness(0, 6, 0, 0);
            words.Children.Add(quiet);
        }
        SetColumn(words, 1);
        Children.Add(words);
        AutomationProperties.SetName(this, $"{title}. {line} {note}".Trim());
    }

    public void SetLit(bool on)
    {
        mark.SetLit(on);
        name.Foreground = on ? Theme.Ink : Theme.B(Look.Ink.Alpha(Look.Ink.A * 0.82));
    }
}

/// <summary>A button that is only its words (Skip).</summary>
public sealed class TourQuietButton : Press
{
    private readonly TextBlock text;
    private readonly Border ring;

    public TourQuietButton(string title, Action action)
    {
        Label = title;
        Height = 28;
        CornerRadius = new CornerRadius(14);
        text = Kit.Text(title, Theme.ButtonSize, Theme.Ink2, FontWeights.Medium, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(12, 0, 12, 1);
        ring = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Theme.Accent, BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false };
        var g = new Grid();
        g.Children.Add(text);
        g.Children.Add(ring);
        Child = g;
        Click += action;
    }

    protected override void Restyle()
    {
        Background = Hover ? Theme.White(0.06) : Brushes.Transparent;
        text.Foreground = Hover ? Theme.Ink : Theme.Ink2;
        ring.Opacity = Ring ? 1 : 0;
    }
}
