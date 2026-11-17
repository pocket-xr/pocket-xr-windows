using System;

namespace PocketXR.Core;

/// <summary>A rotation, w first. Head axes are x right, y up, z back (a right-handed view space), as in Pocket XR.</summary>
public struct Quat : IEquatable<Quat>
{
    public double W, X, Y, Z;

    public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }

    public static readonly Quat Identity = new(1, 0, 0, 0);

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    public readonly Quat Conjugate => new(W, -X, -Y, -Z);

    public readonly Quat Normalized
    {
        get
        {
            double n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
            return n > 0 ? new Quat(W / n, X / n, Y / n, Z / n) : Identity;
        }
    }

    /// <summary>[v] turned by this rotation.</summary>
    public readonly Vec3 Rotate(Vec3 v)
    {
        Quat p = this * new Quat(0, v.X, v.Y, v.Z) * Conjugate;
        return new Vec3(p.X, p.Y, p.Z);
    }

    /// <summary>A turn of [angle] radians about the up axis (left positive).</summary>
    public static Quat AboutY(double angle) => new(Math.Cos(angle / 2), 0, Math.Sin(angle / 2), 0);

    /// <summary>A turn of [angle] radians about the right axis (up positive).</summary>
    public static Quat AboutX(double angle) => new(Math.Cos(angle / 2), Math.Sin(angle / 2), 0, 0);

    /// <summary>The turn about the world's up axis, radians, left positive.</summary>
    public readonly double Yaw
    {
        get
        {
            Vec3 f = Rotate(new Vec3(0, 0, -1));
            return Math.Atan2(-f.X, -f.Z);
        }
    }

    /// <summary>Column-major 4x4 rotation matrix, for the renderer.</summary>
    public readonly float[] Matrix => new[]
    {
        (float)(1 - 2 * (Y * Y + Z * Z)), (float)(2 * (X * Y + W * Z)), (float)(2 * (X * Z - W * Y)), 0f,
        (float)(2 * (X * Y - W * Z)), (float)(1 - 2 * (X * X + Z * Z)), (float)(2 * (Y * Z + W * X)), 0f,
        (float)(2 * (X * Z + W * Y)), (float)(2 * (Y * Z - W * X)), (float)(1 - 2 * (X * X + Y * Y)), 0f,
        0f, 0f, 0f, 1f,
    };

    public static bool operator ==(Quat a, Quat b) => a.W == b.W && a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Quat a, Quat b) => !(a == b);
    public readonly bool Equals(Quat other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Quat q && this == q;
    public override readonly int GetHashCode() => HashCode.Combine(W, X, Y, Z);
    public override readonly string ToString() => FormattableString.Invariant($"({W}, {X}, {Y}, {Z})");
}
