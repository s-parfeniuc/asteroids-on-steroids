namespace AsteroidsGame.Config;

/// <summary>
/// All tuneable fields of FractureProperties plus CrackDirectionality.
/// Maps 1-to-1 with the engine struct; the game layer converts via an extension method.
/// </summary>
public class MaterialConfig
{
    public float Brittleness       { get; set; } = 0.6f;
    public float Toughness         { get; set; } = 16f;
    /// <summary>0 = isotropic shatter, 1 = clean cleavage along grain.</summary>
    public float CrackDirectionality { get; set; } = 0.35f;
    public float GrainArea         { get; set; } = 1500f;
    public float MinFragmentArea   { get; set; } = 180f;
    public float Density           { get; set; } = 1.4f;
    public float KineticFraction   { get; set; } = 0.35f;
    public float SurfaceEfficiency { get; set; } = 0.12f;
    public float SpinPreStress     { get; set; } = 0.12f;
    public float CrackSpeed        { get; set; } = 2f;
    public float DetachCellScale   { get; set; } = 0.90f;
    public float DetachCellJitter  { get; set; } = 0.02f;
}
