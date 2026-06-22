#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Input;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// The WinForms Form. Owns the two bitmaps (front/back buffer),
/// wires input events to InputSystem, and blits the front buffer in OnPaint.
///
/// Responsibilities (and nothing else):
///   - Create the OS window
///   - Allocate and swap the double-buffer bitmaps
///   - Forward keyboard/mouse events to InputSystem
///   - Blit front buffer to screen in OnPaint
///   - Signal GameLoop to stop on close
/// </summary>
public sealed class GameWindow : Form
{
    private readonly InputSystem _input;
    private readonly GameLoop    _loop;

    // Double buffer: game thread draws to _backBuffer,
    // then swaps with _frontBuffer via Interlocked.Exchange.
    private Bitmap _backBuffer;
    private Bitmap _frontBuffer;

    public int GameWidth  { get; }
    public int GameHeight { get; }

    public GameWindow(int width, int height, InputSystem input, GameLoop loop)
    {
        GameWidth  = width;
        GameHeight = height;
        _input     = input;
        _loop      = loop;

        // --- Window setup ---
        Text            = "Asteroids";
        ClientSize      = new Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;

        // Suppress WinForms background erase and painting.
        // We paint every pixel ourselves via DrawImage.
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        // --- Allocate buffers ---
        _backBuffer  = new Bitmap(width, height);
        _frontBuffer = new Bitmap(width, height);

        // --- Wire GameLoop's draw callback ---
        _loop.OnDraw = DrawFrame;

        // --- Wire input ---
        KeyDown   += (_, e) => _input.OnKeyDown((KeyCode)(int)e.KeyCode);
        KeyUp     += (_, e) => _input.OnKeyUp((KeyCode)(int)e.KeyCode);
        MouseMove += (_, e) => _input.OnMouseMove(e.Location);
        MouseDown += (_, e) => _input.OnMouseButton(WinToEngine(e.Button), pressed: true);
        MouseUp   += (_, e) => _input.OnMouseButton(WinToEngine(e.Button), pressed: false);

        // Stop the game loop when the window closes.
        FormClosing += (_, _) => _loop.Stop();
    }

    // -------------------------------------------------------------------------
    // Draw callback — called from game thread each frame
    // -------------------------------------------------------------------------

    private void DrawFrame()
    {
        // Create a Graphics context for the back buffer and hand it to callers.
        using var g = Graphics.FromImage(_backBuffer);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.InterpolationMode  = InterpolationMode.NearestNeighbor;
        g.Clear(Color.Black);

        // Fire the application-level draw (RenderSystem, HUD, etc.).
        OnGameDraw?.Invoke(g);

        // Atomically swap buffers: the just-drawn back buffer becomes the
        // new front buffer; the old front buffer becomes the next back buffer.
        var old = Interlocked.Exchange(ref _frontBuffer, _backBuffer);
        _backBuffer = old;

        // Ask the UI thread to repaint. Safe to call from any thread.
        Invalidate();
    }

    /// <summary>
    /// Register game rendering here. Called each frame with the back-buffer
    /// Graphics. RenderSystem and HUDSystem draw into this.
    /// </summary>
    public Action<Graphics>? OnGameDraw { get; set; }

    // -------------------------------------------------------------------------
    // WinForms paint — UI thread only
    // -------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        // Blit the front buffer (last completed frame) to the form surface.
        // Volatile.Read prevents a stale cached reference being used.
        var bmp = Volatile.Read(ref _frontBuffer);
        e.Graphics.DrawImage(bmp, 0, 0);
    }

    // Suppress default background erase — we cover every pixel ourselves.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MouseButton WinToEngine(MouseButtons btn) => btn switch
    {
        MouseButtons.Left   => MouseButton.Left,
        MouseButtons.Right  => MouseButton.Right,
        MouseButtons.Middle => MouseButton.Middle,
        _                   => MouseButton.Left,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backBuffer.Dispose();
            _frontBuffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
#endif
