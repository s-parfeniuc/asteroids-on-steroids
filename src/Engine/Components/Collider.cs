using AsteroidsEngine.Engine.Collision;

namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Attaches a collision shape to an entity.
/// Layer: what this entity IS (bitmask).
/// Mask:  what this entity checks against (bitmask).
/// A collision pair (A, B) is tested only if (A.Mask & B.Layer) != 0.
/// </summary>
public struct Collider
{
    public CollisionShape Shape;
    public int            Layer;
    public int            Mask;
    /// <summary>Sensor/trigger: the pair is still detected and a CollisionEvent is published, but
    /// no overlap-separation or velocity impulse is applied (the body passes through). Used by the
    /// piercing round so it penetrates instead of being arrested at the surface.</summary>
    public bool           Sensor;
}
