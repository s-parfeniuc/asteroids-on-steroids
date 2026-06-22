using System.Numerics;

namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Visual-only polygon representation, independent of the collision shape.
/// Used for entities whose rendered surface differs from their physics hitbox —
/// primarily asteroids that have absorbed sub-threshold hits and show blast craters.
///
/// Each entry in ConvexPieces is a centroid-relative convex polygon (SH output).
/// Gaps between pieces are rendered as craters. The renderer draws all pieces;
/// the collision system ignores this component entirely.
/// </summary>
public struct VisualMesh
{
    public Vector2[][] ConvexPieces;
}
