using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsGame.Config;

namespace AsteroidsGame;

/// <summary>
/// Converts GameConfig data-model types to engine structs.
/// Lives in the shared gameplay layer so GameConfig stays engine-free.
/// </summary>
public static class ConfigExtensions
{
    public static FractureProperties ToFractureProperties(this MaterialConfig m) => new()
    {
        Brittleness = m.Brittleness,
        Toughness = m.Toughness,
        Restitution = m.Restitution,
        RelaxRate = m.RelaxRate,
        CrackDirectionality = m.CrackDirectionality,
        CrackSpeed = m.CrackSpeed,
        GrainArea = m.GrainArea,
        MinFragmentArea = m.MinFragmentArea,
        Density = m.Density,
        CellToughness = m.CellToughness,
        SpinPreStress = m.SpinPreStress,
        DetachCellScale = m.DetachCellScale,
        DetachCellJitter = m.DetachCellJitter,
    };

    /// <summary>Resolves a body's material: the shape owns it (`ShapeData.Material`); a
    /// non-empty config key overrides. Falls back to the first material if unknown.</summary>
    public static FractureProperties ResolveMaterial(this GameConfig config, string? configOverride, ShapeData shape)
    {
        string key = !string.IsNullOrWhiteSpace(configOverride) ? configOverride : shape.Material;
        if (string.IsNullOrWhiteSpace(key) || !config.Materials.TryGetValue(key, out var mc))
            mc = config.Materials.Values.First();
        return mc.ToFractureProperties();

    }

    public static WeaponProfile ToWeaponProfile(this WeaponConfig w) => new()
    {
        Directionality = w.Directionality,
        BlastFraction = w.BlastFraction,
        Knockback = w.Knockback,
    };
}
