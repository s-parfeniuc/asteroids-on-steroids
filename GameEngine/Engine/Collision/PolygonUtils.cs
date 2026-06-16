using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Pure-geometry utilities for convex polygon generation, clipping, and physical
/// property computation. Consumed by the destruction module (tessellation, fragment
/// physics). No ECS, no rendering dependencies.
///
/// Coordinate convention: matches the engine's Y-down screen space.
/// Winding order produced by GenerateConvex: clockwise (matches PolygonShape).
///
/// Critical invariant maintained by every caller:
///   PolygonShape vertices must be centroid-relative (local space).
///   Transform.Position IS the world centroid.
/// Use RecenterVertices() after any split to re-establish this invariant.
/// </summary>
public static class PolygonUtils
{
    // -------------------------------------------------------------------------
    // Generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a random convex polygon centred at the origin with clockwise winding.
    ///
    /// Uses Valtr's algorithm, which produces a genuinely random convex polygon with
    /// exactly <paramref name="sides"/> vertices. (Naively placing vertices at sorted
    /// angles with random radii does NOT preserve convexity — a short-radius vertex
    /// between two long ones forms a reflex corner.) The result is scaled so the mean
    /// vertex distance from the centroid equals <paramref name="radius"/>.
    /// </summary>
    /// <param name="radiusVariation">
    /// Retained for API compatibility; Valtr's algorithm produces its own natural
    /// irregularity so this value is not used directly.
    /// </param>
    public static (Vector2[] vertices, float[] faultAngles) GenerateConvex(
        int sides,
        float radius,
        Random rng,
        int faultCount = 3,
        float radiusVariation = 0.22f)
    {
        if (sides < 3) throw new ArgumentOutOfRangeException(nameof(sides), "Need at least 3 sides.");

        var verts = RandomConvexPolygon(sides, rng);

        // Re-centre on the area centroid, then scale to the requested mean radius.
        var (_, centred) = RecenterVertices(verts);
        float meanR = 0f;
        foreach (var v in centred) meanR += v.Length();
        meanR /= centred.Length;
        float scale = meanR > 1e-6f ? radius / meanR : 1f;
        for (int i = 0; i < centred.Length; i++) centred[i] *= scale;

        // Clockwise winding (engine convention, Y-down). Positive shoelace area = CCW
        // in Y-up math; reverse to make it CW.
        if (ComputeArea(centred) > 0f) Array.Reverse(centred);

        var faults = new float[faultCount];
        for (int i = 0; i < faultCount; i++)
            faults[i] = (float)(rng.NextDouble() * 2 * MathF.PI);

        return (centred, faults);
    }

    /// <summary>
    /// Valtr's algorithm for a uniformly random convex polygon with exactly n vertices.
    /// Builds n edge vectors whose x- and y-components each sum to zero (so the chain
    /// closes), sorts them by angle, and connects them head-to-tail. Sorting by angle
    /// guarantees the result is convex. Output is roughly in a unit-scale box around origin.
    /// </summary>
    private static Vector2[] RandomConvexPolygon(int n, Random rng)
    {
        var xs = new float[n];
        var ys = new float[n];
        for (int i = 0; i < n; i++) { xs[i] = (float)rng.NextDouble(); ys[i] = (float)rng.NextDouble(); }
        Array.Sort(xs);
        Array.Sort(ys);

        float minX = xs[0], maxX = xs[n - 1];
        float minY = ys[0], maxY = ys[n - 1];

        // Split the interior points of each coordinate into two monotone chains,
        // producing signed component vectors that sum to zero.
        var xVec = new float[n];
        var yVec = new float[n];

        float lastTop = minX, lastBot = minX;
        for (int i = 1; i < n - 1; i++)
        {
            float x = xs[i];
            if (rng.Next(2) == 0) { xVec[i - 1] = x - lastTop; lastTop = x; }
            else                  { xVec[i - 1] = lastBot - x; lastBot = x; }
        }
        xVec[n - 2] = maxX - lastTop;
        xVec[n - 1] = lastBot - maxX;

        float lastLeft = minY, lastRight = minY;
        for (int i = 1; i < n - 1; i++)
        {
            float y = ys[i];
            if (rng.Next(2) == 0) { yVec[i - 1] = y - lastLeft;  lastLeft = y; }
            else                  { yVec[i - 1] = lastRight - y; lastRight = y; }
        }
        yVec[n - 2] = maxY - lastLeft;
        yVec[n - 1] = lastRight - maxY;

        // Randomly pair x- and y-components (shuffle y), forming edge vectors.
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (yVec[i], yVec[j]) = (yVec[j], yVec[i]);
        }

        var vecs = new Vector2[n];
        for (int i = 0; i < n; i++) vecs[i] = new Vector2(xVec[i], yVec[i]);

        // Sort edge vectors by angle → convex chain.
        Array.Sort(vecs, (a, b) => MathF.Atan2(a.Y, a.X).CompareTo(MathF.Atan2(b.Y, b.X)));

        // Connect head-to-tail.
        var pts = new Vector2[n];
        Vector2 cur = Vector2.Zero;
        for (int i = 0; i < n; i++) { pts[i] = cur; cur += vecs[i]; }
        return pts;
    }

    // -------------------------------------------------------------------------
    // Sutherland-Hodgman half-plane clip
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clips a convex polygon against a half-plane, keeping the side where
    ///   dot(point − planePoint, planeNormal) ≥ 0
    /// Returns an empty list if the entire polygon is outside.
    /// </summary>
    public static List<Vector2> ClipConvexByHalfPlane(
        IReadOnlyList<Vector2> polygon,
        Vector2 planePoint,
        Vector2 planeNormal)
    {
        var output = new List<Vector2>(polygon.Count + 1);
        int n = polygon.Count;
        if (n == 0) return output;

        for (int i = 0; i < n; i++)
        {
            Vector2 curr = polygon[i];
            Vector2 next = polygon[(i + 1) % n];

            float dCurr = Vector2.Dot(curr - planePoint, planeNormal);
            float dNext = Vector2.Dot(next - planePoint, planeNormal);

            bool currIn = dCurr >= 0f;
            bool nextIn = dNext >= 0f;

            if (currIn) output.Add(curr);

            if (currIn != nextIn)
            {
                float t = dCurr / (dCurr - dNext);
                output.Add(curr + t * (next - curr));
            }
        }

        return output;
    }

    // -------------------------------------------------------------------------
    // Geometric properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// Signed area via the shoelace formula.
    /// Positive → CCW in Y-up (math); the same vertex order is CW in Y-down (screen).
    /// </summary>
    public static float ComputeArea(IReadOnlyList<Vector2> verts)
    {
        float area = 0f;
        int n = verts.Count;
        for (int i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }

    /// <summary>Area centroid via the shoelace triangulation formula.</summary>
    public static Vector2 ComputeCentroid(IReadOnlyList<Vector2> verts)
    {
        var c = Vector2.Zero;
        float area = 0f;
        int n = verts.Count;

        for (int i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            float cross = a.X * b.Y - b.X * a.Y;
            area += cross;
            c += (a + b) * cross;
        }

        area *= 0.5f;
        if (MathF.Abs(area) < 1e-6f)
        {
            var sum = Vector2.Zero;
            foreach (var v in verts) sum += v;
            return sum / n;
        }

        return c / (6f * area);
    }

    /// <summary>Moment of inertia about the centroid for a solid uniform polygon.</summary>
    public static float ComputeInertia(IReadOnlyList<Vector2> centroidRelativeVerts, float mass)
    {
        float area = MathF.Abs(ComputeArea(centroidRelativeVerts));
        if (area < 1e-6f) return 0f;

        float density = mass / area;
        float inertia = 0f;
        int n = centroidRelativeVerts.Count;

        for (int i = 0; i < n; i++)
        {
            var a = centroidRelativeVerts[i];
            var b = centroidRelativeVerts[(i + 1) % n];
            float cross = MathF.Abs(a.X * b.Y - b.X * a.Y);
            inertia += cross * (Vector2.Dot(a, a) + Vector2.Dot(a, b) + Vector2.Dot(b, b));
        }

        return density * inertia / 12f;
    }

    // -------------------------------------------------------------------------
    // Centroid invariant helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-centres world-space vertices around their area centroid.
    /// Returns (centroid, centroid-relative local vertices).
    /// Call after re-centring fragment geometry to maintain Transform.Position == world centroid.
    /// </summary>
    public static (Vector2 centroid, Vector2[] localVertices) RecenterVertices(
        IReadOnlyList<Vector2> worldVertices)
    {
        Vector2 centroid = ComputeCentroid(worldVertices);
        var local = new Vector2[worldVertices.Count];
        for (int i = 0; i < worldVertices.Count; i++)
            local[i] = worldVertices[i] - centroid;
        return (centroid, local);
    }

    // -------------------------------------------------------------------------
    // Convex hull
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the convex hull of <paramref name="points"/> using Andrew's monotone chain
    /// (O(n log n)). Returns vertices in clockwise winding order (engine convention —
    /// <see cref="ComputeArea"/> returns a negative value for this result).
    /// </summary>
    public static Vector2[] ConvexHull(IReadOnlyList<Vector2> points)
    {
        int n = points.Count;
        if (n < 3) return [.. points];

        var sorted = new Vector2[n];
        for (int i = 0; i < n; i++) sorted[i] = points[i];
        Array.Sort(sorted, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        // Lower hull (left → right) then upper hull (right → left).
        var lower = new List<Vector2>(n);
        foreach (var p in sorted)
        {
            while (lower.Count >= 2 && Cross2D(lower[^2], lower[^1], p) <= 0f)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<Vector2>(n);
        for (int i = n - 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (upper.Count >= 2 && Cross2D(upper[^2], upper[^1], p) <= 0f)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        var hull = lower.ToArray();
        // Monotone chain gives CCW in Y-up math (ComputeArea > 0); reverse to CW.
        if (ComputeArea(hull) > 0f) Array.Reverse(hull);
        return hull;
    }

    private static float Cross2D(Vector2 o, Vector2 a, Vector2 b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    // -------------------------------------------------------------------------
    // Surface projection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the nearest point on the polygon boundary to <paramref name="point"/>.
    /// The result is always on an edge of the polygon, never inside it.
    /// Use this to correct a contact point that may have tunnelled into the interior.
    /// </summary>
    public static Vector2 NearestPointOnBoundary(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        float bestDist = float.MaxValue;
        Vector2 best = polygon[0];
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a  = polygon[i];
            Vector2 b  = polygon[(i + 1) % n];
            Vector2 ab = b - a;
            float lenSq = ab.LengthSquared();
            float t     = lenSq > 1e-10f
                          ? Math.Clamp(Vector2.Dot(point - a, ab) / lenSq, 0f, 1f)
                          : 0f;
            Vector2 proj = a + t * ab;
            float d = (proj - point).LengthSquared();
            if (d < bestDist) { bestDist = d; best = proj; }
        }
        return best;
    }

}
