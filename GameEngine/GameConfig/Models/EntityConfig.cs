namespace AsteroidsGame.Config;

/// <summary>Alien ship prefab config. Shape file is loaded from Assets/shapes/.</summary>
public class EntityConfig
{
    public string          Shape           { get; set; } = "";
    public string          Material        { get; set; } = "metal";
    public float           Speed           { get; set; } = 200f;
    public float           DetectionRadius { get; set; } = 800f;
    public SteeringWeights? SteeringWeights { get; set; }
}

public class SteeringWeights
{
    public float Separation { get; set; } = 1f;
    public float Pursuit    { get; set; } = 1f;
    public float Avoidance  { get; set; } = 1f;
}
