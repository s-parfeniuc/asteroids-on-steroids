namespace AsteroidsEngine.Engine.Input;

/// <summary>
/// Engine-defined key identifiers. Values intentionally match
/// System.Windows.Forms.Keys so GameWindow can cast directly:
///   inputSystem.OnKeyDown((KeyCode)e.KeyCode)
/// </summary>
public enum KeyCode
{
    // Alphabet
    A = 65, B = 66, C = 67, D = 68, E = 69, F = 70,
    G = 71, H = 72, I = 73, J = 74, K = 75, L = 76,
    M = 77, N = 78, O = 79, P = 80, Q = 81, R = 82,
    S = 83, T = 84, U = 85, V = 86, W = 87, X = 88,
    Y = 89, Z = 90,

    // Digits
    D0 = 48, D1 = 49, D2 = 50, D3 = 51, D4 = 52,
    D5 = 53, D6 = 54, D7 = 55, D8 = 56, D9 = 57,

    // Navigation
    Left  = 37,
    Up    = 38,
    Right = 39,
    Down  = 40,

    // Common
    Space  = 32,
    Enter  = 13,
    Escape = 27,
    Tab    = 9,
    Back   = 8,

    // Modifiers
    Shift       = 16,
    Control     = 17,
    Alt         = 18,
    ShiftLeft   = 160,
    ShiftRight  = 161,

    // Function keys
    F1  = 112, F2  = 113, F3  = 114, F4  = 115,
    F5  = 116, F6  = 117, F7  = 118, F8  = 119,
    F9  = 120, F10 = 121, F11 = 122, F12 = 123,
}

public enum MouseButton
{
    Left   = 1,
    Right  = 2,
    Middle = 4,
}
