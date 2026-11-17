using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using PocketXR.Core;

namespace PocketXR;

/// <summary>
/// The settings, kept as one JSON file (%LOCALAPPDATA%\Pocket XR\settings.json). The windows read S and change it
/// through Update; the controller listens to Changed.
/// </summary>
public sealed class SettingsStore
{
    private readonly bool persist;

    public SpaceSettings S { get; private set; }

    /// <summary>The settings changed: the old value, then the new one. Raised on the thread that made the change.</summary>
    public event Action<SpaceSettings, SpaceSettings>? Changed;

    /// <summary>[persist] false keeps the settings in memory only (windows drawn to picture files).</summary>
    public SettingsStore(bool persist = true)
    {
        this.persist = persist;
        S = persist ? SpaceSettings.Load().Clamped : new SpaceSettings();
    }

    /// <summary>Changes a copy of the settings with [change], then takes it if anything is different.</summary>
    public void Update(Action<SpaceSettings> change)
    {
        SpaceSettings next = S.Copy();
        change(next);
        Set(next);
    }

    public void Set(SpaceSettings next)
    {
        next = next.Clamped;
        if (next == S) return;
        SpaceSettings old = S;
        S = next;
        if (persist) next.Save();
        Changed?.Invoke(old, next);
    }

    /// <summary>The settings as a file's contents (Save settings to a file).</summary>
    public byte[] Exported() => S.Encode(pretty: true);

    /// <summary>Takes settings from a file's contents; false if it is not a Pocket XR settings file.</summary>
    public bool Restore(byte[] data)
    {
        SpaceSettings? loaded = SpaceSettings.TryDecode(data);
        if (loaded == null) return false;
        Set(loaded);
        return true;
    }

    public void Reset()
    {
        var fresh = new SpaceSettings { TourSeen = S.TourSeen };
        Set(fresh);
    }
}

/// <summary>One display on the map of displays: the PC's own screen or a floating screen. The glasses' own display is
/// left out, as nothing is to be done on it.</summary>
public sealed record DisplayTile(
    /// <summary>-1 the PC's own screen, else the floating screen's index (0 is "Screen 1").</summary>
    int Id,
    string Title,
    /// <summary>Where Windows has it, in pixels: x right, y down.</summary>
    Rect Frame)
{
    public bool IsOwn => Id < 0;
}

/// <summary>What is true right now, for the windows to show. Written only by the controller, on the main thread, which
/// then calls Notify once.</summary>
public sealed class AppState
{
    /// <summary>Something here changed.</summary>
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    /// <summary>The glasses' name, null with none plugged in.</summary>
    public string? GlassesName;
    public Glasses? Glasses;
    /// <summary>The glasses' display as Windows has it, such as "3840 x 1080, 60 Hz"; null while it is not there.</summary>
    public string? DisplayText;
    /// <summary>The glasses' picture is 3D side by side now.</summary>
    public bool Stereo;
    /// <summary>Frames drawn on the glasses a second.</summary>
    public double Fps;
    /// <summary>Head tracking reports are arriving.</summary>
    public bool TrackingOn;
    /// <summary>Head tracking reports a second.</summary>
    public int ImuHz;
    /// <summary>The gyro's bias has been learnt from stillness.</summary>
    public bool BiasReady;
    /// <summary>Where the head points against the recentred forward, degrees (left and up positive).</summary>
    public double YawDeg, PitchDeg;
    /// <summary>The glasses' settings link is open.</summary>
    public bool LinkOpen;
    /// <summary>The glasses' settings are being changed.</summary>
    public bool Busy;
    /// <summary>Steady mode is on (the glasses hold the picture themselves).</summary>
    public bool Steady;
    /// <summary>These glasses can do Steady mode.</summary>
    public bool SteadyAvailable;
    /// <summary>A virtual display driver Pocket XR can use is installed (floating screens can be made).</summary>
    public bool DriverReady;
    /// <summary>With no floating screens, the glasses show the PC's own screen as the one panel.</summary>
    public bool ShowingOwnScreen;
    /// <summary>Shortcuts Windows would not give Pocket XR because another app already uses their keys (by id).</summary>
    public HashSet<string> ShortcutsTaken = new();
    /// <summary>Why not, in plain words, when DriverReady is false.</summary>
    public string DriverWhy = "";
    /// <summary>Floating screens (virtual displays) that exist now.</summary>
    public int VirtualScreens;
    /// <summary>Screens captured and showing a picture.</summary>
    public int Capturing;
    /// <summary>The pointer has been on a floating screen this session (a tick in the tour).</summary>
    public bool PointerVisitedScreen;
    public bool Recentred;
    /// <summary>One line saying where things stand, as the tray icon shows it.</summary>
    public string StatusLine = "Plug in your glasses.";
    /// <summary>The screen count was changed and needs Pocket XR opened again.</summary>
    public bool NeedsRestart;
    /// <summary>The screens as they are drawn now: where each sits in the Space (moved by hand included).</summary>
    public IReadOnlyList<PanelPlacement> Panels = Array.Empty<PanelPlacement>();
    /// <summary>The glasses' view: each eye's field of view up and down, degrees, and its width over its height.</summary>
    public double ViewFovYDeg = 38.0;
    public double ViewAspect = 16.0 / 9;
    /// <summary>Every display as Windows has arranged it, for a map of where the pointer can go.</summary>
    public IReadOnlyList<DisplayTile> DisplayTiles = Array.Empty<DisplayTile>();
    /// <summary>The floating screen the pointer is on (its index), null on the PC's own screen.</summary>
    public int? PointerScreen;
    /// <summary>The launcher is showing on a floating screen.</summary>
    public bool LauncherOpen;
    /// <summary>Pocket mode: the PC's own screens are black and the pointer is kept on the floating screens.</summary>
    public bool PocketMode;
    /// <summary>A game controller is connected: its name, else null.</summary>
    public string? ControllerName;
    /// <summary>Hand tracking: running, how many hands are seen now, and one line on where it stands.</summary>
    public bool HandsRunning;
    public int HandsSeen;
    public string HandsStatus = "";
    /// <summary>The glasses have a camera Windows can open.</summary>
    public bool HandsCameraFound;
    /// <summary>Hand tracking exists in this version of Pocket XR (false until it is built for Windows: the settings
    /// window then says so and its buttons that start it are off).</summary>
    public bool HandsAvailable;
    /// <summary>Game controllers are read in this version of Pocket XR (false until that is built for Windows).</summary>
    public bool ControllerAvailable;
    /// <summary>The cameras that are not the glasses', by name, as Windows lists them now.</summary>
    public IReadOnlyList<string> HandsCameras = Array.Empty<string>();
    /// <summary>The camera chosen for hand tracking: -1 the glasses' own, else an index into HandsCameras.</summary>
    public int HandsCamera = -1;
    /// <summary>The camera in use faces the person: the air trackpad, not the hand ray.</summary>
    public bool HandsFacing;
    /// <summary>The glasses' camera was chosen, there is none, and another camera is used in its place.</summary>
    public bool HandsFellBack;
    /// <summary>The hand aim's calibration: its dots are up now, one has been kept, and how the last one went in one line.</summary>
    public bool HandsCalibrating;
    public bool HandsCalibrated;
    public string? HandsCalibrationResult;
    /// <summary>The air trackpad's reach: waiting for the pinch that sets it, and one has been set.</summary>
    public bool HandsSettingReach;
    public bool HandsReachSet;

    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";
}

/// <summary>A change to the Space made from the tray menu, a shortcut or a window.</summary>
public enum SpaceAdjust { Bigger, Smaller, Nearer, Further, MoreCurve, LessCurve, NextLayout }

/// <summary>What the windows can ask the app to do.</summary>
public interface IAppActions
{
    void Recentre();
    void Adjust(SpaceAdjust a);
    void SetSteady(bool on);
    /// <summary>Moves the whole Space by hand, degrees (left and up positive). Added to what is already there.</summary>
    void NudgeSpace(double yawDeg, double pitchDeg);
    /// <summary>Moves one screen by hand (its index), degrees, added to where it already is.</summary>
    void NudgeScreen(int index, double yawDeg, double pitchDeg);
    /// <summary>Puts every screen moved by hand back in its place in the layout.</summary>
    void ResetScreenMoves();
    /// <summary>Puts the pointer in the middle of a floating screen (its index), or of the PC's own screen with null.</summary>
    void MovePointer(int? toScreen);
    void OpenControlPanel();
    /// <summary>Shows the launcher on the floating screen the pointer is on, or hides it.</summary>
    void ToggleLauncher();
    void SetPocketMode(bool on);
    /// <summary>Starts or stops hand tracking now.</summary>
    void SetHands(bool on);
    void OpenTour();
    void OpenSettings();
    void SendLogs();
    void SaveGlassesReport();
    void ExportSettings();
    void ImportSettings();
    /// <summary>Opens Windows' own display settings.</summary>
    void OpenDisplaySettings();
    /// <summary>Opens the window that tells how to get the virtual display driver floating screens need.</summary>
    void OpenDriverHelp();
    /// <summary>Starts the download of Parsec's virtual display driver in the browser.</summary>
    void DownloadDriver();
    void RestartApp();
    void Quit();
    /// <summary>Chooses the camera for hand tracking: -1 the glasses' own, else an index into AppState.HandsCameras.</summary>
    void ChooseHandsCamera(int index);
    /// <summary>Shows the hand aim's five dots on the floating screen the pointer is on, or puts the calibration back to the estimate.</summary>
    void BeginHandCalibration();
    void ResetHandCalibration();
    /// <summary>Waits for the pinch that sets the air trackpad's reach, or puts its box back where it rests.</summary>
    void BeginHandReach();
    void ResetHandReach();
    /// <summary>Opens Windows Settings at the camera's privacy page.</summary>
    void OpenCameraSettings();
    /// <summary>Gives a shortcut new keys. Null when it was done, else why not, in plain words.</summary>
    string? ChangeShortcut(string id, ShortcutKeys keys);
    /// <summary>Puts a shortcut back to the keys it comes with ([id] null: every shortcut).</summary>
    void ResetShortcut(string? id);
    /// <summary>Lets go of every shortcut while new keys are being pressed in Settings (so Pocket XR's own keys can
    /// be pressed there too), or takes them again.</summary>
    void PauseShortcuts(bool paused);
}

/// <summary>Actions that do nothing: for windows drawn to picture files.</summary>
public sealed class NoActions : IAppActions
{
    public void Recentre() { }
    public void Adjust(SpaceAdjust a) { }
    public void SetSteady(bool on) { }
    public void NudgeSpace(double yawDeg, double pitchDeg) { }
    public void NudgeScreen(int index, double yawDeg, double pitchDeg) { }
    public void ResetScreenMoves() { }
    public void MovePointer(int? toScreen) { }
    public void OpenControlPanel() { }
    public void ToggleLauncher() { }
    public void SetPocketMode(bool on) { }
    public void SetHands(bool on) { }
    public void OpenTour() { }
    public void OpenSettings() { }
    public void SendLogs() { }
    public void SaveGlassesReport() { }
    public void ExportSettings() { }
    public void ImportSettings() { }
    public void OpenDisplaySettings() { }
    public void OpenDriverHelp() { }
    public void DownloadDriver() { }
    public void RestartApp() { }
    public void Quit() { }
    public void ChooseHandsCamera(int index) { }
    public void BeginHandCalibration() { }
    public void ResetHandCalibration() { }
    public void BeginHandReach() { }
    public void ResetHandReach() { }
    public void OpenCameraSettings() { }
    public string? ChangeShortcut(string id, ShortcutKeys keys) => null;
    public void ResetShortcut(string? id) { }
    public void PauseShortcuts(bool paused) { }
}

/// <summary>The shortcuts, in one place for the tray menu, the settings window and the tour to show. Each comes as
/// Ctrl+Alt and a key and works in any app; the keys can be changed in Settings (ShortcutChoices).</summary>
public sealed record Shortcut(string Id, string Title, ShortcutKeys Usual, bool Repeats = false)
{
    /// <summary>The keys in use now: the person's choice, else the usual ones.</summary>
    public ShortcutKeys Current => ShortcutChoices.Get(Id) ?? Usual;
    public string Keys => Current.Text;
    public uint VirtualKey => Current.VirtualKey;
    public uint Modifiers => Current.Modifiers;
    public bool Changed => Current != Usual;

    public static readonly Shortcut[] Defaults =
    {
        new("recentre", "Recentre", ShortcutKeys.CtrlAlt(0x52)),
        new("steady", "Steady mode", ShortcutKeys.CtrlAlt(0x53)),
        new("bigger", "Bigger", ShortcutKeys.CtrlAlt(0xBB), true),
        new("smaller", "Smaller", ShortcutKeys.CtrlAlt(0xBD), true),
        new("nearer", "Nearer", ShortcutKeys.CtrlAlt(0xDD), true),
        new("further", "Further", ShortcutKeys.CtrlAlt(0xDB), true),
        new("moreCurve", "More curve", ShortcutKeys.CtrlAlt(0xBE), true),
        new("lessCurve", "Less curve", ShortcutKeys.CtrlAlt(0xBC), true),
        new("layout", "Next layout", ShortcutKeys.CtrlAlt(0x4C)),
        new("depth", "3D on or off", ShortcutKeys.CtrlAlt(0x44)),
        new("pointer", "Pointer to the screen I look at", ShortcutKeys.CtrlAlt(0x50)),
        new("launcher", "Launcher", ShortcutKeys.CtrlAlt(0x20)),
        new("pocket", "Pocket mode", ShortcutKeys.CtrlAlt(0x4B)),
        new("hands", "Hand tracking", ShortcutKeys.CtrlAlt(0x48)),
    };

    public static Shortcut[] All => Defaults;

    public static Shortcut Find(string id) => Array.Find(Defaults, s => s.Id == id) ?? Defaults[0];
}

/// <summary>How numbers are said in the windows.</summary>
public static class SettingsFormat
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static string Degrees(double v) => Math.Round(v).ToString("0", Inv) + "°";
    public static string Metres(double v) => v.ToString("0.0", Inv) + " m";
    /// <summary>"Flat" at 0, else how far towards the strongest curve.</summary>
    public static string Curve(double v)
    {
        double top = SpaceSettings.CurveRange.UpperBound;
        return v <= 0.0001 || top <= 0 ? "Flat" : Math.Round(v / top * 100).ToString("0", Inv) + " %";
    }
    /// <summary>"Off" at 0, else a percentage.</summary>
    public static string Glow(double v) => v <= 0.0001 ? "Off" : Percent(v);
    public static string SignedDegrees(double v) => v.ToString("+0.0;-0.0", Inv) + "°";
    public static string Percent(double v) => Math.Round(v * 100).ToString("0", Inv) + " %";
    public static string Millimetres(double v) => Math.Round(v).ToString("0", Inv) + " mm";
    public static string Milliseconds(double v) => Math.Round(v).ToString("0", Inv) + " ms";
}
