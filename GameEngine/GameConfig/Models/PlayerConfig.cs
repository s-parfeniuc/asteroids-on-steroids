namespace AsteroidsGame.Config;

public class PlayerConfig
{
    public string       Shape          { get; set; } = "player_ship";
    public float        ShapeScale     { get; set; } = 0.5f;   // uniform scale applied to authored shape vertices
    public string       Material       { get; set; } = "metal";
    public float        Thrust         { get; set; } = 4500f;  // px/s² acceleration rate
    public float        RotSpeed       { get; set; } = 3.2f;   // rad/s
    public float        MaxSpeed       { get; set; } = 900f;   // px/s
    public float        BrakeDrag      { get; set; } = 4.0f;   // exponential decay rate s⁻¹ when no keys held
    public float        Impulse        { get; set; } = 250f;   // px/s velocity burst on key press
    public float        LateralDrag    { get; set; } = 6.0f;   // s⁻¹ bleed on aim-perpendicular velocity
    public string       StartingWeapon { get; set; } = "cannon";
    public List<string> Skills         { get; set; } = ["dash", "turbo", "slowmo"];
}
