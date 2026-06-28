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
    public static void Spawn(World world, GameContext ctx, Random rng, Vector2 pos, string typeKey)
    {
        if (!ctx.Config.Entities.TryGetValue(typeKey, out var ec)) return;
        if (!ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return;
        var mat = ctx.Config.ResolveMaterial(ec.Material, sd);   // shape owns it; config overrides

        float sc      = ec.ShapeScale;
        var outline   = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos   = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMult  = sd.Seeds.Select(s => s.BondMult).ToList();
        var body      = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, rng);
        FractureBodyFactory.ApplyShapeSeeds(body, sd.Seeds, sc);

        var wc = ctx.Config.World;
        Vector2 worldCenter = new(wc.Width / 2f, wc.Height / 2f);
        Vector2 toCenter    = worldCenter - pos;
        float baseAngle     = MathF.Atan2(toCenter.Y, toCenter.X);
        float spread        = ((float)rng.NextDouble() * 2f - 1f) * (MathF.PI / 6f);
        float speed         = ec.Speed * (0.7f + 0.6f * (float)rng.NextDouble());
        Vector2 vel         = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread)) * speed;

        var color = new BodyColor { Fill = new Color(80, 50, 120), Outline = new Color(160, 100, 220) };
        var e     = FractureBodyFactory.SpawnFromDensity(world, ctx.Config.Physics, body, pos,
                        (float)(rng.NextDouble() * MathF.Tau), vel, 0f, color);
        world.AddComponent(e, new AlienTag());
        world.AddComponent(e, new AlienVariant { Key = typeKey });
        world.AddComponent(e, new ShootCooldown { Remaining = (float)rng.NextDouble() * ec.ShootCooldown });
        world.AddComponent(e, new VortexResponse { CentripetalMult = 0.15f, TangentialMult = 0.08f });
        // Alien layer so player bullets can target aliens but asteroids don't fracture them.
        if (world.HasComponent<Collider>(e))
        {
            ref var col = ref world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
        ctx.CellBudget.Add(body.Cells.Length);
    }
}
