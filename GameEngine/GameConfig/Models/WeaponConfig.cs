namespace AsteroidsGame.Config;

/// <summary>
/// Per-weapon tuning. All weapons share the base fields; type-specific fields are nullable
/// and ignored when null. Maps to WeaponProfile in the engine plus the game-layer shot params.
/// </summary>
public class WeaponConfig
{
    // ── Game-layer shot parameters ────────────────────────────────────────────
    public float FireRate        { get; set; } = 4f;    // shots per second
    public float ProjectileSpeed { get; set; } = 900f;
    public float TimeToLive      { get; set; } = 2.5f;  // seconds before projectile despawns

    // ── WeaponProfile (fracture engine) ──────────────────────────────────────
    /// <summary>0 = omnidirectional blast, 1 = tight forward channel. Combined with
    /// material CrackDirectionality at impact (arithmetic mean).</summary>
    public float Directionality    { get; set; } = 0.4f;
    public float MomentumTransfer  { get; set; } = 0.01f;
    public float EjectFraction     { get; set; } = 0.08f;
    public float ImpactSpin        { get; set; } = 0.5f;
    public float BlastFraction     { get; set; } = 0.3f;

    // ── Direct-fire (cannon / piercing) ──────────────────────────────────────
    public float Energy { get; set; }

    // ── Shotgun ───────────────────────────────────────────────────────────────
    public int?   Rays         { get; set; }
    public float? EnergyPerRay { get; set; }
    public float? ConeAngle    { get; set; }   // full cone spread in degrees

    // ── Grenade ───────────────────────────────────────────────────────────────
    public float? FuseTime      { get; set; }
    public int?   ShrapnelCount { get; set; }
    public float? ShrapnelSpread { get; set; } // degrees; 360 = full ring

    // ── Piercing ──────────────────────────────────────────────────────────────
    public float? Mass                { get; set; }
    public float? LateralImpulseClamp { get; set; }
}
