using System.Numerics;

namespace AsteroidsEngine.Engine.Components;

/// <summary>Position and orientation in world space.</summary>
public struct Transform
{
    public Vector2 Position;
    public float   Rotation;  // radians; 0 = pointing right; increases clockwise

    // Pose at the start of the current fixed step, captured by PreviousStateSystem.
    // The renderer interpolates between these and the current pose using the
    // fixed-step Alpha so motion is smooth independent of the simulation rate.
    public Vector2 PreviousPosition;
    public float   PreviousRotation;
}
