using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.States;

/// <summary>
/// One screen/mode of the game. Update is called on every fixed step.
/// Returning a non-null value triggers a state transition: the current state
/// is Exit()ed, the new one is Enter()ed, and it takes over.
/// </summary>
public interface IGameState
{
    void Enter();
    void Exit();
    /// <summary>Fixed-step update. Return the next state to transition, or null to stay.</summary>
    IGameState? Update(double dt);
    /// <summary>Render frame. alpha is the sub-step interpolation factor [0,1].</summary>
    void Draw(IRenderer renderer, float alpha);
}
