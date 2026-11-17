using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The launcher: the PC's apps on a sheet in the glasses. It is an ordinary borderless window that fills a floating
// screen (a virtual display), so it is captured and drawn in the glasses like any other window. From the top: the
// time, the date, the battery and Wi-Fi; a row of round buttons; a search that takes typing at once; the pinned
// apps; pages of every app. Its looks are in LauncherViews.cs, what it reads from the PC in
// Platform/LauncherApps.cs, and its logic that needs no screen in the core's LauncherLogic.cs.

/// <summary>The round buttons, in the order shown.</summary>
public enum LauncherButton { Recentre, Steady, Depth, Pocket, Hands, ControlPanel, Settings }

public static class LauncherButtons
{
    public static readonly LauncherButton[] All =
    {
        LauncherButton.Recentre, LauncherButton.Steady, LauncherButton.Depth, LauncherButton.Pocket, LauncherButton.Hands,
        LauncherButton.ControlPanel, LauncherButton.Settings,
    };

    public static string Title(this LauncherButton b) => b switch
    {
        LauncherButton.Recentre => "Recentre",
        LauncherButton.Steady => "Steady",
        LauncherButton.Depth => "3D",
        LauncherButton.Pocket => "Pocket mode",
        LauncherButton.Hands => "Hands",
        LauncherButton.ControlPanel => "Control panel",
        _ => "Settings",
    };

    /// <summary>A glyph of Windows' icon face; 3D is drawn as its two characters instead.</summary>
    public static string Glyph(this LauncherButton b) => b switch
    {
        LauncherButton.Recentre => Ui.Glyph.Recentre,
        LauncherButton.Steady => Ui.Glyph.Lock,
        LauncherButton.Depth => "",
        LauncherButton.Pocket => LauncherGlyph.Moon,
        LauncherButton.Hands => Ui.Glyph.Hand,
        LauncherButton.ControlPanel => LauncherGlyph.Sliders,
        _ => Ui.Glyph.Settings,
    };

    /// <summary>A switch stays lit while it is on, and pressing it leaves the launcher open.</summary>
    public static bool StaysOpen(this LauncherButton b) =>
        b is LauncherButton.Steady or LauncherButton.Depth or LauncherButton.Hands;
}

/// <summary>Glyphs of Windows' icon face that only the launcher uses.</summary>
public static class LauncherGlyph
{
    public const string Moon = "", Sliders = "", Wifi = "", Bolt = "";
}

/// <summary>The small menu a right click on an app opens.</summary>
public sealed record LauncherMenu(
    LauncherApp App,
    LauncherSpot Spot,
    /// <summary>Where it was asked for, in the launcher window's points from its top left.</summary>
    Point At,
    bool Pinned,
    /// <summary>The row the keyboard is on, -1 for none (the menu was opened with the mouse).</summary>
    int Row,
    /// <summary>A shortcut is a file File Explorer can show; a Store app is not.</summary>
    bool HasFile)
{
    public int Rows => HasFile ? 2 : 1;
}

/// <summary>Why the launcher closes: whether the app that was in front before it gets the keyboard back.</summary>
public enum LauncherClose
{
    /// <summary>Escape, a click beside the panel, the shortcut, Recentre: back to the app that was in front.</summary>
    Dismissed,
    /// <summary>An app was opened, another window of Pocket XR was asked for, or the user clicked elsewhere: leave it be.</summary>
    HandedOver,
}

/// <summary>Everything the launcher's views show, and what the keys and the mouse do to it.</summary>
public sealed class LauncherModel
{
    public readonly SettingsStore Store;
    public readonly AppState State;
    public readonly IAppActions Actions;
    /// <summary>A fixed list of made-up apps with drawn icons, for pictures drawn to files.</summary>
    public readonly bool Samples;
    public readonly LauncherWorker? Worker;
    public readonly LauncherIcons? Icons;
    public Action<LauncherClose> OnClose = _ => { };
    /// <summary>The floating screen the launcher is on: where an app it starts is asked to open.</summary>
    public DisplayInfo? Screen;
    /// <summary>Where an app's cell is in the window (its middle), for a menu opened from the keyboard. Set by the view.</summary>
    public Func<LauncherSpot, Point?> WhereIs = _ => null;

    /// <summary>Something shown changed. Shape counts the changes that move apps about (the cells are made again).</summary>
    public event Action? Changed;
    public int Shape { get; private set; }

    public IReadOnlyList<LauncherApp> Apps { get; private set; } = Array.Empty<LauncherApp>();
    /// <summary>The apps have been read once (before that the grid says so instead of looking empty).</summary>
    public bool Scanned { get; private set; }
    public IReadOnlyList<LauncherApp> Pins { get; private set; } = Array.Empty<LauncherApp>();
    public string Query { get; private set; } = "";
    /// <summary>What the grid shows: every app, or the search's results.</summary>
    public IReadOnlyList<LauncherApp> Listed { get; private set; } = Array.Empty<LauncherApp>();
    public int Page { get; private set; }
    public LauncherSpot? Spot { get; private set; }
    public LauncherMenu? Menu { get; private set; }
    /// <summary>A short line shown in place of the hint for a few seconds.</summary>
    public string? Notice { get; private set; }
    public DateTime Now = DateTime.Now;
    public LauncherBattery? Battery;
    public LauncherWifi? Wifi;
    public CultureInfo Culture = CultureInfo.CurrentCulture;

    private bool scanning;
    private int noticeCount;
    private DispatcherTimer? noticeTimer;
    private double wheelSum;
    private bool wheelUsed;
    private long lastWheel, lastTurn;

    public LauncherModel(SettingsStore store, AppState state, IAppActions actions, bool samples = false)
    {
        Store = store;
        State = state;
        Actions = actions;
        Samples = samples;
        if (!samples)
        {
            Worker = new LauncherWorker();
            Icons = new LauncherIcons(Worker, Dispatcher.CurrentDispatcher);
            RefreshPins();
            Rescan();
        }
    }

    private void Tell(bool shape = false)
    {
        if (shape) Shape++;
        Changed?.Invoke();
    }

    // MARK: what is shown

    public bool Searching => LauncherSearch.Folded(Query).Length > 0;

    public LauncherGrid Grid => new(
        Searching ? 0 : Pins.Count, Listed.Count, LauncherMetrics.Columns,
        Searching ? LauncherMetrics.SearchRows : LauncherMetrics.Rows);

    /// <summary>The places in Listed that are on the page shown.</summary>
    public (int LowerBound, int UpperBound) PageRange => LauncherPaging.Range(Page, Listed.Count, Grid.PerPage);

    public LauncherApp? App(LauncherSpot spot) => spot.Kind == LauncherSpotKind.Pin
        ? (spot.Value >= 0 && spot.Value < Pins.Count ? Pins[spot.Value] : null)
        : (spot.Value >= 0 && spot.Value < Listed.Count ? Listed[spot.Value] : null);

    public string TimeText => Now.ToString("t", Culture);

    /// <summary>The day and the date without the year, in the PC's own order: "Monday 16 November", "Monday, November 16".</summary>
    public string DateText
    {
        get
        {
            string day = Now.ToString("dddd", Culture), date = Now.ToString("M", Culture);
            bool monthFirst = Culture.DateTimeFormat.MonthDayPattern.TrimStart().StartsWith("M", StringComparison.Ordinal);
            return monthFirst ? $"{day}, {date}" : $"{day} {date}";
        }
    }

    /// <summary>The hint at the bottom, with the launcher's own shortcut when shortcuts are on.</summary>
    public string Hint
    {
        get
        {
            if (Searching) return "Enter opens  ·  Esc clears the search";
            string text = "Type to search  ·  Enter opens  ·  Esc closes";
            if (Store.S.Shortcuts) text += $"  ·  {Shortcut.Find("launcher").Keys} shows or hides this";
            return text;
        }
    }

    // MARK: opening and the PC's state

    /// <summary>Called each time the launcher is shown: a clean start, and the PC looked at again.</summary>
    public void Prepare()
    {
        Query = "";
        Page = 0;
        Spot = null;
        Menu = null;
        Notice = null;
        wheelSum = 0;
        wheelUsed = false;
        Relist();
        RefreshPins();
        RefreshStatus();
        Rescan();
        Tell(shape: true);
    }

    public void RefreshStatus()
    {
        DateTime date = DateTime.Now;
        bool changed = date.Minute != Now.Minute || date.Hour != Now.Hour || date.Date != Now.Date;
        if (Samples) return;
        if (changed) Now = date;
        LauncherBattery? b = LauncherBattery.Read();
        if (b != Battery) { Battery = b; changed = true; }
        if (changed) Tell();
        // The network is asked off the main thread: it can take a moment.
        Dispatcher ui = Dispatcher.CurrentDispatcher;
        Worker?.Do(() =>
        {
            LauncherWifi? w = LauncherWifiReader.Read();
            ui.BeginInvoke(() =>
            {
                if (w == Wifi) return;
                Wifi = w;
                Tell();
            });
        }, after: 1);
    }

    /// <summary>
    /// The first run: up to four of Windows' own apps are pinned, and that is remembered so it happens once. On
    /// the PC the apps are found by name among those read, so this waits for the first reading.
    /// </summary>
    private void ChoosePinsOnce()
    {
        if (Store.S.PinsChosen || !Scanned) return;
        var found = new List<string>();
        if (Store.S.PinnedApps.Count == 0)
        {
            foreach (string name in LauncherApps.DefaultPinNames)
            {
                if (found.Count >= LauncherPins.DefaultCount) break;
                string wanted = LauncherSearch.Folded(name);
                LauncherApp? app = Apps.FirstOrDefault(a => LauncherSearch.Folded(a.Name) == wanted);
                if (app != null) found.Add(app.Id);
            }
        }
        Store.Update(s =>
        {
            if (s.PinnedApps.Count == 0) s.PinnedApps = found;
            s.PinsChosen = true;
        });
    }

    /// <summary>An app is still there: a shortcut's file exists; a Store app is among the apps read.</summary>
    private bool Exists(string id) =>
        Samples || LauncherApps.ShortcutExists(id) || (!LauncherApps.IsShortcutId(id) && Apps.Any(a => a.Id == id));

    /// <summary>The pins to show: those whose app is still there. The setting keeps the others.</summary>
    public void RefreshPins()
    {
        string[] ids = LauncherPins.Shown(Store.S.PinnedApps, SpaceSettings.MaxPinnedApps, Exists);
        var known = new Dictionary<string, string>();
        foreach (LauncherApp a in Apps) known.TryAdd(a.Id, a.Name);
        List<LauncherApp> fresh = ids.Select(id => new LauncherApp(id, known.TryGetValue(id, out string? name) ? name
            : Samples ? id[(id.IndexOf(':') + 1)..] : LauncherApps.PlainName(id))).ToList();
        if (fresh.SequenceEqual(Pins)) return;
        Pins = fresh;
        if (Spot is LauncherSpot s && s.Kind == LauncherSpotKind.Pin && s.Value >= Pins.Count) Spot = null;
        Shape++;
    }

    /// <summary>
    /// Reads the apps again off the main thread. What was read last time stays on show meanwhile, so the launcher
    /// opens at once.
    /// </summary>
    public void Rescan()
    {
        if (Samples || scanning || Worker == null) return;
        scanning = true;
        Dispatcher ui = Dispatcher.CurrentDispatcher;
        Worker.Do(() =>
        {
            List<LauncherApp> found;
            try { found = LauncherApps.Scan(); }
            catch (Exception e)
            {
                Log.P($"launcher: the apps were not read: {e.Message}");
                found = new List<LauncherApp>();
            }
            ui.BeginInvoke(() => Took(found));
        });
    }

    private void Took(List<LauncherApp> found)
    {
        scanning = false;
        bool first = !Scanned;
        Scanned = true;
        if (!found.SequenceEqual(Apps))
        {
            Apps = found;
            var keep = new HashSet<string>(found.Select(a => a.Id));
            foreach (LauncherApp p in Pins) keep.Add(p.Id);
            Icons?.Forget(keep);
            Relist();
        }
        ChoosePinsOnce();
        RefreshPins();
        // The icons are read ahead, the pinned apps' first, so the launcher opens with them there.
        Icons?.ReadAhead(Pins.Concat(Apps));
        Tell(shape: first);
    }

    public void SetApps(IReadOnlyList<LauncherApp> list)
    {
        Apps = list;
        Scanned = true;
        Relist();
        RefreshPins();
        Tell(shape: true);
    }

    private void Relist()
    {
        IReadOnlyList<LauncherApp> fresh = Searching
            ? LauncherSearch.Results(Apps.Select(a => a.Name).ToList(), Query).Select(i => Apps[i]).ToList()
            : Apps;
        if (!fresh.SequenceEqual(Listed)) Listed = fresh;
        Page = LauncherPaging.Clamped(Page, Listed.Count, Grid.PerPage);
        if (Spot is LauncherSpot s && s.Kind == LauncherSpotKind.Item && s.Value >= Listed.Count)
            Spot = Listed.Count == 0 ? null : LauncherSpot.Item(Listed.Count - 1);
        Shape++;
    }

    // MARK: the keyboard

    /// <summary>
    /// A key pressed while the launcher has the keyboard. True when the launcher used it. Letters are not taken
    /// here: they arrive as text (Typed), so every keyboard layout and way of typing works.
    /// </summary>
    public bool KeyDown(Key key, ModifierKeys modifiers)
    {
        // The Windows key, and Alt alone (Alt with Ctrl is AltGr, which types letters on many keyboards), belong to Windows.
        if (modifiers.HasFlag(ModifierKeys.Windows)) return false;
        if (modifiers.HasFlag(ModifierKeys.Alt) && !modifiers.HasFlag(ModifierKeys.Control)) return false;
        bool menuKey = key == Key.Apps || (key == Key.F10 && modifiers.HasFlag(ModifierKeys.Shift));
        if (Menu is LauncherMenu open)
        {
            switch (key)
            {
                case Key.Escape:
                    CloseMenu();
                    return true;
                case Key.Down:
                case Key.Up:
                    int step = key == Key.Down ? 1 : -1;
                    int row = open.Row < 0 ? (step > 0 ? 0 : open.Rows - 1) : Math.Clamp(open.Row + step, 0, open.Rows - 1);
                    Menu = open with { Row = row };
                    Tell();
                    return true;
                case Key.Enter:
                    if (open.Row >= 0)
                    {
                        if (open.Row == 0) TogglePin(open.App);
                        else ShowInExplorer(open.App);
                        return true;
                    }
                    break;
            }
            if (menuKey)
            {
                CloseMenu();
                return true;
            }
            Menu = null;
        }
        switch (key)
        {
            case Key.Escape: Escape(); break;
            case Key.Enter: OpenSelected(); break;
            case Key.Back:
            case Key.Delete: SetQuery(LauncherSearch.Erased(Query)); break;
            case Key.Left: Arrow(LauncherMove.Left); break;
            case Key.Right: Arrow(LauncherMove.Right); break;
            case Key.Down: Arrow(LauncherMove.Down); break;
            case Key.Up: Arrow(LauncherMove.Up); break;
            case Key.PageDown: SetPage(Page + 1); break;
            case Key.PageUp: SetPage(Page - 1); break;
            default:
                if (menuKey)
                {
                    MenuForSelected();
                    break;
                }
                Tell();
                return false;
        }
        Tell();
        return true;
    }

    /// <summary>Text typed while the launcher has the keyboard: it goes to the search.</summary>
    public void Typed(string text)
    {
        if (text.Length == 0) return;
        if (Menu != null) Menu = null;
        SetQuery(LauncherSearch.Typed(Query, text));
        Tell();
    }

    /// <summary>Escape: closes the menu, else clears the search, else closes the launcher.</summary>
    public void Escape()
    {
        if (Menu != null) Menu = null;
        else if (Query.Length > 0) SetQuery("");
        else OnClose(LauncherClose.Dismissed);
    }

    public void SetQuery(string text)
    {
        if (text == Query) return;
        Query = text;
        Page = 0;
        Relist();
        // A search selects its best result, so Enter opens it; with no search nothing is selected until an arrow.
        Spot = Searching && Listed.Count > 0 ? LauncherSpot.Item(0) : null;
    }

    private void Arrow(LauncherMove move)
    {
        (LauncherSpot? spot, int page) = Grid.Move(Spot, move, Page);
        Spot = spot;
        SetPage(page);
    }

    private void OpenSelected()
    {
        if (Spot is LauncherSpot s && App(s) is LauncherApp app) Open(app);
        else if (Searching && Listed.Count > 0) Open(Listed[0]);
    }

    // MARK: pages

    public void SetPage(int wanted)
    {
        int p = LauncherPaging.Clamped(wanted, Listed.Count, Grid.PerPage);
        if (p == Page) return;
        Page = p;
        if (Spot is LauncherSpot s && s.Kind == LauncherSpotKind.Item && LauncherPaging.Page(s.Value, Grid.PerPage) != p) Spot = null;
        Shape++;
    }

    /// <summary>The page the mouse or a dot asks for.</summary>
    public void TurnTo(int page)
    {
        Menu = null;
        SetPage(page);
        Tell();
    }

    /// <summary>
    /// A turn of the wheel over the launcher turns the page: one page for a notch of a wheel, one for a swipe of
    /// two fingers on a touchpad (what the fingers do counts once; the glide after it does not).
    /// </summary>
    public void Wheel(int delta, long milliseconds)
    {
        if (Menu != null || delta == 0) return;
        bool notch = delta % 120 == 0;
        long sinceLast = milliseconds - lastWheel;
        lastWheel = milliseconds;
        if (notch)
        {
            if (milliseconds - lastTurn <= 180) return;
        }
        else
        {
            if (sinceLast > 250)
            {
                wheelSum = 0;
                wheelUsed = false;
            }
            if (wheelUsed) return;
            wheelSum += delta;
            if (Math.Abs(wheelSum) < 90) return;
            wheelUsed = true;
            delta = Math.Sign(wheelSum);
        }
        lastTurn = milliseconds;
        SetPage(Page + (delta < 0 ? 1 : -1));
        Tell();
    }

    // MARK: the mouse

    /// <summary>A right click on [spot]'s app at [point] (the window's points from its top left): its menu.</summary>
    public void ContextClick(LauncherSpot spot, Point point)
    {
        Menu = App(spot) is LauncherApp app ? new LauncherMenu(app, spot, point, Store.S.PinnedApps.Contains(app.Id), -1, Samples || app.IsShortcut) : null;
        Tell();
    }

    /// <summary>The Menu key (or Shift+F10): the menu for the app the arrow keys are on, its first row ready.</summary>
    private void MenuForSelected()
    {
        if (Spot is not LauncherSpot s || App(s) is not LauncherApp app) return;
        Point at = WhereIs(s) ?? new Point(0, 0);
        Menu = new LauncherMenu(app, s, at, Store.S.PinnedApps.Contains(app.Id), 0, Samples || app.IsShortcut);
    }

    public void CloseMenu()
    {
        if (Menu == null) return;
        Menu = null;
        Tell();
    }

    /// <summary>Opens [app] and closes the launcher.</summary>
    public void Open(LauncherApp app)
    {
        Menu = null;
        if (Samples) return;
        Log.P($"launcher: opening {app.Name}");
        // The app is asked to open on the floating screen the launcher is on (see LauncherApps.Start). An app
        // that is already running only comes to the front, its windows where they were.
        DisplayInfo? on = Screen;
        Worker?.Do(() => LauncherApps.Start(app, on));
        OnClose(LauncherClose.HandedOver);
    }

    public void TogglePin(LauncherApp app)
    {
        Menu = null;
        int limit = SpaceSettings.MaxPinnedApps;
        List<string> pinned = Store.S.PinnedApps;
        if (pinned.Contains(app.Id))
        {
            string[] left = LauncherPins.Removing(app.Id, pinned);
            Store.Update(s => s.PinnedApps = left.ToList());
        }
        else
        {
            (string[] pins, LauncherPins.Added result) = LauncherPins.Adding(app.Id, pinned, limit);
            if (result == LauncherPins.Added.Full)
            {
                // Pins whose app is gone are not shown; they should not be what makes ten.
                string[] there = LauncherPins.Shown(pinned, limit, Exists);
                (pins, result) = LauncherPins.Adding(app.Id, there, limit);
            }
            if (result == LauncherPins.Added.Full)
            {
                Say("Ten apps are pinned. Unpin one first.");
                return;
            }
            Store.Update(s => s.PinnedApps = pins.ToList());
        }
        RefreshPins();
        Tell();
    }

    public void ShowInExplorer(LauncherApp app)
    {
        Menu = null;
        if (Samples) return;
        Worker?.Do(() => LauncherApps.ShowInExplorer(app));
        OnClose(LauncherClose.HandedOver);
    }

    public void Say(string line)
    {
        Notice = line;
        int mine = ++noticeCount;
        Tell();
        if (Theme.Static) return;
        noticeTimer?.Stop();
        noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer?.Stop();
            if (noticeCount != mine) return;
            Notice = null;
            Tell();
        };
        noticeTimer.Start();
    }

    // MARK: the round buttons

    public bool IsLit(LauncherButton button) => button switch
    {
        LauncherButton.Steady => State.Steady,
        LauncherButton.Depth => Store.S.Want3D,
        LauncherButton.Pocket => State.PocketMode,
        LauncherButton.Hands => State.HandsRunning,
        _ => false,
    };

    public void Press(LauncherButton button)
    {
        Menu = null;
        if (Samples) return;
        // The launcher closes first, so a window the button opens is not behind it and takes the keyboard itself.
        switch (button)
        {
            case LauncherButton.Recentre:
                OnClose(LauncherClose.Dismissed);
                Actions.Recentre();
                break;
            case LauncherButton.Steady:
                Actions.SetSteady(!State.Steady);
                break;
            case LauncherButton.Depth:
                Store.Update(s => s.Want3D = !s.Want3D);
                break;
            case LauncherButton.Pocket:
                OnClose(LauncherClose.Dismissed);
                Actions.SetPocketMode(!State.PocketMode);
                break;
            case LauncherButton.Hands:
                Actions.SetHands(!State.HandsRunning);
                break;
            case LauncherButton.ControlPanel:
                OnClose(LauncherClose.HandedOver);
                Actions.OpenControlPanel();
                break;
            default:
                OnClose(LauncherClose.HandedOver);
                Actions.OpenSettings();
                break;
        }
        Tell();
    }

    /// <summary>The settings or the app's state changed elsewhere (a shortcut, the control panel).</summary>
    public void Outside()
    {
        RefreshPins();
        Tell();
    }
}

/// <summary>
/// The launcher: a sheet of the PC's apps shown on a floating screen, so it is seen in the glasses. A borderless
/// window over the whole of that display, on top of other windows, that takes the keyboard while it is open.
/// </summary>
public sealed class LauncherController
{
    private readonly SettingsStore store;
    private readonly AppState state;
    private readonly LauncherModel model;
    private Window? window;
    private LauncherView? view;
    private DisplayInfo? shownOn;
    private bool open;
    private long openedAt;
    private DispatcherTimer? timer;
    /// <summary>The window that was in front when the launcher opened; it gets the keyboard back if nothing is opened.</summary>
    private IntPtr before;

    public LauncherController(SettingsStore store, AppState state, IAppActions actions)
    {
        this.store = store;
        this.state = state;
        model = new LauncherModel(store, state, actions);
        model.OnClose = Close;
        store.Changed += (_, _) => { if (open) model.Outside(); };
        state.Changed += () => { if (open) model.Outside(); };
    }

    public bool IsOpen => open;
    /// <summary>When the launcher last closed because the user went to another window.</summary>
    private long leftAt = long.MinValue / 2;

    /// <summary>
    /// Shows the launcher on [on] (a floating screen), or hides it when it is open. Open on another screen, it
    /// moves to [on] instead.
    /// </summary>
    public void Toggle(DisplayInfo? on)
    {
        // Pressing the control panel's Launcher button first takes the keyboard from the launcher, which closes it
        // (the user went elsewhere); the press itself then arrives. It was meant to close the launcher, and it has.
        // Seen on the Steam Deck, 17 Nov 2026: the button closed and at once reopened it.
        if (!open && Environment.TickCount64 - leftAt < 500) return;
        if (open)
        {
            if (on != null && window != null && shownOn != null && !SameFrame(on, shownOn))
            {
                Place(on);
                Front();
                return;
            }
            Close(LauncherClose.Dismissed);
            return;
        }
        if (on == null)
        {
            Log.P("launcher: no floating screen to show it on");
            return;
        }
        Show(on);
    }

    public void Close() => Close(LauncherClose.HandedOver);

    private static bool SameFrame(DisplayInfo a, DisplayInfo b) => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    private void Show(DisplayInfo on)
    {
        Window w = window ??= MakeWindow();
        IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();
        IntPtr front = GetForegroundWindow();
        before = front == hwnd ? IntPtr.Zero : front;
        Place(on);
        model.Prepare();
        open = true;
        openedAt = Environment.TickCount64;
        state.LauncherOpen = true;
        state.Notify();
        w.Show();
        // Showing can put the window back where WPF last thought it was: it is placed again, in pixels.
        Place(on);
        Front();
        view?.Arrive();
        Log.P($"launcher: shown on {on.GdiName} {on.Width}x{on.Height}");
        timer?.Stop();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => model.RefreshStatus();
        timer.Start();
    }

    private void Front()
    {
        if (window == null) return;
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
        if (view != null) Keyboard.Focus(view);
    }

    private void Close(LauncherClose why)
    {
        if (!open) return;
        open = false;
        state.LauncherOpen = false;
        state.Notify();
        timer?.Stop();
        timer = null;
        model.CloseMenu();
        bool wasActive = window?.IsActive == true;
        // The sheet is not see-through, so it does not fade away: it goes at once.
        window?.Hide();
        if (why == LauncherClose.Dismissed && wasActive && before != IntPtr.Zero) SetForegroundWindow(before);
        before = IntPtr.Zero;
    }

    /// <summary>
    /// Covers [on] with the window, in pixels, whatever scaling that display has. Done twice: the first move can
    /// take the window to a display with another scaling, on which Windows resizes it.
    /// </summary>
    private void Place(DisplayInfo on)
    {
        if (window == null) return;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        const uint noActivate = 0x10;
        SetWindowPos(hwnd, new IntPtr(-1), on.X, on.Y, on.Width, on.Height, noActivate);
        SetWindowPos(hwnd, new IntPtr(-1), on.X, on.Y, on.Width, on.Height, noActivate);
        shownOn = on;
        model.Screen = on;
        if (model.Icons != null)
        {
            double scale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            model.Icons.Pixels = scale > 1.75 ? 256 : 128;
        }
    }

    private Window MakeWindow()
    {
        view = new LauncherView(model);
        var w = new Window
        {
            Title = "Pocket XR Launcher",
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = true,
            Background = Theme.Page, Foreground = Theme.Ink, FontFamily = Theme.Text,
            UseLayoutRounding = true, SnapsToDevicePixels = true, Content = view,
        };
        System.Windows.Media.TextOptions.SetTextFormattingMode(w, System.Windows.Media.TextFormattingMode.Ideal);
        // Keys are taken here, before any part of the sheet, so typing needs no click and no text box.
        w.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (view.KeyBelongsToFocus(key)) return;
            bool arrow = key is Key.Left or Key.Right or Key.Up or Key.Down;
            if (model.KeyDown(key, Keyboard.Modifiers))
            {
                e.Handled = true;
                // The arrow keys move over the apps: a round button the Tab key was on gives the keyboard back.
                if (arrow) Keyboard.Focus(view);
            }
        };
        w.PreviewTextInput += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            model.Typed(e.Text);
            Keyboard.Focus(view);
            e.Handled = true;
        };
        w.PreviewMouseWheel += (_, e) =>
        {
            model.Wheel(e.Delta, Environment.TickCount64);
            e.Handled = true;
        };
        // The user clicked another app or another window: the launcher is done. (Just after opening, the keyboard
        // can pass through other hands while Pocket XR steps forward; that is not the user leaving.)
        w.Deactivated += (_, _) =>
        {
            if (open && Environment.TickCount64 - openedAt > 300)
            {
                Close(LauncherClose.HandedOver);
                leftAt = Environment.TickCount64;
            }
        };
        // Alt+F4 hides it like Escape; the window itself lives as long as Pocket XR.
        w.Closing += (_, e) =>
        {
            e.Cancel = true;
            Close(LauncherClose.Dismissed);
        };
        return w;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

/// <summary>
/// The launcher drawn to picture files, with no window, so the design can be looked over with no glasses. The apps
/// are a fixed made-up list with drawn icons, so the pictures are the same on any PC.
/// </summary>
public static class Launcher
{
    public static readonly string[] SampleNames =
    {
        "Calculator", "Calendar", "Camera", "Character Map", "Clock", "Command Prompt", "Drawing Board", "Feedback Hub",
        "File Explorer", "Films & TV", "Game Bar", "Magnifier", "Mail", "Maps", "Media Player", "Microsoft Edge",
        "Narrator", "Notepad", "On-Screen Keyboard", "Paint", "Phone Link", "Photos", "Quick Assist", "Reading List",
        "Remote Desktop Connection", "Recipe Book", "Settings", "Snipping Tool", "Solitaire & Casual Games",
        "Sound Recorder", "Steps Recorder", "Sticky Notes", "Spreadsheets", "Task Manager", "Terminal", "Tips",
        "Visual Studio Code", "Weather", "Windows Media Player Legacy", "Windows Security", "WordPad",
    };

    public static readonly string[] SamplePins = { "Microsoft Edge", "File Explorer", "Notepad", "Photos", "Media Player", "Terminal" };

    private static string SampleId(string name) => "sample:" + name;

    /// <summary>The launcher open on a floating screen with an XREAL Aura working: 41 apps, some pinned, a Monday morning.</summary>
    public static LauncherModel SampleModel(IReadOnlyList<string>? pins = null, bool scanned = true)
    {
        var store = new SettingsStore(persist: false);
        store.Update(s =>
        {
            s.PinnedApps = (pins ?? SamplePins).Select(SampleId).ToList();
            s.PinsChosen = true;
        });
        var state = new AppState
        {
            GlassesName = "XREAL Aura", SteadyAvailable = true, VirtualScreens = 2, LauncherOpen = true, TrackingOn = true,
        };
        var model = new LauncherModel(store, state, new NoActions(), samples: true)
        {
            Culture = CultureInfo.GetCultureInfo("en-GB"),
            Now = new DateTime(2026, 11, 16, 9, 41, 0),
            Battery = new LauncherBattery(82, false),
            Wifi = LauncherWifi.Connected,
        };
        if (scanned) model.SetApps(SampleNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => new LauncherApp(SampleId(n), n)).ToList());
        else model.RefreshPins();
        return model;
    }

    /// <summary>
    /// Draws the launcher to PNG files in [folder], each as one floating screen shows it (1920 x 1080 but for the
    /// small one): the first page with its pins, a search with results, a search with none, the second page, an app
    /// picked with the arrow keys, a pin picked with them, the right-click menu, ten pins with the line that refuses
    /// an eleventh, a 1280 x 720 screen, the apps not read yet, and no pins at all.
    /// </summary>
    public static void Pictures(string folder)
    {
        void One(string name, LauncherModel model, double width = 1920, double height = 1080, Action<LauncherView>? then = null)
        {
            var view = new LauncherView(model) { Width = width, Height = height };
            view.Fit(new Size(width, height));
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            then?.Invoke(view);
            UiRender.Png(Path.Combine(folder, name), view, scale: 1);
        }

        One("launcher-1-first-page.png", SampleModel());

        LauncherModel search = SampleModel();
        search.Typed("re");
        One("launcher-2-search.png", search);

        LauncherModel none = SampleModel();
        none.Typed("garden");
        One("launcher-3-search-none.png", none);

        LauncherModel second = SampleModel();
        second.TurnTo(1);
        One("launcher-4-second-page.png", second);

        LauncherModel picked = SampleModel();
        picked.KeyDown(Key.Down, ModifierKeys.None);
        picked.KeyDown(Key.Down, ModifierKeys.None);
        picked.KeyDown(Key.Right, ModifierKeys.None);
        picked.KeyDown(Key.Right, ModifierKeys.None);
        One("launcher-5-keyboard-pick.png", picked);

        LauncherModel pin = SampleModel();
        pin.KeyDown(Key.Up, ModifierKeys.None);
        pin.KeyDown(Key.Right, ModifierKeys.None);
        pin.KeyDown(Key.Right, ModifierKeys.None);
        One("launcher-6-keyboard-pin.png", pin);

        LauncherModel menu = SampleModel();
        One("launcher-7-menu.png", menu, then: view =>
        {
            Point? at = menu.WhereIs(LauncherSpot.Item(7));
            if (at is Point p) menu.ContextClick(LauncherSpot.Item(7), new Point(p.X + 22, p.Y - 14));
        });

        LauncherModel ten = SampleModel(new[]
        {
            "Microsoft Edge", "File Explorer", "Notepad", "Photos", "Windows Media Player Legacy", "Terminal",
            "Remote Desktop Connection", "Solitaire & Casual Games", "Calculator", "On-Screen Keyboard",
        });
        ten.State.Steady = true;
        ten.TurnTo(1);
        ten.KeyDown(Key.Down, ModifierKeys.None);
        ten.KeyDown(Key.Right, ModifierKeys.None);
        ten.KeyDown(Key.Right, ModifierKeys.None);
        ten.Say("Ten apps are pinned. Unpin one first.");
        One("launcher-8-ten-pins.png", ten);

        One("launcher-9-small-screen.png", SampleModel(), 1280, 720);

        One("launcher-10-looking.png", SampleModel(scanned: false));

        LauncherModel bare = SampleModel(Array.Empty<string>());
        bare.Battery = new LauncherBattery(14, false);
        bare.Wifi = LauncherWifi.Off;
        bare.Store.Update(s => s.Shortcuts = false);
        One("launcher-11-no-pins.png", bare);

        LauncherModel mains = SampleModel();
        mains.Battery = new LauncherBattery(100, true);
        mains.State.HandsRunning = true;
        mains.Store.Update(s => s.Want3D = false);
        mains.State.SteadyAvailable = false;
        mains.Typed("windows media player legacy and more words");
        One("launcher-12-long-search.png", mains);
    }
}
