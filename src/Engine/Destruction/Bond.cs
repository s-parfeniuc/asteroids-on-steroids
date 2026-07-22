namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// A cohesive bond between two adjacent cells that share a Voronoi edge. The crack
/// propagation spends an energy budget breaking bonds; connected-component analysis
/// over the surviving bonds yields the fragments.
/// </summary>
public struct Bond
{
    public int   A;            // cell index
    public int   B;            // cell index
    public float EdgeLength;   // length of the shared edge
    public float Strength;     // stress to break = EdgeLength × material Toughness × StrengthMult.
                               // Computed ONCE at tessellation and never mutated.
    public float StrengthMult; // per-bond strength multiplier (geometric mean of the cells'
                               // cluster bond mults); kept so live Toughness edits preserve clusters
    public float Stress;       // runtime damage accumulator: delivered stress sums here; the bond
                               // breaks at Stress ≥ Strength and decays by RelaxRate (StressRelaxSystem).
                               // Lets repeated hits accumulate (sustained fire cracks a tough body).
    public bool  Broken;       // set once the bond breaks (Stress ≥ effective strength) and never
                               // cleared — a permanent crack. Distinct from Stress, which relaxes;
                               // spin can break a bond below Strength, so this is the true break signal.
}
