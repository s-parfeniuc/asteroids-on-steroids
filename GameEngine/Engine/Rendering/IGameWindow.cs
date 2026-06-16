using System.Numerics;
using AsteroidsEngine.Engine.Input;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Platform-agnostic game window contract.
///
/// The game loop drives rendering by:
///   1. PollEvents() — drains OS events, fires key/mouse callbacks.
///   2. Draw to Renderer (IRenderer) — platform backend executes draw calls.
///   3. Present() — swap/flush the completed frame to the display.
///
/// Current implementations:
///   SdlGameWindow — Linux/Mac/Windows via SDL2 + SkiaSharp GPU (OpenGL).
/// </summary>
public interface IGameWindow : IDisposable
{
    int  Width       { get; }
    int  Height      { get; }
    bool ShouldClose { get; }

    /// <summary>Immediate-mode renderer backed by this window's drawing surface.</summary>
    IRenderer Renderer { get; }

    event Action<KeyCode>?           KeyDown;
    event Action<KeyCode>?           KeyUp;
    event Action<Vector2>?           MouseMoved;
    event Action<MouseButton, bool>? MouseButtonChanged;
    /// <summary>Fired for each SDL_TEXTINPUT event (UTF-8 characters typed).</summary>
    event Action<string>?            TextInput;

    /// <summary>Drain pending OS events and fire input callbacks.</summary>
    void PollEvents();

    /// <summary>Flush GPU commands and swap the front/back buffers.</summary>
    void Present();
}
