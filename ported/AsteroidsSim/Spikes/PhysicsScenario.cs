using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Spikes;

/// <summary>
/// The Spike B scenario, generated deterministically so every contender measures the same world.
/// </summary>
/// <remarks>
/// <para>PHASE0.md Spike B: 2,000 mixed bodies in a bounded arena — 600 compound bodies of ~30 convex
/// shapes each plus 1,400 single-shape bodies, seeded velocities, no gravity, 600 ticks at 60 Hz.</para>
///
/// <para>Lives in <c>AsteroidsSim</c> rather than beside either contender so that the Godot/Rapier
/// harness and the headless baseline harness are provably running the same input. It is engine-free,
/// so the baseline can run without Godot installed.</para>
///
/// <para>Shape counts and sizes are drawn from <see cref="DetRng"/>, so the scenario is reproducible
/// across machines and runs — which also makes it usable as a cross-platform determinism check for
/// whichever physics backend is chosen.</para>
/// </remarks>
public static class PhysicsScenario
{
    public const int CompoundBodies = 600;
    public const int SimpleBodies = 1_400;
    public const int TotalBodies = CompoundBodies + SimpleBodies;
    public const int ShapesPerCompound = 30;

    public const float ArenaWidth = 8_000f;
    public const float ArenaHeight = 4_500f;
    public const float FixedDt = 1f / 60f;
    public const int TickCount = 600;

    public readonly struct BodySpec
    {
        /// <summary>Convex polygons in body-local space; one per cell.</summary>
        public readonly Vec2[][] Shapes;
        public readonly Vec2 Position;
        public readonly float Rotation;
        public readonly Vec2 LinearVelocity;
        public readonly float AngularVelocity;
        public readonly float Mass;
        public readonly float Inertia;

        public BodySpec(Vec2[][] shapes, Vec2 position, float rotation,
                        Vec2 linearVelocity, float angularVelocity, float mass, float inertia)
        {
            Shapes = shapes;
            Position = position;
            Rotation = rotation;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            Mass = mass;
            Inertia = inertia;
        }

        public bool IsCompound => Shapes.Length > 1;
    }

    /// <summary>Build the scenario. Identical output for identical <paramref name="seed"/>, anywhere.</summary>
    /// <param name="arenaScale">
    /// Linear scale on the arena. 1.0 gives ~82% areal coverage — a deliberate worst case where
    /// almost everything overlaps. Larger values give representative densities.
    /// </param>
    public static BodySpec[] Build(ulong seed = 20260814, float arenaScale = 1.0f)
    {
        float aw = ArenaWidth * arenaScale, ah = ArenaHeight * arenaScale;
        var rng = DetRng.FromSeed(seed);
        var bodies = new BodySpec[TotalBodies];

        for (int i = 0; i < TotalBodies; i++)
        {
            bool compound = i < CompoundBodies;
            int shapeCount = compound ? ShapesPerCompound : 1;
            float bodyRadius = compound
                ? rng.NextFloat(RngStream.Tessellation, 90f, 160f)
                : rng.NextFloat(RngStream.Tessellation, 8f, 22f);

            var shapes = new Vec2[shapeCount][];
            float area = 0f;

            for (int s = 0; s < shapeCount; s++)
            {
                // Cell centres scattered inside the body disc; each cell a small convex hexagon.
                Vec2 centre = Vec2.Zero;
                if (compound)
                {
                    float ang = rng.NextFloat(RngStream.Tessellation, 0f, SimMath.TwoPI);
                    float rad = bodyRadius * SimMath.Sqrt(rng.NextFloat(RngStream.Tessellation));
                    SimMath.SinCos(ang, out float sa, out float ca);
                    centre = new Vec2(ca * rad, sa * rad);
                }

                float cellR = compound
                    ? rng.NextFloat(RngStream.Tessellation, 14f, 26f)
                    : bodyRadius;

                const int Verts = 6;
                var poly = new Vec2[Verts];
                for (int v = 0; v < Verts; v++)
                {
                    float a = v * (SimMath.TwoPI / Verts);
                    float r = cellR * rng.NextFloat(RngStream.Tessellation, 0.85f, 1.15f);
                    SimMath.SinCos(a, out float sv, out float cv);
                    poly[v] = new Vec2(centre.X + cv * r, centre.Y + sv * r);
                }
                shapes[s] = poly;
                area += PolygonArea(poly);
            }

            float mass = MathF.Max(1f, area * 0.01f);

            bodies[i] = new BodySpec(
                shapes,
                new Vec2(
                    rng.NextFloat(RngStream.Waves, bodyRadius, aw - bodyRadius),
                    rng.NextFloat(RngStream.Waves, bodyRadius, ah - bodyRadius)),
                rng.NextFloat(RngStream.Waves, 0f, SimMath.TwoPI),
                new Vec2(
                    rng.NextFloat(RngStream.Waves, -120f, 120f),
                    rng.NextFloat(RngStream.Waves, -120f, 120f)),
                rng.NextFloat(RngStream.Waves, -1.5f, 1.5f),
                mass,
                // Disc approximation is fine — both contenders use the same value.
                mass * bodyRadius * bodyRadius * 0.5f);
        }

        return bodies;
    }

    /// <summary>Shoelace area, absolute value.</summary>
    public static float PolygonArea(Vec2[] poly)
    {
        float a = 0f;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            a += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
        return MathF.Abs(a) * 0.5f;
    }

    /// <summary>Fraction of the arena covered by body discs — the density that drives contact count.</summary>
    public static float ArealCoverage(BodySpec[] bodies, float arenaScale)
    {
        float total = 0f;
        foreach (var b in bodies)
        {
            float area = 0f;
            foreach (var poly in b.Shapes) area += PolygonArea(poly);
            total += area;
        }
        return total / (ArenaWidth * arenaScale * ArenaHeight * arenaScale);
    }

    /// <summary>Total shape count — the number the narrow phase actually sees.</summary>
    public static int TotalShapes(BodySpec[] bodies)
    {
        int n = 0;
        foreach (var b in bodies) n += b.Shapes.Length;
        return n;
    }
}
