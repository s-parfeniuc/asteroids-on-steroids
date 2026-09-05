using System;
using System.Collections.Generic;

namespace AsteroidsSim.Fracture;

/// <summary>A build-time point. Double precision — see <see cref="Geometry2D"/>.</summary>
public readonly struct Vec2d : IEquatable<Vec2d>
{
    public readonly double X;
    public readonly double Y;
    public Vec2d(double x, double y) { X = x; Y = y; }

    public static Vec2d operator +(Vec2d a, Vec2d b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2d operator -(Vec2d a, Vec2d b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2d operator *(Vec2d a, double s) => new(a.X * s, a.Y * s);

    public bool Equals(Vec2d other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is Vec2d o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Vec2d a, Vec2d b) => a.Equals(b);
    public static bool operator !=(Vec2d a, Vec2d b) => !a.Equals(b);
}

/// <summary>
/// Polygon primitives for body construction and the contact narrow phase, transcribed from
/// <c>prototypes/stress-fracture-v9.html</c>.
/// </summary>
/// <remarks>
/// <para><b>Why these are double.</b> The prototype is JavaScript, so every number in it is a
/// double, and body construction is where that matters most: seed placement, half-plane clipping
/// and centroid/inertia integrals feed directly into the cell shapes and therefore into every
/// downstream measurement. Construction happens once per body and is not on the tick, so matching
/// the reference exactly costs nothing here, and the results narrow to float when they are baked
/// into the simulation arrays.</para>
///
/// <para>Determinism is unaffected: <c>+ - * /</c> and <c>Sqrt</c> are exactly specified by
/// IEEE 754 in double just as they are in float. No transcendental is used in this file.</para>
/// </remarks>
public static class Geometry2D
{
    /// <summary>Signed area (shoelace). Positive for counter-clockwise winding.</summary>
    public static double Area(IReadOnlyList<Vec2d> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vec2d q = p[i], r = p[(i + 1) % p.Count];
            a += q.X * r.Y - r.X * q.Y;
        }
        return a / 2;
    }

    /// <summary>
    /// Area-weighted centroid, falling back to the vertex mean for a degenerate polygon — the
    /// fallback is the prototype's and must be preserved, because it is what keeps a sliver cell
    /// from producing a NaN centroid that would poison the whole body.
    /// </summary>
    public static Vec2d Centroid(IReadOnlyList<Vec2d> p)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vec2d q = p[i], r = p[(i + 1) % p.Count];
            double c = q.X * r.Y - r.X * q.Y;
            a += c;
            cx += (q.X + r.X) * c;
            cy += (q.Y + r.Y) * c;
        }
        a *= 0.5;
        if (System.Math.Abs(a) < 1e-9)
        {
            double sx = 0, sy = 0;
            for (int i = 0; i < p.Count; i++) { sx += p[i].X; sy += p[i].Y; }
            return new Vec2d(sx / p.Count, sy / p.Count);
        }
        return new Vec2d(cx / (6 * a), cy / (6 * a));
    }

    /// <summary>Second moment of area about <paramref name="c"/>, per unit density.</summary>
    public static double PolyInertia(IReadOnlyList<Vec2d> p, Vec2d c)
    {
        double n = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vec2d a = new(p[i].X - c.X, p[i].Y - c.Y);
            Vec2d q = p[(i + 1) % p.Count];
            Vec2d b = new(q.X - c.X, q.Y - c.Y);
            double cr = System.Math.Abs(a.X * b.Y - b.X * a.Y);
            n += cr * (a.X * a.X + a.X * b.X + b.X * b.X + a.Y * a.Y + a.Y * b.Y + b.Y * b.Y);
        }
        return n / 12;
    }

    public static double Perimeter(IReadOnlyList<Vec2d> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vec2d a = p[i], b = p[(i + 1) % p.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            s += System.Math.Sqrt(dx * dx + dy * dy);
        }
        return s;
    }

    public static void BBox(IReadOnlyList<Vec2d> p,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = 1e9; minY = 1e9; maxX = -1e9; maxY = -1e9;
        for (int i = 0; i < p.Count; i++)
        {
            Vec2d v = p[i];
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
        }
    }

    /// <summary>Crossing-number point-in-polygon, matching the prototype's edge conventions.</summary>
    public static bool PointInPoly(Vec2d p, IReadOnlyList<Vec2d> poly)
    {
        bool ins = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
            if (((yi > p.Y) != (yj > p.Y)) && (p.X < (xj - xi) * (p.Y - yi) / (yj - yi) + xi))
                ins = !ins;
        }
        return ins;
    }

    /// <summary>
    /// Clips a convex polygon to the half-plane containing points with
    /// <c>(v - pt) · n &gt;= 0</c>. This is the Voronoi cell builder's only operation.
    /// </summary>
    /// <remarks>
    /// Note that when every vertex is strictly inside, the output is the input, vertex for vertex,
    /// with no arithmetic applied. That is what makes it safe for the caller to skip a clip whose
    /// half-plane cannot reach the polygon: skipping is bit-identical to performing it.
    /// </remarks>
    public static void ClipHalf(List<Vec2d> poly, Vec2d pt, Vec2d n, List<Vec2d> outPoly)
    {
        outPoly.Clear();
        int N = poly.Count;
        for (int i = 0; i < N; i++)
        {
            Vec2d cur = poly[i], nxt = poly[(i + 1) % N];
            double dc = (cur.X - pt.X) * n.X + (cur.Y - pt.Y) * n.Y;
            double dn = (nxt.X - pt.X) * n.X + (nxt.Y - pt.Y) * n.Y;
            if (dc >= 0) outPoly.Add(cur);
            if ((dc >= 0) != (dn >= 0))
            {
                double t = dc / (dc - dn);
                outPoly.Add(new Vec2d(cur.X + t * (nxt.X - cur.X), cur.Y + t * (nxt.Y - cur.Y)));
            }
        }
    }

    /// <summary>
    /// Monotone-chain convex hull. The sort is by X then Y and must be stable in the same sense the
    /// prototype's is — duplicate points are removed by the turn test, not by the ordering.
    /// </summary>
    public static List<Vec2d> ConvexHull(List<Vec2d> pts)
    {
        var s = new List<Vec2d>(pts);
        s.Sort(static (a, b) =>
        {
            int c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        });

        static double Cross(Vec2d o, Vec2d a, Vec2d b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var lo = new List<Vec2d>();
        for (int i = 0; i < s.Count; i++)
        {
            while (lo.Count >= 2 && Cross(lo[lo.Count - 2], lo[lo.Count - 1], s[i]) <= 0)
                lo.RemoveAt(lo.Count - 1);
            lo.Add(s[i]);
        }
        var up = new List<Vec2d>();
        for (int i = s.Count - 1; i >= 0; i--)
        {
            while (up.Count >= 2 && Cross(up[up.Count - 2], up[up.Count - 1], s[i]) <= 0)
                up.RemoveAt(up.Count - 1);
            up.Add(s[i]);
        }
        lo.RemoveAt(lo.Count - 1);
        up.RemoveAt(up.Count - 1);
        lo.AddRange(up);
        return lo;
    }

    /// <summary>
    /// Length of the collinear overlap between segment a→b and segment c→d, or 0 if they are not
    /// collinear within the prototype's 0.7 px tolerance. This is what decides whether two Voronoi
    /// cells share an edge, and therefore whether they get a bond.
    /// </summary>
    public static double SegOverlap(Vec2d a, Vec2d b, Vec2d c, Vec2d d)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double L = System.Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-6) return 0;
        double ux = dx / L, uy = dy / L;
        if (System.Math.Abs((c.X - a.X) * (-uy) + (c.Y - a.Y) * ux) > 0.7) return 0;
        if (System.Math.Abs((d.X - a.X) * (-uy) + (d.Y - a.Y) * ux) > 0.7) return 0;
        double tc = (c.X - a.X) * ux + (c.Y - a.Y) * uy;
        double td = (d.X - a.X) * ux + (d.Y - a.Y) * uy;
        double hi = System.Math.Min(L, System.Math.Max(tc, td));
        double lo = System.Math.Max(0, System.Math.Min(tc, td));
        return System.Math.Max(0, hi - lo);
    }
}
