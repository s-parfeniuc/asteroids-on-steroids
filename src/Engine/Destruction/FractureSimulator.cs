using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Diagnostics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Geometry + bookkeeping for the conservative fracture model. The per-pop physics lives in
/// <see cref="FractureKernel"/>; this builds the bond graph snapshot, finds connected components
/// over the surviving bonds, and turns them into fragment specs whose fling is DERIVED from the
/// fling energy each cell accumulated. No ECS dependency.
/// </summary>
public static class FractureSimulator
{
    // -------------------------------------------------------------------------
    // Graph snapshot for a live fracture.
    // -------------------------------------------------------------------------

    /// <summary>Snapshot the bond graph: the per-bond spin multiplier (1+spinFactor from body ω)
    /// and the per-cell adjacency. Bond <see cref="Bond.Strength"/> itself is never mutated.</summary>
    public static (float[] spinMul, List<int>[] adj) PrepareGraph(in FracturableBody body, float spinOmega)
    {
        var spinMul = new float[body.Bonds.Length];
        ComputeSpinMul(body.Cells, body.Bonds, spinOmega, body.Material.SpinPreStress, spinMul);
        var adj = BuildAdjacency(body.Cells.Length, body.Bonds);
        return (spinMul, adj);
    }

    /// <summary>Per-bond spin stress multiplier: a spinning body's tangential rim bonds take more
    /// stress per joule. spinFactor = clamp(SpinPreStress·ω²·(0.3+0.7·r/rmax)·tangentiality, 0, SpinCap).</summary>
    private static void ComputeSpinMul(Cell[] cells, Bond[] bonds, float omega, float spinCoeff, float[] spinMul)
    {
        for (int k = 0; k < bonds.Length; k++) spinMul[k] = 1f;
        if (bonds.Length == 0 || spinCoeff <= 0f || omega == 0f) return;

        float rmax = 1e-3f;
        foreach (var c in cells) rmax = MathF.Max(rmax, c.Centroid.Length());

        float w2 = omega * omega;
        for (int k = 0; k < bonds.Length; k++)
        {
            Bond b = bonds[k];
            Vector2 m = (cells[b.A].Centroid + cells[b.B].Centroid) * 0.5f;   // CoM = origin
            float r = m.Length();
            if (r < 1e-3f) continue;

            Vector2 rad = m / r;
            Vector2 tan = new(-rad.Y, rad.X);
            Vector2 dir = cells[b.B].Centroid - cells[b.A].Centroid;
            float dl = dir.Length();
            if (dl > 1e-6f) dir /= dl;

            float tangentiality = MathF.Abs(Vector2.Dot(dir, tan));
            float baseP = FractureTuning.SpinProfileBase;
            float profile = baseP + (1f - baseP) * (r / rmax);
            float sf = Math.Clamp(spinCoeff * w2 * profile * tangentiality, 0f, FractureTuning.SpinCap);
            spinMul[k] = 1f + sf;
        }
    }

    private static List<int>[] BuildAdjacency(int n, Bond[] bonds)
    {
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        for (int k = 0; k < bonds.Length; k++) { adj[bonds[k].A].Add(k); adj[bonds[k].B].Add(k); }
        return adj;
    }

    // -------------------------------------------------------------------------
    // Connected components over the surviving (unbroken, unpulverised) bond graph.
    // -------------------------------------------------------------------------
    private static int[] ConnectedComponents(int n, Bond[] bonds, bool[] broken, bool[] pulverized, out int count)
    {
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        for (int k = 0; k < bonds.Length; k++)
            if (!broken[k] && !pulverized[bonds[k].A] && !pulverized[bonds[k].B])
                parent[Find(bonds[k].A)] = Find(bonds[k].B);

        var label = new int[n];
        for (int i = 0; i < n; i++) label[i] = -1;
        count = 0;
        var comp = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (pulverized[i]) { comp[i] = -1; continue; }
            int r = Find(i);
            if (label[r] < 0) label[r] = count++;
            comp[i] = label[r];
        }
        return comp;
    }

    /// <summary>Counts connected components over the surviving bond graph — used each iteration
    /// to detect when a body has split.</summary>
    public static int CountComponents(int n, Bond[] bonds, bool[] broken, bool[] pulverized)
    {
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        for (int k = 0; k < bonds.Length; k++)
            if (!broken[k] && !pulverized[bonds[k].A] && !pulverized[bonds[k].B])
                parent[Find(bonds[k].A)] = Find(bonds[k].B);

        int count = 0;
        for (int i = 0; i < n; i++)
            if (!pulverized[i] && Find(i) == i) count++;
        return count;
    }

    // -------------------------------------------------------------------------
    // Fragment construction.
    // -------------------------------------------------------------------------

    /// <summary>Finalise a fracture: connected components over the surviving bonds → fragment
    /// specs, with fling derived from <paramref name="flingE"/>. Pulverised cells were already
    /// dusted live, so they are not re-emitted here.</summary>
    public static FragmentSpec[] BuildResult(in FracturableBody body, in FractureInput input,
        bool[] broken, bool[] pulverized, float[] flingE, Random rng, bool fling = true)
    {
        float totalArea = 0f;
        foreach (var c in body.Cells) totalArea += c.Area;
        int[] comp = ConnectedComponents(body.Cells.Length, body.Bonds, broken, pulverized, out int compCount);

        var groups = new List<int>[compCount];
        for (int c = 0; c < compCount; c++) groups[c] = new List<int>();
        for (int i = 0; i < body.Cells.Length; i++) if (comp[i] >= 0) groups[comp[i]].Add(i);

        var result = new List<FragmentSpec>(compCount);
        for (int c = 0; c < compCount; c++)
        {
            if (groups[c].Count == 0) continue;
            result.Add(BuildComponentSpec(body, input, groups[c], comp, c, broken, flingE, totalArea, rng,
                                          fling: fling, out _));
        }
        return result.ToArray();
    }

    /// <summary>Builds one connected component into a re-centred fragment body (+ the old→new cell
    /// index remap, for carrying live crack fronts across a split). When <paramref name="fling"/>
    /// is false the piece keeps the parent's rigid motion only (the continuer).</summary>
    private static FragmentSpec BuildComponentSpec(
        in FracturableBody body, in FractureInput input, List<int> idxs, int[] comp, int label,
        bool[] broken, float[] flingE, float totalArea, Random rng, bool fling, out Dictionary<int, int> remap)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        FractureProperties mat = body.Material;
        float cos = MathF.Cos(input.BodyRotation), sin = MathF.Sin(input.BodyRotation);
        Vector2 bodyPos = input.BodyPosition;

        float area = 0f, weighted = 0f;
        Vector2 cen = Vector2.Zero;
        foreach (int ci in idxs)
        {
            area += cells[ci].Area;
            weighted += cells[ci].Area * cells[ci].DensityMult;
            cen += cells[ci].Centroid * cells[ci].Area;
        }
        cen /= area;

        // A single detaching cell shrinks toward its centroid so it doesn't refill its socket.
        bool shrinkCell = fling && idxs.Count == 1 && mat.DetachCellScale > 0f;
        var newCells = new Cell[idxs.Count];
        var cellFling = new float[idxs.Count];
        remap = new Dictionary<int, int>(idxs.Count);
        for (int k = 0; k < idxs.Count; k++)
        {
            int ci = idxs[k];
            remap[ci] = k;
            cellFling[k] = flingE[ci];
            Cell src = cells[ci];
            Vector2 cellCen = src.Centroid - cen;
            var local = new Vector2[src.Local.Length];
            if (shrinkCell)
            {
                float jitter = mat.DetachCellJitter;
                for (int v = 0; v < local.Length; v++)
                {
                    float s = mat.DetachCellScale + (float)(rng.NextDouble() * 2 - 1) * jitter;
                    local[v] = cellCen + (src.Local[v] - src.Centroid) * s;
                }
            }
            else
            {
                for (int v = 0; v < local.Length; v++) local[v] = src.Local[v] - cen;
            }
            // Damage carries over (× SplitStressInherit): fragmentation must not heal the survivors.
            newCells[k] = new Cell { Local = local, Centroid = cellCen, Area = src.Area,
                                     DensityMult = src.DensityMult, Role = src.Role, FillColor = src.FillColor,
                                     Damage = src.Damage * FractureTuning.SplitStressInherit };
        }

        // Keep only UNBROKEN bonds within the component; broken ones become cracks (missing bonds).
        // Surviving bonds keep their accumulated Stress (× SplitStressInherit) for the same reason.
        var newBonds = new List<Bond>();
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            Bond b = bonds[bi];
            if (!broken[bi] && comp[b.A] == label && comp[b.B] == label)
                newBonds.Add(new Bond { A = remap[b.A], B = remap[b.B], EdgeLength = b.EdgeLength,
                                        Strength = b.Strength, StrengthMult = b.StrengthMult,
                                        Stress = b.Stress * FractureTuning.SplitStressInherit });
        }

        // Fragment mass = its DensityMult-weighted share of the body mass.
        float totalWeighted = 0f;
        foreach (var cc in cells) totalWeighted += cc.Area * cc.DensityMult;
        float mass = totalWeighted > 1e-6f
            ? input.BodyMass * (weighted / totalWeighted)
            : input.BodyMass * (area / totalArea);
        float inertia = InertiaAbout(newCells, mass);
        Vector2 worldCentroid = new(cen.X * cos - cen.Y * sin + bodyPos.X,
                                    cen.X * sin + cen.Y * cos + bodyPos.Y);

        Vector2 linear; float angular;
        if (fling)
            (linear, angular) = DerivedMotion(input, worldCentroid, newCells, cellFling, mass);
        else
        {
            Vector2 r = worldCentroid - input.BodyPosition;
            linear = input.BodyLinear + new Vector2(-input.BodyAngular * r.Y, input.BodyAngular * r.X);
            angular = input.BodyAngular;
        }

        return new FragmentSpec
        {
            Body = new FracturableBody
            {
                Cells = newCells,
                Bonds = newBonds.ToArray(),
                Material = mat,
                State = new FractureState { RngSeed = (uint)rng.Next() },
            },
            WorldCentroid = worldCentroid,
            Rotation = input.BodyRotation,
            Linear = linear,
            Angular = angular,
            Mass = mass,
            Inertia = inertia,
            Area = area,
            // Only genuinely degenerate slivers become pure dust; everything else spawns as a body.
            // Small bodies (area < MinFragmentArea) are flagged Fragile at spawn and one-shot-vaporise
            // instead of lingering — so MinFragmentArea now tunes the fragile threshold, not the dust one.
            IsDebris = idxs.Count == 1 && area < 40f,
        };
    }

    /// <summary>Fragment linear + angular velocity DERIVED from the fling energy it accumulated:
    /// speed ∝ √(2·Ekin/mass) away from the impact, tumble from the off-centre asymmetry of that
    /// fling. No eject/spin knobs — the kinetic channel drives the motion.</summary>
    private static (Vector2 linear, float angular) DerivedMotion(
        in FractureInput input, Vector2 worldCentroid, Cell[] fragCells, float[] cellFling, float mass)
    {
        float ekin = 0f; foreach (float f in cellFling) ekin += f;

        Vector2 r = worldCentroid - input.BodyPosition;
        Vector2 rotVel = new(-input.BodyAngular * r.Y, input.BodyAngular * r.X);   // ω × r carry-over
        Vector2 spread = worldCentroid - input.ImpactPointWorld;
        float sl = spread.Length();
        spread = sl > 1e-4f ? spread / sl : Vector2.UnitX;

        float speed = Math.Clamp(FractureTuning.FlingScale * MathF.Sqrt(2f * MathF.Max(ekin, 0f) / MathF.Max(mass, 1f)), 0f, FractureTuning.FragmentSpeedMax);
        Vector2 linear = input.BodyLinear + rotVel + spread * speed;

        // Tumble: fling-weighted offset of the fragment's cells crossed with its velocity.
        Vector2 offLocal = Vector2.Zero; float inertia = 0f;
        if (ekin > 1e-3f)
            for (int k = 0; k < fragCells.Length; k++)
            {
                Vector2 rc = fragCells[k].Centroid;   // relative to fragment centroid (body-local)
                offLocal += rc * (cellFling[k] / ekin);
                inertia += fragCells[k].Area * fragCells[k].DensityMult * rc.LengthSquared();
            }
        float c = MathF.Cos(input.BodyRotation), s = MathF.Sin(input.BodyRotation);
        Vector2 offWorld = new(offLocal.X * c - offLocal.Y * s, offLocal.X * s + offLocal.Y * c);
        float torque = offWorld.X * linear.Y - offWorld.Y * linear.X;
        float angular = input.BodyAngular + Math.Clamp(torque / MathF.Max(inertia, 1e-3f) * FractureTuning.TumbleScale,
                                                       -FractureTuning.FragmentSpinMax, FractureTuning.FragmentSpinMax);

        return (linear, angular);
    }

    private static float InertiaAbout(Cell[] cells, float mass)
    {
        float total = 0f;
        foreach (var c in cells) total += c.Area;
        if (total <= 0f) return 0f;

        float inertia = 0f;
        foreach (var c in cells)
        {
            float m = mass * (c.Area / total);
            inertia += PolygonUtils.ComputeInertia(c.Local, m) + m * c.Centroid.LengthSquared();
        }
        return inertia;
    }

    // -------------------------------------------------------------------------
    // Mid-fracture split: partition a body that broke into ≥2 components into fresh pieces.
    // -------------------------------------------------------------------------
    public static List<LivePiece> SplitLive(in FracturableBody body, in FractureInput input,
                                            in FractureProcess proc, Random rng)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        bool[] broken = proc.Broken, pulverized = proc.Pulverized;
        float[] flingE = proc.FlingE;

        float totalArea = 0f;
        foreach (var c in cells) totalArea += c.Area;

        int[] comp = ConnectedComponents(cells.Length, bonds, broken, pulverized, out int compCount);
        var groups = new List<int>[compCount];
        for (int c = 0; c < compCount; c++) groups[c] = new List<int>();
        for (int i = 0; i < cells.Length; i++) if (comp[i] >= 0) groups[comp[i]].Add(i);

        // Continuer = component holding the most live wavefront energy.
        var energyByComp = new float[compCount];
        foreach (var f in proc.Fronts)
        {
            if (!f.Active) continue;
            foreach (int fc in f.Frontier)
                if (comp[fc] >= 0) energyByComp[comp[fc]] += f.Energy[fc];
        }
        int continuer = -1; float best = 0f;
        for (int c = 0; c < compCount; c++) if (energyByComp[c] > best) { best = energyByComp[c]; continuer = c; }

        var pieces = new List<LivePiece>(compCount);
        for (int c = 0; c < compCount; c++)
        {
            if (groups[c].Count == 0) continue;

            var spec = BuildComponentSpec(body, input, groups[c], comp, c, broken, flingE, totalArea, rng,
                                          fling: c != continuer, out var remap);

            // Carry any active fronts whose wavefront reaches into this component.
            var subFronts = new List<CrackFront>();
            foreach (var f in proc.Fronts)
            {
                if (!f.Active) continue;
                bool here = false;
                foreach (int fc in f.Frontier) if (comp[fc] == c) { here = true; break; }
                if (!here) continue;
                var sub = PartitionFront(f, comp, c, remap, spec.Body.Cells.Length);
                if (sub != null) subFronts.Add(sub);
            }

            FractureProcess? sub2 = null;
            if (subFronts.Count > 0)
            {
                var (spinMul, adj) = PrepareGraph(spec.Body, input.BodyAngular);
                sub2 = new FractureProcess
                {
                    Fronts = subFronts,
                    Broken = new bool[spec.Body.Bonds.Length],     // cracks already baked as missing bonds
                    Pulverized = new bool[spec.Body.Cells.Length],
                    Emitted = new bool[spec.Body.Cells.Length],
                    FlingE = new float[spec.Body.Cells.Length],
                    SpinMul = spinMul,
                    Adj = adj,
                    ImpactDir = proc.ImpactDir,
                    ImpactPointWorld = proc.ImpactPointWorld,
                    Directionality = proc.Directionality,
                    Done = false,
                };
            }
            pieces.Add(new LivePiece { Spec = spec, Process = sub2 });
        }
        return pieces;
    }

    /// <summary>Carve one component's slice out of a front: its cells' energy (remapped to the new
    /// compact indices) and the frontier cells that fall in it. Returns null if no wavefront reaches.</summary>
    private static CrackFront? PartitionFront(CrackFront f, int[] comp, int label,
                                              Dictionary<int, int> remap, int newCellCount)
    {
        var energy = new float[newCellCount];
        var processed = new float[newCellCount];
        var parent = new int[newCellCount];
        for (int i = 0; i < newCellCount; i++) { processed[i] = -1f; parent[i] = -1; }
        foreach (var kv in remap)
        {
            energy[kv.Value] = f.Energy[kv.Key];
            processed[kv.Value] = f.Processed[kv.Key];
            int p = f.Parent[kv.Key];                              // remap the flow parent if it stayed in this piece
            if (p >= 0 && remap.TryGetValue(p, out int np)) parent[kv.Value] = np;
        }

        var frontier = new List<int>();
        foreach (int fc in f.Frontier)
            if (comp[fc] == label && remap.TryGetValue(fc, out int nk)) frontier.Add(nk);
        if (frontier.Count == 0) return null;

        var sub = new CrackFront
        {
            Energy = energy,
            Processed = processed,
            Parent = parent,
            ImpactDirLocal = f.ImpactDirLocal,
            Directionality = f.Directionality,
            Brittleness = f.Brittleness,
            BlastFraction = f.BlastFraction,
            // Keep the hit's own pace across the split so a fast-cracking front stays fast.
            StepsPerIteration = f.StepsPerIteration,
            FramesPerIteration = f.FramesPerIteration,
            FrameCounter = f.FrameCounter,
        };
        sub.Frontier.AddRange(frontier);
        return sub;
    }
}
