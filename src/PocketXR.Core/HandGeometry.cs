using System;
using System.Collections.Generic;

namespace PocketXR.Core;

// Hand tracking's geometry, with no camera in it: the hand's points as the picture has them, how closed a pinch is,
// which way the palm faces, a steady point to aim through, and the camera as a pinhole. The behaviour and the
// numbers are the Android app's (core/hands: PinchDetector, PalmFacing, SteadyAnchor, HandRay), which were tuned on
// the XREAL Aura's camera with a Galaxy Z Flip 7. Nothing here has been tried with real hands, on a Mac or on a PC.

/// <summary>A point in the camera's picture: x to the right, y DOWN, both 0 to 1 across the frame.</summary>
public struct HandPoint : IEquatable<HandPoint>
{
    public double X;
    public double Y;

    public HandPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// From a point as Apple's Vision gives it: 0 to 1 with the origin at the LOWER left. This is the one place the
    /// picture is turned the right way up. [mirrored] also swaps left and right, for a camera that faces the person
    /// (the computer's own, used only for testing), so that a hand moved to the right moves the pointer to the right.
    /// </summary>
    public static HandPoint FromLowerLeft(double x, double y, bool mirrored = false) =>
        new(mirrored ? 1 - x : x, 1 - y);

    public static bool operator ==(HandPoint a, HandPoint b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(HandPoint a, HandPoint b) => !(a == b);
    public readonly bool Equals(HandPoint other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandPoint p && this == p;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => FormattableString.Invariant($"({X}, {Y})");
}

public enum HandSide
{
    Left,
    Right,
}

public static class HandSideExt
{
    public static string RawValue(this HandSide side) => side == HandSide.Left ? "left" : "right";

    public static HandSide? FromRawValue(string? raw) => raw switch
    {
        "left" => HandSide.Left,
        "right" => HandSide.Right,
        _ => null,
    };

    public static HandSide Other(this HandSide side) => side == HandSide.Left ? HandSide.Right : HandSide.Left;
}

/// <summary>
/// A direction as two angles in degrees: yaw to the left positive, pitch up positive (the app's own convention).
/// </summary>
public struct HandAngles : IEquatable<HandAngles>
{
    public double YawDeg;
    public double PitchDeg;

    public static readonly HandAngles Zero = new(0, 0);

    public HandAngles(double yawDeg, double pitchDeg)
    {
        YawDeg = yawDeg;
        PitchDeg = pitchDeg;
    }

    public static HandAngles operator +(HandAngles a, HandAngles b) =>
        new(a.YawDeg + b.YawDeg, a.PitchDeg + b.PitchDeg);

    public static HandAngles operator -(HandAngles a, HandAngles b) =>
        new(a.YawDeg - b.YawDeg, a.PitchDeg - b.PitchDeg);

    public readonly HandAngles Scaled(double k) => new(YawDeg * k, PitchDeg * k);

    /// <summary>
    /// How far apart two directions are, degrees (flat, which is right to a few percent for the small angles used).
    /// </summary>
    public readonly double Distance(HandAngles to) => HandMath.Hypot(YawDeg - to.YawDeg, PitchDeg - to.PitchDeg);

    public static bool operator ==(HandAngles a, HandAngles b) => a.YawDeg == b.YawDeg && a.PitchDeg == b.PitchDeg;
    public static bool operator !=(HandAngles a, HandAngles b) => !(a == b);
    public readonly bool Equals(HandAngles other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandAngles a && this == a;
    public override readonly int GetHashCode() => HashCode.Combine(YawDeg, PitchDeg);
    public override readonly string ToString() => FormattableString.Invariant($"(yaw {YawDeg}, pitch {PitchDeg})");
}

/// <summary>
/// The seven of a hand's 21 points that hand tracking uses, in the picture, with which hand it is when known.
/// </summary>
public struct HandPoints : IEquatable<HandPoints>
{
    public HandPoint Wrist;
    public HandPoint ThumbTip;
    public HandPoint IndexTip;
    public HandPoint IndexKnuckle;
    public HandPoint MiddleKnuckle;
    public HandPoint RingKnuckle;
    public HandPoint LittleKnuckle;
    /// <summary>The person's own left or right hand, null when the detector could not say.</summary>
    public HandSide? Side;
    /// <summary>The picture's width over its height (640 x 480 is 1.333), so that distances are true in both directions.</summary>
    public double Aspect;

    public HandPoints(HandPoint wrist, HandPoint thumbTip, HandPoint indexTip, HandPoint indexKnuckle,
                      HandPoint middleKnuckle, HandPoint ringKnuckle, HandPoint littleKnuckle, HandSide? side,
                      double aspect)
    {
        Wrist = wrist;
        ThumbTip = thumbTip;
        IndexTip = indexTip;
        IndexKnuckle = indexKnuckle;
        MiddleKnuckle = middleKnuckle;
        RingKnuckle = ringKnuckle;
        LittleKnuckle = littleKnuckle;
        Side = side;
        Aspect = aspect > 0 ? aspect : 1;
    }

    /// <summary>
    /// From Vision's points (origin at the lower left), in this order: wrist, thumb tip, index tip, then the
    /// knuckles of the index, middle, ring and little fingers. Null unless there are exactly seven.
    /// A mirrored picture shows a right hand as a left one, so [mirrored] swaps the side too: the palm test
    /// (HandPalm) then still answers for the camera that took the picture.
    /// </summary>
    public static HandPoints? FromLowerLeft(IReadOnlyList<(double X, double Y)> points, HandSide? side, double aspect,
                                            bool mirrored = false)
    {
        if (points.Count != 7) return null;
        var p = new HandPoint[7];
        for (int i = 0; i < 7; i++) p[i] = HandPoint.FromLowerLeft(points[i].X, points[i].Y, mirrored);
        return new HandPoints(p[0], p[1], p[2], p[3], p[4], p[5], p[6], mirrored ? side?.Other() : side, aspect);
    }

    /// <summary>The distance between two points in units of the picture's height.</summary>
    public readonly double Distance(HandPoint a, HandPoint b) => HandMath.Hypot((a.X - b.X) * Aspect, a.Y - b.Y);

    /// <summary>Wrist to middle knuckle: a size that does not change when the fingers bend (Android: HandFrame.handSize).</summary>
    public readonly double Size => Math.Max(Distance(Wrist, MiddleKnuckle), 1e-4);

    /// <summary>
    /// Thumb tip to index tip over the hand's size, so a pinch reads the same near and far (Android:
    /// HandFrame.pinchRatio, the picture's measure). About 0.6 open, under 0.25 touching.
    /// </summary>
    public readonly double PinchRatio => Distance(ThumbTip, IndexTip) / Size;

    /// <summary>The middle of the four knuckles: where the hand is, for telling two hands apart from frame to frame.</summary>
    public readonly HandPoint Centre => new(
        (IndexKnuckle.X + MiddleKnuckle.X + RingKnuckle.X + LittleKnuckle.X) / 4,
        (IndexKnuckle.Y + MiddleKnuckle.Y + RingKnuckle.Y + LittleKnuckle.Y) / 4);

    public static bool operator ==(HandPoints a, HandPoints b) =>
        a.Wrist == b.Wrist && a.ThumbTip == b.ThumbTip && a.IndexTip == b.IndexTip &&
        a.IndexKnuckle == b.IndexKnuckle && a.MiddleKnuckle == b.MiddleKnuckle && a.RingKnuckle == b.RingKnuckle &&
        a.LittleKnuckle == b.LittleKnuckle && a.Side == b.Side && a.Aspect == b.Aspect;
    public static bool operator !=(HandPoints a, HandPoints b) => !(a == b);
    public readonly bool Equals(HandPoints other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandPoints h && this == h;
    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Wrist);
        hash.Add(ThumbTip);
        hash.Add(IndexTip);
        hash.Add(IndexKnuckle);
        hash.Add(MiddleKnuckle);
        hash.Add(RingKnuckle);
        hash.Add(LittleKnuckle);
        hash.Add(Side);
        hash.Add(Aspect);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Whether a hand shows its palm to the camera (Android: PalmFacing, 0.4.1). A right and a left hand are mirror
/// images, so which way the palm faces follows from which hand it is and the turn from the index knuckle to the
/// little finger's knuckle around the wrist.
/// </summary>
public static class HandPalm
{
    /// <summary>
    /// The sine of the angle between wrist to index knuckle and wrist to little knuckle below which the hand is
    /// seen edge on and counts neither way (Android: MIN_OPEN 0.2).
    /// </summary>
    public const double MinOpen = 0.2;

    /// <summary>
    /// The signed turn from the index knuckle to the little finger's knuckle around the wrist, -1 to 1, with y down:
    /// positive is clockwise on screen.
    /// </summary>
    public static double Turn(HandPoints hand)
    {
        double ax = (hand.IndexKnuckle.X - hand.Wrist.X) * hand.Aspect;
        double ay = hand.IndexKnuckle.Y - hand.Wrist.Y;
        double bx = (hand.LittleKnuckle.X - hand.Wrist.X) * hand.Aspect;
        double by = hand.LittleKnuckle.Y - hand.Wrist.Y;
        double length = HandMath.Hypot(ax, ay) * HandMath.Hypot(bx, by);
        if (length < 1e-12) return 0;
        return (ax * by - ay * bx) / length;
    }

    /// <summary>
    /// True when the palm faces the camera. Hold your right hand up with its palm to your face and the fingers up:
    /// the thumb and the index knuckle are on the right of the picture, the little finger on the left, which with
    /// y down is a turn against the clock (negative). A left hand is the mirror of that. (Android's rule reads the
    /// other way round only because MediaPipe names hands as a mirror would.) A hand of unknown side is never
    /// taken as showing its palm, so an unsure detector cannot open the launcher.
    /// </summary>
    public static bool FacesCamera(HandPoints hand, double minOpen = MinOpen)
    {
        if (hand.Side is not HandSide side) return false;
        double t = Turn(hand);
        return side == HandSide.Right ? t < -minOpen : t > minOpen;
    }
}

/// <summary>
/// A steady point on the hand to aim through (Android: SteadyAnchor, 0.5.9). The fingertips move when a pinch
/// closes, and a pointer aimed through them would move with every click. This point is the mean of the four
/// knuckles, which do not move in a pinch (and whose mean has half one point's noise), pushed out to where a pinch
/// happens by 0.45 of the wrist to knuckles vector and 0.88 of the little to index knuckle vector. Both vectors
/// only change when the hand turns, which is slow, so they are smoothed over 250 ms.
/// </summary>
public sealed class HandAnchor
{
    /// <summary>Android: SteadyAnchor.FORWARD and ACROSS (Meta's stable pinch point, measured from the knuckles' mean).</summary>
    public const double Forward = 0.45;
    public const double Across = 0.88;

    public double OffsetMs { get; }
    private double fx = 0.0, fy = 0.0, ax = 0.0, ay = 0.0;
    private double lastMs = 0.0;
    private bool primed = false;
    private int count = 0;

    public HandAnchor(double offsetMs = 250)
    {
        OffsetMs = offsetMs;
    }

    /// <summary>The point for [hand] seen at [timeMs], in the picture.</summary>
    public HandPoint Point(HandPoints hand, double timeMs)
    {
        double a = hand.Aspect;
        // Worked in units of the picture's height, so the vectors are true in both directions.
        double kx = hand.Centre.X * a;
        double ky = hand.Centre.Y;
        double nfx = kx - hand.Wrist.X * a;
        double nfy = ky - hand.Wrist.Y;
        double nax = (hand.IndexKnuckle.X - hand.LittleKnuckle.X) * a;
        double nay = hand.IndexKnuckle.Y - hand.LittleKnuckle.Y;
        if (!primed || timeMs < lastMs)
        {
            fx = nfx; fy = nfy; ax = nax; ay = nay;
            primed = true;
            count = 1;
        }
        else if (timeMs > lastMs)
        {
            // A plain mean over the first frames, so the offsets start from several frames and not from one.
            count += 1;
            double k = Math.Max(OffsetMs <= 0 ? 1 : 1 - Math.Exp(-(timeMs - lastMs) / OffsetMs), 1 / (double)count);
            fx += k * (nfx - fx); fy += k * (nfy - fy);
            ax += k * (nax - ax); ay += k * (nay - ay);
        }
        lastMs = timeMs;
        double x = kx + fx * Forward + ax * Across;
        double y = ky + fy * Forward + ay * Across;
        return new HandPoint(x / a, y);
    }

    public void Reset()
    {
        primed = false;
        count = 0;
    }
}

/// <summary>
/// The camera as a pinhole (Android: CameraModel): a point in the picture becomes a direction from the camera,
/// which sits by the eyes, so the pointer goes where the hand is seen.
/// </summary>
public struct HandCameraModel : IEquatable<HandCameraModel>
{
    /// <summary>
    /// The Aura camera's width of view, degrees: its quoted 80 degrees is the diagonal of a 4:3 sensor, which is
    /// 68 across (Android: CameraModel.AURA_HFOV_DEG, 0.5.10).
    /// </summary>
    public const double AuraFovDeg = 68.0;
    /// <summary>For a camera that does not say and is not known: a common webcam.</summary>
    public const double UnknownFovDeg = 70.0;

    /// <summary>The width of view, degrees.</summary>
    public double FovDeg;
    /// <summary>The picture's width over its height.</summary>
    public double Aspect;

    public HandCameraModel(double fovDeg, double aspect)
    {
        FovDeg = Math.Min(Math.Max(fovDeg, 10), 170);
        Aspect = aspect > 0 ? aspect : 1;
    }

    /// <summary>The height of view, degrees (square pixels).</summary>
    public readonly double VerticalFovDeg => 2 * Math.Atan(Math.Tan(FovDeg * Math.PI / 360) / Aspect) * 180 / Math.PI;

    /// <summary>
    /// The direction the camera sees [p] in: yaw left positive, pitch up positive, 0,0 at the picture's middle.
    /// As the app turns a ray (yaw about the up axis, then pitch), so the two go together exactly.
    /// </summary>
    public readonly HandAngles Ray(HandPoint p)
    {
        double halfX = Math.Tan(FovDeg * Math.PI / 360);
        double halfY = halfX / Aspect;
        double tx = (p.X - 0.5) * 2 * halfX; // to the right on a plane 1 in front
        double ty = (0.5 - p.Y) * 2 * halfY; // up
        double yaw = Math.Atan(-tx);
        double pitch = Math.Atan2(ty, Math.Sqrt(1 + tx * tx));
        return new HandAngles(yaw * 180 / Math.PI, pitch * 180 / Math.PI);
    }

    public static bool operator ==(HandCameraModel a, HandCameraModel b) => a.FovDeg == b.FovDeg && a.Aspect == b.Aspect;
    public static bool operator !=(HandCameraModel a, HandCameraModel b) => !(a == b);
    public readonly bool Equals(HandCameraModel other) => this == other;
    public override readonly bool Equals(object? obj) => obj is HandCameraModel m && this == m;
    public override readonly int GetHashCode() => HashCode.Combine(FovDeg, Aspect);
}

/// <summary>What the hand files need that Simd.cs does not have.</summary>
internal static class HandMath
{
    /// <summary>Swift's hypot: the length of (x, y).</summary>
    public static double Hypot(double x, double y) => double.Hypot(x, y);
}
