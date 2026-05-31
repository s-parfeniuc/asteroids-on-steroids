using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.State;

/// <summary>
/// A distinct mode of operation for the game (Playing, Paused, MainMenu, etc.).
/// StateStack calls Enter/Exit on transitions and delegates Update/Draw
/// to the appropriate states each frame.
/// </summary>
public interface IGameState
{
    /// <summary>Called once when this state becomes active (pushed or resumed).</summary>
    void Enter();

    /// <summary>Called once when this state is deactivated (popped or suspended).</summary>
    void Exit();

    /// <summary>
    /// Called each frame while this state is the top of the stack.
    /// If UpdatesBelow is true on the state above, lower states also receive Update.
    /// </summary>
    void Update(double dt, InputSystem input);

    /// <summary>
    /// Called each frame in bottom-to-top order so overlay states draw on top.
    /// The renderer is in screen space (no camera transform applied — push the
    /// camera's view matrix yourself if you draw world-space content).
    /// </summary>
    void Draw(IRenderer renderer);

    /// <summary>
    /// If true, the state below this one in the stack also receives Update
    /// (e.g. a transparent HUD overlay that doesn't pause the game).
    /// Default: false.
    /// </summary>
    bool UpdatesBelow => false;
}
