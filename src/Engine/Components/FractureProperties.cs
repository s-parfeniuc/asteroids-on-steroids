namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Immutable material description for a fracturable (cell/bond) body. Shareable
/// across entities; runtime mutable state lives in FractureState.
///
/// See docs/destruction_engine_spec.md §4.3.
/// </summary>
public struct FractureProperties
{
    /// <summary>
    /// Sets bond strength: bond.Strength = sharedEdgeLength × Toughness × StrengthMult — the
    /// stress a bond accumulates before it breaks. High toughness ⇒ a single hit can't crack it;
    /// repeated hits must accumulate (see <see cref="RelaxRate"/>).
    /// </summary>
    public float Toughness;

    /// <summary>Coefficient of restitution for the dissipated-energy: the fraction of contact
    /// energy that bounces back rather than coupling into the fracture is e²; only (1 − e²)
    /// becomes the input energy E. Bouncier materials (high e) fracture less.</summary>
    public float Restitution;

    /// <summary>Rate (stress per second) at which each bond's accumulated <see cref="Bond.Stress"/>
    /// relaxes when not being hit (StressRelaxSystem). Higher = hits must land faster to ever
    /// crack it; 0 = stress never heals. The "sustained DPS demanded" knob.</summary>
    public float RelaxRate;

    /// <summary>
    /// [0 = ductile, 1 = brittle/glass]. The central lever: at each cell the energy splits into
    /// dump = (1−Brittleness)·e (stays local as fling/vaporize) and transmit = Brittleness·e
    /// (travels onward as cracks). 0 = blunt crater + big fling, short cracks; 1 = long thin cracks.
    /// </summary>
    public float Brittleness;

    /// <summary>Cells/second the crack front advances (multi-frame pacing). Replaces the old
    /// global crackSteps/crackFrames — different materials crack at different speeds.</summary>
    public float CrackSpeed;

    /// <summary>Target cell area (px²) at tessellation — the material "grain".
    /// Constant grain ⇒ larger bodies get proportionally more cells.</summary>
    public float GrainArea;

    /// <summary>Cell/fragment area (px²) below which a piece becomes visual debris
    /// rather than a live collidable body.</summary>
    public float MinFragmentArea;

    /// <summary>Mass per unit area. Cell mass = Area × DensityMult × Density.</summary>
    public float Density;

    /// <summary>Vaporise threshold per unit cell mass: a cell pulverises once accumulated comminution
    /// (blast·dump, summed over hits) reaches CellToughness × Area × DensityMult × Density. Higher ⇒
    /// harder to vaporise / more sustained blast to crater.</summary>
    public float CellToughness;

    /// <summary>Gain on how strongly body spin ω multiplies the stress delivered to tangential
    /// rim bonds: spinFactor = clamp(SpinPreStress·ω²·(0.3+0.7·r/rmax)·tangentiality, 0, SpinCap).
    /// Bond Strength is untouched — spin amplifies the delivered stress, so a fast spinner
    /// shatters from a lighter hit.</summary>
    public float SpinPreStress;

    /// <summary>
    /// [0 = isotropic shatter, 1 = clean cleavage]. How strongly the material's own
    /// grain/crystal structure guides crack propagation along stress lines rather than
    /// spreading omnidirectionally. Combined with the weapon's Directionality at impact.
    /// Glass cleaves (high), rock shatters (medium), metal tears ductilely (low).
    /// </summary>
    public float CrackDirectionality;

    /// <summary>Mean scale applied to each vertex of a single isolated cell when it
    /// detaches as its own entity. Vertices contract toward the cell centroid by this
    /// factor (e.g. 0.90 = 10% smaller), with ±DetachCellJitter variance per vertex,
    /// so the cell no longer perfectly fills the hole it left and avoids immediate deep
    /// overlap with the surrounding asteroid walls.</summary>
    public float DetachCellScale;
    /// <summary>Per-vertex ± variance on DetachCellScale (e.g. 0.02 = ±2%).</summary>
    public float DetachCellJitter;
}
