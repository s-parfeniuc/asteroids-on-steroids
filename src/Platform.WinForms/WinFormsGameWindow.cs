using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;               // SKGLControl
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Platform.Skia;         // the shared SkiaRenderer

namespace AsteroidsEngine.Platform.WinForms;

/// <summary>
/// WinForms + SkiaSharp (GPU) implementation of <see cref="IGameWindow"/>. A borderless (optionally
/// fullscreen) <see cref="Form"/> that hosts an <see cref="SKGLControl"/> (an OpenGL surface inside the
/// form). We drive it from the engine's own loop rather than the control's paint event: the GL context
/// is created once, a persistent <see cref="SKSurface"/> is built on the default framebuffer, and the
/// shared <see cref="SkiaRenderer"/> draws into it — exactly as the SDL backend does. <see cref="Present"/>
/// flushes and swaps. So the WinForms build gets every renderer feature (batching, IPostEffects, …) for
/// free, and GDI+ is gone.
///
/// WINDOWS-ONLY: this file is net8.0-windows and depends on the Windows Desktop SDK + SkiaSharp GL views;
/// it neither builds nor runs on the Linux dev box. See apps/Game.WinForms/README.md for the build/verify
/// steps. The SDL build is the source of truth; this backend is written correct-by-construction and is
/// verified by the owner on Windows.
/// </summary>
public sealed class WinFormsGameWindow : Form, IGameWindow
{
    private readonly int          _width, _height;
    private readonly SKGLControl  _gl;
    private GRContext             _grContext = null!;
    private SKSurface             _surface   = null!;
    private SkiaRenderer          _renderer  = null!;
    private bool                  _shouldClose;

    public WinFormsGameWindow(string title, int width, int height, bool fullscreen = true)
    {
        _width  = width;
        _height = height;

        Text            = title;
        FormBorderStyle = FormBorderStyle.None;
        if (fullscreen)
        {
            // WorkingArea (not Bounds) so we cover the drawable desktop and never draw under the taskbar.
            StartPosition = FormStartPosition.Manual;
            Bounds        = Screen.PrimaryScreen!.WorkingArea;
            TopMost       = true;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize    = new Size(width, height);
        }
        KeyPreview = true;

        // The GL surface fills the whole client area; its client coords are the render coords.
        _gl = new SKGLControl { Dock = DockStyle.Fill };
        Controls.Add(_gl);

        // ── Input: raise the PAL events from the control's events (it holds focus) ──
        _gl.KeyDown   += (_, e) => _keyDown?.Invoke((KeyCode)(int)e.KeyCode);
        _gl.KeyUp     += (_, e) => _keyUp?.Invoke((KeyCode)(int)e.KeyCode);
        _gl.MouseMove += (_, e) => MouseMoved?.Invoke(new Vector2(e.X, e.Y));
        _gl.MouseDown += (_, e) => MouseButtonChanged?.Invoke(WinToEngine(e.Button), true);
        _gl.MouseUp   += (_, e) => MouseButtonChanged?.Invoke(WinToEngine(e.Button), false);
        _gl.KeyPress  += (_, e) =>
        {
            // Printable characters only, and not while Ctrl/Alt are held (mirrors the SDL backend).
            if (!char.IsControl(e.KeyChar) && (Control.ModifierKeys & (Keys.Control | Keys.Alt)) == 0)
                TextInput?.Invoke(e.KeyChar.ToString());
        };

        Show();
        Application.DoEvents();   // realise the form + control handles so the GL context exists
        _gl.Focus();              // ensure the control receives key input

        // ── Skia GPU surface on the control's GL context (mirrors SdlGameWindow) ──
        _gl.MakeCurrent();
        var glInterface = GRGlInterface.Create()   // WGL on Windows — no EGL/GLX dance needed
            ?? throw new InvalidOperationException("GRGlInterface.Create() returned null");
        _grContext = GRContext.CreateGl(glInterface)
            ?? throw new InvalidOperationException("GRContext.CreateGl returned null");

        // Default framebuffer: id 0, internal format GL_RGBA8 (0x8058); 8-bit stencil for path fills.
        var fbInfo = new GRGlFramebufferInfo(0, 0x8058);
        var rt     = new GRBackendRenderTarget(width, height, sampleCount: 0, stencilBits: 8, fbInfo);
        // BottomLeft: OpenGL's origin is bottom-left, Skia's is top-left.
        _surface = SKSurface.Create(_grContext, rt, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("SKSurface.Create returned null");

        _renderer = new SkiaRenderer(_surface, width, height);
    }

    /// <summary>Primary-monitor drawable resolution, mirroring SdlGameWindow.QueryDisplaySize.</summary>
    public static (int, int) QueryDisplaySize()
    {
        var b = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);   // minus the taskbar
        return (b.Width, b.Height);
    }

    // ── IGameWindow ─────────────────────────────────────────────────────────────
    public new int Width  => _width;    // 'new' hides Form.Width (outer window size); we want the render size
    public new int Height => _height;
    public bool ShouldClose => _shouldClose;
    public IRenderer Renderer => _renderer;

    // KeyDown/KeyUp are explicit — their names collide with Form.KeyDown/KeyUp.
    private Action<KeyCode>? _keyDown, _keyUp;
    event Action<KeyCode>? IGameWindow.KeyDown { add => _keyDown += value; remove => _keyDown -= value; }
    event Action<KeyCode>? IGameWindow.KeyUp   { add => _keyUp   += value; remove => _keyUp   -= value; }

    public event Action<Vector2>?           MouseMoved;
    public event Action<MouseButton, bool>? MouseButtonChanged;
    public event Action<string>?            TextInput;

    public void PollEvents() => Application.DoEvents();   // pump the Win32 queue; the wired handlers fire

    public void Present()
    {
        _gl.MakeCurrent();
        _surface.Canvas.Flush();
        _grContext.Flush();
        _gl.SwapBuffers();
    }

    // ── Form overrides ──────────────────────────────────────────────────────────
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _shouldClose = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer?.Dispose();
            _surface?.Dispose();
            _grContext?.Dispose();
            _gl?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static MouseButton WinToEngine(MouseButtons b) => b switch
    {
        MouseButtons.Left   => MouseButton.Left,
        MouseButtons.Right  => MouseButton.Right,
        MouseButtons.Middle => MouseButton.Middle,
        _                   => MouseButton.Left,
    };
}
