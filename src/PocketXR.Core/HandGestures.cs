using System;
using System.Collections.Generic;

namespace PocketXR.Core;

// What the hands mean, frame by frame and by the clock, with no camera in it so that the checks can run it:
// which hand has the pointer, and the gestures (aim, press, drag, let go, the launcher, recentre). The behaviour
// and the numbers are the Android app's (core/hands: HandGestureRecognizer, PinchLock), cut down to a first
// version: a pinch is a press, moving is a drag, letting go lets go. There is no scroll gesture.

/// <summary>
/// Which of the hands in a frame has the pointer. The first hand seen keeps it until it has been out of the
/// picture for [lostMs] (500 ms); a second hand is never given it while the first is there or only just gone.
/// The detector gives hands no names from frame to frame, so they are told apart by where they were.
/// </summary>
public sealed class HandPrimary
{
    public double LostMs { get; }
    private HandPoint? primaryAt;
    private double primarySeenMs = 0.0;
    private HandPoint? otherAt;
    private double otherSeenMs = 0.0;

    public HandPrimary(double lostMs = 500)
    {
        LostMs = lostMs;
    }

    /// <summary>
    /// The index, among [hands] (each hand's place in the picture), of the hand that has the pointer in this
    /// frame; null when that hand is not in it.
    /// </summary>
    public int? Choose(IReadOnlyList<HandPoint> hands, double timeMs)
    {
        if (primaryAt != null && timeMs - primarySeenMs >= LostMs) primaryAt = null;
        if (otherAt != null && timeMs - otherSeenMs >= LostMs) otherAt = null;
        if (hands.Count == 0) return null;
        if (primaryAt is not HandPoint was)
        {
            // Nobody has the pointer: the first hand listed takes it. When the other hand was already being
            // followed and is the only one here, that is the hand that takes it.
            primaryAt = hands[0];
            primarySeenMs = timeMs;
            otherAt = hands.Count > 1 ? hands[1] : null;
            otherSeenMs = timeMs;
            return 0;
        }
        if (hands.Count == 1)
        {
            // One hand: the pointer's, unless it is plainly the other hand where that one was.
            if (otherAt is HandPoint other && Gap(hands[0], other) < Gap(hands[0], was))
            {
                otherAt = hands[0];
                otherSeenMs = timeMs;
                return null;
            }
            primaryAt = hands[0];
            primarySeenMs = timeMs;
            return 0;
        }
        int best = 0;
        for (int i = 0; i < hands.Count; i++)
        {
            if (Gap(hands[i], was) < Gap(hands[best], was)) best = i;
        }
        primaryAt = hands[best];
        primarySeenMs = timeMs;
        otherAt = hands[best == 0 ? 1 : 0];
        otherSeenMs = timeMs;
        return best;
    }

    public void Reset()
    {
        primaryAt = null;
        otherAt = null;
    }

    private static double Gap(HandPoint a, HandPoint b) => HandMath.Hypot(a.X - b.X, a.Y - b.Y);
}

/// <summary>One frame of the hand that has the pointer.</summary>
public struct HandInput : IEquatable<HandInput>
{
    /// <summary>Thumb tip to index tip over the hand's size (HandPoints.PinchRatio).</summary>
    public double Ratio;
    /// <summary>The palm is turned to the camera (HandPalm.FacesCamera).</summary>
    public bool PalmFacing;
    /// <summary>Where the pointer would be aimed now.</summary>
    public HandAngles Aim;
    /// <summary>What a drag is measured on: the aim itself when the hand aims, the head's turn when the head does.</summary>
    public HandAngles Motion;

    public HandInput(double ratio, bool palmFacing, HandAngles aim, HandAngles motion)
    {
        Ratio = ratio;
        PalmFacing = palmFacing;
        Aim = aim;
        Motion = motion;
    }

    public static bool operator ==(HandInput a, HandInput b) =>
        a.Ratio == b.Ratio && a.PalmFacing == b.PalmFacing && a.Aim == b.Aim && a.Motion == b.Motion;
    public static bool operator !=(HandInput a, HandInput b) => !(a == b);
    public readonly bool Equals(HandInput other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandInput i && this == i;
    public override readonly int GetHashCode() => HashCode.Combine(Ratio, PalmFacing, Aim, Motion);
}

/// <summary>Which HandEvent it is.</summary>
public enum HandEventKind
{
    /// <summary>Aim the pointer here.</summary>
    Aim,
    /// <summary>Press the button; the pointer was aimed at this just before.</summary>
    Press,
    /// <summary>Let the button go.</summary>
    Release,
    /// <summary>A short pinch with the palm turned to the camera.</summary>
    ToggleLauncher,
    /// <summary>That pinch held.</summary>
    Recentre,
    /// <summary>The hand has been gone long enough to start afresh (after a `release` if it was pressing).</summary>
    HandLost,
}

/// <summary>What a frame leads to, in the order to do it.</summary>
public readonly struct HandEvent : IEquatable<HandEvent>
{
    public HandEventKind Kind { get; }
    /// <summary>Where, for Aim and Press; zero for the others.</summary>
    public HandAngles Angles { get; }

    private HandEvent(HandEventKind kind, HandAngles angles)
    {
        Kind = kind;
        Angles = angles;
    }

    /// <summary>Aim the pointer here.</summary>
    public static HandEvent Aim(HandAngles angles) => new(HandEventKind.Aim, angles);
    /// <summary>Press the button; the pointer was aimed at this just before.</summary>
    public static HandEvent Press(HandAngles angles) => new(HandEventKind.Press, angles);
    /// <summary>Let the button go.</summary>
    public static readonly HandEvent Release = new(HandEventKind.Release, HandAngles.Zero);
    /// <summary>A short pinch with the palm turned to the camera.</summary>
    public static readonly HandEvent ToggleLauncher = new(HandEventKind.ToggleLauncher, HandAngles.Zero);
    /// <summary>That pinch held.</summary>
    public static readonly HandEvent Recentre = new(HandEventKind.Recentre, HandAngles.Zero);
    /// <summary>The hand has been gone long enough to start afresh (after a `release` if it was pressing).</summary>
    public static readonly HandEvent HandLost = new(HandEventKind.HandLost, HandAngles.Zero);

    public static bool operator ==(HandEvent a, HandEvent b) => a.Kind == b.Kind && a.Angles == b.Angles;
    public static bool operator !=(HandEvent a, HandEvent b) => !(a == b);
    public bool Equals(HandEvent other) => this == other;
    public override bool Equals(object? obj) => obj is HandEvent e && this == e;
    public override int GetHashCode() => HashCode.Combine(Kind, Angles);
    public override string ToString() => Kind switch
    {
        HandEventKind.Aim => $"aim{Angles}",
        HandEventKind.Press => $"press{Angles}",
        HandEventKind.Release => "release",
        HandEventKind.ToggleLauncher => "toggleLauncher",
        HandEventKind.Recentre => "recentre",
        _ => "handLost",
    };
}

/// <summary>The numbers the gestures run on. Where they come from on Android is noted on each.</summary>
public sealed record HandGestureSettings
{
    /// <summary>Pinch thresholds (PinchDetector, 0.2.1): start below 0.25 for 2 frames or below 0.15 in 1, end above 0.42.</summary>
    public double EnterRatio { get; init; } = 0.25;
    public double ExitRatio { get; init; } = 0.42;
    public double FastEnterRatio { get; init; } = 0.15;
    /// <summary>The palm must have faced the camera this long before a pinch counts as a palm pinch (PALM_MENU_MS).</summary>
    public double PalmMs { get; init; } = 200.0;
    /// <summary>A palm pinch held this long recentres; let go sooner and it toggles the launcher.</summary>
    public double RecentreHoldMs { get; init; } = 650.0;
    /// <summary>
    /// The aim locks when the fingers start to close: the ratio is below 0.45 and fell 0.08 within 100 ms
    /// (PinchLockSettings onsetRatio, onsetDrop, closingWindowMs).
    /// </summary>
    public double OnsetRatio { get; init; } = 0.45;
    public double OnsetDrop { get; init; } = 0.08;
    public double ClosingWindowMs { get; init; } = 100.0;
    /// <summary>
    /// The lock is put where the pointer was 66 ms before, two frames at 30 a second, about when the fingers began
    /// to move (selectBackMs), but never more than 1.6 degrees from where it is (selectCapPx 50 at 31 px a degree).
    /// </summary>
    public double SelectBackMs { get; init; } = 66.0;
    public double SelectCapDeg { get; init; } = 1.6;
    /// <summary>
    /// A lock that never became a pinch lets go when the fingers reopen: above 0.52, or 0.1 above their tightest
    /// (abortRatio, reopenRise); or after 900 ms (strengthLockTimeoutMs); or, once it has held 250 ms, when the
    /// hand has moved 2.5 degrees on, since a hand moving half closed is aiming and not pinching
    /// (strengthLockMoveDeg, strengthLockMoveAfterMs, 0.5.54).
    /// </summary>
    public double AbortRatio { get; init; } = 0.52;
    public double ReopenRise { get; init; } = 0.1;
    public double LockTimeoutMs { get; init; } = 900.0;
    public double LockMoveDeg { get; init; } = 2.5;
    public double LockMoveAfterMs { get; init; } = 250.0;
    /// <summary>
    /// While pinched the pointer does not move until the hand has gone 2 degrees from where it pressed and the
    /// pinch has lasted 250 ms, or 6 degrees at once: then it is a drag (dragStartDeg 0.5.11, dragMinMs, dragFarDeg).
    /// </summary>
    public double DragStartDeg { get; init; } = 2.0;
    public double DragMinMs { get; init; } = 250.0;
    public double DragFarDeg { get; init; } = 6.0;
    /// <summary>After a lock or a drag the pointer glides back onto the hand over about this long, not in a jump (glideMs).</summary>
    public double GlideMs { get; init; } = 90.0;
    /// <summary>
    /// A hand gone this long is lost: a press is let go and everything starts afresh (the brief's 500 ms; Android's
    /// lostAfterMs is 450). Anything shorter is a dropped frame and is held through.
    /// </summary>
    public double LostMs { get; init; } = 500.0;

    public HandGestureSettings() { }
}

/// <summary>What HandGestures is doing (Swift: HandGestures.Phase).</summary>
public enum HandGesturesPhase
{
    /// <summary>Aiming.</summary>
    Idle,
    /// <summary>The fingers are closing: the pointer is held still for the click that may come.</summary>
    Locked,
    /// <summary>Pinched, the pointer still held where it was locked.</summary>
    Pressed,
    /// <summary>Pinched and moved: the pointer follows the hand with the button down.</summary>
    Dragging,
    /// <summary>Pinched with the palm turned to the camera: the launcher or recentre, never a click.</summary>
    PalmPinch,
}

/// <summary>
/// The gestures of the hand that has the pointer. Feed it every frame, with the hand or with null when the hand is
/// not in the frame, and do what it returns.
/// </summary>
public sealed class HandGestures
{
    public HandGestureSettings Settings { get; }
    public HandGesturesPhase Phase { get; private set; } = HandGesturesPhase.Idle;
    private readonly HandPinchDetector pinch;
    /// <summary>The last 300 ms: when, how closed, and where the pointer was shown.</summary>
    private readonly List<(double T, double Ratio, HandAngles Shown)> history = new();
    private double? lastSeenMs;
    private double? palmSinceMs;
    private HandAngles lockPoint = HandAngles.Zero;
    private HandAngles lockMotion = HandAngles.Zero;
    private double lockSinceMs = 0.0;
    private double lockMin = 0.0;
    private double pressSinceMs = 0.0;
    private HandAngles pressMotion = HandAngles.Zero;
    private bool palmFired = false;
    /// <summary>Added to the aim: what keeps a drag from jumping when it starts, and what then glides away.</summary>
    private HandAngles offset = HandAngles.Zero;
    /// <summary>
    /// After a reset that asked for it: fingers that are closed count as open until they have been seen open, so a
    /// pinch already under way (one that ended a calibration, say) is not taken for a new click.
    /// </summary>
    private bool waitingForOpen = false;

    public HandGestures(HandGestureSettings? settings = null)
    {
        Settings = settings ?? new HandGestureSettings();
        pinch = new HandPinchDetector(Settings.EnterRatio, Settings.ExitRatio, Settings.FastEnterRatio);
    }

    /// <summary>True while the pointer is being held still (a lock, a press that is not yet a drag, a palm pinch).</summary>
    public bool HoldsPointer =>
        Phase == HandGesturesPhase.Locked || Phase == HandGesturesPhase.Pressed || Phase == HandGesturesPhase.PalmPinch;

    /// <summary>The palm has faced the camera long enough that a pinch now would be a palm pinch.</summary>
    public bool PalmReady(double timeMs)
    {
        if (palmSinceMs is not double since) return false;
        return timeMs - since >= Settings.PalmMs;
    }

    public List<HandEvent> Update(HandInput? given, double timeMs)
    {
        double t = timeMs;
        if (given is not HandInput input) return Gone(t);
        HandGestureSettings s = Settings;
        if (waitingForOpen)
        {
            if (input.Ratio > s.ExitRatio) waitingForOpen = false; else input.Ratio = 1;
        }
        double dt = lastSeenMs is double last ? Math.Max(t - last, 0) : 0;
        lastSeenMs = t;
        if (input.PalmFacing)
        {
            if (palmSinceMs == null) palmSinceMs = t;
        }
        else
        {
            palmSinceMs = null;
        }
        double recentMax = input.Ratio;
        bool anyRecent = false;
        foreach (var h in history)
        {
            if (!(h.T >= t - s.ClosingWindowMs)) continue;
            if (!anyRecent || h.Ratio > recentMax) recentMax = h.Ratio;
            anyRecent = true;
        }
        bool wasPalmReady = PalmReady(t);
        bool pinched = pinch.Update(input.Ratio);
        var events = new List<HandEvent>();
        HandAngles shown = input.Aim + offset;

        switch (Phase)
        {
            case HandGesturesPhase.Idle:
            case HandGesturesPhase.Locked:
                if (pinched)
                {
                    if (wasPalmReady)
                    {
                        Phase = HandGesturesPhase.PalmPinch;
                        palmFired = false;
                        pressSinceMs = t;
                        shown = history.Count > 0 ? history[^1].Shown : shown;
                    }
                    else
                    {
                        if (Phase != HandGesturesPhase.Locked) lockPoint = SelectPoint(t, shown);
                        Phase = HandGesturesPhase.Pressed;
                        pressSinceMs = t;
                        pressMotion = input.Motion;
                        shown = lockPoint;
                        events.Add(HandEvent.Aim(lockPoint));
                        events.Add(HandEvent.Press(lockPoint));
                    }
                }
                else if (Phase == HandGesturesPhase.Locked)
                {
                    lockMin = Math.Min(lockMin, input.Ratio);
                    double held = t - lockSinceMs;
                    bool reopened = input.Ratio > s.AbortRatio || input.Ratio >= lockMin + s.ReopenRise;
                    bool movedOn = held >= s.LockMoveAfterMs && input.Motion.Distance(lockMotion) > s.LockMoveDeg;
                    if (reopened || movedOn || held > s.LockTimeoutMs)
                    {
                        // Let go without a jump: the pointer starts from the locked spot and glides onto the hand.
                        Phase = HandGesturesPhase.Idle;
                        offset = lockPoint - input.Aim;
                    }
                    shown = lockPoint;
                }
                else if (input.Ratio < s.OnsetRatio && recentMax - input.Ratio >= s.OnsetDrop)
                {
                    lockPoint = SelectPoint(t, shown);
                    lockMotion = input.Motion;
                    lockSinceMs = t;
                    lockMin = input.Ratio;
                    Phase = HandGesturesPhase.Locked;
                    shown = lockPoint;
                    events.Add(HandEvent.Aim(lockPoint));
                }
                else
                {
                    offset = offset.Scaled(s.GlideMs <= 0 ? 0 : Math.Exp(-dt / s.GlideMs));
                    shown = input.Aim + offset;
                    events.Add(HandEvent.Aim(shown));
                }
                break;

            case HandGesturesPhase.Pressed:
                if (!pinched)
                {
                    Phase = HandGesturesPhase.Idle;
                    offset = lockPoint - input.Aim;
                    shown = lockPoint;
                    events.Add(HandEvent.Release);
                }
                else
                {
                    double moved = input.Motion.Distance(pressMotion);
                    double held = t - pressSinceMs;
                    if ((moved >= s.DragStartDeg && held >= s.DragMinMs) || moved >= s.DragFarDeg)
                    {
                        // The drag starts from the locked spot: the pointer does not jump to the hand.
                        Phase = HandGesturesPhase.Dragging;
                        offset = lockPoint - input.Aim;
                        events.Add(HandEvent.Aim(lockPoint));
                    }
                    shown = lockPoint;
                }
                break;

            case HandGesturesPhase.Dragging:
                shown = input.Aim + offset;
                events.Add(HandEvent.Aim(shown));
                if (!pinched)
                {
                    Phase = HandGesturesPhase.Idle;
                    events.Add(HandEvent.Release);
                }
                break;

            case HandGesturesPhase.PalmPinch:
                shown = history.Count > 0 ? history[^1].Shown : shown;
                if (!pinched)
                {
                    if (!palmFired) events.Add(HandEvent.ToggleLauncher);
                    Phase = HandGesturesPhase.Idle;
                    offset = shown - input.Aim;
                }
                else if (!palmFired && t - pressSinceMs >= s.RecentreHoldMs)
                {
                    palmFired = true;
                    events.Add(HandEvent.Recentre);
                }
                break;
        }

        history.Add((t, input.Ratio, shown));
        history.RemoveAll(h => h.T < t - 300);
        return events;
    }

    /// <summary>Where the pointer was [selectBackMs] ago, or where it is now when that is further off than the cap.</summary>
    private HandAngles SelectPoint(double t, HandAngles shown)
    {
        double back = t - Settings.SelectBackMs;
        if (history.Count == 0) return shown;
        var sample = history[0];
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].T <= back)
            {
                sample = history[i];
                break;
            }
        }
        return sample.Shown.Distance(shown) <= Settings.SelectCapDeg ? sample.Shown : shown;
    }

    /// <summary>A frame without the hand.</summary>
    private List<HandEvent> Gone(double t)
    {
        if (lastSeenMs is not double seen || !(t - seen >= Settings.LostMs)) return new List<HandEvent>();
        var events = new List<HandEvent>();
        if (Phase == HandGesturesPhase.Pressed || Phase == HandGesturesPhase.Dragging) events.Add(HandEvent.Release);
        Reset();
        events.Add(HandEvent.HandLost);
        return events;
    }

    /// <summary>
    /// Starts afresh. A press that is down is NOT let go here: whoever resets while pressed lets it go.
    /// With [waitForOpen], fingers that are closed now do nothing until they have opened.
    /// </summary>
    public void Reset(bool waitForOpen = false)
    {
        waitingForOpen = waitForOpen;
        Phase = HandGesturesPhase.Idle;
        pinch.Reset();
        history.Clear();
        lastSeenMs = null;
        palmSinceMs = null;
        palmFired = false;
        offset = HandAngles.Zero;
    }
}
