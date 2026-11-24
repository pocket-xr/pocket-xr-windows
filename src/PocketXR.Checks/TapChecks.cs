using System;
using System.Collections.Generic;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>Taps on the glasses: the Steam Deck's tests (0.7.5 to 0.7.11), with the same made-up taps, turns and steps.</summary>
public static class TapChecks
{
    private const double Hz = 1000.0; // the XBX sends about 1000 reports a second

    private static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>A head at rest (gravity along y) plus accel(t) in g and gyro(t) in deg/s, with a tap of tapG for tapS at
    /// each time in tapsAt, the frame ringing at ringG for ringS after it; the runs reported.</summary>
    private static List<int> Run(double seconds, Func<double, Vec3>? accel = null, Func<double, Vec3>? gyro = null,
        double[]? tapsAt = null, double tapG = 2.0, double tapS = 0.004, double t0 = 100.0, double ringG = 0.1, double ringS = 0.04,
        List<string>? lines = null, int sensitivity = 100)
    {
        var got = new List<int>();
        var tp = new GlassesTaps(got.Add, lines == null ? null : lines.Add, sensitivity);
        int n = (int)(seconds * Hz);
        for (int i = 0; i < n; i++)
        {
            double t = i / Hz;
            Vec3 a = accel?.Invoke(t) ?? Vec3.Zero;
            double ax = a.X, ay = a.Y + 1.0, az = a.Z;
            foreach (double at in tapsAt ?? Array.Empty<double>())
            {
                if (at <= t && t < at + tapS) az += tapG;
                else if (at + tapS <= t && t < at + ringS) az += ringG * Math.Sin((t - at) * 2 * Math.PI * 150);
            }
            Vec3 w = gyro?.Invoke(t) ?? Vec3.Zero;
            tp.Feed(t0 + t, new Vec3(Rad(w.X), Rad(w.Y), Rad(w.Z)), new Vec3(ax, ay, az) * GlassesTaps.G);
        }
        return got;
    }

    private static bool Is(List<int> l, params int[] want) => l.SequenceEqual(want);
    private static string Show(List<int> l) => "[" + string.Join(",", l) + "]";

    public static void Run()
    {
        var two = Run(2.5, tapsAt: new[] { 1.0, 1.25 });
        Check.That(Is(two, 2), $"two taps are a run of 2 {Show(two)}");
        var three = Run(2.5, tapsAt: new[] { 1.0, 1.3, 1.6 });
        Check.That(Is(three, 3), $"three taps are a run of 3 {Show(three)}");
        Check.That(Run(2.5, tapsAt: new[] { 1.0 }).Count == 0, "one tap does nothing");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.25, 1.5, 1.75 }).Count == 0, "four taps do nothing");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.9 }).Count == 0, "taps too far apart are not a run");

        Check.That(Run(2.5, tapsAt: new[] { 1.0, 1.25 }, tapG: 0.2).Count == 0, "jolts under 0.25 g are not taps");
        Func<double, Vec3> push = t =>
        {
            double p = t - (t < 1.6 ? 1.0 : 1.6);
            return new Vec3(0, 0, p >= 0 && p < 0.25 ? 2.0 * Math.Sin(Math.PI * p / 0.25) : 0);
        };
        Check.That(Run(2.5, accel: push).Count == 0, "a push (a smooth 2 g swell over 250 ms) is not a tap");
        Check.That(Run(1.5, tapsAt: new[] { 0.05, 0.25 }).Count == 0, "the first half second is skipped (a junk first report)");

        Func<double, Vec3> turning = t => new Vec3(0, 150.0 * Math.Sin(t * 2 * Math.PI * 0.7), 0);
        Func<double, Vec3> tilt = t => new Vec3(0.2 * Math.Sin(t * 2 * Math.PI * 0.7), 0, 0.1 * Math.Cos(t * 2 * Math.PI * 0.7));
        Check.That(Run(6.0, accel: tilt, gyro: turning).Count == 0, "head turns alone are not taps");
        Check.That(Run(2.5, gyro: _ => new Vec3(0, 120.0, 0), tapsAt: new[] { 1.0, 1.25 }).Count == 0, "a jolt during a fast turn is left out");

        Func<double, Vec3> steps = t =>
        {
            double p = t % 0.5;
            double strike = p < 0.02 ? 0.7 * Math.Sin(Math.PI * p / 0.02) : 0.0;
            return new Vec3(0.05 * Math.Sin(t * 2 * Math.PI), 0.3 * Math.Sin(t * 4 * Math.PI) + strike, 0);
        };
        Check.That(Run(10.0, accel: steps, gyro: t => new Vec3(0, 20.0 * Math.Sin(t * 2 * Math.PI), 0)).Count == 0, "walking is not taps");
        Func<double, Vec3> stomps = t =>
        {
            double p = t % 0.5;
            return new Vec3(0, p < 0.1 ? 1.0 * Math.Sin(Math.PI * p / 0.1) : 0.0, 0);
        };
        Check.That(Run(6.0, accel: stomps).Count == 0, "hard steps over the tap level, 100 ms long, stay out");

        var gapped = new List<int>();
        var tg = new GlassesTaps(gapped.Add);
        for (int i = 0; i < 800; i++) tg.Feed(i / Hz, Vec3.Zero, new Vec3(0, GlassesTaps.G, 0));
        tg.Feed(5.0, Vec3.Zero, new Vec3(0, GlassesTaps.G, 3 * GlassesTaps.G));
        Check.That(!tg.InJolt && gapped.Count == 0, "a gap in the reports starts again (no tap from the jump after it)");

        // His soft taps on the top of the frame (Deck 0.7.6): 0.33 to 0.51 g, about 320 ms apart.
        Check.That(Is(Run(3.0, tapsAt: new[] { 1.0, 1.32 }, tapG: 0.35, tapS: 0.005), 2), "soft taps like his: a run of 2");
        Check.That(Is(Run(3.0, tapsAt: new[] { 1.0, 1.32, 1.64 }, tapG: 0.35, tapS: 0.005), 3), "soft taps like his: a run of 3");
        Check.That(Is(Run(3.0, tapsAt: new[] { 1.0, 1.5 }, tapG: 0.35), 2), "two a second works");
        Check.That(Is(Run(3.0, tapsAt: new[] { 1.0, 1.25 }, tapG: 0.4, ringG: 0.3), 2), "the frame ringing does not eat the next tap");
        Check.That(Is(Run(3.5, tapsAt: new[] { 1.0, 1.43, 1.86 }, tapG: 3.0, ringG: 0.6, ringS: 0.1), 3), "firm taps ringing for 100 ms: three count");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.134 }, tapG: 0.4).Count == 0, "two jolts 134 ms apart (putting the glasses on): not a run");

        var lines = new List<string>();
        Run(3.0, tapsAt: new[] { 1.0, 1.3 }, tapG: 0.35, lines: lines);
        var joltLines = lines.Where(l => l.StartsWith("taps: jolt ")).ToList();
        Check.That(joltLines.Count >= 2 && joltLines.Count(l => l.Contains("a tap,")) == 2, $"every jolt is logged with why ({joltLines.Count} lines)");
        var softLines = new List<string>();
        Run(2.5, tapsAt: new[] { 1.0 }, tapG: 0.2, lines: softLines);
        Check.That(softLines.Any(l => l.Contains("not a tap: too soft")), "a soft jolt's line says it was too soft");

        // Tap sensitivity, 25% to 300%: 100 keeps the values; higher divides the jolt and jump a tap needs.
        var s = new GlassesTaps(_ => { });
        Check.That(s.Sensitivity == 100 && s.TapGNow == GlassesTaps.TapG && s.JerkGNow == GlassesTaps.JerkG && s.LogGNow == GlassesTaps.LogG, "100%: the Deck's values");
        s.SetSensitivity(200);
        Check.That(Math.Abs(s.TapGNow - GlassesTaps.TapG / 2) < 1e-9 && Math.Abs(s.JerkGNow - GlassesTaps.JerkG / 2) < 1e-9 && s.LogGNow <= s.TapGNow, "200%: half the jolt and jump");
        s.SetSensitivity(50);
        Check.That(Math.Abs(s.TapGNow - GlassesTaps.TapG * 2) < 1e-9, "50%: twice the jolt");
        s.SetSensitivity(1000);
        Check.That(s.Sensitivity == 300, "held to 300%");
        s.SetSensitivity(5);
        Check.That(s.Sensitivity == 25, "held to 25%");
        Check.That(Is(Run(3.0, tapsAt: new[] { 1.0, 1.3 }, tapG: 0.2, sensitivity: 200), 2), "at 200% a 0.2 g tap counts");
        Check.That(Run(3.0, tapsAt: new[] { 1.0, 1.3 }, tapG: 0.35, tapS: 0.005, sensitivity: 50).Count == 0, "at 50% his soft taps do not");

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
        Check.That(new SpaceSettings().TapSensitivity == 100, "tap sensitivity is 100% at first");
        var on = new SpaceSettings { Taps = true, TapSensitivity = 40 };
        var back = SpaceSettings.Decode(on.Encode());
        Check.That(back.Taps && back.TapSensitivity == 40, "the switch and the sensitivity are saved");
        Check.That(new SpaceSettings { TapSensitivity = 900 }.Clamped.TapSensitivity == 300, "a sensitivity out of range is held to 25 to 300");
    }
}
