// Asteroids — Destruction Sandbox
//
//   WASD        thrust          Mouse      aim
//   Left-click  fire            R          respawn asteroids
//   Up/Down     select param    Left/Right adjust param
//   Tab         toggle panel    Esc        quit
//
//   cd GameEngine/Demos/AsteroidDemo && dotnet run

using System.Diagnostics;
using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Engine.Systems;
using AsteroidsEngine.Platform.Sdl;

const int W = 1280, H = 800;

using var window = new SdlGameWindow("Asteroids — Destruction Sandbox", W, H);
var input = new InputSystem();
window.KeyDown            += k        => input.OnKeyDown(k);
window.KeyUp              += k        => input.OnKeyUp(k);
window.MouseMoved         += p        => input.OnMouseMove(p);
window.MouseButtonChanged += (b, pr)  => input.OnMouseButton(b, pr);

var cfg      = new Config();
var session  = new DemoSession(W, H, input, cfg);
var renderer = new DemoRenderer(W, H);

const double FixedDt = 1.0 / 120.0;
var fixedStep = new FixedTimestep(FixedDt);
var sw = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;
bool showPanel = true;

while (!window.ShouldClose)
{
    window.PollEvents();

    long now = sw.ElapsedTicks;
    double frameTime = (double)(now - lastTicks) / Stopwatch.Frequency;
    lastTicks = now;

    input.BeginFrame();
    if (input.IsPressed(KeyCode.Escape)) break;

    // Panel / tuning input (once per render frame).
    if (input.IsPressed(KeyCode.Tab))   showPanel = !showPanel;
    if (input.IsPressed(KeyCode.Up))    cfg.T.Move(-1);
    if (input.IsPressed(KeyCode.Down))  cfg.T.Move(1);
    if (input.IsPressed(KeyCode.Left))  cfg.T.Adjust(-1);
    if (input.IsPressed(KeyCode.Right)) cfg.T.Adjust(1);
    if (input.IsPressed(KeyCode.R))     session.Respawn();

    int steps = fixedStep.Advance(frameTime);
    for (int i = 0; i < steps; i++) session.Update(FixedDt);

    renderer.Draw(window.Renderer, session, cfg, fixedStep.Alpha, showPanel);
    window.Present();

    double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
    int sleep = (int)((1.0 / 120.0 - elapsed) * 1000);
    if (sleep > 1) Thread.Sleep(sleep);
}

// =============================================================================
// Constants, layers, components
// =============================================================================

static class GameConst { public const float PlayerRadius = 15f; }

static class Layers { public const int Asteroid = 1, Player = 2; }

struct PlayerTag { }
struct AsteroidTag { }
struct BulletTag { }
struct BulletVisual { public Color Color; }
struct DebrisParticle { public Color Color; public float MaxTtl; }
struct TimeToLive { public float Remaining; }
struct AimComponent { public Vector2 Dir; }
struct ShootCooldown { public float Remaining; }
struct AsteroidColor { public Color Fill, Outline; }

readonly struct BulletHitEvent
{
    public readonly Entity  Asteroid, Bullet;
    public readonly int     StruckCell;
    public readonly Vector2 Point, ShotDir;
    public BulletHitEvent(Entity asteroid, Entity bullet, int cell, Vector2 point, Vector2 shotDir)
    { Asteroid = asteroid; Bullet = bullet; StruckCell = cell; Point = point; ShotDir = shotDir; }
}

// =============================================================================
// DemoSession — owns the world, systems, spawning and the fracture handler
// =============================================================================

sealed class DemoSession
{
    private readonly World    _world = new();
    private readonly EventBus _bus   = new();
    private readonly ISystem[] _systems;
    private readonly Config   _cfg;
    private readonly Random   _rng = new(1234);
    private readonly int _w, _h;
    private Entity _player;
    private float  _respawnTimer = -1f;

    public World World => _world;
    public Entity Player => _player;

    public DemoSession(int w, int h, InputSystem input, Config cfg)
    {
        _w = w; _h = h; _cfg = cfg;

        SpawnPlayer(new Vector2(w / 2f, h / 2f));
        SpawnWave();

        _systems = new ISystem[]
        {
            new PreviousStateSystem(),
            new PlayerControlSystem(input, _player, cfg),
            new PhysicsSystem(),
            new MovementSystem(),
            new RaycastBulletSystem(_bus),
            new WrapSystem(w, h),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
            new TunableApplySystem(cfg),
        };

        _bus.Subscribe<BulletHitEvent>(OnBulletHit);
    }

    public void Update(double dt)
    {
        foreach (var s in _systems) s.Update(_world, dt);
        _world.FlushDeferred();

        // Auto-respawn when the field is cleared.
        if (_world.Count<AsteroidTag>() == 0)
        {
            if (_respawnTimer < 0f) _respawnTimer = 1.5f;
            else { _respawnTimer -= (float)dt; if (_respawnTimer <= 0f) { Respawn(); _respawnTimer = -1f; } }
        }
        else _respawnTimer = -1f;
    }

    public void Respawn()
    {
        foreach (var e in new List<Entity>(_world.QueryEntities<AsteroidTag>())) _world.DestroyEntity(e);
        SpawnWave();
    }

    // -------------------------------------------------------------------------

    private void SpawnPlayer(Vector2 pos)
    {
        _player = _world.CreateEntity();
        _world.AddComponent(_player, new Transform { Position = pos, PreviousPosition = pos });
        _world.AddComponent(_player, new Velocity());
        _world.AddComponent(_player, new RigidBody { Mass = 12f, Inertia = 0f, LinearDrag = 1.2f, AngularDrag = 2f, Restitution = 0.2f, Friction = 0.1f });
        _world.AddComponent(_player, new Collider { Shape = new CircleShape(GameConst.PlayerRadius), Layer = Layers.Player, Mask = Layers.Asteroid });
        _world.AddComponent(_player, new AimComponent { Dir = Vector2.UnitX });
        _world.AddComponent(_player, new ShootCooldown());
        _world.AddComponent(_player, new PlayerTag());
    }

    private void SpawnWave()
    {
        int count = (int)_cfg.AstCount.Value;
        for (int i = 0; i < count; i++)
        {
            Vector2 pos;
            do { pos = new Vector2(_rng.Next(_w), _rng.Next(_h)); }
            while ((pos - new Vector2(_w / 2f, _h / 2f)).Length() < 180f);   // not on top of the player
            SpawnAsteroid(pos);
        }
    }

    private void SpawnAsteroid(Vector2 pos)
    {
        var mat = new FractureProperties
        {
            Toughness = _cfg.Toughness.Value, Brittleness = _cfg.Brittleness.Value,
            GrainArea = _cfg.Grain.Value, MinFragmentArea = _cfg.MinFragArea.Value,
            Density = _cfg.Density.Value, KineticFraction = _cfg.KineticFraction.Value,
        };
        var body = VoronoiTessellator.BuildAsteroid(_rng.Next(9, 14), _cfg.AstRadius.Value, mat, membership: null, _rng);

        float spread = (float)(_rng.NextDouble() * Math.PI * 2);
        var vel = new Vector2(MathF.Cos(spread), MathF.Sin(spread)) * (float)(_rng.NextDouble() * _cfg.AstSpeed.Value);
        float spin = (float)(_rng.NextDouble() * 2 - 1) * _cfg.AstSpin.Value;

        byte shade = (byte)_rng.Next(50, 80);
        SpawnBody(body, pos, (float)(_rng.NextDouble() * Math.PI * 2), vel, spin,
                  new AsteroidColor { Fill = new Color(shade, (byte)(shade - 6), (byte)(shade - 12)),
                                      Outline = new Color(150, 138, 120) });
    }

    private Entity SpawnBody(FracturableBody body, Vector2 pos, float rot, Vector2 vel, float spin, AsteroidColor color)
    {
        float area    = VoronoiTessellator.TotalArea(body);
        float mass     = MathF.Max(1f, body.Material.Density * area);
        float inertia  = VoronoiTessellator.ComputeInertia(body, mass);

        var e = _world.CreateEntity();
        _world.AddComponent(e, new Transform { Position = pos, Rotation = rot, PreviousPosition = pos, PreviousRotation = rot });
        _world.AddComponent(e, new Velocity { Linear = vel, Angular = spin });
        _world.AddComponent(e, new RigidBody { Mass = mass, Inertia = inertia,
            LinearDrag = _cfg.LinDrag.Value, AngularDrag = _cfg.AngDrag.Value,
            Restitution = _cfg.Restitution.Value, Friction = _cfg.Friction.Value });
        _world.AddComponent(e, new Collider { Shape = VoronoiTessellator.BuildShape(body),
            Layer = Layers.Asteroid, Mask = Layers.Asteroid | Layers.Player });
        _world.AddComponent(e, body);
        _world.AddComponent(e, new AsteroidTag());
        _world.AddComponent(e, color);
        return e;
    }

    // -------------------------------------------------------------------------
    // Fracture handler
    // -------------------------------------------------------------------------

    private void OnBulletHit(BulletHitEvent ev)
    {
        if (!_world.IsAlive(ev.Asteroid) || !_world.IsAlive(ev.Bullet)) return;

        Vector2 bulletVel = _world.GetComponent<Velocity>(ev.Bullet).Linear;
        _world.DestroyEntity(ev.Bullet);

        float bulletMass = _cfg.BulletMass.Value * _cfg.EnergyScale.Value;   // EnergyScale folds into mass (E ∝ m)
        var settings = new FractureSettings { SpinEnergyFraction = _cfg.SpinEnergyFrac.Value, MomentumTransfer = _cfg.MomentumTransfer.Value };

        AsteroidColor color = _world.HasComponent<AsteroidColor>(ev.Asteroid)
            ? _world.GetComponent<AsteroidColor>(ev.Asteroid)
            : new AsteroidColor { Fill = new Color(64, 58, 52), Outline = new Color(150, 138, 120) };

        bool fractured = FractureService.TryFracture(
            _world, ev.Asteroid, ev.StruckCell, ev.Point, ev.ShotDir,
            bulletVel, bulletMass, _cfg.Directionality.Value, settings, _rng, out var result);

        if (!fractured) return;   // sub-threshold: energy was absorbed in place

        foreach (var f in result.Fragments)
        {
            if (f.IsDebris) { SpawnDebris(f, color); continue; }
            SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color);
        }
        _world.DestroyEntity(ev.Asteroid);
    }

    private void SpawnDebris(in FragmentSpec f, AsteroidColor color)
    {
        var e = _world.CreateEntity();
        _world.AddComponent(e, new Transform { Position = f.WorldCentroid, Rotation = f.Rotation, PreviousPosition = f.WorldCentroid });
        _world.AddComponent(e, new Velocity { Linear = f.Linear, Angular = f.Angular });
        _world.AddComponent(e, new DebrisParticle { Color = color.Outline, MaxTtl = 1.2f });
        _world.AddComponent(e, new TimeToLive { Remaining = 1.2f });
    }
}

// =============================================================================
// Systems
// =============================================================================

sealed class PlayerControlSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity _player;
    private readonly Config _cfg;
    private static readonly Color BulletColor = new(255, 230, 90);

    public PlayerControlSystem(InputSystem input, Entity player, Config cfg)
    { _input = input; _player = player; _cfg = cfg; }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(_player)) return;

        Vector2 a = Vector2.Zero;
        if (_input.IsHeld(KeyCode.W)) a.Y -= 1; if (_input.IsHeld(KeyCode.S)) a.Y += 1;
        if (_input.IsHeld(KeyCode.A)) a.X -= 1; if (_input.IsHeld(KeyCode.D)) a.X += 1;
        if (a != Vector2.Zero)
            PhysicsSystem.ApplyForce(world, _player, Vector2.Normalize(a) * _cfg.Thrust.Value);

        ref var t  = ref world.GetComponent<Transform>(_player);
        ref var aim = ref world.GetComponent<AimComponent>(_player);
        Vector2 toMouse = _input.MouseScreen - t.Position;
        if (toMouse.LengthSquared() > 1f) aim.Dir = Vector2.Normalize(toMouse);

        ref var cd = ref world.GetComponent<ShootCooldown>(_player);
        if (cd.Remaining > 0f) cd.Remaining -= (float)dt;

        if (_input.IsMouseLeft && cd.Remaining <= 0f)
        {
            cd.Remaining = _cfg.FireRate.Value;
            Vector2 muzzle = t.Position + aim.Dir * (GameConst.PlayerRadius + 6f);
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
            world.AddComponent(b, new Velocity { Linear = aim.Dir * _cfg.BulletSpeed.Value });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = BulletColor });
            world.AddComponent(b, new TimeToLive { Remaining = 1.5f });
        }
    }
}

// Sweeps each bullet's travel segment against asteroids (raycast → no tunnelling).
sealed class RaycastBulletSystem : ISystem
{
    private readonly EventBus _bus;
    private readonly List<(Entity bullet, Vector2 from, Vector2 to)> _seg = new();

    public RaycastBulletSystem(EventBus bus) { _bus = bus; }

    public void Update(World world, double dt)
    {
        _seg.Clear();
        world.ForEach<Transform, BulletTag>((Entity e, ref Transform t, ref BulletTag _) =>
            _seg.Add((e, t.PreviousPosition, t.Position)));

        foreach (var (bullet, from, to) in _seg)
        {
            if (!world.IsAlive(bullet)) continue;
            Vector2 d = to - from;
            if (d.LengthSquared() < 1e-4f) continue;
            if (PhysicsQueries.Raycast(world, from, to, Layers.Asteroid, out var hit))
                _bus.Publish(new BulletHitEvent(hit.Entity, bullet, hit.PartIndex, hit.Point, Vector2.Normalize(d)));
        }
    }
}

sealed class WrapSystem : ISystem
{
    private readonly float _w, _h;
    public WrapSystem(int w, int h) { _w = w; _h = h; }

    public void Update(World world, double dt)
    {
        world.ForEach<Transform>((Entity _, ref Transform t) =>
        {
            if (t.Position.X < 0) t.Position.X += _w; else if (t.Position.X > _w) t.Position.X -= _w;
            if (t.Position.Y < 0) t.Position.Y += _h; else if (t.Position.Y > _h) t.Position.Y -= _h;
        });
    }
}

sealed class TimeToLiveSystem : ISystem
{
    public void Update(World world, double dt)
    {
        var dead = new List<Entity>();
        foreach (var e in world.QueryEntities<TimeToLive>())
        {
            ref var ttl = ref world.GetComponent<TimeToLive>(e);
            ttl.Remaining -= (float)dt;
            if (ttl.Remaining <= 0f) dead.Add(e);
        }
        foreach (var e in dead) world.DestroyEntity(e);
    }
}

sealed class EventFlushSystem : ISystem
{
    private readonly EventBus _bus;
    public EventFlushSystem(EventBus bus) { _bus = bus; }
    public void Update(World world, double dt) => _bus.Flush();
}

// Writes the live-tunable physics constants onto every body each frame.
sealed class TunableApplySystem : ISystem
{
    private readonly Config _cfg;
    public TunableApplySystem(Config cfg) { _cfg = cfg; }

    public void Update(World world, double dt)
    {
        var cfg = _cfg;
        world.ForEach<RigidBody>((Entity _, ref RigidBody rb) =>
        {
            rb.Restitution = cfg.Restitution.Value;
            rb.Friction    = cfg.Friction.Value;
            rb.LinearDrag  = cfg.LinDrag.Value;
            rb.AngularDrag = cfg.AngDrag.Value;
        });
        world.ForEach<FracturableBody>((Entity _, ref FracturableBody fb) =>
        {
            fb.Material.Brittleness     = cfg.Brittleness.Value;
            fb.Material.KineticFraction = cfg.KineticFraction.Value;
            fb.Material.MinFragmentArea = cfg.MinFragArea.Value;
        });
    }
}

// =============================================================================
// Renderer
// =============================================================================

sealed class DemoRenderer
{
    private readonly int _w, _h;
    private static readonly Color Bg = new(8, 9, 14);
    private static readonly Color PlayerFill = new(70, 130, 240);
    private static readonly Color PlayerEdge = new(170, 205, 255);
    private static readonly FontSpec Hud   = new("monospace", 14f);
    private static readonly FontSpec Panel = new("monospace", 13f);
    private const float TeleSq = 200f * 200f;

    public DemoRenderer(int w, int h) { _w = w; _h = h; }

    public void Draw(IRenderer r, DemoSession session, Config cfg, float alpha, bool showPanel)
    {
        var world = session.World;
        r.Begin(Bg);

        // Asteroids — draw each cell.
        world.ForEach<Transform, FracturableBody, AsteroidColor>(
            (Entity _, ref Transform t, ref FracturableBody fb, ref AsteroidColor col) =>
        {
            var (pos, rot) = Interp(t, alpha);
            float c = MathF.Cos(rot), s = MathF.Sin(rot);
            Span<Vector2> buf = stackalloc Vector2[16];   // reused across cells
            foreach (var cell in fb.Cells)
            {
                int n = cell.Local.Length;
                Span<Vector2> wv = n <= 16 ? buf.Slice(0, n) : new Vector2[n];
                for (int k = 0; k < n; k++)
                {
                    var v = cell.Local[k];
                    wv[k] = new Vector2(v.X * c - v.Y * s + pos.X, v.X * s + v.Y * c + pos.Y);
                }
                r.FillPolygon(wv, col.Fill);
                r.DrawPolygon(wv, col.Outline, 1f);
            }
        });

        // Debris.
        world.ForEach<Transform, DebrisParticle, TimeToLive>(
            (Entity _, ref Transform t, ref DebrisParticle dp, ref TimeToLive ttl) =>
        {
            float k = MathF.Max(0f, ttl.Remaining / dp.MaxTtl);
            r.FillCircle(t.Position, 2.5f, dp.Color.WithAlpha((byte)(200 * k)));
        });

        // Bullets.
        world.ForEach<Transform, BulletTag, BulletVisual>(
            (Entity _, ref Transform t, ref BulletTag _, ref BulletVisual bv) =>
        {
            var (p, _) = Interp(t, alpha);
            r.FillCircle(p, 3f, bv.Color);
        });

        // Player.
        world.ForEach<Transform, PlayerTag, AimComponent>(
            (Entity _, ref Transform t, ref PlayerTag _, ref AimComponent aim) =>
        {
            var (p, _) = Interp(t, alpha);
            r.FillCircle(p, GameConst.PlayerRadius, PlayerFill);
            r.DrawCircle(p, GameConst.PlayerRadius, PlayerEdge, 2f);
            r.DrawLine(p, p + aim.Dir * (GameConst.PlayerRadius + 12f), PlayerEdge, 2f);
        });

        // HUD.
        r.DrawText($"asteroids {world.Count<AsteroidTag>()}", new Vector2(12, 10), new Color(190, 200, 220), Hud);
        r.DrawText("WASD move   mouse aim   click fire   R respawn   arrows tune   Tab panel   Esc quit",
                   new Vector2(12, _h - 22f), new Color(110, 120, 140), Panel);

        if (showPanel) DrawPanel(r, cfg);

        r.End();
    }

    private void DrawPanel(IRenderer r, Config cfg)
    {
        var ps = cfg.T.Params;
        float x = _w - 270f, y = 8f, rowH = 18f;
        float panelH = ps.Count * rowH + 16f;

        Span<Vector2> bg = stackalloc Vector2[4]
        { new(x - 10, y - 4), new(_w - 4, y - 4), new(_w - 4, y + panelH), new(x - 10, y + panelH) };
        r.FillPolygon(bg, new Color(0, 0, 0, 150));

        for (int i = 0; i < ps.Count; i++)
        {
            bool sel = i == cfg.T.Selected;
            Color c = sel ? new Color(255, 220, 110) : new Color(170, 178, 195);
            string line = $"{(sel ? ">" : " ")} {ps[i].Name,-16} {ps[i].Display}";
            r.DrawText(line, new Vector2(x, y + i * rowH), c, Panel);
        }
    }

    private static (Vector2 pos, float rot) Interp(in Transform t, float alpha)
    {
        Vector2 d = t.Position - t.PreviousPosition;
        if (d.LengthSquared() > TeleSq) return (t.Position, t.Rotation);
        float dr = t.Rotation - t.PreviousRotation;
        while (dr >  MathF.PI) dr -= MathF.Tau;
        while (dr < -MathF.PI) dr += MathF.Tau;
        return (t.PreviousPosition + d * alpha, t.PreviousRotation + dr * alpha);
    }
}
