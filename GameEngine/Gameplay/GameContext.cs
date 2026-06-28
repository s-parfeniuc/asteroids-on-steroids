using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Input;
using AsteroidsGame.Config;

namespace AsteroidsGame;

/// <summary>
/// Shared services passed to every game state. Owns nothing that belongs to a
/// single state — per-state worlds and systems live on the states themselves.
/// </summary>
public sealed class GameContext
{
    public GameConfig                    Config   { get; }
    public Dictionary<string, ShapeData> Shapes   { get; }
    public InputSystem                   Input    { get; }
    public int                           ScreenW  { get; }
    public int                           ScreenH  { get; }
    public Random                        Rng      { get; } = new();

    public Score      Score      { get; } = new();
    public CellBudget CellBudget { get; } = new();

    public GameContext(GameConfig config, Dictionary<string, ShapeData> shapes,
                       InputSystem input, int screenW, int screenH)
    {
        Config  = config;
        Shapes  = shapes;
        Input   = input;
        ScreenW = screenW;
        ScreenH = screenH;
        ApplyFractureTuning(config.Fracture);
    }

    /// <summary>Push the global fracture tuning constants from config into the engine.</summary>
    public static void ApplyFractureTuning(FractureGlobalConfig f)
    {
        FractureTuning.EnergyScale         = f.EnergyScale;
        FractureTuning.ReachMin            = f.ReachMin;
        FractureTuning.ReachMax            = f.ReachMax;
        FractureTuning.VaporEff            = f.VaporEff;
        FractureTuning.BreakPerp           = f.BreakPerp;
        FractureTuning.FlingScale          = f.FlingScale;
        FractureTuning.AlignExponent       = f.AlignExponent;
        FractureTuning.SpinCap             = f.SpinCap;
        FractureTuning.FragmentSpeedMax    = f.FragmentSpeedMax;
        FractureTuning.TumbleScale         = f.TumbleScale;
        FractureTuning.FragmentSpinMax     = f.FragmentSpinMax;
        FractureTuning.SpinProfileBase     = f.SpinProfileBase;
    }
}
