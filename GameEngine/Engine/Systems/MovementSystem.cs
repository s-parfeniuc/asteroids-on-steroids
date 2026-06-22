using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>
/// Integrates Transform from Velocity each frame.
/// Simple Euler: position += linear * dt, rotation += angular * dt.
/// Does not apply forces — that is PhysicsSystem's job.
/// </summary>
public sealed class MovementSystem : ISystem
{
    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        world.ForEach<Transform, Velocity>((Entity _, ref Transform t, ref Velocity v) =>
        {
            t.Position += v.Linear  * fdt;
            t.Rotation += v.Angular * fdt;
        });
    }
}
