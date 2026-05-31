using System.Numerics;

namespace AsteroidsEngine.Engine.Input;

/// <summary>
/// Captures keyboard and mouse state from WinForms events (UI thread)
/// and exposes a polling API to game systems (game thread).
///
/// Thread safety: WinForms events write to _pending under a short lock.
/// BeginFrame() (called once per frame from the game thread) commits
/// _pending into _committed, computing pressed/released deltas.
/// Game systems read only _committed and the delta sets — never _pending.
///
/// GameWindow wires WinForms events like this:
///   KeyDown += (_, e) => input.OnKeyDown((KeyCode)e.KeyCode);
/// </summary>
public sealed class InputSystem
{
    private readonly HashSet<KeyCode> _pending = new();
    private readonly HashSet<KeyCode> _committed = new();
    private readonly HashSet<KeyCode> _pressedThisFrame = new();
    private readonly HashSet<KeyCode> _releasedThisFrame = new();
    private readonly object _lock = new();

    private Vector2 _mouseScreenPending;
    private Vector2 _mouseScreenCommitted;

    private bool _mouseLeftPending, _mouseLeftCommitted;
    private bool _mouseRightPending, _mouseRightCommitted;

    // -------------------------------------------------------------------------
    // Called from UI thread (wired in GameWindow)
    // -------------------------------------------------------------------------

    public void OnKeyDown(KeyCode key) { lock (_lock) _pending.Add(key); }
    public void OnKeyUp(KeyCode key) { lock (_lock) _pending.Remove(key); }

    public void OnMouseMove(Vector2 screenPosition)
    {
        lock (_lock) _mouseScreenPending = screenPosition;
    }

    public void OnMouseButton(MouseButton button, bool pressed)
    {
        lock (_lock)
        {
            if (button == MouseButton.Left) _mouseLeftPending = pressed;
            if (button == MouseButton.Right) _mouseRightPending = pressed;
        }
    }

    // -------------------------------------------------------------------------
    // Called from game thread at the start of each frame
    // -------------------------------------------------------------------------

    public void BeginFrame()
    {
        lock (_lock)
        {
            _pressedThisFrame.Clear();
            _releasedThisFrame.Clear();

            foreach (var k in _pending)
                if (!_committed.Contains(k)) _pressedThisFrame.Add(k);
            foreach (var k in _committed)
                if (!_pending.Contains(k)) _releasedThisFrame.Add(k);

            _committed.Clear();
            _committed.UnionWith(_pending);

            _mouseScreenCommitted = _mouseScreenPending;
            _mouseLeftCommitted = _mouseLeftPending;
            _mouseRightCommitted = _mouseRightPending;
        }
    }

    // -------------------------------------------------------------------------
    // Polling API — called from game thread (game systems)
    // -------------------------------------------------------------------------

    /// <summary>Key is currently held down.</summary>
    public bool IsHeld(KeyCode key) => _committed.Contains(key);

    /// <summary>Key went down this frame (true for exactly one frame).</summary>
    public bool IsPressed(KeyCode key) => _pressedThisFrame.Contains(key);

    /// <summary>Key came up this frame (true for exactly one frame).</summary>
    public bool IsReleased(KeyCode key) => _releasedThisFrame.Contains(key);

    public bool IsPressedThisFrame(params KeyCode[] keys) => keys.Any(IsPressed);

    /// <summary>Mouse position in screen (pixel) coordinates.</summary>
    public Vector2 MouseScreen => _mouseScreenCommitted;

    public bool IsMouseLeft => _mouseLeftCommitted;
    public bool IsMouseRight => _mouseRightCommitted;
}
