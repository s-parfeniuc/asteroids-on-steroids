using System;
using System.Diagnostics;
using System.Globalization;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Systems;
using AsteroidsSim.Spikes;
using SysVec2 = System.Numerics.Vector2;

namespace AsteroidsSim.Tools.SpikePhysicsBaseline;

/// <summary>
/// Spike B, third contender: the <b>existing</b> engine's solver, on the identical scenario.
/// </summary>
/// <remarks>
/// <para>Godot DEFAULT and Rapier2D were measured first, and both missed the budget. That establishes
/// "no off-the-shelf backend clears the bar" — it does <i>not</i> establish that ours would. Without
/// this number, "port our own solver" is an assumption rather than a decision.</para>
///
/// <para>Runs the real <c>BroadPhaseSystem</c> → <c>CollisionSystem</c> → <c>PhysicsSystem</c> →
/// <c>MovementSystem</c> pipeline from <c>src/Engine</c>, unmodified, on the same
/// <see cref="PhysicsScenario"/> the Godot spikes use.</para>
///
/// <para><b>Read this as a floor, not a fair fight.</b> The current solver rebuilds its
/// <c>SpatialGrid</c> from scratch every frame (a <c>Dictionary&lt;long, List&lt;Entity&gt;&gt;</c>,
/// ~18k dictionary operations per frame at 2k bodies) and <c>CompoundShape</c> is roughly 201 heap
/// objects per 100-cell asteroid. The Phase 2 rewrite — SoA layout, persistent broad phase, frozen
/// sleeping proxies — targets all of that. If the *unoptimised* version is already competitive, the
/// optimised one is clearly the right call.</para>
///
/// <para>Headless, no Godot. Env vars: <c>AOS_ARENA_SCALE</c> (default 1.0), <c>AOS_TICKS</c>.</para>
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        float arenaScale = EnvFloat("AOS_ARENA_SCALE", 1.0f);
        int ticks = (int)EnvFloat("AOS_TICKS", PhysicsScenario.TickCount);

        var spec = PhysicsScenario.Build(20260814, arenaScale);
        int shapes = PhysicsScenario.TotalShapes(spec);
        float coverage = PhysicsScenario.ArealCoverage(spec, arenaScale) * 100f;

        Console.WriteLine($"Spike B baseline — existing src/Engine solver");
        Console.WriteLine($"{spec.Length:N0} bodies, {shapes:N0} shapes, arena x{arenaScale:F1}, coverage {coverage:F0}%.");

        var world = new World();
        var bus = new EventBus();
        var grid = new SpatialGrid(160f);

        var broad = new BroadPhaseSystem(grid);
        var collision = new CollisionSystem(grid, bus);
        var physics = new PhysicsSystem();
        var movement = new MovementSystem();

        var spawnWatch = Stopwatch.StartNew();
        foreach (var b in spec)
        {
            Entity e = world.CreateEntity();

            world.AddComponent(e, new Transform
            {
                Position = new SysVec2(b.Position.X, b.Position.Y),
                Rotation = b.Rotation,
                PreviousPosition = new SysVec2(b.Position.X, b.Position.Y),
                PreviousRotation = b.Rotation,
            });

            world.AddComponent(e, new Velocity
            {
                Linear = new SysVec2(b.LinearVelocity.X, b.LinearVelocity.Y),
                Angular = b.AngularVelocity,
            });

            world.AddComponent(e, new RigidBody
            {
                Mass = b.Mass,
                Inertia = b.Inertia,
                LinearDrag = 0f,
                AngularDrag = 0f,
                Restitution = 0.3f,
                Friction = 0.2f,
            });

            // One convex part per cell — the CompoundShape path, exactly as an
            // asteroid is built today.
            var parts = new CollisionShape[b.Shapes.Length];
            for (int s = 0; s < b.Shapes.Length; s++)
            {
                var poly = b.Shapes[s];
                var verts = new SysVec2[poly.Length];
                for (int v = 0; v < poly.Length; v++) verts[v] = new SysVec2(poly[v].X, poly[v].Y);
                parts[s] = new PolygonShape(verts);
            }

            world.AddComponent(e, new Collider
            {
                Shape = parts.Length == 1 ? parts[0] : new CompoundShape(parts),
                Layer = 1,
                Mask = 1,
            });
        }
        spawnWatch.Stop();
        Console.WriteLine($"Spawn took {spawnWatch.Elapsed.TotalSeconds:F2} s " +
                          $"({spawnWatch.Elapsed.TotalMilliseconds / spec.Length:F3} ms/body).");

        const double dt = 1.0 / 60.0;
        var tickMs = new double[ticks];
        var wall = Stopwatch.StartNew();
        double freqMs = 1000.0 / Stopwatch.Frequency;

        for (int t = 0; t < ticks; t++)
        {
            long t0 = Stopwatch.GetTimestamp();

            physics.Update(world, dt);
            movement.Update(world, dt);
            broad.Update(world, dt);      // must run after movement, before consumers
            collision.Update(world, dt);
            bus.Flush();
            world.FlushDeferred();

            tickMs[t] = (Stopwatch.GetTimestamp() - t0) * freqMs;

            if ((t + 1) % 60 == 0)
                Console.WriteLine($"  tick {t + 1}/{ticks}  last {tickMs[t]:F2} ms  " +
                                  $"wall {wall.Elapsed.TotalSeconds:F1} s");
        }
        wall.Stop();

        var sorted = (double[])tickMs.Clone();
        Array.Sort(sorted);

        Console.WriteLine();
        Console.WriteLine("──────── Spike B baseline result ────────");
        Console.WriteLine($"backend        src/Engine (existing, UNOPTIMISED)");
        Console.WriteLine($"arena scale    x{arenaScale:F1}  (coverage {coverage:F0}%)");
        Console.WriteLine($"bodies         {spec.Length:N0}");
        Console.WriteLine($"shapes         {shapes:N0}");
        Console.WriteLine($"ticks          {ticks}");
        Console.WriteLine($"wall clock     {wall.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"median tick    {sorted[sorted.Length / 2]:F3} ms");
        Console.WriteLine($"p99 tick       {sorted[(int)(sorted.Length * 0.99)]:F3} ms");
        Console.WriteLine($"worst tick     {sorted[^1]:F3} ms");
        Console.WriteLine($"budget @60Hz   16.667 ms  (physics share target: ~8 ms)");
        return 0;
    }

    private static float EnvFloat(string name, float fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(v) &&
               float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f > 0
            ? f : fallback;
    }
}
