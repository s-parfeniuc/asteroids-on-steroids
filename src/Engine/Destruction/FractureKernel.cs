using System.Numerics;
using AsteroidsEngine.Engine.Components;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>Global, model-shaping fracture scalars (not worth a per-material knob). Set once
/// from config by the game layer; default to the values the HTML prototype was tuned with.</summary>
public static class FractureTuning
{
    public static float EnergyScale         = 0.0001f; // physical ½·m·v² → fracture-energy units (one global conversion)
    public static float ReachMin            = 0.1f;   // transmit fraction at brittleness 0 (lerp floor)
    public static float ReachMax            = 0.96f;  // transmit fraction at brittleness 1 (lerp ceiling)
    public static float VaporEff            = 0.4f;   // blast-wave penetration: surplus continued outward vs lost to heat
    public static float BreakPerp           = 1.0f;   // 0 = break ALONG flow (tunnel) … 1 = PERPENDICULAR (cleave)
    public static float AlignExponent       = 1.6f;   // directional cone sharpness
    public static float SpinCap             = 4.0f;   // max spin stress multiplier
    public static float FlingScale          = 140f;   // fling energy → fragment speed
    public static float FragmentSpeedMax    = 600f;   // clamp on a fragment's fling speed (px/s)
    public static float TumbleScale         = 220f;   // fling-asymmetry → fragment spin gain
    public static float FragmentSpinMax     = 3.5f;   // clamp on a fragment's spin (rad/s)
    public static float SpinProfileBase     = 0.3f;   // spin pre-stress at the centre; rises to 1.0 at the rim

    public static float SplitStressInherit  = 1.0f;   // fraction of Cell.Damage / Bond.Stress fragments keep on split (0 = old full-heal)

    // ── Impact-velocity → crack-speed coupling ─────────────────────────────────
    public static float CrackSpeedRefVelocity = 600f;  // px/s at which a hit cracks at the material's base CrackSpeed
    public static float CrackSpeedVelExponent = 0.5f;  // curve: 0 = velocity-independent, 1 = linear in speed
    public static float CrackSpeedMultMin     = 0.25f; // slowest a crawl-speed impact can crack (× material)
    public static float CrackSpeedMultMax     = 4f;    // fastest a screaming impact can crack (× material)

    /// <summary>Crack-speed multiplier for a hit at <paramref name="normalSpeed"/>: fast collisions
    /// fracture faster than slow-but-heavy ones even at equal energy. ≤0 (speed unknown) = 1.</summary>
    public static float CrackSpeedFactor(float normalSpeed)
    {
        if (normalSpeed <= 0f || CrackSpeedVelExponent <= 0f) return 1f;
        float g = MathF.Pow(normalSpeed / MathF.Max(1f, CrackSpeedRefVelocity), CrackSpeedVelExponent);
        return Math.Clamp(g, CrackSpeedMultMin, CrackSpeedMultMax);
    }
}

/// <summary>
/// One impact's live crack field over a body's bond graph. A cell receiving energy splits it
/// into named channels that sum to the input (break / vaporize / fling / transmit) — nothing
/// vanishes. Single field per hit; multi-frame fracture advances several co-propagating fronts
/// a few pops per frame, all sharing the body's broken / pulverized / bond-stress / fling state.
/// </summary>
public sealed class CrackFront
{
    public float[] Energy    = System.Array.Empty<float>();  // per cell, incoming energy (this front)
    public float[] Processed = System.Array.Empty<float>();  // -1 = not processed; ≥0 = processed at that energy
    public int[]   Parent    = System.Array.Empty<int>();    // per cell, who delivered its energy → local flow dir
    public readonly List<int> Frontier = new();
    public Vector2 ImpactDirLocal;     // flow at the struck cell (no parent)
    public float   Directionality;     // effective (weapon + material)/2
    public float   Brittleness;        // material: transmit vs dump split
    public float   BlastFraction;      // weapon: vaporize budget fraction
    internal readonly List<(int bk, int j, float wc, float wd)> Out = new();   // reused scratch

    // Per-FRONT pacing (material CrackSpeed × the hit's velocity factor): each hit's crack advances
    // on its own clock, so a fast bullet's front races while a slow grinding contact's front creeps —
    // even when both are live on the same body.
    public int StepsPerIteration  = 1;
    public int FramesPerIteration = 1;
    public int FrameCounter;

    public bool Active => Frontier.Count > 0;

    /// <summary>Seed a front at the struck cell over the given (per-front) energy array.
    /// <paramref name="normalSpeed"/> scales the material's CrackSpeed via
    /// <see cref="FractureTuning.CrackSpeedFactor"/> (≤0 = material base pace).</summary>
    public static CrackFront Seed(float[] energy, int struck, float startEnergy,
        Vector2 impactDirLocal, float directionality, float brittleness, float blastFraction,
        float crackSpeed, float normalSpeed = 0f)
    {
        var timing = FractureTiming.FromCrackSpeed(
            crackSpeed * FractureTuning.CrackSpeedFactor(normalSpeed), FractureTiming.DefaultFixedDt);
        var f = new CrackFront
        {
            Energy = energy,
            Processed = new float[energy.Length],
            Parent = new int[energy.Length],
            ImpactDirLocal = impactDirLocal,
            Directionality = directionality,
            Brittleness = brittleness,
            BlastFraction = blastFraction,
            StepsPerIteration = timing.StepsPerIteration,
            FramesPerIteration = timing.FramesPerIteration,
        };
        for (int i = 0; i < f.Processed.Length; i++) { f.Processed[i] = -1f; f.Parent[i] = -1; }
        energy[struck] = startEnergy;
        f.Frontier.Add(struck);
        return f;
    }
}

/// <summary>The pure conservative propagation kernel (ported 1:1 from the HTML prototype).</summary>
public static class FractureKernel
{
    /// <summary>Advance a front by one frontier-pop: process the highest-energy cell — vaporise it
    /// (carving the crater) or keep its local fling, then route the leftover `transmit` outward,
    /// dividing it across intact bonds by alignment and accumulating stress until bonds break.
    /// <paramref name="spinMul"/>[bk] = 1+spinFactor (precomputed); <paramref name="flingE"/> and
    /// <paramref name="pulverized"/> are SHARED across co-propagating fronts; newly-vaporised cell
    /// indices are appended to <paramref name="pulvOut"/> for the system to dust.</summary>
    public static void StepFront(CrackFront f, Cell[] cells, Bond[] bonds, List<int>[] adj,
        float[] spinMul, bool[] broken, bool[] pulverized, float[] flingE, in FractureProperties mat, List<int> pulvOut)
    {
        var frontier = f.Frontier;
        if (frontier.Count == 0) return;
        float[] energy = f.Energy, processed = f.Processed;

        // pop the highest-energy frontier cell (descending order ⇒ each cell processed once at its peak)
        int mi = 0;
        for (int k = 1; k < frontier.Count; k++)
            if (energy[frontier[k]] > energy[frontier[mi]]) mi = k;
        int i = frontier[mi];
        float e = energy[i];
        frontier.RemoveAt(mi);

        if (processed[i] >= 0f) { RouteFling(i, e, pulverized, flingE); return; }   // settled: surplus → fling
        processed[i] = e;

        // Brittleness ALWAYS sets the outward/local split (lerp floors keep both ends non-degenerate).
        // Local dump → fling (1-blast) + vaporiseEnergy (blast). Vaporisation accumulates comminution
        // per-cell toward the threshold (fatigue); once reached the cell pulverises and the surplus
        // continues OUTWARD by VaporEff (penetration), the rest lost to heat.
        float transmit;
        if (pulverized[i])
        {
            transmit = e;   // already dust (another front) → pure conduit
        }
        else
        {
            float tf = Lerp(FractureTuning.ReachMin, FractureTuning.ReachMax, f.Brittleness);
            transmit = tf * e;
            float dump = e - transmit;

            flingE[i] += (1f - f.BlastFraction) * dump;       // 1.2 local shove

            float vaporiseEnergy = f.BlastFraction * dump;    // 1.1 pulverisation (0 at blast=0)
            if (vaporiseEnergy > 0f)
            {
                float threshold = mat.CellToughness * cells[i].Area * cells[i].DensityMult * mat.Density;
                float comminution = MathF.Min(vaporiseEnergy, MathF.Max(0f, threshold - cells[i].Damage));
                cells[i].Damage += comminution;               // accumulates (fatigue); persists across hits
                float surplus = vaporiseEnergy - comminution; // > 0 only once the threshold is reached
                if (cells[i].Damage >= threshold)
                {
                    pulverized[i] = true; pulvOut.Add(i);
                    transmit += FractureTuning.VaporEff * surplus;  // penetration; (1-VaporEff)·surplus → heat (lost)
                }
            }
        }

        // Local flow direction: from the cell that delivered this energy (impact dir at the struck cell).
        Vector2 flow = f.ImpactDirLocal;
        int par = f.Parent[i];
        if (par >= 0)
        {
            Vector2 fd = cells[i].Centroid - cells[par].Centroid;
            float fl = fd.Length();
            if (fl > 1e-6f) flow = fd / fl;
        }

        // Each intact bond splits its share by ONE formula: conduct weight wc (alignment, directionality
        // shapes the cone) + damage weight wd (perpendicularity, breakPerp blends ∥↔⊥). Normalised over W.
        var outBonds = f.Out;
        outBonds.Clear();
        float W = 0f, sumWc = 0f;
        foreach (int bk in adj[i])
        {
            if (broken[bk]) continue;
            int j = bonds[bk].A == i ? bonds[bk].B : bonds[bk].A;
            if (pulverized[j]) continue;
            Vector2 d = cells[j].Centroid - cells[i].Centroid;
            float dl = d.Length();
            if (dl > 1e-6f) d /= dl;
            float align = Vector2.Dot(d, flow);
            float aa = MathF.Abs(align);
            float wc = Lerp(1f, MathF.Pow(MathF.Max(0f, align), FractureTuning.AlignExponent), f.Directionality);
            float wd = Lerp(aa, 1f - aa, FractureTuning.BreakPerp);
            outBonds.Add((bk, j, wc, wd));
            W += wc + wd; sumWc += wc;
        }
        // isolated/last cell: transmit has nowhere to go → becomes this cell's fling (disperses if vapor)
        if (outBonds.Count == 0 || W <= 1e-9f) { RouteFling(i, transmit, pulverized, flingE); return; }

        // DAMAGE pass: each bond is directed transmit·wd/W, but a break only CONSUMES what it needs
        // (its Strength = surface energy); the over-damage is recovered and continues forward.
        // Spin doesn't change a bond's Strength — instead it multiplies the DAMAGE each unit of energy
        // inflicts, so a spinning body's bonds accumulate stress faster (energy stays conserved: the
        // energy consumed is still what physically breaks the bond).
        float recovered = 0f;
        foreach (var (bk, j, wc, wd) in outBonds)
        {
            float absorb = transmit * wd / W;
            float str    = bonds[bk].Strength;                       // fixed; spin no longer scales it
            float spin   = MathF.Max(1e-4f, spinMul[bk]);            // damage multiplier
            float need     = MathF.Max(0f, (str - bonds[bk].Stress) / spin);   // energy still needed
            float consumed = MathF.Min(absorb, need);
            bonds[bk].Stress += consumed * spin;                     // damage = energy × spin
            if (bonds[bk].Stress >= str - 1e-4f) { broken[bk] = true; bonds[bk].Broken = true; }
            recovered += absorb - consumed;
        }
        // FORWARD = conduct budget + recovered over-damage, distributed by conduct weight.
        float fwdTotal = transmit * (sumWc / W) + recovered;
        if (sumWc > 1e-9f)
        {
            foreach (var (bk, j, wc, wd) in outBonds)
            {
                float fwd = fwdTotal * wc / sumWc;
                if (fwd <= 0f) continue;
                if (processed[j] < 0f && fwd > energy[j]) { energy[j] = fwd; f.Parent[j] = i; frontier.Add(j); }
                else RouteFling(j, fwd, pulverized, flingE);
            }
        }
        else RouteFling(i, fwdTotal, pulverized, flingE);
    }

    private static void RouteFling(int j, float amount, bool[] pulverized, float[] flingE)
    {
        if (amount <= 0f) return;
        if (!pulverized[j]) flingE[j] += amount;   // a vaporised cell just disperses it as dust
    }

    internal static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
