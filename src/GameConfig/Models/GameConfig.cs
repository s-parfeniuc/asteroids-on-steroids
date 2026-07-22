namespace AsteroidsGame.Config;

public class GameConfig
{
    public Dictionary<string, MaterialConfig>  Materials  { get; set; } = new();
    public Dictionary<string, WeaponConfig>    Weapons    { get; set; } = new();
    public Dictionary<string, SkillConfig>     Skills     { get; set; } = new();
    public PlayerConfig                        Player     { get; set; } = new();
    public Dictionary<string, EntityConfig>    Entities   { get; set; } = new();
    public Dictionary<string, AsteroidConfig>  Asteroids  { get; set; } = new();
    public int                                 MaxLiveCells { get; set; } = 600;
    public List<WaveDefinition>                Waves      { get; set; } = new();
    public ScoringConfig                       Scoring    { get; set; } = new();
    public WorldConfig                         World      { get; set; } = new();
    public BorderHazardConfig                  BorderHazard { get; set; } = new();
    public VortexConfig                        Vortex     { get; set; } = new();
    public VortexFxConfig                      VortexFx   { get; set; } = new();
    public MinimapConfig                       Minimap    { get; set; } = new();
    public WaveSystemConfig                    WaveSystem { get; set; } = new();
    public VfxConfig                           Vfx        { get; set; } = new();
    public FractureGlobalConfig                Fracture   { get; set; } = new();
    public PhysicsConfig                       Physics    { get; set; } = new();
    public List<DifficultyConfig>              Difficulties { get; set; } = new();
}

/// <summary>A named difficulty preset: a set of multipliers layered over the base tuning at run start.</summary>
public class DifficultyConfig
{
    public string Name             { get; set; } = "Normal";
    /// <summary>Scales the per-wave spawn budget (more/denser enemy waves).</summary>
    public float  BudgetMult       { get; set; } = 1f;
    /// <summary>Scales the live-cell cap growth (bigger battles allowed).</summary>
    public float  CapMult          { get; set; } = 1f;
    /// <summary>Scales alien fire cooldown (below 1 = faster fire = harder).</summary>
    public float  EnemyFireMult    { get; set; } = 1f;
    /// <summary>Scales damage the player takes (via PlayerImpactCoeff).</summary>
    public float  PlayerDamageMult { get; set; } = 1f;
}

public class ScoringConfig
{
    /// <summary>Score per cell = area × toughness × this weight.</summary>
    public float CellScoreAreaWeight { get; set; } = 0.001f;
    /// <summary>Kill-chain multiplier tiers. Index 0 = first kill (×1), last index = max.</summary>
    public float[] KillChainSteps    { get; set; } = [1f, 1.5f, 2f, 4f];
    /// <summary>Seconds without a kill before the chain resets to tier 0.</summary>
    public float KillChainDecay      { get; set; } = 3f;
    public int   WaveSurvivalBonus   { get; set; } = 500;
    public int   LeaderboardSize     { get; set; } = 10;
}
