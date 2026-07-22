using System.Numerics;

namespace AsteroidsEngine.Engine.Components;

/// <summary>Linear and angular velocity. Consumed by MovementSystem.</summary>
public struct Velocity
{
    public Vector2 Linear;   // units per second
    public float   Angular;  // radians per second (clockwise positive)
}
