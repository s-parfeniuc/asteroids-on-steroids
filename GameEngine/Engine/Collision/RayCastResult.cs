using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Result of a shape-level raycast. Carries no entity — the world-level query
/// (PhysicsQueries.Raycast) attaches the entity.
/// </summary>
public readonly struct RayCastResult
{
    public readonly float   Distance;   // along the ray, in [0, maxDist]
    public readonly Vector2 Point;      // world-space hit point on the surface
    public readonly Vector2 Normal;     // outward unit surface normal at the hit
    public readonly int     PartIndex;  // CompoundShape part struck; -1 for simple shapes

    public RayCastResult(float distance, Vector2 point, Vector2 normal, int partIndex = -1)
    {
        Distance  = distance;
        Point     = point;
        Normal    = normal;
        PartIndex = partIndex;
    }
}
