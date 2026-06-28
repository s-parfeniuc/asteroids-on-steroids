using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;

namespace AsteroidsGame.Gameplay;

/// <summary>Spawns a piercing round — a fracturable "metal" body that drives through
/// asteroids, lateral-clamped to stay on-axis. Shared by the game and the demo.</summary>
public static class PiercingPrefab
{
    public static void Spawn(World world, GameContext ctx, Vector2 from, Vector2 aimDir, Random rng)
    {
        if (!ctx.Config.Weapons.TryGetValue("piercing", out var wcfg)) return;
        if (!ctx.Shapes.TryGetValue("piercing_round", out var sd)) return;
        var mat = ctx.Config.ResolveMaterial(null, sd);   // material from the shape (no hardcode)

        var sc = wcfg.ShapeScale ?? 1f;
        var outline = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMlt = sd.Seeds.Select(s => s.BondMult).ToList();
        var body = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMlt, mat, rng);

        float rot = MathF.Atan2(aimDir.Y, aimDir.X) + MathF.PI * 0.5f;
        Vector2 pos = from + aimDir * 40f;
        float speed = wcfg.ProjectileSpeed;
        float clamp = wcfg.LateralImpulseClamp ?? 0.4f;

        float area = VoronoiTessellator.TotalArea(body);
        float mass = MathF.Max(1f, mat.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);

        var e = FractureBodyFactory.Spawn(world, ctx.Config.Physics, body, pos, rot,
            aimDir * speed, 0f, mass, inertia,
            new BodyColor { Fill = new Color(58, 60, 66), Outline = new Color(120, 124, 132) });
        // The round spawns inside the firing ship; ignore the player layer just long enough
        // (PlayerGrace) for the round to clear the ship's shape, then collide with it normally.
        // 170px round at ProjectileSpeed clears in ~0.07s; 0.15s leaves margin.
        world.AddComponent(e, new PiercingRoundTag { Direction = aimDir, LateralClamp = clamp, PlayerGrace = 0.15f });
        world.AddComponent(e, new TimeToLive { Remaining = wcfg.TimeToLive });
        if (world.HasComponent<Collider>(e))
        {
            ref var col = ref world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Bullet;
            col.Mask = GameLayers.Asteroid | GameLayers.Alien;   // PlayerGrace adds Player back once clear
        }
        // High angular drag prevents uncontrolled spin after glancing impacts.
        if (world.HasComponent<RigidBody>(e))
        {
            ref var rb = ref world.GetComponent<RigidBody>(e);
            rb.AngularDrag = 4f;
        }
    }
}
