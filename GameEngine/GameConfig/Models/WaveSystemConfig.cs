namespace AsteroidsGame.Config;

public class WaveSystemConfig
{
    public int   BaseCellCap                { get; set; } = 300;
    public int   MaxCellCap                 { get; set; } = 2000;
    public int   CellCapGrowthAmount        { get; set; } = 30;
    public float GrowthIntervalSeconds      { get; set; } = 30f;
    public int   BaseBudget                 { get; set; } = 20;
    public int   BudgetGrowthPerInterval    { get; set; } = 5;
    public float TriggerThreshold           { get; set; } = 0.30f;
    public float GracePeriodSeconds         { get; set; } = 8.0f;
    public float HardTriggerIntervalSeconds { get; set; } = 30.0f;
    public float SpawnDelaySeconds          { get; set; } = 1.5f;
    public float SizeBiasStart              { get; set; } = -0.2f;
    public float SizeBiasEnd                { get; set; } = 0.6f;
    public float SizeBiasRampEnd            { get; set; } = 600.0f;
    public float MothershpSpawnTime         { get; set; } = 600.0f;

    public Dictionary<string, SpawnBiasEntry> SpawnBias { get; set; } = new();
}

public class SpawnBiasEntry
{
    public float W0 { get; set; }
    public float W1 { get; set; }
    public float T0 { get; set; }
    public float T1 { get; set; }
}

public class VortexConfig
{
    public float Centripetal          { get; set; } = 0.05f;
    public float Tangential           { get; set; } = 0.02f;
    public float Deadzone             { get; set; } = 800f;
    public float CapFrames            { get; set; } = 8f;
    public float VariationCentripetal { get; set; } = 0.3f;
    public float VariationTangential  { get; set; } = 0.3f;
}

public class WorldConfig
{
    public int   Width             { get; set; } = 5760;
    public int   Height            { get; set; } = 3240;
    public float CameraFollowSpeed { get; set; } = 4f;
}
