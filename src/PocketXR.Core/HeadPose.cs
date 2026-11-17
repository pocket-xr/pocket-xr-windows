using System;

namespace PocketXR.Core;

/// <summary>The head tracking choices the pose chain reads each frame, taken from the settings.</summary>
public record struct TrackingOptions
{
    /// <summary>false: identity pose, the screens are head-locked.</summary>
    public bool HeadTracking;
    public TrackingMode Mode;
    public bool DrawSmoothing;
    public bool StillLock;
    /// <summary>Extra prediction for the glasses' own delay, milliseconds.</summary>
    public double DisplayDelayMs;
    public bool RecentreFollowsTilt;
    public bool HeadScroll;

    public TrackingOptions(SpaceSettings s)
    {
        HeadTracking = s.HeadTracking;
        Mode = s.TrackingMode;
        DrawSmoothing = s.DrawSmoothing;
        StillLock = s.StillLock;
        DisplayDelayMs = s.DisplayDelayMs;
        RecentreFollowsTilt = s.RecentreFollowsTilt;
        HeadScroll = s.HeadScroll;
    }
}

/// <summary>
/// Smooth Follow's anchor, ported from Pocket XR's FollowView (0.5.17, 0.5.38): the anchor eases towards the head's
/// turn since Smooth Follow went on, and the screens are drawn with the head relative to it, so they drift back in
/// front. Starting from the head's pose at the switch keeps the view as it was at that moment.
/// (A class here; HeadPose holds the only one.)
/// </summary>
internal sealed class FollowAnchor
{
    /// <summary>Pocket XR's FollowView.FOLLOW_RATE: a 0.5 s time constant, chosen by feel.</summary>
    internal const double FollowRate = 2;

    internal double Rate = FollowRate;
    private Quat anchor = Quat.Identity;
    private Quat? start;

    /// <summary>Moves the anchor on for a frame [dt] seconds long with the tracked pose [q]. With [on] false there is no anchor.</summary>
    internal void Update(Quat q, bool on, double dt)
    {
        if (!on)
        {
            anchor = Quat.Identity;
            start = null;
            return;
        }
        Quat from = q;
        if (start is Quat s)
        {
            from = s;
        }
        else
        {
            start = q;
            anchor = Quat.Identity;
        }
        Quat goal = (q * from.Conjugate).Normalized;
        anchor = HeadMath.Slerp(anchor, goal, 1 - Math.Exp(-dt * Rate));
    }

    /// <summary>The pose to draw for the tracked pose [q].</summary>
    internal Quat Relative(Quat q, bool on)
    {
        return on ? (anchor.Conjugate * q).Normalized : q;
    }

    /// <summary>Smooth Follow starts again from the next pose (a recentre).</summary>
    internal void Restart()
    {
        start = null;
        anchor = Quat.Identity;
    }
}

/// <summary>
/// The orientation the screens are drawn with: Pocket XR's draw chain (HeadTracker.renderPose and the Space's Smooth
/// Follow) in one class. Every sample the fusion makes goes into the predictor; each frame the newest is predicted to
/// the frame's light time, smoothed (DrawSmoothing), held while the head is still (RestLock), and in Smooth Follow
/// taken relative to the anchor that follows the head.
///
/// One HeadPose for a HeadFusion (it takes the fusion's sample sink). Keep both across a reconnect of the glasses, so
/// the last pose is held through the gap. Thread safe: samples come in on the reader's thread, DrawPose on the render
/// thread. Nothing is allocated per sample or per frame.
/// </summary>
public sealed class HeadPose
{
    /// <summary>
    /// The frame's light is taken to leave the glasses this many refresh periods after DrawPose (one queued, half the
    /// frame's own scan-out), plus the glasses' delay.
    /// </summary>
    public const double PipelineFrames = 1.5;

    private readonly HeadFusion fusion;
    private readonly object gate = new();
    private readonly HeadPredictor predictor = new();
    private readonly DrawSmoothing smoothing = new();
    private readonly RestLock restLock = new();
    private readonly HeadScroll scroll = new();
    private readonly FollowAnchor follow = new();

    // All below are read and written with the lock held.
    private double lastSampleTime = 0;
    private bool hasSample = false;
    private int recentresSeen = -1;
    private bool restart = false;
    private double? lastFrameTime;
    private Quat drawn = Quat.Identity;
    private bool heldNow = false;
    private double scrollYaw = 0;
    private double scrollLow = -45;
    private double scrollHigh = 45;
    private int samples = 0;
    private double? rateMarkTime;
    private int rateMarkSamples = 0;
    private int perSecond = 0;

    public HeadPose(HeadFusion fusion)
    {
        this.fusion = fusion;
        // The fusion does not keep the pose alive (the Mac's [weak self]).
        var weak = new WeakReference<HeadPose>(this);
        fusion.SetSampleSink(sample =>
        {
            if (!weak.TryGetTarget(out HeadPose? pose)) return;
            pose.Take(sample);
        });
    }

    /// <summary>One fused sample, on the reader's thread.</summary>
    private void Take(HeadSample s)
    {
        lock (gate)
        {
            if (s.Recentres != recentresSeen)
            {
                // The orientation's frame jumped (a recentre): nothing learnt in the old frame carries over.
                recentresSeen = s.Recentres;
                predictor.Reset();
                restart = true;
            }
            if (HeadMath.IsFinite(s.Orientation) && double.IsFinite(s.Rate.X) && double.IsFinite(s.Rate.Y) && double.IsFinite(s.Rate.Z))
            {
                predictor.Add(s.Orientation, s.Rate, s.HostTime);
                lastSampleTime = s.HostTime;
                hasSample = true;
            }
            samples += 1;
        }
    }

    /// <summary>
    /// The orientation to draw the next frame with (head relative to the recentred forward; x right, y up, z back),
    /// called once per frame on the render thread. `now` is HeadFusion.SystemUptime in seconds, `framePeriod` the
    /// glasses' refresh period in seconds. The pose is predicted to the middle of the frame's light:
    /// now + 1.5 periods + displayDelayMs, never more than 80 ms past the newest sample.
    /// </summary>
    public Quat DrawPose(double now, double framePeriod, TrackingOptions options)
    {
        lock (gate)
        {
            MeasureRate(now);
            double frameDt = 0;
            if (lastFrameTime is double last) frameDt = HeadMath.Clamp(now - last, 0, 0.2);
            lastFrameTime = now;

            if (!options.HeadTracking) return HeadLocked();

            double period = double.IsFinite(framePeriod) ? HeadMath.Clamp(framePeriod, 0, 0.1) : 1.0 / 60.0;
            double delay = double.IsFinite(options.DisplayDelayMs) ? HeadMath.Clamp(options.DisplayDelayMs, 0, 100) / 1000 : 0;
            double target = now + PipelineFrames * period + delay;

            // A pause in the stream keeps the last pose, unpredicted, for up to HeadHold.holdS; then head-locked.
            double since = hasSample ? now - lastSampleTime : double.PositiveInfinity;
            Quat? tracked;
            switch (HeadHold.Use(sinceSampleS: since, hasPose: predictor.Orientation != null))
            {
                case HeadHoldUse.Predict: tracked = predictor.Predict(target); break;
                case HeadHoldUse.Hold: tracked = predictor.Orientation; break;
                default: tracked = null; break;
            }
            if (tracked is not Quat head || !HeadMath.IsFinite(head)) return HeadLocked();

            if (restart)
            {
                restart = false;
                smoothing.Free();
                restLock.Free();
                follow.Restart();
                scroll.Stop();
                scrollYaw = 0;
            }
            smoothing.Enabled = options.DrawSmoothing;
            Quat smoothed = smoothing.Update(head, target);
            restLock.Enabled = options.StillLock;
            Quat steady = restLock.Update(smoothed, target);
            heldNow = restLock.Locked;

            bool following = options.Mode == TrackingMode.SmoothFollow;
            follow.Rate = options.HeadScroll ? HeadScroll.FollowRate : FollowAnchor.FollowRate;
            follow.Update(steady, on: following, dt: frameDt);
            Quat shown = follow.Relative(steady, on: following);

            if (following && options.HeadScroll)
            {
                (double Yaw, double Pitch) look = HeadMath.YawPitchDeg(shown);
                // Pocket XR's Head scroll counts the head's turn right positive.
                double delta = scroll.Step(offsetYawDeg: -look.Yaw, pitchDeg: look.Pitch, dtS: frameDt);
                scrollYaw = HeadScroll.Clamp(viewYawDeg: scrollYaw, deltaDeg: delta, lowDeg: scrollLow, highDeg: scrollHigh);
            }
            else
            {
                scroll.Stop();
                scrollYaw = 0;
            }
            drawn = shown;
            return shown;
        }
    }

    /// <summary>No head to draw with (the lock is held): identity, and every stage starts again from the head when it is back.</summary>
    private Quat HeadLocked()
    {
        smoothing.Free();
        restLock.Free();
        follow.Restart();
        scroll.Stop();
        scrollYaw = 0;
        heldNow = false;
        drawn = Quat.Identity;
        return Quat.Identity;
    }

    /// <summary>Counts the samples of the last second (the lock is held).</summary>
    private void MeasureRate(double now)
    {
        if (rateMarkTime is not double mark)
        {
            rateMarkTime = now;
            rateMarkSamples = samples;
            return;
        }
        double span = now - mark;
        if (span >= 1.0 || span < 0)
        {
            perSecond = span > 0 ? (int)Math.Round((double)(samples - rateMarkSamples) / span, MidpointRounding.AwayFromZero) : 0;
            rateMarkTime = now;
            rateMarkSamples = samples;
        }
    }

    /// <summary>
    /// Yaw the row of screens should be shifted by for Head scroll, degrees (0 when off). Positive turns the row to
    /// the left about the up axis (as Quat.AboutY), which is what a head turned right asks for: the screens on the
    /// right come towards the middle. Only in Smooth Follow with Head scroll on.
    /// </summary>
    public double ScrollYawDeg
    {
        get
        {
            lock (gate)
            {
                return scrollYaw;
            }
        }
    }

    /// <summary>
    /// How far Head scroll may shift the row either way, degrees: the first and last screens' middles, so the row
    /// stops at its ends. -45 to 45 until set.
    /// </summary>
    public void SetScrollRange(double lowDeg, double highDeg)
    {
        lock (gate)
        {
            scrollLow = HeadMath.Min(lowDeg, highDeg);
            scrollHigh = HeadMath.Max(lowDeg, highDeg);
            scrollYaw = HeadMath.Clamp(scrollYaw, scrollLow, scrollHigh);
        }
    }

    /// <summary>
    /// Recentres: the head's heading becomes forward, the view turned `yawDeg` left so the middle of the screens is
    /// ahead; with `followTilt` and the head more than 15 degrees up or down, the screens also come to that pitch.
    /// Takes effect with the glasses' next sample; Smooth Follow and Head scroll start again from there.
    /// </summary>
    public void Recentre(double yawDeg, bool followTilt)
    {
        fusion.Recentre(yawDeg: yawDeg, followTilt: followTilt);
    }

    /// <summary>The Still lock is holding the picture now (for the diagnostics pane).</summary>
    public bool Held
    {
        get
        {
            lock (gate)
            {
                return heldNow;
            }
        }
    }

    /// <summary>The drawn pose's yaw and pitch in degrees (left and up positive), for the status display.</summary>
    public (double Yaw, double Pitch) YawPitchDeg
    {
        get
        {
            lock (gate)
            {
                return HeadMath.YawPitchDeg(drawn);
            }
        }
    }

    /// <summary>Head tracking reports a second, measured over the last second (counted as DrawPose is called).</summary>
    public int ReportsPerSecond
    {
        get
        {
            lock (gate)
            {
                return perSecond;
            }
        }
    }
}
