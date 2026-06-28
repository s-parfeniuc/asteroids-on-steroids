namespace AsteroidsGame.Config;

/// <summary>
/// Global fracture scalars: the model-shaping tuning constants (applied to the engine's
/// FractureTuning), the asteroid-on-asteroid collision settings, and the default impactor mass.
/// Per-weapon and per-material knobs live on WeaponConfig / MaterialConfig.
/// </summary>
public class FractureGlobalConfig
{
    // ── Model tuning constants (mirror engine FractureTuning) ──────────────────
    /// <summary>Single global conversion from physical ½·m·v² to fracture-energy units (real masses
    /// everywhere). The master damage-scale knob.</summary>
    public float EnergyScale { get; set; } = 0.0001f;
    /// <summary>Transmit (outward) fraction at brittleness 0 — the lerp floor.</summary>
    public float ReachMin { get; set; } = 0.1f;
    /// <summary>Transmit (outward) fraction at brittleness 1 — the lerp ceiling.</summary>
    public float ReachMax { get; set; } = 0.96f;
    /// <summary>Blast-wave penetration: fraction of a vaporised cell's surplus that continues outward
    /// (the rest is lost to heat).</summary>
    public float VaporEff { get; set; } = 0.4f;
    /// <summary>0 = bonds break ALONG the flow (tunnel) … 1 = PERPENDICULAR to it (cleave / radial star).</summary>
    public float BreakPerp { get; set; } = 1.0f;
    /// <summary>Fling energy → fragment speed.</summary>
    public float FlingScale { get; set; } = 140f;
    /// <summary>Directional cone sharpness (transmit alignment exponent).</summary>
    public float AlignExponent { get; set; } = 1.6f;
    /// <summary>Max spin stress multiplier.</summary>
    public float SpinCap { get; set; } = 4f;
    /// <summary>Clamp on a fragment's fling speed (px/s).</summary>
    public float FragmentSpeedMax { get; set; } = 600f;
    /// <summary>Fling-asymmetry → fragment spin gain.</summary>
    public float TumbleScale { get; set; } = 220f;
    /// <summary>Clamp on a fragment's spin (rad/s).</summary>
    public float FragmentSpinMax { get; set; } = 3.5f;
    /// <summary>Spin pre-stress at the body centre; rises to 1.0 at the rim.</summary>
    public float SpinProfileBase { get; set; } = 0.3f;

    // ── Asteroid-on-asteroid collision fracture ────────────────────────────────
    public float AsteroidBlastFraction { get; set; } = 0.08f;
    /// <summary>0 = crack direction is pure contact normal; 1 = pure relative velocity (including spin).</summary>
    public float AsteroidDirSpin { get; set; } = 1.0f;
    /// <summary>Minimum approach speed for an asteroid↔asteroid contact to fracture.</summary>
    public float AsteroidCollisionThreshold { get; set; } = 20f;

    // ── Bullet impact ─────────────────────────────────────────────────────────
    /// <summary>Default (real) impactor mass for a bullet's contact energy (weapons may override via
    /// Mass). Tuned so a bullet hits comparably to a moderate collision; EnergyScale sets absolute scale.</summary>
    public float BulletMass { get; set; } = 10f;
}
