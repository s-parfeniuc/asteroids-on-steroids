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
/// Builds and spawns a procedural asteroid from its config — shared by the game and the
/// demo so both produce identical asteroids (geometry, material, per-cell density/blast
/// distributions, drift toward world centre, spin, vortex response).
/// </summary>
public static class AsteroidPrefab
{
    /// <summary>Spawns the asteroid and returns its entity (default if the type/material was
    /// invalid). The demo attaches an AsteroidTypeKey to the result for live material tuning.
    /// <paramref name="aimDir"/> overrides the default inward-±45° entry direction (wave spawn
    /// patterns aim their bodies); <paramref name="speedMult"/> scales the sampled entry speed.</summary>
    public static Entity Spawn(World world, GameContext ctx, Random rng, Vector2 pos, string typeKey, float sizeMult,
                               Vector2? aimDir = null, float speedMult = 1f)
    {
        if (!ctx.Config.Asteroids.TryGetValue(typeKey, out var ac) || ac.Procedural == null) return default;
        if (!ctx.Config.Materials.TryGetValue(ac.Material, out var mc))
            mc = ctx.Config.Materials.Values.First();

        // Single source of truth for the material mapping; only the asteroid's global
        // density multiplier is layered on top.
        var mat = mc.ToFractureProperties();
        mat.Density *= ac.DensityMult;

        var body = BuildProceduralAsteroid(ac.Procedural, sizeMult, mat, rng);

        float speed = (ac.SpeedRange[0] + (float)rng.NextDouble() * (ac.SpeedRange[1] - ac.SpeedRange[0])) * speedMult;
        Vector2 dir;
        if (aimDir is { } ad && ad.LengthSquared() > 1e-6f)
        {
            dir = Vector2.Normalize(ad);
        }
        else
        {
            var wc = ctx.Config.World;
            Vector2 toCenter  = new Vector2(wc.Width / 2f, wc.Height / 2f) - pos;
            float   baseAngle = MathF.Atan2(toCenter.Y, toCenter.X);
            float   spread    = ((float)rng.NextDouble() * 2f - 1f) * (MathF.PI / 4f);
            dir = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread));
        }
        var vel = dir * speed;

        float spinMag = ac.SpinRange[0] + (float)rng.NextDouble() * (ac.SpinRange[1] - ac.SpinRange[0]);
        float spin    = spinMag * (rng.NextDouble() > 0.5 ? 1f : -1f);

        var e = FractureBodyFactory.SpawnFromDensity(world, ctx.Config.Physics, body, pos,
                    (float)(rng.NextDouble() * MathF.Tau), vel, spin, MaterialColor(ac.Material));
        world.AddComponent(e, new AsteroidTag());
        world.AddComponent(e, new AsteroidVariant { Key = typeKey });
        world.AddComponent(e, SampleVortexResponse(ac, rng));
        ctx.CellBudget.Add(body.Cells.Length);
        return e;
    }

    /// <summary>Cell-count estimate for an asteroid type at a size, from area / grain.
    /// Used by the wave manager to budget spawns.</summary>
    public static float CellsFor(GameContext ctx, AsteroidConfig ac, float sizeMult)
    {
        if (ac.Procedural == null) return 1f;
        if (!ctx.Config.Materials.TryGetValue(ac.Material, out var mc)) return 1f;
        float r = ac.Procedural.BaseRadius * sizeMult;
        return MathF.Max(1f, MathF.PI * r * r / mc.GrainArea);
    }

    public static BodyColor MaterialColor(string materialKey) => materialKey switch
    {
        "ice"   => new BodyColor { Fill = new Color(70, 115, 155),  Outline = new Color(140, 195, 230) },
        "metal" => new BodyColor { Fill = new Color(50, 60, 75),    Outline = new Color(115, 135, 165) },
        "glass" => new BodyColor { Fill = new Color(60, 120, 95),   Outline = new Color(110, 195, 155) },
        "sand"  => new BodyColor { Fill = new Color(150, 118, 58),  Outline = new Color(224, 190, 110) },
        _       => new BodyColor { Fill = new Color(64, 58, 52),    Outline = new Color(150, 138, 120) },
    };

    private static VortexResponse SampleVortexResponse(AsteroidConfig ac, Random rng)
    {
        var vc = ac.VortexResponse;
        if (vc == null) return new VortexResponse { CentripetalMult = 1f, TangentialMult = 1f };
        float c = vc.CentripetalRange[0] + (float)rng.NextDouble() * (vc.CentripetalRange[1] - vc.CentripetalRange[0]);
        float t = vc.TangentialRange[0]  + (float)rng.NextDouble() * (vc.TangentialRange[1]  - vc.TangentialRange[0]);
        return new VortexResponse { CentripetalMult = c, TangentialMult = t };
    }

    // ── Procedural construction ───────────────────────────────────────────────

    private static FracturableBody BuildProceduralAsteroid(
        ProceduralAsteroidConfig proc, float sizeMult, in FractureProperties mat, Random rng)
    {
        int sides  = rng.Next(proc.VertexCount[0], proc.VertexCount[1] + 1);
        float radius = proc.BaseRadius * sizeMult;
        var (convex, _) = PolygonUtils.GenerateConvex(sides, radius, rng);

        if (proc.Roughness > 0.01f)
        {
            float freq  = MathF.Max(1f, proc.NoiseFrequency);
            float phase = (float)(rng.NextDouble() * MathF.Tau);
            for (int i = 0; i < convex.Length; i++)
            {
                float θ = MathF.Atan2(convex[i].Y, convex[i].X);
                float n = MathF.Sin(θ * freq + phase) * 0.6f
                        + MathF.Sin(θ * freq * 2.1f + phase * 1.4f) * 0.4f;
                if (n > 0f) convex[i] *= 1f + proc.Roughness * n;
            }
        }

        float maxR = 0f;
        foreach (var v in convex) maxR = MathF.Max(maxR, v.Length());
        if (maxR < 1f) maxR = 1f;

        // Uniform seeds; count from the material grain. Heterogeneity is added by clusters.
        int seedCount = Math.Clamp((int)(MathF.PI * radius * radius / mat.GrainArea), 4, 600);
        var seeds = ScatterSeedsUniform(convex, seedCount, maxR, rng);

        float concavity = proc.ConcavityBias;
        Func<Vector2, bool>? membership = concavity > 0.01f
            ? s => { float r = s.Length() / maxR; return rng.NextDouble() > concavity * (r * r); }
            : (Func<Vector2, bool>?)null;

        // Tessellate with uniform bond mults (1); clusters re-weight bonds/cells afterwards.
        var uniform = new float[seeds.Count];
        Array.Fill(uniform, 1f);
        var body = VoronoiTessellator.BuildWithSeeds(convex, seeds, uniform, membership, mat, rng, proc.RelaxIterations);

        ApplyClusters(body, proc, maxR, mat.Toughness, rng);
        return body;
    }

    /// <summary>Grows ClusterCount clusters of higher bond strength / density / blast
    /// resistance. Each cluster picks a centre cell (radial position from ClusterCentrality),
    /// then spreads by a Dijkstra over inter-centroid distances out to a per-cluster reach
    /// (ClusterSpread × radius); the contribution decays linearly with graph distance and
    /// accumulates across overlapping clusters.</summary>
    private static void ApplyClusters(in FracturableBody body, ProceduralAsteroidConfig proc,
                                      float maxR, float toughness, Random rng)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        int n = cells.Length;
        if (n == 0) return;

        var accum = new float[n];

        if (proc.ClusterCount > 0 && bonds.Length > 0)
        {
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var b in bonds) { adj[b.A].Add(b.B); adj[b.B].Add(b.A); }

            float lo = proc.ClusterSpread.Length > 0 ? proc.ClusterSpread[0] : 0.2f;
            float hi = proc.ClusterSpread.Length > 1 ? proc.ClusterSpread[1] : lo;
            float centrality = Math.Clamp(proc.ClusterCentrality, 0f, 1f);

            var dist = new float[n];
            var done = new bool[n];
            for (int k = 0; k < proc.ClusterCount; k++)
            {
                float budget = (lo + (float)rng.NextDouble() * (hi - lo)) * maxR;
                if (budget <= 1e-3f) continue;

                // Cluster centre: random angle at radius (1 − centrality)·maxR.
                float θ = (float)(rng.NextDouble() * MathF.Tau);
                float rTarget = (1f - centrality) * maxR;
                Vector2 target = new(MathF.Cos(θ) * rTarget, MathF.Sin(θ) * rTarget);
                int centre = NearestCellIndex(cells, target);

                // Dijkstra over centroid-distance edges, cut off at the reach budget.
                Array.Fill(dist, float.MaxValue);
                Array.Fill(done, false);
                dist[centre] = 0f;
                while (true)
                {
                    int u = -1; float best = float.MaxValue;
                    for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                    if (u < 0 || best > budget) break;
                    done[u] = true;
                    Vector2 cu = cells[u].Centroid;
                    foreach (int v in adj[u])
                    {
                        float nd = dist[u] + (cells[v].Centroid - cu).Length();
                        if (nd < dist[v]) dist[v] = nd;
                    }
                }
                for (int i = 0; i < n; i++)
                    if (done[i]) accum[i] += (budget - dist[i]) / budget;   // contribution c ∈ (0,1]
            }
        }

        // Apply per-cell density, and compute per-cell bond multipliers. (Density now carries
        // vaporize-resistance — the old per-cell BlastResist is gone.)
        var bondMult = new float[n];
        for (int i = 0; i < n; i++)
        {
            float a = accum[i];
            cells[i].DensityMult = 1f + a * proc.DensityGain;
            bondMult[i] = 1f + a * proc.BondGain;
        }

        // Re-weight bond strengths from the per-cell cluster bond multipliers (geometric mean).
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            float bm = MathF.Sqrt(bondMult[bonds[bi].A] * bondMult[bonds[bi].B]);
            bonds[bi].Strength     = bonds[bi].EdgeLength * toughness * bm;
            bonds[bi].StrengthMult = bm;
        }
    }

    private static List<Vector2> ScatterSeedsUniform(Vector2[] convex, int count, float maxR, Random rng)
    {
        var seeds = new List<Vector2>(count);
        int tries = 0;
        while (seeds.Count < count && tries < count * 50)
        {
            tries++;
            float θ = (float)(rng.NextDouble() * MathF.Tau);
            float r = maxR * MathF.Sqrt((float)rng.NextDouble());   // uniform by area
            var p = new Vector2(MathF.Cos(θ) * r, MathF.Sin(θ) * r);
            if (ProcPointInPolygon(p, convex)) seeds.Add(p);
        }
        while (seeds.Count < 3)
            seeds.Add(new Vector2(
                (float)(rng.NextDouble() - 0.5) * maxR * 0.4f,
                (float)(rng.NextDouble() - 0.5) * maxR * 0.4f));
        return seeds;
    }

    private static int NearestCellIndex(Cell[] cells, Vector2 p)
    {
        int best = 0; float bestSq = float.MaxValue;
        for (int i = 0; i < cells.Length; i++)
        {
            float sq = (cells[i].Centroid - p).LengthSquared();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    private static bool ProcPointInPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = poly[i], pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }
}
