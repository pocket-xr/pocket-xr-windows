using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// Taps on the glasses, read from their own motion sensor: two taps recentre, three go to the next layout. Ported from
/// Pocket XR for Steam Deck's taps.py as of 0.7.11, with its values, from Pierre Zammit's XBX A01+ recordings (about
/// 1000 reports a second): his taps on the frame were 0.33 to 0.51 g away from the 30 ms average, and what set them
/// apart from head movement was how suddenly they started: 0.23 to 0.42 g within 3 ms, where plain head movement never
/// jumped over 0.10 g. (0.4.4 wanted 0.8 g; on Windows he found taps "need a lot of force".)
///
/// A jolt starts when the acceleration is LogG away from its 30 ms average, and ends once it has stayed under LogG
/// for SettleS (the frame's swing after a tap is part of the same jolt). It counts as a tap when it peaked at TapG or
/// more, jumped JerkG or more within 3 ms somewhere in it, lasted no longer than SpikeMaxS (150 ms: his firm taps lasted
/// 61 to 101 ms with their swing), QuietBeforeS passed since the last jolt ended, and the head turned slower than
/// TurnMax over the 150 ms before it. Taps TapGapMinS to TapGapMaxS apart (0.2 to 0.7 s) make a run; once RunEndS passes
/// without another, a run of two or three is reported. Putting the glasses on gave two sharp jolts 134 ms apart: too
/// close together for a run. Every jolt is logged with why it did or didn't count, at most LogMax lines a minute.
///
/// Tap sensitivity, 25% to 300% (100 the values above): above 100 softer, less sudden jolts count (the jolt and jump a
/// tap needs are divided by it), below 100 only firmer ones. On the Deck Pierre found 25% just enough that pressing the
/// glasses' buttons doesn't count. Untested with real taps on Windows.
/// </summary>
public sealed class GlassesTaps
{
    public const double G = 9.81;
    public const double LogG = 0.15, TapG = 0.25, JerkG = 0.15, JerkS = 0.003, SettleS = 0.008, SpikeMaxS = 0.15;
    public const double QuietBeforeS = 0.06, TapGapMinS = 0.2, TapGapMaxS = 0.7, RunEndS = 0.75, WarmupS = 0.5;
    public const double AvgS = 0.03, TurnAvgS = 0.15;
    public static readonly double TurnMax = 60 * Math.PI / 180; // rad/s
    public const int SensitivityMin = 25, SensitivityMax = 300, LogMax = 120;

    private sealed class Jolt
    {
        public double Start, Peak, Jerk, Turn, Quiet;
        public double? Under;
    }

    private readonly Action<int> onTaps;
    private readonly Action<string>? onLog;
    private readonly List<double> logged = new();
    private double? t0, lastT;
    private Vec3 avg;
    private double turn;
    private readonly List<(double T, Vec3 A)> recent = new();
    private double quietFrom;
    private Jolt? jolt;
    private readonly List<double> taps = new();

    public int Sensitivity { get; private set; }
    public double TapGNow { get; private set; }
    public double JerkGNow { get; private set; }
    public double LogGNow { get; private set; }

    /// <param name="onTaps">Called with 2 or 3, on the thread that feeds.</param>
    /// <param name="onLog">Each jolt's line, on the thread that feeds.</param>
    public GlassesTaps(Action<int> onTaps, Action<string>? onLog = null, int sensitivity = 100)
    {
        this.onTaps = onTaps;
        this.onLog = onLog;
        SetSensitivity(sensitivity);
        Reset();
    }

    /// <summary>Tap sensitivity, percent, held to 25 to 300.</summary>
    public void SetSensitivity(double percent)
    {
        double p = double.IsNaN(percent) ? 100 : Math.Clamp(percent, SensitivityMin, SensitivityMax);
        Sensitivity = (int)Math.Round(p);
        double k = 100.0 / p;
        TapGNow = TapG * k;
        JerkGNow = JerkG * k;
        LogGNow = Math.Min(LogG, TapGNow * 0.6);
    }

    public void Reset()
    {
        t0 = null;
        lastT = null;
        avg = Vec3.Zero;
        turn = 0;
        recent.Clear();
        quietFrom = -1e9;
        jolt = null;
        taps.Clear();
    }

    /// <summary>Whether a jolt is going on now (for checks).</summary>
    public bool InJolt => jolt != null;

    /// <summary>One sample as the head tracking stream gives it (time in ns).</summary>
    public void Feed(ImuSample s) => Feed(s.TimestampNs / 1e9, s.Gyro, s.Accel);

    /// <summary>One motion sample: time in seconds, gyro in rad/s, acceleration in m/s².</summary>
    public void Feed(double t, Vec3 gyro, Vec3 accel)
    {
        if (lastT is not double last || !(t - last > 0 && t - last < 0.1))
        {
            Reset(); // the first report, or a gap in the reports: start again
            t0 = t;
            lastT = t;
            avg = accel;
            recent.Add((t, accel));
            return;
        }
        double dt = t - last;
        lastT = t;
        double d = (accel - avg).Length / G;
        avg += (accel - avg) * (1 - Math.Exp(-dt / AvgS));
        Vec3? old = null;
        for (int i = recent.Count - 1; i >= 0; i--)
        {
            if (t - recent[i].T >= JerkS)
            {
                old = recent[i].A;
                break;
            }
        }
        double jump = old is Vec3 o ? (accel - o).Length / G : 0;
        recent.Add((t, accel));
        while (recent.Count > 2 && t - recent[1].T >= JerkS) recent.RemoveAt(0);
        double turnBefore = turn;
        turn += (gyro.Length - turn) * (1 - Math.Exp(-dt / TurnAvgS));
        if (t - t0!.Value < WarmupS)
        {
            quietFrom = t;
            return;
        }
        if (jolt is not Jolt j)
        {
            if (d >= LogGNow) jolt = new Jolt { Start = t, Peak = d, Jerk = jump, Turn = turnBefore, Quiet = t - quietFrom };
        }
        else
        {
            j.Peak = Math.Max(j.Peak, d);
            j.Jerk = Math.Max(j.Jerk, jump);
            if (d >= LogGNow) j.Under = null;
            else j.Under ??= t;
            if ((j.Under is double u && t - u >= SettleS) || t - j.Start > 0.3)
            {
                jolt = null;
                quietFrom = t;
                Judge(j, (j.Under ?? t) - j.Start);
            }
        }
        if (taps.Count > 0 && jolt == null && t - taps[^1] > RunEndS)
        {
            int n = taps.Count;
            taps.Clear();
            if (n is 2 or 3) onTaps(n);
            else Log(t, $"taps: a run of {n} ended: only two or three do something");
        }
    }

    private static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    private void Judge(Jolt j, double lasted)
    {
        double? gap = taps.Count > 0 ? j.Start - taps[^1] : null;
        string? why = null;
        if (j.Peak < TapGNow) why = $"too soft (under {F(TapGNow, "0.00")} g)";
        else if (j.Jerk < JerkGNow) why = $"not sharp (a tap jumps {F(JerkGNow, "0.00")} g within {F(JerkS * 1000, "0")} ms)";
        else if (lasted > SpikeMaxS) why = $"too long (a tap is under {F(SpikeMaxS * 1000, "0")} ms)";
        else if (j.Quiet < QuietBeforeS) why = $"too soon after other movement (needs {F(QuietBeforeS * 1000, "0")} ms still)";
        else if (j.Turn >= TurnMax) why = $"the head was turning (over {F(TurnMax * 180 / Math.PI, "0")} deg/s)";
        else if (gap is double g0 && g0 < TapGapMinS) why = $"too soon after the last tap (under {F(TapGapMinS * 1000, "0")} ms)";
        string verdict;
        if (why == null)
        {
            if (gap is double g1 && g1 > TapGapMaxS) taps.Clear();
            taps.Add(j.Start);
            verdict = $"a tap, {taps.Count} in this run";
        }
        else verdict = "not a tap: " + why;
        string gapText = gap is double g2 ? $"{F(g2 * 1000, "0")} ms" : "none";
        Log(j.Start, $"taps: jolt {F(j.Peak, "0.00")} g (jump {F(j.Jerk, "0.00")} g in 3 ms), {F(lasted * 1000, "0")} ms, " +
            $"still {F(Math.Min(j.Quiet, 9.99) * 1000, "0")} ms before, head turning {F(j.Turn * 180 / Math.PI, "0")} deg/s, " +
            $"last tap {gapText} before: {verdict}");
    }

    private void Log(double t, string line)
    {
        if (onLog == null) return;
        logged.RemoveAll(x => t - x >= 60);
        if (logged.Count >= LogMax) return;
        logged.Add(t);
        onLog(line);
    }
}
