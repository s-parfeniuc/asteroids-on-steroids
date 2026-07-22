using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;

namespace AsteroidsGame.Gameplay;

/// <summary>Builds and spawns an alien ship (drone/bruiser) from its entity config —
/// shared by the game and the demo so both spawn identical aliens.</summary>
public static class AlienPrefab
{
    /// <summary><paramref name="aimDir"/> overrides the default inward-±30° entry direction (wave
    /// spawn patterns aim their bodies); <paramref name="speedMult"/> scales the entry speed.
    /// Returns the spawned entity (default if the type/shape was invalid).</summary>
    public static Entity Spawn(World world, GameContext ctx, Random rng, Vector2 pos, string typeKey,
                               Vector2? aimDir = null, float speedMult = 1f)
    {
        if (!ctx.Config.Entities.TryGetValue(typeKey, out var ec)) return default;
        if (!ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return default;
        var mat = ctx.Config.ResolveMaterial(ec.Material, sd);   // shape owns it; config overrides

        float sc      = ec.ShapeScale;
        var outline   = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos   = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMult  = sd.Seeds.Select(s => s.BondMult).ToList();
        var body      = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, rng);
        FractureBodyFactory.ApplyShapeSeeds(body, sd.Seeds, sc);

        float speed = ec.Speed * (0.7f + 0.6f * (float)rng.NextDouble()) * speedMult;
        Vector2 dir;
        if (aimDir is { } ad && ad.LengthSquared() > 1e-6f)
        {
            dir = Vector2.Normalize(ad);
        }
        else
        {
            var wc = ctx.Config.World;
            Vector2 toCenter  = new Vector2(wc.Width / 2f, wc.Height / 2f) - pos;
            float baseAngle   = MathF.Atan2(toCenter.Y, toCenter.X);
            float spread      = ((float)rng.NextDouble() * 2f - 1f) * (MathF.PI / 6f);
            dir = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread));
        }
        Vector2 vel = dir * speed;

        // Faction palette so alien type reads at a glance (role tints layer on top per cell).
        var color = typeKey switch
        {
            "drone"      => new BodyColor { Fill = new Color(40, 120, 120),  Outline = new Color(90, 210, 205) },  // teal
            "bruiser"    => new BodyColor { Fill = new Color(135, 55, 40),   Outline = new Color(225, 110, 70) },  // red-orange
            "mothership" => new BodyColor { Fill = new Color(75, 45, 115),   Outline = new Color(155, 100, 215) }, // deep purple
            _            => new BodyColor { Fill = new Color(80, 50, 120),   Outline = new Color(160, 100, 220) },
        };
        var e     = FractureBodyFactory.SpawnFromDensity(world, ctx.Config.Physics, body, pos,
                        (float)(rng.NextDouble() * MathF.Tau), vel, 0f, color);
        world.AddComponent(e, new AlienTag());
        world.AddComponent(e, new AlienVariant { Key = typeKey });
        world.AddComponent(e, new ShootCooldown { Remaining = (float)rng.NextDouble() * ec.ShootCooldown });
        if (ec.Dash is not null)
            world.AddComponent(e, new AlienSkillState { DashCd = ec.Dash.Cooldown }); // start on cooldown
        world.AddComponent(e, new VortexResponse { CentripetalMult = 0.15f, TangentialMult = 0.08f });
        // Alien layer so player bullets can target aliens but asteroids don't fracture them.
        if (world.HasComponent<Collider>(e))
        {
            ref var col = ref world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
        ctx.CellBudget.Add(body.Cells.Length);
        return e;
    }
}
