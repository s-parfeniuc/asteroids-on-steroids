namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Mutable per-entity fracture runtime state. Kept separate from FractureProperties
/// so material presets stay static and shareable.
/// </summary>
public struct FractureState
{
    /// <summary>
    /// Accumulated impact energy from sub-threshold hits. Combined with the current
    /// hit before the threshold check; reset to zero when the body fractures.
    /// </summary>
    public float AbsorbedEnergy;

    /// <summary>
    /// Deterministic per-body RNG seed (tessellation, scatter spin). Fixed timestep +
    /// seeded RNG ⇒ reproducible fractures (replays, leaderboard integrity).
    /// </summary>
    public uint RngSeed;
}
