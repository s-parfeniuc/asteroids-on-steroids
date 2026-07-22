using System;
using System.Linq;
using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;

namespace AsteroidsGame.Gameplay;

/// <summary>Spawns the mothership boss and attaches its per-fragment <see cref="BossBrain"/>. Shared by
/// the game and the demo so both get the identical boss. Skills/movement are driven by BossSystem.</summary>
public static class MothershipPrefab
{
    public static Entity Spawn(World world, GameContext ctx, Random rng, Vector2 pos, Vector2 vel, int groupId,
                               out int initialCockpits)
    {
        initialCockpits = 1;
        if (!ctx.Config.Entities.TryGetValue("mothership", out var ec)) return default;
        if (!ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return default;
        var mat = ctx.Config.ResolveMaterial(ec.Material, sd);

        float sc    = ec.ShapeScale;
        var outline = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMlt = sd.Seeds.Select(s => s.BondMult).ToList();
        var body    = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMlt, mat, rng);
        FractureBodyFactory.ApplyShapeSeeds(body, sd.Seeds, sc);

        initialCockpits = Math.Max(1, sd.Seeds.Count(s => s.Role == "cockpit"));

        var color = new BodyColor { Fill = new Color(90, 30, 130), Outline = new Color(180, 60, 240) };
        var e = FractureBodyFactory.SpawnFromDensity(world, ctx.Config.Physics, body, pos,
            (float)(rng.NextDouble() * MathF.Tau), vel, 0f, color);

        world.AddComponent(e, new AlienTag());
        world.AddComponent(e, new AlienVariant { Key = "mothership" });
        world.AddComponent(e, new ShootCooldown { Remaining = 999f });
        world.AddComponent(e, new MothershpId { Id = groupId, InitialCockpitCount = initialCockpits });
        world.AddComponent(e, new VortexResponse { CentripetalMult = 1f, TangentialMult = 1f });
        AttachBossBrain(world, e, ctx);
        if (world.HasComponent<Collider>(e))
        {
            ref var col = ref world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
        ctx.CellBudget.Add(body.Cells.Length);
        return e;
    }

    /// <summary>Attaches a fresh BossBrain to a cockpit-bearing fragment, counting its skill/spawner
    /// cells (for cooldown/spawn-rate weakening) and seeding the first-cast delays. No-op if the boss
    /// config or body is missing.</summary>
    public static void AttachBossBrain(World world, Entity e, GameContext ctx)
    {
        if (!ctx.Config.Entities.TryGetValue("mothership", out var ec) || ec.Boss is not { } bc) return;
        if (!world.HasComponent<FracturableBody>(e)) return;
        ref var fb = ref world.GetComponent<FracturableBody>(e);
        int skillCells = 0, spawnerCells = 0;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (fb.Cells[i].Role == "skill") skillCells++;
            else if (fb.Cells[i].Role == "spawner") spawnerCells++;
        }
        world.AddComponent(e, new BossBrain
        {
            ShockwaveCd     = bc.ShockwaveCooldown,
            BlackHoleCd     = bc.BlackHoleCooldown * bc.BlackHoleInitialDelay,
            RamCd           = bc.RamChargeCooldown * bc.RamChargeInitialDelay,
            BarrageCd       = bc.BarrageCooldown  * bc.BarrageInitialDelay,
            SpawnCd         = bc.SpawnInterval,
            MaxSkillCells   = skillCells,
            MaxSpawnerCells = spawnerCells,
        });
    }
}
