namespace AsteroidsGame;

public sealed class Score
{
    private float _total;
    private int   _chainTier;    // index into Config.Scoring.KillChainSteps
    private float _chainTimer;   // seconds since last kill; resets chain on expiry

    public float Total     => _total;
    public int   ChainTier => _chainTier;

    public void Reset() { _total = 0; _chainTier = 0; _chainTimer = 0; }

    /// <summary>Called every fixed step to decay the kill chain.</summary>
    public void Update(double dt, float decaySeconds)
    {
        if (_chainTier == 0) return;
        _chainTimer -= (float)dt;
        if (_chainTimer <= 0f) { _chainTier = 0; _chainTimer = 0; }
    }

    /// <summary>Award points for destroying a cell. cellScore = area × toughness × weight.</summary>
    public void AddKill(float cellScore, float[] chainSteps, float chainDecay)
    {
        float mult = _chainTier < chainSteps.Length ? chainSteps[_chainTier] : chainSteps[^1];
        _total += cellScore * mult;
        _chainTier = Math.Min(_chainTier + 1, chainSteps.Length - 1);
        _chainTimer = chainDecay;
    }

    public void Add(float points) => _total += points;
    public void AddBonus(int points) => _total += points;
}
