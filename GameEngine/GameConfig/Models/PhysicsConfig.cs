namespace AsteroidsGame.Config;

/// <summary>Default physics values applied to asteroid rigid bodies at spawn.</summary>
public class PhysicsConfig
{
    public float LinearDrag  { get; set; } = 0.05f;
    public float AngularDrag { get; set; } = 0.05f;
    public float Restitution { get; set; } = 0.30f;
    public float Friction    { get; set; } = 0.20f;
}
