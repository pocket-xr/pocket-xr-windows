using System;

namespace PocketXR.Core;

/// <summary>
/// Head scroll, ported from Pocket XR's HeadScroll.kt (1.4.6), off by default.
///
/// Where the screens are held in front (Smooth Follow, where they move with the head), a small turn of the head past a
/// dead zone slides the row of screens that way, like a joystick: turn a little right and the screens on the right
/// come towards the middle. The further past the dead zone, the faster, up to a limit; looking back to the middle stops
/// it. The speed eases in and out so the row never jumps.
///
/// Reading a screen near the middle stays still: the dead zone is wider than a glance across the screen in front.
/// Looking down or up past [maxPitchDeg] does not scroll.
///
/// Angles here are as in Pocket XR: the head's turn from where the screens are held, right positive. Not thread safe:
/// HeadPose holds its lock for every call.
/// </summary>
public sealed class HeadScroll
{
    public const double DeadZoneDeg = 10;
    public const double GainPerS = 3;
    public const double MaxDegS = 45;
    public const double EaseS = 0.18;
    public const double MaxPitchDeg = 30;
    /// <summary>
    /// Smooth Follow's anchor rate while Head scroll is on (a 2 s time constant, not 0.5 s): the screens stay in front
    /// long enough for a small turn to scroll them, then come round to where the head settles.
    /// </summary>
    public const double FollowRate = 0.5;
    internal const double MaxStepS = 0.1;
    internal const double StopDegS = 0.5;

    /// <summary>The row's speed now, degrees a second (right positive).</summary>
    public double SpeedDegS { get; private set; }

    public HeadScroll() { }

    /// <summary>
    /// The turn to add to the view's yaw for a frame [dtS] seconds long, the head [offsetYawDeg] off (right positive)
    /// and [pitchDeg] up.
    /// </summary>
    public double Step(double offsetYawDeg, double pitchDeg, double dtS)
    {
        if (!double.IsFinite(offsetYawDeg) || !double.IsFinite(pitchDeg) || !double.IsFinite(dtS) || dtS <= 0) return 0;
        double dt = HeadMath.Min(dtS, MaxStepS);
        double target = TargetSpeed(offsetYawDeg: offsetYawDeg, pitchDeg: pitchDeg);
        double k = 1 - Math.Exp(-dt / EaseS);
        SpeedDegS += (target - SpeedDegS) * k;
        if (Math.Abs(SpeedDegS) < StopDegS && target == 0) SpeedDegS = 0;
        return SpeedDegS * dt;
    }

    /// <summary>The speed wanted with the head [offsetYawDeg] off and [pitchDeg] up, before easing.</summary>
    public static double TargetSpeed(double offsetYawDeg, double pitchDeg)
    {
        if (Math.Abs(pitchDeg) > MaxPitchDeg) return 0;
        double past = Math.Abs(offsetYawDeg) - DeadZoneDeg;
        if (past <= 0) return 0;
        double speed = HeadMath.Min(past * GainPerS, MaxDegS);
        return offsetYawDeg < 0 ? -speed : speed;
    }

    /// <summary>Still at once (a recentre, the mode left).</summary>
    public void Stop()
    {
        SpeedDegS = 0;
    }

    /// <summary>
    /// The view's yaw after adding [deltaDeg] to [viewYawDeg], kept between [lowDeg] and [highDeg] (the first and last
    /// screens' middles) so the row stops at its ends. A view already outside them is only kept from going further out.
    /// </summary>
    public static double Clamp(double viewYawDeg, double deltaDeg, double lowDeg, double highDeg)
    {
        double then = viewYawDeg + deltaDeg;
        if (deltaDeg > 0) return then > highDeg ? HeadMath.Max(viewYawDeg, highDeg) : then;
        if (deltaDeg < 0) return then < lowDeg ? HeadMath.Min(viewYawDeg, lowDeg) : then;
        return viewYawDeg;
    }
}
