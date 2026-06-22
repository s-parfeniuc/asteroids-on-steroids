using System.Numerics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// One impact's live crack field: a descending-energy frontier spending a surface
/// budget across the bond graph. Single-frame fracture drains a front in one call;
/// multi-frame fracture advances several fronts a few steps per iteration, all
/// sharing the body's broken-bond state so independent hits co-propagate and fuse
/// where they meet (docs/destruction_engine_spec.md §4.6).
/// </summary>
public sealed class CrackFront
{
    public float[] Energy = System.Array.Empty<float>();    // per cell, this front's crack field
    public float[] Processed = System.Array.Empty<float>(); // per cell, highest energy already spent there
    public readonly List<int> Frontier = new();
    public float Budget;
    public Vector2 ImpactDirLocal;
    public float Directionality;
    public float Transmission;
    public float BlastThresh;     // a cell whose energy exceeds this vaporises (FractureCrackSystem applies it)
    internal readonly List<(int bond, int j, float w)> Out = new();   // reused scratch

    public bool Active => Budget > 0f && Frontier.Count > 0;

    /// <summary>Seed a front at the struck cell over the given (per-front) energy array.</summary>
    public static CrackFront Seed(float[] energy, int struck, float startEnergy, float budget,
        Vector2 impactDirLocal, float directionality, float transmission, float blastThresh)
    {
        var f = new CrackFront
        {
            Energy = energy,
            Processed = new float[energy.Length],
            Budget = budget,
            ImpactDirLocal = impactDirLocal,
            Directionality = directionality,
            Transmission = transmission,
            BlastThresh = blastThresh,
        };
        for (int i = 0; i < f.Processed.Length; i++) f.Processed[i] = -1f;
        energy[struck] = startEnergy;
        f.Frontier.Add(struck);
        return f;
    }
}

/// <summary>The pure propagation kernel shared by single- and multi-frame fracture,
/// so both paths break bonds by identical physics.</summary>
public static class FractureKernel
{
    public const float BoostCap = 3f;       // max directional concentration per bond
    public const float DirExponent = 1.6f;

    /// <summary>Advance a front by one frontier-pop: process the highest-energy cell,
    /// break its outgoing bonds in alignment order, hand the surplus to neighbours.</summary>
    public static void StepFront(CrackFront f, Cell[] cells, Bond[] bonds, List<int>[] adj, float[] eff, bool[] broken)
    {
        var frontier = f.Frontier;
        if (frontier.Count == 0 || f.Budget <= 0f) return;
        float[] energy = f.Energy, processed = f.Processed;

        int mi = 0;
        for (int k = 1; k < frontier.Count; k++)
            if (energy[frontier[k]] > energy[frontier[mi]]) mi = k;
        int i = frontier[mi];
        float e = energy[i];
        frontier.RemoveAt(mi);
        if (e <= processed[i]) return;   // stale / already processed at ≥ this energy
        processed[i] = e;

        var outBonds = f.Out;
        outBonds.Clear();
        float sumW = 0f;
        foreach (int bk in adj[i])
        {
            if (broken[bk]) continue;
            int j = bonds[bk].A == i ? bonds[bk].B : bonds[bk].A;
            Vector2 d = cells[j].Centroid - cells[i].Centroid;
            float dl = d.Length();
            if (dl > 1e-6f) d /= dl;
            float align = Vector2.Dot(d, f.ImpactDirLocal);
            float w = Lerp(1f, MathF.Pow(MathF.Max(0f, align), DirExponent), f.Directionality);
            outBonds.Add((bk, j, w));
            sumW += w;
        }
        if (outBonds.Count == 0) return;

        outBonds.Sort((x, y) => y.w.CompareTo(x.w));      // spend on most-aligned first
        float norm = outBonds.Count / MathF.Max(sumW, 1e-6f);   // mean weight 1: steer, don't attenuate

        foreach (var (bk, j, w) in outBonds)
        {
            if (f.Budget <= 0f) break;
            float deliver = e * MathF.Min(w * norm, BoostCap);
            if (deliver > eff[bk])
            {
                broken[bk] = true;
                f.Budget -= eff[bk];
                float tr = (deliver - eff[bk]) * f.Transmission;
                if (tr > e) tr = e;                          // child never exceeds parent
                if (tr > energy[j]) { energy[j] = tr; frontier.Add(j); }
            }
        }
    }

    internal static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
