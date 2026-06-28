using System.Numerics;

namespace AsteroidsEngine.Engine.Input;

/// <summary>
/// Captures keyboard and mouse state from platform events (UI thread)
/// and exposes a polling API to game systems (game thread).
///
/// Thread safety: platform events write to _pending under a short lock.
/// BeginFrame() (called once per frame from the game thread) commits
/// _pending into _committed, computing pressed/released deltas.
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

    private string _textPending = "";
    private string _textThisFrame = "";

    // ── Called from platform/UI thread ────────────────────────────────────────

    public void OnKeyDown(KeyCode key) { lock (_lock) _pending.Add(key); }
    public void OnKeyUp(KeyCode key)   { lock (_lock) _pending.Remove(key); }

    public void OnMouseMove(Vector2 screenPosition) { lock (_lock) _mouseScreenPending = screenPosition; }

    public void OnMouseButton(MouseButton button, bool pressed)
    {
        lock (_lock)
        {
            if (button == MouseButton.Left)  _mouseLeftPending  = pressed;
            if (button == MouseButton.Right) _mouseRightPending = pressed;
        }
    }

    /// <summary>Called by the platform with each SDL_TextInput frame (UTF-8 string).</summary>
    public void OnTextInput(string chars) { lock (_lock) _textPending += chars; }

    // ── Called from game thread — once per frame ──────────────────────────────

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
            _mouseLeftCommitted   = _mouseLeftPending;
            _mouseRightCommitted  = _mouseRightPending;

            _textThisFrame = _textPending;
            _textPending   = "";
        }
    }

    // ── Polling API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Set true while a UI text-input widget is active to block WASD / hotkeys
    /// from reaching game systems.  Back / Enter / Escape are always passed
    /// through so the text widget itself can handle them.
    /// </summary>
    public bool SuppressKeyboard { get; set; }

    private static bool IsEditingKey(KeyCode key) =>
        key is KeyCode.Back or KeyCode.Enter or KeyCode.Escape;

    private static bool IsModifier(KeyCode key) =>
        key is KeyCode.Control or KeyCode.Shift or KeyCode.Alt;

    private bool CtrlHeld => _committed.Contains(KeyCode.Control);

    /// <summary>Key is currently held down.</summary>
    public bool IsHeld(KeyCode key) =>
        (!SuppressKeyboard || IsModifier(key)) && _committed.Contains(key);

    /// <summary>Key went down this frame (true for exactly one frame).</summary>
    public bool IsPressed(KeyCode key) =>
        (!SuppressKeyboard || IsEditingKey(key) || CtrlHeld) && _pressedThisFrame.Contains(key);

    /// <summary>Key came up this frame (true for exactly one frame).</summary>
    public bool IsReleased(KeyCode key) =>
        (!SuppressKeyboard || IsEditingKey(key) || CtrlHeld) && _releasedThisFrame.Contains(key);

    public bool IsPressedThisFrame(params KeyCode[] keys) => keys.Any(IsPressed);

    /// <summary>Mouse position in screen (pixel) coordinates.</summary>
    public Vector2 MouseScreen => _mouseScreenCommitted;

    /// <summary>
    /// Left mouse button.  False when the UI system has consumed the click
    /// (set <see cref="SuppressMouseLeft"/> = true before calling game systems).
    /// </summary>
    public bool IsMouseLeft => _mouseLeftCommitted && !SuppressMouseLeft;

    /// <summary>
    /// Raw left mouse button state, unaffected by <see cref="SuppressMouseLeft"/>.
    /// Used by the UI layer so UI widgets still respond when game input is blocked.
    /// </summary>
    public bool IsMouseLeftRaw => _mouseLeftCommitted;

    public bool IsMouseRight => _mouseRightCommitted;

    /// <summary>
    /// Set to true before running game systems to prevent mouse-left clicks
    /// from reaching gameplay code (e.g. when the mouse is over a UI panel).
    /// The UI layer reads <see cref="IsMouseLeftRaw"/> instead.
    /// </summary>
    public bool SuppressMouseLeft { get; set; }

    /// <summary>
    /// Characters typed this frame (from SDL_TEXTINPUT events).
    /// Suitable for populating text-input widgets.
    /// </summary>
    public string TextThisFrame => _textThisFrame;
}
