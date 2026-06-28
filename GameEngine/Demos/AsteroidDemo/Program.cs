// Asteroids — Destruction Sandbox
//
//   WASD        thrust          Mouse      aim
//   Left-click  fire            Backspace  respawn asteroids / ship
//   Q/E/R       skills          F/G        grenade / piercing
//   Up/Down     select param    Left/Right adjust param
//   Tab         toggle panel    Esc        quit
//
//   cd GameEngine/Demos/AsteroidDemo && dotnet run

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AsteroidsGame;
using AsteroidsGame.Components;
using AsteroidsGame.Config;
using AsteroidsGame.Gameplay;
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

var session = new DemoSession(W, H, input, gameConfig, shapes);
editor.SetSession(session);
var renderer = new DemoRenderer(W, H);
var shapeEditor = new ShapeEditorViewport(shapes, assetsDir);

const double FixedDt = 1.0 / 120.0;
var fixedStep = new FixedTimestep(FixedDt);
var sw = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;
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

    if (!inShapeEditor && input.IsPressed(KeyCode.Back)) session.Respawn();   // R is the slow-mo skill
    if (input.IsPressed(KeyCode.L)) ToggleForceLog();
    if (!editor.IsTextInputActive)
    {
        if (input.IsPressed(KeyCode.N)) session.ForceNextWave();
        if (input.IsPressed(KeyCode.LeftBracket))  session.GameTime = MathF.Max(0f, session.GameTime - 60f);
        if (input.IsPressed(KeyCode.RightBracket)) session.GameTime += 60f;
    }

    int steps = fixedStep.Advance(frameTime);
    for (int i = 0; i < steps; i++) session.Update(FixedDt);

    if (inShapeEditor)
    {
        const float LeftW = 210f, RightW = 375f, TabH = 32f;
        window.Renderer.Begin(new Color(8, 9, 14));
        shapeEditor.Draw(window.Renderer, editor.SelectedShape, LeftW, W - RightW, TabH, H);
        window.Renderer.End();
    }
    else
    {
        renderer.Draw(window.Renderer, session, gameConfig, fixedStep.Alpha, (float)fps);
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


// =============================================================================
// DemoSession — owns the world, systems, spawning and the fracture handler
// =============================================================================

sealed class DemoSession
{
    private readonly World _world = new();
    private readonly EventBus _bus = new();
    private readonly ISystem[] _systems;
    private readonly GameConfig _gc;
    private readonly Random _rng = new(1234);
    private readonly ParticleSystem _fx = new();
    private readonly ParticleEffects _effects;
    private readonly FractureGameplay _fracture;
    private readonly int _screenW, _screenH;
    // Player entity lives in the shared fracture gameplay (control transfers to a cockpit
    // fragment on ship break-up). Property so existing call sites are unchanged.
    private Entity _player { get => _fracture.Player; set => _fracture.Player = value; }
    private Camera _camera;
    private readonly GameContext _ctx;
    private int _waveNumber = 0;
    private float _waveTimer = 0f;    // seconds since last wave spawn
    private float _gameTime  = 0f;    // total elapsed game time
    private int _maxLiveCells = 300;  // grows +30 per wave, capped at gc.MaxLiveCells
    private int _liveCells = 0;       // cached each Update() — reused for both HUD and wave trigger
    private bool _pendingWave = false;
    private float _waveCountdown = 0f;

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
    public float GameTime { get => _gameTime; set => _gameTime = MathF.Max(0f, value); }

    public void ForceNextWave()
    {
        _pendingWave = false;
        SpawnNextWave();
        _waveTimer = 0f;
    }
    public string BannerLine1 => _bannerLine1;
    public string BannerLine2 => _bannerLine2;
    public string BannerLine3 => _bannerLine3;
    public float BannerAlpha => Math.Clamp(_bannerTimer / 0.8f, 0f, 1f);

    public DemoSession(int screenW, int screenH, InputSystem input, GameConfig gc, Dictionary<string, ShapeData> shapes)
    {
        _screenW = screenW; _screenH = screenH; _gc = gc;
        _camera = new Camera(screenW, screenH) { Position = WorldCenter };
        _ctx = new GameContext(gc, shapes, input, screenW, screenH);
        _effects = new ParticleEffects(_world, _fx, gc.Vfx, _rng);
        _fracture = new FractureGameplay(_world, _bus, _ctx, _rng, _effects);
        _waveLog = new StreamWriter("wave_spawns.log", append: false) { AutoFlush = true };
        _waveLog.WriteLine($"# Wave spawn log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        SpawnPlayer();
        SpawnNextWave();

        _systems = new ISystem[]
        {
            new PreviousStateSystem(),
            new PlayerControlSystem(_ctx, _camera)
            {
                Player = _player,
                OnPiercingFire = (from, dir) => PiercingPrefab.Spawn(_world, _ctx, from, dir, _rng),
            },
            new AlienAiSystem(_ctx, _bus, _rng),
            new PhysicsSystem(),
            new VortexSystem(WorldCenter, gc.Vortex),
            new MovementSystem(),
            new BorderDampSystem(GameConst.WorldW, GameConst.WorldH),
            new RaycastBulletSystem(_bus, _fx, _rng),
            new GrenadeSystem(_bus),
            new GhostSystem(),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            new FractureCrackSystem(_bus, _rng),
            new FractureGroupSystem(),
            new StressRelaxSystem(),
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
        };

        // Fracture-response events are subscribed by the shared FractureGameplay (ctor).
    }

    public void Update(double dt)
    {
        ForceLog.Frame++;   // fixed-step counter, stamped onto every force-log line

        // Slow-mo skill scales the simulation dt (input is polled at wall-clock rate elsewhere).
        double gameDt = dt;
        if (_world.IsAlive(_player) && _world.HasComponent<SkillState>(_player))
        {
            float slowActive = _world.GetComponent<SkillState>(_player).SlowMoActive;
            if (slowActive > 0f && _gc.Skills.TryGetValue("slowmo", out var smCfg))
                gameDt *= smCfg.TimeScale ?? 0.3;
        }

        foreach (var s in _systems)
        {
            if (s is PlayerControlSystem pcs) pcs.Player = _player;   // follow player-transfer
            s.Update(_world, gameDt);
        }
        _world.FlushDeferred();
        _fx.Update((float)gameDt);

        // Camera follows the player with lag, clamped to the world bounds.
        if (_world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
        {
            Vector2 target = _world.GetComponent<Transform>(_player).Position;
            float hw = _screenW * 0.5f, hh = _screenH * 0.5f;
            target = Vector2.Clamp(target, new Vector2(hw, hh),
                                   new Vector2(GameConst.WorldW - hw, GameConst.WorldH - hh));
            _camera.Position += (target - _camera.Position) * (1f - MathF.Exp(-4f * (float)dt));
        }

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

        // Wave manager polling — spawn next wave when field is depleted or hard timer expires.
        _gameTime += (float)dt;
        _waveTimer += (float)dt;
        if (_bannerTimer > 0f) _bannerTimer -= (float)dt;
        _liveCells = CountLiveCells();
        var _ws = _gc.WaveSystem;
        bool shouldSpawn = (_liveCells <= _maxLiveCells * _ws.TriggerThreshold && _waveTimer > _ws.GracePeriodSeconds)
                        || _waveTimer >= _ws.HardTriggerIntervalSeconds;
        if (shouldSpawn && !_pendingWave)
        {
            _pendingWave = true;
            _waveCountdown = _ws.SpawnDelaySeconds;
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
        fb.Material.Restitution = mc.Restitution;
        fb.Material.RelaxRate = mc.RelaxRate;
        fb.Material.Brittleness = mc.Brittleness;
        fb.Material.CrackDirectionality = mc.CrackDirectionality;
        fb.Material.CrackSpeed = mc.CrackSpeed;
        fb.Material.MinFragmentArea = mc.MinFragmentArea;
        fb.Material.Density = mc.Density;
        fb.Material.CellToughness = mc.CellToughness;
        fb.Material.SpinPreStress = mc.SpinPreStress;
        fb.Material.DetachCellScale = mc.DetachCellScale;
        fb.Material.DetachCellJitter = mc.DetachCellJitter;
        float tough = mc.Toughness;
        for (int i = 0; i < fb.Bonds.Length; i++)
            fb.Bonds[i].Strength = fb.Bonds[i].EdgeLength * tough * fb.Bonds[i].StrengthMult;
    }

    public void Respawn()
    {
        foreach (var e in new List<Entity>(_world.QueryEntities<AsteroidTag>())) _world.DestroyEntity(e);
        _pendingWave = false;
        // Reset player to world center on manual respawn, or recreate it if the ship was
        // destroyed (the demo shares the game's fracture model, so the player is mortal now).
        if (_world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
        {
            _world.GetComponent<Transform>(_player).Position = WorldCenter;
            _world.GetComponent<Velocity>(_player).Linear = Vector2.Zero;
        }
        else
        {
            SpawnPlayer();
            _fracture.PendingGameOver = false;
        }
        SpawnNextWave();
        _waveTimer = 0f;
    }

    // -------------------------------------------------------------------------

    private void SpawnPlayer()
    {
        // Shared prefab — identical to the game (seed roles, loadout, drag, colour),
        // so the demo player can shoot and use skills exactly like the game.
        _player = PlayerPrefab.Spawn(_world, _ctx, _rng);
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
                float minMult = ac.SizeRange[0];
                if (ac.BaseCost * minMult > remBudget) continue;
                if (AsteroidPrefab.CellsFor(_ctx, ac, minMult) > remCells) continue;
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
            // AsteroidPrefab.CellsFor(_ctx, ac, mult) = π × (BaseRadius × mult)² / GrainArea = k × mult²
            // solve k × mult² = remCells → mult = sqrt(remCells / k) where k = AsteroidPrefab.CellsFor(_ctx, ac, 1)
            float kUnit = AsteroidPrefab.CellsFor(_ctx, chosenAc, 1f);
            float maxByCells = MathF.Sqrt(remCells / kUnit);
            float maxMult = Math.Min(chosenAc.SizeRange[1], Math.Min(maxByBudget, maxByCells));
            float minMult0 = chosenAc.SizeRange[0];
            if (maxMult < minMult0) break;

            float u = (float)_rng.NextDouble();
            float sizeMult = minMult0 + MathF.Pow(u, alpha) * (maxMult - minMult0);

            result.Add((chosenKey, sizeMult));
            remBudget -= chosenAc.BaseCost * sizeMult;
            remCells  -= AsteroidPrefab.CellsFor(_ctx, chosenAc, sizeMult);
        }
        return result;
    }

    private void SpawnNextWave()
    {
        _waveNumber++;
        var ws = _gc.WaveSystem;

        float gInterval = MathF.Max(1f, ws.GrowthIntervalSeconds);
        int intervals   = (int)(_gameTime / gInterval);
        int budget      = ws.BaseBudget + intervals * ws.BudgetGrowthPerInterval;
        _maxLiveCells   = Math.Min(ws.BaseCellCap + intervals * ws.CellCapGrowthAmount, ws.MaxCellCap);

        float rampT   = ws.SizeBiasRampEnd > 0f ? MathF.Min(1f, _gameTime / ws.SizeBiasRampEnd) : 1f;
        float sizeBias = ws.SizeBiasStart + rampT * (ws.SizeBiasEnd - ws.SizeBiasStart);

        // Evaluate time-parametric weights; split into asteroid vs alien buckets.
        var asteroidBias = new Dictionary<string, float>();
        var alienBias    = new Dictionary<string, float>();
        foreach (var (key, entry) in ws.SpawnBias)
        {
            float w = EvalSpawnWeight(entry, _gameTime);
            if (w <= 0f) continue;
            if (_gc.Asteroids.ContainsKey(key))     asteroidBias[key] = w;
            else if (_gc.Entities.ContainsKey(key)) alienBias[key]    = w;
        }

        int liveCells = CountLiveCells();
        float cellCap = MathF.Max(0, _maxLiveCells - liveCells);
        var spawns = cellCap >= 3f
            ? ChooseAsteroids(budget, asteroidBias, sizeBias, cellCap)
            : new List<(string Key, float SizeMult)>();

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

        // Pick one alien type weighted by alienBias and spawn 1-3.
        var alienSpawns = new List<string>();
        if (alienBias.Count > 0)
        {
            float total = alienBias.Values.Sum();
            float pick  = (float)_rng.NextDouble() * total;
            float cum   = 0f;
            string chosen = alienBias.Keys.First();
            foreach (var (k, w) in alienBias) { cum += w; if (pick <= cum) { chosen = k; break; } }
            if (_gc.Entities.TryGetValue(chosen, out var ecfg))
            {
                int alienCount = Math.Clamp((int)(budget / MathF.Max(1f, ecfg.BaseCost)), 1, 3);
                for (int i = 0; i < alienCount; i++) alienSpawns.Add(chosen);
            }
        }
        foreach (var key in alienSpawns)
        {
            Vector2 pos = FindSpawnPosition(50f, placed, playerPos);
            placed.Add((pos, 50f));
            SpawnAlien(pos, key);
        }

        LogAndBannerWave(spawns, alienSpawns, budget, sizeBias);
    }

    private static float EvalSpawnWeight(SpawnBiasEntry e, float t)
    {
        if (t <= e.T0) return e.W0;
        if (t >= e.T1) return e.W1;
        float f = (t - e.T0) / (e.T1 - e.T0);
        return e.W0 + f * (e.W1 - e.W0);
    }

    private void SpawnAlien(Vector2 pos, string typeKey)
        => AlienPrefab.Spawn(_world, _ctx, _rng, pos, typeKey);

    private void LogAndBannerWave(
        List<(string Key, float SizeMult)> spawns, List<string> alienSpawns,
        int budget, float sizeBias)
    {
        var typeCount = new Dictionary<string, List<float>>();
        int totalCells = 0;
        foreach (var (key, sm) in spawns)
        {
            if (!typeCount.TryGetValue(key, out var lst)) typeCount[key] = lst = new List<float>();
            lst.Add(sm);
            if (_gc.Asteroids.TryGetValue(key, out var ac))
                totalCells += (int)AsteroidPrefab.CellsFor(_ctx, ac, sm);
        }

        string alienStr = alienSpawns.Count > 0
            ? "   " + string.Join(" ", alienSpawns.GroupBy(k => k).Select(g => $"{g.Count()}× {g.Key}"))
            : "";

        _bannerLine1 = $"WAVE {_waveNumber}   T={_gameTime:0.0}s";
        _bannerLine2 = $"budget={budget}   sizeBias={sizeBias:+0.0;-0.0;0.0}   {spawns.Count} asteroids   ~{totalCells} cells";
        var typeTokens = typeCount.Select(kv => $"{kv.Value.Count}× {kv.Key}");
        _bannerLine3 = string.Join("   ", typeTokens) + alienStr;
        _bannerTimer = 4f;

        if (_waveLog == null) return;
        _waveLog.WriteLine();
        _waveLog.WriteLine($"[Wave {_waveNumber}] T={_gameTime:0.0}s   budget={budget}   sizeBias={sizeBias:+0.00;-0.00;0.00}   maxCells={_maxLiveCells}");
        _waveLog.WriteLine($"  {spawns.Count} asteroids   ~{totalCells} cells total{(alienSpawns.Count > 0 ? $"   {alienSpawns.Count} aliens" : "")}");
        foreach (var (key, mults) in typeCount)
        {
            _gc.Asteroids.TryGetValue(key, out var ac);
            int cellsEst = ac != null ? mults.Sum(m => (int)AsteroidPrefab.CellsFor(_ctx, ac, m)) : 0;
            string sizeStr = string.Join(" ", mults.Select(m => $"{m:0.00}"));
            string cellStr = ac != null
                ? string.Join(" ", mults.Select(m => $"~{(int)AsteroidPrefab.CellsFor(_ctx, ac, m)}")) : "?";
            _waveLog.WriteLine($"  {key,-10} x{mults.Count}   sizes: {sizeStr}   cells: {cellStr}   subtotal≈{cellsEst}");
        }
        foreach (var key in alienSpawns.Distinct())
            _waveLog.WriteLine($"  {key,-10} ×{alienSpawns.Count(k => k == key)} (alien)");
    }

    // Border strip width: asteroids spawn within this distance of the world edge.
    private const float BorderZone = 400f;

    // Extra margin beyond the screen edge where spawns are suppressed (so rocks don't pop in).
    private const float ViewMargin = 100f;

    private Vector2 FindSpawnPosition(float radius, List<(Vector2 pos, float r)> placed, Vector2 playerPos)
    {
        float playerClear = radius + GameConst.PlayerRadius + 150f;
        Vector2 camOff = _camera.WorldToScreen(Vector2.Zero);

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

    // Spawns a typed asteroid. sizeMult comes from ChooseAsteroids and has already been
    // sampled from ac.SizeRange, so we use it directly rather than re-sampling.
    private void SpawnAsteroid(Vector2 pos, string typeKey, float sizeMult)
    {
        var e = AsteroidPrefab.Spawn(_world, _ctx, _rng, pos, typeKey, sizeMult);
        // AsteroidTypeKey drives the demo's live material-tuning sync (see Update/SyncMaterial).
        if (_world.IsAlive(e)) _world.AddComponent(e, new AsteroidTypeKey { Key = typeKey });
    }
}

// =============================================================================
// Renderer
// =============================================================================

sealed class DemoRenderer
{
    private readonly int _w, _h;
    private readonly WorldRenderer _worldRenderer = new();   // shared playfield rendering
    private static readonly Color Bg = new(8, 9, 14);
    private static readonly FontSpec Hud     = new("monospace", 14f);
    private static readonly FontSpec Panel   = new("monospace", 13f);
    private static readonly FontSpec Banner1 = new("monospace", 26f);
    private static readonly FontSpec Banner2 = new("monospace", 16f);
    private static readonly FontSpec Banner3 = new("monospace", 13f);

    public DemoRenderer(int w, int h) { _w = w; _h = h; }

    public void Draw(IRenderer r, DemoSession session, AsteroidsGame.Config.GameConfig gc, float alpha, float fps)
    {
        var world = session.World;
        r.Begin(Bg);
        _worldRenderer.Draw(r, world, session.Camera, session.Fx, session.Player, gc.Vfx, alpha);

        // HUD — top bar.
        r.DrawText($"fps {fps,3:F0}   wave {session.WaveNumber}   asteroids {world.Count<AsteroidTag>()}",
                   new Vector2(12, 10), new Color(190, 200, 220), Hud);

        // HUD — bottom bar: live cell counter + wave timer.
        string waveStatus = session.WavePending
            ? "spawning..."
            : $"next wave in: {MathF.Max(0f, 30f - session.WaveTimer):0.0}s";
        string cellBar = $"cells: {session.LiveCells} / {session.MaxLiveCells}   {waveStatus}";
        r.DrawText(cellBar, new Vector2(12, _h - 42f), new Color(160, 175, 200), Hud);
        r.DrawText("WASD move   mouse aim   click fire   R respawn   L force-log   Esc quit",
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

        r.End();
    }
}
