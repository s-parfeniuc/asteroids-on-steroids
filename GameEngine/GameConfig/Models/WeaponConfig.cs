namespace AsteroidsGame.Config;

/// <summary>
/// Per-weapon tuning. All weapons share the base fields; type-specific fields are nullable
/// and ignored when null. Maps to WeaponProfile in the engine plus the game-layer shot params.
/// </summary>
public class WeaponConfig
{
    // ── Game-layer shot parameters ────────────────────────────────────────────
    public float FireRate { get; set; } = 4f;    // shots per second
    public float ProjectileSpeed { get; set; } = 900f;
    public float TimeToLive { get; set; } = 2.5f;  // seconds before projectile despawns

    // ── WeaponProfile (fracture engine) ──────────────────────────────────────
    /// <summary>0 = omnidirectional blast, 1 = tight forward channel. Combined with
    /// material CrackDirectionality at impact (arithmetic mean).</summary>
    public float Directionality { get; set; } = 0.4f;
    /// <summary>Vaporize budget carved from each cell's energy → crater size.</summary>
    public float BlastFraction { get; set; } = 0.3f;
    /// <summary>One-time recoil on the struck body, as a fraction of impactor speed.</summary>
    public float Knockback { get; set; } = 0.01f;

    // ── Pellet variance + drag (shotgun / shrapnel) ──────────────────────────
    /// <summary>±fraction of random per-pellet speed variation.</summary>
    public float SpeedJitter { get; set; } = 0f;
    /// <summary>±fraction of random per-pellet time-to-live variation.</summary>
    public float TtlJitter { get; set; } = 0f;
    /// <summary>Per-pellet angular jitter as a fraction of the spacing between rays
    /// (stratified spread: evenly slotted but not gridded).</summary>
    public float SpreadJitter { get; set; } = 0f;
    /// <summary>Per-bullet velocity drag (1/s). High drag → strong close-range, weak at range.</summary>
    public float Drag { get; set; } = 0f;

    // ── Piercing physical mass ───────────────────────────────────────────────
    /// <summary>Physical projectile mass for the piercing round (its rigidbody + the impactor
    /// mass it drives into the target). Other weapons use the global fracture.bulletMass.</summary>
    public float? Mass { get; set; }

    // ── Shotgun ───────────────────────────────────────────────────────────────
    public int? Rays { get; set; }
    public float? ConeAngle { get; set; }   // full cone spread in degrees

    // ── Grenade ───────────────────────────────────────────────────────────────
    public float? FuseTime { get; set; }
    public int? ShrapnelCount { get; set; }
    public float? ShrapnelSpread { get; set; }  // degrees; 360 = full ring
    public float? ShrapnelSpeed { get; set; }  // px/s for each fragment; falls back to ProjectileSpeed

    // ── Piercing ──────────────────────────────────────────────────────────────
    public float? LateralImpulseClamp { get; set; }

    public float? ShapeScale { get; set; }  // optional override for the projectile shape scale (default = 1)
}
