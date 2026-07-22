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

    /// <summary>Index of the touching part (cell) on A / B when that side is a CompoundShape;
    /// -1 when unknown. Lets fracture seed the cell that was actually struck instead of guessing
    /// from the contact point (which is only an approximation of the surface).</summary>
    public readonly int PartA;
    public readonly int PartB;

    public ContactInfo(Vector2 normal, float depth, Vector2 contactPoint, int partA = -1, int partB = -1)
    {
        Normal       = normal;
        Depth        = depth;
        ContactPoint = contactPoint;
        PartA        = partA;
        PartB        = partB;
    }

    /// <summary>Same contact with the normal reversed. Part indices are unaffected — flipping
    /// only re-orients the normal, it does not exchange the two bodies.</summary>
    public ContactInfo Flipped() => new(-Normal, Depth, ContactPoint, PartA, PartB);

    /// <summary>Same contact with the A/B part indices exchanged — used when the manifold was
    /// collected with the roles reversed (B's cells as the reference body).</summary>
    public ContactInfo SwappedParts() => new(Normal, Depth, ContactPoint, PartB, PartA);

    public ContactInfo WithParts(int partA, int partB) => new(Normal, Depth, ContactPoint, partA, partB);
}
