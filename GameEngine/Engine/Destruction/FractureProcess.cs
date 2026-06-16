using System.Numerics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>Pacing for multi-frame crack propagation. Crack velocity =
/// StepsPerIteration / (FramesPerIteration · fixedDt) frontier-pops per second.</summary>
public struct FractureTiming
{
    public int StepsPerIteration;    // frontier-pops advanced per iteration
    public int FramesPerIteration;   // fixed-steps to wait between iterations
    public bool DetachOnSplit;       // true = pieces fall off mid-spread; false = finalise whole body at end

    public static FractureTiming Default => new() { StepsPerIteration = 2, FramesPerIteration = 1, DetachOnSplit = true };
}

/// <summary>One piece produced by a mid-fracture split: a fragment body and, if it is
/// still cracking, the FractureProcess to attach to it on spawn so it keeps propagating.</summary>
public sealed class LivePiece
{
    public FragmentSpec Spec;
    public FractureProcess? Process;   // non-null → continues cracking
}

/// <summary>
/// Live, multi-frame fracture state on a body whose cracks are still spreading. Holds
/// one or more co-propagating <see cref="CrackFront"/>s (one per hit) sharing the
/// accumulating Broken/Pulverized masks, plus the snapshot needed to fling the pieces
/// when the process finalises. <see cref="FractureCrackSystem"/> advances it and removes
/// it (via entity destruction on completion). The single-frame path does not use this.
/// </summary>
public struct FractureProcess
{
    public List<CrackFront> Fronts;
    public bool[] Broken;        // over body.Bonds — grows as cracks spread
    public bool[] Pulverized;    // over body.Cells — grows as cells vaporise
    public float[] Eff;          // effective bond strengths (spin pre-stress snapshot)
    public List<int>[] Adj;      // per-cell bond adjacency

    // Fling snapshot (latest hit) — fed to FractureSimulator.BuildResult on finalise.
    public Vector2 ImpactDir;
    public Vector2 ImpactPointWorld;
    public Vector2 MomentumKick;
    public float EjectSpeed;
    public float ImpactSpin;
    public float Directionality;

    // Pacing.
    public int StepsPerIteration;
    public int FramesPerIteration;
    public int FrameCounter;
    public bool DetachOnSplit;   // detach pieces as soon as they separate, vs finalise whole body at end

    public bool Done;            // set on finalise so the system ignores it until the entity is destroyed
}
