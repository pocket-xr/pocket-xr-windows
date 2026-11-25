using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// Pocket XR's settings window: a sidebar with the eight panes and a live status block, and the chosen pane on the
// right, scrolling when the window is short. Always dark. The panes themselves are in SettingsPanes.cs,
// SettingsSpacePreview.cs, SettingsController.cs and SettingsHands.cs.

/// <summary>The panes of the settings window, in the sidebar's order.</summary>
public enum SettingsPane { Space, Picture, Tracking, Glasses, Hands, Controls, Shortcuts, Help }

public static class SettingsPaneExt
{
    public static readonly SettingsPane[] AllCases =
    {
        SettingsPane.Space, SettingsPane.Picture, SettingsPane.Tracking, SettingsPane.Glasses, SettingsPane.Hands,
        SettingsPane.Controls, SettingsPane.Shortcuts, SettingsPane.Help,
    };

    public static string RawValue(this SettingsPane pane) => pane.ToString().ToLowerInvariant();

    public static string Title(this SettingsPane pane) => pane switch
    {
        SettingsPane.Space => "Space",
        SettingsPane.Picture => "Picture",
        SettingsPane.Tracking => "Head tracking",
        SettingsPane.Glasses => "Glasses",
        SettingsPane.Hands => "Hands",
        SettingsPane.Controls => "Controls",
        SettingsPane.Shortcuts => "Shortcuts",
        _ => "Help",
    };

    /// <summary>What the pane is for, under its title.</summary>
    public static string Subtitle(this SettingsPane pane) => pane switch
    {
        SettingsPane.Space => "Where your screens float, how large and how far.",
        SettingsPane.Picture => "3D, brightness and the light around the screen you look at.",
        SettingsPane.Tracking => "How the screens stay put as you turn your head.",
        SettingsPane.Glasses => "What is plugged in and what Pocket XR can do with it.",
        SettingsPane.Hands => "Point and pinch in the air, seen by your glasses' camera.",
        SettingsPane.Controls => "A game controller as a remote for the Space, and Pocket mode.",
        SettingsPane.Shortcuts => "Change the Space from the keyboard, in any app.",
        _ => "The tour, logs, your settings as a file, and the app itself.",
    };

    /// <summary>A glyph of Windows' icon face for the sidebar.</summary>
    public static string Symbol(this SettingsPane pane) => pane switch
    {
        SettingsPane.Space => Glyph.Space,
        SettingsPane.Picture => SettingsGlyph.Brightness,
        SettingsPane.Tracking => Glyph.Tracking,
        SettingsPane.Glasses => Glyph.Glasses,
        SettingsPane.Hands => Glyph.Hand,
        SettingsPane.Controls => Glyph.Controller,
        SettingsPane.Shortcuts => Glyph.Keyboard,
        _ => Glyph.Help,
    };
}

/// <summary>Glyphs of Windows' icon face the settings window uses that Glyph does not name.</summary>
public static class SettingsGlyph
{
    public const string Brightness = "", Edit = "", Document = "", Send = "", Info = "",
        SaveFile = "", OpenFile = "";
}

/// <summary>Which pane is showing.</summary>
public sealed class SettingsNavigation
{
    public SettingsPane Pane { get; private set; }
    public event Action? Changed;

    public SettingsNavigation(SettingsPane pane = SettingsPane.Space) { Pane = pane; }

    public void Go(SettingsPane pane)
    {
        if (pane == Pane) return;
        Pane = pane;
        Changed?.Invoke();
    }
}

/// <summary>States a click leads to, which a picture can ask for directly.</summary>
public sealed class SettingsShow
{
    /// <summary>The Help pane is asking "Reset everything?".</summary>
    public bool ConfirmReset;
    /// <summary>The saved arrangement whose name is being typed.</summary>
    public Guid? Renaming;
}

public static class SettingsWindow
{
    public const double DefaultWidth = 880, DefaultHeight = 620, SmallestWidth = 760, SmallestHeight = 540;
    /// <summary>While pictures are drawn: how high the window being drawn would be.</summary>
    private static double PictureHeight = DefaultHeight;

    public const double SidebarWidth = 200, PaneWidth = 680, PaneSide = 28, CardGap = 20;

    /// <summary>The window, kept in step with the settings and the app's state. It opens on the Space pane.</summary>
    public static PxWindow Window(SettingsStore store, AppState state, IAppActions actions) =>
        Window(store, state, actions, new SettingsNavigation());

    /// <summary>The same, with the navigation in the caller's hands (to open the window on a pane of its choice).</summary>
    public static PxWindow Window(SettingsStore store, AppState state, IAppActions actions, SettingsNavigation navigation)
    {
        var binder = new Binder();
        FrameworkElement root = Root(store, state, actions, binder, navigation);
        var w = new PxWindow("Pocket XR", root, null, resizable: true)
        {
            Width = DefaultWidth, Height = DefaultHeight, MinWidth = SmallestWidth, MinHeight = SmallestHeight,
        };
        store.Changed += (_, _) => binder.Sync();
        state.Changed += binder.Sync;
        return w;
    }

    /// <summary>Everything under the strip: the sidebar, a hairline, the pane. The sidebar's colour runs up behind the
    /// strip, so the side is one piece from the window's top to its foot.</summary>
    public static FrameworkElement Root(SettingsStore store, AppState state, IAppActions actions, Binder binder,
        SettingsNavigation navigation, SettingsShow? show = null)
    {
        show ??= new SettingsShow();
        var root = new Grid { Background = Theme.Page };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(Sidebar(state, binder, navigation));
        var rim = new Border { Background = Theme.Rim, Margin = new Thickness(0, -PxWindow.Strip, 0, 0), SnapsToDevicePixels = true };
        Grid.SetColumn(rim, 1);
        root.Children.Add(rim);

        // The chosen pane. A new pane is built afresh, so its cards arrive afresh and it starts at its top.
        var host = new Border { Background = Theme.Page };
        Grid.SetColumn(host, 2);
        root.Children.Add(host);
        var paneBinder = new Binder();
        void ShowPane()
        {
            paneBinder = new Binder();
            SettingsPane pane = navigation.Pane;
            host.Child = Scaffold(pane, SettingsPanes.Build(pane, store, state, actions, paneBinder, show));
        }
        ShowPane();
        navigation.Changed += ShowPane;
        binder.Add(() => paneBinder.Sync());
        return root;
    }

    // MARK: the sidebar

    /// <summary>The left side: the app's name, the panes, and what is happening now.</summary>
    private static FrameworkElement Sidebar(AppState state, Binder binder, SettingsNavigation navigation)
    {
        var name = new StackPanel { Margin = new Thickness(10, 0, 10, 18) };
        name.Children.Add(Kit.Text("Pocket XR", 17, Theme.Ink, FontWeights.Medium, wrap: false));
        TextBlock sub = Kit.Text("for Windows", Theme.NoteSize, Theme.Ink3, wrap: false);
        sub.Margin = new Thickness(0, 1, 0, 0);
        name.Children.Add(sub);

        FrameworkElement status = StatusBlock(state, binder);
        FrameworkElement list = PaneList(navigation);
        list.VerticalAlignment = VerticalAlignment.Top;

        var column = new DockPanel { Margin = new Thickness(10, 18, 10, 12) };
        DockPanel.SetDock(name, Dock.Top);
        column.Children.Add(name);
        DockPanel.SetDock(status, Dock.Bottom);
        column.Children.Add(status);
        column.Children.Add(list);
        if (Theme.Static)
        {
            // A picture is as tall as its pane; the status block stays where the window's foot would be.
            column.Height = PictureHeight - 30;
            column.VerticalAlignment = VerticalAlignment.Top;
        }
        return new Border { Background = Theme.Pad, Margin = new Thickness(0, -PxWindow.Strip, 0, 0), Child = column };
    }

    private const double PaneRow = 32, PaneRowGap = 2;

    /// <summary>The panes, one under another, with a highlight that slides to the chosen one.</summary>
    private static FrameworkElement PaneList(SettingsNavigation navigation)
    {
        SettingsPane[] all = SettingsPaneExt.AllCases;
        var slide = new TranslateTransform(0, Array.IndexOf(all, navigation.Pane) * (PaneRow + PaneRowGap));
        var highlight = new Border
        {
            Height = PaneRow, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(Theme.ControlRadius),
            Background = Theme.B(Look.Accent.Alpha(0.20)), BorderBrush = Theme.B(Look.Accent.Alpha(0.45)),
            BorderThickness = new Thickness(1), RenderTransform = slide, IsHitTestVisible = false,
        };
        var rows = new StackPanel();
        AutomationProperties.SetName(rows, "Panes");
        var made = new List<PaneRowButton>();
        for (int i = 0; i < all.Length; i++)
        {
            int index = i;
            var row = new PaneRowButton(all[i], () => navigation.Pane == all[index]) { Margin = new Thickness(0, 0, 0, PaneRowGap) };
            row.Click += () => navigation.Go(all[index]);
            // Up and down walk the list, as in any list of pages.
            row.PreviewKeyDown += (_, e) =>
            {
                int to = e.Key == Key.Down ? index + 1 : e.Key == Key.Up ? index - 1 : -1;
                if (e.Key is not (Key.Down or Key.Up)) return;
                e.Handled = true;
                if (to < 0 || to >= all.Length) return;
                navigation.Go(all[to]);
                made[to].Focus();
            };
            made.Add(row);
            rows.Children.Add(row);
        }
        navigation.Changed += () =>
        {
            Theme.Animate(slide, TranslateTransform.YProperty, Array.IndexOf(all, navigation.Pane) * (PaneRow + PaneRowGap));
            foreach (PaneRowButton row in made) row.Refresh();
        };
        var grid = new Grid();
        grid.Children.Add(highlight);
        grid.Children.Add(rows);
        return grid;
    }

    private sealed class PaneRowButton : Press
    {
        private readonly Func<bool> selected;
        private readonly TextBlock icon, text;
        private readonly Border hover, ring;

        public PaneRowButton(SettingsPane pane, Func<bool> selected)
        {
            this.selected = selected;
            Label = pane.Title();
            Kind = System.Windows.Automation.Peers.AutomationControlType.TabItem;
            Toggled = selected;
            DimOnPress = false;
            Height = PaneRow;
            icon = Kit.Icon(pane.Symbol(), 14, Theme.Ink2);
            icon.Width = 20;
            icon.TextAlignment = TextAlignment.Center;
            text = Kit.Text(pane.Title(), 14, Theme.Ink2, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(8, 0, 0, 1);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
            line.Children.Add(icon);
            line.Children.Add(text);
            hover = new Border { CornerRadius = new CornerRadius(Theme.ControlRadius), Background = Theme.GlassHover, Opacity = 0 };
            ring = new Border { CornerRadius = new CornerRadius(Theme.ControlRadius), BorderBrush = Theme.Accent, BorderThickness = new Thickness(2), Opacity = 0 };
            var g = new Grid();
            g.Children.Add(hover);
            g.Children.Add(line);
            g.Children.Add(ring);
            Child = g;
            Restyle();
        }

        protected override void Restyle()
        {
            bool on = selected();
            hover.Opacity = Hover && !on ? 0.7 : 0;
            ring.Opacity = Ring ? 1 : 0;
            icon.Foreground = on ? Theme.Held : Theme.Ink2;
            text.Foreground = on ? Theme.Ink : Theme.Ink2;
            text.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
        }
    }

    /// <summary>What is true now, small, at the foot of the sidebar: the glasses, a dot that lights while the head is
    /// tracked, frames a second, and whether the picture is 3D or Steady.</summary>
    private static FrameworkElement StatusBlock(AppState state, Binder binder)
    {
        Grid dot = Kit.Dot(false);
        dot.VerticalAlignment = VerticalAlignment.Center;
        TextBlock name = Kit.Text("", 13, Theme.Ink, FontWeights.Medium, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(6, 0, 0, 1);
        var top = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        top.Children.Add(dot);
        top.Children.Add(name);

        TextBlock plug = Kit.Text("Plug in your glasses.", Theme.ChipSize, Theme.Ink3);
        plug.Margin = new Thickness(0, 8, 0, 0);
        TextBlock fps = Kit.Text("", 11, Theme.Ink2, wrap: false, digits: true);
        fps.VerticalAlignment = VerticalAlignment.Center;
        fps.MinWidth = 40;
        Border depth = Kit.Chip("3D"), steady = Kit.Chip("Steady");
        StackPanel now = Kit.Across(5, fps, depth, steady);
        now.Margin = new Thickness(0, 8, 0, 0);

        var block = new StackPanel();
        block.Children.Add(top);
        block.Children.Add(plug);
        block.Children.Add(now);
        var card = new Border
        {
            Child = block, Padding = new Thickness(10), CornerRadius = new CornerRadius(Theme.ControlRadius + 2),
            Background = Theme.Card, BorderBrush = Theme.Rim, BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(card, "Status");
        binder.Add(() =>
        {
            bool glasses = state.GlassesName != null;
            Kit.SetDot(dot, state.TrackingOn);
            name.Text = state.GlassesName ?? "No glasses";
            name.Foreground = glasses ? Theme.Ink : Theme.Ink2;
            plug.Visibility = glasses ? Visibility.Collapsed : Visibility.Visible;
            now.Visibility = glasses ? Visibility.Visible : Visibility.Collapsed;
            fps.Text = $"{(int)Math.Round(state.Fps)} fps";
            Kit.SetChip(depth, state.Stereo);
            Kit.SetChip(steady, state.Steady);
            var parts = new List<string> { state.GlassesName ?? "No glasses", state.TrackingOn ? "head tracking on" : "head tracking off" };
            if (glasses) parts.Add(fps.Text);
            if (state.Stereo) parts.Add("3D");
            if (state.Steady) parts.Add("Steady mode");
            AutomationProperties.SetHelpText(card, string.Join(", ", parts));
        });
        return card;
    }

    // MARK: a pane's frame

    /// <summary>A pane's frame: its large title, a line under it, then its cards, scrolling when the window is short.</summary>
    private static FrameworkElement Scaffold(SettingsPane pane, IEnumerable<FrameworkElement> cards)
    {
        TextBlock title = Kit.Title(pane.Title());
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        TextBlock subtitle = Kit.Text(pane.Subtitle(), Theme.LineSize, Theme.Ink2);
        subtitle.Margin = new Thickness(0, 4, 0, 0);
        var header = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        header.Children.Add(title);
        header.Children.Add(subtitle);

        var column = new StackPanel();
        column.Children.Add(Theme.Arrive(header, 0));
        int order = 1;
        foreach (FrameworkElement card in cards)
        {
            card.Margin = new Thickness(0, CardGap, 0, 0);
            column.Children.Add(Theme.Arrive(card, order));
            order += 1;
        }

        // The column fills the pane up to 680 wide and keeps to the left beyond that.
        var fit = new Grid { Margin = new Thickness(PaneSide, 12, PaneSide, PaneSide) };
        fit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = PaneWidth });
        fit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) });
        fit.Children.Add(column);
        if (Theme.Static) return fit;
        return new ScrollViewer
        {
            Content = fit, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
    }

    // MARK: pictures

    /// <summary>
    /// Every pane, and the states of a pane worth looking at, drawn to settings-N-PANE.png in [folder] with made-up
    /// but possible states. A pane taller than the window is drawn whole, so nothing hides below a scroll. The
    /// pictures show the look and the words; they prove nothing about the glasses.
    /// </summary>
    public static void Pictures(string folder)
    {
        void One(string name, SettingsPane pane, SettingsStore store, AppState state, SettingsShow? show = null,
            double width = DefaultWidth, double height = DefaultHeight)
        {
            var binder = new Binder();
            PictureHeight = height;
            FrameworkElement root = Root(store, state, new NoActions(), binder, new SettingsNavigation(pane), show);
            root.MinHeight = height - PxWindow.Strip;
            UiRender.WindowPng(Path.Combine(folder, name), "Pocket XR", root, width);
        }

        // An XREAL Aura plugged in: two screens on a curve, two saved arrangements, the first in use.
        var store = new SettingsStore(persist: false);
        store.Update(s =>
        {
            s.Curve = 1.0;
            s.Presets = new List<SpacePreset>
            {
                new(name: "Work", layout: ArrangeLayout.Row, widthDeg: 40, distance: 1.4, curve: 1.0),
                new(name: "Film", layout: ArrangeLayout.Theater, widthDeg: 70, distance: 4.0, curve: 0),
            };
        });
        AppState aura = Sample(store, glasses: true, screens: 2);

        // Nothing plugged in, no virtual display driver, the screen count just changed.
        var bare = new SettingsStore(persist: false);
        bare.Update(s => s.Screens = 3);
        AppState none = Sample(bare, glasses: false, screens: 0, driver: false);
        none.NeedsRestart = true;

        One("settings-1-space.png", SettingsPane.Space, store, aura);
        One("settings-2-space-no-driver.png", SettingsPane.Space, bare, none);
        One("settings-3-space-renaming.png", SettingsPane.Space, store, aura, new SettingsShow { Renaming = store.S.Presets[1].Id });
        // Six screens (0.4.11): the picker, a switch for each to turn it portrait, the main display among six.
        var six = new SettingsStore(persist: false);
        six.Update(s =>
        {
            s.Screens = 6;
            s.Layout = ArrangeLayout.MainSide;
            s.PortraitScreens = 1 << 5;
            s.MainScreen = 4;
        });
        One("settings-3b-space-six-screens.png", SettingsPane.Space, six, Sample(six, glasses: true, screens: 6));
        One("settings-4-picture.png", SettingsPane.Picture, store, aura);
        One("settings-5-tracking.png", SettingsPane.Tracking, store, aura);
        AppState learning = Sample(store, glasses: true, screens: 2);
        learning.BiasReady = false;
        learning.YawDeg = -31;
        learning.PitchDeg = 14;
        One("settings-6-tracking-learning.png", SettingsPane.Tracking, store, learning);
        One("settings-7-glasses.png", SettingsPane.Glasses, store, aura);
        One("settings-8-glasses-none.png", SettingsPane.Glasses, bare, none);

        // Hands as the Windows version has them now (not built yet), and as the pane reads once they run with a
        // camera that faces the person.
        One("settings-9-hands.png", SettingsPane.Hands, store, aura);
        AppState air = Sample(store, glasses: true, screens: 2);
        air.HandsAvailable = true;
        air.HandsRunning = true;
        air.HandsSeen = 1;
        air.HandsStatus = HandLines.Watching;
        air.HandsCameras = new[] { "Integrated Camera", "Logitech BRIO" };
        air.HandsCamera = 0;
        air.HandsFacing = true;
        One("settings-10-hands-air-trackpad.png", SettingsPane.Hands, store, air);

        AppState pad = Sample(store, glasses: true, screens: 2);
        pad.ControllerAvailable = true;
        pad.ControllerName = "Xbox Wireless Controller";
        One("settings-11-controls.png", SettingsPane.Controls, store, pad);
        var always = new SettingsStore(persist: false);
        always.Update(s =>
        {
            s.ControllerMode = ControllerMode.Always;
            s.Shortcuts = false;
        });
        One("settings-12-controls-now.png", SettingsPane.Controls, always, Sample(always, glasses: false, screens: 0));
        One("settings-13-shortcuts.png", SettingsPane.Shortcuts, store, aura);
        // As on the Steam Deck, 17 Nov 2026: two shortcuts' keys held by other apps.
        AppState taken = Sample(store, glasses: true, screens: 2);
        taken.ShortcutsTaken = new HashSet<string> { "recentre", "launcher" };
        One("settings-13b-shortcuts-taken.png", SettingsPane.Shortcuts, store, taken, null, DefaultWidth, 1400);
        One("settings-14-help.png", SettingsPane.Help, store, aura);
        One("settings-15-help-reset.png", SettingsPane.Help, store, aura, new SettingsShow { ConfirmReset = true });

        // The window at its smallest, where rows are tightest.
        One("settings-16-space-smallest.png", SettingsPane.Space, store, aura, null, SmallestWidth, SmallestHeight);
        One("settings-17-controls-smallest.png", SettingsPane.Controls, store, pad, null, SmallestWidth, SmallestHeight);
        One("settings-18-glasses-smallest.png", SettingsPane.Glasses, bare, none, null, SmallestWidth, SmallestHeight);

        // The hand aim's calibration, as it covers a floating screen: the third of its five dots.
        var calibration = new HandCalibrationView(2, () => { }) { Width = 960, Height = 540 };
        UiRender.Png(Path.Combine(folder, "settings-19-hand-calibration.png"), calibration);
    }

    /// <summary>A made-up state for the pictures: the control panel's, with the head a little to the left.</summary>
    private static AppState Sample(SettingsStore store, bool glasses, int screens, bool driver = true)
    {
        AppState state = global::PocketXR.Windows.Pictures.State(store, glasses, screens, driver: driver, yaw: 12, pitch: -4);
        if (glasses) state.StatusLine = "XREAL Aura · head tracking on · 3D";
        return state;
    }
}

/// <summary>One row of a card whose words can change: what it is on the left (with an optional line under it and an
/// optional mark before it), its control on the right. Kit.Row with the text within reach.</summary>
public sealed class SettingsRow : Grid
{
    public readonly TextBlock Title, Note;

    public SettingsRow(string title, string? note = null, UIElement? trailing = null, UIElement? leading = null)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Title = Kit.Line(title);
        Note = Kit.Small("");
        Note.Margin = new Thickness(0, 2, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Title);
        words.Children.Add(Note);
        Grid.SetColumn(words, 1);
        Children.Add(words);
        if (leading is FrameworkElement lead)
        {
            lead.VerticalAlignment = VerticalAlignment.Center;
            lead.Margin = new Thickness(0, 0, 12, 0);
            Children.Add(lead);
        }
        if (trailing is FrameworkElement trail)
        {
            trail.VerticalAlignment = VerticalAlignment.Center;
            trail.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(trail, 2);
            Children.Add(trail);
        }
        SetNote(note);
    }

    public void SetNote(string? note, bool warn = false)
    {
        bool has = !string.IsNullOrEmpty(note);
        Note.Text = note ?? "";
        Note.Foreground = warn ? Theme.Warn : Theme.Ink2;
        Note.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        double pad = has ? 10 : 6;
        Margin = new Thickness(Theme.Inset, pad, Theme.Inset, pad);
        MinHeight = Theme.RowHeight - 2 * pad;
    }
}

/// <summary>Small builders the panes share.</summary>
public static class SettingsParts
{
    /// <summary>A row with a switch that can be off limits.</summary>
    public static SettingsRow ToggleRow(Binder binder, string title, string? note, Func<bool> get, Action<bool> set,
        Func<bool>? enabled = null)
    {
        var toggle = new Toggle(binder, title, get, set);
        var row = new SettingsRow(title, note, toggle);
        if (enabled != null) binder.Add(() => toggle.IsEnabled = enabled());
        return row;
    }

    public static Grid SliderRow(Binder binder, string title, Func<double> get, Action<double> set,
        (double LowerBound, double UpperBound) range, double step, Func<double, string> text) =>
        Kit.SliderRow(binder, title, get, set, range.LowerBound, range.UpperBound, step, text);

    /// <summary>Shows or hides rows together (a row and the hairline above it).</summary>
    public static void Show(bool on, params UIElement[] rows)
    {
        foreach (UIElement row in rows) row.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A small heading inside a card, above a group of rows.</summary>
    public static TextBlock Caption(string text)
    {
        TextBlock t = Kit.Line(text);
        t.Margin = new Thickness(Theme.Inset, 12, Theme.Inset, 4);
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level3);
        return t;
    }

    /// <summary>
    /// A card whose rows depend on the state: they are made again (with a binder of their own) whenever [key] gives
    /// something new, and only shown again otherwise.
    /// </summary>
    public static StackPanel LiveCard<TKey>(Binder binder, string? title, Func<TKey> key, Func<Binder, IEnumerable<UIElement?>> rows)
    {
        StackPanel outer = Kit.Card(title);
        var inner = (StackPanel)((Border)outer.Children[outer.Children.Count - 1]).Child;
        var own = new Binder();
        bool built = false;
        TKey last = default!;
        binder.Add(() =>
        {
            TKey now = key();
            if (built && EqualityComparer<TKey>.Default.Equals(now, last))
            {
                own.Sync();
                return;
            }
            built = true;
            last = now;
            own = new Binder();
            inner.Children.Clear();
            foreach (UIElement? row in rows(own))
                if (row != null) inner.Children.Add(row);
        });
        return outer;
    }

    /// <summary>One calm line at the top of a pane, on a quiet plate: something the person should know first.</summary>
    public static Border Notice(string text)
    {
        TextBlock icon = Kit.Icon(SettingsGlyph.Info, 14, Theme.Held);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 10, 0);
        TextBlock words = Kit.Text(text, Theme.LineSize, Theme.Ink);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        return new Border
        {
            Child = row, Padding = new Thickness(Theme.Inset, 12, Theme.Inset, 13), CornerRadius = new CornerRadius(Theme.CardRadius),
            Background = Theme.B(Look.Accent.Alpha(0.10)), BorderBrush = Theme.B(Look.Accent.Alpha(0.35)),
            BorderThickness = new Thickness(1), SnapsToDevicePixels = true,
        };
    }

    /// <summary>Why floating screens cannot be made yet, in the control panel's words.</summary>
    public static string DriverWords(AppState state) =>
        "Floating screens need a virtual display driver, which Windows asks an administrator to install once."
        + (state.DriverWhy.Length > 0 ? " Now: " + state.DriverWhy + "." : "");

    public static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
