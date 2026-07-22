using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>
/// Rebuilds the shared broad-phase spatial index once per frame from every Transform+Collider
/// entity's AABB. Must run AFTER movement (so positions are current) and BEFORE any consumer —
/// <see cref="RaycastBulletSystem"/>'s ray queries and <see cref="CollisionSystem"/>'s pair
/// candidates both read the same freshly-built index, so the world is indexed exactly once.
/// </summary>
public sealed class BroadPhaseSystem : ISystem
{
    private readonly ISpatialIndex _index;

    public BroadPhaseSystem(ISpatialIndex index) => _index = index;

    public void Update(World world, double dt)
    {
        _index.Clear();
        world.ForEach<Transform, Collider>((Entity e, ref Transform t, ref Collider c) =>
        {
            if (world.HasComponent<DisabledTag>(e)) return;
            var (min, max) = c.Shape.GetAABB(t.Position, t.Rotation);
            _index.Insert(e, min, max);
        });
    }
}
