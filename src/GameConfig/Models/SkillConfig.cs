namespace AsteroidsGame.Config;

/// <summary>
/// Per-skill tuning. Type-specific fields are nullable; only the relevant subset
/// is present for each skill in the JSON.
/// </summary>
public class SkillConfig
{
    public float Cooldown { get; set; }
    public float Duration { get; set; }

    // ── Dash ──────────────────────────────────────────────────────────────────
    public float? VelocitySpike      { get; set; }
    public float? InvincibilityTime  { get; set; }

    // ── Turbo ────────────────────────────────────────────────────────────────
    public float? ThrustMult { get; set; }

    // ── SlowMo ───────────────────────────────────────────────────────────────
    public float? TimeScale        { get; set; }
    public float? PlayerSpeedBoost { get; set; }
    /// <summary>Multiplies the player's aim rotation speed while slow-mo is active, so aim stays
    /// responsive against the scaled-down dt (needs to exceed 1/TimeScale to feel faster than normal).</summary>
    public float? RotSpeedBoost    { get; set; }
}
