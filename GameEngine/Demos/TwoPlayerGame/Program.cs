// Two Player ECS Demo — dotnet build/run
//   Build:  cd GameEngine/Demos/TwoPlayerGame && dotnet build
//   Run:    dotnet run
//   Needs:  libsdl2-2.0-0  (sudo apt install libsdl2-2.0-0)
//
// Controls (in-game):
//   P1 (blue)  — WASD move   Space dash   F shoot
//   P2 (red)   — Arrow keys  Enter dash   L shoot
//   R          — clear all balls
//   Escape     — pause / resume

using System.Diagnostics;
using System.Numerics;
using SkiaSharp;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Systems;

const int W = 1280, H = 720;

using var window = new TwoPlayerGame.SdlGameWindow("Two Player Game", W, H);

var input = new InputSystem();
window.KeyDown += k => input.OnKeyDown(k);
window.KeyUp += k => input.OnKeyUp(k);
window.MouseMoved += p => input.OnMouseMove(new Vector2(p.X, p.Y));
window.MouseButtonChanged += (btn, pr) => input.OnMouseButton(btn, pr);

var bitmap = new SKBitmap(W, H);
var canvas = new SKCanvas(bitmap);

var state = AppState.Menu;
var menu = new MenuScreen(W, H);
var overlay = new OverlayPainter(W, H);
GameSession? session = null;

var sw = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;

while (!window.ShouldClose)
{
    window.PollEvents();

    long now = sw.ElapsedTicks;
    double dt = Math.Min((double)(now - lastTicks) / Stopwatch.Frequency, 0.1);
    lastTicks = now;

    input.BeginFrame();

    switch (state)
    {
        case AppState.Menu:
            menu.Update(input);
            if (menu.StartRequested)
            {
                session = new GameSession(W, H, input, menu.Settings);
                state = AppState.Playing;
            }
            menu.Draw(canvas);
            break;

        case AppState.Playing:
            if (input.IsPressed(KeyCode.Escape))
            {
                state = AppState.Paused;
            }
            else
            {
                session!.Update(dt);
                if (session.IsGameOver) state = AppState.GameOver;
            }
            session!.Draw(canvas);
            break;

        case AppState.Paused:
            session!.Draw(canvas);
            overlay.DrawPause(canvas);
            if (input.IsPressed(KeyCode.Escape)) state = AppState.Playing;
            break;

        case AppState.GameOver:
            session!.Draw(canvas);
            overlay.DrawGameOver(canvas, session.WinnerIndex, session.P1Color, session.P2Color);
            if (input.IsPressed(KeyCode.Enter))
            {
                session = new GameSession(W, H, input, menu.Settings);
                state = AppState.Playing;
            }
            else if (input.IsPressed(KeyCode.Escape))
            {
                state = AppState.Menu;
            }
            break;
    }

    canvas.Flush();
    window.PresentFrame(bitmap.GetPixels(), bitmap.RowBytes);

    double fpsCap = menu.Settings.FpsCap;
    if (fpsCap > 0)
    {
        double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
        int sleep = (int)((1.0 / fpsCap - elapsed) * 1000);
        if (sleep > 1) Thread.Sleep(sleep);
    }
}

canvas.Dispose();
bitmap.Dispose();

// =============================================================================
// App state
// =============================================================================

enum AppState { Menu, Playing, Paused, GameOver }

// =============================================================================
// GameSettings
// =============================================================================

struct GameSettings
{
    public int MaxBalls;
    public float BallRadius;
    public int PlayerHealth;
    public int SpawnRateIndex; // 0=Slow 1=Normal 2=Fast 3=Chaos
    public int FpsCapIndex;    // 0=30   1=60    2=120  3=Uncapped

    public float SpawnInterval => SpawnRateIndex switch { 0 => 0.5f, 1 => 0.2f, 2 => 0.1f, _ => 0.05f };
    public double FpsCap => FpsCapIndex switch { 0 => 30.0, 1 => 60.0, 2 => 120.0, _ => 0.0 };
    public string SpawnLabel => SpawnRateIndex switch { 0 => "Slow", 1 => "Normal", 2 => "Fast", _ => "Chaos" };
    public string FpsLabel => FpsCapIndex switch { 0 => "30", 1 => "60", 2 => "120", _ => "∞" };

    public static GameSettings Default => new()
    {
        MaxBalls = 250,
        BallRadius = 3f,
        PlayerHealth = 100,
        SpawnRateIndex = 1,
        FpsCapIndex = 1
    };
}

// =============================================================================
// GameSession — owns world, bus, systems for one match
// =============================================================================

class GameSession
{
    private const float Restitution = 0.85f;

    private readonly World _world;
    private readonly EventBus _bus;
    private readonly ISystem[] _systems;
    private readonly CircleDrawSystem _drawSys;

    public Entity P1 { get; }
    public Entity P2 { get; }
    public SKColor P1Color { get; } = new(80, 140, 255);
    public SKColor P2Color { get; } = new(255, 80, 70);

    public bool IsGameOver { get; private set; }
    public int WinnerIndex { get; private set; } // 1, 2, or 0 (draw)

    public GameSession(int w, int h, InputSystem input, GameSettings s)
    {
        _world = new World();
        _bus = new EventBus();

        P1 = SpawnPlayer(_world, 1, new Vector2(w / 3f, h / 2f), P1Color, "P1", s.PlayerHealth);
        P2 = SpawnPlayer(_world, 2, new Vector2(2 * w / 3f, h / 2f), P2Color, "P2", s.PlayerHealth);

        var spawner = new BallSpawnSystem(w, h, s);
        _drawSys = new CircleDrawSystem(spawner, input, w, h, s.MaxBalls);

        _systems =
        [
            new PlayerInputSystem(input, P1, P2),
            new PhysicsSystem(),
            new MovementSystem(),
            new WallBounceSystem(w, h, s.BallRadius),
            spawner,
            new ShootSystem(input, P1, P2),
            new CollisionSystem(new SpatialGrid(128f), _bus) { ResolveOverlap = true },
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
            new DashSystem(input, P1, P2),
        ];

        _bus.Subscribe<CollisionEvent>(ev => ApplyImpulse(_world, ev));
        _bus.Subscribe<CollisionEvent>(ev => HandleBulletHit(_world, ev));
    }

    public void Update(double dt)
    {
        if (IsGameOver) return;
        foreach (var sys in _systems)
            sys.Update(_world, dt);
        _world.FlushDeferred();
        CheckDeath();
    }

    public void Draw(SKCanvas canvas) => _drawSys.Draw(_world, canvas);

    // ── Death check ───────────────────────────────────────────────────────────

    private void CheckDeath()
    {
        bool p1Dead = false, p2Dead = false;
        foreach (var e in _world.QueryEntities<PlayerTag>())
        {
            if (!_world.HasComponent<Health>(e)) continue;
            var h = _world.GetComponent<Health>(e);
            var pt = _world.GetComponent<PlayerTag>(e);
            if (h.IsDead) { if (pt.Index == 1) p1Dead = true; else p2Dead = true; }
        }
        if (!p1Dead && !p2Dead) return;
        IsGameOver = true;
        WinnerIndex = (p1Dead && p2Dead) ? 0 : (p1Dead ? 2 : 1);
    }

    // ── Spawn ─────────────────────────────────────────────────────────────────

    private static Entity SpawnPlayer(World world, int index, Vector2 pos, SKColor col, string label, int health)
    {
        Entity e = world.CreateEntity();
        world.AddComponent(e, new Transform { Position = pos });
        world.AddComponent(e, new Velocity());
        world.AddComponent(e, new RigidBody { Mass = 2f, LinearDrag = 2.5f });
        world.AddComponent(e, new Collider
        {
            Shape = new CircleShape(22f),
            Layer = Layers.Player,
            Mask = Layers.Player | Layers.Ball | Layers.Bullet
        });
        world.AddComponent(e, new PlayerTag { Index = index });
        world.AddComponent(e, new CircleVisual { Fill = col, ShowOutline = true, Label = label });
        world.AddComponent(e, Health.Full(health));
        world.AddComponent(e, new LastFacing { Dir = new Vector2(0, -1) });
        world.AddComponent(e, new ShootCooldown());
        return e;
    }

    // ── Collision handlers ────────────────────────────────────────────────────

    private static void ApplyImpulse(World world, CollisionEvent ev)
    {
        if (!world.IsAlive(ev.EntityA) || !world.IsAlive(ev.EntityB)) return;
        if (world.HasComponent<BulletTag>(ev.EntityA) || world.HasComponent<BulletTag>(ev.EntityB)) return;
        if (!world.HasComponent<Velocity>(ev.EntityA) || !world.HasComponent<Velocity>(ev.EntityB)) return;

        float mA = world.HasComponent<RigidBody>(ev.EntityA) ? world.GetComponent<RigidBody>(ev.EntityA).Mass : 1f;
        float mB = world.HasComponent<RigidBody>(ev.EntityB) ? world.GetComponent<RigidBody>(ev.EntityB).Mass : 1f;

        ref var vA = ref world.GetComponent<Velocity>(ev.EntityA);
        ref var vB = ref world.GetComponent<Velocity>(ev.EntityB);

        Vector2 n = ev.Contact.Normal;
        float relVelN = Vector2.Dot(vA.Linear - vB.Linear, n);
        if (relVelN >= 0f) return;

        float j = -(1f + Restitution) * relVelN / (1f / mA + 1f / mB);
        vA.Linear += n * (j / mA);
        vB.Linear -= n * (j / mB);
    }

    private static void HandleBulletHit(World world, CollisionEvent ev)
    {
        bool aIsBullet = world.IsAlive(ev.EntityA) && world.HasComponent<BulletTag>(ev.EntityA);
        bool bIsBullet = world.IsAlive(ev.EntityB) && world.HasComponent<BulletTag>(ev.EntityB);
        if (!aIsBullet && !bIsBullet) return;

        Entity bullet = aIsBullet ? ev.EntityA : ev.EntityB;
        Entity target = aIsBullet ? ev.EntityB : ev.EntityA;
        if (!world.IsAlive(bullet) || !world.IsAlive(target)) return;

        int ownerIndex = world.GetComponent<BulletTag>(bullet).OwnerIndex;

        if (world.HasComponent<PlayerTag>(target))
        {
            if (world.GetComponent<PlayerTag>(target).Index == ownerIndex) return;
            ref var hp = ref world.GetComponent<Health>(target);
            hp.Current = Math.Max(0, hp.Current - 25);
            world.DestroyEntity(bullet);
        }
        else if (world.HasComponent<BallTag>(target))
        {
            world.DestroyEntity(bullet);
        }
    }
}

// =============================================================================
// MenuScreen
// =============================================================================

class MenuScreen
{
    public GameSettings Settings { get; private set; } = GameSettings.Default;
    public bool StartRequested { get; private set; }

    private int _selectedRow;
    private bool _prevMouse;
    private Vector2 _mousePos;
    private readonly int _w, _h;

    // Rows: 0=MaxBalls  1=BallRadius  2=PlayerHealth  3=SpawnRate  4=FpsCap
    private const int RowCount = 5;

    // Layout (all in pixels)
    private const float PanelW = 540f;
    private const float PanelH = 410f;
    private const float BtnSz = 30f;
    private const float CtrlRightMargin = 30f; // gap between [+] and panel right edge

    private float PanelX => (_w - PanelW) / 2f;
    private float PanelY => (_h - PanelH) / 2f - 10f;
    private float BtnIncX => PanelX + PanelW - CtrlRightMargin - BtnSz;
    private float ValueCX => BtnIncX - 60f;            // center of value display
    private float BtnDecX => BtnIncX - 120f - BtnSz;  // [-] button left edge
    private float RowH => 46f;
    private float RowY(int r) => PanelY + 118f + r * RowH;

    private float StartBtnW => 220f;
    private float StartBtnH => 48f;
    private float StartBtnX => (_w - StartBtnW) / 2f;
    private float StartBtnY => PanelY + PanelH - 66f;

    // Paints
    private readonly SKPaint _panelFill = new() { Style = SKPaintStyle.Fill, Color = new SKColor(18, 24, 42, 245) };
    private readonly SKPaint _panelBorder = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(55, 80, 140, 200), IsAntialias = true };
    private readonly SKPaint _gridPaint = new() { Color = new SKColor(255, 255, 255, 14), StrokeWidth = 1 };
    private readonly SKPaint _titlePaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 34f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _sectionPaint = new()
    {
        Color = new SKColor(100, 120, 170),
        IsAntialias = true,
        TextSize = 12f,
        Typeface = SKTypeface.FromFamilyName("monospace")
    };
    private readonly SKPaint _labelPaint = new()
    {
        Color = new SKColor(190, 205, 230),
        IsAntialias = true,
        TextSize = 15f,
        Typeface = SKTypeface.FromFamilyName("monospace")
    };
    private readonly SKPaint _valuePaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 15f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _btnFill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _btnText = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 16f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _rowHl = new() { Style = SKPaintStyle.Fill, Color = new SKColor(60, 100, 200, 35) };
    private readonly SKPaint _startFill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _startText = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 18f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _hintPaint = new()
    {
        Color = new SKColor(90, 105, 140),
        IsAntialias = true,
        TextSize = 12f,
        Typeface = SKTypeface.FromFamilyName("monospace")
    };

    public MenuScreen(int w, int h) { _w = w; _h = h; }

    // ── Update ────────────────────────────────────────────────────────────────

    public void Update(InputSystem input)
    {
        StartRequested = false;
        _mousePos = new Vector2(input.MouseScreen.X, input.MouseScreen.Y);

        bool justClicked = input.IsMouseLeft && !_prevMouse;
        _prevMouse = input.IsMouseLeft;

        if (input.IsPressed(KeyCode.Up)) _selectedRow = Math.Max(0, _selectedRow - 1);
        if (input.IsPressed(KeyCode.Down)) _selectedRow = Math.Min(RowCount - 1, _selectedRow + 1);
        if (input.IsPressed(KeyCode.Left)) Adjust(_selectedRow, -1);
        if (input.IsPressed(KeyCode.Right)) Adjust(_selectedRow, +1);
        if (input.IsPressed(KeyCode.Enter) || input.IsPressed(KeyCode.Space))
            StartRequested = true;

        if (justClicked) HandleClick();
    }

    private void HandleClick()
    {
        if (Hit(StartBtnX, StartBtnY, StartBtnW, StartBtnH)) { StartRequested = true; return; }
        for (int i = 0; i < RowCount; i++)
        {
            float top = RowY(i) - BtnSz / 2f;
            if (Hit(BtnDecX, top, BtnSz, BtnSz)) { _selectedRow = i; Adjust(i, -1); }
            if (Hit(BtnIncX, top, BtnSz, BtnSz)) { _selectedRow = i; Adjust(i, +1); }
        }
    }

    private void Adjust(int row, int dir)
    {
        var s = Settings;
        switch (row)
        {
            case 0: s.MaxBalls = Math.Clamp(s.MaxBalls + dir * 10, 10, 500); break;
            case 1: s.BallRadius = Math.Clamp(s.BallRadius + dir, 2f, 12f); break;
            case 2: s.PlayerHealth = Math.Clamp(s.PlayerHealth + dir * 25, 25, 200); break;
            case 3: s.SpawnRateIndex = (s.SpawnRateIndex + dir + 4) % 4; break;
            case 4: s.FpsCapIndex = (s.FpsCapIndex + dir + 4) % 4; break;
        }
        Settings = s;
    }

    private bool Hit(float x, float y, float w, float h) =>
        _mousePos.X >= x && _mousePos.X <= x + w && _mousePos.Y >= y && _mousePos.Y <= y + h;

    // ── Draw ──────────────────────────────────────────────────────────────────

    public void Draw(SKCanvas canvas)
    {
        canvas.Clear(new SKColor(12, 12, 20));
        for (int x = 0; x <= _w; x += 80) canvas.DrawLine(x, 0, x, _h, _gridPaint);
        for (int y = 0; y <= _h; y += 80) canvas.DrawLine(0, y, _w, y, _gridPaint);

        // Panel
        var panel = new SKRect(PanelX, PanelY, PanelX + PanelW, PanelY + PanelH);
        canvas.DrawRoundRect(panel, 12, 12, _panelFill);
        canvas.DrawRoundRect(panel, 12, 12, _panelBorder);

        // Title
        string title = "TWO PLAYER GAME";
        DrawTC(canvas, title, _w / 2f, PanelY + 26f, _titlePaint);

        // Game Settings section
        DrawTC(canvas, "— GAME SETTINGS —", _w / 2f, PanelY + 80f, _sectionPaint);
        DrawRow(canvas, 0, "Max Balls", Settings.MaxBalls.ToString());
        DrawRow(canvas, 1, "Ball Size", ((int)Settings.BallRadius).ToString());
        DrawRow(canvas, 2, "Player Health", Settings.PlayerHealth.ToString());
        DrawRow(canvas, 3, "Spawn Rate", Settings.SpawnLabel);

        // System section
        float sectionY2 = RowY(3) + RowH * 0.6f;
        DrawTC(canvas, "— SYSTEM —", _w / 2f, sectionY2, _sectionPaint);
        DrawRow(canvas, 4, "FPS Cap", Settings.FpsLabel);

        // Start button
        bool hoverStart = Hit(StartBtnX, StartBtnY, StartBtnW, StartBtnH);
        _startFill.Color = hoverStart ? new SKColor(70, 130, 230) : new SKColor(45, 95, 190);
        var startRect = new SKRect(StartBtnX, StartBtnY, StartBtnX + StartBtnW, StartBtnY + StartBtnH);
        canvas.DrawRoundRect(startRect, 8, 8, _startFill);
        DrawTC(canvas, "▶  START GAME", _w / 2f, StartBtnY + StartBtnH / 2f, _startText);

        // Keyboard hint
        DrawTC(canvas, "↑↓ select   ←→ adjust   Enter start", _w / 2f, PanelY + PanelH + 14f, _hintPaint);
    }

    private void DrawRow(SKCanvas canvas, int row, string label, string value)
    {
        float cy = RowY(row);
        float top = cy - RowH / 2f;

        if (row == _selectedRow)
            canvas.DrawRect(PanelX + 8f, top, PanelW - 16f, RowH, _rowHl);

        // Label
        float th = _labelPaint.FontMetrics.Descent - _labelPaint.FontMetrics.Ascent;
        SkiaUtils.DrawTextAt(canvas, label, PanelX + 22f, cy - th / 2f, _labelPaint);

        // [-] button
        DrawSmallBtn(canvas, BtnDecX, cy - BtnSz / 2f, "−");

        // Value centered between buttons
        float vw = _valuePaint.MeasureText(value);
        float vh = _valuePaint.FontMetrics.Descent - _valuePaint.FontMetrics.Ascent;
        SkiaUtils.DrawTextAt(canvas, value, ValueCX - vw / 2f, cy - vh / 2f, _valuePaint);

        // [+] button
        DrawSmallBtn(canvas, BtnIncX, cy - BtnSz / 2f, "+");
    }

    private void DrawSmallBtn(SKCanvas canvas, float x, float y, string text)
    {
        bool hover = Hit(x, y, BtnSz, BtnSz);
        _btnFill.Color = hover ? new SKColor(70, 85, 120) : new SKColor(42, 50, 72);
        canvas.DrawRoundRect(new SKRect(x, y, x + BtnSz, y + BtnSz), 4, 4, _btnFill);
        float tw = _btnText.MeasureText(text);
        float th = _btnText.FontMetrics.Descent - _btnText.FontMetrics.Ascent;
        SkiaUtils.DrawTextAt(canvas, text, x + (BtnSz - tw) / 2f, y + (BtnSz - th) / 2f, _btnText);
    }

    // Draw text centered horizontally at cx, with vertical center at cy.
    private static void DrawTC(SKCanvas canvas, string text, float cx, float cy, SKPaint paint)
    {
        float tw = paint.MeasureText(text);
        float th = paint.FontMetrics.Descent - paint.FontMetrics.Ascent;
        SkiaUtils.DrawTextAt(canvas, text, cx - tw / 2f, cy - th / 2f, paint);
    }
}

// =============================================================================
// OverlayPainter — pause / game-over screens drawn on top of a frozen game
// =============================================================================

class OverlayPainter
{
    private readonly int _w, _h;

    private readonly SKPaint _dim = new()
    { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 170) };
    private readonly SKPaint _titlePaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 52f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _bodyPaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 26f,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _hintPaint = new()
    {
        Color = new SKColor(150, 160, 185),
        IsAntialias = true,
        TextSize = 16f,
        Typeface = SKTypeface.FromFamilyName("monospace")
    };

    public OverlayPainter(int w, int h) { _w = w; _h = h; }

    public void DrawPause(SKCanvas canvas)
    {
        canvas.DrawRect(0, 0, _w, _h, _dim);
        DrawTC(canvas, "PAUSED", _w / 2f, _h / 2f - 32f, _titlePaint);
        DrawTC(canvas, "Escape — Resume", _w / 2f, _h / 2f + 24f, _hintPaint);
    }

    public void DrawGameOver(SKCanvas canvas, int winner, SKColor p1Col, SKColor p2Col)
    {
        canvas.DrawRect(0, 0, _w, _h, _dim);
        DrawTC(canvas, "GAME OVER", _w / 2f, _h / 2f - 70f, _titlePaint);

        string winText = winner switch { 1 => "Player 1 wins!", 2 => "Player 2 wins!", _ => "Draw!" };
        _bodyPaint.Color = winner switch { 1 => p1Col, 2 => p2Col, _ => new SKColor(200, 200, 200) };
        DrawTC(canvas, winText, _w / 2f, _h / 2f - 10f, _bodyPaint);

        _hintPaint.Color = new SKColor(150, 160, 185);
        DrawTC(canvas, "Enter — Play Again      Escape — Menu", _w / 2f, _h / 2f + 46f, _hintPaint);
    }

    private static void DrawTC(SKCanvas canvas, string text, float cx, float cy, SKPaint paint)
    {
        float tw = paint.MeasureText(text);
        float th = paint.FontMetrics.Descent - paint.FontMetrics.Ascent;
        SkiaUtils.DrawTextAt(canvas, text, cx - tw / 2f, cy - th / 2f, paint);
    }
}

// =============================================================================
// SkiaUtils
// =============================================================================

static class SkiaUtils
{
    // Draws text with top-left anchor at (x, y).
    // SKCanvas.DrawText uses baseline; offset by -Ascent converts to top anchor.
    public static void DrawTextAt(SKCanvas canvas, string text, float x, float y, SKPaint paint)
        => canvas.DrawText(text, x, y - paint.FontMetrics.Ascent, paint);
}

// =============================================================================
// Demo-specific components
// =============================================================================

struct PlayerTag { public int Index; }
struct BallTag { }
struct BulletTag { public int OwnerIndex; }
struct LastFacing { public Vector2 Dir; }
struct TimeToLive { public float Remaining; }
struct ShootCooldown { public float Remaining; }

struct CircleVisual
{
    public SKColor Fill;
    public bool ShowOutline;
    public string Label;
}

static class Layers
{
    public const int Player = 1;
    public const int Ball = 2;
    public const int Bullet = 4;
}

// =============================================================================
// PlayerInputSystem
// =============================================================================

class PlayerInputSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity _p1, _p2;
    private const float Thrust = 1800f;

    public PlayerInputSystem(InputSystem input, Entity p1, Entity p2)
    { _input = input; _p1 = p1; _p2 = p2; }

    public void Update(World world, double dt)
    {
        Thrust2D(world, _p1, KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D);
        Thrust2D(world, _p2, KeyCode.Up, KeyCode.Down, KeyCode.Left, KeyCode.Right);

        UpdateLastFacing(world, _p1);
        UpdateLastFacing(world, _p2);

        if (_input.IsPressed(KeyCode.R))
            foreach (var e in world.QueryEntities<BallTag>())
                world.DestroyEntity(e);
    }

    private void Thrust2D(World world, Entity e,
                          KeyCode up, KeyCode down, KeyCode left, KeyCode right)
    {
        float ax = 0f, ay = 0f;
        if (_input.IsHeld(up)) ay -= Thrust;
        if (_input.IsHeld(down)) ay += Thrust;
        if (_input.IsHeld(left)) ax -= Thrust;
        if (_input.IsHeld(right)) ax += Thrust;
        PhysicsSystem.ApplyForce(world, e, new Vector2(ax, ay));
    }

    private static void UpdateLastFacing(World world, Entity e)
    {
        if (!world.IsAlive(e) || !world.HasComponent<LastFacing>(e)) return;
        ref var v = ref world.GetComponent<Velocity>(e);
        if (v.Linear.LengthSquared() > 100f)
        {
            ref var lf = ref world.GetComponent<LastFacing>(e);
            lf.Dir = Vector2.Normalize(v.Linear);
        }
    }
}

// =============================================================================
// ShootSystem
// =============================================================================

class ShootSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity _p1, _p2;
    private const float CooldownSecs = 0.18f;
    private const float BulletSpeed = 700f;
    private const float BulletTTL = 1.8f;
    private const float BulletRadius = 5f;

    private static readonly SKColor ColP1 = new(140, 210, 255);
    private static readonly SKColor ColP2 = new(255, 140, 100);

    public ShootSystem(InputSystem input, Entity p1, Entity p2)
    { _input = input; _p1 = p1; _p2 = p2; }

    public void Update(World world, double dt)
    {
        Tick(world, _p1, dt);
        Tick(world, _p2, dt);
        if (_input.IsPressed(KeyCode.F)) TryShoot(world, _p1, 1, ColP1);
        if (_input.IsPressed(KeyCode.L)) TryShoot(world, _p2, 2, ColP2);
    }

    private static void Tick(World world, Entity e, double dt)
    {
        if (!world.IsAlive(e)) return;
        ref var cd = ref world.GetComponent<ShootCooldown>(e);
        if (cd.Remaining > 0f) cd.Remaining -= (float)dt;
    }

    private static void TryShoot(World world, Entity shooter, int ownerIndex, SKColor col)
    {
        if (!world.IsAlive(shooter)) return;
        ref var cd = ref world.GetComponent<ShootCooldown>(shooter);
        if (cd.Remaining > 0f) return;
        cd.Remaining = CooldownSecs;

        ref var t = ref world.GetComponent<Transform>(shooter);
        ref var lf = ref world.GetComponent<LastFacing>(shooter);
        Vector2 dir = lf.Dir;
        Vector2 spawnPos = t.Position + dir * 26f;

        Entity bullet = world.CreateEntity();
        world.AddComponent(bullet, new Transform { Position = spawnPos });
        world.AddComponent(bullet, new Velocity { Linear = dir * BulletSpeed });
        world.AddComponent(bullet, new RigidBody { Mass = 0.1f, LinearDrag = 0f });
        world.AddComponent(bullet, new Collider { Shape = new CircleShape(BulletRadius), Layer = Layers.Bullet, Mask = Layers.Player | Layers.Ball });
        world.AddComponent(bullet, new BulletTag { OwnerIndex = ownerIndex });
        world.AddComponent(bullet, new TimeToLive { Remaining = BulletTTL });
        world.AddComponent(bullet, new CircleVisual { Fill = col, ShowOutline = false, Label = null! });
    }
}

// =============================================================================
// TimeToLiveSystem
// =============================================================================

class TimeToLiveSystem : ISystem
{
    public void Update(World world, double dt)
    {
        List<Entity> expired = [];
        foreach (var e in world.QueryEntities<TimeToLive>())
        {
            ref var ttl = ref world.GetComponent<TimeToLive>(e);
            ttl.Remaining -= (float)dt;
            if (ttl.Remaining <= 0f) expired.Add(e);
        }
        foreach (var e in expired) world.DestroyEntity(e);
    }
}

// =============================================================================
// WallBounceSystem
// =============================================================================

class WallBounceSystem : ISystem
{
    private readonly int _w, _h;
    private readonly float _ballR;

    public WallBounceSystem(int w, int h, float ballR) { _w = w; _h = h; _ballR = ballR; }

    public void Update(World world, double dt)
    {
        int w = _w;
        int h = _h;
        float br = _ballR;

        world.ForEach<Transform, Velocity, BallTag>(
            (Entity _, ref Transform t, ref Velocity v, ref BallTag _b) =>
        {
            if (t.Position.X < br) { t.Position = new Vector2(br, t.Position.Y); v.Linear = new Vector2(MathF.Abs(v.Linear.X), v.Linear.Y); }
            if (t.Position.X > w - br) { t.Position = new Vector2(w - br, t.Position.Y); v.Linear = new Vector2(-MathF.Abs(v.Linear.X), v.Linear.Y); }
            if (t.Position.Y < br) { t.Position = new Vector2(t.Position.X, br); v.Linear = new Vector2(v.Linear.X, MathF.Abs(v.Linear.Y)); }
            if (t.Position.Y > h - br) { t.Position = new Vector2(t.Position.X, h - br); v.Linear = new Vector2(v.Linear.X, -MathF.Abs(v.Linear.Y)); }
        });

        world.ForEach<Transform, Velocity, PlayerTag>(
            (Entity _, ref Transform t, ref Velocity v, ref PlayerTag _p) =>
        {
            const float r = 22f;
            if (t.Position.X < r) { t.Position = new Vector2(r, t.Position.Y); if (v.Linear.X < 0) v.Linear = new Vector2(0, v.Linear.Y); }
            if (t.Position.X > w - r) { t.Position = new Vector2(w - r, t.Position.Y); if (v.Linear.X > 0) v.Linear = new Vector2(0, v.Linear.Y); }
            if (t.Position.Y < r) { t.Position = new Vector2(t.Position.X, r); if (v.Linear.Y < 0) v.Linear = new Vector2(v.Linear.X, 0); }
            if (t.Position.Y > h - r) { t.Position = new Vector2(t.Position.X, h - r); if (v.Linear.Y > 0) v.Linear = new Vector2(v.Linear.X, 0); }
        });
    }
}

// =============================================================================
// DashSystem
// =============================================================================

class DashSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity _p1, _p2;
    private const float DashSpeed = 800f;

    public DashSystem(InputSystem input, Entity p1, Entity p2)
    { _input = input; _p1 = p1; _p2 = p2; }

    public void Update(World world, double dt)
    {
        if (_input.IsPressedThisFrame(KeyCode.Space)) ApplyDash(world, _p1);
        if (_input.IsPressedThisFrame(KeyCode.Enter)) ApplyDash(world, _p2);
    }

    private static void ApplyDash(World world, Entity e)
    {
        if (!world.IsAlive(e) || !world.HasComponent<Velocity>(e)) return;
        ref var v = ref world.GetComponent<Velocity>(e);
        Vector2 dir = v.Linear != Vector2.Zero ? Vector2.Normalize(v.Linear) : new Vector2(0, -1);
        v.Linear += dir * DashSpeed;
    }
}

// =============================================================================
// BallSpawnSystem
// =============================================================================

class BallSpawnSystem : ISystem
{
    public float SpawnProgress { get; private set; }

    private float _timer;
    private readonly int _cx, _cy;
    private readonly int _maxBalls;
    private readonly float _interval;
    private readonly float _ballR;
    private readonly Random _rng = new();

    private static readonly SKColor[] Palette =
    [
        new SKColor(255, 105, 180), new SKColor(50, 205, 50), new SKColor(255, 215, 0),
        new SKColor(0, 255, 255), new SKColor(255, 69, 0), new SKColor(147, 112, 219),
        new SKColor(127, 255, 212),
    ];

    public BallSpawnSystem(int worldW, int worldH, GameSettings s)
    {
        _maxBalls = s.MaxBalls;
        _interval = s.SpawnInterval;
        _ballR = s.BallRadius;
        _cx = worldW / 2 + _rng.Next(-100, 100);
        _cy = worldH / 2 + _rng.Next(-100, 100);
    }

    public void Update(World world, double dt)
    {
        _timer += (float)dt;
        SpawnProgress = Math.Min(_timer / _interval, 1f);
        if (_timer < _interval) return;
        _timer = 0f;
        if (world.Count<BallTag>() >= _maxBalls) return;

        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
        float speed = 120f + (float)(_rng.NextDouble() * 350f);
        SKColor col = Palette[_rng.Next(Palette.Length)];

        Entity ball = world.CreateEntity();
        world.AddComponent(ball, new Transform { Position = new Vector2(_cx, _cy) });
        world.AddComponent(ball, new Velocity { Linear = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed });
        world.AddComponent(ball, new RigidBody { Mass = 0.4f, LinearDrag = 0.25f });
        world.AddComponent(ball, new Collider { Shape = new CircleShape(_ballR), Layer = Layers.Ball, Mask = Layers.Player | Layers.Ball | Layers.Bullet });
        world.AddComponent(ball, new BallTag());
        world.AddComponent(ball, new CircleVisual { Fill = col, ShowOutline = false, Label = null! });
    }
}

// =============================================================================
// EventFlushSystem
// =============================================================================

class EventFlushSystem : ISystem
{
    private readonly EventBus _bus;
    public EventFlushSystem(EventBus bus) { _bus = bus; }
    public void Update(World world, double dt) => _bus.Flush();
}

// =============================================================================
// CircleDrawSystem — all rendering via SkiaSharp
// =============================================================================

class CircleDrawSystem
{
    private readonly BallSpawnSystem _spawner;
    private readonly InputSystem _input;
    private readonly int _w, _h, _maxBalls;

    private readonly SKPaint _gridPaint = new() { Color = new SKColor(255, 255, 255, 22), StrokeWidth = 1, IsAntialias = false };
    private readonly SKPaint _hudPaint = new()
    {
        Color = new SKColor(100, 100, 100, 220),
        IsAntialias = true,
        TextSize = 18,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _labelPaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 14,
        Typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold)
    };
    private readonly SKPaint _keyPaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
        TextSize = 13,
        Typeface = SKTypeface.FromFamilyName("monospace")
    };
    private readonly SKPaint _spawnPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true, Color = SKColors.White };
    private readonly SKPaint _glowPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _keyBgPaint = new() { Style = SKPaintStyle.Fill };
    private readonly SKPaint _keyBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, 100) };
    private readonly SKPaint _hpBgPaint = new() { Style = SKPaintStyle.Fill, Color = new SKColor(30, 30, 30, 210) };
    private readonly SKPaint _hpFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };
    private readonly SKPaint _hpBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(200, 200, 200, 130) };

    public CircleDrawSystem(BallSpawnSystem spawner, InputSystem input, int w, int h, int maxBalls)
    { _spawner = spawner; _input = input; _w = w; _h = h; _maxBalls = maxBalls; }

    public void Draw(World world, SKCanvas canvas)
    {
        canvas.Clear(new SKColor(12, 12, 20));
        DrawGrid(canvas);
        DrawSpawnRing(canvas);

        world.ForEach<Transform, Collider, CircleVisual>(
            (Entity _, ref Transform t, ref Collider c, ref CircleVisual cv) =>
        {
            if (c.Shape is CircleShape circle)
                DrawDisc(canvas, t.Position.X, t.Position.Y, circle.Radius, cv.Fill, cv.ShowOutline, cv.Label);
        });

        // Health bars on top
        world.ForEach<Transform, PlayerTag, Health>(
            (Entity _, ref Transform t, ref PlayerTag _, ref Health h) =>
                DrawHealthBar(canvas, t.Position, h.Fraction));

        string info = $"Balls: {world.Count<BallTag>()} / {_maxBalls}   [R] clear";
        float iw = _hudPaint.MeasureText(info);
        SkiaUtils.DrawTextAt(canvas, info, (_w - iw) / 2f, 12f, _hudPaint);

        DrawKeys(canvas, 20, _h - 90, KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, new SKColor(80, 140, 255));
        DrawKeys(canvas, _w - 110, _h - 90, KeyCode.Up, KeyCode.Left, KeyCode.Down, KeyCode.Right, new SKColor(255, 80, 70));
    }

    private void DrawHealthBar(SKCanvas canvas, Vector2 pos, float fraction)
    {
        const float barW = 52f, barH = 7f;
        float bx = pos.X - barW / 2f;
        float by = pos.Y - 22f - 14f;

        canvas.DrawRect(bx, by, barW, barH, _hpBgPaint);
        if (fraction > 0f)
        {
            float r = fraction < 0.5f ? 1f : 1f - (fraction - 0.5f) * 2f;
            float g = fraction > 0.5f ? 1f : fraction * 2f;
            _hpFillPaint.Color = new SKColor((byte)(r * 230), (byte)(g * 210), 0, 220);
            canvas.DrawRect(bx, by, barW * fraction, barH, _hpFillPaint);
        }
        canvas.DrawRect(bx, by, barW, barH, _hpBorderPaint);
    }

    private void DrawGrid(SKCanvas canvas)
    {
        for (int x = 0; x <= _w; x += 80) canvas.DrawLine(x, 0, x, _h, _gridPaint);
        for (int y = 0; y <= _h; y += 80) canvas.DrawLine(0, y, _w, y, _gridPaint);
    }

    private void DrawSpawnRing(SKCanvas canvas)
    {
        float t = _spawner.SpawnProgress;
        float r = 8f + 24f * t;
        _spawnPaint.Color = new SKColor(255, 240, 80, (byte)(100 * t));
        canvas.DrawCircle(_w / 2f, _h / 2f, r, _spawnPaint);
    }

    private void DrawDisc(SKCanvas canvas, float x, float y, float r, SKColor fill, bool outline, string? label)
    {
        _glowPaint.Color = new SKColor(fill.Red, fill.Green, fill.Blue, 35);
        canvas.DrawCircle(x, y, r + 9f, _glowPaint);

        _fillPaint.Color = fill;
        canvas.DrawCircle(x, y, r, _fillPaint);

        if (outline) canvas.DrawCircle(x, y, r, _strokePaint);

        if (label != null)
        {
            float lw = _labelPaint.MeasureText(label);
            float lh = _labelPaint.FontMetrics.Descent - _labelPaint.FontMetrics.Ascent;
            SkiaUtils.DrawTextAt(canvas, label, x - lw / 2f, y - lh / 2f, _labelPaint);
        }
    }

    private void DrawKeys(SKCanvas canvas, int x, int y,
        KeyCode up, KeyCode left, KeyCode down, KeyCode right, SKColor accent)
    {
        DrawKey(canvas, x + 28, y, "^", _input.IsHeld(up), accent);
        DrawKey(canvas, x, y + 28, "<", _input.IsHeld(left), accent);
        DrawKey(canvas, x + 28, y + 28, "v", _input.IsHeld(down), accent);
        DrawKey(canvas, x + 56, y + 28, ">", _input.IsHeld(right), accent);
    }

    private void DrawKey(SKCanvas canvas, int x, int y, string lbl, bool active, SKColor accent)
    {
        _keyBgPaint.Color = active ? accent : new SKColor(255, 255, 255, 55);
        canvas.DrawRect(x, y, 24, 24, _keyBgPaint);
        canvas.DrawRect(x, y, 24, 24, _keyBorderPaint);

        float lw = _keyPaint.MeasureText(lbl);
        float lh = _keyPaint.FontMetrics.Descent - _keyPaint.FontMetrics.Ascent;
        _keyPaint.Color = active ? SKColors.White : new SKColor(180, 180, 180);
        SkiaUtils.DrawTextAt(canvas, lbl, x + (24 - lw) / 2f, y + (24 - lh) / 2f, _keyPaint);
    }
}
