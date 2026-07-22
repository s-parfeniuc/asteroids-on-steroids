using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

public sealed class CircleShape : CollisionShape
{
    public float Radius { get; }

    public CircleShape(float radius) => Radius = radius;

    public override ContactInfo? Intersects(Vector2 posA, float rotA,
                                            CollisionShape other,
                                            Vector2 posB, float rotB) =>
        other.IntersectsCircle(posB, rotB, this, posA);

    public override (Vector2 min, Vector2 max) GetAABB(Vector2 pos, float rot) =>
        (pos - new Vector2(Radius), pos + new Vector2(Radius));

    // ---- Double dispatch ----

    internal override ContactInfo? IntersectsCircle(Vector2 posA, float rotA,
                                                    CircleShape circle, Vector2 posB)
    {
        // posA = this circle's centre (= entity B due to dispatch)
        // posB = other circle's centre (= entity A due to dispatch)
        // Normal must point from B toward A so TrySeparate and the impulse handler
        // both receive the direction in which A should move to separate.
        Vector2 diff     = posB - posA;   // A_pos - B_pos → points B→A
        float   distSq   = diff.LengthSquared();
        float   radSum   = Radius + circle.Radius;

        if (distSq >= radSum * radSum) return null;

        float   dist     = MathF.Sqrt(distSq);
        Vector2 normal   = dist > 1e-6f ? diff / dist : Vector2.UnitX;
        float   depth    = radSum - dist;
        Vector2 contact  = posB - normal * circle.Radius;  // A's surface facing B

        return new ContactInfo(normal, depth, contact);
    }

    internal override ContactInfo? IntersectsPolygon(Vector2 posA, float rotA,
                                                     PolygonShape polygon,
                                                     Vector2 posB, float rotB)
    {
        // Delegate to polygon — it knows how to test circle vs polygon.
        var result = polygon.IntersectsCircle(posB, rotB, this, posA);
        return result?.Flipped();
    }

    public override bool Raycast(Vector2 origin, Vector2 dir, float maxDist,
                                 Vector2 pos, float rot, out RayCastResult hit)
    {
        hit = default;
        // |origin + t*dir - centre|² = R²  →  t² + 2b t + c = 0  (dir is unit)
        Vector2 m = origin - pos;
        float   b = Vector2.Dot(m, dir);
        float   c = Vector2.Dot(m, m) - Radius * Radius;
        if (c > 0f && b > 0f) return false;          // origin outside and pointing away
        float disc = b * b - c;
        if (disc < 0f) return false;                 // ray misses the circle
        float t = -b - MathF.Sqrt(disc);
        if (t < 0f) t = 0f;                          // origin inside → hit at origin
        if (t > maxDist) return false;
        Vector2 point  = origin + dir * t;
        Vector2 normal = Vector2.Normalize(point - pos);
        hit = new RayCastResult(t, point, normal);
        return true;
    }

    internal override ContactInfo? IntersectsAABB(Vector2 posA, float rotA,
                                                  AABBShape aabb, Vector2 posB)
    {
        // posA = circle centre, posB = AABB centre
        Vector2 half    = new(aabb.HalfWidth, aabb.HalfHeight);
        Vector2 local   = posA - posB;
        Vector2 clamped = Vector2.Clamp(local, -half, half);
        Vector2 closest = posB + clamped;
        Vector2 diff    = posA - closest;
        float   distSq  = diff.LengthSquared();

        if (distSq >= Radius * Radius) return null;

        float   dist    = MathF.Sqrt(distSq);
        Vector2 normal  = dist > 1e-6f ? diff / dist : Vector2.UnitY;
        float   depth   = Radius - dist;

        return new ContactInfo(normal, depth, closest);
    }
}
