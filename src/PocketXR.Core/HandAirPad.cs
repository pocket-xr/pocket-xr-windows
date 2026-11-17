using System;

namespace PocketXR.Core;

// The air trackpad: hand tracking with a camera that FACES the person (the computer's own), where a ray from the
// glasses makes no sense. The hand's place in the camera's picture places the pointer on the screen: a comfortable
// box in the middle of the picture stands for the whole screen. The picture is mirrored before it gets here
// (HandPoint.FromLowerLeft with mirrored), so a hand moved to the person's right is further right in it.
// Not tried with real hands yet.

/// <summary>The part of the (mirrored) picture that stands for the whole screen.</summary>
public readonly struct HandAirBox : IEquatable<HandAirBox>
{
    /// <summary>
    /// The box is the middle 60 % of the picture's width and 55 % of its height: the hand reaches every corner of
    /// the screen without leaving the picture, and a small move is not a big jump.
    /// </summary>
    public const double WidthShare = 0.60;
    public const double HeightShare = 0.55;
    /// <summary>Its middle sits a little below the picture's, so the hand can rest lower than the face.</summary>
    public static readonly HandPoint RestCentre = new(0.5, 0.56);

    /// <summary>
    /// The gestures and the smoothing work in degrees (they were tuned on a hand ray). The box is given a
    /// screen's usual size in the glasses, 40 by 22.5 degrees, so that the same numbers mean about the same thing:
    /// a drag starts after the pointer would move 2 degrees, a twentieth of the screen's width.
    /// </summary>
    public const double WidthDeg = 40.0;
    public const double HeightDeg = 22.5;

    /// <summary>The box's middle, in the picture.</summary>
    public HandPoint Centre { get; }

    /// <summary>The box at rest (Swift: the default of [centre], HandAirBox.restCentre).</summary>
    public HandAirBox() : this(RestCentre) { }

    /// <summary>[centre] is brought in so that the whole box stays inside the picture.</summary>
    public HandAirBox(HandPoint centre)
    {
        double halfW = WidthShare / 2, halfH = HeightShare / 2;
        Centre = new HandPoint(
            Math.Min(Math.Max(centre.X, halfW), 1 - halfW),
            Math.Min(Math.Max(centre.Y, halfH), 1 - halfH));
    }

    public double MinX => Centre.X - WidthShare / 2;
    public double MinY => Centre.Y - HeightShare / 2;

    /// <summary>
    /// Where on the screen a point of the picture stands for: u from the left, v from the top, 0 to 1 inside the
    /// box and beyond that outside it (not yet clamped, so that smoothing does not stick at the edges).
    /// </summary>
    public (double U, double V) Place(HandPoint p) => ((p.X - MinX) / WidthShare, (p.Y - MinY) / HeightShare);

    /// <summary>The point of the picture that stands for [u], [v] of the screen.</summary>
    public HandPoint Point(double u, double v) => new(MinX + u * WidthShare, MinY + v * HeightShare);

    /// <summary>A place on the screen as the angles the gestures work in: the middle is 0,0, left and up positive.</summary>
    public static HandAngles Angles(double u, double v) => new((0.5 - u) * WidthDeg, (0.5 - v) * HeightDeg);

    /// <summary>Back from those angles to a place on the screen, kept on it: u and v 0 to 1.</summary>
    public static (double U, double V) Shares(HandAngles a) =>
        (Math.Min(Math.Max(0.5 - a.YawDeg / WidthDeg, 0), 1), Math.Min(Math.Max(0.5 - a.PitchDeg / HeightDeg, 0), 1));

    /// <summary>The same without keeping it on the screen (for finding where in the picture a hand was).</summary>
    public static (double U, double V) OpenShares(HandAngles a) => (0.5 - a.YawDeg / WidthDeg, 0.5 - a.PitchDeg / HeightDeg);

    public static bool operator ==(HandAirBox a, HandAirBox b) => a.Centre == b.Centre;
    public static bool operator !=(HandAirBox a, HandAirBox b) => !(a == b);
    public bool Equals(HandAirBox other) => this == other;
    public override bool Equals(object? obj) => obj is HandAirBox b && this == b;
    public override int GetHashCode() => Centre.GetHashCode();
}

/// <summary>Which HandCameraChoice it is.</summary>
public enum HandCameraChoiceKind
{
    Glasses,
    Mac,
    Device,
}

/// <summary>
/// Which camera hand tracking uses, as kept: the glasses' own, the computer's built-in one (kept as "mac", as
/// Pocket XR for Mac keeps it), or one device by its id.
/// </summary>
public readonly struct HandCameraChoice : IEquatable<HandCameraChoice>
{
    public HandCameraChoiceKind Kind { get; }
    /// <summary>The device's id, for Device; null for the others.</summary>
    public string? Id { get; }

    private HandCameraChoice(HandCameraChoiceKind kind, string? id)
    {
        Kind = kind;
        Id = id;
    }

    public static readonly HandCameraChoice Glasses = new(HandCameraChoiceKind.Glasses, null);
    public static readonly HandCameraChoice Mac = new(HandCameraChoiceKind.Mac, null);
    public static HandCameraChoice Device(string id) => new(HandCameraChoiceKind.Device, id);

    public HandCameraChoice(string? stored)
    {
        switch (stored)
        {
            case null:
            case "":
            case "glasses":
                Kind = HandCameraChoiceKind.Glasses;
                Id = null;
                break;
            case "mac":
                Kind = HandCameraChoiceKind.Mac;
                Id = null;
                break;
            default:
                Kind = HandCameraChoiceKind.Device;
                Id = stored;
                break;
        }
    }

    public string Stored => Kind switch
    {
        HandCameraChoiceKind.Glasses => "glasses",
        HandCameraChoiceKind.Mac => "mac",
        _ => Id ?? "",
    };

    public static bool operator ==(HandCameraChoice a, HandCameraChoice b) => a.Kind == b.Kind && a.Id == b.Id;
    public static bool operator !=(HandCameraChoice a, HandCameraChoice b) => !(a == b);
    public bool Equals(HandCameraChoice other) => this == other;
    public override bool Equals(object? obj) => obj is HandCameraChoice c && this == c;
    public override int GetHashCode() => HashCode.Combine(Kind, Id);
}
