using System.Numerics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>Pacing for multi-frame crack propagation, derived from the material's CrackSpeed
/// (cells/sec): crack velocity = StepsPerIteration / (FramesPerIteration · fixedDt) pops/sec.</summary>
public struct FractureTiming
{
    public const float DefaultFixedDt = 1f / 120f;   // the game's fixed timestep

    public int StepsPerIteration;    // frontier-pops advanced per iteration
    public int FramesPerIteration;   // fixed-steps to wait between iterations

    public static FractureTiming Default => new() { StepsPerIteration = 2, FramesPerIteration = 1 };

    /// <summary>Map a material CrackSpeed (cells/sec) to integer step/frame pacing at the given
    /// fixed timestep. Fast materials advance several pops per step; slow ones wait frames.</summary>
    public static FractureTiming FromCrackSpeed(float crackSpeed, float fixedDt)
    {
        float popsPerStep = MathF.Max(0.0001f, crackSpeed) * MathF.Max(1e-4f, fixedDt);
        if (popsPerStep >= 1f)
            return new FractureTiming { StepsPerIteration = (int)MathF.Round(popsPerStep), FramesPerIteration = 1 };
        return new FractureTiming { StepsPerIteration = 1, FramesPerIteration = Math.Max(1, (int)MathF.Round(1f / popsPerStep)) };
    }
}

/// <summary>One piece produced by a mid-fracture split: a fragment body and, if it is
/// still cracking, the FractureProcess to attach to it on spawn so it keeps propagating.</summary>
public sealed class LivePiece
{
    public FragmentSpec Spec;
    public FractureProcess? Process;   // non-null → continues cracking
}

/// <summary>
/// Live, multi-frame fracture state on a body whose cracks are still spreading. Holds one or
/// more co-propagating <see cref="CrackFront"/>s (one per hit) sharing the accumulating
/// Broken / Pulverized / FlingE state plus the persistent per-bond Stress on the body's bonds.
/// <see cref="FractureCrackSystem"/> advances it and removes it on completion.
/// </summary>
public struct FractureProcess
{
    public List<CrackFront> Fronts;
    public bool[]  Broken;        // over body.Bonds — grows as cracks spread
    public bool[]  Pulverized;    // over body.Cells — grows as cells vaporise
    public bool[]  Emitted;       // over body.Cells — which pulverised cells have been dusted (event sent)
    public float[] FlingE;        // over body.Cells — accumulated fragment kinetic energy (shared)
    public float[] SpinMul;       // over body.Bonds — 1+spinFactor, precomputed from body spin ω
    public List<int>[] Adj;       // per-cell bond adjacency

    // Fling snapshot (latest hit) — fed to fragment construction on finalise.
    public Vector2 ImpactDir;
    public Vector2 ImpactPointWorld;
    public float   Directionality;

    // Pacing lives on each CrackFront (material CrackSpeed × the hit's velocity factor), so every
    // hit's crack advances on its own clock rather than inheriting the first hit's pace.

    public bool Done;            // set on finalise so the system ignores it until the entity is destroyed
}
