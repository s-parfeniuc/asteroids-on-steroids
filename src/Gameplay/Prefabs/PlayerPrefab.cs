using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Spawns the player ship — identical in the game and the demo so both share the same
/// loadout (weapon/skill components), the same fracturable body with seed roles
/// (cockpit/cannon/propeller), drag, and colour. The shared PlayerControlSystem gates
/// firing on weapon-role cells and skills on propeller-role cells, so the roles MUST be
/// applied here (this was the cause of the demo being unable to shoot).
/// </summary>
public static class PlayerPrefab
{
    private static readonly BodyColor ShipColor =
        new() { Fill = new Color(60, 120, 200), Outline = new Color(140, 190, 255) };

    public static Entity Spawn(World world, GameContext ctx, Random rng)
    {
        var wc  = ctx.Config.World;
        var pos = new Vector2(wc.Width / 2f, wc.Height / 2f);

        var player = world.CreateEntity();
        world.AddComponent(player, new Transform { Position = pos, PreviousPosition = pos });
        world.AddComponent(player, new Velocity());
        world.AddComponent(player, new AimComponent { Dir = -Vector2.UnitY });
        world.AddComponent(player, new WeaponCooldowns());
        world.AddComponent(player, new ActiveWeapon { Key = ctx.Config.Player.StartingWeapon });
        world.AddComponent(player, new SkillState());
        world.AddComponent(player, new PlayerTag());
        world.AddComponent(player, new VortexResponse
        {
            CentripetalMult = ctx.Config.Player.VortexCentripetal,
            TangentialMult  = ctx.Config.Player.VortexTangential,
        });

        var pc = ctx.Config.Player;
        if (ctx.Shapes.TryGetValue(pc.Shape, out var sd) && sd.Seeds.Length >= 1 && sd.Outline.Length >= 3)
            AttachBody(world, player, ctx, sd, rng);
        else
        {
            world.AddComponent(player, new RigidBody
            {
                Mass = 12f, Inertia = 0f, LinearDrag = 1.2f, AngularDrag = 2f,
                Restitution = 0.2f, Friction = 0.1f,
            });
            world.AddComponent(player, new Collider
            {
                Shape = new CircleShape(18f),
                Layer = GameLayers.Player,
                Mask  = GameLayers.Asteroid | GameLayers.Alien,
            });
        }
        return player;
    }

    private static void AttachBody(World world, Entity player, GameContext ctx, ShapeData sd, Random rng)
    {
        var pc = ctx.Config.Player;
        var mat = ctx.Config.ResolveMaterial(pc.Material, sd);   // shape owns it; config overrides

        float sc = pc.ShapeScale;
        var outline  = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos  = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMult = sd.Seeds.Select(s => s.BondMult).ToList();

        var body = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, rng);
        FractureBodyFactory.ApplyShapeSeeds(body, sd.Seeds, sc);

        float area    = VoronoiTessellator.TotalArea(body);
        float mass    = MathF.Max(1f, mat.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);

        world.AddComponent(player, new RigidBody
        {
            Mass = mass, Inertia = inertia, LinearDrag = 1.2f, AngularDrag = 2f,
            Restitution = 0.2f, Friction = 0.1f,
        });
        world.AddComponent(player, new Collider
        {
            Shape = VoronoiTessellator.BuildShape(body),
            Layer = GameLayers.Player,
            Mask  = GameLayers.Asteroid | GameLayers.Alien,
        });
        CellColorizer.Apply(body, ShipColor);   // bake per-cell colours (this path bypasses the factory)
        world.AddComponent(player, body);
        world.AddComponent(player, ShipColor);
        var (outlineSeg, cracks) = FractureMesh.ComputeEdges(body.Cells, body.Bonds);
        world.AddComponent(player, new RenderOutline { Outline = outlineSeg, Cracks = cracks });
    }
}
