using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketXR.Core;
using PocketXR.Hands;
using PocketXR.Platform;
using PocketXR.Ui;
using PocketXR.Windows;

namespace PocketXR;

/// <summary>
/// Pocket XR on a PC: finds the glasses, makes the floating screens, captures them, tracks the head and draws the
/// Space on the glasses. Lives in the notification area, with a control panel, a settings window and the tour.
/// </summary>
public sealed class AppController : IAppActions, IHandsHost, IDisposable
{
    public readonly SettingsStore Store = new();
    public readonly AppState State = new();

    private readonly Application app;
    private readonly Dispatcher ui;
    private MessageWindow? messages;
    private IntPtr iconIdle, iconLit;
    private PxWindow? controlPanel, settingsWindow, tourWindow;
    private Window? trayMenu;
    private LauncherController? launcher;
    private PocketMode? pocket;
    private ControllerInput? controllerInput;
    private SpaceGrab? grab;
    private EdgeHint? edgeHint;
    private HandTracking? hands;
    private bool panelShownBeforePocket;
    private DispatcherTimer? timer;
    private EventWaitHandle? showSignal, quitSignal;

    private Glasses? glasses;
    private HeadFusion? fusion;
    private HeadPose? headPose;
    private ImuReader? imu;
    private HidImuReader? hidImu;
    private SettingsControl? settings;
    private SettingsControl.State settingsState = new();
    private Render.GlassesPicture? picture;
    private VirtualDisplays? displays;
    private List<DisplayInfo> floating = new();
    private DisplayInfo? glassesDisplay;
    /// <summary>The PC's own screen when it is what the glasses show (no floating screens), else null.</summary>
    private DisplayInfo? mirrored;
    private DispatcherTimer? arrangeTimer;
    /// <summary>Windows shows the same desktop on the glasses as on another screen (Duplicate).</summary>
    private bool glassesMirrored;
    private double lastExtendAsked = double.NegativeInfinity;
    private readonly string? standIn;
    private bool quitting;
    private bool resumingSteady, resumeStarted;
    private readonly bool resumeSteady;
    private int ticks;

    /// <summary>When the glasses' display was last seen to go, null while it is there.</summary>
    private double? glassesGoneSince;
    /// <summary>When the glasses were last asked to change mode (3D, Steady mode), or Pocket XR started: for a while
    /// after, their display and even their USB connection come and go, and none of that is an unplugging.</summary>
    private double modeAskedAt = Now;
    private const double DisplayGoneGraceS = 8.0;

    /// <summary>Head scroll in Steady mode: the row's slide along the canvas, in the canvas's own degrees.</summary>
    private readonly HeadScroll steadyScroll = new();
    private double steadyScrollYaw;
    /// <summary>The canvas is drawn through a narrow lens (see Recompute), so a real degree of slide is fewer of its degrees.</summary>
    private const double SteadyScrollScale = 0.6;

    // What a frame needs, worked out when the settings change and not on every frame. Read by the render thread.
    private readonly object frameGate = new();
    private PanelPlacement[] placements = Array.Empty<PanelPlacement>();
    private TrackingOptions trackingOptions = new(new SpaceSettings());
    private Render.RenderOptions renderOptions = new();
    private double framePeriod = 1.0 / 60;
    private bool frameSteady, frameHeadScroll;

    private int? pointerScreen;
    private (int Index, double Since)? lookedAt;

    private static double Now => HeadFusion.SystemUptime;

    /// <summary>[standIn] is a test: a real monitor (its number, 1 or 2...) stands in for the glasses' display, and
    /// shows the PC's main screen as one floating screen. For a PC with no USB-C port that carries video.</summary>
    public AppController(Application app, bool resumeSteady, string? standIn = null)
    {
        this.app = app;
        this.resumeSteady = resumeSteady;
        resumingSteady = resumeSteady;
        this.standIn = standIn;
        ui = app.Dispatcher;
    }

    // MARK: start and stop

    public void Start()
    {
        Log.P($"Pocket XR for Windows {State.Version} starting, {RuntimeInformation.OSDescription}");
        ShortcutChoices.Load();
        messages = new MessageWindow();
        int size = MessageWindow.SmallIconSize;
        iconIdle = MessageWindow.GlassesIcon(size, lit: false);
        iconLit = MessageWindow.GlassesIcon(size, lit: true);
        messages.ShowTray(iconIdle, "Pocket XR");
        messages.TrayClick += OpenControlPanel;
        messages.TrayMenu += (x, y) =>
        {
            trayMenu?.Close();
            trayMenu = TrayMenu.Show(Store, State, this, x, y);
        };
        RegisterShortcuts();
        hands = new HandTracking(Store, State, this, ui);
        Store.Changed += (old, now) => ui.BeginInvoke(() => SettingsChanged(old, now));
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        // A second start of Pocket XR asks this one to show its control panel.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "PocketXR.show");
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "PocketXR.quit");
        var waiter = new Thread(() =>
        {
            var both = new WaitHandle[] { showSignal, quitSignal };
            while (!quitting)
            {
                int which = WaitHandle.WaitAny(both, 500);
                if (quitting) break;
                if (which == 0) ui.BeginInvoke(OpenControlPanel);
                else if (which == 1)
                {
                    Log.P("quit asked from outside (PocketXR --quit)");
                    ui.BeginInvoke(Quit);
                }
            }
        }) { IsBackground = true, Name = "Pocket XR show" };
        waiter.Start();

        VirtualDisplays.DriverInfo? driver = VirtualDisplays.Probe(out string why);
        State.DriverReady = driver != null;
        State.DriverWhy = why;
        Log.P(driver != null
            ? $"virtual display driver: {driver.Name}, version {driver.Version}, watchdog {driver.WatchdogTimeoutS} s"
            : $"virtual display driver: {why}");

        Recompute();
        ScreensChanged();
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        if (Store.S.PanelAtStart) OpenControlPanel();
        State.ControllerAvailable = true; // written and checked by arithmetic; not yet tried with a real controller
        controllerInput = new ControllerInput(Store, State, this);
        controllerInput.Start();
        grab = new SpaceGrab(() => Store.S.GrabKeys && floating.Count > 0 && !quitting, NudgeSpace);
        if (Tour.OpensByItself(Store.S.TourSeen))
        {
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                OpenTour();
            };
            later.Start();
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref lastDisplayChange, Now);
        ui.BeginInvoke(ScreensChanged);
        AskDepthWhenQuiet();
    }

    /// <summary>The displays have not changed for [DisplayQuietS] seconds.</summary>
    private bool DisplaysQuiet => Now - Volatile.Read(ref lastDisplayChange) >= DisplayQuietS;
    private double lastDisplayChange = Now;
    private const double DisplayQuietS = 4.0;
    private DispatcherTimer? depthTimer;

    /// <summary>Once the displays are quiet, the glasses are asked for 3D (if the settings want it and they are not
    /// in it): the ask that was held back while Windows was busy.</summary>
    private void AskDepthWhenQuiet()
    {
        ui.BeginInvoke(() =>
        {
            depthTimer?.Stop();
            depthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DisplayQuietS + 0.5) };
            depthTimer.Tick += (_, _) =>
            {
                if (!DisplaysQuiet) return; // another change came: its own timer will ask
                depthTimer?.Stop();
                if (quitting || settings == null || !State.LinkOpen || State.Steady || State.Busy) return;
                if (Store.S.Want3D && settingsState.Stereo != 1)
                {
                    Log.P("settings: the displays are quiet; asking for 3D now");
                    settings.Apply();
                }
            };
            depthTimer.Start();
        });
    }

    /// <summary>The glasses were plugged in or out, or their display came, went, or changed mode (a 3D switch
    /// re-plugs it).</summary>
    private void ScreensChanged()
    {
        if (quitting) return;
        KeepWindowsOnOwnScreen();
        List<UsbDeviceInfo> usb = UsbDevices.List();
        Glasses? found = Glasses.Detect(usb);
        if (!Nullable.Equals(found, glasses))
        {
            Log.P($"glasses: {found?.Name ?? "none"} (USB: {UsbDevices.Describe(usb.Where(d => d.Vendor == Glasses.Xreal || d.Vendor == Glasses.Viture))})");
            GlassesChanged(found);
        }

        List<DisplayInfo> all = Displays.List();
        DisplayInfo? screen = StandIn(all) ?? Displays.Glasses(all);
        bool wasMirrored = glassesMirrored;
        glassesMirrored = screen == null && Displays.GlassesMirrored(all);
        if (glassesMirrored && !wasMirrored)
            Log.P("glasses display: Windows is duplicating another screen onto the glasses");
        // The glasses came back mirrored (their 3D and Steady modes look like a new monitor to Windows): the desktop
        // is extended onto them again, as Win+P, Extend does. At most once every 5 s, so a refusal cannot loop.
        if (glassesMirrored && standIn == null && Now - lastExtendAsked > 5)
        {
            lastExtendAsked = Now;
            if (Displays.Extend())
            {
                all = Displays.List();
                screen = Displays.Glasses(all);
                glassesMirrored = screen == null && Displays.GlassesMirrored(all);
            }
        }
        if (screen == null)
        {
            if (picture != null) Log.P("glasses display: gone");
            picture?.Dispose();
            picture = null;
            glassesDisplay = null;
            mirrored = null;
            State.ShowingOwnScreen = false;
            State.DisplayText = null;
            // With no glasses the floating screens cannot be seen, so they go, and Windows moves their windows back to
            // the PC's own screen. Not at once: a 3D switch takes the glasses' display away for a few seconds.
            glassesGoneSince ??= Now;
            if (displays != null) CheckGlassesStillGone(DisplayGoneGraceS);
            UpdateStatus();
            return;
        }
        glassesGoneSince = null;
        framePeriod = 1.0 / Math.Max(screen.RefreshHz, 30);
        State.DisplayText = $"{screen.Width} x {screen.Height}, {Math.Round(screen.RefreshHz)} Hz";
        State.Stereo = GlassesOptics.IsSideBySide(screen.Width, screen.Height);
        if (picture != null && glassesDisplay != null && glassesDisplay == screen)
        {
            RefreshFloating(all);
            if (!FloatingLost()) return;
            // Floating screens went while the glasses stayed (their driver took them away): they are made again,
            // and the picture with them.
            picture.Dispose();
            picture = null;
        }
        picture?.Dispose();
        picture = null;
        glassesDisplay = screen;
        Log.P($"glasses display: {screen}");
        EnsureDisplays();
        all = Displays.List();
        RefreshFloating(all);
        // The floating screens beside this PC's screen, as they sit in the glasses, and the glasses' display out of the
        // pointer's way. Windows then says the displays changed, and this runs again with everything in place.
        if (ArrangeDisplays(all))
        {
            all = Displays.List();
            RefreshFloating(all);
            screen = Displays.Glasses(all) ?? screen;
            glassesDisplay = screen;
        }
        // With no floating screens (no virtual display driver, or a stand-in test), the PC's own screen is shown as
        // the one panel: the glasses are still a screen held still in space, and nothing has to be installed.
        mirrored = floating.Count == 0 && Displays.Own(all) is DisplayInfo own ? own : null;
        State.ShowingOwnScreen = mirrored != null;
        if (mirrored != null) Log.P($"glasses picture: no floating screens, so the PC's own screen is shown: {mirrored}");
        List<DisplayInfo> sources = floating.Count > 0 ? floating : mirrored != null ? new List<DisplayInfo> { mirrored } : new();
        try
        {
            picture = new Render.GlassesPicture(screen, sources, Frame);
            picture.Lost += () => ui.BeginInvoke(ScreensChanged);
        }
        catch (Exception e)
        {
            Log.P($"glasses picture: could not start: {e.Message}");
            picture = null;
        }
        Recompute();
        UpdateStatus();
    }

    private DisplayInfo? StandIn(List<DisplayInfo> all)
    {
        if (standIn == null) return null;
        return all.Find(d => d.GdiName.EndsWith("DISPLAY" + standIn, StringComparison.OrdinalIgnoreCase) && !d.Primary);
    }

    /// <summary>Which of Windows' displays are Pocket XR's floating screens now, in their own order.</summary>
    private void RefreshFloating(List<DisplayInfo> all)
    {
        var now = new List<DisplayInfo>();
        if (displays != null)
        {
            foreach (VirtualDisplays.Screen s in displays.Screens)
            {
                DisplayInfo? d = VirtualDisplays.Find(s, all);
                if (d != null) now.Add(d);
            }
        }
        floating = now;
        State.VirtualScreens = now.Count;
    }

    private void GlassesChanged(Glasses? g)
    {
        Volatile.Write(ref lastDisplayChange, Now);
        AskDepthWhenQuiet();
        StopTracking();
        settings?.Stop();
        settings = null;
        settingsState = new SettingsControl.State();
        glasses = g;
        State.Glasses = g;
        State.GlassesName = g?.Name;
        State.SteadyAvailable = g?.SteadyCapable ?? false;
        State.Steady = false;
        State.LinkOpen = false;
        messages?.ShowTray(g != null ? iconLit : iconIdle, g != null ? $"Pocket XR · {g.Value.Name}" : "Pocket XR");
        Recompute();
        if (g is not Glasses glassesNow)
        {
            hands?.Stop();
            return;
        }
        // Steady mode comes back on with glasses that can do it, if it was on when the user last chose (or Pocket XR
        // restarted itself in it). Until then the glasses stay in 2D.
        resumeStarted = false;
        resumingSteady = glassesNow.SteadyCapable && (resumeSteady || Store.S.SteadyOn);
        StartTracking(glassesNow);
        if (Store.S.HandsOn) hands?.Start();
        if (glassesNow.ControlLink)
        {
            var s = new SettingsControl(glassesNow, st => ui.BeginInvoke(() => SettingsStateChanged(st)));
            // Read on the settings thread: the store's value is swapped whole, never changed in place.
            // 3D only once the displays have been quiet for a while: on the Steam Deck (17 Nov 2026) the Aura dropped
            // off USB three times when 3D was asked while Windows was still adding the floating screens and moving
            // the glasses' display (as the Mac saw on 16 Nov with a mode change straight after another).
            s.Want3D = () => Store.S.Want3D && !resumingSteady && DisplaysQuiet;
            s.SteadySixDof = () => Store.S.SteadySixDof;
            s.Start();
            settings = s;
        }
    }

    private void SettingsStateChanged(SettingsControl.State st)
    {
        bool wasSteady = settingsState.Steady;
        settingsState = st;
        State.LinkOpen = st.Link;
        State.Busy = st.Busy;
        State.Steady = st.Steady;
        if (wasSteady != st.Steady)
        {
            Log.P($"Steady mode: {(st.Steady ? "on" : "off")}");
            ResetSteadyScroll();
            Recompute();
        }
        // Already on (the glasses kept it): nothing to bring back.
        if (st.Steady) resumingSteady = false;
        // Steady mode was on last time (or when Pocket XR started itself again): back on, once the glasses have
        // settled. Until then the glasses are left in 2D (Want3D says no): a switch to 3D and straight on to Steady mode
        // was too much for the Aura on the Mac (16 Nov 2026: it answered 5001 three times, then dropped off USB).
        if (st.Link && !st.Busy && !st.Steady && resumingSteady && !resumeStarted)
        {
            resumeStarted = true;
            Log.P(resumeSteady ? "Steady mode: coming back on after the restart, once the glasses have settled"
                : "Steady mode: it was on last time, so it comes back on once the glasses have settled");
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                resumingSteady = false;
                if (!quitting) SetSteady(true);
            };
            later.Start();
        }
        UpdateStatus();
        State.Notify();
    }

    private void StartTracking(Glasses g)
    {
        bool useHid = !g.NetworkImu && g.HidImu && Store.S.HidTracking;
        if (!g.NetworkImu && !useHid)
        {
            Log.P(g.HidImu
                ? $"head tracking: {g.Name} uses USB HID, which is an untested switch in Settings; the screens are shown flat"
                : $"head tracking: none on a PC for {g.Name}; the screens are shown flat");
            return;
        }
        var f = new HeadFusion(g.Axes);
        fusion = f;
        headPose = new HeadPose(f);
        SetScrollRange();
        if (useHid)
        {
            var r = new HidImuReader(g, s => f.Report(s));
            r.Start();
            hidImu = r;
        }
        else
        {
            var r = new ImuReader(s => f.Report(s));
            r.Start();
            imu = r;
        }
    }

    private void StopTracking()
    {
        imu?.Stop();
        imu = null;
        hidImu?.Stop();
        hidImu = null;
        fusion = null;
        headPose = null;
        State.TrackingOn = false;
        State.ImuHz = 0;
    }

    /// <summary>Removes the floating screens once the glasses' display has stayed away long enough: 8 s when the
    /// glasses are unplugged, 25 s while they are still on USB or changing mode.</summary>
    private void CheckGlassesStillGone(double delay)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (quitting || displays == null || glassesGoneSince is not double since) return;
            List<DisplayInfo> all = Displays.List();
            if ((StandIn(all) ?? Displays.Glasses(all)) != null) return;
            double gone = Now - since;
            bool pluggedIn = Glasses.Detect(UsbDevices.List()) != null;
            bool changingMode = State.Busy || Now - modeAskedAt < 45;
            double limit = pluggedIn || changingMode ? 25.0 : DisplayGoneGraceS;
            if (gone < limit - 0.2)
            {
                CheckGlassesStillGone(Math.Min(2, limit - gone));
                return;
            }
            Log.P($"virtual displays: removed, the glasses' display has been gone {(int)gone} s");
            RemoveDisplays();
        };
        t.Start();
    }

    private void RemoveDisplays()
    {
        displays?.Dispose();
        displays = null;
        floating = new();
        State.VirtualScreens = 0;
        State.Capturing = 0;
    }

    /// <summary>The floating screens, made when the glasses' display is there. Unlike the Mac, Windows lets them be
    /// added and removed while Pocket XR runs, so a change of the screen count needs no restart.</summary>
    private void EnsureDisplays()
    {
        if (standIn != null || !State.DriverReady) return;
        int wanted = Store.S.Screens;
        if (displays != null && displays.Screens.Count == wanted) return;
        displays?.Dispose();
        displays = VirtualDisplays.Make(wanted, 1920, 1080, 60, out string why);
        if (displays == null)
        {
            Log.P($"virtual displays: not made: {why}");
            State.DriverWhy = why;
            return;
        }
        // Windows takes a moment to bring a new display up.
        for (int i = 0; i < 20; i++)
        {
            List<DisplayInfo> all = Displays.List();
            RefreshFloating(all);
            if (floating.Count == wanted) break;
            Thread.Sleep(100);
        }
        Log.P($"virtual displays: {floating.Count} of {wanted} are on the desktop");
    }

    // MARK: settings

    private void SettingsChanged(SpaceSettings old, SpaceSettings now)
    {
        if (quitting) return;
        // Screens moved by hand belong to the layout they were moved in: another layout starts tidy. (A saved
        // arrangement brings its own moves with its layout, and those are kept.)
        if (old.Layout != now.Layout && now.ScreensMoved && old.ScreenYawDeg.SequenceEqual(now.ScreenYawDeg) && old.ScreenPitchDeg.SequenceEqual(now.ScreenPitchDeg))
        {
            ResetScreenMoves();
            return;
        }
        if (old.Screens != now.Screens && glassesDisplay != null && standIn == null)
        {
            Log.P($"screens: {now.Screens}");
            picture?.Dispose();
            picture = null;
            glassesDisplay = null;
            ScreensChanged();
        }
        Recompute();
        // The pointer crosses between the floating screens the way they now look (not while one is being dragged
        // about: Windows takes a moment to move displays, so it waits until the screens have been still).
        if (floating.Count > 0 && (old.Layout != now.Layout || !old.ScreenYawDeg.SequenceEqual(now.ScreenYawDeg)
            || !old.ScreenPitchDeg.SequenceEqual(now.ScreenPitchDeg))) ArrangeSoon();
        if (old.Want3D != now.Want3D)
        {
            Log.P($"3D side by side: {(now.Want3D ? "on" : "off")}");
            modeAskedAt = Now;
            settings?.Apply();
        }
        if (old.Shortcuts != now.Shortcuts) RegisterShortcuts();
        if (old.LaunchAtLogin != now.LaunchAtLogin) SignIn.Set(now.LaunchAtLogin);
        if (old.HidTracking != now.HidTracking && glasses is Glasses g && !g.NetworkImu)
        {
            Log.P($"head tracking over USB HID: {(now.HidTracking ? "on" : "off")}");
            StopTracking();
            StartTracking(g);
        }
        State.Notify();
    }

    /// <summary>
    /// Some floating screens are gone from the desktop though the glasses are still there. Then they are let go, to be
    /// made again: at most 3 times in 2 minutes, so a driver that keeps refusing cannot loop.
    /// </summary>
    private bool FloatingLost()
    {
        if (displays == null || floating.Count >= displays.Screens.Count) return false;
        for (int look = 0; look < 6 && floating.Count < displays.Screens.Count; look++)
        {
            Thread.Sleep(250);
            RefreshFloating(Displays.List());
        }
        if (floating.Count >= displays.Screens.Count) return false;
        remakes.RemoveAll(t => Now - t > 120);
        if (remakes.Count >= 3)
        {
            Log.P($"virtual displays: {displays.Screens.Count - floating.Count} gone again; not made again (3 times in 2 minutes already)");
            return false;
        }
        remakes.Add(Now);
        Log.P($"virtual displays: {displays.Screens.Count - floating.Count} of {displays.Screens.Count} went from the desktop; making them again");
        RemoveDisplays();
        return true;
    }

    private readonly List<double> remakes = new();

    /// <summary>
    /// Places the floating screens to the right of this PC's screen, laid out as they sit in the glasses, and the
    /// glasses' own display to the left of this PC's screen, away from them. True when anything moved. Not in
    /// Steady mode, where the screens are one flat row on the glasses' own canvas.
    /// </summary>
    private bool ArrangeDisplays(List<DisplayInfo> all)
    {
        if (floating.Count == 0 || standIn != null || Displays.Own(all) is not DisplayInfo own) return false;
        Recompute();
        PanelPlacement[] panels;
        lock (frameGate) panels = placements;
        var sizes = floating.Select(d => (Width: d.Width, Height: d.Height)).ToList();
        (int X, int Y)[] origins = DisplayGrid.Origins(State.Steady ? Array.Empty<PanelPlacement>() : panels, sizes);
        DisplayInfo? glasses = Displays.Glasses(all);
        bool already = glasses == null || (glasses.X == own.X - glasses.Width && glasses.Y == own.Y);
        for (int i = 0; i < floating.Count && i < origins.Length; i++)
            already &= floating[i].X == own.X + own.Width + origins[i].X && floating[i].Y == own.Y + origins[i].Y;
        if (already) return false;
        Displays.Arrange(own, glasses, floating, origins);
        return true;
    }

    /// <summary>The layout changed: the floating screens are placed again once it has been still for a moment.</summary>
    private void ArrangeSoon()
    {
        arrangeTimer?.Stop();
        arrangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
        arrangeTimer.Tick += (_, _) =>
        {
            arrangeTimer?.Stop();
            if (!quitting && ArrangeDisplays(Displays.List())) ScreensChanged();
        };
        arrangeTimer.Start();
    }

    /// <summary>Works out the screens' places and how a frame is drawn, from the settings and the glasses.</summary>
    private void Recompute()
    {
        SpaceSettings s = Store.S;
        List<DisplayInfo> shownScreens = floating.Count > 0 ? floating : mirrored != null ? new List<DisplayInfo> { mirrored } : new();
        int count = shownScreens.Count > 0 ? shownScreens.Count : s.Screens;
        var aspects = new List<double>();
        for (int i = 0; i < count; i++)
            aspects.Add(i < shownScreens.Count ? (double)shownScreens[i].Width / Math.Max(shownScreens[i].Height, 1) : 16.0 / 9);
        double fov = GlassesOptics.FovYDeg(glasses);
        double wholeAspect = glassesDisplay != null ? (double)glassesDisplay.Width / Math.Max(glassesDisplay.Height, 1) : 16.0 / 9;
        PanelPlacement[] placed;
        if (State.Steady)
        {
            // The glasses hold one flat picture themselves, the whole display as one wide canvas. The screens are laid
            // in a row that fills it, drawn once, through a narrow lens so they stay near flat.
            fov = 12;
            double hfov = 2 * Math.Atan(Math.Tan(fov * Math.PI / 360) * wholeAspect) * 180 / Math.PI;
            double n = Math.Max(aspects.Count, 1);
            double gap = s.SteadyGapDeg;
            // Bigger and smaller in Steady mode: the row drawn larger or smaller on the glasses' picture.
            double each = Math.Max(2, (hfov * 0.96 - gap * (n - 1)) / n * s.SteadyZoom);
            placed = SpaceLayout.Row(aspects, each, SpaceLayout.DefaultDistance, gap);
        }
        else
        {
            placed = SpaceLayout.Arrange(s.Layout, aspects, s.WidthDeg, s.Distance, s.Curve);
            // Moved by hand: the whole Space turned about the head.
            for (int i = 0; i < placed.Length; i++)
            {
                placed[i].YawDeg += s.OffsetYawDeg + (i < s.ScreenYawDeg.Count ? s.ScreenYawDeg[i] : 0);
                placed[i].PitchDeg += s.OffsetPitchDeg + (i < s.ScreenPitchDeg.Count ? s.ScreenPitchDeg[i] : 0);
            }
        }
        var o = new Render.RenderOptions
        {
            FovYDeg = fov, Ipd = s.IpdMm / 1000, Brightness = s.Brightness, Glow = s.Glow, GlowColor = s.GlowColor.Color(),
            EdgeFade = State.Steady ? EdgeFade.Off : s.EdgeFade, Mono = State.Steady, Focused = pointerScreen,
        };
        lock (frameGate)
        {
            placements = placed;
            trackingOptions = new TrackingOptions(s);
            renderOptions = o;
            frameSteady = State.Steady;
            frameHeadScroll = s.HeadScroll && s.HeadTracking;
        }
        State.Panels = placed.ToArray();
        State.ViewFovYDeg = fov;
        State.ViewAspect = !State.Steady && wholeAspect >= 3.2 ? wholeAspect / 2 : wholeAspect;
        SetScrollRange();
        State.Notify();
    }

    /// <summary>Head scroll slides the row no further than its first and last screens.</summary>
    private void SetScrollRange()
    {
        PanelPlacement[] p = placements;
        if (p.Length > 0) headPose?.SetScrollRange(p[0].YawDeg, p[^1].YawDeg);
    }

    /// <summary>Head scroll in Steady mode starts from "looking at the middle" again.</summary>
    private void ResetSteadyScroll()
    {
        steadyScroll.Stop();
        steadyScrollYaw = 0;
        headPose?.Recentre(0, false);
    }

    // MARK: drawing

    /// <summary>What the next frame shows. Called on the render thread, as late as possible before each frame.</summary>
    private (Quat Head, PanelPlacement[] Panels, Render.RenderOptions Options) Frame()
    {
        PanelPlacement[] shown;
        TrackingOptions options;
        Render.RenderOptions render;
        bool steady, headScroll;
        lock (frameGate)
        {
            shown = (PanelPlacement[])placements.Clone();
            options = trackingOptions;
            render = renderOptions;
            steady = frameSteady;
            headScroll = frameHeadScroll;
        }
        Quat head = Quat.Identity;
        HeadPose? pose = headPose;
        // In Steady mode the glasses hold the picture, so the app draws it fixed to the display.
        if (!steady && pose != null)
        {
            head = pose.DrawPose(Now, framePeriod, options);
            double scroll = pose.ScrollYawDeg;
            if (scroll != 0)
                for (int i = 0; i < shown.Length; i++) shown[i].YawDeg += scroll;
        }
        else if (steady && pose != null && headScroll && shown.Length > 1)
        {
            // Head scroll in Steady mode: the glasses hold the canvas, and the app still knows which way the head has
            // turned since Steady mode came on (or the last Recentre). Past the dead zone the row slides along the
            // canvas, as it does in Smooth Follow. The pose is worked out only to read the turn from it.
            pose.DrawPose(Now, framePeriod, options);
            (double yaw, double pitch) = pose.YawPitchDeg;
            double delta = steadyScroll.Step(-yaw, pitch, framePeriod) * SteadyScrollScale;
            double low = Math.Min(shown[0].YawDeg, shown[^1].YawDeg), high = Math.Max(shown[0].YawDeg, shown[^1].YawDeg);
            steadyScrollYaw = HeadScroll.Clamp(steadyScrollYaw, delta, low, high);
            if (steadyScrollYaw != 0)
                for (int i = 0; i < shown.Length; i++) shown[i].YawDeg += steadyScrollYaw;
        }
        else if (steadyScrollYaw != 0)
        {
            steadyScrollYaw = 0;
            steadyScroll.Stop();
        }
        return (head, shown, render);
    }

    // MARK: the pointer, and what the windows show

    /// <summary>Four times a second: where the pointer is, what the status says.</summary>
    private void Tick()
    {
        if (quitting) return;
        ticks++;
        // Glasses plugged in or out: asked of Windows once a second (a display change is told at once).
        if (ticks % 4 == 0)
        {
            Glasses? found = Glasses.Detect(UsbDevices.List());
            if (!Nullable.Equals(found, glasses)) ScreensChanged();
        }
        LookForDriver();
        TrackPointer();
        PublishDisplayTiles();
        UpdateEdgeHint();
        State.Fps = picture?.Fps ?? 0;
        State.Capturing = picture?.Capturing ?? 0;
        if (headPose is HeadPose pose && fusion is HeadFusion f)
        {
            int hz = pose.ReportsPerSecond;
            State.ImuHz = hz;
            State.TrackingOn = hz > 0;
            State.BiasReady = f.BiasReady;
            if (picture == null) pose.DrawPose(Now, framePeriod, trackingOptions); // keeps the readings alive with no picture
            (State.YawDeg, State.PitchDeg) = pose.YawPitchDeg;
        }
        else
        {
            State.TrackingOn = false;
            State.ImuHz = 0;
        }
        UpdateStatus();
        State.Notify();
    }

    private void TrackPointer()
    {
        if (floating.Count == 0 || Pointer.Position is not (int x, int y))
        {
            if (pointerScreen != null) SetPointerScreen(null);
            return;
        }
        int on = floating.FindIndex(d => d.Contains(x, y));
        int? now = on >= 0 ? on : null;
        if (now != pointerScreen) SetPointerScreen(now);
        if (now != null) State.PointerVisitedScreen = true;
        // The pointer has no business on the glasses' own display, where it cannot be found: it is put back at the
        // nearest point of this PC's screen, so it stops at the edge rather than jumping.
        if (glassesDisplay is DisplayInfo g && standIn == null && g.Contains(x, y) && Displays.Own(Displays.List()) is DisplayInfo own)
            Pointer.MoveTo(Math.Clamp(x, own.X, own.X + own.Width - 1), Math.Clamp(y, own.Y, own.Y + own.Height - 1));
        if (!Store.S.PointerFollowsHead || State.Steady || headPose is not HeadPose pose || now is not int here)
        {
            lookedAt = null;
            return;
        }
        // The head has to rest on another screen for a moment before the pointer goes there.
        if (LookTarget.Hit(HeadQuat(pose), placements) is not (int index, _, _) || index == here || index >= floating.Count)
        {
            lookedAt = null;
            return;
        }
        if (lookedAt is (int was, double since) && was == index)
        {
            if (Now - since >= 0.45)
            {
                Pointer.MoveTo(floating[index], 0.5, 0.5);
                lookedAt = null;
            }
        }
        else
        {
            lookedAt = (index, Now);
        }
    }

    private void SetPointerScreen(int? on)
    {
        pointerScreen = on;
        State.PointerScreen = on;
        lock (frameGate) renderOptions = renderOptions with { Focused = on };
    }

    private static Quat HeadQuat(HeadPose pose)
    {
        (double yaw, double pitch) = pose.YawPitchDeg;
        return Quat.AboutY(yaw * Math.PI / 180) * Quat.AboutX(pitch * Math.PI / 180);
    }

    /// <summary>The pointer goes to the screen the head points at, where it points.</summary>
    private void PointerToLookedScreen()
    {
        if (headPose is not HeadPose pose || State.Steady) return;
        if (LookTarget.Hit(HeadQuat(pose), placements) is (int index, double u, double v) && index < floating.Count)
            Pointer.MoveTo(floating[index], u, v);
    }

    /// <summary>Every display as Windows has it now, for the control panel's map.</summary>
    private void PublishDisplayTiles()
    {
        var tiles = new List<DisplayTile>();
        if (floating.Count > 0)
        {
            if (Displays.Own(Displays.List()) is DisplayInfo own)
                tiles.Add(new DisplayTile(-1, "This PC", new Rect(own.X, own.Y, own.Width, own.Height)));
            for (int i = 0; i < floating.Count; i++)
                tiles.Add(new DisplayTile(i, $"Screen {i + 1}", new Rect(floating[i].X, floating[i].Y, floating[i].Width, floating[i].Height)));
        }
        if (!tiles.SequenceEqual(State.DisplayTiles)) State.DisplayTiles = tiles;
    }

    /// <summary>The glowing tab on the edge of the PC's screen that the floating screens lie beyond, until the pointer
    /// has been across once this session.</summary>
    private void UpdateEdgeHint()
    {
        DisplayInfo? own = floating.Count > 0 && !State.PointerVisitedScreen && !State.PocketMode ? Displays.Own(Displays.List()) : null;
        string? edge = own == null ? null : ControlPanel.MapLayout.EdgeWord(State.DisplayTiles);
        if (own == null || (edge != "right" && edge != "left"))
        {
            edgeHint?.Close();
            edgeHint = null;
            return;
        }
        edgeHint ??= new EdgeHint();
        edgeHint.Show(own, edge == "right");
    }

    private void UpdateStatus()
    {
        if (quitting) return;
        string line;
        if (State.GlassesName is string name)
        {
            var parts = new List<string> { name };
            if (picture == null)
            {
                if (glassesMirrored)
                {
                    // Windows is duplicating: Pocket XR stays off the glasses until they are a screen of their own.
                    parts.Add("the glasses show a copy of this screen: Pocket XR is asking Windows to extend onto them (Win+P, Extend)");
                }
                else
                {
                    // The glasses are on USB but Windows has no display for them: no picture is reaching them.
                    parts.Add(State.TrackingOn ? "head tracking on, no picture reaching the glasses yet" : "no picture reaching the glasses yet");
                }
            }
            else
            {
                if (mirrored != null) parts.Add("showing this PC's screen");
                if (State.Steady) parts.Add("Steady mode");
                else if (State.TrackingOn) parts.Add("head tracking on");
                else if (headPose != null) parts.Add("head tracking starting");
                if (State.Stereo && !State.Steady) parts.Add("3D");
                parts.Add($"{(int)Math.Round(State.Fps)} fps");
            }
            if (State.Busy) parts.Add("changing settings…");
            line = string.Join(" · ", parts);
        }
        else
        {
            line = "Plug in your glasses.";
        }
        if (line != State.StatusLine)
        {
            State.StatusLine = line;
            messages?.SetTip(line.Length > 0 ? "Pocket XR · " + line : "Pocket XR");
        }
    }

    // MARK: actions

    public void Recentre()
    {
        if (State.Steady)
        {
            settings?.RecentreCanvas();
            ResetSteadyScroll();
        }
        else
        {
            // Recentre is "in front of me, now": a move by hand is undone first.
            if (Store.S.OffsetYawDeg != 0 || Store.S.OffsetPitchDeg != 0)
            {
                Store.Update(s =>
                {
                    s.OffsetYawDeg = 0;
                    s.OffsetPitchDeg = 0;
                });
                Recompute();
            }
            (double yawDeg, _) = SpaceLayout.Middle(placements);
            headPose?.Recentre(yawDeg, Store.S.RecentreFollowsTilt);
        }
        State.Recentred = true;
        Log.P("recentre");
        State.Notify();
    }

    public void Adjust(SpaceAdjust a)
    {
        if (State.Steady)
        {
            // The glasses hold one flat picture: size is the only thing to change, and nearer or further are the
            // same thing to the eye. Layout and curve do not apply.
            double by = a switch { SpaceAdjust.Bigger or SpaceAdjust.Nearer => 0.1, SpaceAdjust.Smaller or SpaceAdjust.Further => -0.1, _ => 0 };
            if (by != 0) Store.Update(s => s.SteadyZoom += by);
            return;
        }
        Store.Update(s =>
        {
            switch (a)
            {
                case SpaceAdjust.Bigger: s.WidthDeg += 5; break;
                case SpaceAdjust.Smaller: s.WidthDeg -= 5; break;
                case SpaceAdjust.Nearer: s.Distance -= 0.2; break;
                case SpaceAdjust.Further: s.Distance += 0.2; break;
                case SpaceAdjust.MoreCurve: s.Curve += 0.25; break;
                case SpaceAdjust.LessCurve: s.Curve -= 0.25; break;
                case SpaceAdjust.NextLayout:
                    ArrangeLayout[] choices = SpaceLayout.Choices(Math.Max(State.VirtualScreens, 1));
                    int i = Array.IndexOf(choices, s.Layout);
                    if (choices.Length > 0) s.Layout = choices[(i + 1) % choices.Length];
                    break;
            }
        });
    }

    public void SetSteady(bool on)
    {
        if (settings == null || !State.SteadyAvailable) return;
        Log.P($"Steady mode: asked {(on ? "on" : "off")}");
        // Remembered for the next time the glasses connect or Pocket XR starts.
        if (Store.S.SteadyOn != on) Store.Update(s => s.SteadyOn = on);
        // The user has chosen: nothing is waiting to come back on, and 3D may be asked for again.
        resumingSteady = false;
        modeAskedAt = Now;
        settings.SetSteady(on);
    }

    public void NudgeSpace(double yawDeg, double pitchDeg)
    {
        if (State.Steady) return;
        Store.Update(s =>
        {
            s.OffsetYawDeg += yawDeg;
            s.OffsetPitchDeg += pitchDeg;
        });
    }

    public void NudgeScreen(int index, double yawDeg, double pitchDeg)
    {
        if (State.Steady) return;
        Store.Update(s => s.MoveScreen(index, yawDeg, pitchDeg));
    }

    public void ResetScreenMoves() => Store.Update(s =>
    {
        s.ScreenYawDeg = new List<double>();
        s.ScreenPitchDeg = new List<double>();
    });

    public void MovePointer(int? toScreen)
    {
        if (toScreen is int index)
        {
            if (index >= 0 && index < floating.Count) Pointer.MoveTo(floating[index], 0.5, 0.5);
        }
        else if (Displays.Own(Displays.List()) is DisplayInfo own)
        {
            Pointer.MoveTo(own, 0.5, 0.5);
        }
    }

    public void OpenControlPanel() => ShowWindow(ref controlPanel, () => ControlPanel.Window(Store, State, this), "control panel");
    public void OpenSettings() => ShowWindow(ref settingsWindow, () => SettingsWindow.Window(Store, State, this), "settings");
    public void OpenTour() => ShowWindow(ref tourWindow, () => TourWindow.Window(Store, State, this), "tour");

    /// <summary>Makes the window on first use, in the middle of the PC's own screen, and brings it to the front.</summary>
    private void ShowWindow(ref PxWindow? window, Func<PxWindow> make, string what)
    {
        if (quitting) return;
        try
        {
            if (window == null)
            {
                window = make();
                Rect area = SystemParameters.WorkArea;
                window.Loaded += (s, _) =>
                {
                    var w = (Window)s;
                    w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
                    w.Top = area.Top + Math.Max((area.Height - w.ActualHeight) / 2, 0);
                };
            }
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            // Windows may refuse to bring a window forward (Pocket XR started by itself, at sign-in, or from another
            // program, while the person works in another window): put on top for a moment, it comes up above the
            // others without taking the keyboard from what they are doing. Seen on the Steam Deck, 17 Nov 2026.
            if (!window.Activate())
            {
                window.Topmost = true;
                window.Topmost = false;
            }
        }
        catch (Exception e)
        {
            Log.P($"{what}: could not open: {e}");
        }
    }

    /// <summary>Brings Pocket XR's windows back to the PC's own screen when Windows has moved one onto the glasses'
    /// display or a floating screen, where it cannot be seen.</summary>
    private void KeepWindowsOnOwnScreen()
    {
        if (Displays.Own(Displays.List()) is not DisplayInfo own) return;
        foreach (PxWindow? w in new[] { controlPanel, settingsWindow, tourWindow })
        {
            if (w == null || !w.IsVisible || w.WindowState == WindowState.Minimized) continue;
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect r)) continue;
            int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
            if (own.Contains(midX, midY)) continue;
            Log.P($"{w.Title}: was off the PC's own screen, moved back");
            SetWindowPos(hwnd, IntPtr.Zero, own.X + (own.Width - (r.Right - r.Left)) / 2, own.Y + Math.Max((own.Height - (r.Bottom - r.Top)) / 2, 0), 0, 0, 0x0001 | 0x0004);
        }
    }

    public void ToggleLauncher()
    {
        if (floating.Count == 0) return;
        launcher ??= new LauncherController(Store, State, this);
        launcher.Toggle(floating[Math.Min(pointerScreen ?? 0, floating.Count - 1)]);
        State.LauncherOpen = launcher.IsOpen;
        State.Notify();
    }

    public void SetPocketMode(bool on)
    {
        if (on == State.PocketMode) return;
        // With no floating screen there is nowhere for the pointer to be.
        if (on && floating.Count == 0) return;
        Log.P($"Pocket mode: {(on ? "on" : "off")}");
        pocket ??= new PocketMode(State, this);
        // The control panel is where it was when the PC's screen comes back.
        if (on) panelShownBeforePocket = controlPanel?.IsVisible ?? false;
        pocket.Set(on);
        if (!on && panelShownBeforePocket) OpenControlPanel();
    }

    public void SetHands(bool on)
    {
        Log.P($"hand tracking: asked {(on ? "on" : "off")}");
        if (on) hands?.Start();
        else hands?.Stop();
        State.Notify();
    }

    public void ChooseHandsCamera(int index) => hands?.ChooseCamera(index);
    public void BeginHandCalibration() => hands?.BeginCalibration();
    public void ResetHandCalibration() => hands?.ResetCalibration();
    public void BeginHandReach() => hands?.BeginReach();
    public void ResetHandReach() => hands?.ResetReach();

    // MARK: what hand tracking asks of the app (IHandsHost)

    public bool AimPointer(double headYawDeg, double headPitchDeg)
    {
        // In Steady mode the glasses place the picture themselves, and the app does not know where it sits.
        if (State.Steady || headPose is not HeadPose pose) return false;
        Quat ray = HeadQuat(pose) * (Quat.AboutY(headYawDeg * Math.PI / 180) * Quat.AboutX(headPitchDeg * Math.PI / 180));
        PanelPlacement[] panels;
        lock (frameGate) panels = placements;
        List<DisplayInfo> shown = Shown;
        if (LookTarget.Hit(ray, panels) is not (int index, double u, double v) || index >= shown.Count) return false;
        Pointer.MoveTo(shown[index], u, v);
        return true;
    }

    /// <summary>The screens drawn in the glasses, in the panels' order: the floating screens, or the PC's own screen
    /// when there are none (no virtual display driver, or a stand-in test).</summary>
    private List<DisplayInfo> Shown => floating.Count > 0 ? floating : mirrored is DisplayInfo own ? new List<DisplayInfo> { own } : new();

    public bool PlacePointer(double u, double v)
    {
        List<DisplayInfo> shown = Shown;
        if (shown.Count == 0) return false;
        int index = Math.Min(pointerScreen ?? 0, shown.Count - 1);
        if (!State.Steady && headPose is HeadPose pose)
        {
            PanelPlacement[] panels;
            lock (frameGate) panels = placements;
            if (LookTarget.Hit(HeadQuat(pose), panels) is (int looked, _, _) && looked < shown.Count) index = looked;
        }
        Pointer.MoveTo(shown[index], Math.Clamp(u, 0.002, 0.998), Math.Clamp(v, 0.002, 0.998));
        return true;
    }

    public void PointerButton(bool down) => Pointer.Button(down);

    public DisplayInfo? FloatingScreen(int index) => index >= 0 && index < Shown.Count ? Shown[index] : null;

    public (double Yaw, double Pitch) HeadDeg => headPose is HeadPose pose ? pose.YawPitchDeg : (0, 0);
    public void OpenCameraSettings() => Open("ms-settings:privacy-webcam");

    public void SendLogs() => Platform.SendLogs.Run(State.Version);

    public void SaveGlassesReport()
    {
        List<UsbDeviceInfo> usb = UsbDevices.List();
        List<string> shown = Displays.List().Select(d => $"{(d.Name.Length > 0 ? d.Name : d.GdiName)}: {d.Width} x {d.Height}, {Math.Round(d.RefreshHz)} Hz").ToList();
        string tracking = $"{State.ImuHz} reports a second via {imu?.ConnectedVia ?? hidImu?.ConnectedVia ?? "nothing"}, bias {(State.BiasReady ? "learnt" : "learning")}";
        string text = GlassesReport.Text(State.Version, RuntimeInformation.OSDescription, glasses, usb,
            GlassesNet.Routes().Select(r => r.Description).ToList(), shown, settings == null ? null : settingsState, tracking);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string file = Path.Combine(Platform.SendLogs.Downloads(), $"Pocket XR glasses report {stamp}.txt");
        try
        {
            File.WriteAllText(file, text);
            Log.P("glasses report: saved");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.P($"glasses report: not saved: {e.Message}");
        }
    }

    public void ExportSettings()
    {
        var dialog = new SaveFileDialog { FileName = "Pocket XR settings.json", Filter = "Pocket XR settings (*.json)|*.json", Title = "Save settings to a file" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllBytes(dialog.FileName, Store.Exported()); }
        catch (Exception e) { Log.P($"settings: not saved to a file: {e.Message}"); }
    }

    public void ImportSettings()
    {
        var dialog = new OpenFileDialog { Filter = "Pocket XR settings (*.json)|*.json|All files (*.*)|*.*", Title = "Restore settings from a file" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (!Store.Restore(File.ReadAllBytes(dialog.FileName)))
                MessageBox.Show("That is not a Pocket XR settings file.", "Pocket XR", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e)
        {
            Log.P($"settings: not restored from a file: {e.Message}");
        }
    }

    public void OpenDisplaySettings() => Open("ms-settings:display");
    public void OpenDriverHelp() => ShowWindow(ref driverWindow, () => DriverHelp.Window(State, this), "driver help");

    public void DownloadDriver()
    {
        Log.P("driver: download of Parsec's virtual display driver asked for");
        Open(DriverHelp.DownloadUrl);
    }

    private PxWindow? driverWindow;

    /// <summary>Every few seconds while there is no driver: is one there now? Then the floating screens are made at
    /// once, with nothing to restart.</summary>
    private void LookForDriver()
    {
        if (State.DriverReady || ticks % 12 != 0) return;
        VirtualDisplays.DriverInfo? driver = VirtualDisplays.Probe(out string why);
        if (driver == null)
        {
            State.DriverWhy = why;
            return;
        }
        Log.P($"virtual display driver: found now: {driver.Name}, version {driver.Version}");
        State.DriverReady = true;
        State.DriverWhy = "";
        State.Notify();
        if (glassesDisplay != null && standIn == null)
        {
            // The glasses are showing this PC's screen: their picture is made again, with floating screens.
            picture?.Dispose();
            picture = null;
            glassesDisplay = null;
            ScreensChanged();
        }
    }

    private static void Open(string what)
    {
        try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
        catch (Exception e) { Log.P($"open {what}: {e.Message}"); }
    }

    public void RestartApp()
    {
        Log.P("restart asked");
        bool steady = State.Steady;
        Leave(() =>
        {
            if (Environment.ProcessPath is string exe)
                Process.Start(new ProcessStartInfo(exe, steady ? "--resume-steady" : "") { UseShellExecute = false });
            app.Shutdown();
        });
    }

    public void OpenPcScreen()
    {
        if (!PcScreens.PcScreenLauncher.Available)
        {
            Log.P("pc screens: Moonlight's core (moonlight-pxr.dll) is not beside the app");
            return;
        }
        PcScreens.PcScreenLauncher.OpenNext(floating);
    }

    public void Quit()
    {
        Log.P("quit");
        PcScreens.PcScreenLauncher.CloseAll();
        Leave(() => app.Shutdown());
    }

    /// <summary>Stops drawing, puts the glasses back as they were, then calls [then] on the main thread (within 20 s
    /// at most).</summary>
    private void Leave(Action then)
    {
        if (quitting) return;
        quitting = true;
        // Nothing of Pocket XR's is left over the PC's screen.
        pocket?.Set(false);
        edgeHint?.Close();
        grab?.Stop();
        controllerInput?.Stop();
        launcher?.Close();
        trayMenu?.Close();
        State.StatusLine = "Putting the glasses back…";
        messages?.SetTip("Pocket XR · " + State.StatusLine);
        State.Notify();
        picture?.Dispose();
        picture = null;
        StopTracking();
        hands?.Stop();
        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            RemoveDisplays();
            then();
        }
        if (settings == null)
        {
            Finish();
            return;
        }
        settings.Restore(() => ui.BeginInvoke(Finish));
        // Never hang on glasses that stopped answering. Leaving Steady mode takes more steps, so it gets longer.
        var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(State.Steady ? 30 : 20) };
        limit.Tick += (_, _) =>
        {
            limit.Stop();
            Finish();
        };
        limit.Start();
    }

    public void Dispose()
    {
        quitting = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        timer?.Stop();
        picture?.Dispose();
        displays?.Dispose();
        imu?.Stop();
        hidImu?.Stop();
        settings?.Stop();
        messages?.Dispose();
        showSignal?.Dispose();
        quitSignal?.Dispose();
    }

    // MARK: shortcuts

    private void RegisterShortcuts()
    {
        if (messages == null) return;
        messages.RemoveHotKeys();
        var taken = new HashSet<string>();
        if (Store.S.Shortcuts && !shortcutsPaused)
        {
            for (int i = 0; i < Shortcut.All.Length; i++)
            {
                Shortcut sc = Shortcut.All[i];
                if (!messages.AddHotKey(i + 1, sc.Modifiers, sc.VirtualKey, sc.Repeats, () => RunShortcut(sc.Id)))
                {
                    Log.P($"shortcut {sc.Keys} ({sc.Title}): another app already uses these keys");
                    taken.Add(sc.Id);
                }
            }
        }
        if (!shortcutsPaused)
        {
            State.ShortcutsTaken = taken;
            State.Notify();
        }
    }

    private bool shortcutsPaused;

    public void PauseShortcuts(bool paused)
    {
        if (paused == shortcutsPaused) return;
        shortcutsPaused = paused;
        RegisterShortcuts();
    }

    public string? ChangeShortcut(string id, ShortcutKeys keys)
    {
        Shortcut sc = Shortcut.Find(id);
        if (!keys.Usable) return "Use Ctrl, Alt or the Windows key with it.";
        Shortcut? clash = Array.Find(Shortcut.All, o => o.Id != id && o.Current == keys);
        if (clash != null) return $"{keys.Text} is already {clash.Title}.";
        if (messages != null)
        {
            // Pocket XR's own keys are let go while new ones are pressed, so a refusal here is another app's.
            messages.RemoveHotKeys();
            bool free = messages.KeysFree(keys.Modifiers, keys.VirtualKey);
            RegisterShortcuts();
            if (!free) return $"Another app already uses {keys.Text}. Choose other keys.";
        }
        ShortcutChoices.Set(id, keys);
        Log.P($"shortcut {sc.Title}: now {keys.Text}");
        RegisterShortcuts();
        Store.Update(_ => { }); // nothing changes; the windows show the new keys on the next sync
        State.Notify();
        return null;
    }

    public void ResetShortcut(string? id)
    {
        if (id == null) ShortcutChoices.ResetAll(); else ShortcutChoices.Reset(id);
        Log.P($"shortcut {(id == null ? "every one" : Shortcut.Find(id).Title)}: back to the usual keys");
        RegisterShortcuts();
        State.Notify();
    }

    private void RunShortcut(string id)
    {
        switch (id)
        {
            case "recentre": Recentre(); break;
            case "steady": SetSteady(!State.Steady); break;
            case "launcher": ToggleLauncher(); break;
            case "pocket": SetPocketMode(!State.PocketMode); break;
            case "hands": SetHands(!State.HandsRunning); break;
            case "bigger": Adjust(SpaceAdjust.Bigger); break;
            case "smaller": Adjust(SpaceAdjust.Smaller); break;
            case "nearer": Adjust(SpaceAdjust.Nearer); break;
            case "further": Adjust(SpaceAdjust.Further); break;
            case "moreCurve": Adjust(SpaceAdjust.MoreCurve); break;
            case "lessCurve": Adjust(SpaceAdjust.LessCurve); break;
            case "layout": Adjust(SpaceAdjust.NextLayout); break;
            case "depth": Store.Update(s => s.Want3D = !s.Want3D); break;
            case "pointer": PointerToLookedScreen(); break;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
