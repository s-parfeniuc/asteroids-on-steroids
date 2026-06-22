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
    }
}
