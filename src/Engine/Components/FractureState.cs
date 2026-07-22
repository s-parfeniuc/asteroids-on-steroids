namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Mutable per-entity fracture runtime state. Kept separate from FractureProperties
/// so material presets stay static and shareable.
/// </summary>
public struct FractureState
{
    /// <summary>
    /// Deterministic per-body RNG seed (tessellation, scatter spin). Fixed timestep +
    /// seeded RNG ⇒ reproducible fractures (replays, leaderboard integrity).
    /// Accumulated damage now lives on <see cref="Bond.Stress"/>, not here.
    /// </summary>
    public uint RngSeed;
}
