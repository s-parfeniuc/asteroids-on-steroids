using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Silk.NET.SDL;
using AsteroidsEngine.Engine.Rendering;
using EngineKey   = AsteroidsEngine.Engine.Input.KeyCode;
using EngineMouse = AsteroidsEngine.Engine.Input.MouseButton;
using SdlRenderer = Silk.NET.SDL.Renderer;   // distinct from the engine's IRenderer
using SdlApi      = Silk.NET.SDL.Sdl;        // namespace ends in 'Sdl', so alias the API type

namespace AsteroidsEngine.Platform.Sdl;

/// <summary>
/// SDL2 window + SkiaSharp renderer — the SDL/Skia backend of the Platform
/// Abstraction Layer. Owns the OS window, the input event pump, and the Skia
/// drawing surface. Games draw through <see cref="Renderer"/> and call
/// <see cref="Present"/> once per frame; no game code touches Skia or SDL.
/// </summary>
public sealed unsafe class SdlGameWindow : IGameWindow
{
    private readonly SdlApi _sdl;
    private Window*      _window;
    private SdlRenderer* _renderer;
    private Texture*     _texture;
    private bool         _shouldClose;
    private bool         _disposed;

    private readonly SKBitmap     _bitmap;
    private readonly SKCanvas     _canvas;
    private readonly SkiaRenderer _skiaRenderer;

    public int  Width       { get; }
    public int  Height      { get; }
    public bool ShouldClose => _shouldClose;

    /// <summary>Engine-facing immediate-mode renderer backed by this window's Skia surface.</summary>
    public IRenderer Renderer => _skiaRenderer;

    public event Action<EngineKey>?         KeyDown;
    public event Action<EngineKey>?         KeyUp;
    public event Action<Vector2>?           MouseMoved;
    public event Action<EngineMouse, bool>? MouseButtonChanged;

    public SdlGameWindow(string title, int width, int height)
    {
        Width  = width;
        Height = height;
        _sdl   = SdlApi.GetApi();

        if (_sdl.Init(SdlApi.InitVideo | SdlApi.InitEvents) < 0) Throw("SDL_Init");

        _window = _sdl.CreateWindow(title,
            SdlApi.WindowposCentered, SdlApi.WindowposCentered,
            width, height, (uint)WindowFlags.Shown);
        if (_window == null) Throw("SDL_CreateWindow");

        _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Accelerated);
        if (_renderer == null) Throw("SDL_CreateRenderer");

        _texture = _sdl.CreateTexture(_renderer,
            SdlApi.PixelformatArgb8888, (int)TextureAccess.Streaming, width, height);
        if (_texture == null) Throw("SDL_CreateTexture");

        _bitmap       = new SKBitmap(width, height);
        _canvas       = new SKCanvas(_bitmap);
        _skiaRenderer = new SkiaRenderer(_canvas);
    }

    public void PollEvents()
    {
        Event ev = default;
        while (_sdl.PollEvent(ref ev) != 0)
        {
            switch ((EventType)ev.Type)
            {
                case EventType.Quit:
                    _shouldClose = true;
                    break;
                case EventType.Keydown:
                    if (ev.Key.Repeat == 0)
                    {
                        var key = MapKey((int)ev.Key.Keysym.Sym);
                        if (key.HasValue) KeyDown?.Invoke(key.Value);
                    }
                    break;
                case EventType.Keyup:
                    var upKey = MapKey((int)ev.Key.Keysym.Sym);
                    if (upKey.HasValue) KeyUp?.Invoke(upKey.Value);
                    break;
                case EventType.Mousemotion:
                    MouseMoved?.Invoke(new Vector2(ev.Motion.X, ev.Motion.Y));
                    break;
                case EventType.Mousebuttondown:
                    if (ev.Button.Button == 1) MouseButtonChanged?.Invoke(EngineMouse.Left,  true);
                    if (ev.Button.Button == 3) MouseButtonChanged?.Invoke(EngineMouse.Right, true);
                    break;
                case EventType.Mousebuttonup:
                    if (ev.Button.Button == 1) MouseButtonChanged?.Invoke(EngineMouse.Left,  false);
                    if (ev.Button.Button == 3) MouseButtonChanged?.Invoke(EngineMouse.Right, false);
                    break;
            }
        }
    }

    /// <summary>Upload the current Skia frame to the window and present it.</summary>
    public void Present() => PresentFrame(_bitmap.GetPixels(), _bitmap.RowBytes);

    public void PresentFrame(IntPtr pixels, int stride)
    {
        _sdl.UpdateTexture(_texture, null, (void*)pixels, stride);
        _sdl.RenderClear(_renderer);
        _sdl.RenderCopy(_renderer, _texture, null, null);
        _sdl.RenderPresent(_renderer);
    }

    private static EngineKey? MapKey(int sym) => sym switch
    {
        >= 97 and <= 122 => (EngineKey)(sym - 32),
        >= 48 and <= 57  => (EngineKey)sym,
        1073741906       => EngineKey.Up,
        1073741905       => EngineKey.Down,
        1073741904       => EngineKey.Left,
        1073741903       => EngineKey.Right,
        32               => EngineKey.Space,
        27               => EngineKey.Escape,
        13               => EngineKey.Enter,
        9                => EngineKey.Tab,
        8                => EngineKey.Back,
        _                => null
    };

    private void Throw(string call)
    {
        string err = Marshal.PtrToStringAnsi((IntPtr)_sdl.GetError()) ?? "unknown error";
        throw new InvalidOperationException($"{call} failed: {err}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _skiaRenderer.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();

        if (_texture  != null) { _sdl.DestroyTexture(_texture);   _texture  = null; }
        if (_renderer != null) { _sdl.DestroyRenderer(_renderer); _renderer = null; }
        if (_window   != null) { _sdl.DestroyWindow(_window);     _window   = null; }

        _sdl.Quit();
        _sdl.Dispose();
    }
}
