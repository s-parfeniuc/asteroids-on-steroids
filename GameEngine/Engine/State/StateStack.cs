using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.State;

/// <summary>
/// Manages a stack of IGameState instances.
///
/// Push(state)  — overlay a new state; current state is suspended (not exited)
/// Pop()        — remove top state; state below resumes
/// Replace(s)   — pop current + push new in one step (no Resume on the old state)
///
/// Update: propagates down the stack while states have UpdatesBelow = true.
/// Draw:   renders all states bottom-to-top so the top state is visually on top.
/// </summary>
public sealed class StateStack
{
    private readonly List<IGameState> _stack = new();

    public IGameState? Top => _stack.Count > 0 ? _stack[^1] : null;
    public bool        IsEmpty => _stack.Count == 0;

    // -------------------------------------------------------------------------
    // Transitions
    // -------------------------------------------------------------------------

    public void Push(IGameState state)
    {
        state.Enter();
        _stack.Add(state);
    }

    public void Pop()
    {
        if (_stack.Count == 0) return;
        _stack[^1].Exit();
        _stack.RemoveAt(_stack.Count - 1);
    }

    /// <summary>Exits the current top state and pushes a new one.</summary>
    public void Replace(IGameState state)
    {
        if (_stack.Count > 0)
        {
            _stack[^1].Exit();
            _stack.RemoveAt(_stack.Count - 1);
        }
        state.Enter();
        _stack.Add(state);
    }

    public void Clear()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            _stack[i].Exit();
        _stack.Clear();
    }

    // -------------------------------------------------------------------------
    // Per-frame
    // -------------------------------------------------------------------------

    public void Update(double dt, InputSystem input)
    {
        // Walk from the top downward while UpdatesBelow is true.
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            _stack[i].Update(dt, input);
            if (!_stack[i].UpdatesBelow) break;
        }
    }

    public void Draw(IRenderer renderer)
    {
        // Draw bottom-to-top: lower layers appear behind upper layers.
        foreach (var state in _stack)
            state.Draw(renderer);
    }
}
