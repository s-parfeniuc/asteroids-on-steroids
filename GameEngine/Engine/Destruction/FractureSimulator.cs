using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Diagnostics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// The pure-logic core of the destruction model (docs/destruction_engine_spec.md
/// §4.4–4.8). Given a body, the struck cell and the impact energy, it:
///   1. checks the fracture threshold (sub-threshold → absorb, no split),
///   2. splits the available energy into a surface budget + a kinetic share,
///   3. propagates a crack from the struck cell, spending the budget breaking bonds
///      (brittleness = reach, directionality = which bonds, spin = pre-stress),
///   4. finds connected components over the surviving bonds,
///   5. builds a FragmentSpec per component (re-centred geometry, mass, inertia,
///      velocity from parent motion + kinetic fling).
/// No ECS dependency — the FractureSystem feeds it and wires the result to entities.
/// </summary>
public static class FractureSimulator
{
    private const float Transmission0 = 0.18f;  // brittleness 0 → short reach
    private const float Transmission1 = 0.96f;  // brittleness 1 → long reach
    private const float MinEffStrengthFrac = 0.02f;

    public static FractureResult Simulate(in FracturableBody body, in FractureInput input, Random rng)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        FractureProperties mat = body.Material;

        var budget = ComputeBudget(body, input.StruckCell, input.BodyMass, input.EnergyTotal);
        if (!budget.Fractured)
            return new FractureResult
            {
                Fractured = false,
                AbsorbedEnergy = budget.Absorbed,
                ImpactPointWorld = input.ImpactPointWorld,
            };

        float totalArea = budget.TotalArea;
        float eSurface = budget.Surface;
        float eKinetic = budget.Kinetic;

        var eff = new float[bonds.Length];
        ComputeEffStrengths(cells, bonds, input.SpinOmega, mat.SpinPreStress, eff);

        var adj = BuildAdjacency(cells.Length, bonds);
        var broken = new bool[bonds.Length];
        var energy = new float[cells.Length];
        Propagate(cells, bonds, adj, eff, broken, energy,
                  input.StruckCell, input.EnergyTotal, eSurface,
                  RotateInv(input.ImpactDir, input.BodyRotation),
                  input.Directionality, mat.Brittleness);

        // Cells that absorb enough crack energy are PULVERISED → vaporise to debris.
        // This carves the impact crater and opens the surface, so subsurface fragments
        // aren't left trapped under intact cells.
        var pulverized = new bool[cells.Length];
        float blast = Math.Clamp(input.BlastFraction, 0f, 1f);
        if (blast > 0f)
        {
            // Vaporisation crater. The struck cell holds the full impact energy and the
            // energy decays outward, so a threshold of (1-blast)·E selects the hottest
            // cells around the hit: blast→0 vaporises nothing (pure fragmentation),
            // blast→1 vaporises everything the crack reached.
            float blastBase = (1f - blast) * input.EnergyTotal;
            for (int i = 0; i < cells.Length; i++)
            {
                if (energy[i] <= 0f) continue;
                // BlastResist [0,1]: 0 = normal threshold, 1 = needs full impact energy to vaporise.
                float cellThresh = blastBase + cells[i].BlastResist * (input.EnergyTotal - blastBase);
                if (energy[i] > cellThresh) pulverized[i] = true;
            }
        }

        int[] comp = ConnectedComponents(cells.Length, bonds, broken, pulverized, out int compCount);
        var fragments = BuildFragments(body, input, comp, compCount, broken, pulverized, totalArea, rng);

        return new FractureResult
        {
            Fractured = true,
            AbsorbedEnergy = 0f,
            Fragments = fragments,
            ImpactPointWorld = input.ImpactPointWorld,
            EnergySurface = eSurface,
            EnergyKinetic = eKinetic,
        };
    }

    // -------------------------------------------------------------------------
    // Shared building blocks (used by both single-frame Simulate and the
    // multi-frame FractureCrackSystem so both paths use identical physics).
    // -------------------------------------------------------------------------

    /// <summary>Energy split after the fracture threshold: a surface (crack) budget and
    /// a kinetic (fling) share. Fractured == false means the hit was sub-threshold.</summary>
    public readonly struct FractureBudget
    {
        public readonly bool Fractured;
        public readonly float Surface, Kinetic, Absorbed, TotalArea;
        public FractureBudget(bool fractured, float surface, float kinetic, float absorbed, float totalArea)
        { Fractured = fractured; Surface = surface; Kinetic = kinetic; Absorbed = absorbed; TotalArea = totalArea; }
    }

    public static FractureBudget ComputeBudget(in FracturableBody body, int struckCell, float bodyMass, float energyTotal)
    {
        Cell[] cells = body.Cells;
        FractureProperties mat = body.Material;

        float totalArea = 0f;
        foreach (var c in cells) totalArea += c.Area;

        float struckMass = bodyMass * (cells[struckCell].Area / totalArea);
        float threshold = mat.Toughness * struckMass;
        float combined = energyTotal + body.State.AbsorbedEnergy;
        if (combined < threshold)
            return new FractureBudget(false, 0f, 0f, body.State.AbsorbedEnergy + energyTotal, totalArea);

        float eAvail = combined - threshold;
        float kFrac = Lerp(mat.KineticFraction, mat.KineticFraction * 0.3f, mat.Brittleness);
        // Only a fraction of the available energy creates fracture surface (rest → heat/sound).
        float surfEff = mat.SurfaceEfficiency > 0f ? mat.SurfaceEfficiency : 1f;
        return new FractureBudget(true, surfEff * (1f - kFrac) * eAvail, kFrac * eAvail, 0f, totalArea);
    }

    /// <summary>Snapshot the bond graph for a live fracture: effective bond strengths
    /// (with spin pre-stress) and the per-cell adjacency.</summary>
    public static (float[] eff, List<int>[] adj) PrepareGraph(in FracturableBody body, float spinOmega)
    {
        var eff = new float[body.Bonds.Length];
        ComputeEffStrengths(body.Cells, body.Bonds, spinOmega, body.Material.SpinPreStress, eff);
        var adj = BuildAdjacency(body.Cells.Length, body.Bonds);
        return (eff, adj);
    }

    /// <summary>Crack transmission (reach) for a brittleness, for seeding a CrackFront.</summary>
    public static float TransmissionFor(float brittleness) => Lerp(Transmission0, Transmission1, brittleness);

    /// <summary>Finalise a multi-frame fracture: connected components over the accumulated
    /// broken/pulverised state → fragment specs. Pulverised cells are NOT re-emitted as
    /// debris (the live system already vaporised them to dust as it propagated).</summary>
    public static FragmentSpec[] BuildResult(in FracturableBody body, in FractureInput input,
        bool[] broken, bool[] pulverized, Random rng, bool fling = true)
    {
        float totalArea = 0f;
        foreach (var c in body.Cells) totalArea += c.Area;
        int[] comp = ConnectedComponents(body.Cells.Length, body.Bonds, broken, pulverized, out int compCount);
        return BuildFragments(body, input, comp, compCount, broken, pulverized, totalArea, rng,
                              includePulverizedDebris: false, fling: fling);
    }

    // -------------------------------------------------------------------------
    // Spin pre-stress: weaken tangentially-oriented bonds, growing outward.
    // -------------------------------------------------------------------------
    private static void ComputeEffStrengths(Cell[] cells, Bond[] bonds, float omega, float spinCoeff, float[] eff)
    {
        if (bonds.Length == 0) return;

        float avg = 0f;
        foreach (var b in bonds) avg += b.Strength;
        avg /= bonds.Length;

        float rmax = 1e-3f;
        foreach (var c in cells) rmax = MathF.Max(rmax, c.Centroid.Length());

        float w2 = omega * omega;
        for (int k = 0; k < bonds.Length; k++)
        {
            Bond b = bonds[k];
            Vector2 m = (cells[b.A].Centroid + cells[b.B].Centroid) * 0.5f;   // CoM = origin
            float r = m.Length();
            if (r < 1e-3f) { eff[k] = b.Strength; continue; }

            Vector2 rad = m / r;
            Vector2 tan = new(-rad.Y, rad.X);
            Vector2 dir = cells[b.B].Centroid - cells[b.A].Centroid;
            float dl = dir.Length();
            if (dl > 1e-6f) dir /= dl;

            float tangentiality = MathF.Abs(Vector2.Dot(dir, tan));
            float profile = 0.3f + 0.7f * (r / rmax);
            float preStress = spinCoeff * avg * w2 * profile * tangentiality;
            eff[k] = MathF.Max(b.Strength * MinEffStrengthFrac, b.Strength - preStress);
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
    // Crack propagation — descending-energy flood spending a surface budget.
    // -------------------------------------------------------------------------
    private static void Propagate(
        Cell[] cells, Bond[] bonds, List<int>[] adj, float[] eff, bool[] broken, float[] energy,
        int struck, float startEnergy, float budget,
        Vector2 impactDirLocal, float directionality, float brittleness)
    {
        var front = CrackFront.Seed(energy, struck, startEnergy, budget,
                                    impactDirLocal, directionality, TransmissionFor(brittleness),
                                    float.PositiveInfinity);   // pulverisation handled separately below
        while (front.Active)
            FractureKernel.StepFront(front, cells, bonds, adj, eff, broken);
    }

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
            if (pulverized[i]) { comp[i] = -1; continue; }   // vaporised → in no fragment
            int r = Find(i);
            if (label[r] < 0) label[r] = count++;
            comp[i] = label[r];
        }
        return comp;
    }

    // -------------------------------------------------------------------------
    // Fragment construction
    // -------------------------------------------------------------------------
    private static FragmentSpec[] BuildFragments(
        in FracturableBody body, in FractureInput input,
        int[] comp, int compCount, bool[] broken, bool[] pulverized, float totalArea, Random rng,
        bool includePulverizedDebris = true, bool fling = true)
    {
        Cell[] cells = body.Cells;
        FractureProperties mat = body.Material;

        float cos = MathF.Cos(input.BodyRotation), sin = MathF.Sin(input.BodyRotation);
        Vector2 bodyPos = input.BodyPosition;   // local copy: can't capture an 'in' parameter
        Vector2 ToWorld(Vector2 local) => new(
            local.X * cos - local.Y * sin + bodyPos.X,
            local.X * sin + local.Y * cos + bodyPos.Y);

        float refArea = MathF.Max(1f, mat.GrainArea);   // one-cell reference size

        var groups = new List<int>[compCount];
        for (int c = 0; c < compCount; c++) groups[c] = new List<int>();
        for (int i = 0; i < cells.Length; i++) if (comp[i] >= 0) groups[comp[i]].Add(i);

        var result = new List<FragmentSpec>(compCount + 4);

        // --- surviving components → fragment bodies ---
        for (int c = 0; c < compCount; c++)
        {
            if (groups[c].Count == 0) continue;
            result.Add(BuildComponentSpec(body, input, groups[c], comp, c, broken, totalArea, rng,
                                          fling: fling, out _));
        }

        // --- pulverised cells → debris (the asteroid loses this material) ---
        if (includePulverizedDebris)
            for (int i = 0; i < cells.Length; i++)
            {
                if (!pulverized[i]) continue;
                Vector2 worldCentroid = ToWorld(cells[i].Centroid);
                var (linear, angular) = FragmentMotion(input, worldCentroid, cells[i].Area, refArea, mat, rng, debris: true);
                result.Add(new FragmentSpec
                {
                    WorldCentroid = worldCentroid,
                    Rotation = input.BodyRotation,
                    Linear = linear,
                    Angular = angular,
                    Mass = input.BodyMass * (cells[i].Area / totalArea),
                    Inertia = 0f,
                    Area = cells[i].Area,
                    IsDebris = true,
                });
            }

        return result.ToArray();
    }

    /// <summary>Builds one connected component into a re-centred fragment body (+ the
    /// old→new cell index remap, for carrying live crack fronts across a split). When
    /// <paramref name="fling"/> is false the piece keeps the parent's rigid motion only
    /// (no scatter impulse) — used for the component that continues cracking in place.</summary>
    private static FragmentSpec BuildComponentSpec(
        in FracturableBody body, in FractureInput input, List<int> idxs, int[] comp, int label,
        bool[] broken, float totalArea, Random rng, bool fling, out Dictionary<int, int> remap)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        FractureProperties mat = body.Material;
        float cos = MathF.Cos(input.BodyRotation), sin = MathF.Sin(input.BodyRotation);
        Vector2 bodyPos = input.BodyPosition;
        float refArea = MathF.Max(1f, mat.GrainArea);

        float area = 0f;
        Vector2 cen = Vector2.Zero;
        foreach (int ci in idxs) { area += cells[ci].Area; cen += cells[ci].Centroid * cells[ci].Area; }
        cen /= area;

        // A single detaching cell (fling=true, count=1) shrinks each vertex toward the
        // cell centroid by mat.DetachCellScale ± mat.DetachCellJitter so it no longer
        // perfectly fills the hole it left, preventing immediate deep overlap with the walls.
        bool shrinkCell = fling && idxs.Count == 1 && mat.DetachCellScale > 0f;
        var newCells = new Cell[idxs.Count];
        remap = new Dictionary<int, int>(idxs.Count);
        for (int k = 0; k < idxs.Count; k++)
        {
            int ci = idxs[k];
            remap[ci] = k;
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
            newCells[k] = new Cell { Local = local, Centroid = cellCen, Area = src.Area,
                                     DensityMult = src.DensityMult, BlastResist = src.BlastResist };
        }

        // Keep only UNBROKEN bonds within the component; broken ones become cracks (missing bonds).
        var newBonds = new List<Bond>();
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            Bond b = bonds[bi];
            if (!broken[bi] && comp[b.A] == label && comp[b.B] == label)
                newBonds.Add(new Bond { A = remap[b.A], B = remap[b.B], EdgeLength = b.EdgeLength, Strength = b.Strength });
        }

        float mass = input.BodyMass * (area / totalArea);
        float inertia = InertiaAbout(newCells, mass);
        Vector2 worldCentroid = new(cen.X * cos - cen.Y * sin + bodyPos.X,
                                    cen.X * sin + cen.Y * cos + bodyPos.Y);

        Vector2 linear; float angular;
        if (fling)
            (linear, angular) = FragmentMotion(input, worldCentroid, area, refArea, mat, rng, debris: false);
        else
        {
            // Continuer: rigid-body velocity of this piece's centre — no scatter (avoids
            // jitter when a body sheds several chunks).
            Vector2 r = worldCentroid - input.BodyPosition;
            linear = input.BodyLinear + new Vector2(-input.BodyAngular * r.Y, input.BodyAngular * r.X);
            angular = input.BodyAngular;
            if (ForceLog.On(ForceCat.Fling, ForceLog.CurrentBody))
                ForceLog.Write(ForceCat.Fling, ForceLog.CurrentBody,
                    $"continuer area{area:0.#}: linear = parent{ForceLog.V(input.BodyLinear)} + ωr{ForceLog.V(linear - input.BodyLinear)} = " +
                    $"{ForceLog.V(linear)} (no scatter) | angular = parent{angular:0.##}");
        }

        return new FragmentSpec
        {
            Body = new FracturableBody
            {
                Cells = newCells,
                Bonds = newBonds.ToArray(),
                Material = mat,
                State = new FractureState { AbsorbedEnergy = 0f, RngSeed = (uint)rng.Next() },
            },
            WorldCentroid = worldCentroid,
            Rotation = input.BodyRotation,
            Linear = linear,
            Angular = angular,
            Mass = mass,
            Inertia = inertia,
            Area = area,
            IsDebris = idxs.Count == 1 && area < mat.MinFragmentArea,
        };
    }

    /// <summary>Counts connected components over the surviving (unbroken, unpulverised)
    /// bond graph — used each iteration to detect when a body has split.</summary>
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

    /// <summary>
    /// Mid-fracture split: partition a body that has broken into ≥2 components into fresh
    /// re-centred pieces. The piece holding the most live front energy keeps the parent's
    /// motion (continuer); the rest get a fling impulse. Each piece that still has an
    /// active front carries a remapped FractureProcess so it keeps cracking identically to
    /// if it had stayed attached (no discarded energy). Pulverised cells already dusted live.
    /// </summary>
    public static List<LivePiece> SplitLive(in FracturableBody body, in FractureInput input,
                                            in FractureProcess proc, Random rng)
    {
        Cell[] cells = body.Cells;
        Bond[] bonds = body.Bonds;
        bool[] broken = proc.Broken, pulverized = proc.Pulverized;

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

            var spec = BuildComponentSpec(body, input, groups[c], comp, c, broken, totalArea, rng,
                                          fling: c != continuer, out var remap);

            // Carry any active fronts whose wavefront reaches into this component.
            var subFronts = new List<CrackFront>();
            foreach (var f in proc.Fronts)
            {
                if (!f.Active) continue;
                float total = 0f, here = 0f;
                foreach (int fc in f.Frontier) { float e = f.Energy[fc]; total += e; if (comp[fc] == c) here += e; }
                if (here <= 0f || total <= 0f) continue;
                var sub = PartitionFront(f, comp, c, remap, spec.Body.Cells.Length, here / total);
                if (sub != null) subFronts.Add(sub);
            }

            FractureProcess? sub2 = null;
            if (subFronts.Count > 0)
            {
                var (eff, adj) = PrepareGraph(spec.Body, input.BodyAngular);
                sub2 = new FractureProcess
                {
                    Fronts = subFronts,
                    Broken = new bool[spec.Body.Bonds.Length],     // cracks already baked as missing bonds
                    Pulverized = new bool[spec.Body.Cells.Length],
                    Eff = eff,
                    Adj = adj,
                    ImpactDir = proc.ImpactDir,
                    ImpactPointWorld = proc.ImpactPointWorld,
                    MomentumKick = proc.MomentumKick,
                    EjectSpeed = proc.EjectSpeed,
                    ImpactSpin = proc.ImpactSpin,
                    Directionality = proc.Directionality,
                    StepsPerIteration = proc.StepsPerIteration,
                    FramesPerIteration = proc.FramesPerIteration,
                    FrameCounter = 0,
                    DetachOnSplit = true,
                    Done = false,
                };
            }
            pieces.Add(new LivePiece { Spec = spec, Process = sub2 });
        }
        return pieces;
    }

    /// <summary>Carve one component's slice out of a front: its cells' energy (remapped to
    /// the new compact indices), the frontier cells that fall in it, and a proportional
    /// share of the remaining budget. Returns null if no wavefront reaches the component.</summary>
    private static CrackFront? PartitionFront(CrackFront f, int[] comp, int label,
                                              Dictionary<int, int> remap, int newCellCount, float share)
    {
        var energy = new float[newCellCount];
        var processed = new float[newCellCount];
        for (int i = 0; i < newCellCount; i++) processed[i] = -1f;
        foreach (var kv in remap) { energy[kv.Value] = f.Energy[kv.Key]; processed[kv.Value] = f.Processed[kv.Key]; }

        var frontier = new List<int>();
        foreach (int fc in f.Frontier)
            if (comp[fc] == label && remap.TryGetValue(fc, out int nk)) frontier.Add(nk);
        if (frontier.Count == 0) return null;

        var sub = new CrackFront
        {
            Energy = energy,
            Processed = processed,
            Budget = f.Budget * share,
            ImpactDirLocal = f.ImpactDirLocal,   // direction is translation-invariant → still valid
            Directionality = f.Directionality,
            Transmission = f.Transmission,
            BlastThresh = f.BlastThresh,
        };
        sub.Frontier.AddRange(frontier);
        return sub;
    }

    /// <summary>Fragment linear + angular velocity: parent drift, ω×r carry-over,
    /// directional push along the shot, radial scatter, and an impact-induced shear
    /// spin (deterministic per side, not random).</summary>
    private static (Vector2 linear, float angular) FragmentMotion(
        in FractureInput input, Vector2 worldCentroid, float area, float refArea,
        FractureProperties mat, Random rng, bool debris)
    {
        Vector2 r = worldCentroid - input.BodyPosition;
        Vector2 rotVel = new(-input.BodyAngular * r.Y, input.BodyAngular * r.X);   // ω × r
        Vector2 spread = worldCentroid - input.ImpactPointWorld;
        float sl = spread.Length();
        spread = sl > 1e-4f ? spread / sl : RandomUnit(rng);

        float boost = MathF.Sqrt(refArea / MathF.Max(area, refArea * 0.1f));        // smaller → faster
        float spd = input.EjectSpeed * boost * (0.6f + 0.8f * (float)rng.NextDouble()) * (debris ? 1.6f : 1f);
        // MomentumKick is applied to the whole body at impact (FractureService.BeginFracture)
        // and inherited here via BodyLinear, so it is NOT added again per fragment.
        Vector2 linear = input.BodyLinear + rotVel + spread * spd;

        // Shear spin from the hit: off-axis fragments rotate consistently with the shot
        // (same side → same sign) — cross(spreadDir, shotDir). Plus a little variety.
        float shear = spread.X * input.ImpactDir.Y - spread.Y * input.ImpactDir.X;
        float spinRand = (float)(rng.NextDouble() - 0.5) * Lerp(0.4f, 1.2f, mat.Brittleness);
        float angular = input.BodyAngular + input.ImpactSpin * shear + spinRand;

        if (ForceLog.On(ForceCat.Fling, ForceLog.CurrentBody))
            ForceLog.Write(ForceCat.Fling, ForceLog.CurrentBody,
                $"{(debris ? "debris" : "frag")} area{area:0.#}: linear = parent{ForceLog.V(input.BodyLinear)} + " +
                $"ωr{ForceLog.V(rotVel)} + spread{ForceLog.V(spread)}·spd{spd:0.#}(eject{input.EjectSpeed:0.#}·boost{boost:0.##}) = {ForceLog.V(linear)} | " +
                $"angular = parent{input.BodyAngular:0.##} + spin{input.ImpactSpin:0.##}·shear{shear:0.##} + rand{spinRand:0.##} = {angular:0.##}");
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
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Vector2 RotateInv(Vector2 v, float rot)
    {
        float c = MathF.Cos(rot), s = MathF.Sin(rot);
        return new Vector2(v.X * c + v.Y * s, -v.X * s + v.Y * c);
    }

    private static Vector2 RandomUnit(Random rng)
    {
        float a = (float)(rng.NextDouble() * Math.PI * 2.0);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }
}
