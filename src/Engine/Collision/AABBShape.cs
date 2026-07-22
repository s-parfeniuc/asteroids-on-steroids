using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Axis-aligned bounding box. Ignores rotation (stays axis-aligned regardless
/// of the entity's Transform.Rotation). Useful for UI elements and broad-phase
/// stand-ins. For rotating boxes, use PolygonShape with 4 vertices.
/// </summary>
public sealed class AABBShape : CollisionShape
{
    public float HalfWidth  { get; }
    public float HalfHeight { get; }

    public AABBShape(float halfWidth, float halfHeight)
    {
        HalfWidth  = halfWidth;
        HalfHeight = halfHeight;
    }

    public override ContactInfo? Intersects(Vector2 posA, float rotA,
                                            CollisionShape other,
                                            Vector2 posB, float rotB) =>
        other.IntersectsAABB(posB, rotB, this, posA);

    public override (Vector2 min, Vector2 max) GetAABB(Vector2 pos, float rot) =>
        (pos - new Vector2(HalfWidth, HalfHeight),
         pos + new Vector2(HalfWidth, HalfHeight));

    // ---- Double dispatch ----

    internal override ContactInfo? IntersectsCircle(Vector2 posA, float rotA,
                                                    CircleShape circle, Vector2 posB)
    {
        // posA = this AABB, posB = circle centre
        var result = circle.IntersectsAABB(posB, 0, this, posA);
        return result?.Flipped();
    }

    internal override ContactInfo? IntersectsAABB(Vector2 posA, float rotA,
                                                  AABBShape other, Vector2 posB)
    {
        // posA = this, posB = other
        float overlapX = (HalfWidth  + other.HalfWidth)  - MathF.Abs(posA.X - posB.X);
        float overlapY = (HalfHeight + other.HalfHeight) - MathF.Abs(posA.Y - posB.Y);

        if (overlapX <= 0 || overlapY <= 0) return null;

        Vector2 normal;
        float   depth;
        if (overlapX < overlapY)
        {
            normal = posA.X < posB.X ? -Vector2.UnitX : Vector2.UnitX;
            depth  = overlapX;
        }
        else
        {
            normal = posA.Y < posB.Y ? -Vector2.UnitY : Vector2.UnitY;
            depth  = overlapY;
        }

        Vector2 contact = posA - normal * (depth * 0.5f);
        return new ContactInfo(normal, depth, contact);
    }

    internal override ContactInfo? IntersectsPolygon(Vector2 posA, float rotA,
                                                     PolygonShape polygon,
                                                     Vector2 posB, float rotB)
    {
        // Treat AABB as a polygon (4 vertices) and use SAT.
        var asPolygon = ToPolygon(posA);
        var result    = asPolygon.IntersectsPolygon(posA, 0f, polygon, posB, rotB);
        return result;
    }

    internal PolygonShape ToPolygon(Vector2 pos)
    {
        // Returns an untransformed polygon; caller must add pos themselves
        // via the position argument to Intersects.
        return new PolygonShape(new[]
        {
            new Vector2(-HalfWidth, -HalfHeight),
            new Vector2( HalfWidth, -HalfHeight),
            new Vector2( HalfWidth,  HalfHeight),
            new Vector2(-HalfWidth,  HalfHeight),
        });
    }
}
