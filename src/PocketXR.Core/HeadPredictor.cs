using System;

namespace PocketXR.Core;

// The head pose at the time a frame's light leaves the glasses, ported from Pocket XR's HeadPrediction.kt (ImuClock,
// HeadPredictor, HeadHold). Pocket XR's TremorPredictor, which learns a worn head's tremor, is left out: this is the
// predictor Pocket XR 0.5.19 shipped (smoothed rate and acceleration, the One Euro rate filter, the still head mode),
// with 0.5.30's still blend fitted to its own prediction.

/// <summary>
/// Small rotation sums the pose chain shares. Angles in radians unless a name says degrees. (Public here, where the
/// Mac has it internal: the checks are their own assembly.)
/// </summary>
public static class HeadMath
{
    public const double DegPerRad = 180.0 / Math.PI;

    /// <summary>
    /// Swift's min(x, y): y when y is less than x, else x. Not Math.Min, which gives NaN when either is NaN; this
    /// keeps what the Mac does with a NaN.
    /// </summary>
    internal static double Min(double x, double y) => y < x ? y : x;

    /// <summary>Swift's max(x, y): y when y is at least x, else x (see Min).</summary>
    internal static double Max(double x, double y) => y >= x ? y : x;

    public static double Length(Vec3 v)
    {
        return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    public static double Clamp(double x, double lo, double hi)
    {
        return Min(Max(x, lo), hi);
    }

    /// <summary>0 to 1 with a soft start and end.</summary>
    public static double Smooth(double x)
    {
        double c = Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }

    /// <summary>[a] moved [k] of the way to [b].</summary>
    public static Vec3 Toward(Vec3 a, Vec3 b, double k)
    {
        return a + (b - a) * k;
    }

    public static bool IsFinite(Quat q)
    {
        return double.IsFinite(q.W) && double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z);
    }

    /// <summary>The shortest way from [a] to [b], [t] of it (0 is [a], 1 is [b]).</summary>
    public static Quat Slerp(Quat a, Quat b, double t)
    {
        double d = a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        double bw = b.W;
        double bx = b.X;
        double by = b.Y;
        double bz = b.Z;
        if (d < 0)
        {
            d = -d;
            bw = -bw;
            bx = -bx;
            by = -by;
            bz = -bz;
        }
        double ka = 1 - t;
        double kb = t;
        if (d < 0.999999)
        {
            double theta = Math.Acos(d);
            double s = Math.Sin(theta);
            ka = Math.Sin((1 - t) * theta) / s;
            kb = Math.Sin(t * theta) / s;
        }
        return new Quat(ka * a.W + kb * bw, ka * a.X + kb * bx, ka * a.Y + kb * by, ka * a.Z + kb * bz).Normalized;
    }

    /// <summary>[q] turned on by the rate [omega] (the head's own axes, rad/s) for [dt] seconds.</summary>
    public static Quat TurnedBy(Quat q, Vec3 omega, double dt)
    {
        double l = Length(omega);
        double angle = l * dt;
        if (angle < 1e-12) return q;
        double k = Math.Sin(angle / 2) / l;
        return (q * new Quat(Math.Cos(angle / 2), omega.X * k, omega.Y * k, omega.Z * k)).Normalized;
    }

    /// <summary>The turn from [a] to [b] as a rotation vector (radians, the head's axes); exact for small angles.</summary>
    public static Vec3 TurnVector(Quat a, Quat b)
    {
        Quat d = a.Conjugate * b;
        double s = d.W < 0 ? -2 : 2;
        return new Vec3(d.X * s, d.Y * s, d.Z * s);
    }

    /// <summary>The angle between [a] and [b], degrees.</summary>
    public static double AngleDeg(Quat a, Quat b)
    {
        Quat d = a.Conjugate * b;
        double v = Min(1, Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z));
        return 2 * Math.Asin(v) * DegPerRad;
    }

    /// <summary>Where [q] points: yaw (left positive) and pitch (up positive), degrees.</summary>
    public static (double Yaw, double Pitch) YawPitchDeg(Quat q)
    {
        Vec3 f = q.Rotate(new Vec3(0, 0, -1));
        double yaw = Math.Atan2(-f.X, -f.Z) * DegPerRad;
        double pitch = Math.Asin(Clamp(f.Y, -1, 1)) * DegPerRad;
        return (yaw, pitch);
    }
}

/// <summary>
/// The glasses' sample times on the host's clock (Pocket XR 0.5.10).
///
/// The stream arrives in bursts, so a sample's arrival says little about when it was taken. The glasses' clock runs at
/// a steady rate, so host time = device time + offset, where the offset is the smallest (arrival - device time) seen:
/// the sample that waited least on the way. It may creep up by [driftNsPerSample] a sample (the two clocks' rates
/// differ a little) and starts again when the device clock jumps (a reconnect).
///
/// A class here (a struct on the Mac), so that new ImuClock() takes the defaults and ToHost changes the one clock.
/// </summary>
public sealed class ImuClock
{
    public long DriftNsPerSample;
    public long JumpNs;
    private long? offset;

    public ImuClock(long driftNsPerSample = 100, long jumpNs = 500_000_000)
    {
        DriftNsPerSample = driftNsPerSample;
        JumpNs = jumpNs;
        offset = null;
    }

    /// <summary>The host time (ns) of a sample stamped [deviceNs] that arrived at [arrivedNs].</summary>
    public long ToHost(long deviceNs, long arrivedNs)
    {
        unchecked
        {
            long candidate = arrivedNs - deviceNs;
            long next = candidate;
            if (offset is long o)
            {
                long gap = candidate - o;
                if (gap < -JumpNs || gap > JumpNs)
                {
                    next = candidate;
                }
                else if (gap < 0)
                {
                    next = candidate;
                }
                else
                {
                    next = o + Math.Min(DriftNsPerSample, gap);
                }
            }
            offset = next;
            return deviceNs + next;
        }
    }

    public void Reset()
    {
        offset = null;
    }
}

/// <summary>
/// What HeadHold.Use answers. (HeadHold.Use on the Mac, an enum inside HeadHold; C# cannot have a type and a method
/// of one name in a class, so it stands beside it.)
/// </summary>
public enum HeadHoldUse
{
    Predict,
    Hold,
    None,
}

/// <summary>
/// What the view does when head tracking pauses (Pocket XR 0.5.10): a pause of up to [holdS] keeps the last pose,
/// unpredicted, and the stream carries on from it when it comes back; only after that does the view go head-locked.
/// </summary>
public static class HeadHold
{
    public const double FreshS = 0.1;
    public const double HoldS = 5.0;

    /// <summary>Predict from samples newer than [freshS]; else hold the last pose until [holdS]; else none.</summary>
    public static HeadHoldUse Use(double sinceSampleS, bool hasPose)
    {
        if (!hasPose) return HeadHoldUse.None;
        if (sinceSampleS <= FreshS) return HeadHoldUse.Predict;
        if (sinceSampleS <= HoldS) return HeadHoldUse.Hold;
        return HeadHoldUse.None;
    }
}

/// <summary>
/// The head pose at a display frame's light time (Pocket XR 0.5.10 to 0.5.30).
///
/// Each fused sample comes with its host time. The rate is smoothed (raw 1 kHz gyro samples carry a few degrees a
/// second of noise, which a 30 ms prediction turns into visible shake) and so is its change, the angular acceleration.
/// [predict] turns the newest orientation on by the rate, plus the acceleration's share, to the target time, never
/// further than [maxAheadMs].
///
/// Not thread safe: HeadPose holds its lock for every call.
/// </summary>
public sealed class HeadPredictor
{
    /// <summary>The rate filter when the head moves, ms (a 53 Hz low pass).</summary>
    public const double RateTauMs = 3;
    public const double AccelTauMs = 10;
    public const double MaxAheadMs = 80;
    /// <summary>Angular acceleration above this (rad/s^2) is noise or a knock, not a head.</summary>
    public const double MaxAccel = 400;
    internal const double ActivityMs = 300;
    /// <summary>The acceleration term counts fully once the head's recent peak rate reaches this (rad/s, about 17 deg/s).</summary>
    internal const double AccelFullRate = 0.3;

    // The One Euro rate filter (0.5.18): 6 Hz at rest, opening with the head's speed and acceleration to rateTauMs.
    public const double EuroMinHz = 6;
    public const double EuroBetaRate = 40000;
    internal const double EuroRateFloor = 0.002;
    internal const double EuroBetaAccel = 40;
    internal const double EuroAccelFloor = 2;

    // The still head mode (0.5.18): under stillEnter rad/s prediction fades over stillRiseMs to stillShare of itself,
    // back in full by stillExit.
    internal const double StillEnter = 0.0025;
    internal const double StillExit = 0.005;
    internal const double StillShare = 0.05;
    internal const double StillRiseMs = 300;
    internal const double StillFallMs = 20;

    // The still blend (0.5.30): the prediction scaled by the share of it that came true. Every sample's prediction
    // blendRefMs ahead is set, once that time has passed, against the turn the gyro then made; the least squares gain
    // over the last blendTauMs (0 to 1) scales the prediction, and a move lets it go to 1 at once.
    public const double BlendRefMs = 30;
    public const double BlendTauMs = 160;
    internal const double BlendReleaseLow = 3;
    internal const double BlendReleaseHigh = 6;
    internal const double BlendSpeedLow = 0.1;
    internal const double BlendSpeedHigh = 0.25;
    internal const int BlendCap = 256;
    internal const int BlendWarmup = 200;
    internal const double TurnSpeedMs = 100;

    public bool StillHead = true;
    public bool StillBlend = true;

    /// <summary>The newest sample's orientation and host time (seconds); null before the first.</summary>
    public Quat? Orientation { get; private set; }
    public double SampleTime { get; private set; }

    private Vec3 rate = new(0, 0, 0);
    private Vec3 accel = new(0, 0, 0);
    private double activity = 0;
    private Vec3 fastRate = new(0, 0, 0);
    private double fastAccel = 0;
    private double stillSlide = 0;
    private bool stillWanted = false;
    /// <summary>The still head mode's weight: 0 moving, 1 still.</summary>
    public double StillWeight { get; private set; }
    /// <summary>The gyro's peak speed, falling away over 0.1 s, rad/s.</summary>
    public double TurnSpeed { get; private set; }

    // The still blend's ring of predictions waiting for their turn to finish, and the gyro's integral.
    private readonly double[] blendT;
    private readonly Vec3[] blendP;
    private readonly Vec3[] blendI;
    private int blendHead = 0;
    private int blendTail = 0;
    private Vec3 integral = new(0, 0, 0);
    private double blendMade = 0;
    private double blendPred = 0;
    private double blendWeight = 0;
    private double blendNow = 0;
    private int blendCount = 0;
    /// <summary>The still blend's fitted gain (0 to 1) and the share of the prediction it gives now.</summary>
    public double BlendGain { get; private set; } = 1;
    public double BlendShare { get; private set; } = 1;

    public HeadPredictor()
    {
        blendT = new double[BlendCap];
        blendP = new Vec3[BlendCap];
        blendI = new Vec3[BlendCap];
    }

    /// <summary>The smoothed rate in the head's axes, rad/s.</summary>
    public Vec3 AngularVelocity => rate;

    /// <summary>The share of the prediction the still head mode gives now (1 moving, stillShare still).</summary>
    public double PredictionShare => 1 - StillWeight * (1 - StillShare);

    /// <summary>One fused sample: the orientation [q], the rate [omega] in the head's axes (rad/s), taken at [host] seconds.</summary>
    public void Add(Quat q, Vec3 omega, double host)
    {
        bool hadPose = Orientation != null;
        double dtMs = hadPose ? (host - SampleTime) * 1000 : 0;
        bool steady = hadPose && dtMs > 0 && dtMs <= 100;
        if (!steady)
        {
            double speed = HeadMath.Length(omega);
            rate = omega;
            accel = new Vec3(0, 0, 0);
            activity = speed;
            fastRate = omega;
            fastAccel = 0;
            stillSlide = 0;
            stillWanted = false;
            StillWeight = 0;
            TurnSpeed = speed;
            BlendClear();
        }
        else
        {
            // The rate through the 3 ms filter and its change, which open the One Euro filter.
            double kf = dtMs / (RateTauMs + dtMs);
            Vec3 nextFast = HeadMath.Toward(fastRate, omega, kf);
            double fastChange = HeadMath.Length(nextFast - fastRate) * (1000 / dtMs);
            fastAccel += (fastChange - fastAccel) * (dtMs / (AccelTauMs + dtMs));
            fastRate = nextFast;

            double k = dtMs / (RateTau() + dtMs);
            Vec3 next = HeadMath.Toward(rate, omega, k);
            Vec3 change = (next - rate) * (1000 / dtMs);
            double ka = dtMs / (AccelTauMs + dtMs);
            accel = HeadMath.Toward(accel, change, ka);
            double accelLength = HeadMath.Length(accel);
            if (accelLength > MaxAccel) accel = accel * (MaxAccel / accelLength);
            rate = next;
            activity = HeadMath.Max(HeadMath.Length(rate), activity * Math.Exp(-dtMs / ActivityMs));
            TurnSpeed = HeadMath.Max(HeadMath.Length(omega), TurnSpeed * Math.Exp(-dtMs / TurnSpeedMs));
            if (StillHead) StillStep(dtMs); else StillWeight = 0;
            if (StillBlend) BlendStep(omega, host, dtMs);
        }
        Orientation = q;
        SampleTime = host;
    }

    /// <summary>The orientation at [target] (host seconds), or null before the first sample.</summary>
    public Quat? Predict(double target)
    {
        if (Orientation is not Quat q) return null;
        double dt = HeadMath.Clamp(target - SampleTime, 0, MaxAheadMs / 1000);
        if (dt <= 0) return q;
        double share = StillHead ? PredictionShare : 1;
        double blend = StillBlend ? BlendShare : 1;
        double scale = share * blend;
        Vec3 whole = ClassicStep(dt);
        var step = new Vec3(whole.X * scale, whole.Y * scale, whole.Z * scale);
        return HeadMath.TurnedBy(q, step, 1);
    }

    public void Reset()
    {
        Orientation = null;
        SampleTime = 0;
        rate = new Vec3(0, 0, 0);
        accel = new Vec3(0, 0, 0);
        activity = 0;
        fastRate = new Vec3(0, 0, 0);
        fastAccel = 0;
        stillSlide = 0;
        stillWanted = false;
        StillWeight = 0;
        TurnSpeed = 0;
        BlendClear();
    }

    /// <summary>The rate filter's time constant for this step, ms: the One Euro rule.</summary>
    private double RateTau()
    {
        double overRate = HeadMath.Max(0, HeadMath.Length(fastRate) - EuroRateFloor);
        double overAccel = HeadMath.Max(0, fastAccel - EuroAccelFloor);
        double speed = EuroBetaRate * overRate + EuroBetaAccel * overAccel;
        double openHz = 1000 / (2 * Math.PI * RateTauMs);
        double hz = HeadMath.Clamp(EuroMinHz + speed, EuroMinHz, openHz);
        return 1000 / (2 * Math.PI * hz);
    }

    /// <summary>
    /// The still head weight. The filtered rate decides with hysteresis whether the head is still; the weight slides
    /// toward that over stillRiseMs (in) or stillFallMs (out), and a move on the 3 ms rate takes it down at once,
    /// smoothly from stillEnter to none left at stillExit.
    /// </summary>
    private void StillStep(double dtMs)
    {
        double slow = HeadMath.Length(rate);
        if (slow <= StillEnter)
        {
            stillWanted = true;
        }
        else if (slow >= StillExit)
        {
            stillWanted = false;
        }
        if (stillWanted)
        {
            stillSlide += (1 - stillSlide) * (dtMs / (StillRiseMs + dtMs));
        }
        else
        {
            stillSlide *= 1 - dtMs / (StillFallMs + dtMs);
        }
        double band = StillExit - StillEnter;
        double gate = 1 - HeadMath.Smooth((HeadMath.Length(fastRate) - StillEnter) / band);
        StillWeight = HeadMath.Min(stillSlide, gate);
    }

    /// <summary>Rate integration with the acceleration's share: angle = w t + a w' t^2 / 2.</summary>
    private Vec3 ClassicStep(double dt)
    {
        double accelShare = HeadMath.Clamp(activity / AccelFullRate, 0, 1);
        return rate * dt + accel * (accelShare * dt * dt / 2);
    }

    private void BlendClear()
    {
        blendHead = 0;
        blendTail = 0;
        integral = new Vec3(0, 0, 0);
        blendMade = 0;
        blendPred = 0;
        blendWeight = 0;
        blendNow = 0;
        blendCount = 0;
        BlendGain = 1;
        BlendShare = 1;
    }

    /// <summary>One sample of the still blend, after the filters have the sample.</summary>
    private void BlendStep(Vec3 omega, double host, double dtMs)
    {
        int cap = BlendCap;
        double refS = BlendRefMs / 1000;
        integral += omega * (dtMs / 1000);
        double k = dtMs / (BlendTauMs + dtMs);
        while (blendTail != blendHead && blendT[blendTail] + refS <= host)
        {
            Vec3 p = blendP[blendTail];
            Vec3 turned = integral - blendI[blendTail];
            double made = turned.X * p.X + turned.Y * p.Y + turned.Z * p.Z;
            double pp = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
            blendMade += (made - blendMade) * k;
            blendPred += (pp - blendPred) * k;
            blendWeight += (1 - blendWeight) * k;
            blendCount += 1;
            blendTail = (blendTail + 1) % cap;
        }
        // Before 0.2 s of finished turns the gain is not trusted: 1.
        if (blendCount < BlendWarmup || blendPred <= 1e-18)
        {
            BlendGain = 1;
        }
        else
        {
            BlendGain = HeadMath.Clamp(blendMade / blendPred, 0, 1);
        }
        Vec3 now = ClassicStep(refS);
        blendT[blendHead] = host;
        blendP[blendHead] = now;
        blendI[blendHead] = integral;
        blendHead = (blendHead + 1) % cap;
        if (blendHead == blendTail) blendTail = (blendTail + 1) % cap;
        blendNow = now.X * now.X + now.Y * now.Y + now.Z * now.Z;
        BlendShare = BlendShareNow();
    }

    /// <summary>The fitted gain, let go to 1 by a move (the prediction against its own recent size, or the gyro's peak).</summary>
    private double BlendShareNow()
    {
        double rms = blendWeight > 0 ? Math.Sqrt(blendPred / blendWeight) : 0;
        double z = rms > 0 ? Math.Sqrt(blendNow) / rms : double.MaxValue;
        double low = BlendReleaseLow;
        double high = BlendReleaseHigh;
        double byPrediction = HeadMath.Smooth((z - low) / (high - low));
        double speedBand = BlendSpeedHigh - BlendSpeedLow;
        double bySpeed = HeadMath.Smooth((TurnSpeed - BlendSpeedLow) / speedBand);
        return BlendGain + (1 - BlendGain) * HeadMath.Max(byPrediction, bySpeed);
    }
}
