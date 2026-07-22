using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>A raycast hit against a specific entity's collider.</summary>
public readonly struct RayHit
{
    public readonly Entity  Entity;
    public readonly Vector2 Point;
    public readonly Vector2 Normal;
    public readonly float   Distance;
    public readonly int     PartIndex;   // CompoundShape part struck; -1 for simple shapes

    public RayHit(Entity entity, Vector2 point, Vector2 normal, float distance, int partIndex)
    {
        Entity    = entity;
        Point     = point;
        Normal    = normal;
        Distance  = distance;
        PartIndex = partIndex;
    }
}

/// <summary>
/// Spatial queries against the collision world. Raycasting underpins raycast
/// bullets (no tunnelling, exact impact point + normal + struck part).
/// </summary>
public static class PhysicsQueries
{
    // Per-thread candidate buffer for the index-accelerated raycast (reused across calls, so a
    // per-frame storm of bullet raycasts allocates nothing). ThreadStatic guards a future parallel path.
    [ThreadStatic] private static List<Entity>? _rayCandidates;

    /// <summary>
    /// Casts a ray from <paramref name="from"/> to <paramref name="to"/> and returns the nearest
    /// collider hit whose Collider.Layer intersects <paramref name="layerMask"/>, using the shared
    /// broad-phase <paramref name="index"/>: only colliders in cells the segment crosses are tested.
    /// This is O(colliders near the ray), independent of total world entity count.
    /// </summary>
    public static bool Raycast(World world, ISpatialIndex index,
                               Vector2 from, Vector2 to, int layerMask, out RayHit hit)
    {
        hit = default;

        Vector2 delta   = to - from;
        float   maxDist = delta.Length();
        if (maxDist < 1e-6f) return false;
        Vector2 dir = delta / maxDist;

        var candidates = _rayCandidates ??= new List<Entity>(32);
        candidates.Clear();
        index.QuerySegment(from, to, candidates);

        bool   found   = false;
        float  best    = maxDist;
        RayHit bestHit = default;

        foreach (var e in candidates)
        {
            if (!world.IsAlive(e)) continue;
            if (world.HasComponent<DisabledTag>(e)) continue;
            if (!world.HasComponent<Collider>(e))   continue;

            ref var c = ref world.GetComponent<Collider>(e);
            if ((c.Layer & layerMask) == 0) continue;
            ref var t = ref world.GetComponent<Transform>(e);

            // Broad-phase reject: skip the (potentially per-cell) shape raycast unless the ray
            // segment actually crosses the whole shape's world AABB within the current best distance.
            var (amin, amax) = c.Shape.GetAABB(t.Position, t.Rotation);
            if (!RaySegmentHitsAabb(from, dir, best, amin, amax)) continue;

            if (c.Shape.Raycast(from, dir, best, t.Position, t.Rotation, out var r))
            {
                best    = r.Distance;   // tighten so we keep only the nearest
                bestHit = new RayHit(e, r.Point, r.Normal, r.Distance, r.PartIndex);
                found   = true;
            }
        }

        hit = bestHit;
        return found;
    }

    /// <summary>
    /// Brute-force raycast with no spatial index — scans every collider. Retained as a fallback
    /// for callers that run before the broad phase is built; prefer the index overload in the loop.
    /// </summary>
    public static bool Raycast(World world, Vector2 from, Vector2 to, int layerMask, out RayHit hit)
    {
        hit = default;

        Vector2 delta   = to - from;
        float   maxDist = delta.Length();
        if (maxDist < 1e-6f) return false;
        Vector2 dir = delta / maxDist;

        bool    found   = false;
        float   best    = maxDist;
        RayHit  bestHit = default;

        world.ForEach<Transform, Collider>((Entity e, ref Transform t, ref Collider c) =>
        {
            if ((c.Layer & layerMask) == 0) return;
            if (world.HasComponent<DisabledTag>(e)) return;

            var (amin, amax) = c.Shape.GetAABB(t.Position, t.Rotation);
            if (!RaySegmentHitsAabb(from, dir, best, amin, amax)) return;

            if (c.Shape.Raycast(from, dir, best, t.Position, t.Rotation, out var r))
            {
                best    = r.Distance;   // tighten so we keep only the nearest
                bestHit = new RayHit(e, r.Point, r.Normal, r.Distance, r.PartIndex);
                found   = true;
            }
        });

        hit = bestHit;
        return found;
    }

    /// <summary>Slab test: does the segment origin→origin+dir·maxDist intersect the AABB [min,max]?
    /// O(1), branch-light; used as a broad-phase gate before an expensive per-cell shape raycast.</summary>
    private static bool RaySegmentHitsAabb(Vector2 origin, Vector2 dir, float maxDist,
                                           Vector2 min, Vector2 max)
    {
        float tmin = 0f, tmax = maxDist;

        // X slab.
        if (MathF.Abs(dir.X) < 1e-8f)
        {
            if (origin.X < min.X || origin.X > max.X) return false;
        }
        else
        {
            float inv = 1f / dir.X;
            float t1 = (min.X - origin.X) * inv, t2 = (max.X - origin.X) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }

        // Y slab.
        if (MathF.Abs(dir.Y) < 1e-8f)
        {
            if (origin.Y < min.Y || origin.Y > max.Y) return false;
        }
        else
        {
            float inv = 1f / dir.Y;
            float t1 = (min.Y - origin.Y) * inv, t2 = (max.Y - origin.Y) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }

        return true;
    }

    /// <summary>True if a circle (centre + radius) overlaps any existing collider on
    /// <paramref name="layerMask"/>. Conservative (tests the circle against each collider's AABB), so
    /// it may reject a near-miss — ideal for spawn placement, where the cost of overlap (stuck/jitter)
    /// far outweighs an occasional retry. Disabled colliders are ignored.</summary>
    public static bool OverlapsCircle(World world, Vector2 centre, float radius, int layerMask)
    {
        bool hit = false;
        float r2 = radius * radius;
        world.ForEach<Transform, Collider>((Entity e, ref Transform t, ref Collider c) =>
        {
            if (hit) return;
            if ((c.Layer & layerMask) == 0) return;
            if (world.HasComponent<DisabledTag>(e)) return;

            var (min, max) = c.Shape.GetAABB(t.Position, t.Rotation);
            // Closest point on the AABB to the circle centre.
            float cx = Math.Clamp(centre.X, min.X, max.X);
            float cy = Math.Clamp(centre.Y, min.Y, max.Y);
            float dx = centre.X - cx, dy = centre.Y - cy;
            if (dx * dx + dy * dy < r2) hit = true;
        });
        return hit;
    }
}
