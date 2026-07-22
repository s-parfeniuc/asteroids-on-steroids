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
    /// <summary>[min, max] initial speed (px/s). Direction is inward from the spawn border.</summary>
    public float[]                   SpeedRange  { get; set; } = [30f, 90f];
    /// <summary>First wave this type can appear on (wave director may still suppress it via zero bias).</summary>
    public int                       UnlockWave  { get; set; } = 1;
    /// <summary>Per-entity vortex force multiplier ranges sampled at spawn. Null = use defaults (1,1).</summary>
    public VortexResponseConfig?     VortexResponse { get; set; }
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
    /// <summary>Lloyd (centroidal-Voronoi) relaxation passes applied to the seeds before tessellation:
    /// each pass nudges every seed to its cell's centroid, evening out cell sizes and removing slivers/
    /// elongated cells. 0 = raw random (lumpy); 2-3 = natural and uniform; high = honeycomb-regular.</summary>
    public int    RelaxIterations   { get; set; } = 2;

    // ── Material clusters ──────────────────────────────────────────────────────
    // Seeds are placed uniformly; heterogeneity comes from clusters — picked cells
    // whose higher bond/density/blast-resistance spreads to neighbours by a BFS whose
    // reach is a fraction of the body radius. (Dense core = few high-centrality
    // clusters; armored shell = many low-centrality clusters.)
    /// <summary>Number of clusters seeded across the body (0 = uniform material).</summary>
    public int     ClusterCount      { get; set; } = 0;
    /// <summary>[0,1] radial position of cluster centres: 1 = centre (core), 0 = surface (shell).</summary>
    public float   ClusterCentrality { get; set; } = 0.5f;
    /// <summary>[min,max] per-cluster reach as a fraction of the body radius (sampled per cluster).</summary>
    public float[] ClusterSpread     { get; set; } = [0.2f, 0.4f];
    /// <summary>How strongly a cluster raises bond strength at its core (additive multiplier).</summary>
    public float   BondGain          { get; set; } = 2f;
    /// <summary>How strongly a cluster raises per-cell density at its core (additive multiplier).</summary>
    public float   DensityGain       { get; set; } = 1.5f;
}

/// <summary>
/// Per-entity vortex multipliers sampled uniformly from [CentripetalRange, TangentialRange] at spawn.
/// Negative values make the entity resist or oppose the vortex direction.
/// </summary>
public class VortexResponseConfig
{
    /// <summary>[min, max] centripetal (inward) force multiplier. Negative = pushed outward.</summary>
    public float[] CentripetalRange { get; set; } = [1f, 1f];
    /// <summary>[min, max] tangential (CCW) force multiplier. Negative = reversed spin direction.</summary>
    public float[] TangentialRange  { get; set; } = [1f, 1f];
}
