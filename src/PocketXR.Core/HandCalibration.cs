using System;
using System.Collections.Generic;

namespace PocketXR.Core;

// The hand aim's calibration: "point at the dot and pinch" on five dots, then a least squares fit of a correction
// from the angles the camera's ray had to the angles that land on each dot (Android: RayCalibration and
// HandCalibration in HandRay.kt, 0.5.0). It takes up what the pinhole guess gets wrong: where the camera really
// points, how it is rolled and how wide it sees.

/// <summary>A correction of ray angles, degrees: yaw' = a yaw + b pitch + c, pitch' = d yaw + e pitch + f.</summary>
public struct HandCalibration : IEquatable<HandCalibration>
{
    public double A, B, C, D, E, F;

    public static readonly HandCalibration Identity = new(1, 0, 0, 0, 1, 0);

    /// <summary>
    /// Gains outside this range, or cross terms above [maxCross], are a bad fit and not a camera (Android:
    /// MIN_GAIN, MAX_GAIN, MAX_CROSS).
    /// </summary>
    public const double MinGain = 0.6;
    public const double MaxGain = 1.6;
    public const double MaxCross = 0.4;
    /// <summary>A fit further off than this over its own dots is not kept (Android: HandCalibration.maxRmsDeg).</summary>
    public const double MaxRmsDeg = 2.5;

    public HandCalibration(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public readonly bool IsIdentity => this == Identity;

    public readonly HandAngles Apply(HandAngles ray) => new(
        A * ray.YawDeg + B * ray.PitchDeg + C,
        Math.Min(Math.Max(D * ray.YawDeg + E * ray.PitchDeg + F, -89), 89));

    /// <summary>The six numbers, as kept in the settings.</summary>
    public readonly double[] Numbers => new[] { A, B, C, D, E, F };

    /// <summary>From six finite numbers, else null (Swift: init?(numbers:)).</summary>
    public static HandCalibration? FromNumbers(IReadOnlyList<double> numbers)
    {
        IReadOnlyList<double> n = numbers;
        if (n.Count != 6) return null;
        for (int i = 0; i < 6; i++)
        {
            if (!double.IsFinite(n[i])) return null;
        }
        return new HandCalibration(n[0], n[1], n[2], n[3], n[4], n[5]);
    }

    /// <summary>
    /// The least squares correction from the [measured] ray angles to the [wanted] ones. With three or more pairs
    /// a full fit, kept only when its gains and cross terms are sensible; otherwise, or with one or two pairs,
    /// the mean offset alone. With none, no correction.
    /// </summary>
    public static HandCalibration Fit(IReadOnlyList<HandAngles> measured, IReadOnlyList<HandAngles> wanted)
    {
        int n = Math.Min(measured.Count, wanted.Count);
        if (!(n > 0)) return Identity;
        HandCalibration offset = Identity;
        double sumYaw = 0.0;
        double sumPitch = 0.0;
        for (int i = 0; i < n; i++)
        {
            sumYaw += wanted[i].YawDeg - measured[i].YawDeg;
            sumPitch += wanted[i].PitchDeg - measured[i].PitchDeg;
        }
        offset.C = sumYaw / n;
        offset.F = sumPitch / n;
        if (!(n >= 3)) return offset;
        var rows = new double[n][];
        var wantedYaw = new double[n];
        var wantedPitch = new double[n];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new[] { measured[i].YawDeg, measured[i].PitchDeg, 1 };
            wantedYaw[i] = wanted[i].YawDeg;
            wantedPitch[i] = wanted[i].PitchDeg;
        }
        double[]? yaw = Solve(rows, wantedYaw);
        if (yaw == null) return offset;
        double[]? pitch = Solve(rows, wantedPitch);
        if (pitch == null) return offset;
        var full = new HandCalibration(yaw[0], yaw[1], yaw[2], pitch[0], pitch[1], pitch[2]);
        bool sane = full.A >= MinGain && full.A <= MaxGain && full.E >= MinGain && full.E <= MaxGain &&
                    Math.Abs(full.B) <= MaxCross && Math.Abs(full.D) <= MaxCross;
        return sane ? full : offset;
    }

    /// <summary>The root mean square angle, degrees, between this correction applied to [measured] and [wanted].</summary>
    public readonly double RmsDeg(IReadOnlyList<HandAngles> measured, IReadOnlyList<HandAngles> wanted)
    {
        int n = Math.Min(measured.Count, wanted.Count);
        if (!(n > 0)) return 0;
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = Apply(measured[i]).Distance(wanted[i]);
            sum += d * d;
        }
        return Math.Sqrt(sum / n);
    }

    /// <summary>
    /// Least squares x for rows * x = y with three unknowns (the normal equations, solved by elimination with
    /// pivoting), or null when the rows do not pin all three down (dots in a line).
    /// </summary>
    internal static double[]? Solve(double[][] rows, double[] y)
    {
        var m = new double[3][];
        for (int i = 0; i < 3; i++) m[i] = new double[4];
        for (int k = 0; k < rows.Length; k++)
        {
            double[] r = rows[k];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) m[i][j] += r[i] * r[j];
                m[i][3] += r[i] * y[k];
            }
        }
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col; row < 3; row++)
            {
                if (Math.Abs(m[row][col]) > Math.Abs(m[pivot][col])) pivot = row;
            }
            if (Math.Abs(m[pivot][col]) < 1e-9) return null;
            (m[col], m[pivot]) = (m[pivot], m[col]);
            for (int row = 0; row < 3; row++)
            {
                if (row == col) continue;
                double k = m[row][col] / m[col][col];
                for (int j = col; j < 4; j++) m[row][j] -= k * m[col][j];
            }
        }
        return new[] { m[0][3] / m[0][0], m[1][3] / m[1][1], m[2][3] / m[2][2] };
    }

    public static bool operator ==(HandCalibration a, HandCalibration b) =>
        a.A == b.A && a.B == b.B && a.C == b.C && a.D == b.D && a.E == b.E && a.F == b.F;
    public static bool operator !=(HandCalibration a, HandCalibration b) => !(a == b);
    public readonly bool Equals(HandCalibration other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandCalibration c && this == c;
    public override readonly int GetHashCode() => HashCode.Combine(A, B, C, D, E, F);
}

/// <summary>What a run of the calibration came to (Swift: HandCalibrationRun.Result).</summary>
public readonly struct HandCalibrationRunResult : IEquatable<HandCalibrationRunResult>
{
    public HandCalibration Calibration { get; }
    public double RmsDeg { get; }
    /// <summary>Good enough to keep.</summary>
    public bool Ok { get; }

    public HandCalibrationRunResult(HandCalibration calibration, double rmsDeg, bool ok)
    {
        Calibration = calibration;
        RmsDeg = rmsDeg;
        Ok = ok;
    }

    public static bool operator ==(HandCalibrationRunResult a, HandCalibrationRunResult b) =>
        a.Calibration == b.Calibration && a.RmsDeg == b.RmsDeg && a.Ok == b.Ok;
    public static bool operator !=(HandCalibrationRunResult a, HandCalibrationRunResult b) => !(a == b);
    public bool Equals(HandCalibrationRunResult other) => this == other;
    public override bool Equals(object? obj) => obj is HandCalibrationRunResult r && this == r;
    public override int GetHashCode() => HashCode.Combine(Calibration, RmsDeg, Ok);
}

/// <summary>
/// One run of the calibration: the dots in order, the pairs gathered so far, and the result after the last.
/// A struct in Swift; a class here, so a run handed on is the same run and not a copy.
/// </summary>
public sealed class HandCalibrationRun
{
    /// <summary>
    /// Where the dots are on the screen, as shares of its width and height from the top left: the middle, then the
    /// four corners 20 % in from the edges.
    /// </summary>
    public static readonly (double U, double V)[] Dots = { (0.5, 0.5), (0.2, 0.2), (0.8, 0.2), (0.8, 0.8), (0.2, 0.8) };

    private readonly List<HandAngles> measured = new();
    private readonly List<HandAngles> wanted = new();

    public IReadOnlyList<HandAngles> Measured => measured;
    public IReadOnlyList<HandAngles> Wanted => wanted;

    public HandCalibrationRun() { }

    /// <summary>The dot being asked for now (0 is the first), equal to `Dots.Length` when all are done.</summary>
    public int Step => measured.Count;
    public bool Done => measured.Count >= Dots.Length;
    public (double U, double V)? Dot => Done ? null : Dots[measured.Count];

    /// <summary>
    /// The angles, in the head's own frame, that land on the point [u], [v] of a screen placed at [panel] in the
    /// Space, with the head turned [headYawDeg], [headPitchDeg] from the Space's forward. Angles are added and
    /// taken away as numbers, which is right to a fraction of a degree while the head is near level.
    /// </summary>
    public static HandAngles Target(PanelPlacement panel, double u, double v, double headYawDeg, double headPitchDeg) =>
        new(panel.YawDeg + (0.5 - u) * panel.WidthDeg - headYawDeg,
            panel.PitchDeg + (0.5 - v) * panel.HeightDeg - headPitchDeg);

    /// <summary>A pinch on the current dot: [ray] is the hand's ray then (uncorrected), [target] where the dot is.</summary>
    public void Record(HandAngles ray, HandAngles target)
    {
        if (Done) return;
        measured.Add(ray);
        wanted.Add(target);
    }

    public HandCalibrationRunResult Result()
    {
        HandCalibration fit = HandCalibration.Fit(measured, wanted);
        double rms = fit.RmsDeg(measured, wanted);
        return new HandCalibrationRunResult(fit, rms, measured.Count != 0 && rms <= HandCalibration.MaxRmsDeg);
    }
}
