using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// The menu under Pocket XR's icon in the notification area (a right click; a left click opens the control panel).
/// The same things as the Mac's menu bar menu, in Pocket XR's look. Layout, Tracking and Screens open in place.
/// </summary>
public static class TrayMenu
{
    public const double Width = 300;

    /// <summary>Shows the menu by the icon, at a screen position in pixels. It closes when something is picked or
    /// anything else is clicked.</summary>
    public static Window Show(SettingsStore store, AppState state, IAppActions actions, int x, int y)
    {
        Window? window = null;
        var binder = new Binder();
        FrameworkElement content = Content(store, state, actions, binder, () => window?.Close());
        window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Content = content, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            UseLayoutRounding = true, Title = "Pocket XR menu",
        };
        Action sync = binder.Sync;
        Action<SpaceSettings, SpaceSettings> storeSync = (_, _) => binder.Sync();
        state.Changed += sync;
        store.Changed += storeSync;
        window.Closed += (_, _) =>
        {
            state.Changed -= sync;
            store.Changed -= storeSync;
        };
        bool closing = false;
        window.Closing += (_, _) => closing = true;
        window.Deactivated += (_, _) => { if (!closing) window.Close(); };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        window.SourceInitialized += (_, _) => Place(window, x, y);
        window.SizeChanged += (_, _) => Place(window, x, y);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Above and to the left of the click, inside that monitor's work area.</summary>
    private static void Place(Window window, int x, int y)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int w = (int)Math.Ceiling(window.ActualWidth * scale), h = (int)Math.Ceiling(window.ActualHeight * scale);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(new NativePoint { X = x, Y = y }, 2), ref info);
        int left = Math.Clamp(x - w + 12, info.work.Left + 8, Math.Max(info.work.Left + 8, info.work.Right - w - 8));
        int top = Math.Clamp(y - h - 8, info.work.Top + 8, Math.Max(info.work.Top + 8, info.work.Bottom - h - 8));
        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010); // no size, no z-order, no activate
    }

    public static FrameworkElement Content(SettingsStore store, AppState state, IAppActions actions, Binder binder, Action close)
    {
        var list = new StackPanel { Width = Width - 14 };
        string Hint(string id) => store.S.Shortcuts ? Shortcut.Find(id).Keys : "";
        void Add(UIElement e) => list.Children.Add(e);
        void Rule() => Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(6, 5, 6, 5) });
        Item Do(string title, Action action, string keys = "", Func<bool>? on = null, Func<bool>? enabled = null, bool stay = false)
        {
            var item = new Item(title, keys, () =>
            {
                action();
                if (!stay) close();
            });
            binder.Add(() =>
            {
                item.SetOn(on?.Invoke());
                item.IsEnabled = enabled?.Invoke() ?? true;
            });
            Add(item);
            return item;
        }

        TextBlock status = Kit.Small("");
        status.Margin = new Thickness(10, 6, 10, 8);
        Add(status);
        binder.Add(() => status.Text = state.StatusLine);
        Rule();

        Do("Open the control panel", actions.OpenControlPanel);
        Do("Launcher", actions.ToggleLauncher, Hint("launcher"), () => state.LauncherOpen, () => state.VirtualScreens > 0);
        Do("Pocket mode", () => actions.SetPocketMode(!state.PocketMode), Hint("pocket"), () => state.PocketMode, () => state.VirtualScreens > 0);
        Do("Hand tracking", () => actions.SetHands(!state.HandsRunning), Hint("hands"), () => state.HandsRunning);
        if (PcScreens.PcScreenLauncher.Available)
        {
            Do(PcScreens.PcScreenLauncher.Title, actions.OpenPcScreen);
            TextBlock pcNote = Kit.Small(PcScreens.PcScreenLauncher.Note);
            pcNote.Margin = new Thickness(26, 0, 10, 6);
            Add(pcNote);
        }
        Do("Let my phone stream this PC", () => actions.SetPhoneStreams(!store.S.PhoneStreamsPc), "", () => store.S.PhoneStreamsPc);
        Do("Forget phones", actions.ForgetPhones, "", null, () => store.S.PhoneStreamsPc);
        Do("Set up Apollo for PC screens…", actions.SetUpApollo);
        Rule();
        Do("Recentre", actions.Recentre, Hint("recentre"));
        Do("Steady mode", () => actions.SetSteady(!state.Steady), Hint("steady"), () => state.Steady, () => state.SteadyAvailable && state.LinkOpen);
        Do("3D side by side", () => store.Update(s => s.Want3D = !s.Want3D), Hint("depth"), () => store.S.Want3D);
        Rule();

        // Layout, Tracking and Screens open in place: one is open at a time.
        var groups = new List<(Item Head, StackPanel Body)>();
        StackPanel Group(string title, Func<string> now)
        {
            var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 0, 2) };
            Item? head = null;
            head = new Item(title, "", () =>
            {
                bool open = body.Visibility != Visibility.Visible;
                foreach (var g in groups)
                {
                    g.Body.Visibility = Visibility.Collapsed;
                    g.Head.SetOpen(false);
                }
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                head!.SetOpen(open);
            }, group: true);
            binder.Add(() => head.SetValue(now()));
            groups.Add((head, body));
            Add(head);
            Add(body);
            return body;
        }
        void In(StackPanel body, string title, Action action, Func<bool> on)
        {
            var item = new Item(title, "", action);
            binder.Add(() => item.SetOn(on()));
            body.Children.Add(item);
        }

        int Count() => Math.Max(state.VirtualScreens, store.S.Screens);
        StackPanel layout = Group("Layout", () => SpaceLayout.Title(store.S.Layout, Count()));
        (bool Steady, int Count)? builtFor = null;
        var layoutBinder = new Binder();
        binder.Add(() =>
        {
            var key = (state.Steady, Count());
            if (builtFor != key)
            {
                builtFor = key;
                layout.Children.Clear();
                layoutBinder = new Binder();
                foreach (ArrangeLayout l in SpaceLayout.Choices(key.Item2))
                {
                    ArrangeLayout choice = l;
                    var item = new Item(SpaceLayout.Title(l, key.Item2), "", () => store.Update(s => s.Layout = choice));
                    layoutBinder.Add(() => item.SetOn(store.S.Layout == choice));
                    layout.Children.Add(item);
                }
            }
            layoutBinder.Sync();
        });
        Do("Bigger", () => actions.Adjust(SpaceAdjust.Bigger), Hint("bigger"), stay: true);
        Do("Smaller", () => actions.Adjust(SpaceAdjust.Smaller), Hint("smaller"), stay: true);
        Do("Nearer", () => actions.Adjust(SpaceAdjust.Nearer), Hint("nearer"), stay: true);
        Do("Further", () => actions.Adjust(SpaceAdjust.Further), Hint("further"), stay: true);
        Do("More curve", () => actions.Adjust(SpaceAdjust.MoreCurve), Hint("moreCurve"), stay: true);
        Do("Less curve", () => actions.Adjust(SpaceAdjust.LessCurve), Hint("lessCurve"), stay: true);

        StackPanel tracking = Group("Tracking", () => !store.S.HeadTracking ? "Off" : store.S.TrackingMode.Title());
        foreach (TrackingMode m in TrackingModeExt.AllCases)
        {
            TrackingMode mode = m;
            In(tracking, m.Title(), () => store.Update(s =>
            {
                s.HeadTracking = true;
                s.TrackingMode = mode;
            }), () => store.S.HeadTracking && store.S.TrackingMode == mode);
        }
        In(tracking, "Head tracking", () => store.Update(s => s.HeadTracking = !s.HeadTracking), () => store.S.HeadTracking);

        StackPanel screens = Group("Screens", () => store.S.Screens.ToString());
        for (int n = SpaceSettings.ScreensRange.LowerBound; n <= ScreenCount.Offered(state.DriverLimit); n++)
        {
            int count = n;
            In(screens, n.ToString(), () => store.Update(s => s.Screens = count), () => store.S.Screens == count);
        }
        Rule();
        Do("Settings…", actions.OpenSettings);
        Do("Tour of Pocket XR", actions.OpenTour);
        Do("Send logs to Mike", actions.SendLogs);
        Rule();
        Do("Quit and put the glasses back", actions.Quit);

        return new Border
        {
            Child = list, Padding = new Thickness(6), Margin = new Thickness(1), Background = Theme.Card,
            BorderBrush = Theme.RimFocus, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Theme.CardRadius),
        };
    }

    /// <summary>One line of the menu: a tick when it is on, its name, its shortcut or its value at the right.</summary>
    private sealed class Item : Press
    {
        private readonly TextBlock tick, title, right, chevron;

        public Item(string text, string keys, Action action, bool group = false)
        {
            Label = text;
            Kind = AutomationControlType.MenuItem;
            Height = 30;
            CornerRadius = new CornerRadius(7);
            DimOnPress = false;
            tick = Kit.Icon(Glyph.Check, 10, Theme.Accent);
            tick.Width = 18;
            tick.Visibility = Visibility.Hidden;
            title = Kit.Text(text, Theme.LineSize, Theme.Ink, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            right = Kit.Text(keys, 12, Theme.Ink3, wrap: false);
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(12, 0, 0, 0);
            chevron = Kit.Icon(Glyph.ChevronDown, 8, Theme.Ink3);
            chevron.Margin = new Thickness(8, 1, 0, 0);
            chevron.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
            var row = new DockPanel { Margin = new Thickness(4, 0, 10, 1) };
            DockPanel.SetDock(tick, Dock.Left);
            DockPanel.SetDock(chevron, Dock.Right);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(tick);
            row.Children.Add(chevron);
            row.Children.Add(right);
            row.Children.Add(title);
            Child = row;
            Click += action;
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        }

        public void SetOn(bool? on) => tick.Visibility = on == true ? Visibility.Visible : Visibility.Hidden;
        public void SetValue(string value) => right.Text = value;
        public void SetOpen(bool open) => chevron.RenderTransform = open ? new RotateTransform(180, 4, 4) : null;

        protected override void Restyle() => Background = Hover || Ring ? Theme.GlassHover : Brushes.Transparent;
    }

    public static void Pictures(string folder)
    {
        var store = new SettingsStore(persist: false);
        AppState state = Windows.Pictures.State(store, glasses: true, screens: 2);
        UiRender.Png(Path.Combine(folder, "tray-menu-1.png"), Content(store, state, new NoActions(), new Binder(), () => { }));
        AppState none = Windows.Pictures.State(store, glasses: false, screens: 0);
        UiRender.Png(Path.Combine(folder, "tray-menu-2-no-glasses.png"), Content(store, none, new NoActions(), new Binder(), () => { }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect monitor, work;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint p, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
