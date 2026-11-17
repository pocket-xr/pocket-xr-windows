using System;

namespace PocketXR.Core;

// Small vector and matrix types with the same meaning as the ones Pocket XR for Mac uses (Apple's simd), so the
// shared logic reads the same in both. Mat4 is column-major and multiplies column vectors: (a * b) * v applies b first.

/// <summary>Three doubles: a direction or a point. Head axes are x right, y up, z back.</summary>
public struct Vec3 : IEquatable<Vec3>
{
    public double X, Y, Z;

    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public static bool operator ==(Vec3 a, Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Vec3 a, Vec3 b) => !(a == b);

    public double this[int i]
    {
        readonly get => i == 0 ? X : i == 1 ? Y : Z;
        set { if (i == 0) X = value; else if (i == 1) Y = value; else Z = value; }
    }

    public readonly double LengthSquared => X * X + Y * Y + Z * Z;
    public readonly double Length => Math.Sqrt(LengthSquared);
    /// <summary>This vector at length 1 (simd_normalize: a zero vector gives NaN, as on the Mac).</summary>
    public readonly Vec3 Normalized => this / Length;

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
    /// <summary>a + (b - a) * t.</summary>
    public static Vec3 Mix(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    public readonly bool Equals(Vec3 other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vec3 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override readonly string ToString() => FormattableString.Invariant($"({X}, {Y}, {Z})");
}

/// <summary>Two doubles.</summary>
public struct Vec2 : IEquatable<Vec2>
{
    public double X, Y;

    public Vec2(double x, double y) { X = x; Y = y; }

    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);
    public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

    public readonly double LengthSquared => X * X + Y * Y;
    public readonly double Length => Math.Sqrt(LengthSquared);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static Vec2 Mix(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    public readonly bool Equals(Vec2 other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vec2 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => FormattableString.Invariant($"({X}, {Y})");
}

/// <summary>Two floats (texture coordinates).</summary>
public struct Vec2f : IEquatable<Vec2f>
{
    public float X, Y;

    public Vec2f(float x, float y) { X = x; Y = y; }

    public static bool operator ==(Vec2f a, Vec2f b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2f a, Vec2f b) => !(a == b);
    public readonly bool Equals(Vec2f other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vec2f v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
}

/// <summary>Three floats (a mesh position).</summary>
public struct Vec3f : IEquatable<Vec3f>
{
    public float X, Y, Z;

    public Vec3f(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static bool operator ==(Vec3f a, Vec3f b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Vec3f a, Vec3f b) => !(a == b);
    public readonly bool Equals(Vec3f other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vec3f v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
}

/// <summary>Four floats.</summary>
public struct Vec4f : IEquatable<Vec4f>
{
    public float X, Y, Z, W;

    public Vec4f(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static Vec4f operator +(Vec4f a, Vec4f b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static Vec4f operator *(Vec4f a, float s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);
    public static bool operator ==(Vec4f a, Vec4f b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
    public static bool operator !=(Vec4f a, Vec4f b) => !(a == b);
    public readonly bool Equals(Vec4f other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Vec4f v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
}

/// <summary>A 4x4 float matrix held as four columns, like simd_float4x4. Multiplies column vectors.</summary>
public struct Mat4 : IEquatable<Mat4>
{
    public Vec4f C0, C1, C2, C3;

    /// <summary>simd_float4x4(columns: (c0, c1, c2, c3)).</summary>
    public Mat4(Vec4f c0, Vec4f c1, Vec4f c2, Vec4f c3) { C0 = c0; C1 = c1; C2 = c2; C3 = c3; }

    public static readonly Mat4 Identity = Diagonal(new Vec4f(1, 1, 1, 1));

    /// <summary>simd_float4x4(diagonal:).</summary>
    public static Mat4 Diagonal(Vec4f d) =>
        new(new Vec4f(d.X, 0, 0, 0), new Vec4f(0, d.Y, 0, 0), new Vec4f(0, 0, d.Z, 0), new Vec4f(0, 0, 0, d.W));

    public static Vec4f operator *(Mat4 m, Vec4f v) => m.C0 * v.X + m.C1 * v.Y + m.C2 * v.Z + m.C3 * v.W;

    public static Mat4 operator *(Mat4 a, Mat4 b) => new(a * b.C0, a * b.C1, a * b.C2, a * b.C3);

    /// <summary>The 16 numbers, column after column: what a shader's column-major float4x4 takes.</summary>
    public readonly float[] ToArray() => new[]
    {
        C0.X, C0.Y, C0.Z, C0.W, C1.X, C1.Y, C1.Z, C1.W, C2.X, C2.Y, C2.Z, C2.W, C3.X, C3.Y, C3.Z, C3.W,
    };

    public static bool operator ==(Mat4 a, Mat4 b) => a.C0 == b.C0 && a.C1 == b.C1 && a.C2 == b.C2 && a.C3 == b.C3;
    public static bool operator !=(Mat4 a, Mat4 b) => !(a == b);
    public readonly bool Equals(Mat4 other) => this == other;
    public override readonly bool Equals(object? obj) => obj is Mat4 m && this == m;
    public override readonly int GetHashCode() => HashCode.Combine(C0, C1, C2, C3);
}
