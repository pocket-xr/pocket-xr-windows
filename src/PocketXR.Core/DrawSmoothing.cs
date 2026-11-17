using System;

namespace PocketXR.Core;

/// <summary>
/// A One Euro filter on the orientation the screens are drawn with, ported from Pocket XR's DrawSmoothing.kt (0.5.64).
///
/// On Mike's Flip 7 with his Aura an unfiltered head pose changed yaw direction 10 to 18 times a second while the head
/// was held still, and the windows shook with small movements; this filter on the drawn orientation fixed it.
///
/// Each frame the drawn orientation moves toward the predicted one by a share set from a cutoff of [minCutoffHz] plus
/// [beta] times how far behind it is, in degrees per 60 Hz frame. Still, the tremor is cut at about 1 Hz; in a turn the
/// cutoff opens with the lag, so it stays a few degrees at most. The lag term counts per 60 Hz frame whatever the
/// display's rate, so 120 Hz behaves as 60 Hz does.
///
/// [update] once a frame with the frame's pose; [apply] for another pose of that frame, which takes the same
/// correction. Not thread safe: HeadPose holds its lock for every call.
/// </summary>
public sealed class DrawSmoothing
{
    /// <summary>The frame rate the filter was tuned at: the lag term is in degrees per frame at this rate.</summary>
    public const double ReferenceHz = 60;
    /// <summary>A longer gap than this restarts from the head, seconds.</summary>
    public const double MaxGapS = 0.2;
    /// <summary>Two updates this close together are the same frame's, seconds (under half a 120 Hz frame).</summary>
    public const double SameFrameS = 0.002;

    public readonly double MinCutoffHz;
    public readonly double Beta;

    /// <summary>Off: every pose passes as it is.</summary>
    public bool Enabled = true;

    private Quat? filtered;
    private double? lastTime;
    // The filter as it was before this frame's step, so a second update for the same frame redoes the step.
    private Quat? before;
    private double? beforeTime;
    private Quat correction = Quat.Identity;
    private bool hasCorrection = false;

    public DrawSmoothing(double minCutoffHz = 1.0, double beta = 0.04)
    {
        MinCutoffHz = minCutoffHz;
        Beta = beta;
    }

    /// <summary>The orientation to draw for the frame's pose [q] at [now] seconds.</summary>
    public Quat Update(Quat q, double now)
    {
        if (!Enabled)
        {
            Free();
            return q;
        }
        bool sameFrame = false;
        if (lastTime is double lastSeen)
        {
            double gap = now - lastSeen;
            sameFrame = gap >= 0 && gap <= SameFrameS;
        }
        if (sameFrame)
        {
            filtered = before;
            lastTime = beforeTime;
        }
        else
        {
            before = filtered;
            beforeTime = lastTime;
        }
        Quat? previous = filtered;
        double dt = 0;
        if (lastTime is double last) dt = now - last;
        lastTime = now;
        // The first frame, or a stall (a pause, a reconnect): start from the head, not from where it was.
        if (previous is not Quat prev || !(dt > 0 && dt <= MaxGapS))
        {
            filtered = q;
            correction = Quat.Identity;
            hasCorrection = false;
            return q;
        }
        double lagDeg = HeadMath.AngleDeg(prev, q);
        double cutoff = MinCutoffHz + Beta * lagDeg * ReferenceHz;
        double tau = 1 / (2 * Math.PI * cutoff);
        double alpha = 1 / (1 + tau / dt);
        Quat f = HeadMath.Slerp(prev, q, alpha);
        filtered = f;
        correction = (q.Conjugate * f).Normalized;
        hasCorrection = true;
        return f;
    }

    /// <summary>Another pose of the frame [update] last saw: the same correction.</summary>
    public Quat Apply(Quat q)
    {
        if (!Enabled || !hasCorrection) return q;
        return (q * correction).Normalized;
    }

    /// <summary>Starts again from the head with no blend (a recentre, a reconnect, the switch turned off).</summary>
    public void Free()
    {
        filtered = null;
        lastTime = null;
        before = null;
        beforeTime = null;
        correction = Quat.Identity;
        hasCorrection = false;
    }
}
