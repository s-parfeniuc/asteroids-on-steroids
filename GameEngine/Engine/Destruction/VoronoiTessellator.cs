using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Builds a <see cref="FracturableBody"/> by pre-fracturing a convex region into
/// Voronoi cells and connecting adjacent cells with bonds.
///
/// Algorithm (matches the design prototype, docs/destruction_engine_spec.md §4.1):
///   1. Scatter seeds on a jittered grid (spacing from the material grain).
///   2. Compute every seed's Voronoi cell = the bound clipped by the perpendicular
///      bisector against every OTHER seed (so kept cells keep their true size).
///   3. Keep cells whose seed passes the membership predicate — concavity is just
///      absent cells (a crater is a region of removed cells).
///   4. Re-centre to the collective centroid (Transform.Position = world centroid).
///   5. Bond cells that share a Voronoi edge; bond strength ∝ shared edge length.
/// </summary>
public static class VoronoiTessellator
{
    private const float MinCellArea     = 2f;    // drop degenerate slivers
    private const float MinSharedEdge   = 1.5f;  // below this no bond is formed
    private const float CollinearTol    = 0.7f;  // px; shared-edge collinearity tolerance

    /// <summary>Builds a fracturable asteroid: a random convex outline of the given
    /// radius, tessellated by the material grain. <paramref name="membership"/> may
    /// carve concavities (return false to drop a cell); null = solid convex blob.</summary>
    public static FracturableBody BuildAsteroid(
        int sides, float radius, in FractureProperties material,
        Func<Vector2, bool>? membership, Random rng)
    {
        var (bound, _) = PolygonUtils.GenerateConvex(sides, radius, rng);
        return Build(bound, material, membership, rng);
    }

    /// <summary>
    /// Builds a fracturable body from an authored shape: explicit seed positions tessellate
    /// the convex hull of <paramref name="outline"/>. Per-seed bond multipliers scale each
    /// bond's strength by the geometric mean of the two adjacent seeds' values, letting
    /// authored shapes declare reinforced joints (cockpit, bumper) vs. weak separation zones.
    /// </summary>
    /// <param name="outline">Body-local shape vertices (may be non-convex); the convex hull
    /// is used as the Voronoi boundary.</param>
    /// <param name="seedPositions">Explicit Voronoi seed positions (body-local).</param>
    /// <param name="seedBondMults">Per-seed bond-strength multiplier, parallel to
    /// <paramref name="seedPositions"/>; 1.0 = nominal, >1 = tougher joint.</param>
    public static FracturableBody BuildFromExplicitSeeds(
        IReadOnlyList<Vector2> outline,
        IReadOnlyList<Vector2> seedPositions,
        IReadOnlyList<float>   seedBondMults,
        in FractureProperties  material,
        Random rng)
    {
        if (outline.Count < 3)      throw new ArgumentException("Outline needs ≥ 3 vertices.", nameof(outline));
        if (seedPositions.Count < 1) throw new ArgumentException("At least one seed required.", nameof(seedPositions));

        // Use the outline directly as the Voronoi boundary — same as the JS shape editor.
        // Taking the convex hull would lose the non-convex silhouette (e.g. ship wings).
        var seeds = new Vector2[seedPositions.Count];
        for (int i = 0; i < seeds.Length; i++) seeds[i] = seedPositions[i];

        var kept    = new List<Vector2[]>(seeds.Length);
        var keptIdx = new List<int>(seeds.Length);

        for (int i = 0; i < seeds.Length; i++)
        {
            var poly = VoronoiCell(i, seeds, outline);
            if (poly.Count < 3) continue;
            if (MathF.Abs(PolygonUtils.ComputeArea(poly)) < MinCellArea) continue;
            kept.Add(poly.ToArray());
            keptIdx.Add(i);
        }
        if (kept.Count == 0) { kept.Add(outline.ToArray()); keptIdx.Add(0); }

        float totalArea = 0f;
        Vector2 bodyCentroid = Vector2.Zero;
        foreach (var poly in kept)
        {
            float a = MathF.Abs(PolygonUtils.ComputeArea(poly));
            bodyCentroid += PolygonUtils.ComputeCentroid(poly) * a;
            totalArea    += a;
        }
        bodyCentroid /= totalArea;

        var cells = new Cell[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var world = kept[i];
            var local = new Vector2[world.Length];
            for (int k = 0; k < world.Length; k++) local[k] = world[k] - bodyCentroid;
            cells[i] = new Cell
            {
                Local    = local,
                Centroid = PolygonUtils.ComputeCentroid(world) - bodyCentroid,
                Area     = MathF.Abs(PolygonUtils.ComputeArea(world)),
            };
        }

        var bonds = BuildBondsWeighted(cells, keptIdx, seedBondMults, material.Toughness);

        return new FracturableBody
        {
            Cells    = cells,
            Bonds    = bonds,
            Material = material,
            State    = new FractureState { AbsorbedEnergy = 0f, RngSeed = (uint)rng.Next() },
        };
    }

    /// <summary>
    /// Builds a fracturable body from explicit seed positions, with per-seed bond multipliers
    /// and an optional membership predicate for carving concavities.
    /// The full Voronoi is computed over ALL seeds; cells whose seed fails membership are
    /// dropped, leaving holes. Bond strengths are the geometric mean of adjacent seed mults.
    /// </summary>
    public static FracturableBody BuildWithSeeds(
        IReadOnlyList<Vector2> convexBound,
        IReadOnlyList<Vector2> seedPositions,
        IReadOnlyList<float>   seedBondMults,
        Func<Vector2, bool>?   membership,
        in FractureProperties  material,
        Random rng)
    {
        if (convexBound.Count < 3)     throw new ArgumentException("Bound needs ≥ 3 vertices.", nameof(convexBound));
        if (seedPositions.Count < 1)   throw new ArgumentException("At least one seed required.", nameof(seedPositions));

        var seeds = new Vector2[seedPositions.Count];
        for (int i = 0; i < seeds.Length; i++) seeds[i] = seedPositions[i];

        var kept    = new List<Vector2[]>(seeds.Length);
        var keptIdx = new List<int>(seeds.Length);

        for (int i = 0; i < seeds.Length; i++)
        {
            var poly = VoronoiCell(i, seeds, convexBound);
            if (poly.Count < 3) continue;
            if (MathF.Abs(PolygonUtils.ComputeArea(poly)) < MinCellArea) continue;
            if (membership != null && !membership(seeds[i])) continue;
            kept.Add(poly.ToArray());
            keptIdx.Add(i);
        }
        if (kept.Count == 0) { kept.Add(convexBound.ToArray()); keptIdx.Add(0); }

        float totalArea = 0f;
        Vector2 bodyCentroid = Vector2.Zero;
        foreach (var poly in kept)
        {
            float a = MathF.Abs(PolygonUtils.ComputeArea(poly));
            bodyCentroid += PolygonUtils.ComputeCentroid(poly) * a;
            totalArea    += a;
        }
        bodyCentroid /= totalArea;

        var cells = new Cell[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var world = kept[i];
            var local = new Vector2[world.Length];
            for (int k = 0; k < world.Length; k++) local[k] = world[k] - bodyCentroid;
            cells[i] = new Cell
            {
                Local    = local,
                Centroid = PolygonUtils.ComputeCentroid(world) - bodyCentroid,
                Area     = MathF.Abs(PolygonUtils.ComputeArea(world)),
            };
        }

        var bonds = BuildBondsWeighted(cells, keptIdx, seedBondMults, material.Toughness);

        return new FracturableBody
        {
            Cells    = cells,
            Bonds    = bonds,
            Material = material,
            State    = new FractureState { AbsorbedEnergy = 0f, RngSeed = (uint)rng.Next() },
        };
    }

    /// <summary>Builds a fracturable body from an explicit convex bound.</summary>
    public static FracturableBody Build(
        IReadOnlyList<Vector2> convexBound, in FractureProperties material,
        Func<Vector2, bool>? membership, Random rng)
    {
        Vector2[] seeds = ScatterSeeds(convexBound, material.GrainArea, rng);

        // Full Voronoi over ALL seeds, then keep those passing membership.
        var kept = new List<Vector2[]>(seeds.Length);
        for (int i = 0; i < seeds.Length; i++)
        {
            var poly = VoronoiCell(i, seeds, convexBound);
            if (poly.Count < 3) continue;
            if (MathF.Abs(PolygonUtils.ComputeArea(poly)) < MinCellArea) continue;
            if (membership != null && !membership(seeds[i])) continue;
            kept.Add(poly.ToArray());
        }
        if (kept.Count == 0) kept.Add(convexBound.ToArray());   // degenerate fallback

        // Collective (area-weighted) centroid → body local origin.
        float totalArea = 0f;
        Vector2 bodyCentroid = Vector2.Zero;
        foreach (var poly in kept)
        {
            float a = MathF.Abs(PolygonUtils.ComputeArea(poly));
            bodyCentroid += PolygonUtils.ComputeCentroid(poly) * a;
            totalArea    += a;
        }
        bodyCentroid /= totalArea;

        var cells = new Cell[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var world = kept[i];
            var local = new Vector2[world.Length];
            for (int k = 0; k < world.Length; k++) local[k] = world[k] - bodyCentroid;
            cells[i] = new Cell
            {
                Local    = local,
                Centroid = PolygonUtils.ComputeCentroid(world) - bodyCentroid,
                Area     = MathF.Abs(PolygonUtils.ComputeArea(world)),
            };
        }

        var bonds = BuildBonds(cells, material.Toughness);

        return new FracturableBody
        {
            Cells    = cells,
            Bonds    = bonds,
            Material = material,
            State    = new FractureState { AbsorbedEnergy = 0f, RngSeed = (uint)rng.Next() },
        };
    }

    // -------------------------------------------------------------------------
    // Collision-shape / mass helpers
    // -------------------------------------------------------------------------

    /// <summary>Builds the CompoundShape collider for a body (one convex part per cell).</summary>
    public static CompoundShape BuildShape(in FracturableBody body)
    {
        var parts = new CollisionShape[body.Cells.Length];
        for (int i = 0; i < body.Cells.Length; i++)
            parts[i] = new PolygonShape(body.Cells[i].Local);
        return new CompoundShape(parts);
    }

    public static float TotalArea(in FracturableBody body)
    {
        float a = 0f;
        foreach (var c in body.Cells) a += c.Area;
        return a;
    }

    /// <summary>Total mass of the body accounting for per-cell DensityMult.
    /// Use this instead of <c>Density × TotalArea</c> when cells may have non-uniform density.</summary>
    public static float TotalMass(in FracturableBody body)
    {
        float weighted = 0f;
        foreach (var c in body.Cells) weighted += c.Area * c.DensityMult;
        return MathF.Max(1f, body.Material.Density * weighted);
    }

    /// <summary>Moment of inertia about the body's centre of mass (parallel-axis sum over
    /// cells), using per-cell DensityMult to weight the mass distribution.</summary>
    public static float ComputeInertia(in FracturableBody body, float mass)
    {
        float totalWeighted = 0f;
        foreach (var c in body.Cells) totalWeighted += c.Area * c.DensityMult;
        if (totalWeighted <= 0f) return 0f;
        float inertia = 0f;
        foreach (var c in body.Cells)
        {
            float cellMass = mass * (c.Area * c.DensityMult / totalWeighted);
            inertia += PolygonUtils.ComputeInertia(c.Local, cellMass)
                     + cellMass * c.Centroid.LengthSquared();
        }
        return inertia;
    }

    // -------------------------------------------------------------------------
    // Voronoi + seeds
    // -------------------------------------------------------------------------

    private static Vector2[] ScatterSeeds(IReadOnlyList<Vector2> bound, float grainArea, Random rng)
    {
        float step = MathF.Sqrt(MathF.Max(grainArea, 1f));

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in bound) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }

        var seeds = new List<Vector2>();
        for (float y = min.Y; y < max.Y; y += step)
            for (float x = min.X; x < max.X; x += step)
            {
                var p = new Vector2(
                    x + (float)(rng.NextDouble() - 0.5) * step * 0.7f,
                    y + (float)(rng.NextDouble() - 0.5) * step * 0.7f);
                if (PointInPolygon(p, bound)) seeds.Add(p);
            }

        // Guarantee at least a few seeds for tiny bounds.
        while (seeds.Count < 3)
            seeds.Add(new Vector2(
                (min.X + max.X) * 0.5f + (float)(rng.NextDouble() - 0.5) * step,
                (min.Y + max.Y) * 0.5f + (float)(rng.NextDouble() - 0.5) * step));

        return seeds.ToArray();
    }

    /// <summary>Voronoi cell of seed[self]: the bound clipped by the perpendicular
    /// bisector half-plane (keeping the seed's side) against every other seed.</summary>
    private static List<Vector2> VoronoiCell(int self, Vector2[] seeds, IReadOnlyList<Vector2> bound)
    {
        Vector2 s = seeds[self];
        var poly = new List<Vector2>(bound);

        for (int j = 0; j < seeds.Length; j++)
        {
            if (j == self) continue;
            Vector2 t   = seeds[j];
            Vector2 n   = s - t;
            float   len = n.Length();
            if (len < 1e-6f) continue;
            n /= len;
            Vector2 mid = (s + t) * 0.5f;
            poly = PolygonUtils.ClipConvexByHalfPlane(poly, mid, n);   // keep dot(p-mid,n) ≥ 0 (s's side)
            if (poly.Count < 3) break;
        }
        return poly;
    }

    // -------------------------------------------------------------------------
    // Bonds: two cells bond if they share a Voronoi edge
    // -------------------------------------------------------------------------

    private static Bond[] BuildBonds(Cell[] cells, float toughness)
    {
        var bonds = new List<Bond>();
        for (int i = 0; i < cells.Length; i++)
            for (int j = i + 1; j < cells.Length; j++)
            {
                float shared = SharedEdgeLength(cells[i].Local, cells[j].Local);
                if (shared > MinSharedEdge)
                    bonds.Add(new Bond { A = i, B = j, EdgeLength = shared, Strength = shared * toughness });
            }
        return bonds.ToArray();
    }

    private static Bond[] BuildBondsWeighted(
        Cell[] cells, List<int> seedIndices, IReadOnlyList<float> mults, float toughness)
    {
        var bonds = new List<Bond>();
        for (int i = 0; i < cells.Length; i++)
            for (int j = i + 1; j < cells.Length; j++)
            {
                float shared = SharedEdgeLength(cells[i].Local, cells[j].Local);
                if (shared > MinSharedEdge)
                {
                    float mi = seedIndices[i] < mults.Count ? mults[seedIndices[i]] : 1f;
                    float mj = seedIndices[j] < mults.Count ? mults[seedIndices[j]] : 1f;
                    float bm = MathF.Sqrt(mi * mj);
                    bonds.Add(new Bond
                    {
                        A = i, B = j,
                        EdgeLength = shared,
                        Strength   = shared * toughness * bm,
                    });
                }
            }
        return bonds.ToArray();
    }

    private static float SharedEdgeLength(Vector2[] a, Vector2[] b)
    {
        float shared = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            Vector2 a0 = a[i], a1 = a[(i + 1) % a.Length];
            for (int j = 0; j < b.Length; j++)
            {
                Vector2 b0 = b[j], b1 = b[(j + 1) % b.Length];
                shared += SegmentOverlap(a0, a1, b0, b1);
            }
        }
        return shared;
    }

    /// <summary>Length of the collinear overlap between segments a0-a1 and b0-b1
    /// (0 if not collinear within tolerance). Voronoi-shared edges lie on the same
    /// bisector line, so their overlap is the bond's edge length.</summary>
    private static float SegmentOverlap(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        Vector2 ab = a1 - a0;
        float   L  = ab.Length();
        if (L < 1e-6f) return 0f;
        Vector2 u    = ab / L;
        Vector2 perp = new(-u.Y, u.X);

        if (MathF.Abs(Vector2.Dot(b0 - a0, perp)) > CollinearTol) return 0f;
        if (MathF.Abs(Vector2.Dot(b1 - a0, perp)) > CollinearTol) return 0f;

        float tb0 = Vector2.Dot(b0 - a0, u);
        float tb1 = Vector2.Dot(b1 - a0, u);
        float lo  = MathF.Max(0f, MathF.Min(tb0, tb1));
        float hi  = MathF.Min(L,  MathF.Max(tb0, tb1));
        return MathF.Max(0f, hi - lo);
    }

    private static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = poly[i], pj = poly[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                inside = !inside;
        }
        return inside;
    }
}
