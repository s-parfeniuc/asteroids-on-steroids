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
    /// Energy per unit bond length to break a bond (bond.Strength = sharedEdgeLength ×
    /// Toughness). Higher = harder to fracture. (Abstract units; calibrated against
    /// the impact energy budget — see spec §9.)
    /// </summary>
    public float Toughness;

    /// <summary>
    /// [0 = ductile, 1 = brittle/glass]. Controls how far crack energy propagates
    /// through the bond graph (brittle = far → shatter; ductile = local chip) and
    /// the kinetic/surface energy split.
    /// </summary>
    public float Brittleness;

    /// <summary>Target cell area (px²) at tessellation — the material "grain".
    /// Constant grain ⇒ larger bodies get proportionally more cells.</summary>
    public float GrainArea;

    /// <summary>Cell/fragment area (px²) below which a piece becomes visual debris
    /// rather than a live collidable body.</summary>
    public float MinFragmentArea;

    /// <summary>Mass per unit area.</summary>
    public float Density;

    /// <summary>
    /// Fraction of the available fracture energy converted to fragment kinetic energy
    /// (the remainder creates fracture surface) at Brittleness = 0. Brittle materials
    /// put more into surface (more cracks), ductile more into fling.
    /// </summary>
    public float KineticFraction;

    /// <summary>Fraction of available energy that becomes fracture surface (the rest
    /// dissipates as heat/sound). The master "how much shatters" coefficient — a
    /// material property (a tough/ductile rock dissipates more). ≤0 ⇒ treated as 1.</summary>
    public float SurfaceEfficiency;

    /// <summary>How strongly the body's spin pre-weakens its tangentially-oriented
    /// bonds (centrifugal pre-stress). A material property of how spin loads it.</summary>
    public float SpinPreStress;

    /// <summary>
    /// [0 = isotropic shatter, 1 = clean cleavage]. How strongly the material's own
    /// grain/crystal structure guides crack propagation along stress lines rather than
    /// spreading omnidirectionally. Combined with the weapon's Directionality at impact.
    /// Glass cleaves (high), rock shatters (medium), metal tears ductilely (low).
    /// </summary>
    public float CrackDirectionality;

    /// <summary>Crack-propagation speed for multi-frame fracture: frontier-pops per
    /// iteration (≈ terminal crack velocity). Brittle materials race (glass shatters
    /// near-instantly), ductile ones tear slowly. The game may override it live.</summary>
    public float CrackSpeed;

    /// <summary>Mean scale applied to each vertex of a single isolated cell when it
    /// detaches as its own entity. Vertices contract toward the cell centroid by this
    /// factor (e.g. 0.90 = 10% smaller), with ±DetachCellJitter variance per vertex,
    /// so the cell no longer perfectly fills the hole it left and avoids immediate deep
    /// overlap with the surrounding asteroid walls.</summary>
    public float DetachCellScale;
    /// <summary>Per-vertex ± variance on DetachCellScale (e.g. 0.02 = ±2%).</summary>
    public float DetachCellJitter;

    // ---- Presets (relative values; calibrate the absolute budget per spec §9) ----

    public static readonly FractureProperties Glass = new()
    { Toughness =  6f, Brittleness = 1.00f, GrainArea =  600f, MinFragmentArea =  40f, Density = 1.0f, KineticFraction = 0.25f, SurfaceEfficiency = 0.20f, SpinPreStress = 0.15f, CrackSpeed = 6f, CrackDirectionality = 0.75f, DetachCellScale = 0.90f, DetachCellJitter = 0.02f };

    public static readonly FractureProperties Ice = new()
    { Toughness = 10f, Brittleness = 0.80f, GrainArea =  900f, MinFragmentArea =  80f, Density = 0.9f, KineticFraction = 0.30f, SurfaceEfficiency = 0.16f, SpinPreStress = 0.13f, CrackSpeed = 4f, CrackDirectionality = 0.55f, DetachCellScale = 0.90f, DetachCellJitter = 0.02f };

    public static readonly FractureProperties Rock = new()
    { Toughness = 16f, Brittleness = 0.60f, GrainArea = 1500f, MinFragmentArea = 180f, Density = 1.4f, KineticFraction = 0.35f, SurfaceEfficiency = 0.12f, SpinPreStress = 0.12f, CrackSpeed = 2f, CrackDirectionality = 0.35f, DetachCellScale = 0.90f, DetachCellJitter = 0.02f };

    public static readonly FractureProperties Metal = new()
    { Toughness = 40f, Brittleness = 0.15f, GrainArea = 3000f, MinFragmentArea = 400f, Density = 2.0f, KineticFraction = 0.45f, SurfaceEfficiency = 0.06f, SpinPreStress = 0.08f, CrackSpeed = 1f, CrackDirectionality = 0.15f, DetachCellScale = 0.92f, DetachCellJitter = 0.01f };
}
