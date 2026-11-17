using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PocketXR.Core;

/// <summary>The newest fused head sample, for the pose chain (HeadPose): everything read under the fusion's lock at once.</summary>
public record struct HeadSample
{
    /// <summary>The head relative to the recentred forward (x right, y up, z back).</summary>
    public Quat Orientation;
    /// <summary>The gyro's rate with the learnt bias taken out, in the head's own axes, rad/s.</summary>
    public Vec3 Rate;
    /// <summary>
    /// When the sample was taken, on the host's clock (HeadFusion.SystemUptime), seconds: the glasses' own time stamp
    /// carried over by ImuClock, so a burst of reports is not all stamped with the burst's arrival.
    /// </summary>
    public double HostTime;
    /// <summary>How many recentres the fusion has made: a change means the orientation's frame jumped.</summary>
    public int Recentres;

    public HeadSample(Quat orientation, Vec3 rate, double hostTime, int recentres)
    {
        Orientation = orientation;
        Rate = rate;
        HostTime = hostTime;
        Recentres = recentres;
    }
}

/// <summary>
/// Head orientation from gyro and accelerometer: a small complementary filter, ported as it is from Pocket XR for
/// Steam Deck's fusion.py (tuned there on Mike's Aura, 11 and 12 November 2026).
///
/// Head axes are x right, y up, z back. The Aura's sensor already uses them; the One family is x right, y down, z front
/// (Pocket XR's AxisMap), so its y and z are negated.
/// </summary>
public sealed class HeadFusion
{
    public enum Axes
    {
        Aura,
        One,
    }

    private static Vec3 Sign(Axes axes) => axes == Axes.Aura ? new Vec3(1, 1, 1) : new Vec3(1, -1, -1);

    internal const double Kp = 1.0; // how hard tilt follows gravity, 1/s
    internal const int RestBlock = 500; // reports, half a second at 1 kHz
    // A worn head turning slowly once passed an older test and was learnt as bias (the windows crept sideways), so the
    // bias is learnt only from two seconds of real stillness: four quiet half seconds in a row, each axis's mean under
    // 1 deg/s (3 on glasses not calibrated on board) and its spread under 0.6 deg/s.
    internal const double RestSd = 0.6 * Math.PI / 180;
    internal const double RestMaxAura = 1.0 * Math.PI / 180;
    internal const double RestMaxOther = 3.0 * Math.PI / 180;
    internal const int RestRun = 4;
    internal const double RestGain = 0.5;
    internal const double FirstRecentreS = 5.0;
    /// <summary>
    /// A recentre that follows the head's tilt takes its pitch as straight ahead only past this, degrees (Pocket XR's
    /// FusionSettings.recentreTiltMinDeg).
    /// </summary>
    public const double RecentreTiltMinDeg = 15.0;
    /// <summary>Looking nearly straight up or down, the forward's heading is noise: the head's right side gives it instead.</summary>
    internal const double NearVerticalDeg = 80.0;

    /// <summary>
    /// The host's clock, seconds: what the Mac reads as ProcessInfo.processInfo.systemUptime. Samples' arrival and
    /// HeadPose.DrawPose's now are both on it.
    /// </summary>
    public static double SystemUptime => (double)Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly object gate = new();
    private readonly Vec3 sign;
    private readonly double restMax;
    private Quat q = Quat.Identity; // world from head
    private Quat reference = Quat.Identity; // the recentre
    private ulong? lastTs;
    private ulong firstTs = 0;
    private Vec3 bias = new(0, 0, 0);
    private Vec3 biasSum = new(0, 0, 0);
    private Vec3 biasSq = new(0, 0, 0);
    private int biasN = 0;
    private readonly List<Vec3> restRunMeans = new();
    private double? gMag;
    private bool wantRecentre = true;
    private double recentreYaw = 0.0;
    // The pitch the last recentre took as straight ahead, radians (0 on the horizon), and whether the recentre that
    // waits was asked for by the user (it decides the pitch again) or is the fusion's own (it keeps the pitch).
    private double refPitch = 0.0;
    private bool recentreSetsTilt = false;
    private bool recentreFollowsTilt = false;
    private readonly ImuClock clock = new();
    private HeadSample? newest;
    private Action<HeadSample>? sink;

    /// <summary>Recentres made so far (the first sample's and the one when the bias is first known count too).</summary>
    public int Recentres { get; private set; }

    /// <summary>The pitch the last recentre took as straight ahead, degrees up; 0 on the horizon.</summary>
    public double RecentrePitchDeg
    {
        get
        {
            lock (gate)
            {
                return refPitch * 180 / Math.PI;
            }
        }
    }

    public int Count { get; private set; }
    public bool BiasReady { get; private set; }
    public int RestBlocks { get; private set; }
    public Vec3 LastGyro { get; private set; } = new(0, 0, 0);

    public HeadFusion(Axes axes)
    {
        sign = Sign(axes);
        restMax = axes == Axes.Aura ? RestMaxAura : RestMaxOther;
    }

    /// <summary>
    /// One sample from the glasses, which arrived at [arrived] on the host's clock (seconds, the clock HeadPose's
    /// DrawPose is called with). Left out, it is now (SystemUptime).
    /// </summary>
    public void Report(ImuSample s, double? arrived = null)
    {
        double arrivedS = arrived ?? SystemUptime;
        Vec3 g = s.Gyro * sign;
        Vec3 a = s.Accel * sign;
        HeadSample? made = null;
        Action<HeadSample>? to = null;
        lock (gate)
        {
            if (Integrate(s, g, a))
            {
                long host = clock.ToHost(deviceNs: unchecked((long)s.TimestampNs), arrivedNs: (long)(arrivedS * 1e9));
                var sample = new HeadSample(
                    orientation: (reference.Conjugate * q).Normalized, rate: LastGyro, hostTime: (double)host / 1e9,
                    recentres: Recentres);
                newest = sample;
                made = sample;
                to = sink;
            }
        }
        // Outside the lock, so the receiver may call back into the fusion.
        if (made is HeadSample done && to != null) to(done);
    }

    /// <summary>The fusion's step for one sample (the lock is held). True when the orientation moved on with it.</summary>
    private bool Integrate(ImuSample s, Vec3 g, Vec3 a)
    {
        Count += 1;
        if (lastTs is not ulong last)
        {
            lastTs = s.TimestampNs;
            firstTs = s.TimestampNs;
            LevelFrom(a);
            return false;
        }
        double dt = (double)unchecked((long)(s.TimestampNs - last)) / 1e9;
        lastTs = s.TimestampNs;
        if (!(dt > 0 && dt < 0.05)) return false;
        double an = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        gMag = gMag is double old ? 0.999 * old + 0.001 * an : an;
        LearnBias(g, s.TimestampNs);
        Vec3 w = g - bias;
        LastGyro = w;
        // Tilt: the accelerometer reads the reaction to gravity, "up", in head axes.
        if (an > 0 && gMag is double gm && Math.Abs(an / gm - 1) < 0.1)
        {
            Vec3 upEst = q.Conjugate.Rotate(new Vec3(0, 1, 0));
            Vec3 am = a / an;
            var e = new Vec3(
                am.Y * upEst.Z - am.Z * upEst.Y,
                am.Z * upEst.X - am.X * upEst.Z,
                am.X * upEst.Y - am.Y * upEst.X);
            w += Kp * e;
        }
        double half = 0.5 * dt;
        q = (q * new Quat(1, w.X * half, w.Y * half, w.Z * half)).Normalized;
        if (wantRecentre)
        {
            ApplyRecentre();
            wantRecentre = false;
        }
        return true;
    }

    /// <summary>
    /// Makes the head's heading straight ahead (the lock is held). A recentre the user asked for with the head more
    /// than recentreTiltMinDeg up or down takes that pitch as straight ahead too, never the roll (Pocket XR 1.2.13);
    /// the fusion's own recentres keep the pitch the last one took.
    /// </summary>
    private void ApplyRecentre()
    {
        Vec3 f = q.Rotate(new Vec3(0, 0, -1));
        double headPitch = Math.Asin(HeadMath.Min(1.0, HeadMath.Max(-1.0, f.Y)));
        if (recentreSetsTilt)
        {
            bool past = Math.Abs(headPitch) > RecentreTiltMinDeg * Math.PI / 180;
            refPitch = (recentreFollowsTilt && past) ? headPitch : 0;
            recentreSetsTilt = false;
        }
        double yaw = q.Yaw;
        if (Math.Abs(headPitch) >= NearVerticalDeg * Math.PI / 180)
        {
            // up x right: where the head faces along the floor.
            Vec3 r = q.Rotate(new Vec3(1, 0, 0));
            double sideLength = Math.Sqrt(r.Z * r.Z + r.X * r.X);
            if (sideLength >= 0.2) yaw = Math.Atan2(-r.Z, r.X);
        }
        Quat turn = Quat.AboutY(yaw - recentreYaw);
        reference = refPitch == 0 ? turn : (turn * Quat.AboutX(refPitch)).Normalized;
        Recentres += 1;
    }

    private void LearnBias(Vec3 g, ulong ts)
    {
        biasSum += g;
        biasSq += g * g;
        biasN += 1;
        if (biasN < RestBlock) return;
        double n = (double)biasN;
        Vec3 mean = biasSum / n;
        Vec3 variance = biasSq / n - mean * mean;
        var sd = new Vec3(
            Math.Sqrt(HeadMath.Max(0, variance.X)), Math.Sqrt(HeadMath.Max(0, variance.Y)),
            Math.Sqrt(HeadMath.Max(0, variance.Z)));
        biasSum = Vec3.Zero;
        biasSq = Vec3.Zero;
        biasN = 0;
        double sdMax = Math.Max(sd.X, Math.Max(sd.Y, sd.Z));
        if (!(sdMax < RestSd && Math.Abs(mean.X) < restMax && Math.Abs(mean.Y) < restMax && Math.Abs(mean.Z) < restMax))
        {
            restRunMeans.Clear();
            return;
        }
        restRunMeans.Add(mean);
        if (restRunMeans.Count < RestRun) return;
        Vec3 total = Vec3.Zero;
        foreach (Vec3 m in restRunMeans) total += m;
        Vec3 runMean = total / (double)restRunMeans.Count;
        restRunMeans.Clear();
        double gain = BiasReady ? RestGain : 1.0; // the first two still seconds are taken as they are
        bias += gain * (runMean - bias);
        RestBlocks += 1;
        if (!BiasReady)
        {
            BiasReady = true;
            if ((double)unchecked(ts - firstTs) / 1e9 < FirstRecentreS) wantRecentre = true;
        }
    }

    /// <summary>Starts with tilt from gravity, yaw zero.</summary>
    private void LevelFrom(Vec3 a)
    {
        double n = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        if (!(n > 0)) return;
        Vec3 up = a / n;
        double c = up.Y;
        var axis = new Vec3(-up.Z, 0, up.X); // up x (0, 1, 0)
        double s = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z);
        if (s < 1e-6)
        {
            q = c > 0 ? Quat.Identity : new Quat(0, 1, 0, 0);
            return;
        }
        double ang = Math.Atan2(s, c);
        double k = Math.Sin(ang / 2) / s;
        q = new Quat(Math.Cos(ang / 2), axis.X * k, 0, axis.Z * k).Normalized;
    }

    /// <summary>
    /// The head's heading becomes straight ahead, with the view turned [yawDeg] (left positive), so the panels' middle
    /// comes in front of the head. With [followTilt] and the head more than recentreTiltMinDeg up or down, the head's
    /// pitch becomes straight ahead too, so the screens come to where the head looks. Takes effect with the next sample.
    /// </summary>
    public void Recentre(double yawDeg = 0, bool followTilt = false)
    {
        lock (gate)
        {
            recentreYaw = yawDeg * Math.PI / 180;
            recentreFollowsTilt = followTilt;
            recentreSetsTilt = true;
            wantRecentre = true;
        }
    }

    /// <summary>The newest sample: orientation, rate and host time together. Null before the second report.</summary>
    public HeadSample? Latest()
    {
        lock (gate)
        {
            return newest;
        }
    }

    /// <summary>
    /// Every sample from now on is also handed to [receiver], on the thread that reports (HeadPose's predictor needs
    /// each one, not only the newest). One receiver at a time; null stops it.
    /// </summary>
    public void SetSampleSink(Action<HeadSample>? receiver)
    {
        lock (gate)
        {
            sink = receiver;
        }
    }

    /// <summary>The head's orientation relative to the recentred forward.</summary>
    public Quat View()
    {
        lock (gate)
        {
            return reference.Conjugate * q;
        }
    }
}
