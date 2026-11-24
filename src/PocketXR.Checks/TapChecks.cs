using System;
using System.Collections.Generic;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>Taps on the glasses: the Steam Deck's tests (taps.py), with the same synthetic taps, turns and steps.</summary>
public static class TapChecks
{
    private const double Hz = 1000.0; // the XBX sends about 1000 reports a second

    private static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>A head at rest (gravity along y) plus accel(t) in g and gyro(t) in deg/s, with a tap of tapG for tapS
    /// at each time in tapsAt, ringing down after it; the runs reported.</summary>
    private static List<int> Run(double seconds, Func<double, Vec3>? accel = null, Func<double, Vec3>? gyro = null,
        double[]? tapsAt = null, double tapG = 2.0, double tapS = 0.004, double t0 = 100.0, double skipFrom = -1, double skipTo = -1)
    {
        var got = new List<int>();
        var tp = new GlassesTaps(got.Add);
        int n = (int)(seconds * Hz);
        for (int i = 0; i < n; i++)
        {
            double t = i / Hz;
            if (t >= skipFrom && t < skipTo) continue;
            Vec3 a = accel?.Invoke(t) ?? Vec3.Zero;
            double ax = a.X, ay = a.Y + 1.0, az = a.Z;
            foreach (double at in tapsAt ?? Array.Empty<double>())
            {
                if (at <= t && t < at + tapS) az += tapG;
                else if (at + tapS <= t && t < at + 0.03) az += 0.1 * Math.Sin((t - at) * 2 * Math.PI * 150); // ringing, well under QuietG
            }
            Vec3 w = gyro?.Invoke(t) ?? Vec3.Zero;
            tp.Feed(t0 + t, new Vec3(Rad(w.X), Rad(w.Y), Rad(w.Z)), new Vec3(ax, ay, az) * GlassesTaps.G);
        }
        return got;
    }

    private static string Show(List<int> l) => "[" + string.Join(",", l) + "]";

    public static void Run()
    {
        var two = Run(2.5, tapsAt: new[] { 1.0, 1.25 });
        Check.That(two.Count == 1 && two[0] == 2, $"two taps are a run of 2 {Show(two)}");
        var three = Run(2.5, tapsAt: new[] { 1.0, 1.2, 1.4 });
        Check.That(three.Count == 1 && three[0] == 3, $"three taps are a run of 3 {Show(three)}");

        Check.That(Run(2.5, tapsAt: new[] { 1.0 }).Count == 0, "one tap does nothing");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.2, 1.4, 1.6 }).Count == 0, "four taps do nothing");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.7 }).Count == 0, "taps too far apart are not a run");

        Check.That(Run(2.5, tapsAt: new[] { 1.0, 1.25 }, tapG: 0.5).Count == 0, "soft jolts are not taps");
        Check.That(Run(2.5, tapsAt: new[] { 1.0, 1.25 }, tapS: 0.08).Count == 0, "a push (80 ms) is not a tap");
        Check.That(Run(1.5, tapsAt: new[] { 0.05, 0.25 }).Count == 0, "the first half second is skipped (a junk first report)");

        // Head turns: side to side up to 150 deg/s with some tilt, and a jolt during a fast turn.
        Func<double, Vec3> turning = t => new Vec3(0, 150.0 * Math.Sin(t * 2 * Math.PI * 0.7), 0);
        Func<double, Vec3> tilt = t => new Vec3(0.2 * Math.Sin(t * 2 * Math.PI * 0.7), 0, 0.1 * Math.Cos(t * 2 * Math.PI * 0.7));
        Check.That(Run(6.0, accel: tilt, gyro: turning).Count == 0, "head turns alone are not taps");
        Check.That(Run(2.5, gyro: _ => new Vec3(0, 120.0, 0), tapsAt: new[] { 1.0, 1.25 }).Count == 0, "a jolt during a fast turn is left out");

        // Walking: 2 steps a second, a sway of 0.3 g and a heel strike of 0.7 g over 20 ms at each step.
        Func<double, Vec3> steps = t =>
        {
            double p = t % 0.5;
            double strike = p < 0.02 ? 0.7 * Math.Sin(Math.PI * p / 0.02) : 0.0;
            return new Vec3(0.05 * Math.Sin(t * 2 * Math.PI), 0.3 * Math.Sin(t * 4 * Math.PI) + strike, 0);
        };
        Check.That(Run(10.0, accel: steps, gyro: t => new Vec3(0, 20.0 * Math.Sin(t * 2 * Math.PI), 0)).Count == 0, "walking is not taps");
        // Hard steps over the tap level, but 50 ms long.
        Func<double, Vec3> stomps = t =>
        {
            double p = t % 0.5;
            return new Vec3(0, p < 0.05 ? 1.0 * Math.Sin(Math.PI * p / 0.05) : 0.0, 0);
        };
        Check.That(Run(6.0, accel: stomps).Count == 0, "hard steps over the tap level stay out");

        // A gap in the reports starts again (and its half second is skipped after it).
        Check.That(Run(3.0, tapsAt: new[] { 1.6, 1.8 }, skipFrom: 1.2, skipTo: 1.5).Count == 0, "after a gap, taps in its first half second do nothing");
        var after = Run(3.5, tapsAt: new[] { 2.2, 2.4 }, skipFrom: 1.2, skipTo: 1.5);
        Check.That(after.Count == 1 && after[0] == 2, $"and later ones count again {Show(after)}");

        // A second start (RunningCopy): the same copy is shown; another version, another file or an older copy that
        // keeps no record is asked to quit (a tester's 0.4.1 kept running after he installed 0.4.5 and 0.4.7).
        string rec = RunningCopy.Text("0.4.8", @"C:\PXR\PocketXR.exe", 1234);
        Check.That(RunningCopy.Read(rec) == ("0.4.8", @"C:\PXR\PocketXR.exe", 1234), "the running copy's record reads back");
        Check.That(RunningCopy.Read(null) == null && RunningCopy.Read("") == null && RunningCopy.Read("0.4.8\n") == null, "no record, or a broken one, reads as none");
        var r = RunningCopy.Read(rec);
        Check.That(RunningCopy.Decide(r, true, "0.4.8", @"c:\pxr\pocketxr.exe") == RunningCopy.Choice.ShowRunning, "the same copy: shown, as before");
        Check.That(RunningCopy.Decide(r, true, "0.4.9", @"C:\PXR\PocketXR.exe") == RunningCopy.Choice.Replace, "another version: replaced");
        Check.That(RunningCopy.Decide(r, true, "0.4.8", @"D:\New\PocketXR.exe") == RunningCopy.Choice.Replace, "another file: replaced");
        Check.That(RunningCopy.Decide(null, false, "0.4.8", @"C:\PXR\PocketXR.exe") == RunningCopy.Choice.Replace, "an older copy with no record: replaced");
        Check.That(RunningCopy.Decide(r, false, "0.4.8", @"C:\PXR\PocketXR.exe") == RunningCopy.Choice.Replace, "a record left by a copy no longer running: not trusted");
        Check.That(RunningCopy.Describe(null, false) == "an older version" && RunningCopy.Describe(r, true) == "0.4.8", "what it is called in messages");

        Check.That(!new SpaceSettings().Taps, "taps are off by default");
        var on = new SpaceSettings { Taps = true };
        Check.That(SpaceSettings.Decode(on.Encode()).Taps, "the switch is saved");
    }
}
