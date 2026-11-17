using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PocketXR.Core;

namespace PocketXR.Platform;

// A game controller as a remote for the Space: lean back with the glasses on and move, resize and recentre the
// screens without the keyboard. A controller already works in games on a PC, so by default it changes the Space
// only while the left stick is held pressed in, and a game on a floating screen keeps the controller to itself.
//
// Read through Windows' XInput (xinput1_4.dll, part of Windows 8 and later): Xbox controllers and those that present
// themselves as one. XInput tells nothing by itself, so the controller is asked 60 times a second while one is
// connected and once a second while none is.
//
// Not tried with a controller on a PC yet: the arithmetic is checked (ControllerChecks), the reading of a real
// controller is not.
//
// It does not rumble and it does not click for you.

/// <summary>A game controller that changes the Space.</summary>
public sealed class ControllerInput
{
    private readonly SettingsStore store;
    private readonly AppState state;
    private readonly IAppActions actions;
    private DispatcherTimer? timer;
    /// <summary>The controller in use (XInput's slot 0 to 3), -1 with none.</summary>
    private int pad = -1;
    private uint lastPacket;
    private ControllerSnapshot snapshot = new();
    private ControllerButtons before;
    private double lastTick;
    private bool wasContinuous;
    private ControllerPacer widthPacer = new();
    private ControllerRepeat repeatUp = new(), repeatDown = new(), repeatLeft = new(), repeatRight = new();
    /// <summary>Where a bumper last sent the pointer, and when.</summary>
    private (int? To, double At)? stepped;
    private bool unavailable;

    public ControllerInput(SettingsStore store, AppState state, IAppActions actions)
    {
        this.store = store;
        this.state = state;
        this.actions = actions;
    }

    public void Start()
    {
        if (timer != null) return;
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    public void Stop()
    {
        timer?.Stop();
        timer = null;
    }

    private static double Now => HeadFusion.SystemUptime;

    private bool IsActive => ControllerLogic.Active(store.S.ControllerMode, snapshot.Buttons.Contains(ControllerButtons.LeftStick));

    private void Tick()
    {
        if (unavailable || timer == null) return;
        XInputState raw = default;
        try
        {
            if (pad < 0 || XInputGetState(pad, out raw) != 0)
            {
                // The controller in use went, or there was none: look at all four slots.
                int found = -1;
                for (int i = 0; i < 4 && found < 0; i++)
                    if (XInputGetState(i, out raw) == 0) found = i;
                if (found != pad)
                {
                    Log.P(found >= 0 ? $"controller: connected (XInput slot {found})" : "controller: disconnected");
                    pad = found;
                    snapshot = new ControllerSnapshot();
                    before = default;
                    lastPacket = 0;
                    wasContinuous = false;
                    if (widthPacer.Flush() is double rest) Resize(rest);
                    state.ControllerName = found >= 0 ? "Game controller" : null;
                    state.Notify();
                    timer.Interval = TimeSpan.FromSeconds(found >= 0 ? 1.0 / 60 : 1);
                }
                if (pad < 0) return;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.P("controller: Windows' XInput is not on this PC; game controllers are left alone");
            unavailable = true;
            state.ControllerAvailable = false;
            state.Notify();
            Stop();
            return;
        }

        double now = Now;
        // A stalled main thread must not turn into one large jump.
        double dt = Math.Clamp(now - lastTick, 0, 0.05);
        lastTick = now;
        bool changed = raw.PacketNumber != lastPacket;
        lastPacket = raw.PacketNumber;
        if (!changed && !wasContinuous && !AnyRepeatHeld()) return;

        snapshot = Look(raw.Gamepad);
        ControllerButtons pressed = ControllerLogic.Pressed(before, snapshot.Buttons);
        before = snapshot.Buttons;
        if (!IsActive)
        {
            EndHold(now);
            return;
        }
        if (!pressed.IsEmpty) Act(pressed);
        RunRepeats(now);
        bool continuous = ControllerLogic.Continuous(snapshot);
        if (continuous)
        {
            (double yawDeg, double pitchDeg) = ControllerLogic.Move(snapshot, dt);
            if (yawDeg != 0 || pitchDeg != 0) actions.NudgeSpace(yawDeg, pitchDeg);
            double grow = ControllerLogic.Grow(snapshot, dt);
            if (widthPacer.Add(grow, now) is double amount) Resize(amount);
        }
        else if (wasContinuous && widthPacer.Flush() is double rest)
        {
            Resize(rest);
        }
        wasContinuous = continuous;
    }

    private bool AnyRepeatHeld() => !(snapshot.Buttons & ControllerButtons.Dpad).IsEmpty;

    /// <summary>Nothing is held any more as far as the Space goes.</summary>
    private void EndHold(double now)
    {
        if (widthPacer.Flush() is double rest) Resize(rest);
        wasContinuous = false;
        repeatUp.Fires(false, now);
        repeatDown.Fires(false, now);
        repeatLeft.Fires(false, now);
        repeatRight.Fires(false, now);
    }

    private static ControllerSnapshot Look(XInputGamepad g)
    {
        ControllerButtons b = default;
        void If(ushort mask, ControllerButtons button) { if ((g.Buttons & mask) != 0) b |= button; }
        If(0x1000, ControllerButtons.South);
        If(0x2000, ControllerButtons.East);
        If(0x4000, ControllerButtons.West);
        If(0x8000, ControllerButtons.North);
        If(0x0100, ControllerButtons.LeftBumper);
        If(0x0200, ControllerButtons.RightBumper);
        If(0x0010, ControllerButtons.Menu);
        If(0x0040, ControllerButtons.LeftStick);
        If(0x0001, ControllerButtons.Up);
        If(0x0002, ControllerButtons.Down);
        If(0x0004, ControllerButtons.Left);
        If(0x0008, ControllerButtons.Right);
        return new ControllerSnapshot(b, Math.Max(g.RightX / 32767.0, -1), Math.Max(g.RightY / 32767.0, -1),
            g.LeftTrigger / 255.0, g.RightTrigger / 255.0);
    }

    /// <summary>The buttons that act once, on the press.</summary>
    private void Act(ControllerButtons pressed)
    {
        if (pressed.Contains(ControllerButtons.South)) actions.Recentre();
        if (pressed.Contains(ControllerButtons.East)) actions.Adjust(SpaceAdjust.NextLayout);
        if (pressed.Contains(ControllerButtons.West) && state.SteadyAvailable && state.LinkOpen) actions.SetSteady(!state.Steady);
        if (pressed.Contains(ControllerButtons.North)) actions.ToggleLauncher();
        if (pressed.Contains(ControllerButtons.Menu)) actions.OpenControlPanel();
        if (pressed.Contains(ControllerButtons.RightBumper)) StepPointer(forward: true);
        if (pressed.Contains(ControllerButtons.LeftBumper)) StepPointer(forward: false);
    }

    /// <summary>The pointer to the next or the previous floating screen, round through the PC's own screen (which is
    /// left out in Pocket mode, where the pointer is kept off it).</summary>
    private void StepPointer(bool forward)
    {
        int floating = state.DisplayTiles.Count(t => !t.IsOwn);
        if (floating <= 0) return;
        // The app looks where the pointer is four times a second; a second press inside that counts from where the
        // first one sent it.
        double now = Now;
        int? current = state.PointerScreen;
        if (stepped is { } last && now - last.At < 0.5) current = last.To;
        int? next = ControllerLogic.NextScreen(current, floating, forward, includeOwn: !state.PocketMode);
        actions.MovePointer(next);
        stepped = (next, now);
    }

    /// <summary>The D-pad: at once, then every quarter second while held.</summary>
    private void RunRepeats(double now)
    {
        ControllerButtons b = snapshot.Buttons;
        bool up = repeatUp.Fires(b.Contains(ControllerButtons.Up), now);
        bool down = repeatDown.Fires(b.Contains(ControllerButtons.Down), now);
        bool left = repeatLeft.Fires(b.Contains(ControllerButtons.Left), now);
        bool right = repeatRight.Fires(b.Contains(ControllerButtons.Right), now);
        if (up) actions.Adjust(SpaceAdjust.Further);
        if (down) actions.Adjust(SpaceAdjust.Nearer);
        if (left) actions.Adjust(SpaceAdjust.LessCurve);
        if (right) actions.Adjust(SpaceAdjust.MoreCurve);
    }

    private void Resize(double amount)
    {
        // In Steady mode size is the row's share of the glasses' picture.
        if (state.Steady) store.Update(s => s.SteadyZoom += amount / 40);
        else store.Update(s => s.WidthDeg += amount);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int index, out XInputState state);
}
