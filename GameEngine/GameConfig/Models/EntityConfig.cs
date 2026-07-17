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
    public float            TurnSpeed                { get; set; } = 7f;    // max aim turn rate (rad/s)
    public float            ShootCooldown            { get; set; } = 2f;
    public float            LateralThrustPenaltyMult { get; set; } = 0.4f;
    public float            AlienImpactCoeff         { get; set; } = 1.0f;
    public float            ShapeScale               { get; set; } = 1.0f;
    public float            BaseCost                 { get; set; } = 20f;
    public int              CellCount                { get; set; } = 8;
    public BossConfig?      Boss                     { get; set; }

    /// <summary>Engagement radius for movement/firing: beyond it the alien ignores the player and
    /// wanders aimlessly. 0 = always aggro (legacy behaviour).</summary>
    public float            AggroRadius              { get; set; } = 0f;
    /// <summary>Standoff distance a kiting alien (drone) tries to hold from the player: it closes in
    /// when farther, backs off when closer, strafes near it. 0 = pursue directly (no standoff).</summary>
    public float            PreferredRange           { get; set; } = 0f;
    /// <summary>Optional dash skill (e.g. bruiser lunge). Null = no dash.</summary>
    public AlienDashConfig? Dash                     { get; set; }
}

/// <summary>A cooldown-gated lunge toward the player, triggered when within TriggerRange.</summary>
public class AlienDashConfig
{
    public float Cooldown     { get; set; } = 8f;     // seconds between dashes
    public float TriggerRange { get; set; } = 400f;   // dash when the player is within this distance
    public float Speed        { get; set; } = 1500f;  // velocity-spike magnitude added toward the player
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
    public float  RamChargeSpeed     { get; set; } = 850f;   // homing lunge speed (mass-independent)
    public float  RamChargeAccel     { get; set; } = 1400f;  // how fast it reaches ram speed (px/s²)
    public float  SpawnInterval      { get; set; } = 8f;
    public string SpawnType          { get; set; } = "drone";
    public float  SpawnSafetyMargin  { get; set; } = 80f;
    public float  DriftThrust        { get; set; } = 150f;

    // ── Movement: velocity-model standoff pursuit (advance when far, hold a range, back off if crowded) ──
    public float  CruiseSpeed        { get; set; } = 90f;   // px/s cruise toward/around the player
    public float  PreferredRange     { get; set; } = 750f;  // standoff distance it tries to hold
    public float  Accel              { get; set; } = 220f;  // px/s² toward the desired velocity

    // ── Overdrive + first-cast delays (formerly hardcoded) ──────────────────────
    public float  OverdriveCockpitFraction { get; set; } = 0.5f; // enrage when living cockpits ≤ this × initial
    public float  OverdriveSpawnMult       { get; set; } = 0.5f; // spawn interval × this while overdriven
    public float  BlackHoleInitialDelay    { get; set; } = 0.4f; // first black hole after cooldown × this
    public float  RamChargeInitialDelay    { get; set; } = 0.7f;

    // ── Black hole targeting ────────────────────────────────────────────────────
    public float  BlackHoleLead      { get; set; } = 1f;    // 0 = aim at current pos, 1 = full intercept lead

    // ── Radial bullet barrage (new skill) ───────────────────────────────────────
    public float  BarrageCooldown     { get; set; } = 6f;
    public int    BarrageCount        { get; set; } = 24;   // bullets in the ring
    public float  BarrageSpeed        { get; set; } = 650f;
    public float  BarrageInitialDelay { get; set; } = 0.6f;
    public float  BarrageOverdriveMult { get; set; } = 0.6f; // cooldown × this while overdriven
    public float  BarrageSpeedJitter  { get; set; } = 0.25f; // ±fraction per-bullet speed (like the grenade)
    public float  BarrageSpreadJitter { get; set; } = 0.6f;  // angular scatter within the ring spacing
    public float  BarrageTtlJitter    { get; set; } = 0.3f;
    public float  BarrageSpawnRadius  { get; set; } = 220f;  // ring radius bullets spawn on (clears the hull)
    /// <summary>Impact mass of each barrage ray. Barrage rays reuse the cannon_alien visual/TTL but
    /// hit with THIS mass instead of that weapon's, so the barrage's damage tunes independently.
    /// Null = fall back to the cannon_alien weapon mass.</summary>
    public float? BarrageRayMass      { get; set; }
}

public class SteeringWeights
{
    public float Separation { get; set; } = 1f;
    public float Pursuit    { get; set; } = 1f;
    public float Avoidance  { get; set; } = 1f;
}
