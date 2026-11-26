using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Windows;

namespace PocketXR.Hands;

// Hand tracking: a camera sees the hands, and either a ray from the glasses' camera through a steady point on the
// knuckles aims the pointer, or (with a camera that faces the person) the hand moves the pointer as on a trackpad in
// the air; a pinch clicks. This file joins the parts: the camera and detector (HandCamera.cs, HandDetector.cs), the
// logic (PocketXR.Core/Hand*.cs), the app's actions, the settings (Windows/SettingsHands.cs) and the calibration
// (Windows/HandCalibrationWindow.cs). The same as HandTracking.swift on the Mac.

/// <summary>What hand tracking needs from the app.</summary>
public interface IHandsHost
{
    /// <summary>Puts the pointer where a direction this far from the head's points, on a floating screen.</summary>
    bool AimPointer(double headYawDeg, double headPitchDeg);
    /// <summary>Puts the pointer at shares of the floating screen looked at (or the one it is on).</summary>
    bool PlacePointer(double u, double v);
    void PointerButton(bool down);
    void ToggleLauncher();
    void Recentre();
    /// <summary>The floating screen with this index, as Windows has it; null when there is none.</summary>
    DisplayInfo? FloatingScreen(int index);
    /// <summary>The head's yaw and pitch now, degrees.</summary>
    (double Yaw, double Pitch) HeadDeg { get; }
}

/// <summary>What hand tracking keeps for itself, apart from the app's settings: which camera, the hand ray's
/// calibration with a line about how it went, and the air trackpad's reach. In hands.json beside settings.json.</summary>
public sealed class HandsPrefs
{
    public string Camera { get; set; } = "glasses";
    public double[]? Calibration { get; set; }
    public string? CalibrationResult { get; set; }
    public double[]? Reach { get; set; }

    private static string FilePath => Path.Combine(Path.GetDirectoryName(SpaceSettings.SettingsFile)!, "hands.json");

    public static HandsPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<HandsPrefs>(File.ReadAllText(FilePath)) ?? new HandsPrefs();
        }
        catch (Exception e)
        {
            Log.P($"hands: hands.json could not be read, starting afresh: {e.Message}");
        }
        return new HandsPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.P($"hands: hands.json could not be saved: {e.Message}");
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public HandCameraChoice Choice
    {
        get => new(Camera);
        set => Camera = value.Stored;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public HandCalibration? Calibrated => Calibration is { } n ? HandCalibration.FromNumbers(n) : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public HandPoint? ReachCentre => Reach is { Length: 2 } r && double.IsFinite(r[0]) && double.IsFinite(r[1]) ? new HandPoint(r[0], r[1]) : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public HandAirBox AirBox => ReachCentre is HandPoint c ? new HandAirBox(c) : new HandAirBox();
}

/// <summary>The camera's picture for the settings window, while it shows it with a camera that faces the person: a
/// small mirrored copy about 10 times a second, with where the hand's steady point is.</summary>
public static class HandsPreview
{
    public static BitmapSource? Picture;
    /// <summary>The tracked point in the (mirrored) picture, null with no hand.</summary>
    public static HandPoint? Point;
    /// <summary>The settings window is showing the preview now (set by the view itself).</summary>
    public static bool Watching;
    /// <summary>A new picture or point, on the main thread.</summary>
    public static event Action? Changed;

    internal static void Tell() => Changed?.Invoke();
}

public sealed class HandTracking
{
    /// <summary>A camera is looked for every 3 seconds for up to 30, then hand tracking stops with the reason showing.</summary>
    private const double RetryS = 3, GiveUpS = 30;
    /// <summary>A calibration with no pinch for this long is put away.</summary>
    private const double CalibrationIdleS = 30;

    private readonly SettingsStore store;
    private readonly AppState state;
    private readonly IHandsHost host;
    private readonly Dispatcher ui;
    private readonly HandsPrefs prefs = HandsPrefs.Load();

    private bool running;
    private HandCamera? camera;
    private HandDetector? detector;
    private string? detectorError;
    private bool looking;
    private double waitingSince;
    private readonly DispatcherTimer waitTimer, headTimer, watchTimer;
    private List<HandCameraDevice> cameras = new();

    private readonly HandPrimary primary = new();
    private readonly HandAnchor anchor = new();
    private readonly HandRayFilter filter = new();
    private readonly HandGestures gestures = new();
    private bool buttonDown;
    private HandAim aimMode = HandAim.HandRay;
    private double lastFrameMs;
    /// <summary>Look and pinch: the head aims the pointer now (a hand is seen and no pinch is holding it still).</summary>
    private bool headAims;

    private HandCalibrationRun? run;
    private int runScreen;
    private HandCalibrationWindow? runWindow;
    private DispatcherTimer? runTimer, reachTimer;

    private static double NowS => HeadFusion.SystemUptime;

    public HandTracking(SettingsStore store, AppState state, IHandsHost host, Dispatcher ui)
    {
        this.store = store;
        this.state = state;
        this.host = host;
        this.ui = ui;
        state.HandsAvailable = true;
        waitTimer = new DispatcherTimer(TimeSpan.FromSeconds(RetryS), DispatcherPriority.Normal, (_, _) => LookForCamera(), ui);
        waitTimer.Stop();
        // Look and pinch: the pointer goes where the head points, 30 times a second, while a hand is seen.
        headTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 30), DispatcherPriority.Normal, (_, _) => HeadAimTick(), ui);
        headTimer.Stop();
        // Cameras come and go (glasses plugged in, a webcam): the list and the camera in use are kept current.
        watchTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => RefreshCameras(), ui);
        watchTimer.Stop();
        ShowPrefs();
        RefreshCameras();
    }

    public bool IsRunning => running;

    // MARK: the cameras

    /// <summary>Lists the cameras afresh (off the main thread), then shows them and changes over to the glasses'
    /// camera if it has come while another stood in for it.</summary>
    public void RefreshCameras(Action? then = null)
    {
        Task.Run(HandCamera.List).ContinueWith(t => ui.BeginInvoke(() =>
        {
            cameras = t.Result;
            ShowCameras();
            if (running && camera != null && !looking)
            {
                (HandCameraDevice? now, _) = HandCamera.Resolve(prefs.Choice, cameras);
                if (now != null && now.Id != camera.Id)
                {
                    Log.P($"hands: {now.Name} is there now, changing over");
                    CloseCamera();
                    waitingSince = NowS;
                    LookForCamera();
                }
                else if (now == null)
                {
                    CameraUnplugged();
                }
            }
            then?.Invoke();
        }));
    }

    /// <summary>The camera list and choice, as the settings window shows them.</summary>
    private void ShowCameras()
    {
        List<HandCameraDevice> others = HandCamera.Others(cameras);
        (HandCameraDevice? resolved, bool fellBack) = HandCamera.Resolve(prefs.Choice, cameras);
        state.HandsCameras = others.Select(d => d.Name).ToList();
        HandCameraChoice choice = prefs.Choice;
        state.HandsCamera = choice.Kind switch
        {
            HandCameraChoiceKind.Glasses => -1,
            HandCameraChoiceKind.Mac => others.FindIndex(d => d.BuiltIn) is int i and >= 0 ? i : (others.Count > 0 ? 0 : -1),
            _ => others.FindIndex(d => d.Id == choice.Id),
        };
        state.HandsCameraFound = cameras.Any(d => d.Glasses);
        state.HandsFacing = resolved != null ? !resolved.Glasses : choice.Kind != HandCameraChoiceKind.Glasses;
        state.HandsFellBack = fellBack;
        state.Notify();
    }

    private void ShowPrefs()
    {
        state.HandsCalibrated = prefs.Calibrated != null;
        state.HandsCalibrationResult = prefs.CalibrationResult;
        state.HandsReachSet = prefs.ReachCentre != null;
        state.Notify();
    }

    /// <summary>The settings' camera choice: -1 the glasses' own, else an index into AppState.HandsCameras.</summary>
    public void ChooseCamera(int index)
    {
        List<HandCameraDevice> others = HandCamera.Others(cameras);
        HandCameraChoice next = index < 0 || index >= others.Count ? HandCameraChoice.Glasses
            : others[index].BuiltIn ? HandCameraChoice.Mac : HandCameraChoice.Device(others[index].Id);
        if (next == prefs.Choice) return;
        prefs.Choice = next;
        prefs.Save();
        Log.P($"hands: camera chosen: {(index < 0 ? "the glasses'" : others[index].Name)}");
        ShowCameras();
        if (!running) return;
        Log.P("hands: the camera to use changed, starting again");
        Stop();
        Start();
    }

    // MARK: starting and stopping

    public void Start()
    {
        if (running) return;
        running = true;
        waitingSince = NowS;
        state.HandsRunning = true;
        Log.P($"hands: starting ({store.S.HandAim.RawValue()})");
        SetStatus(prefs.Choice.Kind == HandCameraChoiceKind.Glasses ? HandLines.Looking : HandLines.LookingAny);
        watchTimer.Start();
        LookForCamera();
    }

    public void Stop()
    {
        bool was = running;
        running = false;
        waitTimer.Stop();
        watchTimer.Stop();
        EndCalibration(null);
        CloseCamera();
        if (was) Log.P("hands: stopped");
        state.HandsRunning = false;
        state.HandsSeen = 0;
        SetStatus("");
    }

    /// <summary>One look for the camera to use, off the main thread. Opens it when there is one; otherwise says what
    /// is missing and, after 30 seconds of that, stops.</summary>
    private void LookForCamera()
    {
        if (!running || camera != null || looking) return;
        looking = true;
        HandCameraChoice choice = prefs.Choice;
        Task.Run(() =>
        {
            List<HandCameraDevice> all = HandCamera.List();
            (HandCameraDevice? device, _) = HandCamera.Resolve(choice, all);
            if (detector == null && detectorError == null)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                detector = HandDetector.Load(out detectorError);
                Log.P(detector != null ? $"hands: the hand models are loaded ({t.ElapsedMilliseconds} ms)" : $"hands: the hand models could not be loaded: {detectorError}");
            }
            (HandCamera? opened, HandCamera.Failure why) = device != null && detector != null
                ? HandCamera.Open(device, detector) : (null, HandCamera.Failure.None);
            return (all, device, opened, why);
        }).ContinueWith(t => ui.BeginInvoke(() =>
        {
            looking = false;
            (List<HandCameraDevice> all, HandCameraDevice? device, HandCamera? opened, HandCamera.Failure why) = t.Result;
            cameras = all;
            ShowCameras();
            if (!running)
            {
                opened?.Stop();
                return;
            }
            if (opened != null)
            {
                Use(opened);
                return;
            }
            bool ofGlasses = prefs.Choice.Kind == HandCameraChoiceKind.Glasses;
            string waiting = ofGlasses ? HandLines.Looking : HandLines.LookingAny;
            string final = ofGlasses ? HandLines.NoCamera : HandLines.NoCameraAny;
            if (detector == null)
            {
                waiting = final = "Hand tracking could not start: " + (detectorError ?? "its models are missing");
            }
            else if (device != null)
            {
                waiting = final = why == HandCamera.Failure.NotAllowed ? HandLines.NotAllowed : HandLines.NoOpen;
            }
            if (NowS - waitingSince >= GiveUpS || detector == null)
            {
                Log.P($"hands: gave up: {final}");
                running = false;
                waitTimer.Stop();
                watchTimer.Stop();
                state.HandsRunning = false;
                SetStatus(final);
            }
            else
            {
                SetStatus(waiting);
                if (!waitTimer.IsEnabled) waitTimer.Start();
            }
        }));
    }

    private void Use(HandCamera opened)
    {
        camera = opened;
        waitTimer.Stop();
        StartAfresh();
        aimMode = store.S.HandAim;
        opened.OnFrame = frame => ui.BeginInvoke(() =>
        {
            if (camera == opened) Handle(frame);
        });
        opened.OnPreview = (w, h, pixels) => ui.BeginInvoke(() =>
        {
            if (camera != opened || !HandsPreview.Watching) return;
            var picture = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            picture.Freeze();
            HandsPreview.Picture = picture;
            HandsPreview.Tell();
        });
        opened.OnLost = () => ui.BeginInvoke(() =>
        {
            if (camera == opened) CameraUnplugged();
        });
        opened.PreviewWanted = opened.Facing && HandsPreview.Watching;
        SetStatus(SteadyStopsAim(opened.Facing) ? HandLines.Steady : HandLines.Watching);
        headTimer.Start();
    }

    /// <summary>In Steady mode the app does not know where the picture sits, so nothing that aims by angle can aim.
    /// The air trackpad places the pointer on a screen by shares of it, and still works.</summary>
    private bool SteadyStopsAim(bool facing) => state.Steady && !(facing && store.S.HandAim == HandAim.HandRay);

    private void CloseCamera()
    {
        headTimer.Stop();
        camera?.Stop();
        camera = null;
        EndReach();
        if (HandsPreview.Picture != null || HandsPreview.Point != null)
        {
            HandsPreview.Picture = null;
            HandsPreview.Point = null;
            HandsPreview.Tell();
        }
        LetGo();
        StartAfresh();
    }

    /// <summary>The camera went (the glasses were unplugged, or their camera turned off): look for it again, for 30 seconds.</summary>
    private void CameraUnplugged()
    {
        if (!running || camera == null) return;
        Log.P("hands: the camera went away");
        EndCalibration("Calibration stopped: the camera went away.");
        CloseCamera();
        state.HandsSeen = 0;
        waitingSince = NowS;
        LookForCamera();
    }

    private void StartAfresh()
    {
        primary.Reset();
        anchor.Reset();
        filter.Reset();
        gestures.Reset(waitForOpen: true);
        headAims = false;
        detector?.Reset();
    }

    /// <summary>Lets the mouse button go if a pinch has it down.</summary>
    private void LetGo()
    {
        if (!buttonDown) return;
        buttonDown = false;
        host.PointerButton(false);
    }

    private void SetStatus(string line)
    {
        if (state.HandsStatus == line) return;
        state.HandsStatus = line;
        state.Notify();
    }

    // MARK: each frame

    private void Handle(HandCameraFrame frame)
    {
        if (!running || camera is not HandCamera cam) return;
        lastFrameMs = frame.TimeMs;
        bool look = store.S.HandAim == HandAim.LookAndPinch;
        if (store.S.HandAim != aimMode)
        {
            // The way of aiming changed under a hand: let go and start afresh, so nothing is left half done.
            aimMode = store.S.HandAim;
            LetGo();
            StartAfresh();
        }
        if (run != null && (state.Steady || look)) EndCalibration("Calibration stopped: it needs the hand ray, and not Steady mode.");
        // A camera that faces the person: the air trackpad. The hand's place in the picture places the pointer.
        bool air = cam.Facing && !look;
        cam.PreviewWanted = cam.Facing && HandsPreview.Watching;

        int? which = primary.Choose(frame.Hands.Select(h => h.Centre).ToList(), frame.TimeMs);
        HandInput? input = null;
        if (which is int i)
        {
            HandPoints hand = frame.Hands[i];
            HandPoint point = anchor.Point(hand, frame.TimeMs);
            HandAngles aim;
            if (air)
            {
                (double u, double v) = prefs.AirBox.Place(point);
                aim = filter.Filter(HandAirBox.Angles(u, v), frame.TimeMs);
            }
            else
            {
                var model = new HandCameraModel(cam.FovDeg, frame.Aspect);
                HandAngles smooth = filter.Filter(model.Ray(point), frame.TimeMs);
                // While the dots are up the ray is used as the camera gives it: that is what is being measured.
                HandCalibration correction = run == null ? prefs.Calibrated ?? HandCalibration.Identity : HandCalibration.Identity;
                aim = look ? HandAngles.Zero : correction.Apply(smooth);
            }
            if (HandsPreview.Watching)
            {
                HandsPreview.Point = point;
                HandsPreview.Tell();
            }
            (double yaw, double pitch) = host.HeadDeg;
            HandAngles motion = look ? new HandAngles(yaw, pitch) : aim;
            input = new HandInput(hand.PinchRatio, HandPalm.TowardFace(hand, cam.Facing), aim, motion);
        }
        else if (HandsPreview.Point != null)
        {
            HandsPreview.Point = null;
            HandsPreview.Tell();
        }
        foreach (HandEvent e in gestures.Update(input, frame.TimeMs)) Act(e, look, air);
        headAims = look && which != null && !gestures.HoldsPointer;

        int seen = Math.Min(frame.Hands.Count, 2);
        bool changed = state.HandsSeen != seen;
        state.HandsSeen = seen;
        string line = SteadyStopsAim(cam.Facing) ? HandLines.Steady
            : seen == 0 ? (cam.Dark ? HandLines.Dark : HandLines.Watching)
            : seen == 1 ? "1 hand" : "2 hands";
        if (state.HandsStatus != line) SetStatus(line);
        else if (changed) state.Notify();
    }

    private void Act(HandEvent e, bool look, bool air)
    {
        switch (e.Kind)
        {
            case HandEventKind.Aim:
                if (air)
                {
                    (double u, double v) = HandAirBox.Shares(e.Angles);
                    host.PlacePointer(u, v);
                }
                else if (!look)
                {
                    // When the head aims, the 30 a second timer does it, so the pointer is as smooth as the head.
                    host.AimPointer(e.Angles.YawDeg, e.Angles.PitchDeg);
                }
                break;
            case HandEventKind.Press:
                if (run != null) CalibrationPinch(e.Angles);
                else if (state.HandsSettingReach) ReachPinch(e.Angles, air);
                else
                {
                    buttonDown = true;
                    host.PointerButton(true);
                }
                break;
            case HandEventKind.Release:
                LetGo();
                break;
            case HandEventKind.ToggleLauncher:
                if (run == null && !state.HandsSettingReach)
                {
                    Log.P("hands: palm pinch, launcher");
                    host.ToggleLauncher();
                }
                break;
            case HandEventKind.Recentre:
                if (run == null && !state.HandsSettingReach)
                {
                    Log.P("hands: palm pinch held, recentre");
                    host.Recentre();
                }
                break;
            case HandEventKind.HandLost:
                anchor.Reset();
                filter.Reset();
                break;
        }
    }

    private void HeadAimTick()
    {
        if (!running || !headAims || store.S.HandAim != HandAim.LookAndPinch) return;
        // The camera has stopped sending frames: the hand is not known to be there.
        if (HandCamera.Now - lastFrameMs >= 500) return;
        host.AimPointer(0, 0);
    }

    // MARK: calibration

    public void BeginCalibration()
    {
        if (run != null || camera is not HandCamera cam || cam.Facing || SettingsHands.CalibrationBlocker(store, state) != null) return;
        int index = Math.Clamp(state.PointerScreen ?? 0, 0, state.Panels.Count - 1);
        if (host.FloatingScreen(index) is not DisplayInfo screen)
        {
            prefs.CalibrationResult = "Calibration could not find the floating screen to show its dots on.";
            prefs.Save();
            ShowPrefs();
            return;
        }
        Log.P($"hands: calibration on screen {index + 1}");
        LetGo();
        run = new HandCalibrationRun();
        runScreen = index;
        state.HandsCalibrating = true;
        state.Notify();
        runWindow = new HandCalibrationWindow(screen, () => EndCalibration("Calibration stopped."));
        runWindow.Show(0);
        ArmCalibrationTimer();
    }

    public void ResetCalibration()
    {
        Log.P("hands: calibration reset");
        prefs.Calibration = null;
        prefs.CalibrationResult = null;
        prefs.Save();
        ShowPrefs();
    }

    /// <summary>A pinch while the dots are up: the ray as the camera has it, against where the dot is.</summary>
    private void CalibrationPinch(HandAngles ray)
    {
        if (run is not HandCalibrationRun current || current.Dot is not (double U, double V) dot || runScreen >= state.Panels.Count) return;
        (double yaw, double pitch) = host.HeadDeg;
        HandAngles target = HandCalibrationRun.Target(state.Panels[runScreen], dot.U, dot.V, yaw, pitch);
        current.Record(ray, target);
        Log.P(FormattableString.Invariant($"hands: dot {current.Step} ray {ray.YawDeg:0.0}, {ray.PitchDeg:0.0} wanted {target.YawDeg:0.0}, {target.PitchDeg:0.0}"));
        if (!current.Done)
        {
            runWindow?.Show(current.Step);
            ArmCalibrationTimer();
            return;
        }
        HandCalibrationRunResult result = current.Result();
        string off = result.RmsDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Log.P($"hands: calibration {(result.Ok ? "kept" : "not kept")}, {off} degrees off, " +
              string.Join(" ", result.Calibration.Numbers.Select(n => n.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        if (result.Ok)
        {
            prefs.Calibration = result.Calibration.Numbers.ToArray();
            prefs.Save();
            EndCalibration($"Calibrated on {DateTime.Now.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))}: the five dots land within {off} degrees.");
        }
        else
        {
            EndCalibration($"Not kept: the five pinches were {off} degrees off the dots. Try again, with your hand over each dot as you pinch.");
        }
    }

    /// <summary>Puts the dots away. [line] is how it went, null to leave the last line as it is.</summary>
    private void EndCalibration(string? line)
    {
        if (run == null && runWindow == null) return;
        run = null;
        runTimer?.Stop();
        runTimer = null;
        runWindow?.Close();
        runWindow = null;
        state.HandsCalibrating = false;
        if (line != null)
        {
            prefs.CalibrationResult = line;
            prefs.Save();
        }
        ShowPrefs();
        // The pinch that ended it is still closed: start afresh so its opening is not taken for anything.
        gestures.Reset(waitForOpen: true);
    }

    private void ArmCalibrationTimer()
    {
        runTimer?.Stop();
        runTimer = new DispatcherTimer(TimeSpan.FromSeconds(CalibrationIdleS), DispatcherPriority.Normal, (_, _) =>
            EndCalibration("Calibration stopped: no pinch for 30 seconds."), ui);
    }

    // MARK: the air trackpad's reach

    /// <summary>"Set your reach": the next pinch puts the middle of the air trackpad's box where the hand is.</summary>
    public void BeginReach()
    {
        if (!running || camera is not HandCamera cam || !cam.Facing || state.HandsSettingReach) return;
        Log.P("hands: waiting for a pinch to set the reach");
        LetGo();
        state.HandsSettingReach = true;
        state.Notify();
        reachTimer?.Stop();
        reachTimer = new DispatcherTimer(TimeSpan.FromSeconds(CalibrationIdleS), DispatcherPriority.Normal, (_, _) => EndReach(), ui);
    }

    public void ResetReach()
    {
        Log.P("hands: reach reset");
        prefs.Reach = null;
        prefs.Save();
        ShowPrefs();
    }

    /// <summary>The pinch that sets the reach: [at] is where the pointer was aimed, which says where the hand was.</summary>
    private void ReachPinch(HandAngles at, bool air)
    {
        if (air)
        {
            (double u, double v) = HandAirBox.OpenShares(at);
            HandPoint centre = new HandAirBox(prefs.AirBox.Point(u, v)).Centre;
            prefs.Reach = new[] { centre.X, centre.Y };
            prefs.Save();
            Log.P(FormattableString.Invariant($"hands: reach set, the box's middle at {centre.X:0.00}, {centre.Y:0.00} of the picture"));
            // The pointer was where the hand is; the hand is now the screen's middle. Start the smoothing there.
            filter.Reset();
            ShowPrefs();
        }
        EndReach();
    }

    private void EndReach()
    {
        reachTimer?.Stop();
        reachTimer = null;
        if (!state.HandsSettingReach) return;
        state.HandsSettingReach = false;
        state.Notify();
        gestures.Reset(waitForOpen: true);
    }
}
