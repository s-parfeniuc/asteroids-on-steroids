using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Result of a successful narrow-phase collision test.
/// Normal points from B into A (the direction A must move to separate).
/// Depth is the overlap distance along the normal.
/// </summary>
public readonly struct ContactInfo
{
    public readonly Vector2 Normal;        // unit vector; points from B toward A
    public readonly float   Depth;         // penetration depth (> 0 means overlapping)
    public readonly Vector2 ContactPoint;  // approximate world-space contact point

    public ContactInfo(Vector2 normal, float depth, Vector2 contactPoint)
    {
        Normal       = normal;
        Depth        = depth;
        ContactPoint = contactPoint;
    }

    /// <summary>Returns the same contact but with Normal flipped (swap A and B roles).</summary>
    public ContactInfo Flipped() => new(-Normal, Depth, ContactPoint);
}
