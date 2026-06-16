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
