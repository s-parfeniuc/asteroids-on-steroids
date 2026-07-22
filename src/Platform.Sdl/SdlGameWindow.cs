using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Silk.NET.SDL;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Platform.Skia;
using EngineKey   = AsteroidsEngine.Engine.Input.KeyCode;
using EngineMouse = AsteroidsEngine.Engine.Input.MouseButton;
using SdlApi      = Silk.NET.SDL.Sdl;

namespace AsteroidsEngine.Platform.Sdl;

/// <summary>
/// SDL2 window with a SkiaSharp GPU (OpenGL) backend.
///
/// Init sequence:
///   SDL_GL_SetAttribute (OpenGL 3.3 core + 8-bit stencil)
///   → SDL_CreateWindow (WindowFlags.OpenGL)
///   → SDL_GL_CreateContext
///   → GRGlInterface.Create (routes SDL_GL_GetProcAddress)
///   → GRContext.CreateGl
///   → SKSurface on the default framebuffer (id 0, GL_RGBA8)
///
/// Present: canvas.Flush → GRContext.Flush → SDL_GL_SwapWindow.
/// No pixel copy; Skia rasterises directly on the GPU.
/// </summary>
public sealed unsafe class SdlGameWindow : IGameWindow
{
    private readonly SdlApi _sdl;
    private Window* _window;
    private void*   _glContext;
    private bool    _shouldClose;
    private bool    _disposed;

    private readonly GRContext    _grContext;
    private readonly SKSurface    _surface;
    private readonly SkiaRenderer _skiaRenderer;

    public int  Width       { get; }
    public int  Height      { get; }
    public bool ShouldClose => _shouldClose;
    public IRenderer Renderer => _skiaRenderer;

    public event Action<EngineKey>?         KeyDown;
    public event Action<EngineKey>?         KeyUp;
    public event Action<Vector2>?           MouseMoved;
    public event Action<EngineMouse, bool>? MouseButtonChanged;
    public event Action<string>?            TextInput;

    /// <summary>The primary display's current resolution. (Fullscreen uses FullscreenDesktop, which
    /// on macOS covers the display and auto-hides the menu bar, so no usable-bounds inset is needed.)</summary>
    public static (int Width, int Height) QueryDisplaySize(int displayIndex = 0)
    {
        var sdl = SdlApi.GetApi();
        sdl.Init(SdlApi.InitVideo);
        DisplayMode mode = default;
        sdl.GetCurrentDisplayMode(displayIndex, ref mode);
        return (mode.W > 0 ? mode.W : 1920, mode.H > 0 ? mode.H : 1080);
    }

    public SdlGameWindow(string title, int width, int height, bool fullscreen = false)
    {
        _sdl = SdlApi.GetApi();

        if (_sdl.Init(SdlApi.InitVideo | SdlApi.InitEvents) < 0) Throw("SDL_Init");

        if (fullscreen)
        {
            DisplayMode mode = default;
            _sdl.GetCurrentDisplayMode(0, ref mode);
            if (mode.W > 0) width  = mode.W;
            if (mode.H > 0) height = mode.H;
        }

        Width  = width;
        Height = height;

        // GL attributes must be set before window creation.
        // Skia requires a stencil buffer for path rendering.
        _sdl.GLSetAttribute(GLattr.ContextMajorVersion, 3);
        _sdl.GLSetAttribute(GLattr.ContextMinorVersion, 3);
        _sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GLprofile.Core);
        _sdl.GLSetAttribute(GLattr.StencilSize, 8);
        _sdl.GLSetAttribute(GLattr.Doublebuffer, 1);

        var winFlags = WindowFlags.Shown | WindowFlags.Opengl;
        if (fullscreen) winFlags |= WindowFlags.FullscreenDesktop;
        _window = _sdl.CreateWindow(title,
            SdlApi.WindowposCentered, SdlApi.WindowposCentered,
            width, height,
            (uint)winFlags);
        if (_window == null) Throw("SDL_CreateWindow");

        _glContext = _sdl.GLCreateContext(_window);
        if (_glContext == null) Throw("SDL_GL_CreateContext");

        _sdl.GLMakeCurrent(_window, _glContext);
        _sdl.GLSetSwapInterval(0); // Game loop does its own frame cap; disable driver vsync.

        // GRGlInterface.CreateOpenGl() uses GLX and crashes on Wayland/EGL contexts.
        // eglGetProcAddress works for both desktop GL and GLES and covers both backends.
        // Fall back to GLX (X11) if EGL is not available.
        var glInterface = CreateGlInterface()
            ?? throw new InvalidOperationException("Could not create GRGlInterface (EGL/GLX)");

        _grContext = GRContext.CreateGl(glInterface)
            ?? throw new InvalidOperationException("GRContext.CreateGl returned null");

        // Describe the default framebuffer: id=0, internal format GL_RGBA8 (0x8058).
        var fbInfo = new GRGlFramebufferInfo(0, 0x8058);
        var rt     = new GRBackendRenderTarget(width, height, sampleCount: 0, stencilBits: 8, fbInfo);

        // BottomLeft: OpenGL's origin is bottom-left; Skia's is top-left.
        _surface = SKSurface.Create(_grContext, rt, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("SKSurface.Create returned null");

        _skiaRenderer = new SkiaRenderer(_surface, width, height);   // needs the surface for snapshot-based post FX
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
                {
                    int ksym = (int)ev.Key.Keysym.Sym;
                    if (ev.Key.Repeat == 0)
                    {
                        var key = MapKey(ksym);
                        if (key.HasValue) KeyDown?.Invoke(key.Value);
                    }
                    // Derive printable char from sym — works for all key-repeat rates,
                    // bypasses SDL text-input / IME (sufficient for ASCII editor names).
                    // Suppress when a modifier is held so Ctrl+S doesn't insert 's' into text fields.
                    bool modHeld = (ev.Key.Keysym.Mod & 0x3C0) != 0; // KMOD_CTRL | KMOD_ALT
                    char ch = SdlSymToChar(ksym);
                    if (ch != '\0' && !modHeld) TextInput?.Invoke(ch.ToString());
                    break;
                }
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
                // SDL_TEXTINPUT events are handled via KeyDown sym mapping above.
            }
        }
    }

    public void Present()
    {
        _surface.Canvas.Flush();
        _grContext.Flush();
        _sdl.GLSwapWindow(_window);
    }

    // Maps an SDL keysym to a printable ASCII char for text-input widgets.
    // Lowercase a-z, digits 0-9, underscore, hyphen, space — sufficient for asset names.
    private static char SdlSymToChar(int sym) => sym switch
    {
        >= 97 and <= 122 => (char)sym,    // a-z
        >= 48 and <= 57  => (char)sym,    // 0-9
        95               => '_',
        45               => '-',
        46               => '.',
        32               => ' ',
        _                => '\0'
    };

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
        91               => EngineKey.LeftBracket,
        93               => EngineKey.RightBracket,
        1073742048       => EngineKey.Control,   // SDLK_LCTRL  (scancode 224)
        1073742052       => EngineKey.Control,   // SDLK_RCTRL  (scancode 228)
        1073742049       => EngineKey.Shift,     // SDLK_LSHIFT (scancode 225)
        1073742053       => EngineKey.Shift,     // SDLK_RSHIFT (scancode 229)
        1073742050       => EngineKey.Alt,       // SDLK_LALT   (scancode 226)
        1073742054       => EngineKey.Alt,       // SDLK_RALT   (scancode 230)
        _                => null
    };

    // Proc-address getters for EGL (Wayland) and GLX (X11).
    // GRGlInterface.CreateOpenGl() uses GLX internally and crashes on EGL/Wayland contexts,
    // so we load the platform proc-getter ourselves and route through GRGlInterface.Create().
    private static GRGlInterface? CreateGlInterface()
    {
        // EGL — Wayland and modern X11 with EGL.
        if (NativeLibrary.TryLoad("libEGL.so.1", out var egl) &&
            NativeLibrary.TryGetExport(egl, "eglGetProcAddress", out var eglFn))
        {
            var getter = Marshal.GetDelegateForFunctionPointer<ProcGetter>(eglFn);
            var iface  = GRGlInterface.Create(name => getter(name));
            if (iface?.Validate() == true) return iface;
        }
        // GLX — classic X11.
        if (NativeLibrary.TryLoad("libGL.so.1", out var gl) &&
            NativeLibrary.TryGetExport(gl, "glXGetProcAddressARB", out var glxFn))
        {
            var getter = Marshal.GetDelegateForFunctionPointer<ProcGetter>(glxFn);
            var iface  = GRGlInterface.Create(name => getter(name));
            if (iface?.Validate() == true) return iface;
        }
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ProcGetter([MarshalAs(UnmanagedType.LPStr)] string name);

    private void Throw(string call)
    {
        string err = Marshal.PtrToStringAnsi((IntPtr)_sdl.GetError()) ?? "unknown error";
        throw new InvalidOperationException($"{call} failed: {err}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _skiaRenderer.Dispose();   // SKPaint objects
        _surface.Dispose();        // SKCanvas + GPU surface
        _grContext.Dispose();      // Skia GL context

        if (_glContext != null) { _sdl.GLDeleteContext(_glContext); _glContext = null; }
        if (_window   != null) { _sdl.DestroyWindow(_window);      _window   = null; }

        _sdl.Quit();
        _sdl.Dispose();
    }
}
