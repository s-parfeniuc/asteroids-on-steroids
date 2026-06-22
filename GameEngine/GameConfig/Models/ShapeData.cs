namespace AsteroidsGame.Config;

/// <summary>
/// Authored compound-body shape exported from the shape editor.
/// Coordinates are centroid-normalised (centroid at [0,0]).
/// </summary>
public class ShapeData
{
    public string     Name     { get; set; } = "";
    public string     Material { get; set; } = "metal";
    /// <summary>Outline polygon vertices as [x, y] pairs.</summary>
    public float[][]  Outline  { get; set; } = [];
    public SeedData[] Seeds    { get; set; } = [];
}

public class SeedData
{
    public float  X           { get; set; }
    public float  Y           { get; set; }
    public string Role        { get; set; } = "generic";
    /// <summary>Bond-strength multiplier for all bonds adjacent to this cell (default 1).</summary>
    public float  BondMult    { get; set; } = 1f;
    /// <summary>Vaporisation resistance [0,1]; 0 = normal, 1 = indestructible (default 0).</summary>
    public float  BlastResist { get; set; } = 0f;
    /// <summary>Mass contribution multiplier for this cell (default 1).</summary>
    public float  DensityMult { get; set; } = 1f;
}
