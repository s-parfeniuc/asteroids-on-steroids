namespace AsteroidsGame.Config;

/// <summary>
/// Asteroid variant config. Either references an authored shape file (Shape != null)
/// or describes a procedural generator (Procedural != null). The loader checks Shape first.
/// </summary>
public class AsteroidConfig
{
    public string?                   Shape      { get; set; }
    public ProceduralAsteroidConfig? Procedural { get; set; }
    public string                    Material   { get; set; } = "rock";
    /// <summary>[min, max] uniform scale applied at spawn.</summary>
    public float[]                   SizeRange  { get; set; } = [0.8f, 1.2f];
    /// <summary>[min, max] initial angular speed (rad/s).</summary>
    public float[]                   SpinRange  { get; set; } = [0f, 1f];
    /// <summary>Global density multiplier on top of the material density.</summary>
    public float                     DensityMult { get; set; } = 1f;
    /// <summary>Abstract cost per unit of size_mult for the wave budget system.</summary>
    public int                       BaseCost    { get; set; } = 3;
    /// <summary>Estimated cell count per unit of size_mult for the wave cell-cap check.</summary>
    public int                       BaseCells   { get; set; } = 12;
    /// <summary>[min, max] initial speed (px/s). Direction is inward from the spawn border.</summary>
    public float[]                   SpeedRange  { get; set; } = [30f, 90f];
    /// <summary>First wave this type can appear on (wave director may still suppress it via zero bias).</summary>
    public int                       UnlockWave  { get; set; } = 1;
}

public class ProceduralAsteroidConfig
{
    public float  BaseRadius        { get; set; } = 80f;
    /// <summary>[min, max] vertex count for the outline polygon.</summary>
    public int[]  VertexCount       { get; set; } = [10, 16];
    /// <summary>Noise amplitude as a fraction of BaseRadius.</summary>
    public float  Roughness         { get; set; } = 0.22f;
    /// <summary>Frequency of the angular noise function.</summary>
    public float  NoiseFrequency    { get; set; } = 3f;
    /// <summary>Probability [0,1] of an inward dent per vertex.</summary>
    public float  ConcavityBias     { get; set; } = 0.05f;
    /// <summary>[min, max] number of Voronoi seeds to scatter.</summary>
    public int[]  SeedCount         { get; set; } = [6, 10];
    /// <summary>[0,1] bias toward placing seeds near the centroid (0 = uniform, 1 = all central).</summary>
    public float  SeedClusterCenter { get; set; } = 0.3f;

    /// <summary>
    /// Spatial distribution of bond-strength multipliers across cells.
    /// Null = uniform 1.0 (all bonds at material toughness, no variance).
    /// </summary>
    public CellPropertyDistribution? BondMultDistribution    { get; set; }

    /// <summary>
    /// Spatial distribution of per-cell density multipliers.
    /// Null = uniform 1.0 (mass proportional to cell area × material density only).
    /// </summary>
    public CellPropertyDistribution? DensityMultDistribution { get; set; }

    /// <summary>
    /// Spatial distribution of per-cell blast resistance [0,1].
    /// Null = uniform 0.0 (no vaporisation resistance).
    /// </summary>
    public CellPropertyDistribution? BlastResistDistribution { get; set; }
}

/// <summary>
/// Describes how a scalar cell property varies across the body of a procedural asteroid.
/// The generator evaluates this at each cell's normalised radial position (r ∈ [0,1],
/// where 0 = centroid and 1 = furthest cell from centroid).
///
/// Types:
///   "constant"      — every cell gets Value. Equivalent to the shape-editor default.
///   "radialGradient"— lerp from CenterValue (r=0) to SurfaceValue (r=1), shaped by Exponent.
///                     Exponent > 1 concentrates the high end near the centre;
///                     Exponent &lt; 1 concentrates it near the surface.
///   "noiseClusters" — BaseValue ± Amplitude·noise(cell·Frequency). Produces random
///                     pockets of high/low values. Frequency controls cluster size
///                     (higher = smaller, more numerous clusters).
/// </summary>
public class CellPropertyDistribution
{
    public string Type { get; set; } = "constant";

    // ── constant ──────────────────────────────────────────────────────────────
    public float Value { get; set; } = 1f;

    // ── radialGradient ────────────────────────────────────────────────────────
    public float? CenterValue  { get; set; }
    public float? SurfaceValue { get; set; }
    /// <summary>Power applied to normalised r before lerping (default 1 = linear).</summary>
    public float? Exponent     { get; set; }

    // ── noiseClusters ─────────────────────────────────────────────────────────
    public float? BaseValue  { get; set; }
    public float? Amplitude  { get; set; }
    public float? Frequency  { get; set; }
}
