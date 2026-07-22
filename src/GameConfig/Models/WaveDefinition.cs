namespace AsteroidsGame.Config;

/// <summary>
/// One wave entry. Type = "budget": game picks spawns from Spawns weights until Budget
/// is spent (budget unit = total cell count of spawned entity). Type = "explicit": spawns
/// exactly the list in Asteroids. Both types use SpawnPattern and Modifiers.
/// </summary>
public class WaveDefinition
{
    public int    Wave   { get; set; }
    /// <summary>"budget" or "explicit".</summary>
    public string Type   { get; set; } = "budget";

    // ── Budget wave ───────────────────────────────────────────────────────────
    /// <summary>How many asteroids to spawn (budget type). 0 = fall back to
    /// the global AstCount tunable. Explicit waves use per-group Count fields.</summary>
    public int                        AsteroidCount { get; set; } = 0;
    /// <summary>Total cell-count budget for this wave (budget type only).</summary>
    public float                      Budget  { get; set; }
    /// <summary>Entity-type key → relative spawn weight (budget type only).</summary>
    public Dictionary<string, float>? Spawns  { get; set; }

    // ── Explicit wave ─────────────────────────────────────────────────────────
    public List<ExplicitSpawn>? Asteroids { get; set; }

    /// <summary>Size distribution bias: -1 = prefer small multipliers, 0 = uniform, +1 = prefer large.</summary>
    public float SizeBias { get; set; } = 0f;

    // ── Common ────────────────────────────────────────────────────────────────
    /// <summary>"burst" = all at once, "rapid" = one every RapidInterval s,
    /// "staggered" = one every 0.5–1.5 s (randomised).</summary>
    public string       SpawnPattern   { get; set; } = "burst";
    public float        RapidInterval  { get; set; } = 0.4f;
    /// <summary>Named modifier tags applied to every asteroid in this wave
    /// (e.g. "high_spin", "armored", "unstable"). Interpreted by the spawner.</summary>
    public List<string> Modifiers      { get; set; } = [];
    public bool         Boss           { get; set; }
}

public class ExplicitSpawn
{
    public string Type       { get; set; } = "";
    public int    Count      { get; set; } = 1;
    /// <summary>Seconds after the wave trigger before this group spawns.</summary>
    public float  SpawnDelay { get; set; }
}
