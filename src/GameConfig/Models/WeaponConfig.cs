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
    public float? ShrapnelMass  { get; set; }  // impactor mass per shrapnel piece; falls back to global BulletMass

    // ── Piercing ──────────────────────────────────────────────────────────────
    public float? LateralImpulseClamp { get; set; }
    /// <summary>Superseded by the penetration-power model. Kept so old configs still load.</summary>
    public float? PenetrationSpeedLoss { get; set; }

    // ── Piercing: terminal-ballistics budget. Four orthogonal dials: ────────────
    //   PenetrationPower  → tunnel length     PenetrationCostScale → per-material exchange rate
    //   PierceDamageScale → collateral        PierceSpeedExponent  → visible deceleration

    /// <summary>Total material (Σ cell thresholds + residual bond strength, at cost scale 1) a full
    /// round fired at ProjectileSpeed can chew through in its lifetime. The round's actual budget
    /// scales with its spawn kinetic energy, so shards recompute theirs from their own mass and
    /// fling speed. When a cell costs more than what's left, the round shatters on its face.</summary>
    public float? PenetrationPower { get; set; }

    /// <summary>Scales what a cell charges to be punched through (its pulverise threshold plus the
    /// residual strength of its intact bonds). Higher = tougher armour / shorter tunnels.</summary>
    public float? PenetrationCostScale { get; set; }

    /// <summary>Fracture energy deposited in each crossed cell, as a multiple of that cell's
    /// penetration cost. 1 = just enough to carve the tunnel; above 1 shatters around it (shredder),
    /// below 1 leaves a clean needle of cracked-but-standing cells.</summary>
    public float? PierceDamageScale { get; set; }

    /// <summary>How the round's speed fades as its budget drains: v = v₀·(Power/Power₀)^exponent.
    /// 0 = full speed until it dies; 1 = linear fade; 0.5 ≈ energy-like.</summary>
    public float? PierceSpeedExponent { get; set; }
    /// <summary>Fraction of the round's momentum (roundMass·speed) imparted to the struck body,
    /// then divided by the target mass — heavy asteroids barely budge, light fragments get nudged.</summary>
    public float? TargetPushCoeff { get; set; }

    public float? ShapeScale { get; set; }  // optional override for the projectile shape scale (default = 1)
}
