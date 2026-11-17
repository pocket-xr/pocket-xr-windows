using System;

namespace PocketXR.Core;

/// <summary>
/// The still head's lock, ported from Pocket XR's RestLock.kt (0.5.32 to 0.5.41).
///
/// A worn head is never still: a 4 to 12 Hz tremor moves the windows by a pixel or two, and sub-pixel motion makes
/// text edges shimmer. Here, while the head's slow motion (its rate through a two pole [cutoffHz] low pass, which the
/// tremor barely gets through) stays under [enterDegS] for [enterS], the view holds one pose: the tremor moves nothing
/// on the display. A slow drift past [followDevDeg] is followed at [followDegS] at most, too slowly to see. A real move
/// (the slow rate over [releaseDegS], or the head [maxDevDeg] from the held pose) lets go at once: the view tracks the
/// head from that frame, with the held pose's small offset faded out over [releaseS] instead of a jump.
///
/// [update] once a frame with the frame's pose; [apply] for another pose of that frame. Not thread safe: HeadPose
/// holds its lock for every call.
/// </summary>
public sealed class RestLock
{
    /// <summary>Poses this close in time are one frame's, seconds (a refresh at 120 Hz is 8.3 ms).</summary>
    public const double SameFrameS = 0.004;

    public readonly double EnterDegS;
    public readonly double ReleaseDegS;
    public readonly double EnterS;
    public readonly double MaxDevDeg;
    public readonly double FollowDevDeg;
    public readonly double FollowDegS;
    public readonly double ReleaseS;
    public readonly double CutoffHz;
    /// <summary>The slow pose's time constant, seconds.</summary>
    public readonly double FollowTauS;
    /// <summary>How far the head may sit from the held pose before the hold is drawn along with it.</summary>
    public readonly double BandDeg;
    /// <summary>The time constant of the pose the band follows: a deliberate wobble gets through, the tremor barely.</summary>
    public readonly double BandTauS;

    /// <summary>Off: every pose passes as it is.</summary>
    public bool Enabled = true;

    public bool Locked { get; private set; }
    /// <summary>The head's slow rate now, deg/s.</summary>
    public double SpeedDegS { get; private set; }

    private Quat? prev;
    private double prevTime = 0;
    private Vec3 v1 = new(0, 0, 0);
    private Vec3 v2 = new(0, 0, 0);
    private Quat slow = Quat.Identity;
    private Quat mid = Quat.Identity;
    private Quat? held;
    private double? calmSince;
    private Quat? releaseOffset;
    private double releaseStart = 0;
    // This frame's correction: shown = tracked x correction (none when free).
    private Quat correction = Quat.Identity;
    private bool hasCorrection = false;

    public RestLock(
        double enterDegS = 3,
        double releaseDegS = 5,
        double enterS = 0.120,
        double maxDevDeg = 1.0,
        double followDevDeg = 0.3,
        double followDegS = 0.3,
        double releaseS = 0.100,
        double cutoffHz = 2,
        double followTauS = 2.0,
        double bandDeg = 0.3,
        double bandTauS = 0.1)
    {
        EnterDegS = enterDegS;
        ReleaseDegS = releaseDegS;
        EnterS = enterS;
        MaxDevDeg = maxDevDeg;
        FollowDevDeg = followDevDeg;
        FollowDegS = followDegS;
        ReleaseS = releaseS;
        CutoffHz = cutoffHz;
        FollowTauS = followTauS;
        BandDeg = bandDeg;
        BandTauS = bandTauS;
    }

    /// <summary>The pose to draw this frame for the tracked (predicted, smoothed) pose [q] at [now] seconds.</summary>
    public Quat Update(Quat q, double now)
    {
        // A call within sameFrameS of the last one is that frame's again: it gets the frame's hold or correction and
        // moves nothing on.
        if (prev != null)
        {
            double gap = now - prevTime;
            if (gap >= 0 && gap <= SameFrameS) return Apply(q);
        }
        double dt = 0;
        if (prev != null) dt = now - prevTime;
        if (prev is Quat p && dt > 0 && dt <= 0.2)
        {
            Vec3 v = HeadMath.TurnVector(p, q) * (1 / dt);
            double a = 1 - Math.Exp(-2 * Math.PI * CutoffHz * dt);
            v1 = HeadMath.Toward(v1, v, a);
            v2 = HeadMath.Toward(v2, v1, a);
            slow = HeadMath.Slerp(slow, q, 1 - Math.Exp(-dt / FollowTauS));
            mid = HeadMath.Slerp(mid, q, 1 - Math.Exp(-dt / BandTauS));
        }
        else
        {
            v1 = new Vec3(0, 0, 0);
            v2 = new Vec3(0, 0, 0);
            slow = q;
            mid = q;
        }
        prev = q;
        prevTime = now;
        SpeedDegS = HeadMath.Length(v2) * HeadMath.DegPerRad;
        if (!Enabled)
        {
            Free();
            return q;
        }
        if (Locked && held is Quat h)
        {
            double dev = HeadMath.AngleDeg(h, q);
            if (SpeedDegS > ReleaseDegS || dev > MaxDevDeg)
            {
                Locked = false;
                held = null;
                // The offset the view already had on the last frame (at most maxDevDeg), not this frame's whole turn:
                // from here the view moves with the head, only that small offset fading out.
                releaseOffset = hasCorrection ? correction : null;
                releaseStart = now;
            }
            else
            {
                // The hold is drawn along once the head (through a bandTauS low pass, which the tremor barely gets
                // through) is bandDeg from it, so a small deliberate wobble moves the view as it should, only bandDeg
                // behind; the tremor, smaller, still moves nothing. The low pass's lag is made up with the slow rate.
                Quat kept = h;
                Quat lead = HeadMath.TurnedBy(mid, v2, BandTauS);
                double midDev = HeadMath.AngleDeg(h, lead);
                if (midDev > BandDeg)
                {
                    kept = HeadMath.Slerp(h, lead, (midDev - BandDeg) / midDev);
                    held = kept;
                }
                double drift = HeadMath.AngleDeg(kept, slow);
                if (drift > FollowDevDeg && dt > 0)
                {
                    double stepDeg = HeadMath.Min(FollowDegS * dt, drift - FollowDevDeg);
                    kept = HeadMath.Slerp(kept, slow, stepDeg / drift);
                    held = kept;
                }
                correction = (q.Conjugate * kept).Normalized;
                hasCorrection = true;
                return kept;
            }
        }
        // Free, or letting go: the head as tracked, with what is left of the held offset.
        Quat shown = q;
        if (releaseOffset is Quat off)
        {
            double w = HeadMath.Clamp((now - releaseStart) / ReleaseS, 0, 1);
            if (w >= 1)
            {
                releaseOffset = null;
                correction = Quat.Identity;
                hasCorrection = false;
            }
            else
            {
                correction = HeadMath.Slerp(off, Quat.Identity, HeadMath.Smooth(w));
                hasCorrection = true;
                shown = (q * correction).Normalized;
            }
        }
        else
        {
            correction = Quat.Identity;
            hasCorrection = false;
        }
        if (SpeedDegS < EnterDegS && releaseOffset == null)
        {
            if (calmSince is double since)
            {
                if (now - since >= EnterS)
                {
                    Locked = true;
                    held = shown;
                    calmSince = null;
                }
            }
            else
            {
                calmSince = now;
            }
        }
        else
        {
            calmSince = null;
        }
        return shown;
    }

    /// <summary>Another pose of the frame [update] last saw: the held pose, or the same correction.</summary>
    public Quat Apply(Quat q)
    {
        if (!Enabled) return q;
        if (Locked && held is Quat h) return h;
        return hasCorrection ? (q * correction).Normalized : q;
    }

    /// <summary>Lets go at once with no blend (a recentre, a reconnect, the switch turned off).</summary>
    public void Free()
    {
        Locked = false;
        held = null;
        releaseOffset = null;
        correction = Quat.Identity;
        hasCorrection = false;
        calmSince = null;
    }
}
