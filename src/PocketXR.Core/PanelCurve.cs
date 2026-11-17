using System;

namespace PocketXR.Core;

/// <summary>
/// One corner of a screen's drawing mesh: where it is in head-centred space (x right, y up, z back, metres) and
/// which point of the picture it shows (uv, (0, 0) the picture's top left).
/// </summary>
public record struct PanelVertex
{
    public Vec3f Position;
    public Vec2f Uv;

    public PanelVertex(Vec3f position, Vec2f uv)
    {
        Position = position;
        Uv = uv;
    }
}

/// <summary>
/// A curved screen, as Pocket XR's PanelCurve has it ("like an ultrawide curved monitor").
///
/// A screen's curve runs from 0 (flat) to 2.5. It bends the screen round a vertical cylinder (the axis along the
/// screen's own up) of radius distance / curve, whose axis is on the user's side of the screen. At 1 the axis passes
/// through the head, so every column is the same distance from the eyes. Past 1 the axis is in front of the head and
/// the edges come clearly forward. The angular width stays the screen's widthDeg at any curve: the edges are bent
/// forward until each is seen half that angle from the centre, so a curve never makes a screen look wider or
/// narrower, only rounder.
/// </summary>
public static class PanelCurve
{
    /// <summary>At or under this a curve is drawn and hit tested flat.</summary>
    public const double FlatEps = 0.005;
    /// <summary>The strongest curve.</summary>
    public const double MaxCurve = 2.5;
    /// <summary>The furthest an edge bends round its axis, degrees (a wide screen is held to it), so no edge turns past side on.</summary>
    public const double MaxHalfArcDeg = 85.0;
    /// <summary>
    /// 64 columns: the widest screen (100 degrees) at full curve is 1.6 degrees of arc a column, whose chord sits a
    /// small fraction of a glasses pixel off the arc.
    /// </summary>
    public const int DefaultColumns = 64;

    /// <summary>Half the screen's angular width, radians (held to 1...179 degrees whole).</summary>
    private static double HalfWidth(double widthDeg)
    {
        double w = HeadMath.Min(HeadMath.Max(widthDeg, 1), 179);
        return w * Math.PI / 360;
    }

    /// <summary>
    /// The curve a screen is drawn with: [curve] inside 0...2.5, held so its half arc stays under 85 degrees
    /// (phi = a + asin(sin(a)(c - 1)) reaches 85 degrees at c = 1 + sin(85 degrees - a) / sin(a)).
    /// </summary>
    public static double Effective(double curve, double widthDeg)
    {
        double c = HeadMath.Min(HeadMath.Max(curve, 0), MaxCurve);
        if (c <= 1) return c;
        double a = HalfWidth(widthDeg);
        double limit = MaxHalfArcDeg * Math.PI / 180;
        if (a >= limit) return 1;
        return HeadMath.Min(c, 1 + Math.Sin(limit - a) / Math.Sin(a));
    }

    public static bool IsCurved(PanelPlacement p) => p.Curve > FlatEps;

    /// <summary>The cylinder's radius, metres (infinite when flat).</summary>
    public static double Radius(PanelPlacement p)
    {
        if (!IsCurved(p)) return double.PositiveInfinity;
        return p.Distance / Effective(curve: p.Curve, widthDeg: p.WidthDeg);
    }

    /// <summary>
    /// Half the screen's arc around the axis, radians (0 when flat). With half the screen's angular width a, an edge
    /// at arc angle phi is at R sin(phi) across and R (1 - cos(phi)) nearer, and is seen at a from the head when
    /// phi = a - asin(sin(a)(1 - c)). At c = 1 phi = a.
    /// </summary>
    public static double HalfArc(PanelPlacement p)
    {
        if (!IsCurved(p)) return 0;
        double a = HalfWidth(p.WidthDeg);
        double c = Effective(curve: p.Curve, widthDeg: p.WidthDeg);
        double s = HeadMath.Min(HeadMath.Max(Math.Sin(a) * (1 - c), -1), 1);
        return a - Math.Asin(s);
    }

    /// <summary>
    /// The surface point at [x] (-0.5 at the left edge to 0.5 at the right, along the arc) and [y] (-0.5 bottom to
    /// 0.5 top, straight up the cylinder), before the screen is turned into place: the screen's centre is at
    /// (0, 0, -distance), its right is +x, its up +y, and the head is at the origin.
    /// </summary>
    public static Vec3 LocalPoint(PanelPlacement p, double x, double y)
    {
        Vec2 size = p.SizeMetres;
        if (!IsCurved(p))
        {
            return new Vec3(x * size.X, y * size.Y, -p.Distance);
        }
        double r = Radius(p);
        double th = x * 2 * HalfArc(p);
        // The axis is at (0, -distance + r) in x and z; the surface is r from it, on the far side.
        return new Vec3(r * Math.Sin(th), y * size.Y, -p.Distance + r - r * Math.Cos(th));
    }

    /// <summary>The same point with the screen turned into place by its yaw and pitch, in head-centred space.</summary>
    public static Vec3 Point(PanelPlacement p, double x, double y) => p.Turn.Rotate(LocalPoint(p, x: x, y: y));

    /// <summary>
    /// Where a ray from the head meets the screen's surface (a cylinder when curved, else its plane): the distance
    /// along the ray and the fractions there, x -0.5 left to 0.5 right and y -0.5 bottom to 0.5 top (past those the
    /// ray passes beside the screen). [direction] is in head-centred space and need not be a unit vector. null when
    /// the ray never meets the surface in front of the head.
    /// </summary>
    public static (double T, double X, double Y)? Intersect(PanelPlacement p, Vec3 direction)
    {
        // The ray in the screen's own frame.
        Vec3 d = p.Turn.Conjugate.Rotate(direction);
        Vec2 size = p.SizeMetres;
        if (size.X <= 0 || size.Y <= 0) return null;
        if (!IsCurved(p))
        {
            if (d.Z > -1e-9) return null;
            double flatT = -p.Distance / d.Z;
            return (T: flatT, X: d.X * flatT / size.X, Y: d.Y * flatT / size.Y);
        }
        double r = Radius(p);
        double half = HalfArc(p);
        if (half <= 0) return null;
        // From the axis to the head, across (x) and along the screen's normal (z, toward the head).
        double qz = p.Distance - r;
        double a = d.X * d.X + d.Z * d.Z;
        if (a < 1e-12) return null;
        double b = 2 * qz * d.Z;
        double c = qz * qz - r * r;
        double disc = b * b - 4 * a * c;
        if (disc < 0) return null;
        double s = Math.Sqrt(disc);
        // The stable form of the two roots.
        double k = -0.5 * (b + (b >= 0 ? s : -s));
        double root0 = k / a;
        double root1 = root0;
        int count = 1;
        if (k != 0)
        {
            root1 = c / k;
            count = 2;
            // roots.sort()
            if (root1 < root0) (root0, root1) = (root1, root0);
        }
        for (int i = 0; i < count; i++)
        {
            double t = i == 0 ? root0 : root1;
            if (t <= 1e-6) continue;
            double px = t * d.X;
            double pz = qz + t * d.Z;
            // The screen's half of the cylinder is the far one.
            if (pz >= 0) continue;
            double th = Math.Atan2(px, -pz);
            return (T: t, X: th / (2 * half), Y: t * d.Y / size.Y);
        }
        return null;
    }
}

public partial record struct PanelPlacement
{
    /// <summary>
    /// The screen as a triangle strip in head-centred space (x right, y up, z back, metres), uv (0, 0) at the
    /// picture's top left.
    ///
    /// Flat: 4 vertices, the unit quad's corners times `ModelMatrix`, in the renderer's order (bottom left, bottom
    /// right, top left, top right). Curved: bent round a vertical cylinder as Pocket XR's PanelCurve does (radius
    /// distance / curve, angular width kept, half arc at most 85 degrees), [columns] columns from left to right,
    /// each column's top vertex then its bottom one (2 * (columns + 1) vertices), then turned into place by yaw and
    /// pitch. Both wind the same way (counter clockwise seen from the head).
    /// </summary>
    public readonly PanelVertex[] Mesh(int columns = 64)
    {
        if (!PanelCurve.IsCurved(this))
        {
            Mat4 m = ModelMatrix;
            Vec2f[] corners =
            {
                new Vec2f(-0.5f, -0.5f), new Vec2f(0.5f, -0.5f), new Vec2f(-0.5f, 0.5f), new Vec2f(0.5f, 0.5f),
            };
            var flat = new PanelVertex[4];
            for (int i = 0; i < corners.Length; i++)
            {
                Vec2f c = corners[i];
                Vec4f q = m * new Vec4f(c.X, c.Y, 0, 1);
                var uv = new Vec2f(c.X + 0.5f, 0.5f - c.Y);
                flat[i] = new PanelVertex(position: new Vec3f(q.X, q.Y, q.Z), uv: uv);
            }
            return flat;
        }
        int n = Math.Max(columns, 1);
        Quat t = Turn;
        var result = new PanelVertex[2 * (n + 1)];
        for (int i = 0; i <= n; i++)
        {
            double f = (double)i / (double)n;
            Vec3 top = t.Rotate(PanelCurve.LocalPoint(this, x: f - 0.5, y: 0.5));
            Vec3 bottom = t.Rotate(PanelCurve.LocalPoint(this, x: f - 0.5, y: -0.5));
            result[2 * i] = new PanelVertex(
                position: new Vec3f((float)top.X, (float)top.Y, (float)top.Z),
                uv: new Vec2f((float)f, 0));
            result[2 * i + 1] = new PanelVertex(
                position: new Vec3f((float)bottom.X, (float)bottom.Y, (float)bottom.Z),
                uv: new Vec2f((float)f, 1));
        }
        return result;
    }
}
