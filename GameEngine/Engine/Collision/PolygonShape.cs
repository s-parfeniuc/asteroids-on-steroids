using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Convex polygon collision shape. Narrow phase uses the Separating Axis
/// Theorem (SAT). Vertices are defined in local space (centred at origin).
/// They are transformed to world space each test — no caching.
///
/// Winding order: clockwise (consistent with GDI+ Y-down convention).
/// Must be convex. Concave polygons produce incorrect results.
/// </summary>
public sealed class PolygonShape : CollisionShape
{
    // Local-space vertices (centred at origin, no rotation applied).
    private readonly Vector2[] _localVertices;

    public int VertexCount => _localVertices.Length;

    public PolygonShape(Vector2[] localVertices)
    {
        if (localVertices.Length < 3)
            throw new ArgumentException("A polygon needs at least 3 vertices.");
        _localVertices = localVertices;
    }

    // -------------------------------------------------------------------------
    // CollisionShape overrides
    // -------------------------------------------------------------------------

    public override ContactInfo? Intersects(Vector2 posA, float rotA,
                                            CollisionShape other,
                                            Vector2 posB, float rotB) =>
        other.IntersectsPolygon(posB, rotB, this, posA, rotA);

    public override (Vector2 min, Vector2 max) GetAABB(Vector2 pos, float rot)
    {
        var verts = TransformVertices(pos, rot);
        var min   = new Vector2(float.MaxValue);
        var max   = new Vector2(float.MinValue);
        foreach (var v in verts)
        {
            min = Vector2.Min(min, v);
            max = Vector2.Max(max, v);
        }
        return (min, max);
    }

    // -------------------------------------------------------------------------
    // Double dispatch
    // -------------------------------------------------------------------------

    internal override ContactInfo? IntersectsCircle(Vector2 posA, float rotA,
                                                    CircleShape circle, Vector2 posB)
    {
        // posA = this polygon, posB = circle centre
        var verts = TransformVertices(posA, rotA);
        return SatCirclePolygon(verts, posA, circle.Radius, posB);
    }

    internal override ContactInfo? IntersectsPolygon(Vector2 posA, float rotA,
                                                     PolygonShape other,
                                                     Vector2 posB, float rotB)
    {
        var vertsA = TransformVertices(posA, rotA);
        var vertsB = other.TransformVertices(posB, rotB);
        return SatPolygonPolygon(vertsA, vertsB);
    }

    internal override ContactInfo? IntersectsAABB(Vector2 posA, float rotA,
                                                  AABBShape aabb, Vector2 posB)
    {
        // Convert AABB to polygon and run SAT.
        var aabbPoly = aabb.ToPolygon(posB);
        return IntersectsPolygon(posA, rotA, aabbPoly, posB, 0f);
    }

    // -------------------------------------------------------------------------
    // SAT — polygon vs polygon
    // -------------------------------------------------------------------------

    /// <summary>
    /// Separating Axis Theorem for two convex polygons.
    /// Tests all edge normals of both polygons as potential separating axes.
    /// Returns null if separated; ContactInfo with minimum overlap otherwise.
    /// Normal in ContactInfo points from B toward A.
    /// </summary>
    private static ContactInfo? SatPolygonPolygon(Vector2[] a, Vector2[] b)
    {
        float   minDepth  = float.MaxValue;
        Vector2 minNormal = Vector2.Zero;

        // Test axes from A's edges, then B's edges.
        if (!TestAxes(a, b, a, ref minDepth, ref minNormal)) return null;
        if (!TestAxes(a, b, b, ref minDepth, ref minNormal)) return null;

        // Ensure normal points from B toward A (centre-to-centre direction).
        Vector2 centreA = Centroid(a);
        Vector2 centreB = Centroid(b);
        if (Vector2.Dot(minNormal, centreA - centreB) < 0)
            minNormal = -minNormal;

        Vector2 contact = FindContactPoint(a, b, minNormal);
        return new ContactInfo(minNormal, minDepth, contact);
    }

    /// <summary>
    /// Tests all edge-normal axes from 'axisSource' for separation between
    /// projections of a and b. Returns false (separated) early if any axis
    /// separates. Updates minDepth/minNormal if a smaller overlap is found.
    /// </summary>
    private static bool TestAxes(Vector2[] a, Vector2[] b,
                                  Vector2[] axisSource,
                                  ref float minDepth, ref Vector2 minNormal)
    {
        for (int i = 0; i < axisSource.Length; i++)
        {
            Vector2 edge   = axisSource[(i + 1) % axisSource.Length] - axisSource[i];
            Vector2 axis   = Vector2.Normalize(new Vector2(-edge.Y, edge.X));

            Project(a, axis, out float minA, out float maxA);
            Project(b, axis, out float minB, out float maxB);

            float overlap = MathF.Min(maxA, maxB) - MathF.Max(minA, minB);
            if (overlap <= 0) return false;   // separating axis found

            if (overlap < minDepth)
            {
                minDepth  = overlap;
                minNormal = axis;
            }
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // SAT — circle vs polygon
    // -------------------------------------------------------------------------

    private static ContactInfo? SatCirclePolygon(Vector2[] verts,
                                                  Vector2 polyPos,
                                                  float radius,
                                                  Vector2 circlePos)
    {
        float   minDepth  = float.MaxValue;
        Vector2 minNormal = Vector2.Zero;

        // Test each edge normal of the polygon.
        for (int i = 0; i < verts.Length; i++)
        {
            Vector2 edge   = verts[(i + 1) % verts.Length] - verts[i];
            Vector2 axis   = Vector2.Normalize(new Vector2(-edge.Y, edge.X));

            Project(verts, axis, out float minP, out float maxP);
            float circleProj = Vector2.Dot(circlePos, axis);
            float minC = circleProj - radius;
            float maxC = circleProj + radius;

            float overlap = MathF.Min(maxP, maxC) - MathF.Max(minP, minC);
            if (overlap <= 0) return null;

            if (overlap < minDepth) { minDepth = overlap; minNormal = axis; }
        }

        // Also test the axis from circle centre to nearest polygon vertex.
        Vector2 nearest    = NearestVertex(verts, circlePos);
        Vector2 vertAxis   = Vector2.Normalize(circlePos - nearest);

        if (vertAxis.LengthSquared() > 1e-10f)
        {
            Project(verts, vertAxis, out float minP, out float maxP);
            float circleProj = Vector2.Dot(circlePos, vertAxis);
            float minC = circleProj - radius;
            float maxC = circleProj + radius;

            float overlap = MathF.Min(maxP, maxC) - MathF.Max(minP, minC);
            if (overlap <= 0) return null;
            if (overlap < minDepth) { minDepth = overlap; minNormal = vertAxis; }
        }

        // Normal should point from polygon toward circle.
        if (Vector2.Dot(minNormal, circlePos - polyPos) < 0)
            minNormal = -minNormal;

        Vector2 contact = circlePos - minNormal * radius;
        return new ContactInfo(minNormal, minDepth, contact);
    }

    // -------------------------------------------------------------------------
    // Raycast — Cyrus-Beck clip of the ray against the convex half-planes
    // -------------------------------------------------------------------------

    public override bool Raycast(Vector2 origin, Vector2 dir, float maxDist,
                                 Vector2 pos, float rot, out RayCastResult hit)
    {
        hit = default;
        var verts    = TransformVertices(pos, rot);
        var centroid = Centroid(verts);

        float   tEnter = 0f, tExit = maxDist;
        Vector2 enterNormal = Vector2.Zero;
        bool    hasEnter = false;
        int     n = verts.Length;

        for (int i = 0; i < n; i++)
        {
            Vector2 a    = verts[i];
            Vector2 edge = verts[(i + 1) % n] - a;

            // Outward normal, oriented away from the centroid.
            Vector2 nrm = new Vector2(-edge.Y, edge.X);
            if (Vector2.Dot(nrm, a - centroid) < 0f) nrm = -nrm;
            nrm = Vector2.Normalize(nrm);

            float denom = Vector2.Dot(dir, nrm);
            if (MathF.Abs(denom) < 1e-9f)
            {
                // Parallel to this edge: if the origin is on the outside, no hit.
                if (Vector2.Dot(origin - a, nrm) > 0f) return false;
                continue;
            }

            float t = Vector2.Dot(a - origin, nrm) / denom;
            if (denom < 0f)            // entering the half-plane
            {
                if (t > tEnter) { tEnter = t; enterNormal = nrm; hasEnter = true; }
            }
            else                        // exiting the half-plane
            {
                if (t < tExit) tExit = t;
            }
            if (tEnter > tExit) return false;
        }

        if (!hasEnter) return false;             // origin inside, or grazing
        if (tEnter < 0f || tEnter > maxDist) return false;

        Vector2 point = origin + dir * tEnter;
        hit = new RayCastResult(tEnter, point, enterNormal);
        return true;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns world-space vertices by applying rotation then translation.
    /// Allocates a new array; use sparingly in hot paths.
    /// </summary>
    public Vector2[] GetWorldVertices(Vector2 pos, float rot) =>
        TransformVertices(pos, rot);

    /// <summary>Returns a copy of the centroid-relative local vertices.</summary>
    public Vector2[] GetLocalVertices() => (Vector2[])_localVertices.Clone();

    internal Vector2[] TransformVertices(Vector2 pos, float rot)
    {
        var result = new Vector2[_localVertices.Length];
        float cos  = MathF.Cos(rot);
        float sin  = MathF.Sin(rot);
        for (int i = 0; i < _localVertices.Length; i++)
        {
            var v = _localVertices[i];
            result[i] = new Vector2(
                v.X * cos - v.Y * sin + pos.X,
                v.X * sin + v.Y * cos + pos.Y);
        }
        return result;
    }

    private static void Project(Vector2[] verts, Vector2 axis,
                                 out float min, out float max)
    {
        min = max = Vector2.Dot(verts[0], axis);
        for (int i = 1; i < verts.Length; i++)
        {
            float p = Vector2.Dot(verts[i], axis);
            if (p < min) min = p;
            if (p > max) max = p;
        }
    }

    private static Vector2 Centroid(Vector2[] verts)
    {
        var sum = Vector2.Zero;
        foreach (var v in verts) sum += v;
        return sum / verts.Length;
    }

    private static Vector2 NearestVertex(Vector2[] verts, Vector2 point)
    {
        Vector2 best    = verts[0];
        float   bestSq  = (verts[0] - point).LengthSquared();
        for (int i = 1; i < verts.Length; i++)
        {
            float sq = (verts[i] - point).LengthSquared();
            if (sq < bestSq) { bestSq = sq; best = verts[i]; }
        }
        return best;
    }

    /// <summary>
    /// Approximates the contact point as the deepest vertex of B in A's
    /// direction (good enough for impulse response and particle spawning).
    /// </summary>
    private static Vector2 FindContactPoint(Vector2[] a, Vector2[] b, Vector2 normal)
    {
        Vector2 best    = b[0];
        float   bestDot = Vector2.Dot(b[0], -normal);
        for (int i = 1; i < b.Length; i++)
        {
            float d = Vector2.Dot(b[i], -normal);
            if (d > bestDot) { bestDot = d; best = b[i]; }
        }
        return best;
    }
}
