using System;
using System.Globalization;
using PocketXR.Core;

namespace PocketXR.Checks;

// The head pose chain's checks: the ports of Pocket XR's HeadPrediction, DrawSmoothing0564, RestLock0532 and
// HeadScroll146 tests that need no recorded gyro files, and the whole chain (fusion to drawn pose) on made heads.

public static class TrackingChecks
{
    /// <summary>The same numbers every run, -1 to 1.</summary>
    private struct TrackingRandom
    {
        public ulong State;

        public TrackingRandom(ulong seed) { State = seed; }

        public double Next()
        {
            State = unchecked(State * 6364136223846793005UL + 1442695040888963407UL);
            ulong top = State >> 11;
            return (double)top / 9007199254740992.0 * 2 - 1;
        }
    }

    private const double TrackingRad = Math.PI / 180;
    private const double TrackingFrame = 1.0 / 60.0;

    /// <summary>A turn of [deg] degrees to the left.</summary>
    private static Quat TrackingYaw(double deg) { return Quat.AboutY(deg * TrackingRad); }

    /// <summary>A number in a check's message, written as Swift writes a Double (40.0, not 40).</summary>
    private static string D(double v)
    {
        string text = v.ToString("R", CultureInfo.InvariantCulture);
        return double.IsFinite(v) && !text.Contains('.') && !text.Contains('E') ? text + ".0" : text;
    }

    /// <summary>Made glasses: 1000 reports a second into a fusion, a frame drawn every 16 ms.</summary>
    private sealed class TrackingRig
    {
        public readonly HeadFusion Fusion;
        public readonly HeadPose Pose;
        public TrackingOptions Options;
        public Vec3 Accel;
        public double Noise = 0;
        public TrackingRandom Random = new TrackingRandom(7);
        public int N = 0;
        public Quat Last = Quat.Identity;
        public readonly double Host0 = 1000;

        /// <summary>The head starts [pitchDeg] up.</summary>
        public TrackingRig(double pitchDeg = 0)
        {
            Fusion = new HeadFusion(axes: HeadFusion.Axes.Aura);
            Pose = new HeadPose(fusion: Fusion);
            Options = new TrackingOptions(new SpaceSettings());
            double p = pitchDeg * TrackingRad;
            Accel = new Vec3(0, 9.81 * Math.Cos(p), -9.81 * Math.Sin(p));
        }

        public double Now => Host0 + (double)N / 1000;

        /// <summary>[seconds] of reports with the head turning at [gyro] rad/s; [each] sees every drawn frame.</summary>
        public void Run(double seconds, Vec3? gyro = null, Action<Quat>? each = null)
        {
            Vec3 turning = gyro ?? new Vec3(0, 0, 0);
            int steps = (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
            for (int step = 0; step < steps; step++)
            {
                N += 1;
                Vec3 g = turning;
                if (Noise > 0)
                {
                    // Swift evaluates the three in order, x first.
                    double nx = Random.Next();
                    double ny = Random.Next();
                    double nz = Random.Next();
                    g += new Vec3(nx, ny, nz) * Noise;
                }
                var sample = new ImuSample(timestampNs: (ulong)N * 1_000_000, gyro: g, accel: Accel);
                Fusion.Report(sample, arrived: Now);
                if (N % 16 == 0)
                {
                    Last = Pose.DrawPose(now: Now, framePeriod: TrackingFrame, options: Options);
                    if (each != null) each(Last);
                }
            }
        }

        /// <summary>The glasses send nothing for [seconds].</summary>
        public void Skip(double seconds)
        {
            N += (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        }

        public Quat Draw()
        {
            Last = Pose.DrawPose(now: Now, framePeriod: TrackingFrame, options: Options);
            return Last;
        }
    }

    public static void Run()
    {
        TrackingSettingsChecks();
        TrackingClockChecks();
        TrackingPredictorChecks();
        TrackingSmoothingChecks();
        TrackingRestLockChecks();
        TrackingScrollChecks();
        TrackingChainStillChecks();
        TrackingChainRecentreChecks();
        TrackingChainFollowChecks();
        TrackingChainHoldChecks();
    }

    private static void TrackingSettingsChecks()
    {
        var s = new SpaceSettings();
        var d = new TrackingOptions(s);
        Check.That(d.HeadTracking && d.Mode == TrackingMode.BodyAnchor && d.DrawSmoothing && d.StillLock, "the default settings track, smooth and hold");
        Check.That(d.DisplayDelayMs == 8 && d.RecentreFollowsTilt && !d.HeadScroll, "the default delay, tilt and Head scroll");
        s.HeadTracking = false;
        s.TrackingMode = TrackingMode.SmoothFollow;
        s.DrawSmoothing = false;
        s.StillLock = false;
        s.DisplayDelayMs = 12;
        s.RecentreFollowsTilt = false;
        s.HeadScroll = true;
        var o = new TrackingOptions(s);
        Check.That(!o.HeadTracking && o.Mode == TrackingMode.SmoothFollow && !o.DrawSmoothing && !o.StillLock, "the switches are carried over");
        Check.That(o.DisplayDelayMs == 12 && !o.RecentreFollowsTilt && o.HeadScroll, "the delay, tilt and Head scroll are carried over");
        Check.That(o != d, "options compare by value");
    }

    private static void TrackingClockChecks()
    {
        // 1 kHz samples arriving in 16 ms bursts: after the first burst the offset is the smallest wait, and steady.
        var clock = new ImuClock();
        long offset = 5_000_000_000;
        long lowest = long.MaxValue;
        long highest = long.MinValue;
        double sum = 0;
        int count = 0;
        for (int i = 0; i < 2000; i++)
        {
            long dev = (long)i * 1_000_000;
            long arrived = ((long)(i / 16) + 1) * 16_000_000 + 2_000_000 + offset;
            long mapped = clock.ToHost(deviceNs: dev, arrivedNs: arrived) - dev;
            if (i >= 100)
            {
                lowest = Math.Min(lowest, mapped);
                highest = Math.Max(highest, mapped);
                sum += (double)mapped;
                count += 1;
            }
        }
        Check.That(highest - lowest < 2_000_000, "the sample clock takes the bursts out");
        Check.That(Check.Near(sum / (double)count, (double)(offset + 2_000_000), 1_500_000), "the sample clock's offset is the shortest wait");
        // A reconnect with a new device clock starts again.
        long after = clock.ToHost(deviceNs: 999_000_000_000, arrivedNs: offset + 40_000_000_000);
        Check.That(after == offset + 40_000_000_000, "a jump of the device clock starts the sample clock again");

        Check.That(HeadHold.Use(sinceSampleS: 0.02, hasPose: true) == HeadHoldUse.Predict, "fresh samples are predicted from");
        Check.That(HeadHold.Use(sinceSampleS: 1.0, hasPose: true) == HeadHoldUse.Hold, "a pause holds the last pose");
        Check.That(HeadHold.Use(sinceSampleS: 6.0, hasPose: true) == HeadHoldUse.None, "a long pause goes head-locked");
        Check.That(HeadHold.Use(sinceSampleS: 0.0, hasPose: false) == HeadHoldUse.None, "no pose, nothing to hold");
    }

    private static void TrackingPredictorChecks()
    {
        // A steady turn at 60 deg/s: the pose 30 ms ahead is the true pose then.
        var p = new HeadPredictor();
        double rate = 60 * TrackingRad;
        double t = 0;
        for (int i = 0; i < 500; i++)
        {
            t = (double)i / 1000;
            p.Add(Quat.AboutY(rate * t), new Vec3(0, rate, 0), t);
        }
        Check.That(p.Predict(t - 1) == Quat.AboutY(rate * t), "a target before the newest sample gives the sample");
        if (p.Predict(t + 0.030) is Quat ahead)
        {
            double error = HeadMath.AngleDeg(ahead, Quat.AboutY(rate * (t + 0.030)));
            Check.That(error < 0.01, $"a steady turn is predicted 30 ms ahead to {D(error)} deg");
        }
        else
        {
            Check.That(false, "the predictor has a pose");
        }
        if (p.Predict(t + 0.5) is Quat far)
        {
            double error = HeadMath.AngleDeg(far, Quat.AboutY(rate * (t + 0.080)));
            Check.That(error < 0.01, $"prediction stops at 80 ms ahead ({D(error)} deg off that)");
        }
        Check.That(p.BlendShare == 1 && p.StillWeight == 0, "a turning head is predicted in full");

        // A head speeding up: the acceleration's share brings the prediction closer than the rate alone.
        var q = new HeadPredictor();
        double speedUp = 4; // rad/s^2
        double angle = 0;
        for (int i = 0; i < 500; i++)
        {
            t = (double)i / 1000;
            angle = 0.5 * speedUp * t * t;
            q.Add(Quat.AboutY(angle), new Vec3(0, speedUp * t, 0), t);
        }
        Quat truth = Quat.AboutY(0.5 * speedUp * (t + 0.030) * (t + 0.030));
        if (q.Predict(t + 0.030) is Quat aheadOfSpeedUp)
        {
            double error = HeadMath.AngleDeg(aheadOfSpeedUp, truth);
            double unpredicted = HeadMath.AngleDeg(Quat.AboutY(angle), truth);
            Check.That(error < 0.15 && error < unpredicted / 10, $"a head speeding up is predicted to {D(error)} deg (unpredicted {D(unpredicted)})");
        }

        // A still head with gyro noise gets next to no prediction noise.
        var still = new HeadPredictor();
        var random = new TrackingRandom(3);
        double worst = 0;
        for (int i = 0; i < 2000; i++)
        {
            t = (double)i / 1000;
            double wx = random.Next();
            double wy = random.Next();
            double wz = random.Next();
            Vec3 w = new Vec3(wx, wy, wz) * 0.003;
            still.Add(Quat.Identity, w, t);
            if (i > 1000 && still.Predict(t + 0.030) is Quat aheadOfStill)
            {
                worst = Math.Max(worst, HeadMath.AngleDeg(aheadOfStill, Quat.Identity));
            }
        }
        Check.That(worst < 0.004, $"a still head's gyro noise is not pushed ahead ({D(worst)} deg at most)");

        still.Reset();
        Check.That(still.Predict(1) == null, "no pose after a reset");
    }

    private static void TrackingSmoothingChecks()
    {
        // A steady turn: the drawn pose trails by a bounded angle (Pocket XR's bounds).
        (double Rate, double Most)[] cases = { (20, 1.1), (60, 2.0), (150, 3.2) };
        foreach ((double Rate, double Most) c in cases)
        {
            var s = new DrawSmoothing();
            double lag = 0;
            for (int i = 0; i < 120; i++)
            {
                double t = (double)i * TrackingFrame;
                Quat head = TrackingYaw(c.Rate * t);
                lag = HeadMath.AngleDeg(s.Update(head, t), head);
            }
            Check.That(lag <= c.Most && lag > 0, $"a turn at {D(c.Rate)} deg/s is drawn {D(lag)} deg behind");
        }

        // A head held still with jitter: the drawn pose moves far less from frame to frame.
        var jitter = new DrawSmoothing();
        var random = new TrackingRandom(11);
        Quat? before = null;
        Quat? beforeShown = null;
        double trackedSq = 0;
        double shownSq = 0;
        double worstOff = 0;
        for (int i = 0; i < 240; i++)
        {
            double t = (double)i * TrackingFrame;
            double jitterYaw = 0.03 * random.Next();
            double jitterPitch = 0.03 * random.Next() * TrackingRad;
            Quat head = (TrackingYaw(jitterYaw) * Quat.AboutX(jitterPitch)).Normalized;
            Quat drawn = jitter.Update(head, t);
            if (i > 30 && before is Quat b && beforeShown is Quat bs)
            {
                double a = HeadMath.AngleDeg(b, head);
                double d = HeadMath.AngleDeg(bs, drawn);
                trackedSq += a * a;
                shownSq += d * d;
                worstOff = Math.Max(worstOff, HeadMath.AngleDeg(drawn, head));
            }
            before = head;
            beforeShown = drawn;
        }
        Check.That(Math.Sqrt(shownSq) <= 0.35 * Math.Sqrt(trackedSq), $"smoothing cuts a still head's jitter ({D(Math.Sqrt(shownSq))} of {D(Math.Sqrt(trackedSq))})");
        Check.That(worstOff < 0.1, $"the smoothed pose stays with the still head ({D(worstOff)} deg)");

        // The frame's other poses take the same correction.
        var two = new DrawSmoothing();
        Quat q = Quat.Identity;
        for (int i = 0; i < 30; i++)
        {
            q = TrackingYaw((double)i * 0.5);
            _ = two.Update(q, (double)i * TrackingFrame);
        }
        Quat shown = two.Update(q, 30 * TrackingFrame);
        Check.That(HeadMath.AngleDeg(two.Apply(q), shown) < 1e-6, "another pose of the frame gets the frame's correction");
        Quat again = two.Update(q, 30 * TrackingFrame);
        Check.That(HeadMath.AngleDeg(again, shown) < 1e-6, "a second update for the same frame redoes the step, it does not step again");

        // Off or freed, the head passes as it is.
        var off = new DrawSmoothing();
        _ = off.Update(Quat.Identity, 0);
        Quat turned = TrackingYaw(10);
        off.Free();
        Check.That(off.Update(turned, TrackingFrame) == turned, "freed, smoothing starts from the head");
        off.Enabled = false;
        Quat more = TrackingYaw(20);
        Check.That(off.Update(more, 2 * TrackingFrame) == more && off.Apply(more) == more, "off, smoothing passes the head through");
        // A stall starts from the head too.
        var stall = new DrawSmoothing();
        _ = stall.Update(Quat.Identity, 0);
        _ = stall.Update(Quat.Identity, TrackingFrame);
        Check.That(stall.Update(turned, 1.0) == turned, "after a 0.2 s gap smoothing starts from the head");
    }

    private static void TrackingRestLockChecks()
    {
        // A still head with small jitter is held: the drawn pose does not move at all.
        var restLock = new RestLock();
        var random = new TrackingRandom(5);
        double t = 0;
        Quat? heldPose = null;
        double moved = 0;
        double trackedMoved = 0;
        for (int i = 0; i < 120; i++)
        {
            double jitterYaw = 0.02 * random.Next();
            double jitterPitch = 0.02 * random.Next() * TrackingRad;
            Quat head = (TrackingYaw(jitterYaw) * Quat.AboutX(jitterPitch)).Normalized;
            Quat drawn = restLock.Update(head, t);
            if (i == 30) heldPose = drawn;
            if (i > 30 && heldPose is Quat h)
            {
                moved = Math.Max(moved, HeadMath.AngleDeg(h, drawn));
                trackedMoved = Math.Max(trackedMoved, HeadMath.AngleDeg(h, head));
            }
            t += TrackingFrame;
        }
        Check.That(restLock.Locked, "a still head with jitter is held");
        Check.That(moved < 1e-6 && trackedMoved > 0.005, $"the held picture does not move ({D(moved)} deg) while the head jitters ({D(trackedMoved)} deg)");

        // A turn lets go at once and tracks exactly after the fade.
        var turn = new RestLock();
        Quat q = Quat.Identity;
        t = 0;
        for (int i = 0; i < 60; i++)
        {
            _ = turn.Update(q, t);
            t += TrackingFrame;
        }
        Check.That(turn.Locked, "a second of stillness is held");
        Quat step = TrackingYaw(1); // 60 deg/s
        double maxOff = 0;
        int framesHeld = 0;
        for (int i = 0; i < 30; i++)
        {
            q = (q * step).Normalized;
            Quat drawn = turn.Update(q, t);
            if (turn.Locked) framesHeld += 1;
            maxOff = Math.Max(maxOff, HeadMath.AngleDeg(drawn, q));
            t += TrackingFrame;
        }
        Check.That(framesHeld <= 1, $"a turn lets go within its first frame ({framesHeld} held)");
        Check.That(maxOff <= 1.01, $"never more than the lock's limit behind ({D(maxOff)} deg)");
        q = (q * step).Normalized;
        Check.That(HeadMath.AngleDeg(turn.Update(q, t), q) < 1e-3, "after the fade the drawn pose is the head");

        // A slow drift is followed without a visible step.
        var drift = new RestLock();
        q = Quat.Identity;
        t = 0;
        Quat? prevShown = null;
        double maxStep = 0;
        Quat creep = TrackingYaw(0.2 / 60);
        for (int i = 0; i < 180; i++)
        {
            q = (q * creep).Normalized;
            Quat drawn = drift.Update(q, t);
            if (prevShown is Quat p) maxStep = Math.Max(maxStep, HeadMath.AngleDeg(p, drawn));
            prevShown = drawn;
            t += TrackingFrame;
        }
        Check.That(drift.Locked, "a slow drift stays held");
        Check.That(maxStep <= 0.014, $"a slow drift is followed in steps of {D(maxStep)} deg at most");
        if (prevShown is Quat lastShown) Check.That(HeadMath.AngleDeg(lastShown, q) <= 1.01, "the held pose stays within the limit of the head");

        // The frame's other poses get the held pose; the switch turns the lock off.
        var rows = new RestLock();
        t = 0;
        for (int i = 0; i < 60; i++)
        {
            _ = rows.Update(Quat.Identity, t);
            t += TrackingFrame;
        }
        Quat row = TrackingYaw(0.03);
        Check.That(HeadMath.AngleDeg(rows.Apply(row), Quat.Identity) < 1e-6, "another pose of a held frame is the held pose");
        rows.Enabled = false;
        _ = rows.Update(row, t);
        Check.That(!rows.Locked && HeadMath.AngleDeg(rows.Apply(row), row) < 1e-6, "off, the lock passes the head through");
    }

    private static void TrackingScrollChecks()
    {
        var s = new HeadScroll();
        Check.That(s.Step(offsetYawDeg: 5, pitchDeg: 0, dtS: TrackingFrame) == 0, "inside the dead zone nothing scrolls");
        double total = 0;
        for (int i = 0; i < 120; i++) { total += s.Step(offsetYawDeg: 20, pitchDeg: 0, dtS: TrackingFrame); }
        Check.That(Check.Near(s.SpeedDegS, 30, 0.5), $"10 deg past the dead zone scrolls at 30 deg/s ({D(s.SpeedDegS)})");
        Check.That(total > 40 && total < 60, $"two seconds of it scroll {D(total)} deg");
        for (int i = 0; i < 90; i++) { _ = s.Step(offsetYawDeg: 0, pitchDeg: 0, dtS: TrackingFrame); }
        Check.That(s.SpeedDegS == 0, "looking back to the middle stops the scroll");
        Check.That(HeadScroll.TargetSpeed(offsetYawDeg: 100, pitchDeg: 0) == 45, "the scroll speed has a limit");
        Check.That(HeadScroll.TargetSpeed(offsetYawDeg: -100, pitchDeg: 0) == -45, "and the same to the left");
        Check.That(HeadScroll.TargetSpeed(offsetYawDeg: 20, pitchDeg: 40) == 0, "looking far up or down does not scroll");
        Check.That(s.Step(offsetYawDeg: 20, pitchDeg: 0, dtS: 0) == 0, "no time, no scroll");
        Check.That(HeadScroll.Clamp(viewYawDeg: 40, deltaDeg: 10, lowDeg: -45, highDeg: 45) == 45, "the row stops at its end");
        Check.That(HeadScroll.Clamp(viewYawDeg: 50, deltaDeg: 1, lowDeg: -45, highDeg: 45) == 50, "a view past the end goes no further out");
        Check.That(HeadScroll.Clamp(viewYawDeg: 50, deltaDeg: -1, lowDeg: -45, highDeg: 45) == 49, "but may come back");
    }

    /// <summary>The whole chain on a still head with gyro noise, then a turn; and head tracking off.</summary>
    private static void TrackingChainStillChecks()
    {
        var rig = new TrackingRig();
        rig.Noise = 0.003;
        rig.Run(3.0);
        Check.That(rig.Fusion.BiasReady, "two still seconds teach the bias");
        Check.That(rig.Pose.Held, "a still head is held by the Still lock");
        Check.That(Math.Abs(rig.Pose.ReportsPerSecond - 1000) <= 5, $"the report rate is measured ({rig.Pose.ReportsPerSecond})");
        Quat heldPose = rig.Last;
        double moved = 0;
        rig.Run(1.0, each: q => { moved = Math.Max(moved, HeadMath.AngleDeg(heldPose, q)); });
        Check.That(moved < 1e-6, $"the held picture does not move with the gyro's noise ({D(moved)} deg)");
        Check.That(Math.Abs(rig.Pose.YawPitchDeg.Yaw) < 0.2 && Math.Abs(rig.Pose.YawPitchDeg.Pitch) < 0.2, "a still head reads straight ahead");

        // A turn to the left at 30 deg/s lets go and is followed closely.
        double rate = 30 * TrackingRad;
        rig.Run(0.3, gyro: new Vec3(0, rate, 0));
        Check.That(!rig.Pose.Held, "a turn lets the Still lock go");
        rig.Run(0.7, gyro: new Vec3(0, rate, 0));
        double yaw = rig.Pose.YawPitchDeg.Yaw;
        Check.That(Check.Near(yaw, 30, 2.0), $"a second at 30 deg/s to the left is drawn at {D(yaw)} deg");

        // Without smoothing and lock the drawn pose is the predicted head: ahead of the measured one in a turn.
        rig.Options.DrawSmoothing = false;
        rig.Options.StillLock = false;
        // 5000 reports so far; 504 more end on a drawn frame (every 16th), so the frame's pose is from the newest report.
        rig.Run(0.504, gyro: new Vec3(0, rate, 0));
        double plain = rig.Pose.YawPitchDeg.Yaw;
        double measured = rig.Fusion.Latest() is HeadSample newest ? HeadMath.YawPitchDeg(newest.Orientation).Yaw : 0;
        // 1.5 frames and 8 ms at 30 deg/s is 0.99 deg.
        Check.That(Check.Near(plain - measured, 0.99, 0.1), $"the pose is predicted to the frame's light ({D(plain - measured)} deg ahead)");
        Check.That(!rig.Pose.Held, "with the Still lock off nothing is held");

        // Head tracking off: the screens are head-locked.
        rig.Options.HeadTracking = false;
        rig.Run(0.2, gyro: new Vec3(0, rate, 0));
        Check.That(rig.Last == Quat.Identity && !rig.Pose.Held, "head tracking off gives the identity pose");
        Check.That(rig.Pose.YawPitchDeg.Yaw == 0 && rig.Pose.ScrollYawDeg == 0, "and reads straight ahead");
    }

    private static void TrackingChainRecentreChecks()
    {
        // Recentre makes the current heading forward.
        var rig = new TrackingRig();
        rig.Run(2.5);
        rig.Run(0.698, gyro: new Vec3(0, 1, 0)); // 40 degrees to the left
        rig.Run(1.0);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 40, 1.0), $"the head turned 40 deg left is drawn there ({D(rig.Pose.YawPitchDeg.Yaw)})");
        int before = rig.Fusion.Recentres;
        rig.Pose.Recentre(yawDeg: 0, followTilt: false);
        rig.Run(0.5);
        Check.That(rig.Fusion.Recentres == before + 1, "one recentre is made");
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 0, 0.5), $"recentre makes the heading forward ({D(rig.Pose.YawPitchDeg.Yaw)})");
        rig.Pose.Recentre(yawDeg: 10, followTilt: false);
        rig.Run(0.5);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 10, 0.5), $"recentre with the screens' middle 10 deg left puts the head there ({D(rig.Pose.YawPitchDeg.Yaw)})");

        // A head 25 degrees up: the horizon unless recentre follows the tilt.
        var up = new TrackingRig(pitchDeg: 25);
        up.Run(2.5);
        Check.That(Check.Near(up.Pose.YawPitchDeg.Pitch, 25, 0.5), $"a head 25 deg up is drawn so ({D(up.Pose.YawPitchDeg.Pitch)})");
        up.Pose.Recentre(yawDeg: 0, followTilt: false);
        up.Run(0.5);
        Check.That(Check.Near(up.Pose.YawPitchDeg.Pitch, 25, 0.5) && up.Fusion.RecentrePitchDeg == 0, "a plain recentre keeps the screens on the horizon");
        up.Pose.Recentre(yawDeg: 0, followTilt: true);
        up.Run(0.5);
        Check.That(Check.Near(up.Fusion.RecentrePitchDeg, 25, 0.5), $"a recentre that follows the tilt takes the head's pitch ({D(up.Fusion.RecentrePitchDeg)})");
        Check.That(Check.Near(up.Pose.YawPitchDeg.Pitch, 0, 0.5), $"so the screens come to where the head looks ({D(up.Pose.YawPitchDeg.Pitch)})");
        Check.That(Check.Near(up.Pose.YawPitchDeg.Yaw, 0, 0.5), "and the heading is forward");

        // Within 15 degrees of level the horizon is kept.
        var low = new TrackingRig(pitchDeg: 10);
        low.Run(2.5);
        low.Pose.Recentre(yawDeg: 0, followTilt: true);
        low.Run(0.5);
        Check.That(low.Fusion.RecentrePitchDeg == 0 && Check.Near(low.Pose.YawPitchDeg.Pitch, 10, 0.5), "a head 10 deg up keeps the horizon");
    }

    private static void TrackingChainFollowChecks()
    {
        // Smooth Follow: the screens drift back in front of a head that turned and stayed.
        var rig = new TrackingRig();
        rig.Options.Mode = TrackingMode.SmoothFollow;
        rig.Run(2.5);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 0, 0.2), "Smooth Follow starts with the view as it was");
        rig.Run(0.5, gyro: new Vec3(0, 60 * TrackingRad, 0)); // 30 degrees to the left
        double turned = rig.Pose.YawPitchDeg.Yaw;
        Check.That(turned > 12 && turned < 26, $"right after the turn the screens are still off to the side ({D(turned)} deg)");
        rig.Run(4.0);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 0, 0.5), $"Smooth Follow converges on the head ({D(rig.Pose.YawPitchDeg.Yaw)} deg left)");
        Check.That(rig.Pose.ScrollYawDeg == 0, "Head scroll off shifts nothing");

        // Body Anchor leaves the screens where they are in the room.
        var room = new TrackingRig();
        room.Run(2.5);
        room.Run(0.5, gyro: new Vec3(0, 60 * TrackingRad, 0));
        room.Run(4.0);
        Check.That(Check.Near(room.Pose.YawPitchDeg.Yaw, 30, 1.0), $"Body Anchor keeps the screens in the room ({D(room.Pose.YawPitchDeg.Yaw)})");

        // Head scroll: a head held 20 degrees to the right slides the row until its end.
        var scroll = new TrackingRig();
        scroll.Options.Mode = TrackingMode.SmoothFollow;
        scroll.Options.HeadScroll = true;
        scroll.Pose.SetScrollRange(lowDeg: -25, highDeg: 25);
        scroll.Run(2.5);
        scroll.Run(0.5, gyro: new Vec3(0, -40 * TrackingRad, 0)); // 20 degrees to the right
        scroll.Run(0.5);
        double slid = scroll.Pose.ScrollYawDeg;
        Check.That(slid > 1, $"a head turned right past the dead zone scrolls the row ({D(slid)} deg)");
        scroll.Run(6.0);
        double end = scroll.Pose.ScrollYawDeg;
        Check.That(end >= slid && end <= 25, $"the row scrolls on and stops at its end ({D(end)} deg)");
        scroll.Options.HeadScroll = false;
        scroll.Run(0.1);
        Check.That(scroll.Pose.ScrollYawDeg == 0, "Head scroll turned off shifts nothing");
    }

    private static void TrackingChainHoldChecks()
    {
        // The stream stops: the last pose holds for 5 s, then the view is head-locked; it carries on when it is back.
        var rig = new TrackingRig();
        rig.Run(2.5);
        rig.Run(0.349, gyro: new Vec3(0, 1, 0)); // 20 degrees to the left
        rig.Run(1.0);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 20, 1.0), "the head turned 20 deg left is drawn there");
        rig.Skip(1.0);
        _ = rig.Draw();
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 20, 1.0), $"a 1 s pause in the stream holds the last pose ({D(rig.Pose.YawPitchDeg.Yaw)})");
        rig.Skip(5.0);
        Check.That(rig.Draw() == Quat.Identity, "after 5 s without reports the view is head-locked");
        rig.Run(0.2);
        Check.That(Check.Near(rig.Pose.YawPitchDeg.Yaw, 20, 1.0), $"the stream back, the pose carries on from where it was ({D(rig.Pose.YawPitchDeg.Yaw)})");
    }
}
