using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Builds the common component skeleton for a fracturable body entity — Transform,
/// Velocity, RigidBody (from the physics config), Collider (asteroid or ghost layer),
/// the body itself, its colour, and the render outline. Mass/inertia are supplied by
/// the caller so each site keeps its own mass strategy; tags (AsteroidTag, VortexResponse,
/// player/mothership state) are added by the caller afterwards.
/// </summary>
public static class FractureBodyFactory
{
    /// <summary>Spawns a body whose mass/inertia are derived from its material density ×
    /// area — the strategy used for asteroids, aliens, the player, and piercing rounds.</summary>
    public static Entity SpawnFromDensity(
        World world, PhysicsConfig ph, FracturableBody body,
        Vector2 pos, float rot, Vector2 vel, float spin,
        BodyColor color, bool ghost = false, float ghostRemaining = 0.04f)
    {
        // TotalMass + ComputeInertia honour per-cell DensityMult (dense cores weigh more).
        float mass    = VoronoiTessellator.TotalMass(body);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);
        return Spawn(world, ph, body, pos, rot, vel, spin, mass, inertia, color, ghost, ghostRemaining);
    }

    public static Entity Spawn(
        World world, PhysicsConfig ph, FracturableBody body,
        Vector2 pos, float rot, Vector2 vel, float spin, float mass, float inertia,
        BodyColor color, bool ghost = false, float ghostRemaining = 0.04f)
    {
        var e = world.CreateEntity();
        world.AddComponent(e, new Transform
            { Position = pos, Rotation = rot, PreviousPosition = pos, PreviousRotation = rot });
        world.AddComponent(e, new Velocity { Linear = vel, Angular = spin });
        world.AddComponent(e, new RigidBody
        {
            Mass        = mass,
            Inertia     = inertia,
            LinearDrag  = ph.LinearDrag,
            AngularDrag = ph.AngularDrag,
            Restitution = ph.Restitution,
            Friction    = ph.Friction,
        });
        world.AddComponent(e, new Collider
        {
            Shape = VoronoiTessellator.BuildShape(body),
            Layer = ghost ? GameLayers.Ghost : GameLayers.Asteroid,
            Mask  = ghost ? 0 : (GameLayers.Asteroid | GameLayers.Player),
        });
        world.AddComponent(e, body);
        world.AddComponent(e, color);
        var (outline, cracks) = FractureMesh.ComputeEdges(body.Cells, body.Bonds);
        world.AddComponent(e, new RenderOutline { Outline = outline, Cracks = cracks });
        if (ghost) world.AddComponent(e, new FractureGhost { Remaining = ghostRemaining });
        return e;
    }

    /// <summary>Matches each Voronoi cell to the nearest seed (by centroid proximity) and
    /// applies that seed's role, densityMult, and blastResist to the cell. Used by the
    /// player, alien, and mothership builds in both the game and the demo.</summary>
    public static void ApplyShapeSeeds(FracturableBody body, SeedData[] seeds, float scale)
    {
        for (int ci = 0; ci < body.Cells.Length; ci++)
        {
            float bestSq = float.MaxValue;
            int   bestI  = 0;
            for (int si = 0; si < seeds.Length; si++)
            {
                var sp = new Vector2(seeds[si].X * scale, seeds[si].Y * scale);
                float dsq = (body.Cells[ci].Centroid - sp).LengthSquared();
                if (dsq < bestSq) { bestSq = dsq; bestI = si; }
            }
            body.Cells[ci].Role        = seeds[bestI].Role;
            body.Cells[ci].DensityMult = seeds[bestI].DensityMult;
        }
    }
}
