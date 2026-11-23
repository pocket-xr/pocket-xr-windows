using System;
using System.Collections.Generic;

namespace PocketXR.Core;

/// <summary>
/// Taps on the glasses, read from their own motion sensor: two taps recentre, three go to the next layout. Ported from
/// Pocket XR for Steam Deck's taps.py with the same thresholds (asked for by Pierre Zammit, 23 Nov 2026, so the jobs
/// work with no keyboard or controller to hand). Untested with real taps on glasses.
///
/// A tap on the frame is a short, sharp jolt: the acceleration jumps away from its recent average and back within a
/// few hundredths of a second. Head turns and walking move the glasses too, but more slowly. So a tap is:
///   the acceleration at least TapG away from its 30 ms average,
///   back under QuietG within SpikeMaxS,
///   after QuietBeforeS under QuietG (so a jolt in the middle of shaking or a step is left out),
///   while the head turned slower than TurnMax on average over the last 150 ms.
/// Taps TapGapMinS to TapGapMaxS apart make a run; once RunEndS passes without another, a run of two or three is
/// reported. One tap, or four and more, does nothing. The first half second after the stream starts (or after a gap
/// in it) is skipped: a stream's first report can be a junk value.
/// </summary>
public sealed class GlassesTaps
{
    public const double G = 9.81;
    public const double TapG = 0.8;
    public const double QuietG = 0.3;
    public const double SpikeMaxS = 0.04;
    public const double QuietBeforeS = 0.06;
    public static readonly double TurnMax = 60 * Math.PI / 180; // rad/s
    public const double TapGapMinS = 0.08;
    public const double TapGapMaxS = 0.45;
    public const double RunEndS = 0.5;
    public const double WarmupS = 0.5;
    public const double AvgS = 0.03;
    public const double TurnAvgS = 0.15;

    private readonly Action<int> onTaps;
    private double? t0, lastT;
    private Vec3 avg;
    private double turn;
    private double loudAt;
    private (double Start, double TurnBefore)? spike;
    private readonly List<double> taps = new();

    /// <param name="onTaps">Called with 2 or 3, on the thread that feeds.</param>
    public GlassesTaps(Action<int> onTaps)
    {
        this.onTaps = onTaps;
        Reset();
    }

    public void Reset()
    {
        t0 = null;
        lastT = null;
        avg = Vec3.Zero;
        turn = 0;
        loudAt = -1e9;
        spike = null;
        taps.Clear();
    }

    /// <summary>One motion sample: time in seconds, gyro in rad/s, acceleration in m/s².</summary>
    public void Feed(double t, Vec3 gyro, Vec3 accel)
    {
        if (lastT is not double last || !(t - last > 0 && t - last < 0.1))
        {
            Reset(); // the first report, or a gap in the reports: start again
            t0 = t;
            lastT = t;
            avg = accel;
            return;
        }
        double dt = t - last;
        lastT = t;
        double d = (accel - avg).Length / G;
        avg += (accel - avg) * (1 - Math.Exp(-dt / AvgS));
        turn += (gyro.Length - turn) * (1 - Math.Exp(-dt / TurnAvgS));
        if (t - t0!.Value < WarmupS)
        {
            loudAt = t;
            return;
        }
        if (spike is { } sp)
        {
            double start = sp.Start, turnBefore = sp.TurnBefore;
            if (d < QuietG)
            {
                spike = null;
                if (t - start <= SpikeMaxS && turnBefore < TurnMax) Tap(start);
                loudAt = t;
            }
            else if (t - start > SpikeMaxS)
            {
                loudAt = t; // too long for a tap: a push or a knock, wait for it to settle
            }
        }
        else if (d >= TapG && t - loudAt >= QuietBeforeS)
        {
            spike = (t, turn);
        }
        else if (d >= QuietG)
        {
            loudAt = t;
        }
        if (taps.Count > 0 && spike == null && t - taps[^1] > RunEndS)
        {
            int n = taps.Count;
            taps.Clear();
            if (n is 2 or 3) onTaps(n);
        }
    }

    /// <summary>One sample as the head tracking stream gives it (time in ns).</summary>
    public void Feed(ImuSample s) => Feed(s.TimestampNs / 1e9, s.Gyro, s.Accel);

    private void Tap(double start)
    {
        if (taps.Count > 0)
        {
            double gap = start - taps[^1];
            if (gap < TapGapMinS) return; // the same tap ringing on
            if (gap > TapGapMaxS) taps.Clear();
        }
        taps.Add(start);
    }
}
