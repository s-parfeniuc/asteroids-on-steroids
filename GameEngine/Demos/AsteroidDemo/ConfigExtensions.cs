using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsGame.Config;

namespace AsteroidDemo;

/// <summary>
/// Converts GameConfig data-model types to engine structs.
/// Lives in the demo/game layer so GameConfig stays engine-free.
/// </summary>
public static class ConfigExtensions
{
    public static FractureProperties ToFractureProperties(this MaterialConfig m) => new()
    {
        Brittleness       = m.Brittleness,
        Toughness         = m.Toughness,
        CrackDirectionality = m.CrackDirectionality,
        GrainArea         = m.GrainArea,
        MinFragmentArea   = m.MinFragmentArea,
        Density           = m.Density,
        KineticFraction   = m.KineticFraction,
        SurfaceEfficiency = m.SurfaceEfficiency,
        SpinPreStress     = m.SpinPreStress,
        CrackSpeed        = m.CrackSpeed,
        DetachCellScale   = m.DetachCellScale,
        DetachCellJitter  = m.DetachCellJitter,
    };

    public static WeaponProfile ToWeaponProfile(this WeaponConfig w) => new()
    {
        Directionality   = w.Directionality,
        MomentumTransfer = w.MomentumTransfer,
        EjectFraction    = w.EjectFraction,
        ImpactSpin       = w.ImpactSpin,
        BlastFraction    = w.BlastFraction,
        EnergyScale      = 1f,
    };
}
