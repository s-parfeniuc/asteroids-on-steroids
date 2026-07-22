namespace AsteroidsGame.Config;

/// <summary>
/// All tuneable fields of FractureProperties plus CrackDirectionality.
/// Maps 1-to-1 with the engine struct; the game layer converts via an extension method.
/// </summary>
public class MaterialConfig
{
    public float Brittleness       { get; set; } = 0.6f;
    public float Toughness         { get; set; } = 16f;
    /// <summary>Coefficient of restitution for the dissipated energy: only (1 − e²) of the
    /// contact energy couples into fracture (the rest bounces back).</summary>
    public float Restitution       { get; set; } = 0.30f;
    /// <summary>Stress/sec at which accumulated per-bond Stress relaxes when not being hit.</summary>
    public float RelaxRate         { get; set; } = 200f;
    /// <summary>0 = isotropic shatter, 1 = clean cleavage along grain.</summary>
    public float CrackDirectionality { get; set; } = 0.35f;
    /// <summary>Cells/second the crack front advances (multi-frame pacing).</summary>
    public float CrackSpeed        { get; set; } = 240f;
    public float GrainArea         { get; set; } = 1500f;
    public float MinFragmentArea   { get; set; } = 180f;
    public float Density           { get; set; } = 1.4f;
    /// <summary>Vaporise threshold per unit cell mass — how much blast energy (accumulated) it takes
    /// to pulverise a cell. Higher = harder to vaporise / more sustained blast to crater.</summary>
    public float CellToughness     { get; set; } = 0.5f;
    public float SpinPreStress     { get; set; } = 0.12f;
    public float DetachCellScale   { get; set; } = 0.90f;
    public float DetachCellJitter  { get; set; } = 0.02f;
}
