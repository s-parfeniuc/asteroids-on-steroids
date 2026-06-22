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
}
