using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>
/// Snapshots each Transform's pose into its Previous* fields at the start of every
/// fixed step, before movement/physics mutate the current pose. The renderer then
/// interpolates between Previous* and current using the fixed-step Alpha for smooth
/// motion. Register this FIRST in the system order.
/// </summary>
public sealed class PreviousStateSystem : ISystem
{
    public void Update(World world, double dt)
    {
        world.ForEach<Transform>((Entity _, ref Transform t) =>
        {
            t.PreviousPosition = t.Position;
            t.PreviousRotation = t.Rotation;
        });
    }
}
