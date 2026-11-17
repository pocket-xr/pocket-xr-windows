using System;

namespace PocketXR.Core;

// Hand tracking's filters: the One Euro filter that steadies the aim, and the pinch detector with its two
// thresholds. Ported in idea and in numbers from the Android app (core/hands: OneEuroFilter, RayAimFilter,
// PinchDetector).

/// <summary>
/// The One Euro filter (Casiez, Roussel and Vogel, CHI 2012): a low pass filter whose cut off rises with speed, so
/// a still hand gives a steady pointer and a fast hand has little lag. [speedFloor] (value units a second) is taken
/// off the speed before [beta] opens the cut off, so that the points' own jitter does not open it for a still hand
/// (Android 0.5.4).
/// </summary>
public sealed class HandOneEuro
{
    public double MinCutoffHz { get; }
    public double Beta { get; }
    public double DerivativeCutoffHz { get; }
    public double SpeedFloor { get; }
    private double value = 0.0;
    private double derivative = 0.0;
    private double lastMs = 0.0;
    private bool primed = false;

    /// <summary>
    /// The defaults are the Android hand ray's, in degrees (RayFilterSettings, 0.5.45): 1.2 Hz at rest, opening by
    /// 1.5 Hz for each degree a second above 7, with a 2 Hz filter on the speed.
    /// </summary>
    public HandOneEuro(double minCutoffHz = 1.2, double beta = 1.5, double derivativeCutoffHz = 2.0, double speedFloor = 7)
    {
        MinCutoffHz = Math.Max(minCutoffHz, 1e-3);
        Beta = Math.Max(beta, 0);
        DerivativeCutoffHz = Math.Max(derivativeCutoffHz, 1e-3);
        SpeedFloor = Math.Max(speedFloor, 0);
    }

    /// <summary>
    /// Feeds one value seen at [timeMs] and returns the filtered one. A time that does not move forward changes
    /// nothing.
    /// </summary>
    public double Filter(double x, double timeMs)
    {
        if (!primed)
        {
            primed = true;
            lastMs = timeMs;
            value = x;
            derivative = 0;
            return value;
        }
        if (!(timeMs > lastMs)) return value;
        double dt = (timeMs - lastMs) / 1000;
        lastMs = timeMs;
        double speed = (x - value) / dt;
        derivative += Alpha(DerivativeCutoffHz, dt) * (speed - derivative);
        double cutoff = MinCutoffHz + Beta * Math.Max(Math.Abs(derivative) - SpeedFloor, 0);
        value += Alpha(cutoff, dt) * (x - value);
        return value;
    }

    public void Reset()
    {
        primed = false;
        derivative = 0;
    }

    private static double Alpha(double cutoffHz, double dt)
    {
        double tau = 1 / (2 * Math.PI * cutoffHz);
        return 1 / (1 + tau / dt);
    }
}

/// <summary>
/// The hand ray's smoothing: yaw and pitch each through their own One Euro filter, so a sideways sweep keeps the
/// pitch as steady as a resting hand's (Android: RayAimFilter).
/// </summary>
public sealed class HandRayFilter
{
    private readonly HandOneEuro yaw;
    private readonly HandOneEuro pitch;

    public HandRayFilter(double minCutoffHz = 1.2, double beta = 1.5, double derivativeCutoffHz = 2.0, double speedFloor = 7)
    {
        yaw = new HandOneEuro(minCutoffHz, beta, derivativeCutoffHz, speedFloor);
        pitch = new HandOneEuro(minCutoffHz, beta, derivativeCutoffHz, speedFloor);
    }

    public HandAngles Filter(HandAngles ray, double timeMs)
    {
        double y = yaw.Filter(ray.YawDeg, timeMs);
        double p = pitch.Filter(ray.PitchDeg, timeMs);
        return new HandAngles(y, p);
    }

    public void Reset()
    {
        yaw.Reset();
        pitch.Reset();
    }
}

/// <summary>
/// Pinch on and off with two thresholds (Android: PinchDetector, tuned in 0.2.1). A pinch starts when the thumb to
/// index distance over the hand's size is below [enterRatio] (0.25) for [enterFrames] (2) frames in a row, or below
/// [fastEnterRatio] (0.15, fingers clearly touching) on one frame, which takes a frame off every click. It ends
/// only when the ratio is above [exitRatio] (0.42) for [exitFrames] (2) frames: the gap between the thresholds
/// stops jitter from clicking, a drag does not drop when the fingers loosen a little, and one bad frame never lets go.
/// </summary>
public sealed class HandPinchDetector
{
    public double EnterRatio { get; }
    public double ExitRatio { get; }
    public double FastEnterRatio { get; }
    public int EnterFrames { get; }
    public int ExitFrames { get; }
    public bool Pinched { get; private set; } = false;
    private int streak = 0;

    public HandPinchDetector(double enterRatio = 0.25, double exitRatio = 0.42, double fastEnterRatio = 0.15,
                             int enterFrames = 2, int exitFrames = 2)
    {
        EnterRatio = enterRatio;
        ExitRatio = Math.Max(exitRatio, enterRatio);
        FastEnterRatio = Math.Min(fastEnterRatio, enterRatio);
        EnterFrames = Math.Max(enterFrames, 1);
        ExitFrames = Math.Max(exitFrames, 1);
    }

    /// <summary>Feeds one frame's ratio and returns whether the hand is pinching after it.</summary>
    public bool Update(double ratio)
    {
        if (!Pinched && ratio < FastEnterRatio)
        {
            Pinched = true;
            streak = 0;
            return true;
        }
        bool crossing = Pinched ? ratio > ExitRatio : ratio < EnterRatio;
        streak = crossing ? streak + 1 : 0;
        if (streak >= (Pinched ? ExitFrames : EnterFrames))
        {
            Pinched = !Pinched;
            streak = 0;
        }
        return Pinched;
    }

    public void Reset()
    {
        Pinched = false;
        streak = 0;
    }
}
