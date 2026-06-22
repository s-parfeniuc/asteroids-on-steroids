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
using System.Runtime.InteropServices;
using AsteroidsGame.Config;
using AsteroidDemo;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Diagnostics;
using AsteroidsEngine.Engine.Effects;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Engine.Systems;
using AsteroidsEngine.Platform.Sdl;

var (W, H) = SdlGameWindow.QueryDisplaySize();

using var window = new SdlGameWindow("Asteroids — Destruction Sandbox", W, H, fullscreen: true);
var input = new InputSystem();
window.KeyDown += k => input.OnKeyDown(k);
window.KeyUp += k => input.OnKeyUp(k);
window.MouseMoved += p => input.OnMouseMove(p);
window.MouseButtonChanged += (b, pr) => input.OnMouseButton(b, pr);
window.TextInput += s => input.OnTextInput(s);

string assetsDir = GameConfigLoader.FindAssetsDir(AppContext.BaseDirectory);
var (gameConfig, shapes) = GameConfigLoader.Load(assetsDir);
var editor = new Editor(gameConfig, shapes, assetsDir);

var cfg = new Config();
var session = new DemoSession(W, H, input, cfg, gameConfig, shapes);
var renderer = new DemoRenderer(W, H);
var shapeEditor = new ShapeEditorViewport(shapes, assetsDir);

const double FixedDt = 1.0 / 120.0;
var fixedStep = new FixedTimestep(FixedDt);
var sw = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;
bool showPanel = true;
double fps = 60.0;


ForceLog.Categories = ForceCat.Contact;

// Force logging: press L to toggle. Writes every velocity/spin/force application with its
// formula to forces.log for offline tuning. See info/forces.md.
System.IO.StreamWriter? forceWriter = null;
void ToggleForceLog()
{
    ForceLog.Enabled = !ForceLog.Enabled;
    if (ForceLog.Enabled)
    {
        forceWriter ??= new System.IO.StreamWriter("forces.log", append: false) { AutoFlush = true };
        ForceLog.Sink = forceWriter.WriteLine;
        Console.WriteLine("[force log] ON → forces.log  (categories: " + ForceLog.Categories + ")");
    }
    else Console.WriteLine("[force log] OFF");
}

while (!window.ShouldClose)
{
    window.PollEvents();

    long now = sw.ElapsedTicks;
    double frameTime = (double)(now - lastTicks) / Stopwatch.Frequency;
    lastTicks = now;
    if (frameTime > 0) fps += (1.0 / frameTime - fps) * 0.1;   // exponential smoothing

    input.BeginFrame();
    bool inShapeEditor = editor.SelectedTab == 5;
    // While a text-input widget is active: block game hotkeys (WASD / R / M / etc.)
    // and mouse-left so the player can't shoot while typing an asset name.
    // Back / Enter / Escape are always passed through for the text widget itself.
    // Also suppress when the shape editor is active (viewport owns left-click for seed editing).
    input.SuppressKeyboard = editor.IsTextInputActive;
    input.SuppressMouseLeft = editor.IsMouseOverPanel(input.MouseScreen, W)
                           || editor.IsTextInputActive
                           || inShapeEditor;
    if (input.IsPressed(KeyCode.Escape) && !editor.IsTextInputActive) break;

    // Shape editor takes priority over panel/game hotkeys when its tab is active.
    bool shapeConsumedTab = false;
    if (inShapeEditor)
    {
        // Editor panel bounds: left panel = [0, 210], right panel = [W-375, W], tab bar = [0, 32].
        const float LeftW = 210f, RightW = 375f, TabH = 32f;
        shapeConsumedTab = shapeEditor.Update(input, editor.SelectedShape, LeftW, W - RightW, TabH, H);
    }

    // Panel / tuning input (once per render frame).
    if (!shapeConsumedTab && input.IsPressed(KeyCode.Tab)) showPanel = !showPanel;
    if (!inShapeEditor)
    {
        if (input.IsPressed(KeyCode.Up))    cfg.T.Move(-1);
        if (input.IsPressed(KeyCode.Down))  cfg.T.Move(1);
        if (input.IsPressed(KeyCode.Left))  cfg.T.Adjust(-1);
        if (input.IsPressed(KeyCode.Right)) cfg.T.Adjust(1);
        if (input.IsPressed(KeyCode.R))     session.Respawn();
        if (input.IsPressed(KeyCode.M))     { cfg.CycleMaterial(); session.Respawn(); }
    }
    if (input.IsPressed(KeyCode.L)) ToggleForceLog();

    int steps = fixedStep.Advance(frameTime);
    for (int i = 0; i < steps; i++) session.Update(FixedDt);

    if (inShapeEditor)
    {
        const float LeftW = 210f, RightW = 375f, TabH = 32f;
        window.Renderer.Begin(new Color(8, 9, 14));  // same bg as simulation
        shapeEditor.Draw(window.Renderer, editor.SelectedShape, LeftW, W - RightW, TabH, H);
        window.Renderer.End();
    }
    else
    {
        renderer.Draw(window.Renderer, session, cfg, fixedStep.Alpha, showPanel, (float)fps);
    }
    editor.Draw(window.Renderer, input, W, H);
    window.Present();

    double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
    int sleep = (int)((1.0 / 120.0 - elapsed) * 1000);
    if (sleep > 1) Thread.Sleep(sleep);
}

forceWriter?.Dispose();

// =============================================================================
// Constants, layers, components
// =============================================================================

static class GameConst
{
    public const float PlayerRadius = 15f;
    public const int WorldW = 5760, WorldH = 3240;  // game-coordinate world size
    public const int ScreenW = 1920, ScreenH = 1080; // window / viewport size
}

static class Layers { public const int Asteroid = 1, Player = 2, Ghost = 4; }

struct PlayerTag { }
struct AsteroidTag { }
struct BulletTag { }
struct BulletVisual { public Color Color; }
struct TimeToLive { public float Remaining; }
struct AimComponent { public Vector2 Dir; }
struct ShootCooldown { public float Remaining; }
struct AsteroidColor { public Color Fill, Outline; }
// Tracks which GameConfig asteroid type key was used to spawn this body, enabling live
// material sync: editor changes to that material propagate to the running simulation.
struct AsteroidTypeKey { public string Key; }
// Fresh fragments don't collide for a short grace period so they separate without
// fighting each other (they spawn touching their siblings). On layer Ghost = no collision.
struct FractureGhost { public float Remaining; public bool Done; }
// Body-local boundary edges (pairs of points) — the silhouette + craters. Cached at
// spawn so the renderer can hide internal cell lines and stroke only the outline.
struct RenderOutline { public Vector2[] Outline; public Vector2[] Cracks; }
// A collider-less polygon chunk shed by a vaporised cell. Local verts are centroid-relative;
// the renderer fills it (faded by remaining TTL / MaxTtl) transformed by its Transform pose.
struct DebrisPiece { public Vector2[] Local; public Color Color; public float MaxTtl; }

readonly struct BulletHitEvent
{
    public readonly Entity Asteroid, Bullet;
    public readonly int StruckCell;
    public readonly Vector2 Point, ShotDir;
    public BulletHitEvent(Entity asteroid, Entity bullet, int cell, Vector2 point, Vector2 shotDir)
    { Asteroid = asteroid; Bullet = bullet; StruckCell = cell; Point = point; ShotDir = shotDir; }
}

// =============================================================================
// DemoSession — owns the world, systems, spawning and the fracture handler
// =============================================================================

sealed class DemoSession
{
    private readonly World _world = new();
    private readonly EventBus _bus = new();
    private readonly ISystem[] _systems;
    private readonly Config _cfg;
    private readonly GameConfig _gc;
    private readonly Dictionary<string, ShapeData> _shapes;
    private readonly Random _rng = new(1234);
    private readonly ParticleSystem _fx = new();
    private readonly int _screenW, _screenH;
    private Entity _player;
    private Camera _camera;
    private int _waveNumber = 0;
    private float _waveTimer = 0f;    // seconds since last wave spawn
    private float _gameTime  = 0f;    // total elapsed game time
    private int _maxLiveCells = 300;  // grows +30 per wave, capped at gc.MaxLiveCells
    private int _liveCells = 0;       // cached each Update() — reused for both HUD and wave trigger
    private bool _pendingWave = false;
    private float _waveCountdown = 0f;
    private int _nextGroupId = 1;

    // Wave banner (shown for 4s after each wave spawns)
    private string _bannerLine1 = "";
    private string _bannerLine2 = "";
    private string _bannerLine3 = "";
    private float  _bannerTimer = 0f;

    // Wave spawn log
    private StreamWriter? _waveLog;

    private static readonly Vector2 WorldCenter =
        new(GameConst.WorldW * 0.5f, GameConst.WorldH * 0.5f);

    // Pairs (minId, maxId) where an asteroid-on-asteroid fracture was already triggered
    // this collision; cleared when either entity dies so fresh contact re-evaluates.
    private readonly HashSet<(int, int)> _activeCollisionFractures = new();

    public World World => _world;
    public Entity Player => _player;
    public ParticleSystem Fx => _fx;
    public Camera Camera => _camera;
    public int WaveNumber => _waveNumber;
    public int LiveCells => _liveCells;
    public int MaxLiveCells => _maxLiveCells;
    public float WaveTimer => _waveTimer;
    public bool WavePending => _pendingWave;
    public bool BannerActive => _bannerTimer > 0f;
    public string BannerLine1 => _bannerLine1;
    public string BannerLine2 => _bannerLine2;
    public string BannerLine3 => _bannerLine3;
    public float BannerAlpha => Math.Clamp(_bannerTimer / 0.8f, 0f, 1f);

    public DemoSession(int screenW, int screenH, InputSystem input, Config cfg, GameConfig gc, Dictionary<string, ShapeData> shapes)
    {
        _screenW = screenW; _screenH = screenH; _cfg = cfg; _gc = gc; _shapes = shapes;
        _camera = new Camera(WorldCenter, screenW, screenH);
        _waveLog = new StreamWriter("wave_spawns.log", append: false) { AutoFlush = true };
        _waveLog.WriteLine($"# Wave spawn log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        SpawnPlayer(WorldCenter);
        SpawnNextWave();

        _systems = new ISystem[]
        {
            new PreviousStateSystem(),
            new PlayerControlSystem(input, _player, cfg, gc, _camera),
            new PhysicsSystem(),
            new VortexSystem(WorldCenter, cfg),
            new MovementSystem(),
            new BorderDampSystem(),
            new RaycastBulletSystem(_bus, _fx, _rng),
            new GhostSystem(),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            new FractureCrackSystem(_bus, _rng),
            new FractureGroupSystem(),
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
            new TunableApplySystem(cfg),
        };

        _bus.Subscribe<BulletHitEvent>(OnBulletHit);
        _bus.Subscribe<CollisionEvent>(OnCollision);
        _bus.Subscribe<CellPulverizedEvent>(OnCellPulverized);
        _bus.Subscribe<FractureCompletedEvent>(OnFractureCompleted);
        _bus.Subscribe<FractureSplitEvent>(OnFractureSplit);
    }

    public void Update(double dt)
    {
        ForceLog.Frame++;   // fixed-step counter, stamped onto every force-log line
        foreach (var s in _systems) s.Update(_world, dt);
        _world.FlushDeferred();
        _fx.Update((float)dt);

        // Camera follows the player with lag.
        if (_world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
            _camera.Update(_world.GetComponent<Transform>(_player).Position, (float)dt);

        // Phase 4: override TunableApplySystem's material values for typed asteroids.
        // Runs after all systems so editor changes take effect within the same frame.
        if (_gc.Asteroids.Count > 0)
        {
            _world.ForEach<FracturableBody, AsteroidTypeKey>((Entity _, ref FracturableBody fb, ref AsteroidTypeKey tk) =>
            {
                if (!_gc.Asteroids.TryGetValue(tk.Key, out var ac)) return;
                if (!_gc.Materials.TryGetValue(ac.Material, out var mc)) return;
                SyncMaterial(ref fb, mc);
            });
        }

        // Wave manager polling — spawn next wave when field is depleted or 30s have passed.
        _gameTime += (float)dt;
        _waveTimer += (float)dt;
        if (_bannerTimer > 0f) _bannerTimer -= (float)dt;
        _liveCells = CountLiveCells();
        // Threshold check requires 8s grace so a small wave doesn't instantly re-trigger.
        bool shouldSpawn = (_liveCells <= _maxLiveCells * 0.30f && _waveTimer > 8f)
                        || _waveTimer >= 30f;
        if (shouldSpawn && !_pendingWave)
        {
            _pendingWave = true;
            _waveCountdown = 1.5f;
        }
        if (_pendingWave)
        {
            _waveCountdown -= (float)dt;
            if (_waveCountdown <= 0f)
            {
                SpawnNextWave();
                _waveTimer = 0f;
                _pendingWave = false;
            }
        }
    }

    // Applies live-editable material fields from a MaterialConfig to a FracturableBody.
    // GrainArea and Density are not updated (spawn-time only — would require retessellation).
    private static void SyncMaterial(ref FracturableBody fb, MaterialConfig mc)
    {
        fb.Material.Toughness = mc.Toughness;
        fb.Material.Brittleness = mc.Brittleness;
        fb.Material.CrackDirectionality = mc.CrackDirectionality;
        fb.Material.MinFragmentArea = mc.MinFragmentArea;
        fb.Material.KineticFraction = mc.KineticFraction;
        fb.Material.SurfaceEfficiency = mc.SurfaceEfficiency;
        fb.Material.SpinPreStress = mc.SpinPreStress;
        fb.Material.CrackSpeed = mc.CrackSpeed;
        fb.Material.DetachCellScale = mc.DetachCellScale;
        fb.Material.DetachCellJitter = mc.DetachCellJitter;
        float tough = mc.Toughness;
        for (int i = 0; i < fb.Bonds.Length; i++)
            fb.Bonds[i].Strength = fb.Bonds[i].EdgeLength * tough;
    }

    public void Respawn()
    {
        foreach (var e in new List<Entity>(_world.QueryEntities<AsteroidTag>())) _world.DestroyEntity(e);
        _pendingWave = false;
        // Reset player to world center on manual respawn.
        if (_world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
        {
            _world.GetComponent<Transform>(_player).Position = WorldCenter;
            _world.GetComponent<Velocity>(_player).Linear = Vector2.Zero;
        }
        SpawnNextWave();
        _waveTimer = 0f;
    }

    // -------------------------------------------------------------------------

    // Blue palette used for the player ship cells and aim indicator.
    private static readonly AsteroidColor PlayerShipColor =
        new() { Fill = new Color(70, 130, 240), Outline = new Color(170, 205, 255) };

    private void SpawnPlayer(Vector2 pos)
    {
        _player = _world.CreateEntity();
        _world.AddComponent(_player, new Transform { Position = pos, PreviousPosition = pos });
        _world.AddComponent(_player, new Velocity());
        _world.AddComponent(_player, new AimComponent { Dir = Vector2.UnitX });
        _world.AddComponent(_player, new ShootCooldown());
        _world.AddComponent(_player, new PlayerTag());

        // Build a fracturable compound body from the authored shape if available.
        if (!string.IsNullOrEmpty(_gc.Player.Shape)
            && _shapes.TryGetValue(_gc.Player.Shape, out var sd)
            && sd.Outline.Length >= 3 && sd.Seeds.Length >= 1)
        {
            if (!_gc.Materials.TryGetValue(_gc.Player.Material, out var mc))
                mc = _gc.Materials.Values.First();
            var mat = new FractureProperties
            {
                Toughness = mc.Toughness,
                Brittleness = mc.Brittleness,
                CrackDirectionality = mc.CrackDirectionality,
                GrainArea = mc.GrainArea,
                MinFragmentArea = mc.MinFragmentArea,
                Density = mc.Density,
                KineticFraction = mc.KineticFraction,
                SurfaceEfficiency = mc.SurfaceEfficiency,
                SpinPreStress = mc.SpinPreStress,
                CrackSpeed = mc.CrackSpeed,
                DetachCellScale = mc.DetachCellScale,
                DetachCellJitter = mc.DetachCellJitter,
            };

            float sc = _gc.Player.ShapeScale;
            var outline = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
            var seedPos = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
            var seedMult = sd.Seeds.Select(s => s.BondMult).ToList();
            var body = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, _rng);

            float area = VoronoiTessellator.TotalArea(body);
            float mass = MathF.Max(1f, mat.Density * area);
            float inertia = VoronoiTessellator.ComputeInertia(body, mass);
            // LinearDrag = 0: player braking is handled manually in PlayerControlSystem.
            // Inertia = 0: rotation is driven by aim direction, not physics.
            _world.AddComponent(_player, new RigidBody { Mass = mass, Inertia = 0f, LinearDrag = 0f, AngularDrag = 0f, Restitution = 0.2f, Friction = 0.1f });
            _world.AddComponent(_player, new Collider { Shape = VoronoiTessellator.BuildShape(body), Layer = Layers.Player, Mask = Layers.Asteroid });
            _world.AddComponent(_player, body);
            _world.AddComponent(_player, PlayerShipColor);
            var (outline2, cracks) = ComputeEdges(body.Cells, body.Bonds);
            _world.AddComponent(_player, new RenderOutline { Outline = outline2, Cracks = cracks });
            return;
        }

        // Fallback: circle collider (no shape file present).
        _world.AddComponent(_player, new RigidBody { Mass = 12f, Inertia = 0f, LinearDrag = 0f, AngularDrag = 0f, Restitution = 0.2f, Friction = 0.1f });
        _world.AddComponent(_player, new Collider { Shape = new CircleShape(GameConst.PlayerRadius), Layer = Layers.Player, Mask = Layers.Asteroid });
    }

    private int CountLiveCells()
    {
        int total = 0;
        _world.ForEach<AsteroidTag, FracturableBody>((Entity _, ref AsteroidTag _, ref FracturableBody fb) =>
            total += fb.Cells.Length);
        return total;
    }

    // Expected Voronoi cell count for an asteroid of the given type and size.
    // Derived purely from geometry: area / GrainArea — the same formula BuildProceduralAsteroid uses.
    // Automatically stays in sync when GrainArea is changed in the material config.
    private float CellsFor(AsteroidConfig ac, float sizeMult)
    {
        if (ac.Procedural == null) return 1f;
        if (!_gc.Materials.TryGetValue(ac.Material, out var mc)) return 1f;
        float r = ac.Procedural.BaseRadius * sizeMult;
        return MathF.Max(1f, MathF.PI * r * r / mc.GrainArea);
    }

    private static Dictionary<string, float> DefaultBias(int wave) => new()
    {
        ["gravel"] = 0.05f,
        ["standard"] = 0.10f,
        ["spinner"] = 0.15f,
        ["unstable"] = 0.25f,
        ["boulder"] = 0.25f,
        ["armored"] = 0.20f,
    };

    // Stateless budget-packing function. Selects (type, sizeMult) pairs until budget or
    // cell cap is exhausted. sizeBias ∈ [-1,+1] applies a power-law skew to size sampling:
    // -1 = prefer small, 0 = uniform within SizeRange, +1 = prefer large.
    private List<(string Key, float SizeMult)> ChooseAsteroids(
        int budget, Dictionary<string, float> bias, float sizeBias, float cellCap)
    {
        var result = new List<(string, float)>();
        float remBudget = budget;
        float remCells = cellCap;
        float alpha = MathF.Pow(2f, -sizeBias);   // 1=uniform, 0.5=large-biased, 2=small-biased

        while (true)
        {
            var candidates = new List<(string key, AsteroidConfig ac, float w)>();
            foreach (var (key, w) in bias)
            {
                if (w <= 0f) continue;
                if (!_gc.Asteroids.TryGetValue(key, out var ac) || ac.Procedural == null) continue;
                if (ac.UnlockWave > _waveNumber) continue;
                float minMult = ac.SizeRange[0];
                if (ac.BaseCost * minMult > remBudget) continue;
                if (CellsFor(ac, minMult) > remCells) continue;
                candidates.Add((key, ac, w));
            }
            if (candidates.Count == 0) break;

            float total = candidates.Sum(c => c.w);
            float r = (float)_rng.NextDouble() * total;
            float cum = 0f;
            (string chosenKey, AsteroidConfig chosenAc, float _) = candidates[0];
            foreach (var c in candidates) { cum += c.w; if (r <= cum) { chosenKey = c.key; chosenAc = c.ac; break; } }

            // Budget constraint: linear in sizeMult.
            float maxByBudget = remBudget / chosenAc.BaseCost;
            // Cell constraint: cells ∝ radius² ∝ sizeMult² → invert quadratically.
            // CellsFor(ac, mult) = π × (BaseRadius × mult)² / GrainArea = k × mult²
            // solve k × mult² = remCells → mult = sqrt(remCells / k) where k = CellsFor(ac, 1)
            float kUnit = CellsFor(chosenAc, 1f);
            float maxByCells = MathF.Sqrt(remCells / kUnit);
            float maxMult = Math.Min(chosenAc.SizeRange[1], Math.Min(maxByBudget, maxByCells));
            float minMult0 = chosenAc.SizeRange[0];
            if (maxMult < minMult0) break;

            float u = (float)_rng.NextDouble();
            float sizeMult = minMult0 + MathF.Pow(u, alpha) * (maxMult - minMult0);

            result.Add((chosenKey, sizeMult));
            remBudget -= chosenAc.BaseCost * sizeMult;
            remCells  -= CellsFor(chosenAc, sizeMult);
        }
        return result;
    }

    private void SpawnNextWave()
    {
        _waveNumber++;
        _maxLiveCells = Math.Min(300 + (_waveNumber - 1) * 30, _gc.MaxLiveCells);

        var waveDef = _gc.Waves.FirstOrDefault(w => w.Wave == _waveNumber);
        int budget = waveDef is { Budget: > 0 } ? (int)waveDef.Budget : 20 + _waveNumber * 5;
        var bias = waveDef?.Spawns ?? DefaultBias(_waveNumber);
        float sizeBias = waveDef?.SizeBias ?? 0.4f;

        int liveCells = CountLiveCells();
        float cellCap = MathF.Max(0, _maxLiveCells - liveCells);
        if (cellCap < 3f)
        {
            LogAndBannerWave(waveDef, new List<(string Key, float SizeMult)>(), budget, sizeBias);
            return;
        }

        var spawns = ChooseAsteroids(budget, bias, sizeBias, cellCap);

        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : WorldCenter;

        var placed = new List<(Vector2 pos, float r)>();
        foreach (var (key, sizeMult) in spawns)
        {
            float r = _gc.Asteroids.TryGetValue(key, out var ac) && ac.Procedural != null
                ? ac.Procedural.BaseRadius * sizeMult : 80f * sizeMult;
            Vector2 pos = FindSpawnPosition(r, placed, playerPos);
            placed.Add((pos, r));
            SpawnAsteroid(pos, key, sizeMult);
        }

        LogAndBannerWave(waveDef, spawns, budget, sizeBias);
    }

    private void LogAndBannerWave(
        WaveDefinition? waveDef, List<(string Key, float SizeMult)> spawns,
        int budget, float sizeBias)
    {
        // Aggregate per-type counts for banner and log
        var typeCount = new Dictionary<string, List<float>>();
        int totalCells = 0;
        foreach (var (key, sm) in spawns)
        {
            if (!typeCount.TryGetValue(key, out var lst)) typeCount[key] = lst = new List<float>();
            lst.Add(sm);
            if (_gc.Asteroids.TryGetValue(key, out var ac))
                totalCells += (int)CellsFor(ac, sm);
        }

        // Build banner strings
        _bannerLine1 = $"WAVE {_waveNumber}";
        _bannerLine2 = $"budget={budget}   sizeBias={sizeBias:+0.0;-0.0;0.0}   {spawns.Count} asteroids   ~{totalCells} cells";
        var typeTokens = typeCount.Select(kv => $"{kv.Value.Count}× {kv.Key}");
        _bannerLine3 = string.Join("   ", typeTokens);
        _bannerTimer = 4f;

        // Write to wave log
        if (_waveLog == null) return;
        _waveLog.WriteLine();
        _waveLog.WriteLine($"[Wave {_waveNumber}] T={_gameTime:0.0}s   budget={budget}   sizeBias={sizeBias:+0.00;-0.00;0.00}   maxCells={_maxLiveCells}");
        _waveLog.WriteLine($"  {spawns.Count} asteroids   ~{totalCells} cells total");
        foreach (var (key, mults) in typeCount)
        {
            _gc.Asteroids.TryGetValue(key, out var ac);
            int cellsEst = ac != null ? mults.Sum(m => (int)CellsFor(ac, m)) : 0;
            string sizeStr = string.Join(" ", mults.Select(m => $"{m:0.00}"));
            string cellStr = ac != null
                ? string.Join(" ", mults.Select(m => $"~{(int)CellsFor(ac, m)}")) : "?";
            _waveLog.WriteLine($"  {key,-10} x{mults.Count}   sizes: {sizeStr}   cells: {cellStr}   subtotal≈{cellsEst}");
        }
    }

    // Border strip width: asteroids spawn within this distance of the world edge.
    private const float BorderZone = 400f;

    // Extra margin beyond the screen edge where spawns are suppressed (so rocks don't pop in).
    private const float ViewMargin = 100f;

    private Vector2 FindSpawnPosition(float radius, List<(Vector2 pos, float r)> placed, Vector2 playerPos)
    {
        float playerClear = radius + GameConst.PlayerRadius + 150f;
        Vector2 camOff = _camera.Offset;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            // Pick a random position within the border zone of one of the four world edges.
            Vector2 pos;
            switch (_rng.Next(4))
            {
                case 0:
                    pos = new((float)_rng.NextDouble() * GameConst.WorldW,
                               (float)_rng.NextDouble() * BorderZone); break;                          // top strip
                case 1:
                    pos = new((float)_rng.NextDouble() * GameConst.WorldW,
                               GameConst.WorldH - (float)_rng.NextDouble() * BorderZone); break;       // bottom strip
                case 2:
                    pos = new((float)_rng.NextDouble() * BorderZone,
                               (float)_rng.NextDouble() * GameConst.WorldH); break;                    // left strip
                default:
                    pos = new(GameConst.WorldW - (float)_rng.NextDouble() * BorderZone,
                               (float)_rng.NextDouble() * GameConst.WorldH); break;                    // right strip
            }

            // Reject if inside the camera viewport (would pop in visibly).
            Vector2 sp = pos + camOff;
            if (sp.X > -ViewMargin && sp.X < _screenW + ViewMargin &&
                sp.Y > -ViewMargin && sp.Y < _screenH + ViewMargin) continue;

            // Reject if too close to the player.
            if ((pos - playerPos).LengthSquared() < playerClear * playerClear) continue;

            // Reject if overlapping an already-placed asteroid.
            bool clear = true;
            foreach (var (p, r) in placed)
            {
                float minDist = radius + r + 20f;
                if ((pos - p).LengthSquared() < minDist * minDist) { clear = false; break; }
            }
            if (clear) return pos;
        }

        // Fallback: pick a random position in the top border strip, ignoring overlap.
        return new Vector2((float)_rng.NextDouble() * GameConst.WorldW,
                           (float)_rng.NextDouble() * BorderZone);
    }

    private void SpawnAsteroid(Vector2 pos)
    {
        var mat = new FractureProperties
        {
            Toughness = _cfg.Toughness.Value,
            Brittleness = _cfg.Brittleness.Value,
            GrainArea = _cfg.Grain.Value,
            MinFragmentArea = _cfg.MinFragArea.Value,
            Density = _cfg.Density.Value,
            KineticFraction = _cfg.KineticFraction.Value,
            SurfaceEfficiency = _cfg.SurfaceEff.Value,
            SpinPreStress = _cfg.SpinPreStress.Value,
            CrackSpeed = _cfg.CrackSteps.Value,
            DetachCellScale = _cfg.DetachScale.Value,
            DetachCellJitter = _cfg.DetachJitter.Value,
        };
        var body = VoronoiTessellator.BuildAsteroid(_rng.Next(9, 14), _cfg.AstRadius.Value, mat, membership: null, _rng);

        float spread = (float)(_rng.NextDouble() * Math.PI * 2);
        var vel = new Vector2(MathF.Cos(spread), MathF.Sin(spread)) * (float)(_rng.NextDouble() * _cfg.AstSpeed.Value);
        float spin = (float)(_rng.NextDouble() * 2 - 1) * _cfg.AstSpin.Value;

        byte shade = (byte)_rng.Next(50, 80);

        var e = SpawnBody(body, pos, (float)(_rng.NextDouble() * Math.PI * 2), vel, spin,
                  new AsteroidColor
                  {
                      Fill = new Color(shade, (byte)(shade - 6), (byte)(shade - 12)),
                      Outline = new Color(150, 138, 120)
                  });

        ForceLog.EntityFilter = e.Id;
        ForceLog.Categories = ForceCat.Contact | ForceCat.Spawn;

        if (ForceLog.On(ForceCat.Spawn, e.Id))
            ForceLog.Write(ForceCat.Spawn, e.Id,
                $"asteroid: vel {ForceLog.V(vel)} (≤AstSpeed{_cfg.AstSpeed.Value:0.#}) spin {spin:0.##} (±AstSpin{_cfg.AstSpin.Value:0.##})");
    }

    // Spawns a typed asteroid. sizeMult comes from ChooseAsteroids and has already been
    // sampled from ac.SizeRange, so we use it directly rather than re-sampling.
    private void SpawnAsteroid(Vector2 pos, string typeKey, float sizeMult)
    {
        if (!_gc.Asteroids.TryGetValue(typeKey, out var ac) || ac.Procedural == null) return;
        if (!_gc.Materials.TryGetValue(ac.Material, out var mc))
            mc = _gc.Materials.Values.First();

        var mat = new FractureProperties
        {
            Toughness = mc.Toughness,
            Brittleness = mc.Brittleness,
            CrackDirectionality = mc.CrackDirectionality,
            GrainArea = mc.GrainArea,
            MinFragmentArea = mc.MinFragmentArea,
            Density = mc.Density * ac.DensityMult,
            KineticFraction = mc.KineticFraction,
            SurfaceEfficiency = mc.SurfaceEfficiency,
            SpinPreStress = mc.SpinPreStress,
            CrackSpeed = mc.CrackSpeed,
            DetachCellScale = mc.DetachCellScale,
            DetachCellJitter = mc.DetachCellJitter,
        };

        var body = BuildProceduralAsteroid(ac.Procedural, sizeMult, mat);

        // Direction: toward world center from spawn position, with ±45° random spread.
        Vector2 toCenter = WorldCenter - pos;
        float baseAngle = MathF.Atan2(toCenter.Y, toCenter.X);
        float spread = ((float)_rng.NextDouble() * 2f - 1f) * (MathF.PI / 4f);
        float angle = baseAngle + spread;
        float speed = ac.SpeedRange[0] + (float)_rng.NextDouble() * (ac.SpeedRange[1] - ac.SpeedRange[0]);
        var vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;

        float spinMag = ac.SpinRange[0] + (float)_rng.NextDouble() * (ac.SpinRange[1] - ac.SpinRange[0]);
        float spin = spinMag * (_rng.NextDouble() > 0.5 ? 1f : -1f);

        var e = SpawnBody(body, pos, (float)(_rng.NextDouble() * MathF.Tau), vel, spin,
                    MaterialColor(ac.Material));
        _world.AddComponent(e, new AsteroidTypeKey { Key = typeKey });

        if (ForceLog.On(ForceCat.Spawn, e.Id))
            ForceLog.Write(ForceCat.Spawn, e.Id,
                $"asteroid [{typeKey}/{ac.Material}]: vel {ForceLog.V(vel)} spin {spin:0.##} sizeMult {sizeMult:0.##}");
    }

    // ── Procedural asteroid construction ────────────────────────────────────────

    private FracturableBody BuildProceduralAsteroid(
        ProceduralAsteroidConfig proc, float sizeMult, in FractureProperties mat)
    {
        // 1. Convex outline
        int sides = _rng.Next(proc.VertexCount[0], proc.VertexCount[1] + 1);
        float radius = proc.BaseRadius * sizeMult;
        var (convex, _) = PolygonUtils.GenerateConvex(sides, radius, _rng);

        // 2. Roughness: sinusoidal noise on vertex radii; only positive displacements to
        //    preserve convexity (inward bumps would be absorbed by the convex hull anyway).
        if (proc.Roughness > 0.01f)
        {
            float freq = MathF.Max(1f, proc.NoiseFrequency);
            float phase = (float)(_rng.NextDouble() * MathF.Tau);
            for (int i = 0; i < convex.Length; i++)
            {
                float θ = MathF.Atan2(convex[i].Y, convex[i].X);
                float n = MathF.Sin(θ * freq + phase) * 0.6f
                        + MathF.Sin(θ * freq * 2.1f + phase * 1.4f) * 0.4f;
                if (n > 0f) convex[i] *= 1f + proc.Roughness * n;
            }
        }

        // 3. Bounding radius (for distribution normalisation)
        float maxR = 0f;
        foreach (var v in convex) maxR = MathF.Max(maxR, v.Length());
        if (maxR < 1f) maxR = 1f;

        // 4. Seed count derived from area / GrainArea — same source as CellsFor() in the wave manager.
        int seedCount = Math.Clamp((int)(MathF.PI * radius * radius / mat.GrainArea), 4, 600);
        var seeds = ScatterSeedsProc(convex, seedCount, proc.SeedClusterCenter);

        // 5. Per-seed bond mult from BondMultDistribution
        var bondMults = new float[seeds.Count];
        for (int i = 0; i < seeds.Count; i++)
            bondMults[i] = EvalProcDist(proc.BondMultDistribution, seeds[i], maxR);

        // 6. Concavity: radially-biased probability of removing peripheral cells
        float concavity = proc.ConcavityBias;
        Func<Vector2, bool>? membership = concavity > 0.01f
            ? s =>
              {
                  float r = s.Length() / maxR;
                  return _rng.NextDouble() > concavity * (r * r);
              }
        : (Func<Vector2, bool>?)null;

        // 7. Build with explicit seeds
        var body = VoronoiTessellator.BuildWithSeeds(convex, seeds, bondMults, membership, mat, _rng);

        // 8. Patch per-cell DensityMult and BlastResist from their distributions
        if (proc.DensityMultDistribution != null || proc.BlastResistDistribution != null)
        {
            for (int i = 0; i < body.Cells.Length; i++)
            {
                Vector2 cen = body.Cells[i].Centroid;
                if (proc.DensityMultDistribution != null)
                    body.Cells[i].DensityMult = EvalProcDist(proc.DensityMultDistribution, cen, maxR);
                if (proc.BlastResistDistribution != null)
                    body.Cells[i].BlastResist = Math.Clamp(
                        EvalProcDist(proc.BlastResistDistribution, cen, maxR), 0f, 1f);
            }
        }

        return body;
    }

    private List<Vector2> ScatterSeedsProc(Vector2[] convex, int count, float clusterCenter)
    {
        float maxR = 0f;
        foreach (var v in convex) maxR = MathF.Max(maxR, v.Length());

        var seeds = new List<Vector2>(count);
        int tries = 0;
        while (seeds.Count < count && tries < count * 50)
        {
            tries++;
            float θ = (float)(_rng.NextDouble() * MathF.Tau);
            float u = (float)_rng.NextDouble();
            // Power distribution: higher clusterCenter → seeds congregate near centroid
            float r = maxR * MathF.Pow(u, 1f + clusterCenter * 4f);
            var p = new Vector2(MathF.Cos(θ) * r, MathF.Sin(θ) * r);
            if (ProcPointInPolygon(p, convex)) seeds.Add(p);
        }
        while (seeds.Count < 3)
            seeds.Add(new Vector2(
                (float)(_rng.NextDouble() - 0.5) * maxR * 0.4f,
                (float)(_rng.NextDouble() - 0.5) * maxR * 0.4f));
        return seeds;
    }

    private static bool ProcPointInPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = poly[i], pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private static float EvalProcDist(CellPropertyDistribution? dist, Vector2 pos, float maxR)
    {
        if (dist == null) return 1f;
        float r = maxR > 0f ? Math.Clamp(pos.Length() / maxR, 0f, 1f) : 0f;
        return dist.Type switch
        {
            "radialGradient" =>
                (dist.CenterValue ?? 1f) * (1f - MathF.Pow(r, dist.Exponent ?? 1f))
              + (dist.SurfaceValue ?? 1f) * MathF.Pow(r, dist.Exponent ?? 1f),
            "noiseClusters" =>
                Math.Clamp(
                    (dist.BaseValue ?? 1f) + (dist.Amplitude ?? 0f) *
                    (MathF.Sin(pos.X * (dist.Frequency ?? 1f) / maxR * MathF.Tau + 1.73f)
                   * MathF.Cos(pos.Y * (dist.Frequency ?? 1f) / maxR * MathF.Tau + 0.91f)),
                    0f, 10f),
            _ => dist.Value,
        };
    }

    private static AsteroidColor MaterialColor(string materialKey) => materialKey switch
    {
        "ice" => new AsteroidColor { Fill = new Color(70, 115, 155), Outline = new Color(140, 195, 230) },
        "metal" => new AsteroidColor { Fill = new Color(50, 60, 75), Outline = new Color(115, 135, 165) },
        "glass" => new AsteroidColor { Fill = new Color(60, 120, 95), Outline = new Color(110, 195, 155) },
        _ => new AsteroidColor { Fill = new Color(64, 58, 52), Outline = new Color(150, 138, 120) },
    };

    private Entity SpawnBody(FracturableBody body, Vector2 pos, float rot, Vector2 vel, float spin, AsteroidColor color, bool ghost = false)
    {
        float mass = VoronoiTessellator.TotalMass(body);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);

        var e = _world.CreateEntity();
        _world.AddComponent(e, new Transform { Position = pos, Rotation = rot, PreviousPosition = pos, PreviousRotation = rot });
        _world.AddComponent(e, new Velocity { Linear = vel, Angular = spin });
        _world.AddComponent(e, new RigidBody
        {
            Mass = mass,
            Inertia = inertia,
            LinearDrag = _cfg.LinDrag.Value,
            AngularDrag = _cfg.AngDrag.Value,
            Restitution = _cfg.Restitution.Value,
            Friction = _cfg.Friction.Value
        });
        _world.AddComponent(e, new Collider
        {
            Shape = VoronoiTessellator.BuildShape(body),
            Layer = ghost ? Layers.Ghost : Layers.Asteroid,
            Mask = ghost ? 0 : (Layers.Asteroid | Layers.Player)
        });
        _world.AddComponent(e, body);
        _world.AddComponent(e, new AsteroidTag());
        _world.AddComponent(e, color);
        var (outline, cracks) = ComputeEdges(body.Cells, body.Bonds);
        _world.AddComponent(e, new RenderOutline { Outline = outline, Cracks = cracks });
        if (ghost) _world.AddComponent(e, new FractureGhost { Remaining = 0.0f });
        return e;
    }

    /// <summary>Boundary edges of a celled body: a cell edge whose midpoint isn't
    /// shared with another cell (≈ unshared edge). Returns body-local segment endpoint
    /// pairs — the outer silhouette plus any crater edges left by vaporised cells.</summary>
    /// <summary>Classifies a body's cell edges for rendering: OUTLINE (unshared
    /// silhouette + crater edges) and CRACKS (edges shared by two cells no longer
    /// bonded). Both are body-local segment-endpoint pairs.</summary>
    private static (Vector2[] outline, Vector2[] cracks) ComputeEdges(Cell[] cells, Bond[] bonds)
    {
        var bonded = new HashSet<(int, int)>();
        foreach (var b in bonds) bonded.Add((Math.Min(b.A, b.B), Math.Max(b.A, b.B)));

        // edge-midpoint key → the up-to-two cells that own that edge
        var edgeCells = new Dictionary<(int, int), (int a, int b)>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 mid = (v[i] + v[(i + 1) % n]) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                edgeCells[key] = edgeCells.TryGetValue(key, out var p) ? (p.a, ci) : (ci, -1);
            }
        }

        var outline = new List<Vector2>();
        var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                Vector2 mid = (a + b) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }                       // unshared → silhouette
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))      // shared but not bonded → crack
                {
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }     // dedupe (edge is in both cells)
                }
                // else bonded shared edge → hidden
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }

    // -------------------------------------------------------------------------
    // Fracture handler
    // -------------------------------------------------------------------------

    private void OnBulletHit(BulletHitEvent ev)
    {
        if (!_world.IsAlive(ev.Asteroid) || !_world.IsAlive(ev.Bullet)) return;

        Vector2 bulletVel = _world.GetComponent<Velocity>(ev.Bullet).Linear;
        _world.DestroyEntity(ev.Bullet);

        float bulletMass = _cfg.BulletMass.Value * _cfg.EnergyScale.Value;

        // Use GameConfig weapon profile when available; Tuner fallback keeps calibration.
        _gc.Weapons.TryGetValue(_gc.Player.StartingWeapon, out var wc);
        var weapon = new WeaponProfile
        {
            Directionality = wc?.Directionality ?? _cfg.Directionality.Value,
            MomentumTransfer = wc?.MomentumTransfer ?? _cfg.MomentumTransfer.Value,
            EjectFraction = wc?.EjectFraction ?? _cfg.EjectFraction.Value,
            ImpactSpin = wc?.ImpactSpin ?? _cfg.ImpactSpin.Value,
            BlastFraction = wc?.BlastFraction ?? _cfg.Blast.Value,
        };
        var timing = new FractureTiming
        {
            StepsPerIteration = (int)_cfg.CrackSteps.Value,
            FramesPerIteration = (int)_cfg.CrackFrames.Value,
            DetachOnSplit = _cfg.Detach.Value > 0.5f,
        };

        // Impact flash at the hit (energy proxy ∝ bullet KE).
        EmitFlash(ev.Point, 0.5f * bulletMass * bulletVel.LengthSquared());

        // Seed (or extend) a multi-frame crack. Dust and fragments arrive over the next
        // frames via CellPulverizedEvent / FractureCompletedEvent.
        FractureService.BeginFracture(
            _world, ev.Asteroid, ev.StruckCell, ev.Point, ev.ShotDir,
            bulletVel, bulletMass, weapon, timing, _rng);
    }

    // Minimum approach speed (px/s) at the contact point for a collision to trigger fracture.
    private const float AsteroidCollisionThreshold = 20f;

    private void OnCollision(CollisionEvent ev)
    {
        Entity eA = ev.EntityA, eB = ev.EntityB;
        if (!_world.IsAlive(eA) || !_world.IsAlive(eB)) return;
        if (!_world.HasComponent<AsteroidTag>(eA) || !_world.HasComponent<AsteroidTag>(eB)) return;
        if (!_world.HasComponent<FracturableBody>(eA) || !_world.HasComponent<FracturableBody>(eB)) return;
        if (!_world.HasComponent<Velocity>(eA) || !_world.HasComponent<Velocity>(eB)) return;
        if (!_world.HasComponent<RigidBody>(eA) || !_world.HasComponent<RigidBody>(eB)) return;

        var pair = eA.Id < eB.Id ? (eA.Id, eB.Id) : (eB.Id, eA.Id);
        if (_activeCollisionFractures.Contains(pair)) return;   // already fracturing this contact

        // Relative velocity at the contact point, incorporating both bodies' rotation.
        // Normal points from B toward A, so a negative dot means approaching.
        Vector2 cp = ev.Contact.ContactPoint;
        ref var tA = ref _world.GetComponent<Transform>(eA);
        ref var tB = ref _world.GetComponent<Transform>(eB);
        ref var vA = ref _world.GetComponent<Velocity>(eA);
        ref var vB = ref _world.GetComponent<Velocity>(eB);
        Vector2 rA = cp - tA.Position;
        Vector2 rB = cp - tB.Position;
        // Velocity of each body at the contact point: v + ω × r (2D: ω × r = (-ωy, ωx))
        Vector2 vcA = vA.Linear + new Vector2(-vA.Angular * rA.Y, vA.Angular * rA.X);
        Vector2 vcB = vB.Linear + new Vector2(-vB.Angular * rB.Y, vB.Angular * rB.X);
        // Relative velocity of B w.r.t. A at contact. Normal points B→A, so approaching = dot < 0.
        Vector2 vRel = vcB - vcA;
        float approachSpeed = -Vector2.Dot(vRel, ev.Contact.Normal);
        if (approachSpeed < AsteroidCollisionThreshold) return;

        _activeCollisionFractures.Add(pair);

        if (ForceLog.On(ForceCat.Energy, eA.Id) || ForceLog.On(ForceCat.Energy, eB.Id))
            ForceLog.Write(ForceCat.Energy, eA.Id,
                $"AST-COLLISION vs e{eB.Id}: approach {approachSpeed:0.#}px/s (thresh {AsteroidCollisionThreshold}) " +
                $"vRel{ForceLog.V(vRel)} → fracture both (energy logged per body below)");

        float massA = _world.GetComponent<RigidBody>(eA).Mass;
        float massB = _world.GetComponent<RigidBody>(eB).Mass;

        // WeaponProfile for asteroid impacts. MomentumTransfer = 0 (contact solver handles
        // momentum exchange). EnergyScale deflates the raw kinetic energy so impacts scale
        // naturally: slow grazes chip, fast collisions shatter.
        var weapon = new WeaponProfile
        {
            Directionality = _cfg.Directionality.Value,
            MomentumTransfer = 0f,
            EjectFraction = _cfg.EjectFraction.Value,
            ImpactSpin = _cfg.ImpactSpin.Value * 0.5f,
            BlastFraction = _cfg.AsteroidBlast.Value,
            EnergyScale = _cfg.AsteroidEnergyScale.Value,
        };
        var timing = new FractureTiming
        {
            StepsPerIteration = (int)_cfg.CrackSteps.Value,
            FramesPerIteration = (int)_cfg.CrackFrames.Value,
            DetachOnSplit = _cfg.Detach.Value > 0.5f,
        };

        float vfxEnergy = 0.5f * (massA * massB / (massA + massB)) * approachSpeed * approachSpeed;
        EmitFlash(cp, vfxEnergy);

        // Crack direction into A: blend from the contact normal (head-on, geometry only)
        // to the full relative velocity (carries B's + A's spin via ω×r). The normal points
        // B→A, i.e. into A — the head-on impact direction. AstDirSpin = 0 → pure normal,
        // 1 → pure relative velocity.
        Vector2 vRelDir = vRel.LengthSquared() > 1f ? Vector2.Normalize(vRel) : ev.Contact.Normal;
        Vector2 blended = Vector2.Lerp(ev.Contact.Normal, vRelDir, _cfg.AstDirSpin.Value);
        Vector2 impactDirAB = blended.LengthSquared() > 1e-6f ? Vector2.Normalize(blended) : ev.Contact.Normal;

        // Fracture A: B is the impactor. Pass the contact-point relative velocity in a
        // form that makes BeginFracture's (impactorVel - bodyLinear) == vRel correctly:
        // impactorVelForA = vRel + vA.Linear, so vRel+vA.Linear - vA.Linear = vRel. ✓
        FractureService.BeginFracture(_world, eA, -1, cp, impactDirAB,
            vRel + vA.Linear, massB, weapon, timing, _rng);

        // Fracture B: A is the impactor. Relative velocity is reversed.
        Vector2 impactDirBA = -impactDirAB;
        FractureService.BeginFracture(_world, eB, -1, cp, impactDirBA,
            -vRel + vB.Linear, massA, weapon, timing, _rng);
    }

    private void OnCellPulverized(CellPulverizedEvent ev)
    {
        AsteroidColor color = BodyColor(ev.Body);
        Vector2 bodyPos = ev.WorldCentroid, carrier = Vector2.Zero;
        if (_world.IsAlive(ev.Body))
        {
            if (_world.HasComponent<Transform>(ev.Body)) bodyPos = _world.GetComponent<Transform>(ev.Body).Position;
            if (_world.HasComponent<Velocity>(ev.Body)) carrier = _world.GetComponent<Velocity>(ev.Body).Linear;
        }
        // Dust and polygon debris coexist: the dust is the haze, the debris are the chunks.
        EmitDustBurst(ev.WorldCentroid, ev.WorldCentroid - bodyPos, carrier, ev.Area, color, EnergyRef);
        SpawnCellDebris(ev, color);
    }

    /// <summary>Cuts a pulverised cell's polygon into 2 or 4 convex pieces (1–2 random lines
    /// through its centroid) and spawns each as a collider-less, fading debris entity that
    /// inherits the cell's motion plus an outward scatter.</summary>
    private void SpawnCellDebris(in CellPulverizedEvent ev, AsteroidColor color)
    {
        var verts = ev.WorldVerts;
        if (verts == null || verts.Length < 3) return;

        var pieces = new List<List<Vector2>> { new List<Vector2>(verts) };
        int cuts = _rng.Next(1, 3);   // 1 or 2 cuts → 2 or 4 pieces
        for (int c = 0; c < cuts; c++)
        {
            float ang = (float)(_rng.NextDouble() * Math.PI);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            var next = new List<List<Vector2>>();
            foreach (var poly in pieces)
            {
                var left = new List<Vector2>(); var right = new List<Vector2>();
                SplitConvexByLine(poly, ev.WorldCentroid, dir, left, right);
                if (left.Count >= 3) next.Add(left);
                if (right.Count >= 3) next.Add(right);
            }
            if (next.Count > 0) pieces = next;
        }

        float ttl = _cfg.DebrisTtl.Value;
        float scatter = _cfg.DebrisScatter.Value;
        foreach (var poly in pieces)
        {
            Vector2 cen = Vector2.Zero;
            foreach (var v in poly) cen += v;
            cen /= poly.Count;

            var local = new Vector2[poly.Count];
            for (int i = 0; i < poly.Count; i++) local[i] = poly[i] - cen;

            Vector2 outward = cen - ev.WorldCentroid;
            outward = outward.LengthSquared() > 1e-4f
                ? Vector2.Normalize(outward)
                : new Vector2(MathF.Cos((float)(_rng.NextDouble() * Math.PI * 2)),
                              MathF.Sin((float)(_rng.NextDouble() * Math.PI * 2)));
            Vector2 vel = ev.CellVelocity + outward * (scatter * (0.5f + (float)_rng.NextDouble()));
            float spin = ev.BodyAngular + (float)(_rng.NextDouble() * 2 - 1) * 4f;

            var e = _world.CreateEntity();
            _world.AddComponent(e, new Transform { Position = cen, Rotation = 0f, PreviousPosition = cen, PreviousRotation = 0f });
            _world.AddComponent(e, new Velocity { Linear = vel, Angular = spin });
            _world.AddComponent(e, new TimeToLive { Remaining = ttl });
            _world.AddComponent(e, new DebrisPiece { Local = local, Color = color.Fill, MaxTtl = ttl });

            if (ForceLog.On(ForceCat.Debris, e.Id))
                ForceLog.Write(ForceCat.Debris, e.Id,
                    $"vel = cell{ForceLog.V(ev.CellVelocity)} + out{ForceLog.V(outward)}·scatter{scatter:0.#} = {ForceLog.V(vel)} | " +
                    $"spin = parent{ev.BodyAngular:0.##} + rand = {spin:0.##} | ttl {ttl:0.##}");
        }
    }

    // Splits a convex polygon by the infinite line through P with direction dir into two
    // convex halves (left = side ≥ 0, right = side < 0). Either may end up < 3 verts.
    private static void SplitConvexByLine(List<Vector2> poly, Vector2 P, Vector2 dir,
                                          List<Vector2> left, List<Vector2> right)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 cur = poly[i], nxt = poly[(i + 1) % n];
            float sc = dir.X * (cur.Y - P.Y) - dir.Y * (cur.X - P.X);
            float sn = dir.X * (nxt.Y - P.Y) - dir.Y * (nxt.X - P.X);
            if (sc >= 0f) left.Add(cur); else right.Add(cur);
            if ((sc > 0f) != (sn > 0f) && sc != sn)
            {
                float tt = sc / (sc - sn);
                Vector2 ip = cur + (nxt - cur) * tt;
                left.Add(ip); right.Add(ip);
            }
        }
    }

    private void RefreshFractureGroup(int gid)
    {
        _world.ForEach<FractureGroup>((Entity _, ref FractureGroup fg) =>
        {
            if (fg.Id == gid) fg.FramesLeft = 16;
        });
    }

    private void OnFractureCompleted(FractureCompletedEvent ev)
    {
        _activeCollisionFractures.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        if (ev.Body == _player) { _world.DestroyEntity(ev.Body); _player = default; return; }
        AsteroidColor color = BodyColor(ev.Body);
        var fg = new FractureGroup
        {
            Id = _world.HasComponent<FractureGroup>(ev.Body)
                ? _world.GetComponent<FractureGroup>(ev.Body).Id
                : _nextGroupId++,
            FramesLeft = 16,
        };
        RefreshFractureGroup(fg.Id);
        foreach (var f in ev.Fragments)
        {
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color, EnergyRef); continue; }
            var ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
        }
        _world.DestroyEntity(ev.Body);
    }

    private void OnFractureSplit(FractureSplitEvent ev)
    {
        _activeCollisionFractures.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        if (ev.Body == _player) { _world.DestroyEntity(ev.Body); _player = default; return; }
        AsteroidColor color = BodyColor(ev.Body);
        var fg = new FractureGroup
        {
            Id = _world.HasComponent<FractureGroup>(ev.Body)
                ? _world.GetComponent<FractureGroup>(ev.Body).Id
                : _nextGroupId++,
            FramesLeft = 16,
        };
        RefreshFractureGroup(fg.Id);
        foreach (var p in ev.Pieces)
        {
            var f = p.Spec;
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color, EnergyRef); continue; }
            Entity ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
            // A piece still cracking carries its remapped process → keep propagating.
            if (p.Process.HasValue) _world.AddComponent(ne, p.Process.Value);
        }
        _world.DestroyEntity(ev.Body);
    }

    private static readonly AsteroidColor DefaultColor =
        new() { Fill = new Color(64, 58, 52), Outline = new Color(150, 138, 120) };

    private AsteroidColor BodyColor(Entity e) =>
        _world.IsAlive(e) && _world.HasComponent<AsteroidColor>(e)
            ? _world.GetComponent<AsteroidColor>(e) : DefaultColor;

    // VFX modulation references (a default shot ≈ 1.0). Tuned to the default weapon.
    private const float EnergyRef = 80_000f;
    private const float DustAreaRef = 1400f;

    /// <summary>Impact flash: a bright disk that expands and fades; size ∝ impact energy.</summary>
    private void EmitFlash(Vector2 point, float energy)
    {
        if (_cfg.FlashSize.Value <= 0f) return;
        float e = Math.Clamp(energy / EnergyRef, 0.25f, 2.5f);
        float sz = _cfg.FlashSize.Value * e;
        float ttl = _cfg.FlashTtl.Value;
        _fx.Emit(new Particle
        {
            Position = point,
            Velocity = Vector2.Zero,
            Drag = 0f,
            Life = ttl,
            MaxLife = ttl,
            Size0 = sz * 0.35f,
            Size1 = sz,
            Color0 = new Color(255, 245, 210, 235),
            Color1 = new Color(255, 165, 70, 0),
        });
    }

    /// <summary>Dust burst (a vaporised cell or a tiny leftover shard). Count ∝ energy,
    /// flung in a cone around <paramref name="dirHint"/> and drifting with <paramref name="carrier"/>,
    /// size ∝ cell area, colour from the material.</summary>
    private void EmitDustBurst(Vector2 centroid, Vector2 dirHint, Vector2 carrier, float area, AsteroidColor color, float energy)
    {
        int n = (int)(_cfg.DustCount.Value * Math.Clamp(energy / EnergyRef, 0.35f, 2.5f));
        if (n <= 0) return;

        Vector2 vdir = dirHint.LengthSquared() > 1e-4f
            ? Vector2.Normalize(dirHint)
            : new Vector2(MathF.Cos((float)_rng.NextDouble() * MathF.Tau), MathF.Sin((float)_rng.NextDouble() * MathF.Tau));
        float cone = _cfg.DustSpread.Value * MathF.PI;
        float baseSz = _cfg.DustSize.Value * MathF.Sqrt(MathF.Max(area, 1f) / DustAreaRef);
        Color dust = color.Outline;

        for (int i = 0; i < n; i++)
        {
            float ang = ((float)_rng.NextDouble() * 2f - 1f) * cone;
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            Vector2 dir = new(vdir.X * ca - vdir.Y * sa, vdir.X * sa + vdir.Y * ca);
            float spd = _cfg.DustSpeed.Value * (0.4f + (float)_rng.NextDouble());
            float ttl = _cfg.DustTtl.Value * (0.6f + 0.6f * (float)_rng.NextDouble());
            float sz = baseSz * (0.7f + 0.6f * (float)_rng.NextDouble());
            Vector2 jit = new((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f);
            _fx.Emit(new Particle
            {
                Position = centroid + jit * baseSz,
                Velocity = dir * spd + carrier,
                Drag = 2.2f,
                Life = ttl,
                MaxLife = ttl,
                Size0 = sz,
                Size1 = sz * 0.1f,
                Color0 = dust.WithAlpha(220),
                Color1 = dust.WithAlpha(0),
            });
        }
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
    private readonly GameConfig _gc;
    private readonly Camera _camera;
    private static readonly Color BulletColor = new(255, 230, 90);

    public PlayerControlSystem(InputSystem input, Entity player, Config cfg, GameConfig gc, Camera camera)
    { _input = input; _player = player; _cfg = cfg; _gc = gc; _camera = camera; }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(_player)) return;

        // Keep the player body locked to the aim orientation — no physics spinning.
        ref var vel0 = ref world.GetComponent<Velocity>(_player);
        vel0.Angular = 0f;

        ref var t = ref world.GetComponent<Transform>(_player);
        ref var aim = ref world.GetComponent<AimComponent>(_player);

        // ── Aim: mouse in screen space → world space via camera offset ──
        float fdt = (float)dt;
        Vector2 mouseWorld = _input.MouseScreen - _camera.Offset;
        Vector2 toMouse = mouseWorld - t.Position;
        if (toMouse.LengthSquared() > 1f)
        {
            Vector2 targetDir = Vector2.Normalize(toMouse);
            float targetAngle = MathF.Atan2(targetDir.Y, targetDir.X);
            float currentAngle = MathF.Atan2(aim.Dir.Y, aim.Dir.X);
            float delta = targetAngle - currentAngle;
            while (delta > MathF.PI) delta -= MathF.Tau;
            while (delta < -MathF.PI) delta += MathF.Tau;
            var p0 = _gc.Player;
            float maxTurn = p0.RotSpeed * fdt;
            float turn = MathF.Abs(delta) <= maxTurn ? delta : MathF.Sign(delta) * maxTurn;
            float newAngle = currentAngle + turn;
            aim.Dir = new Vector2(MathF.Cos(newAngle), MathF.Sin(newAngle));
        }

        // ── World-axis WASD with aim-relative lateral drag ──
        // WASD always maps to world-up/down/left/right — no rotation of keys.
        // Velocity is decomposed into aim-aligned and aim-perpendicular components each frame.
        // The perpendicular component bleeds at LateralDrag s⁻¹, so turning to face your
        // direction of travel removes the drain and movement becomes fully efficient.
        var p = _gc.Player;

        ref var vel = ref world.GetComponent<Velocity>(_player);

        // Passive lateral bleed — applied before thrust so it can't prevent reaching MaxSpeed
        // when moving in the aim direction.
        Vector2 fwd = aim.Dir;
        Vector2 rgt = new(fwd.Y, -fwd.X);   // CW perpendicular (aim's right)
        float vAlong = Vector2.Dot(vel.Linear, fwd);
        float vPerp = Vector2.Dot(vel.Linear, rgt);
        vPerp *= MathF.Exp(-p.LateralDrag * fdt);
        vel.Linear = fwd * vAlong + rgt * vPerp;

        Vector2 wantDir = Vector2.Zero;
        if (_input.IsHeld(KeyCode.W)) wantDir.Y -= 1;
        if (_input.IsHeld(KeyCode.S)) wantDir.Y += 1;
        if (_input.IsHeld(KeyCode.A)) wantDir.X -= 1;
        if (_input.IsHeld(KeyCode.D)) wantDir.X += 1;
        if (wantDir != Vector2.Zero) wantDir = Vector2.Normalize(wantDir);

        if (wantDir != Vector2.Zero)
        {
            bool freshPress = (_input.IsPressed(KeyCode.W) && wantDir.Y < 0)
                           || (_input.IsPressed(KeyCode.S) && wantDir.Y > 0)
                           || (_input.IsPressed(KeyCode.A) && wantDir.X < 0)
                           || (_input.IsPressed(KeyCode.D) && wantDir.X > 0);
            if (freshPress) vel.Linear += wantDir * p.Impulse;

            float step = p.Thrust * fdt;
            Vector2 target = wantDir * p.MaxSpeed;
            Vector2 diff = target - vel.Linear;
            float diffLen = diff.Length();
            vel.Linear += diffLen <= step ? diff : diff / diffLen * step;

            float spd = vel.Linear.Length();
            if (spd > p.MaxSpeed) vel.Linear *= p.MaxSpeed / spd;
        }
        else
        {
            vel.Linear *= MathF.Exp(-p.BrakeDrag * fdt);
        }

        // Sync body rotation to aim. The shape was authored facing up (−Y), so +π/2 aligns
        // it with the engine's default aim direction (UnitX = right).
        t.Rotation = MathF.Atan2(aim.Dir.Y, aim.Dir.X) + MathF.PI / 2f;

        ref var cd = ref world.GetComponent<ShootCooldown>(_player);
        if (cd.Remaining > 0f) cd.Remaining -= (float)dt;

        // Weapon shot params from GameConfig; Tuner values as fallback.
        _gc.Weapons.TryGetValue(p.StartingWeapon, out var wc);
        float fireCooldown = wc != null ? 1f / wc.FireRate : _cfg.FireRate.Value;
        float bulletSpeed = wc?.ProjectileSpeed ?? _cfg.BulletSpeed.Value;
        float bulletTtl = wc?.TimeToLive ?? 1.5f;

        if (_input.IsMouseLeft && cd.Remaining <= 0f)
        {
            cd.Remaining = fireCooldown;
            Vector2 muzzle = t.Position + aim.Dir * (GameConst.PlayerRadius + 6f);
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
            world.AddComponent(b, new Velocity { Linear = aim.Dir * bulletSpeed });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = BulletColor });
            world.AddComponent(b, new TimeToLive { Remaining = bulletTtl });
            if (ForceLog.On(ForceCat.Spawn, b.Id))
                ForceLog.Write(ForceCat.Spawn, b.Id,
                    $"bullet: vel {ForceLog.V(aim.Dir * bulletSpeed)} (speed {bulletSpeed:0.#})");
        }
    }
}

// Sweeps each bullet's travel segment against asteroids (raycast → no tunnelling).
sealed class RaycastBulletSystem : ISystem
{
    private readonly EventBus _bus;
    private readonly ParticleSystem _fx;
    private readonly Random _rng;
    private readonly List<(Entity bullet, Vector2 from, Vector2 to)> _seg = new();

    public RaycastBulletSystem(EventBus bus, ParticleSystem fx, Random rng) { _bus = bus; _fx = fx; _rng = rng; }

    public void Update(World world, double dt)
    {
        _seg.Clear();
        world.ForEach<Transform, BulletTag>((Entity e, ref Transform t, ref BulletTag _) =>
            _seg.Add((e, t.PreviousPosition, t.Position)));

        foreach (var (bullet, from, to) in _seg)
        {
            if (!world.IsAlive(bullet)) continue;

            // Tracer sparks — a hot fleck shed along the bullet's path each step.
            float ttl = 0.08f + 0.06f * (float)_rng.NextDouble();
            _fx.Emit(new Particle
            {
                Position = to,
                Drag = 3f,
                Life = ttl,
                MaxLife = ttl,
                Velocity = new Vector2((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f) * 40f,
                Size0 = 1.7f,
                Size1 = 0.2f,
                Color0 = new Color(255, 235, 130, 210),
                Color1 = new Color(255, 110, 40, 0),
            });

            Vector2 d = to - from;
            if (d.LengthSquared() < 1e-4f) continue;
            if (PhysicsQueries.Raycast(world, from, to, Layers.Asteroid, out var hit))
                _bus.Publish(new BulletHitEvent(hit.Entity, bullet, hit.PartIndex, hit.Point, Vector2.Normalize(d)));
        }
    }
}


sealed class FractureGroupSystem : ISystem
{
    public void Update(World world, double dt)
    {
        world.ForEach<FractureGroup>((Entity e, ref FractureGroup fg) =>
        {
            if (--fg.FramesLeft <= 0) world.RemoveComponent<FractureGroup>(e);
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

// Counts down the spawn-grace on fresh fragments, then re-enables their collision.
sealed class GhostSystem : ISystem
{
    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        world.ForEach<FractureGhost, Collider>((Entity _, ref FractureGhost g, ref Collider c) =>
        {
            if (g.Done) return;
            g.Remaining -= fdt;
            if (g.Remaining <= 0f)
            {
                c.Layer = Layers.Asteroid;
                c.Mask = Layers.Asteroid | Layers.Player;
                g.Done = true;
            }
        });
    }
}

// Writes the live-tunable physics constants onto every body each frame.
sealed class TunableApplySystem : ISystem
{
    private readonly Config _cfg;
    public TunableApplySystem(Config cfg) { _cfg = cfg; }

    public void Update(World world, double dt)
    {
        var cfg = _cfg;
        world.ForEach<RigidBody>((Entity e, ref RigidBody rb) =>
        {
            // Player handles its own drag in PlayerControlSystem — don't overwrite.
            if (world.HasComponent<PlayerTag>(e)) return;
            rb.Restitution = cfg.Restitution.Value;
            rb.Friction = cfg.Friction.Value;
            rb.LinearDrag = cfg.LinDrag.Value;
            rb.AngularDrag = cfg.AngDrag.Value;
        });
        float tough = cfg.Toughness.Value;
        world.ForEach<FracturableBody>((Entity e, ref FracturableBody fb) =>
        {
            // Player material is set from GameConfig at spawn and must not be overwritten.
            if (world.HasComponent<PlayerTag>(e)) return;
            fb.Material.Brittleness = cfg.Brittleness.Value;
            fb.Material.KineticFraction = cfg.KineticFraction.Value;
            fb.Material.MinFragmentArea = cfg.MinFragArea.Value;
            fb.Material.SurfaceEfficiency = cfg.SurfaceEff.Value;
            fb.Material.SpinPreStress = cfg.SpinPreStress.Value;
            fb.Material.Toughness = tough;
            // Toughness is live: rescale every bond from its stored edge length.
            for (int i = 0; i < fb.Bonds.Length; i++)
                fb.Bonds[i].Strength = fb.Bonds[i].EdgeLength * tough;
        });
    }
}

// =============================================================================
// Camera
// =============================================================================

sealed class Camera
{
    public Vector2 Position;   // world-space coordinate of the screen centre
    public float FollowSpeed = 4f;

    private readonly float _hw, _hh;  // half screen dimensions
    private readonly float _minX, _minY, _maxX, _maxY;  // clamped world bounds

    public Camera(Vector2 startPos, int screenW, int screenH)
    {
        _hw = screenW * 0.5f; _hh = screenH * 0.5f;
        _minX = _hw; _minY = _hh;
        _maxX = GameConst.WorldW - _hw;
        _maxY = GameConst.WorldH - _hh;
        Position = Vector2.Clamp(startPos, new Vector2(_minX, _minY), new Vector2(_maxX, _maxY));
    }

    public void Update(Vector2 target, float dt)
    {
        target = Vector2.Clamp(target, new Vector2(_minX, _minY), new Vector2(_maxX, _maxY));
        Position += (target - Position) * (1f - MathF.Exp(-FollowSpeed * dt));
    }

    /// <summary>Pixel offset to add to every world position to get screen position:
    /// screenPos = worldPos + Offset.</summary>
    public Vector2 Offset => new(_hw - Position.X, _hh - Position.Y);

    /// <summary>True if the world-space point (with optional radius) is within the screen.</summary>
    public bool IsVisible(Vector2 worldPos, float radius = 0f)
    {
        Vector2 s = worldPos + Offset;
        return s.X > -radius && s.X < _hw * 2 + radius && s.Y > -radius && s.Y < _hh * 2 + radius;
    }
}

// =============================================================================
// VortexSystem — pulls bodies toward the world centre with a CCW tangential spin
// =============================================================================

sealed class VortexSystem : ISystem
{
    private readonly Vector2 _centre;
    private readonly Config _cfg;
    private float _time = 0f;

    private float _variation_centripetal = 0.3f;
    private float _variation_tangential = 0.3f;
    public VortexSystem(Vector2 centre, Config cfg) { _centre = centre; _cfg = cfg; }

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        _time += fdt;
        float deadzone = _cfg.VortexDeadzone.Value;
        // Centripetal and tangential oscillate ±0.05 on independent slow cycles (11s / 13s)
        // so the vortex breathes without a fixed beat.
        float centripetalK = MathF.Max(0f, _cfg.VortexCentripetal.Value
                             + _variation_centripetal * MathF.Sin(_time * MathF.Tau / 11f));
        float tangentialK = MathF.Max(0f, _cfg.VortexTangential.Value
                             + _variation_tangential * MathF.Sin(_time * MathF.Tau / 13f + MathF.PI * 0.5f));
        float capFrames = _cfg.VortexCapFrames.Value;

        world.ForEach<Transform, Velocity, RigidBody>((Entity _, ref Transform t, ref Velocity v, ref RigidBody rb) =>
        {
            Vector2 toCenter = _centre - t.Position;
            float dist = toCenter.Length();
            if (dist < 1e-3f) return;
            float excess = dist - deadzone;
            if (excess <= 0f) return;

            Vector2 radial = toCenter / dist;           // toward centre
            Vector2 tangent = new(-radial.Y, radial.X);  // CCW perpendicular

            // Per-component velocity cap: capFrames × one frame of force at this excess.
            // Projections are positive when velocity already agrees with the force direction.
            // The cap only kicks in when velocity and force are co-directional — if an object
            // is moving outward (against centripetal) or CW (against tangential) the full force
            // is always applied. The soft clamp adds only what's left to reach the cap,
            // removing the discontinuity at the threshold.
            float forceC = centripetalK * excess * fdt;
            float forceT = tangentialK * excess * fdt;
            float capC = forceC * capFrames;
            float capT = forceT * capFrames;

            float vC = Vector2.Dot(v.Linear, radial);   // + = toward centre
            float vT = Vector2.Dot(v.Linear, tangent);  // + = CCW

            if (vC < capC) v.Linear += radial * MathF.Min(forceC, capC - vC);
            if (vT < capT) v.Linear += tangent * MathF.Min(forceT, capT - vT);
        });
    }
}

// =============================================================================
// BorderDampSystem — exponentially damps the outward velocity component when an entity enters the 200px
// border zone. factor = 0 at zone edge → 1 at wall face, so damping strengthens smoothly.
// No counter-force is applied — entities slow and stop rather than bounce back.
sealed class BorderDampSystem : ISystem
{
    private const float Zone = 200f;
    private const float DampK = 20f;   // effective s⁻¹ rate at the wall face

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        world.ForEach<Transform, Velocity>((Entity _, ref Transform t, ref Velocity v) =>
        {
            float x = t.Position.X, y = t.Position.Y;

            float dL = x;
            if (dL < Zone && v.Linear.X < 0f)
                v.Linear.X *= MathF.Exp(-DampK * (1f - dL / Zone) * fdt);

            float dR = GameConst.WorldW - x;
            if (dR < Zone && v.Linear.X > 0f)
                v.Linear.X *= MathF.Exp(-DampK * (1f - dR / Zone) * fdt);

            float dT = y;
            if (dT < Zone && v.Linear.Y < 0f)
                v.Linear.Y *= MathF.Exp(-DampK * (1f - dT / Zone) * fdt);

            float dB = GameConst.WorldH - y;
            if (dB < Zone && v.Linear.Y > 0f)
                v.Linear.Y *= MathF.Exp(-DampK * (1f - dB / Zone) * fdt);

            // Hard clamp — safety net only, rarely reached.
            t.Position = Vector2.Clamp(t.Position,
                Vector2.Zero, new Vector2(GameConst.WorldW, GameConst.WorldH));
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
    private static readonly Color GridCol = new(22, 24, 35);
    private const float GridSpacing = 200f;
    private static readonly Color PlayerFill = new(70, 130, 240);
    private static readonly Color PlayerEdge = new(170, 205, 255);
    private static readonly FontSpec Hud     = new("monospace", 14f);
    private static readonly FontSpec Panel   = new("monospace", 13f);
    private static readonly FontSpec Banner1 = new("monospace", 26f);
    private static readonly FontSpec Banner2 = new("monospace", 16f);
    private static readonly FontSpec Banner3 = new("monospace", 13f);
    private const float TeleSq = 200f * 200f;
    private readonly List<Vector2> _mesh = new();      // reused: all cell world-verts of a body
    private readonly List<int> _meshLens = new();  // reused: per-cell vertex counts
    private readonly List<Vector2> _dbuf = new();      // reused: one debris piece's world verts

    public DemoRenderer(int w, int h) { _w = w; _h = h; }

    public void Draw(IRenderer r, DemoSession session, Config cfg, float alpha, bool showPanel, float fps)
    {
        var world = session.World;
        r.Begin(Bg);
        var camera = session.Camera;
        Vector2 camOff = camera.Offset;

        // World-space grid — only lines that fall within the current viewport.
        {
            float wx0 = -camOff.X, wx1 = _w - camOff.X;
            float wy0 = -camOff.Y, wy1 = _h - camOff.Y;
            float gx0 = MathF.Ceiling(wx0 / GridSpacing) * GridSpacing;
            float gy0 = MathF.Ceiling(wy0 / GridSpacing) * GridSpacing;
            for (float gx = gx0; gx <= wx1; gx += GridSpacing)
            {
                float sx = gx + camOff.X;
                r.DrawLine(new Vector2(sx, 0), new Vector2(sx, _h), GridCol, 1f);
            }
            for (float gy = gy0; gy <= wy1; gy += GridSpacing)
            {
                float sy = gy + camOff.Y;
                r.DrawLine(new Vector2(0, sy), new Vector2(_w, sy), GridCol, 1f);
            }
        }

        // Asteroids — draw each cell.
        world.ForEach<Transform, FracturableBody, AsteroidColor>(
            (Entity e, ref Transform t, ref FracturableBody fb, ref AsteroidColor col) =>
        {
            var (wpos, rot) = Interp(t, alpha);
            if (!camera.IsVisible(wpos, 300f)) return;
            var pos = wpos + camOff;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);

            // A body mid-fracture carries live damage masks; settled bodies use the cache.
            bool[]? broken = null, pulv = null;
            if (world.HasComponent<FractureProcess>(e))
            {
                ref var fp = ref world.GetComponent<FractureProcess>(e);
                broken = fp.Broken; pulv = fp.Pulverized;
            }

            // Fill cells as one path (skip vaporised cells → craters open live).
            _mesh.Clear(); _meshLens.Clear();
            for (int ci = 0; ci < fb.Cells.Length; ci++)
            {
                if (pulv != null && pulv[ci]) continue;
                var lv = fb.Cells[ci].Local;
                for (int k = 0; k < lv.Length; k++)
                    _mesh.Add(new Vector2(lv[k].X * c - lv[k].Y * s + pos.X, lv[k].X * s + lv[k].Y * c + pos.Y));
                _meshLens.Add(lv.Length);
            }
            r.FillPath(CollectionsMarshal.AsSpan(_mesh), CollectionsMarshal.AsSpan(_meshLens), col.Fill);

            // Outline (silhouette + craters) and cracks (broken bonds between touching cells).
            if (broken != null && pulv != null)
            {
                var (outline, cracks) = ComputeEdgesLive(fb.Cells, fb.Bonds, broken, pulv);
                DrawSegs(r, outline, pos, c, s, col.Outline, 1.5f);
                DrawSegs(r, cracks, pos, c, s, CrackColor(col.Fill), 1f);
            }
            else if (world.HasComponent<RenderOutline>(e))
            {
                var ro = world.GetComponent<RenderOutline>(e);
                DrawSegs(r, ro.Outline, pos, c, s, col.Outline, 1.5f);
                DrawSegs(r, ro.Cracks, pos, c, s, CrackColor(col.Fill), 1f);
            }
        });

        // Polygon debris (collider-less rock chunks from vaporised cells), fading over TTL.
        world.ForEach<Transform, DebrisPiece, TimeToLive>(
            (Entity _, ref Transform t, ref DebrisPiece dp, ref TimeToLive ttl) =>
        {
            var (wpos, rot) = Interp(t, alpha);
            if (!camera.IsVisible(wpos, 100f)) return;
            var pos = wpos + camOff;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);
            float k = dp.MaxTtl > 0f ? Math.Clamp(ttl.Remaining / dp.MaxTtl, 0f, 1f) : 1f;
            _dbuf.Clear();
            foreach (var lv in dp.Local)
                _dbuf.Add(new Vector2(lv.X * c - lv.Y * s + pos.X, lv.X * s + lv.Y * c + pos.Y));
            r.FillPolygon(CollectionsMarshal.AsSpan(_dbuf), dp.Color.WithAlpha((byte)(dp.Color.A * k)));
        });

        // Dust, sparks, flashes (drawn over the rock, under the bullets).
        session.Fx.Draw(r, camOff);

        // Bullets — a tapering tracer streak (glow + hot core) plus the round.
        float tracerLen = cfg.TracerLen.Value, tracerW = cfg.TracerWidth.Value;
        world.ForEach<Transform, BulletTag, BulletVisual>(
            (Entity _, ref Transform t, ref BulletTag _, ref BulletVisual bv) =>
        {
            var (wpos, _) = Interp(t, alpha);
            if (!camera.IsVisible(wpos, 10f)) return;
            var p = wpos + camOff;
            Vector2 d = t.Position - t.PreviousPosition;
            Vector2 dir = d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : new Vector2(0f, -1f);
            if (tracerLen > 0f)
            {
                Vector2 tail = p - dir * tracerLen;
                r.DrawLine(tail, p, new Color(255, 170, 60, 80), tracerW * 2.5f);    // glow
                r.DrawLine(tail, p, new Color(255, 240, 165, 220), tracerW);          // hot core
            }
            r.FillCircle(p, tracerW * 1.3f, bv.Color);                               // round
        });

        // Player — cell mesh drawn above by the asteroid pass if fracturable; just aim line here.
        world.ForEach<Transform, PlayerTag, AimComponent>(
            (Entity e, ref Transform t, ref PlayerTag _, ref AimComponent aim) =>
        {
            var (wpos, _) = Interp(t, alpha);
            var p = wpos + camOff;
            if (!world.HasComponent<FracturableBody>(e))
            {
                r.FillCircle(p, GameConst.PlayerRadius, PlayerFill);
                r.DrawCircle(p, GameConst.PlayerRadius, PlayerEdge, 2f);
            }
            r.DrawLine(p, p + aim.Dir * (GameConst.PlayerRadius + 12f), PlayerEdge, 2f);
        });

        // HUD — top bar.
        r.DrawText($"fps {fps,3:F0}   wave {session.WaveNumber}   asteroids {world.Count<AsteroidTag>()}   material {cfg.MaterialName}",
                   new Vector2(12, 10), new Color(190, 200, 220), Hud);

        // HUD — bottom bar: live cell counter + wave timer.
        string waveStatus = session.WavePending
            ? "spawning..."
            : $"next wave in: {MathF.Max(0f, 30f - session.WaveTimer):0.0}s";
        string cellBar = $"cells: {session.LiveCells} / {session.MaxLiveCells}   {waveStatus}";
        r.DrawText(cellBar, new Vector2(12, _h - 42f), new Color(160, 175, 200), Hud);
        r.DrawText("WASD move   mouse aim   click fire   R respawn   M material   arrows tune   Tab panel   Esc quit",
                   new Vector2(12, _h - 22f), new Color(110, 120, 140), Panel);

        // Wave banner — fades in for 0.8s, holds, fades out over last 0.8s.
        if (session.BannerActive)
        {
            byte a = (byte)(255 * session.BannerAlpha);
            float cx = _w * 0.5f;
            float cy = _h * 0.38f;
            // Approximate centering: monospace char width ≈ fontSize × 0.6
            float x1 = cx - session.BannerLine1.Length * Banner1.Size * 0.30f;
            float x2 = cx - session.BannerLine2.Length * Banner2.Size * 0.30f;
            float x3 = cx - session.BannerLine3.Length * Banner3.Size * 0.30f;
            r.DrawText(session.BannerLine1, new Vector2(x1, cy),        new Color(255, 220, 80,  a), Banner1);
            r.DrawText(session.BannerLine2, new Vector2(x2, cy + 38f),  new Color(200, 215, 240, a), Banner2);
            r.DrawText(session.BannerLine3, new Vector2(x3, cy + 64f),  new Color(140, 155, 175, a), Banner3);
        }

        if (showPanel) DrawPanel(r, cfg);

        r.End();
    }

    private void DrawPanel(IRenderer r, Config cfg)
    {
        var ps = cfg.T.Params;
        float x = _w - 270f, y = 8f, rowH = 17f;
        float panelH = ps.Count * rowH + 16f;

        Span<Vector2> bg = stackalloc Vector2[4]
        { new(x - 10, y - 4), new(_w - 4, y - 4), new(_w - 4, y + panelH), new(x - 10, y + panelH) };
        r.FillPolygon(bg, new Color(0, 0, 0, 150));

        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.IsHeader)
            {
                r.DrawText(p.Name, new Vector2(x, y + i * rowH), new Color(120, 200, 230), Panel);
                continue;
            }
            bool sel = i == cfg.T.Selected;
            Color c = sel ? new Color(255, 220, 110) : new Color(170, 178, 195);
            string line = $"{(sel ? ">" : " ")} {p.Name,-16} {p.Display}";
            r.DrawText(line, new Vector2(x, y + i * rowH), c, Panel);
        }
    }

    /// <summary>Edge classification for a body mid-fracture, from its live damage masks:
    /// vaporised cells are absent (their edges become crater rims), a shared edge is hidden
    /// only while its two cells are still bonded, otherwise it draws as a crack.</summary>
    private static (Vector2[] outline, Vector2[] cracks) ComputeEdgesLive(
        Cell[] cells, Bond[] bonds, bool[] broken, bool[] pulverized)
    {
        var bonded = new HashSet<(int, int)>();
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            if (broken[bi]) continue;
            int a = bonds[bi].A, b = bonds[bi].B;
            if (pulverized[a] || pulverized[b]) continue;
            bonded.Add((Math.Min(a, b), Math.Max(a, b)));
        }

        var edgeCells = new Dictionary<(int, int), (int a, int b)>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            if (pulverized[ci]) continue;
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 mid = (v[i] + v[(i + 1) % n]) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                edgeCells[key] = edgeCells.TryGetValue(key, out var p) ? (p.a, ci) : (ci, -1);
            }
        }

        var outline = new List<Vector2>();
        var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            if (pulverized[ci]) continue;
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                Vector2 mid = (a + b) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))
                {
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }
                }
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }

    private static void DrawSegs(IRenderer r, Vector2[] segs, Vector2 pos, float c, float s, Color color, float w)
    {
        for (int k = 0; k + 1 < segs.Length; k += 2)
        {
            Vector2 a = segs[k], b = segs[k + 1];
            r.DrawLine(
                new Vector2(a.X * c - a.Y * s + pos.X, a.X * s + a.Y * c + pos.Y),
                new Vector2(b.X * c - b.Y * s + pos.X, b.X * s + b.Y * c + pos.Y),
                color, w);
        }
    }

    private static Color CrackColor(Color fill) =>
        new((byte)(fill.R * 0.35f), (byte)(fill.G * 0.35f), (byte)(fill.B * 0.35f));

    private static (Vector2 pos, float rot) Interp(in Transform t, float alpha)
    {
        Vector2 d = t.Position - t.PreviousPosition;
        if (d.LengthSquared() > TeleSq) return (t.Position, t.Rotation);
        float dr = t.Rotation - t.PreviousRotation;
        while (dr > MathF.PI) dr -= MathF.Tau;
        while (dr < -MathF.PI) dr += MathF.Tau;
        return (t.PreviousPosition + d * alpha, t.PreviousRotation + dr * alpha);
    }
}
