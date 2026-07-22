namespace AsteroidsGame;

public sealed class Score
{
    private float _total;
    private int   _chainTier;    // index into Config.Scoring.KillChainSteps
    private float _chainTimer;   // seconds since last kill; resets chain on expiry

    public float Total     => _total;
    public int   ChainTier => _chainTier;
    /// <summary>Points added by the most recent AddKill (base × chain multiplier) — for score popups.</summary>
    public float LastAward { get; private set; }

    /// <summary>While set, the score is locked: no awards, no chain decay. Set when the player dies —
    /// the world keeps simulating under the game-over overlay, but the run's score is final.</summary>
    public bool Frozen { get; set; }

    public void Reset() { _total = 0; _chainTier = 0; _chainTimer = 0; LastAward = 0f; Frozen = false; }

    /// <summary>Called every fixed step to decay the kill chain.</summary>
    public void Update(double dt, float decaySeconds)
    {
        if (Frozen || _chainTier == 0) return;
        _chainTimer -= (float)dt;
        if (_chainTimer <= 0f) { _chainTier = 0; _chainTimer = 0; }
    }

    /// <summary>Award points for destroying a cell. cellScore = area × toughness × weight.</summary>
    public void AddKill(float cellScore, float[] chainSteps, float chainDecay)
    {
        if (Frozen) { LastAward = 0f; return; }
        float mult = _chainTier < chainSteps.Length ? chainSteps[_chainTier] : chainSteps[^1];
        LastAward   = cellScore * mult;
        _total     += LastAward;
        _chainTier  = Math.Min(_chainTier + 1, chainSteps.Length - 1);
        _chainTimer = chainDecay;
    }

    public void Add(float points)    { if (!Frozen) _total += points; }
    public void AddBonus(int points) { if (!Frozen) _total += points; }
}
