namespace AsteroidsGame.Config;

/// <summary>Alien ship prefab config. Shape file is loaded from Assets/shapes/.</summary>
public class EntityConfig
{
    public string           Shape                    { get; set; } = "";
    /// <summary>Optional material override; empty = use the shape's own material.</summary>
    public string           Material                 { get; set; } = "";
    public float            Speed                    { get; set; } = 200f;
    public float            DetectionRadius          { get; set; } = 800f;
    public SteeringWeights? SteeringWeights          { get; set; }
    public float            Thrust                   { get; set; } = 600f;
    public float            ShootCooldown            { get; set; } = 2f;
    public float            LateralThrustPenaltyMult { get; set; } = 0.4f;
    public float            AlienImpactCoeff         { get; set; } = 1.0f;
    public float            ShapeScale               { get; set; } = 1.0f;
    public float            BaseCost                 { get; set; } = 20f;
    public int              CellCount                { get; set; } = 8;
    public BossConfig?      Boss                     { get; set; }
}

public class BossConfig
{
    public float  ShockwaveCooldown  { get; set; } = 8f;
    public float  ShockwaveRadius    { get; set; } = 1200f;
    public float  ShockwaveStrength  { get; set; } = 80000f;
    public float  BlackHoleCooldown  { get; set; } = 15f;
    public float  BlackHoleRadius    { get; set; } = 500f;
    public float  BlackHoleStrength  { get; set; } = 50000f;
    public float  BlackHoleCrushRadius { get; set; } = 40f;
    public float  BlackHoleDuration  { get; set; } = 6f;
    public float  BlackHoleSpeed     { get; set; } = 200f;
    public float  RamChargeCooldown  { get; set; } = 12f;
    public float  RamChargeMinDist   { get; set; } = 600f;
    public float  RamChargeDuration  { get; set; } = 2.5f;
    public float  RamChargeThrust    { get; set; } = 6000f;
    public float  SpawnInterval      { get; set; } = 8f;
    public string SpawnType          { get; set; } = "drone";
    public float  SpawnSafetyMargin  { get; set; } = 80f;
    public float  DriftThrust        { get; set; } = 150f;
}

public class SteeringWeights
{
    public float Separation { get; set; } = 1f;
    public float Pursuit    { get; set; } = 1f;
    public float Avoidance  { get; set; } = 1f;
}
