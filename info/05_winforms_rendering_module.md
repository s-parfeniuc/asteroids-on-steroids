# WinForms Integration and the Rendering Module

---

## The Core Principle: WinForms as a Thin Shell

WinForms should own exactly three things:

1. **The OS window** — the native window handle, title bar, border, resize events
2. **The message pump** — the Windows event loop that delivers input and repaint requests
3. **Input event forwarding** — KeyDown/KeyUp/MouseMove/MouseDown/MouseUp, forwarded immediately to the engine's InputSystem

That's it. WinForms draws nothing, runs no game logic, holds no game state. Every pixel on screen is produced by the engine and handed to WinForms as a finished `Bitmap` to display. WinForms is a display surface and an input pipe — nothing more.

This boundary is not just good design; it's necessary. The game loop runs on a background thread, but WinForms controls can only be touched from the UI thread. By keeping WinForms dumb, thread interaction is reduced to one safe, well-defined crossing point: the bitmap hand-off.

---

## The Rendering Pipeline

### The Problem with Drawing in OnPaint

WinForms triggers repaints by calling `OnPaint` on the **UI thread** (the main thread where `Application.Run` lives). Your game loop runs on a **background thread**. If you try to call GDI+ drawing functions from the game loop thread directly on the form's `Graphics`, you'll get cross-thread exceptions or data races.

The naive fix — using `form.Invoke(...)` to marshal every draw call to the UI thread — is the wrong solution. It means the background thread blocks waiting for the UI thread to finish drawing, and the UI thread blocks on every `Invoke` call, making both threads run sequentially anyway. You've gained nothing over single-threading while adding complexity.

### The Back-Buffer Bitmap Approach

The correct solution: the game loop draws to an off-screen `Bitmap` it owns entirely, then hands the finished `Bitmap` to the UI thread atomically. The UI thread's only job is to blit that `Bitmap` to the form's surface in `OnPaint`.

```
Game Thread (background)              UI Thread (main, message pump)
─────────────────────────             ──────────────────────────────
Create Graphics from backBuffer
Clear(black)
Draw all entities
Draw HUD
Dispose Graphics
                                      ← Invalidate() wakes up the pump
Swap: frontBuffer ↔ backBuffer  ───→  OnPaint: DrawImage(frontBuffer, 0, 0)
Invalidate()
Sleep(remaining frame time)
```

Two `Bitmap` instances exist at all times:
- **Back buffer** — exclusively owned by the game thread; it draws here every frame
- **Front buffer** — the last completed frame; the UI thread reads this in `OnPaint`

The swap is a single `Interlocked.Exchange` call — atomic, no locks:

```csharp
// Game thread, after drawing is complete:
Bitmap oldFront = Interlocked.Exchange(ref _frontBuffer, _backBuffer);
_backBuffer = oldFront;  // reuse the old front buffer as next frame's back buffer
gameWindow.Invalidate(); // ask UI thread to repaint
```

`Invalidate()` is safe to call from any thread — it just posts a message to the window's message queue; it does not touch any GDI resources directly.

### Why OnPaint Stays Simple

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    Bitmap? bmp = Volatile.Read(ref _frontBuffer);
    if (bmp != null)
        e.Graphics.DrawImage(bmp, 0, 0);
}

// Suppress WinForms erasing the background before each paint —
// we cover every pixel ourselves so the erase is wasted work (and causes flicker).
protected override void OnPaintBackground(PaintEventArgs e) { }
```

`Volatile.Read` ensures the compiler and CPU don't cache a stale reference across threads. The `DrawImage` call is a simple memory copy from the front buffer onto the form's surface. The entire `OnPaint` body is ~2 lines.

---

## Setting Up the Form

The `GameWindow` class (our `Form` subclass) sets several style flags in its constructor that are critical for correct rendering:

```csharp
public GameWindow()
{
    // Tell WinForms we handle all painting ourselves.
    // Without this, WinForms tries to draw the control background,
    // causing flicker between our frames.
    SetStyle(
        ControlStyles.UserPaint |
        ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer,
        true);

    // Lock the window size — no resize handling needed for a game.
    FormBorderStyle = FormBorderStyle.FixedSingle;
    MaximizeBox     = false;

    ClientSize = new Size(1280, 720);  // drawable area, excluding title bar
    Text       = "Asteroids";
}
```

- `UserPaint` — we draw everything; WinForms draws nothing
- `AllPaintingInWmPaint` — combine erase + paint into one message, eliminating one source of flicker
- `OptimizedDoubleBuffer` — WinForms-level buffering as a backup; belt-and-suspenders with our manual bitmap swap

---

## The Graphics Object

The `Graphics` object is the GDI+ drawing context. There are **two separate `Graphics` objects** in play — it's important not to confuse them:

| | `Graphics` from | Used for | Owned by |
|---|---|---|---|
| **Draw Graphics** | `Graphics.FromImage(backBuffer)` | Drawing entities, HUD, background | Game thread |
| **Blit Graphics** | `OnPaint`'s `e.Graphics` | Copying front buffer to screen | UI thread |

The RenderSystem uses the Draw Graphics exclusively. It never touches the Blit Graphics. The `OnPaint` method uses the Blit Graphics exclusively. It never draws game content — it only does one `DrawImage`.

Creating the Draw Graphics each frame is cheap — it wraps an existing `Bitmap` and adds no allocation overhead. It must be disposed after drawing to release the GDI handle:

```csharp
using var g = Graphics.FromImage(_backBuffer);
g.SmoothingMode   = SmoothingMode.AntiAlias;   // smooth polygon edges
g.Clear(Color.Black);
// ... draw everything ...
// 'using' disposes g here automatically
```

### GDI+ Coordinate System

Origin `(0, 0)` is the **top-left corner**. X increases rightward, Y increases **downward**. This is the opposite of mathematical convention. When you rotate an object, positive angles rotate **clockwise** (not counter-clockwise as in math). Keep this in mind for all rotation logic in the physics and render systems.

---

## Camera Transform

The camera converts **world coordinates** (where entities live) to **screen coordinates** (where pixels are). It does this by applying a GDI+ transform to the `Graphics` object before drawing entities.

```csharp
void ApplyCamera(Graphics g, Camera cam)
{
    // Order matters: scale first (around world origin), then translate.
    g.TranslateTransform(-cam.Position.X + ScreenWidth  / 2f,
                         -cam.Position.Y + ScreenHeight / 2f);
    g.ScaleTransform(cam.Zoom, cam.Zoom);
}
```

With this transform active, you draw entities at their world positions and GDI+ handles converting to screen positions automatically. When drawing screen-space elements (HUD, score, lives), reset the transform first:

```csharp
// Draw world-space entities (affected by camera):
g.Save();
ApplyCamera(g, camera);
DrawEntities(g, world);
g.Restore();

// Draw screen-space HUD (not affected by camera):
DrawHUD(g, session);
```

`g.Save()` / `g.Restore()` push and pop the transform stack — the same concept as OpenGL's matrix stack.

---

## Draw Order: Layers

GDI+ has no depth buffer. Whatever is drawn last is on top (painter's algorithm). The RenderSystem sorts drawables by `Sprite.Layer` before drawing:

```
Layer -1:  background stars, tiled background
Layer  0:  terrain, static world geometry
Layer  1:  game entities (asteroids, bullets, ship)
Layer  2:  particle effects, explosions
Layer  9:  HUD overlay (always last, always on top)
```

The sort happens once per frame over the set of visible entities. For a few hundred entities this is negligible cost.

---

## Input Handling

### WinForms Events → InputSystem

Wire the form's input events to the InputSystem in the `GameWindow` constructor. These fire on the UI thread:

```csharp
KeyDown   += (_, e) => InputSystem.OnKeyDown(e.KeyCode);
KeyUp     += (_, e) => InputSystem.OnKeyUp(e.KeyCode);
MouseMove += (_, e) => InputSystem.OnMouseMove(e.Location);
MouseDown += (_, e) => InputSystem.OnMouseButton(e.Button, pressed: true);
MouseUp   += (_, e) => InputSystem.OnMouseButton(e.Button, pressed: false);
```

### Thread Safety for Input

WinForms events fire on the UI thread. The game systems read input on the game thread. Without synchronization, a key could be added to `_held` by the UI thread while the game thread is reading it.

The simplest correct solution: **double-buffered input state**. The InputSystem has a `_pending` set that the UI thread writes into, and a `_committed` set that the game thread reads from. At the start of each frame, the game thread atomically swaps pending into committed:

```csharp
class InputSystem
{
    private readonly HashSet<Keys> _pending   = new();
    private readonly HashSet<Keys> _committed = new();
    private readonly HashSet<Keys> _pressedThisFrame  = new();
    private readonly HashSet<Keys> _releasedThisFrame = new();
    private readonly object _lock = new();

    // Called from UI thread:
    public void OnKeyDown(Keys key) { lock (_lock) _pending.Add(key);    }
    public void OnKeyUp  (Keys key) { lock (_lock) _pending.Remove(key); }

    // Called from game thread at start of each frame:
    public void BeginFrame()
    {
        lock (_lock)
        {
            _pressedThisFrame .Clear();
            _releasedThisFrame.Clear();

            foreach (var k in _pending)
                if (!_committed.Contains(k)) _pressedThisFrame.Add(k);
            foreach (var k in _committed)
                if (!_pending.Contains(k))   _releasedThisFrame.Add(k);

            _committed.Clear();
            _committed.UnionWith(_pending);
        }
    }

    // Called from game thread (game systems):
    public bool IsHeld   (Keys key) => _committed.Contains(key);
    public bool IsPressed(Keys key) => _pressedThisFrame.Contains(key);
    public bool IsReleased(Keys key) => _releasedThisFrame.Contains(key);
}
```

The lock is held for microseconds. There is no contention problem here.

### Mouse Coordinates: Screen → World

WinForms gives mouse position in **screen (pixel) coordinates**. To know where in the world the mouse is pointing, you must invert the camera transform:

```csharp
public Vector2 MouseWorldPosition(Camera cam)
{
    // Invert the camera transform:
    // screen → world is the reverse of world → screen
    return new Vector2(
        (_mouseScreen.X - ScreenWidth  / 2f) / cam.Zoom + cam.Position.X,
        (_mouseScreen.Y - ScreenHeight / 2f) / cam.Zoom + cam.Position.Y
    );
}
```

### No WinForms Controls for Game UI

Do not use `Button`, `Label`, `Panel`, or any other WinForms control for game UI elements (score display, health bars, menus). As you already saw, their layout behavior is unreliable in Mono, and even on Windows they create artifacts when mixed with manual GDI+ drawing.

Draw all game UI directly with GDI+:

```csharp
// Score display — drawn in HUD layer (screen space, no camera transform)
g.DrawString($"SCORE: {session.Score}", _hudFont, Brushes.White, 16f, 16f);

// Lives display
for (int i = 0; i < session.Lives; i++)
    DrawMiniShip(g, 16 + i * 24, 48);
```

This is also more flexible: you can animate, fade, and style UI elements with full GDI+ control.

---

## What the GameWindow Class Owns

Summarizing everything above into one list:

**GameWindow is responsible for:**
- Creating the OS window (title, size, border style, style flags)
- Allocating the two Bitmap buffers at startup
- Exposing the back buffer for the game thread to draw into
- Performing the buffer swap and calling `Invalidate()`
- `OnPaint`: blitting the front buffer to the form surface
- `OnPaintBackground`: suppressed (empty override)
- Wiring keyboard/mouse events to InputSystem
- Signaling the game loop to stop when the form closes (`FormClosing` event)

**GameWindow is NOT responsible for:**
- Any game logic
- Any rendering logic (it doesn't know what gets drawn or how)
- Managing entities, components, or systems
- The game loop thread (that lives in `GameLoop`)

---

## Complete Frame Flow (Both Threads)

```
STARTUP
  Main thread: new GameWindow() → allocates buffers, sets styles
  Main thread: new GameLoop(window, world, systems) → creates but doesn't start thread
  Main thread: gameLoop.Start() → launches background thread
  Main thread: Application.Run(window) → blocks here, runs message pump forever

─────────────────────────────────────────────────────────────────
GAME THREAD (every frame, ~16ms at 60fps)
─────────────────────────────────────────────────────────────────
  1. dt = stopwatch.Elapsed - lastTime; lastTime = now
  2. inputSystem.BeginFrame()          → commit pending key state
  3. eventBus.Flush()                  → dispatch queued events
  4. foreach system: system.Update(world, dt)
  5. world.FlushDeferred()             → destroy marked entities
  6. using g = Graphics.FromImage(backBuffer):
       g.Clear(Black)
       g.Save(); ApplyCamera(g, camera)
       renderSystem.Draw(world, g)     → draw all Transform+Sprite entities
       g.Restore()
       hudSystem.Draw(world, g)        → draw score/lives in screen space
  7. Interlocked.Exchange(frontBuffer ↔ backBuffer)
  8. window.Invalidate()               → wake up UI thread to repaint
  9. sleep remaining frame budget

─────────────────────────────────────────────────────────────────
UI THREAD (whenever OS schedules a repaint)
─────────────────────────────────────────────────────────────────
  OnKeyDown/Up:   inputSystem.OnKeyDown/Up(key)
  OnMouseMove:    inputSystem.OnMouseMove(pos)
  OnPaint:        e.Graphics.DrawImage(frontBuffer, 0, 0)
```

---

## Mono-Specific Notes

When developing on Ubuntu with Mono and periodically testing on .NET 8 Windows:

- `ControlStyles.OptimizedDoubleBuffer` behaves differently on Mono/GTK. If you see flickering on Linux that's absent on Windows, it's this. The manual back-buffer swap we use is the correct fix — it doesn't rely on WinForms double buffering at all.
- `Invalidate()` + `OnPaint` round-trip latency is higher on Mono/GTK than on Windows GDI. The game still runs; frames may appear slightly less smooth. Not a bug.
- `Graphics.SmoothingMode` and `Graphics.InterpolationMode` are implemented via Cairo on Mono. Results are visually identical to Windows for simple shapes (lines, polygons, circles), which is all we draw.
- Never call `Refresh()` from the game thread — it tries to paint synchronously on the calling thread, which is wrong. `Invalidate()` only.

---

## Sources

- **Microsoft Docs — Control.Paint and DoubleBuffering**: https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/double-buffered-graphics
- **Microsoft Docs — Graphics.FromImage**: https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.fromimage
- **Microsoft Docs — Control.Invalidate**: https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invalidate
- **Microsoft Docs — ControlStyles enum**: https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.controlstyles
- **Mono WinForms implementation notes**: https://www.mono-project.com/docs/gui/winforms/
- **Threading in WinForms** — Jon Skeet: https://jonskeet.uk/csharp/threads/winforms.html
