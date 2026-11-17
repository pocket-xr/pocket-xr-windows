using System;

namespace PocketXR.Core;

// A game controller as a remote for the Space, and Pocket mode's watch: the parts that are only arithmetic, so they
// can be checked without a controller, a screen or glasses (PocketXR.Checks/ControllerChecks.cs).

/// <summary>A controller's buttons by where they sit, not by the letter printed on them (the letters differ by maker).</summary>
public readonly struct ControllerButtons : IEquatable<ControllerButtons>
{
    public ushort RawValue { get; }
    public ControllerButtons(ushort rawValue) { RawValue = rawValue; }

    /// <summary>The face buttons: bottom, right, left, top.</summary>
    public static readonly ControllerButtons South = new(1 << 0);
    public static readonly ControllerButtons East = new(1 << 1);
    public static readonly ControllerButtons West = new(1 << 2);
    public static readonly ControllerButtons North = new(1 << 3);
    /// <summary>The bumpers (L1, R1).</summary>
    public static readonly ControllerButtons LeftBumper = new(1 << 4);
    public static readonly ControllerButtons RightBumper = new(1 << 5);
    /// <summary>Menu, Options or +.</summary>
    public static readonly ControllerButtons Menu = new(1 << 6);
    /// <summary>The left stick pressed in.</summary>
    public static readonly ControllerButtons LeftStick = new(1 << 7);
    public static readonly ControllerButtons Up = new(1 << 8);
    public static readonly ControllerButtons Down = new(1 << 9);
    public static readonly ControllerButtons Left = new(1 << 10);
    public static readonly ControllerButtons Right = new(1 << 11);

    public static readonly ControllerButtons Dpad = Up | Down | Left | Right;

    // What Swift's OptionSet gives: [.a, .b] is a | b, [] is default.
    public static ControllerButtons operator |(ControllerButtons a, ControllerButtons b) => new((ushort)(a.RawValue | b.RawValue));
    public static ControllerButtons operator &(ControllerButtons a, ControllerButtons b) => new((ushort)(a.RawValue & b.RawValue));
    public bool IsEmpty => RawValue == 0;
    public bool Contains(ControllerButtons member) => (RawValue & member.RawValue) == member.RawValue;
    public ControllerButtons Union(ControllerButtons other) => this | other;
    public ControllerButtons Intersection(ControllerButtons other) => this & other;
    public ControllerButtons Subtracting(ControllerButtons other) => new((ushort)(RawValue & ~other.RawValue));
    public bool IsDisjoint(ControllerButtons with) => (RawValue & with.RawValue) == 0;

    public static bool operator ==(ControllerButtons a, ControllerButtons b) => a.RawValue == b.RawValue;
    public static bool operator !=(ControllerButtons a, ControllerButtons b) => !(a == b);
    public bool Equals(ControllerButtons other) => this == other;
    public override bool Equals(object? obj) => obj is ControllerButtons b && this == b;
    public override int GetHashCode() => RawValue.GetHashCode();
}

/// <summary>Everything read from the controller at one moment. Sticks are -1 to 1 (right and up positive), triggers 0 to 1.</summary>
public record struct ControllerSnapshot
{
    public ControllerButtons Buttons;
    public double RightX;
    public double RightY;
    public double LeftTrigger;
    public double RightTrigger;

    public ControllerSnapshot(ControllerButtons buttons = default, double rightX = 0, double rightY = 0,
                              double leftTrigger = 0, double rightTrigger = 0)
    {
        Buttons = buttons;
        RightX = rightX;
        RightY = rightY;
        LeftTrigger = leftTrigger;
        RightTrigger = rightTrigger;
    }
}

public static class ControllerLogic
{
    /// <summary>A stick this close to its middle is at rest (sticks never sit at exactly 0).</summary>
    public const double StickDeadZone = 0.18;
    public const double TriggerDeadZone = 0.04;
    /// <summary>The Space's speed with the stick right over, degrees a second.</summary>
    public const double MoveDegPerSecond = 40.0;
    /// <summary>The screens' change of width with a trigger right down, degrees a second.</summary>
    public const double SizeDegPerSecond = 25.0;
    /// <summary>A held D-pad direction acts again this often, seconds.</summary>
    public const double RepeatEvery = 0.25;
    /// <summary>The settings are written at most this often while a trigger is held.</summary>
    public const double WritesPerSecond = 30.0;

    /// <summary>
    /// A stick with its dead zone taken out: nothing inside it, then growing evenly from 0 at its edge to 1 right
    /// over, in the direction the stick points. The dead zone is round, so a diagonal starts as soon as any other way.
    /// </summary>
    public static (double X, double Y) Stick(double x, double y, double deadZone = StickDeadZone)
    {
        if (!(double.IsFinite(x) && double.IsFinite(y))) return (0, 0);
        double size = Math.Sqrt(x * x + y * y);
        if (!(size > deadZone && deadZone < 1)) return (0, 0);
        double past = Math.Min((size - deadZone) / (1 - deadZone), 1);
        return (x / size * past, y / size * past);
    }

    /// <summary>A trigger with its dead zone taken out, 0 to 1.</summary>
    public static double Trigger(double value, double deadZone = TriggerDeadZone)
    {
        if (!(double.IsFinite(value) && value > deadZone && deadZone < 1)) return 0;
        return Math.Min((value - deadZone) / (1 - deadZone), 1);
    }

    /// <summary>Does the controller change the Space now?</summary>
    public static bool Active(ControllerMode mode, bool leftStickIn)
    {
        switch (mode)
        {
            case ControllerMode.Off: return false;
            case ControllerMode.Held: return leftStickIn;
            case ControllerMode.Always: return true;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>The buttons that went down between two readings (buttons act on the press, not on letting go).</summary>
    public static ControllerButtons Pressed(ControllerButtons before, ControllerButtons now) => now.Subtracting(before);

    /// <summary>
    /// Is anything held that acts for as long as it is held (the right stick, a trigger, the D-pad)? The 60 Hz timer
    /// runs only while this is so.
    /// </summary>
    public static bool Continuous(ControllerSnapshot s)
    {
        (double X, double Y) move = Stick(s.RightX, s.RightY);
        if (move.X != 0 || move.Y != 0) return true;
        if (Trigger(s.LeftTrigger) > 0 || Trigger(s.RightTrigger) > 0) return true;
        return !s.Buttons.IsDisjoint(ControllerButtons.Dpad);
    }

    /// <summary>
    /// How far to move the Space in [dt] seconds, degrees (left and up positive): the stick to the right carries the
    /// screens to the right, which is a negative yaw.
    /// </summary>
    public static (double YawDeg, double PitchDeg) Move(ControllerSnapshot s, double dt)
    {
        (double X, double Y) v = Stick(s.RightX, s.RightY);
        return (-v.X * MoveDegPerSecond * dt, v.Y * MoveDegPerSecond * dt);
    }

    /// <summary>How much wider the screens get in [dt] seconds, degrees: the right trigger makes them bigger, the left smaller.</summary>
    public static double Grow(ControllerSnapshot s, double dt) =>
        (Trigger(s.RightTrigger) - Trigger(s.LeftTrigger)) * SizeDegPerSecond * dt;

    /// <summary>
    /// The screen the pointer goes to next (an index into the floating screens, null the PC's own screen): forward
    /// through the floating screens and then the PC's own, round and round. With [includeOwn] false the PC's own
    /// screen is left out (Pocket mode keeps the pointer off it). Null with no floating screen.
    /// </summary>
    public static int? NextScreen(int? current, int floating, bool forward, bool includeOwn = true)
    {
        if (!(floating > 0)) return null;
        // Places in order: the PC's own screen first (when it counts), then each floating screen.
        int places = floating + (includeOwn ? 1 : 0);
        int shift = includeOwn ? 1 : 0;
        int at = 0;
        if (current is int c && c >= 0 && c < floating)
        {
            at = c + shift;
        }
        else if (!includeOwn)
        {
            // Coming from the PC's own screen with it left out: forward lands on the first, back on the last.
            return forward ? 0 : floating - 1;
        }
        int next = ((at + (forward ? 1 : -1)) % places + places) % places;
        if (includeOwn) return next == 0 ? null : next - 1;
        return next;
    }
}

/// <summary>A held button that acts at once and then again every so often.</summary>
public struct ControllerRepeat
{
    public readonly double Every;
    private double? nextAt;

    // Without this, new ControllerRepeat() would be all zeros: a C# struct's optional parameters do not count.
    public ControllerRepeat() : this(ControllerLogic.RepeatEvery) { }

    public ControllerRepeat(double every)
    {
        Every = every;
        nextAt = null;
    }

    /// <summary>Call on every reading with whether the button is held and the time now, seconds. True when it should act.</summary>
    public bool Fires(bool held, double now)
    {
        if (!held) { nextAt = null; return false; }
        if (nextAt is not double due)
        {
            nextAt = now + Every;
            return true;
        }
        if (!(now >= due)) return false;
        // After a stall it does not fire a burst to catch up.
        nextAt = due + Every > now ? due + Every : now + Every;
        return true;
    }
}

/// <summary>
/// Lets small changes pile up and hands them over at most so many times a second, so a held trigger does not write
/// the settings 60 times a second.
/// </summary>
public struct ControllerPacer
{
    public readonly double Interval;
    private double pending;
    private double nextAt;

    // Without this, new ControllerPacer() would be all zeros: a C# struct's optional parameters do not count.
    public ControllerPacer() : this(ControllerLogic.WritesPerSecond) { }

    public ControllerPacer(double perSecond)
    {
        Interval = 1 / Math.Max(perSecond, 1);
        pending = 0.0;
        nextAt = double.NegativeInfinity;
    }

    /// <summary>Adds [amount]; returns everything piled up when it is time to write, else null.</summary>
    public double? Add(double amount, double now)
    {
        pending += amount;
        if (!(now >= nextAt && pending != 0)) return null;
        // Slots on an even grid, so timer jitter does not lower the rate; after a pause the grid starts afresh.
        nextAt = nextAt + Interval >= now ? nextAt + Interval : now + Interval;
        double all = pending;
        pending = 0;
        return all;
    }

    /// <summary>What is still piled up when the trigger is let go (null when nothing is).</summary>
    public double? Flush()
    {
        if (!(pending != 0)) return null;
        double all = pending;
        pending = 0;
        return all;
    }
}

/// <summary>
/// Pocket mode's watch, asked 30 times a second: keeps the pointer on the floating screens, notices the hold that
/// leaves, and leaves by itself when there is nothing left to look at.
/// </summary>
public record struct PocketWatch
{
    /// <summary>Holding the button on the black screen this long leaves.</summary>
    public const double HoldSeconds = 1.0;
    /// <summary>The glasses' display gone this long leaves.</summary>
    public const double GlassesGoneSeconds = 5.0;
    /// <summary>"Hold to leave Pocket mode" stays this long after the button is let go.</summary>
    public const double HintSeconds = 4.0;

    public enum Leave
    {
        /// <summary>The button was held on the black screen.</summary>
        Held,
        /// <summary>The floating screens went away.</summary>
        NoScreens,
        /// <summary>The glasses' display has been gone too long.</summary>
        NoGlasses,
    }

    public enum StepKind
    {
        Stay,
        PushBack,
        Leave,
    }

    /// <summary>
    /// What a step asks for (Swift's PocketWatch.Step; C# cannot have a type and a method of the same name in one
    /// type, and the method keeps the name).
    /// </summary>
    public readonly record struct StepResult
    {
        public StepKind Kind { get; }
        /// <summary>PushBack: put the pointer back on this floating screen.</summary>
        public int Screen { get; }
        /// <summary>Leave: why.</summary>
        public PocketWatch.Leave? Reason { get; }

        private StepResult(StepKind kind, int screen, PocketWatch.Leave? reason)
        {
            Kind = kind;
            Screen = screen;
            Reason = reason;
        }

        public static readonly StepResult Stay = new(StepKind.Stay, 0, null);
        /// <summary>Put the pointer back on this floating screen.</summary>
        public static StepResult PushBack(int screen) => new(StepKind.PushBack, screen, null);
        public static StepResult Leave(PocketWatch.Leave reason) => new(StepKind.Leave, 0, reason);
    }

    /// <summary>The floating screen the pointer was last on.</summary>
    public int LastScreen { get; private set; }
    private double? holdStart;
    private double? lastHeldAt;
    private double? glassesGoneSince;
    /// <summary>The button has been seen up since Pocket mode came on (the click that turned it on must not count as a hold).</summary>
    private bool armed;

    public PocketWatch() : this(0) { }

    public PocketWatch(int lastScreen)
    {
        LastScreen = Math.Max(lastScreen, 0);
        holdStart = null;
        lastHeldAt = null;
        glassesGoneSince = null;
        armed = false;
    }

    /// <summary>
    /// [buttonDown]: the mouse or trackpad button is down. [pointerOnCover]: the pointer is on a blacked-out screen.
    /// [pointerScreen]: the floating screen the pointer is on, null when it is on none.
    /// </summary>
    public StepResult Step(double now, bool buttonDown, bool pointerOnCover, int? pointerScreen, int floatingScreens,
                           bool glassesPresent)
    {
        if (!(floatingScreens > 0)) return StepResult.Leave(PocketWatch.Leave.NoScreens);
        if (glassesPresent)
        {
            glassesGoneSince = null;
        }
        else
        {
            double since = glassesGoneSince ?? now;
            glassesGoneSince = since;
            if (now - since >= GlassesGoneSeconds) return StepResult.Leave(PocketWatch.Leave.NoGlasses);
        }
        if (pointerScreen is int p && p >= 0 && p < floatingScreens) LastScreen = p;
        LastScreen = Math.Min(LastScreen, floatingScreens - 1);

        if (!buttonDown) armed = true;
        if (buttonDown && pointerOnCover && armed)
        {
            double start = holdStart ?? now;
            holdStart = start;
            lastHeldAt = now;
            return now - start >= HoldSeconds ? StepResult.Leave(PocketWatch.Leave.Held) : StepResult.Stay;
        }
        holdStart = null;
        // A drag is left alone, so the pointer can be carried onto the PC's screen and held there.
        if (buttonDown) return StepResult.Stay;
        return pointerScreen == null ? StepResult.PushBack(LastScreen) : StepResult.Stay;
    }

    /// <summary>How far the hold has got, 0 to 1 (the ring round the lock).</summary>
    public readonly double Progress(double now)
    {
        if (holdStart is not double start) return 0;
        return Math.Min(Math.Max((now - start) / HoldSeconds, 0), 1);
    }

    /// <summary>"Hold to leave Pocket mode" shows from the first press until a little after the button is let go.</summary>
    public readonly bool HintShown(double now)
    {
        if (lastHeldAt is not double last) return false;
        return holdStart != null || now - last < HintSeconds;
    }
}
