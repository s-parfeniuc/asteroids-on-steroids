using System.Numerics;
using System.Runtime.InteropServices;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Effects;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Engine.Systems;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.States;

public sealed class PlayingState : IGameState
{
    private readonly GameContext _ctx;

    private readonly World          _world  = new();
    private readonly EventBus       _bus    = new();
    private readonly ParticleSystem _fx     = new();
    private readonly ISystem[]      _systems;
    private readonly Camera         _camera;
    private readonly Random         _rng;

    private Entity _player;
    private int    _nextGroupId = 1;

    // Wave manager state
    private float _gameTime     = 0f;
    private float _waveTimer    = 0f;   // seconds since last wave spawn
    private bool  _pendingWave  = false;
    private float _waveCountdown = 0f;
    private bool  _mothershpSpawned          = false;
    private bool  _mothershpKilled            = false;
    private int   _mothershpGroupId           = 0;
    private int   _mothershpInitialCockpits   = 3;
    private bool  _pendingGameOver            = false;
    private float _bossShockwaveCd            = 0f;
    private float _bossBlackHoleCd            = 0f;
    private float _bossRamChargeCd            = 0f;
    private float _bossRamChargeActive        = 0f;
    private bool  _bossOverdriveTriggered     = false;

    private readonly HashSet<(int, int)> _activeCollisions = new();

    private const double FixedDt = 1.0 / 120.0;

    // ── Spawn position constants ──────────────────────────────────────────────
    private const float BorderZone = 400f;
    private const float ViewMargin = 100f;

    // ── Construction ─────────────────────────────────────────────────────────

    public PlayingState(GameContext ctx, int startWave = 1)
    {
        _ctx = ctx;
        _rng = new Random(ctx.Rng.Next());

        var wc = ctx.Config.World;
        _camera = new Camera(ctx.ScreenW, ctx.ScreenH)
        {
            Position = new Vector2(wc.Width / 2f, wc.Height / 2f),
        };

        var worldCenter = new Vector2(wc.Width / 2f, wc.Height / 2f);

        var pcs = new PlayerControlSystem(ctx, _camera);
        pcs.OnPiercingFire = SpawnPiercingRound;
        _systems =
        [
            new PreviousStateSystem(),
            pcs,
            new AlienAiSystem(ctx, _bus, _rng),
            new PhysicsSystem(),
            new VortexSystem(worldCenter, ctx.Config.Vortex),
            new MovementSystem(),
            new BorderDampSystem(wc.Width, wc.Height),
            new RaycastBulletSystem(_bus, _fx, _rng),
            new GrenadeSystem(_bus),
            new BlackHoleSystem(),
            new GhostSystem(),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            new FractureCrackSystem(_bus, _rng),
            new FractureGroupSystem(),
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
        ];
    }

    // ── IGameState ────────────────────────────────────────────────────────────

    public void Enter()
    {
        _bus.Subscribe<BulletHitEvent>(OnBulletHit);
        _bus.Subscribe<CollisionEvent>(OnCollision);
        _bus.Subscribe<CellPulverizedEvent>(OnCellPulverized);
        _bus.Subscribe<FractureCompletedEvent>(OnFractureCompleted);
        _bus.Subscribe<FractureSplitEvent>(OnFractureSplit);
        _bus.Subscribe<GrenadeDetonateEvent>(OnGrenadeDetonate);

        _ctx.CellBudget.Reset();
        _ctx.Score.Reset();
        _gameTime                 = 0f;
        _waveTimer                = 0f;
        _pendingWave              = false;
        _mothershpSpawned         = false;
        _mothershpKilled          = false;
        _mothershpGroupId         = 0;
        _mothershpInitialCockpits = 3;
        _pendingGameOver          = false;
        _bossShockwaveCd          = 0f;
        _bossBlackHoleCd          = 0f;
        _bossRamChargeCd          = 0f;
        _bossRamChargeActive      = 0f;
        _bossOverdriveTriggered   = false;

        SpawnPlayer();
        SpawnNextWave();
    }

    public void Exit()
    {
        _ctx.CellBudget.Reset();
    }

    public IGameState? Update(double dt)
    {
        // Slow-mo scales game simulation dt; input is polled before Update so key presses
        // still happen at wall-clock rate regardless of the scaled dt.
        double gameDt = dt;
        if (_world.IsAlive(_player) && _world.HasComponent<SkillState>(_player))
        {
            float slowActive = _world.GetComponent<SkillState>(_player).SlowMoActive;
            if (slowActive > 0f && _ctx.Config.Skills.TryGetValue("slowmo", out var smCfg))
                gameDt *= smCfg.TimeScale ?? 0.3;
        }

        foreach (var s in _systems)
        {
            if (s is PlayerControlSystem pcs) pcs.Player = _player;
            s.Update(_world, gameDt);
        }
        _world.FlushDeferred();
        _fx.Update((float)gameDt);

        _ctx.Score.Update(dt, _ctx.Config.Scoring.KillChainDecay);

        // Camera smooth-follow the player, clamped to world bounds.
        if (_world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
        {
            var wc = _ctx.Config.World;
            float hw = _ctx.ScreenW * 0.5f, hh = _ctx.ScreenH * 0.5f;
            Vector2 target = Vector2.Clamp(
                _world.GetComponent<Transform>(_player).Position,
                new Vector2(hw, hh),
                new Vector2(wc.Width - hw, wc.Height - hh));
            float k = 1f - MathF.Exp(-wc.CameraFollowSpeed * (float)dt);
            _camera.Position += (target - _camera.Position) * k;
        }

        // Wave manager
        _gameTime  += (float)dt;
        _waveTimer += (float)dt;

        var ws = _ctx.Config.WaveSystem;
        int liveCells    = CountLiveCells();
        int currentCap   = Math.Min(
            ws.BaseCellCap + (int)(_gameTime / ws.GrowthIntervalSeconds) * ws.CellCapGrowthAmount,
            ws.MaxCellCap);

        bool shouldSpawn = (liveCells <= currentCap * ws.TriggerThreshold && _waveTimer > ws.GracePeriodSeconds)
                        || _waveTimer >= ws.HardTriggerIntervalSeconds;
        if (shouldSpawn && !_pendingWave)
        {
            _pendingWave    = true;
            _waveCountdown  = ws.SpawnDelaySeconds;
        }
        if (_pendingWave)
        {
            _waveCountdown -= (float)dt;
            if (_waveCountdown <= 0f)
            {
                SpawnNextWave();
                _waveTimer   = 0f;
                _pendingWave = false;
            }
        }

        // Mothership spawn (one-shot time gate)
        if (!_mothershpSpawned && _gameTime >= ws.MothershpSpawnTime)
        {
            SpawnMothership();
            _mothershpSpawned = true;
        }
        // Boss skills + win condition
        if (_mothershpSpawned && !_mothershpKilled)
            UpdateBossSkills((float)gameDt);

        if (!_world.IsAlive(_player)) _pendingGameOver = true;
        if (_pendingGameOver) return new GameOverState(_ctx, won: _mothershpKilled);
        return null;
    }

    public void Draw(IRenderer r, float alpha)
    {
        r.Begin(new Color(8, 9, 14));

        r.PushTransform(_camera.GetViewMatrix());
        DrawBodies(r, alpha);   // asteroids + player ship (anything with FracturableBody + BodyColor)
        DrawDebris(r, alpha);
        _fx.Draw(r);
        DrawBullets(r, alpha);
        DrawBlackHoles(r, alpha);
        DrawPlayerAim(r, alpha);
        r.PopTransform();

        DrawHud(r);

        r.End();
    }

    // ── Wave management ───────────────────────────────────────────────────────

    private void SpawnNextWave()
    {
        var ws = _ctx.Config.WaveSystem;
        var wc = _ctx.Config.World;

        int budget = ws.BaseBudget
            + (int)(_gameTime / ws.GrowthIntervalSeconds) * ws.BudgetGrowthPerInterval;
        int currentCap = Math.Min(
            ws.BaseCellCap + (int)(_gameTime / ws.GrowthIntervalSeconds) * ws.CellCapGrowthAmount,
            ws.MaxCellCap);
        float sizeBias = ws.SizeBiasRampEnd > 0f
            ? ws.SizeBiasStart + (ws.SizeBiasEnd - ws.SizeBiasStart)
              * Math.Clamp(_gameTime / ws.SizeBiasRampEnd, 0f, 1f)
            : ws.SizeBiasEnd;

        int liveCells = CountLiveCells();
        float cellCap = MathF.Max(0f, currentCap - liveCells);
        if (cellCap < 3f) return;

        // Evaluate time-parametric bias weights for this moment in the game.
        var asteroidBias = new Dictionary<string, float>();
        var alienBias    = new Dictionary<string, float>();
        foreach (var (key, entry) in ws.SpawnBias)
        {
            float t01 = entry.T1 > entry.T0
                ? Math.Clamp((_gameTime - entry.T0) / (entry.T1 - entry.T0), 0f, 1f)
                : (_gameTime >= entry.T0 ? 1f : 0f);
            float w = entry.W0 + (entry.W1 - entry.W0) * t01;
            if (w <= 0f) continue;
            if (_ctx.Config.Asteroids.ContainsKey(key))  asteroidBias[key] = w;
            else if (_ctx.Config.Entities.ContainsKey(key)) alienBias[key]  = w;
        }

        var asteroidSpawns = ChooseAsteroids(budget, asteroidBias, sizeBias, cellCap);

        // Alien budget allocation: sample one alien per wave from alien bias if any are unlocked.
        var alienSpawns = new List<string>();
        if (alienBias.Count > 0)
        {
            float total = alienBias.Values.Sum();
            float pick  = (float)_rng.NextDouble() * total;
            float cum   = 0f;
            string chosen = alienBias.Keys.First();
            foreach (var (k, w) in alienBias) { cum += w; if (pick <= cum) { chosen = k; break; } }
            if (_ctx.Config.Entities.TryGetValue(chosen, out var ecfg))
            {
                int alienCount = Math.Clamp((int)(budget / MathF.Max(1f, ecfg.BaseCost)), 1, 3);
                for (int i = 0; i < alienCount; i++) alienSpawns.Add(chosen);
            }
        }

        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : new Vector2(wc.Width / 2f, wc.Height / 2f);

        var placed = new List<(Vector2 pos, float r)>();
        foreach (var (key, sizeMult) in asteroidSpawns)
        {
            float r = _ctx.Config.Asteroids.TryGetValue(key, out var ac) && ac.Procedural != null
                ? ac.Procedural.BaseRadius * sizeMult : 80f * sizeMult;
            Vector2 pos = FindSpawnPosition(r, placed, playerPos);
            placed.Add((pos, r));
            SpawnAsteroid(pos, key, sizeMult);
        }
        foreach (var key in alienSpawns)
        {
            Vector2 pos = FindSpawnPosition(80f, placed, playerPos);
            placed.Add((pos, 80f));
            SpawnAlien(pos, key);
        }
    }

    // Stateless budget-packing: selects (type, sizeMult) pairs until budget or cell cap is exhausted.
    private List<(string Key, float SizeMult)> ChooseAsteroids(
        int budget, Dictionary<string, float> bias, float sizeBias, float cellCap)
    {
        var result    = new List<(string, float)>();
        float remBudget = budget;
        float remCells  = cellCap;
        float alpha   = MathF.Pow(2f, -sizeBias);  // 1=uniform, 0.5=large-biased, 2=small-biased

        while (true)
        {
            var candidates = new List<(string key, AsteroidConfig ac, float w)>();
            foreach (var (key, w) in bias)
            {
                if (!_ctx.Config.Asteroids.TryGetValue(key, out var ac) || ac.Procedural == null) continue;
                float minMult = ac.SizeRange[0];
                if (ac.BaseCost * minMult > remBudget) continue;
                if (CellsFor(ac, minMult) > remCells) continue;
                candidates.Add((key, ac, w));
            }
            if (candidates.Count == 0) break;

            float total = candidates.Sum(c => c.w);
            float pick  = (float)_rng.NextDouble() * total;
            float cum   = 0f;
            (string chosenKey, AsteroidConfig chosenAc, float _) = candidates[0];
            foreach (var c in candidates) { cum += c.w; if (pick <= cum) { chosenKey = c.key; chosenAc = c.ac; break; } }

            float maxByBudget = remBudget / chosenAc.BaseCost;
            float kUnit       = CellsFor(chosenAc, 1f);
            float maxByCells  = MathF.Sqrt(remCells / kUnit);
            float maxMult     = Math.Min(chosenAc.SizeRange[1], Math.Min(maxByBudget, maxByCells));
            float minMult0    = chosenAc.SizeRange[0];
            if (maxMult < minMult0) break;

            float u        = (float)_rng.NextDouble();
            float sizeMult = minMult0 + MathF.Pow(u, alpha) * (maxMult - minMult0);

            result.Add((chosenKey, sizeMult));
            remBudget -= chosenAc.BaseCost * sizeMult;
            remCells  -= CellsFor(chosenAc, sizeMult);
        }
        return result;
    }

    private Vector2 FindSpawnPosition(float radius, List<(Vector2 pos, float r)> placed, Vector2 playerPos)
    {
        var wc = _ctx.Config.World;
        float playerClear = radius + 18f + 150f;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            Vector2 pos = _rng.Next(4) switch
            {
                0 => new((float)_rng.NextDouble() * wc.Width, (float)_rng.NextDouble() * BorderZone),
                1 => new((float)_rng.NextDouble() * wc.Width, wc.Height - (float)_rng.NextDouble() * BorderZone),
                2 => new((float)_rng.NextDouble() * BorderZone, (float)_rng.NextDouble() * wc.Height),
                _ => new(wc.Width - (float)_rng.NextDouble() * BorderZone, (float)_rng.NextDouble() * wc.Height),
            };

            // Reject if inside the visible viewport.
            Vector2 sp = _camera.WorldToScreen(pos);
            if (sp.X > -ViewMargin && sp.X < _ctx.ScreenW + ViewMargin &&
                sp.Y > -ViewMargin && sp.Y < _ctx.ScreenH + ViewMargin) continue;

            if ((pos - playerPos).LengthSquared() < playerClear * playerClear) continue;

            bool clear = true;
            foreach (var (p, r) in placed)
            {
                float minDist = radius + r + 20f;
                if ((pos - p).LengthSquared() < minDist * minDist) { clear = false; break; }
            }
            if (clear) return pos;
        }

        // Fallback: top border strip.
        return new Vector2(
            (float)_rng.NextDouble() * _ctx.Config.World.Width,
            (float)_rng.NextDouble() * BorderZone);
    }

    // Spawns a typed asteroid from GameConfig.Asteroids using the procedural pipeline.
    private void SpawnAsteroid(Vector2 pos, string typeKey, float sizeMult)
    {
        if (!_ctx.Config.Asteroids.TryGetValue(typeKey, out var ac) || ac.Procedural == null) return;
        if (!_ctx.Config.Materials.TryGetValue(ac.Material, out var mc))
            mc = _ctx.Config.Materials.Values.First();

        var mat = new FractureProperties
        {
            Toughness            = mc.Toughness,
            Brittleness          = mc.Brittleness,
            CrackDirectionality  = mc.CrackDirectionality,
            GrainArea            = mc.GrainArea,
            MinFragmentArea      = mc.MinFragmentArea,
            Density              = mc.Density * ac.DensityMult,
            KineticFraction      = mc.KineticFraction,
            SurfaceEfficiency    = mc.SurfaceEfficiency,
            SpinPreStress        = mc.SpinPreStress,
            CrackSpeed           = mc.CrackSpeed,
            DetachCellScale      = mc.DetachCellScale,
            DetachCellJitter     = mc.DetachCellJitter,
        };

        var body = BuildProceduralAsteroid(ac.Procedural, sizeMult, mat);

        var wc = _ctx.Config.World;
        Vector2 worldCenter = new(wc.Width / 2f, wc.Height / 2f);
        Vector2 toCenter    = worldCenter - pos;
        float   baseAngle   = MathF.Atan2(toCenter.Y, toCenter.X);
        float   spread      = ((float)_rng.NextDouble() * 2f - 1f) * (MathF.PI / 4f);
        float   speed       = ac.SpeedRange[0] + (float)_rng.NextDouble() * (ac.SpeedRange[1] - ac.SpeedRange[0]);
        var vel = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread)) * speed;

        float spinMag = ac.SpinRange[0] + (float)_rng.NextDouble() * (ac.SpinRange[1] - ac.SpinRange[0]);
        float spin    = spinMag * (_rng.NextDouble() > 0.5 ? 1f : -1f);

        var e = SpawnFracturableBody(body, pos, (float)(_rng.NextDouble() * MathF.Tau), vel, spin,
                    MaterialColor(ac.Material));
        _world.AddComponent(e, new AsteroidTag());
        _world.AddComponent(e, new AsteroidVariant { Key = typeKey });
        _ctx.CellBudget.Add(body.Cells.Length);
    }

    private void SpawnAlien(Vector2 pos, string typeKey)
    {
        if (!_ctx.Config.Entities.TryGetValue(typeKey, out var ec)) return;
        if (!_ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return;
        if (!_ctx.Config.Materials.TryGetValue(ec.Material, out var mc))
            mc = _ctx.Config.Materials.Values.First();
        var mat = mc.ToFractureProperties();

        float sc      = ec.ShapeScale;
        var outline   = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos   = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMult  = sd.Seeds.Select(s => s.BondMult).ToList();
        var body      = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, _rng);
        ApplyShapeSeeds(body, sd.Seeds, sc);

        var wc = _ctx.Config.World;
        Vector2 worldCenter = new(wc.Width / 2f, wc.Height / 2f);
        Vector2 toCenter    = worldCenter - pos;
        float baseAngle     = MathF.Atan2(toCenter.Y, toCenter.X);
        float spread        = ((float)_rng.NextDouble() * 2f - 1f) * (MathF.PI / 6f);
        float speed         = ec.Speed * (0.7f + 0.6f * (float)_rng.NextDouble());
        Vector2 vel         = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread)) * speed;

        var color = new BodyColor { Fill = new Color(80, 50, 120), Outline = new Color(160, 100, 220) };
        var e     = SpawnFracturableBody(body, pos, (float)(_rng.NextDouble() * MathF.Tau), vel, 0f, color);
        _world.AddComponent(e, new AlienTag());
        _world.AddComponent(e, new AlienVariant { Key = typeKey });
        _world.AddComponent(e, new ShootCooldown { Remaining = (float)_rng.NextDouble() * ec.ShootCooldown });
        // Set alien layer so player bullets can target aliens but not other asteroids for aliens.
        if (_world.HasComponent<Collider>(e))
        {
            ref var col = ref _world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
        _ctx.CellBudget.Add(body.Cells.Length);
    }

    private void SpawnMothership()
    {
        if (!_ctx.Config.Entities.TryGetValue("mothership", out var ec)) return;
        if (!_ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return;
        if (!_ctx.Config.Materials.TryGetValue(ec.Material, out var mc))
            mc = _ctx.Config.Materials.Values.First();
        var mat = mc.ToFractureProperties();

        float sc    = ec.ShapeScale;
        var outline = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMlt = sd.Seeds.Select(s => s.BondMult).ToList();
        var body    = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMlt, mat, _rng);
        ApplyShapeSeeds(body, sd.Seeds, sc);

        _mothershpInitialCockpits = Math.Max(1, sd.Seeds.Count(s => s.Role == "cockpit"));

        var wc = _ctx.Config.World;
        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : new Vector2(wc.Width / 2f, wc.Height / 2f);
        Vector2 pos     = FindSpawnPosition(220f, new List<(Vector2, float)>(), playerPos);
        Vector2 center  = new(wc.Width / 2f, wc.Height / 2f);
        Vector2 dir     = center - pos;
        float   len     = dir.Length();
        Vector2 vel     = len > 1f ? dir / len * ec.Speed : Vector2.Zero;

        var color = new BodyColor { Fill = new Color(90, 30, 130), Outline = new Color(180, 60, 240) };
        var e = SpawnFracturableBody(body, pos, (float)(_rng.NextDouble() * MathF.Tau), vel, 0f, color);

        _mothershpGroupId = _nextGroupId++;
        _world.AddComponent(e, new AlienTag());
        _world.AddComponent(e, new AlienVariant { Key = "mothership" });
        _world.AddComponent(e, new ShootCooldown { Remaining = 999f });
        _world.AddComponent(e, new MothershpId  { Id = _mothershpGroupId, InitialCockpitCount = _mothershpInitialCockpits });
        _world.AddComponent(e, new SpawnerAccumulator { Value = 0f });
        if (_world.HasComponent<Collider>(e))
        {
            ref var col = ref _world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
        _ctx.CellBudget.Add(body.Cells.Length);

        if (ec.Boss is { } bc)
        {
            _bossShockwaveCd        = bc.ShockwaveCooldown;
            _bossBlackHoleCd        = bc.BlackHoleCooldown * 0.4f;
            _bossRamChargeCd        = bc.RamChargeCooldown * 0.7f;
            _bossRamChargeActive    = 0f;
            _bossOverdriveTriggered = false;
        }
    }

    // ── Procedural asteroid construction ──────────────────────────────────────

    private FracturableBody BuildProceduralAsteroid(
        ProceduralAsteroidConfig proc, float sizeMult, in FractureProperties mat)
    {
        int sides  = _rng.Next(proc.VertexCount[0], proc.VertexCount[1] + 1);
        float radius = proc.BaseRadius * sizeMult;
        var (convex, _) = PolygonUtils.GenerateConvex(sides, radius, _rng);

        if (proc.Roughness > 0.01f)
        {
            float freq  = MathF.Max(1f, proc.NoiseFrequency);
            float phase = (float)(_rng.NextDouble() * MathF.Tau);
            for (int i = 0; i < convex.Length; i++)
            {
                float θ = MathF.Atan2(convex[i].Y, convex[i].X);
                float n = MathF.Sin(θ * freq + phase) * 0.6f
                        + MathF.Sin(θ * freq * 2.1f + phase * 1.4f) * 0.4f;
                if (n > 0f) convex[i] *= 1f + proc.Roughness * n;
            }
        }

        float maxR = 0f;
        foreach (var v in convex) maxR = MathF.Max(maxR, v.Length());
        if (maxR < 1f) maxR = 1f;

        int seedCount = Math.Clamp((int)(MathF.PI * radius * radius / mat.GrainArea), 4, 600);
        var seeds     = ScatterSeedsProc(convex, seedCount, proc.SeedClusterCenter);

        var bondMults = new float[seeds.Count];
        for (int i = 0; i < seeds.Count; i++)
            bondMults[i] = EvalProcDist(proc.BondMultDistribution, seeds[i], maxR);

        float concavity = proc.ConcavityBias;
        Func<Vector2, bool>? membership = concavity > 0.01f
            ? s =>
              {
                  float r = s.Length() / maxR;
                  return _rng.NextDouble() > concavity * (r * r);
              }
            : (Func<Vector2, bool>?)null;

        var body = VoronoiTessellator.BuildWithSeeds(convex, seeds, bondMults, membership, mat, _rng);

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

    private static BodyColor MaterialColor(string materialKey) => materialKey switch
    {
        "ice"   => new BodyColor { Fill = new Color(70, 115, 155),  Outline = new Color(140, 195, 230) },
        "metal" => new BodyColor { Fill = new Color(50, 60, 75),    Outline = new Color(115, 135, 165) },
        "glass" => new BodyColor { Fill = new Color(60, 120, 95),   Outline = new Color(110, 195, 155) },
        _       => new BodyColor { Fill = new Color(64, 58, 52),    Outline = new Color(150, 138, 120) },
    };

    // ── Player spawning ───────────────────────────────────────────────────────

    private void SpawnPlayer()
    {
        var wc  = _ctx.Config.World;
        var pos = new Vector2(wc.Width / 2f, wc.Height / 2f);
        _player = _world.CreateEntity();
        _world.AddComponent(_player, new Transform { Position = pos, PreviousPosition = pos });
        _world.AddComponent(_player, new Velocity());
        _world.AddComponent(_player, new AimComponent { Dir = -Vector2.UnitY });
        _world.AddComponent(_player, new WeaponCooldowns());
        _world.AddComponent(_player, new ActiveWeapon { Key = _ctx.Config.Player.StartingWeapon });
        _world.AddComponent(_player, new SkillState());
        _world.AddComponent(_player, new PlayerTag());

        var pc = _ctx.Config.Player;
        if (_ctx.Shapes.TryGetValue(pc.Shape, out var sd) && sd.Seeds.Length >= 1 && sd.Outline.Length >= 3)
        {
            BuildAndAttachPlayerBody(sd);
        }
        else
        {
            _world.AddComponent(_player, new RigidBody
            {
                Mass = 12f, Inertia = 0f, LinearDrag = 1.2f, AngularDrag = 2f,
                Restitution = 0.2f, Friction = 0.1f,
            });
            _world.AddComponent(_player, new Collider
            {
                Shape = new CircleShape(18f),
                Layer = GameLayers.Player,
                Mask  = GameLayers.Asteroid | GameLayers.Alien,
            });
        }
    }

    private void BuildAndAttachPlayerBody(ShapeData sd)
    {
        var pc = _ctx.Config.Player;
        if (!_ctx.Config.Materials.TryGetValue(pc.Material, out var mc))
            mc = _ctx.Config.Materials.Values.First();
        var mat = mc.ToFractureProperties();

        float sc = pc.ShapeScale;
        var outline  = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos  = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMult = sd.Seeds.Select(s => s.BondMult).ToList();

        var body = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMult, mat, _rng);
        ApplyShapeSeeds(body, sd.Seeds, sc);

        float area    = VoronoiTessellator.TotalArea(body);
        float mass    = MathF.Max(1f, mat.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);

        var color = new BodyColor { Fill = new Color(60, 120, 200), Outline = new Color(140, 190, 255) };
        _world.AddComponent(_player, new RigidBody
        {
            Mass = mass, Inertia = inertia, LinearDrag = 1.2f, AngularDrag = 2f,
            Restitution = 0.2f, Friction = 0.1f,
        });
        _world.AddComponent(_player, new Collider
        {
            Shape = VoronoiTessellator.BuildShape(body),
            Layer = GameLayers.Player,
            Mask  = GameLayers.Asteroid | GameLayers.Alien,
        });
        _world.AddComponent(_player, body);
        _world.AddComponent(_player, color);
        var (edgeOut, edgeCr) = ComputeEdges(body.Cells, body.Bonds);
        _world.AddComponent(_player, new RenderOutline { Outline = edgeOut, Cracks = edgeCr });
    }

    // Matches each Voronoi cell to the nearest seed (by centroid proximity) and
    // applies that seed's role, densityMult, and blastResist to the cell.
    private static void ApplyShapeSeeds(FracturableBody body, SeedData[] seeds, float scale)
    {
        for (int ci = 0; ci < body.Cells.Length; ci++)
        {
            float bestSq = float.MaxValue;
            int   bestI  = 0;
            for (int si = 0; si < seeds.Length; si++)
            {
                var sp = new Vector2(seeds[si].X * scale, seeds[si].Y * scale);
                float dsq = (body.Cells[ci].Centroid - sp).LengthSquared();
                if (dsq < bestSq) { bestSq = dsq; bestI = si; }
            }
            body.Cells[ci].Role        = seeds[bestI].Role;
            body.Cells[ci].DensityMult = seeds[bestI].DensityMult;
            body.Cells[ci].BlastResist = seeds[bestI].BlastResist;
        }
    }

    // ── Generic body spawning ─────────────────────────────────────────────────

    private Entity SpawnFracturableBody(
        FracturableBody body, Vector2 pos, float rot,
        Vector2 vel, float spin, BodyColor color, bool ghost = false)
    {
        float area    = VoronoiTessellator.TotalArea(body);
        float mass    = MathF.Max(1f, body.Material.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);

        var e = _world.CreateEntity();
        _world.AddComponent(e, new Transform
            { Position = pos, Rotation = rot, PreviousPosition = pos, PreviousRotation = rot });
        _world.AddComponent(e, new Velocity { Linear = vel, Angular = spin });
        _world.AddComponent(e, new RigidBody
        {
            Mass        = mass,
            Inertia     = inertia,
            LinearDrag  = 0.05f,
            AngularDrag = 0.08f,
            Restitution = 0.4f,
            Friction    = 0.3f,
        });
        _world.AddComponent(e, new Collider
        {
            Shape = VoronoiTessellator.BuildShape(body),
            Layer = ghost ? GameLayers.Ghost : GameLayers.Asteroid,
            Mask  = ghost ? 0 : (GameLayers.Asteroid | GameLayers.Player),
        });
        _world.AddComponent(e, body);
        _world.AddComponent(e, color);
        var (outline, cracks) = ComputeEdges(body.Cells, body.Bonds);
        _world.AddComponent(e, new RenderOutline { Outline = outline, Cracks = cracks });
        if (ghost)
            _world.AddComponent(e, new FractureGhost { Remaining = 0.04f });
        return e;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int CountLiveCells()
    {
        int total = 0;
        _world.ForEach<AsteroidTag, FracturableBody>(
            (Entity _, ref AsteroidTag _, ref FracturableBody fb) => total += fb.Cells.Length);
        return total;
    }

    private float CellsFor(AsteroidConfig ac, float sizeMult)
    {
        if (ac.Procedural == null) return 1f;
        if (!_ctx.Config.Materials.TryGetValue(ac.Material, out var mc)) return 1f;
        float r = ac.Procedural.BaseRadius * sizeMult;
        return MathF.Max(1f, MathF.PI * r * r / mc.GrainArea);
    }

    private FractureProperties GetMaterial(string key)
    {
        if (_ctx.Config.Materials.TryGetValue(key, out var m)) return m.ToFractureProperties();
        return FractureProperties.Rock;
    }

    private BodyColor GetBodyColor(Entity e) =>
        _world.IsAlive(e) && _world.HasComponent<BodyColor>(e)
            ? _world.GetComponent<BodyColor>(e)
            : new BodyColor { Fill = new Color(64, 58, 52), Outline = new Color(150, 138, 120) };

    private FractureGroup MakeFractureGroup(Entity source)
    {
        int id = _world.IsAlive(source) && _world.HasComponent<FractureGroup>(source)
            ? _world.GetComponent<FractureGroup>(source).Id
            : _nextGroupId++;
        _world.ForEach<FractureGroup>((Entity _, ref FractureGroup fg) =>
        {
            if (fg.Id == id) fg.FramesLeft = 16;
        });
        return new FractureGroup { Id = id, FramesLeft = 16 };
    }

    private (AimComponent aim, WeaponCooldowns wc, ActiveWeapon wep, SkillState sk)
        SavePlayerState(Entity e)
    {
        var aim = _world.HasComponent<AimComponent>(e)      ? _world.GetComponent<AimComponent>(e)      : new AimComponent { Dir = -Vector2.UnitY };
        var wc  = _world.HasComponent<WeaponCooldowns>(e)   ? _world.GetComponent<WeaponCooldowns>(e)   : default;
        var wep = _world.HasComponent<ActiveWeapon>(e)      ? _world.GetComponent<ActiveWeapon>(e)      : new ActiveWeapon { Key = _ctx.Config.Player.StartingWeapon };
        var sk  = _world.HasComponent<SkillState>(e)        ? _world.GetComponent<SkillState>(e)        : default;
        return (aim, wc, wep, sk);
    }

    private void TransferPlayerToFragment(Entity ne, in FragmentSpec f,
        AimComponent aim, WeaponCooldowns wc, ActiveWeapon wep, SkillState sk)
    {
        _world.AddComponent(ne, new PlayerTag());
        _world.AddComponent(ne, aim);
        _world.AddComponent(ne, wc);
        _world.AddComponent(ne, new ActiveWeapon { Key = wep.Key ?? _ctx.Config.Player.StartingWeapon });
        _world.AddComponent(ne, sk);
        _player = ne;

        // Death condition 2: cockpit fragment has no functional cells.
        bool hasFunc = f.Body.Cells.Any(c =>
            c.Role is "cannon" or "shotgun" or "piercing" or "grenade" or "propeller");
        if (!hasFunc) _pendingGameOver = true;
    }

    private void CopyTags(Entity source, Entity target)
    {
        if (!_world.IsAlive(source)) return;
        if (_world.HasComponent<AsteroidTag>(source))
        {
            _world.AddComponent(target, new AsteroidTag());
            if (_world.HasComponent<AsteroidVariant>(source))
                _world.AddComponent(target, _world.GetComponent<AsteroidVariant>(source));
        }
        else if (_world.HasComponent<AlienTag>(source))
        {
            _world.AddComponent(target, new AlienTag());
            if (_world.HasComponent<AlienVariant>(source))
                _world.AddComponent(target, _world.GetComponent<AlienVariant>(source));
        }
    }

    // ── Fracture event handlers ───────────────────────────────────────────────

    private void OnGrenadeDetonate(GrenadeDetonateEvent ev)
    {
        if (_world.IsAlive(ev.Grenade)) _world.DestroyEntity(ev.Grenade);
        if (!_ctx.Config.Weapons.TryGetValue(ev.WeaponKey, out var wcfg)) return;

        int   count  = wcfg.ShrapnelCount ?? 12;
        float spread = (wcfg.ShrapnelSpread ?? 360f) * MathF.PI / 180f;
        float step   = count > 1 ? spread / count : 0f;
        float start  = (float)(_rng.NextDouble() * MathF.Tau);
        float speed  = wcfg.ProjectileSpeed;
        float energy = (wcfg.EnergyPerRay ?? wcfg.Energy) / MathF.Max(1f, count);

        EmitFlash(ev.WorldPos, energy * count);

        for (int i = 0; i < count; i++)
        {
            float angle = start + step * i;
            Vector2 dir = new(MathF.Cos(angle), MathF.Sin(angle));
            var b = _world.CreateEntity();
            _world.AddComponent(b, new Transform { Position = ev.WorldPos, PreviousPosition = ev.WorldPos });
            _world.AddComponent(b, new Velocity { Linear = dir * speed });
            _world.AddComponent(b, new BulletTag());
            _world.AddComponent(b, new BulletVisual { Color = new Color(255, 160, 50) });
            _world.AddComponent(b, new BulletData { WeaponKey = ev.WeaponKey, Energy = energy });
            _world.AddComponent(b, new TimeToLive { Remaining = wcfg.TimeToLive });
        }
    }

    private void SpawnPiercingRound(Vector2 from, Vector2 aimDir)
    {
        if (!_ctx.Config.Weapons.TryGetValue("piercing", out var wcfg)) return;
        if (!_ctx.Shapes.TryGetValue("piercing_round", out var sd)) return;
        if (!_ctx.Config.Materials.TryGetValue("metal", out var mc)) mc = _ctx.Config.Materials.Values.First();
        var mat = mc.ToFractureProperties();

        // Build the piercing round body aligned to aim direction (rotate 90° since shape points up).
        var outline = sd.Outline.Select(xy => new Vector2(xy[0], xy[1])).ToList();
        var seedPos = sd.Seeds.Select(s => new Vector2(s.X, s.Y)).ToList();
        var seedMlt = sd.Seeds.Select(s => s.BondMult).ToList();
        var body    = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMlt, mat, _rng);

        float rot   = MathF.Atan2(aimDir.Y, aimDir.X) + MathF.PI * 0.5f;
        Vector2 pos = from + aimDir * 40f;
        float speed = wcfg.ProjectileSpeed;
        float clamp = wcfg.LateralImpulseClamp ?? 0.4f;

        var e = SpawnFracturableBody(body, pos, rot, aimDir * speed, 0f,
            new BodyColor { Fill = new Color(180, 200, 230), Outline = new Color(230, 240, 255) });
        _world.AddComponent(e, new PiercingRoundTag { Direction = aimDir, LateralClamp = clamp });
        _world.AddComponent(e, new TimeToLive { Remaining = wcfg.TimeToLive });
        // Use Bullet layer so it only collides with asteroids and aliens (not the player).
        if (_world.HasComponent<Collider>(e))
        {
            ref var col = ref _world.GetComponent<Collider>(e);
            col.Layer = GameLayers.Bullet;
            col.Mask  = GameLayers.Asteroid | GameLayers.Alien;
        }
    }

    private bool IsDashInvincible() =>
        _world.IsAlive(_player) && _world.HasComponent<SkillState>(_player)
        && _world.GetComponent<SkillState>(_player).DashActive > 0f;

    private void OnBulletHit(BulletHitEvent ev)
    {
        if (!_world.IsAlive(ev.Target) || !_world.IsAlive(ev.Bullet)) return;
        Vector2 bulletVel = _world.GetComponent<Velocity>(ev.Bullet).Linear;
        _world.DestroyEntity(ev.Bullet);

        // Dash invincibility — bullet still consumed but no fracture applied to player.
        if (ev.Target == _player && IsDashInvincible()) return;

        bool hasData  = _world.HasComponent<BulletData>(ev.Bullet);
        string weaponKey = hasData ? _world.GetComponent<BulletData>(ev.Bullet).WeaponKey
                                   : _ctx.Config.Player.StartingWeapon;
        float storedEnergy = hasData ? _world.GetComponent<BulletData>(ev.Bullet).Energy : 0f;

        // Grenade hits detonate instead of fracturing directly.
        if (hasData && _world.HasComponent<GrenadeFuse>(ev.Bullet))
        {
            var fuse = _world.GetComponent<GrenadeFuse>(ev.Bullet);
            _bus.Publish(new GrenadeDetonateEvent(ev.Bullet, ev.Point, fuse.WeaponKey));
            return;
        }

        if (!_ctx.Config.Weapons.TryGetValue(weaponKey, out var weaponCfg)) return;
        WeaponProfile profile = weaponCfg.ToWeaponProfile();
        // Use stored per-bullet energy (EnergyPerRay for shotgun, Energy for cannon).
        float effectiveEnergy = storedEnergy > 0f ? storedEnergy : weaponCfg.Energy;
        float bulletMass = effectiveEnergy / MathF.Max(1f, bulletVel.LengthSquared()) * 2f;

        EmitFlash(ev.Point, 0.5f * bulletMass * bulletVel.LengthSquared());

        // Scale fracture energy by target's impact coefficient.
        float adjMass = bulletMass;
        if (ev.Target == _player)
            adjMass = bulletMass * _ctx.Config.Player.PlayerImpactCoeff;
        else if (_world.HasComponent<AlienVariant>(ev.Target))
        {
            string varKey = _world.GetComponent<AlienVariant>(ev.Target).Key;
            float coeff = _ctx.Config.Entities.TryGetValue(varKey, out var ecfg)
                ? ecfg.AlienImpactCoeff : 1f;
            adjMass = bulletMass * coeff;
        }

        FractureService.BeginFracture(
            _world, ev.Target, ev.StruckCell,
            ev.Point, ev.ShotDir, bulletVel, adjMass,
            profile, FractureTiming.Default, _rng);
    }

    private const float CollisionFractureThreshold = 20f;

    private void OnCollision(CollisionEvent ev)
    {
        Entity eA = ev.EntityA, eB = ev.EntityB;
        if (!_world.IsAlive(eA) || !_world.IsAlive(eB)) return;
        bool aIsBody = _world.HasComponent<FracturableBody>(eA);
        bool bIsBody = _world.HasComponent<FracturableBody>(eB);
        if (!aIsBody || !bIsBody) return;

        // Piercing round: use weapon profile for the target, lateral-clamp the round.
        Entity piercing = _world.HasComponent<PiercingRoundTag>(eA) ? eA
                        : _world.HasComponent<PiercingRoundTag>(eB) ? eB
                        : default;
        if (_world.IsAlive(piercing))
        {
            Entity target = piercing == eA ? eB : eA;
            if (_world.IsAlive(target) && _world.HasComponent<FracturableBody>(target))
            {
                var pt = _world.GetComponent<PiercingRoundTag>(piercing);
                if (!_ctx.Config.Weapons.TryGetValue("piercing", out var pcfg)) return;
                WeaponProfile pProfile = pcfg.ToWeaponProfile();
                float pMass = pcfg.Mass ?? 3f;
                Vector2 pVel = _world.HasComponent<Velocity>(piercing)
                    ? _world.GetComponent<Velocity>(piercing).Linear : Vector2.Zero;
                float adjMass = target == _player ? pMass * _ctx.Config.Player.PlayerImpactCoeff : pMass;
                FractureService.BeginFracture(_world, target, -1, ev.Contact.ContactPoint,
                    pt.Direction, pVel, adjMass, pProfile, FractureTiming.Default, _rng);
                // Clamp lateral velocity on the round to keep it on-axis.
                if (_world.HasComponent<Velocity>(piercing))
                {
                    ref var pv = ref _world.GetComponent<Velocity>(piercing);
                    float fwdComp = Vector2.Dot(pv.Linear, pt.Direction);
                    Vector2 lat = pv.Linear - pt.Direction * fwdComp;
                    float latLen = lat.Length();
                    float maxLat = pt.LateralClamp * MathF.Abs(fwdComp);
                    if (latLen > maxLat && latLen > 1e-4f)
                        pv.Linear = pt.Direction * fwdComp + lat / latLen * maxLat;
                }
            }
            return;
        }

        var pair = eA.Id < eB.Id ? (eA.Id, eB.Id) : (eB.Id, eA.Id);
        if (_activeCollisions.Contains(pair)) return;

        ref var vA = ref _world.GetComponent<Velocity>(eA);
        ref var vB = ref _world.GetComponent<Velocity>(eB);
        ref var tA = ref _world.GetComponent<Transform>(eA);
        ref var tB = ref _world.GetComponent<Transform>(eB);
        Vector2 cp = ev.Contact.ContactPoint;
        Vector2 rA = cp - tA.Position, rB = cp - tB.Position;
        Vector2 vcA = vA.Linear + new Vector2(-vA.Angular * rA.Y, vA.Angular * rA.X);
        Vector2 vcB = vB.Linear + new Vector2(-vB.Angular * rB.Y, vB.Angular * rB.X);
        Vector2 vRel = vcB - vcA;
        float approach = -Vector2.Dot(vRel, ev.Contact.Normal);
        if (approach < CollisionFractureThreshold) return;

        _activeCollisions.Add(pair);

        float mA = _world.GetComponent<RigidBody>(eA).Mass;
        float mB = _world.GetComponent<RigidBody>(eB).Mass;
        var weapon = new WeaponProfile
        {
            Directionality   = 0.3f,
            MomentumTransfer = 0f,
            EjectFraction    = 0.08f,
            ImpactSpin       = 0.3f,
            BlastFraction    = 0.05f,
            EnergyScale      = 0.0008f,
        };

        EmitFlash(cp, 0.5f * (mA * mB / (mA + mB)) * approach * approach);

        Vector2 dirAB = vRel.LengthSquared() > 1f
            ? Vector2.Normalize(vRel) : ev.Contact.Normal;

        float impCoeff = _ctx.Config.Player.PlayerImpactCoeff;
        float massBForA = eA == _player ? mB * impCoeff : mB;
        float massAForB = eB == _player ? mA * impCoeff : mA;

        bool dashInv = IsDashInvincible();
        if (!(eA == _player && dashInv))
            FractureService.BeginFracture(_world, eA, -1, cp, dirAB,
                vRel + vA.Linear, massBForA, weapon, FractureTiming.Default, _rng);
        if (!(eB == _player && dashInv))
            FractureService.BeginFracture(_world, eB, -1, cp, -dirAB,
                -vRel + vB.Linear, massAForB, weapon, FractureTiming.Default, _rng);
    }

    private void OnCellPulverized(CellPulverizedEvent ev)
    {
        _ctx.CellBudget.Remove(1);

        // Score: area × material density (denser materials score more).
        if (ev.Body != _player && _world.IsAlive(ev.Body) && _world.HasComponent<FracturableBody>(ev.Body))
        {
            float density = _world.GetComponent<FracturableBody>(ev.Body).Material.Density;
            _ctx.Score.Add(ev.Area * density);
        }

        // Check if a cockpit cell was pulverized on the player entity.
        if (ev.Body == _player && _world.IsAlive(_player) && _world.HasComponent<FracturableBody>(_player))
        {
            ref var fb = ref _world.GetComponent<FracturableBody>(_player);
            ref var t  = ref _world.GetComponent<Transform>(_player);
            // Transform the world centroid to body-local space to identify the cell.
            float cos = MathF.Cos(-t.Rotation), sin = MathF.Sin(-t.Rotation);
            Vector2 d = ev.WorldCentroid - t.Position;
            Vector2 localPos = new(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
            float bestSq = float.MaxValue;
            string? role = null;
            for (int i = 0; i < fb.Cells.Length; i++)
            {
                float dsq = (fb.Cells[i].Centroid - localPos).LengthSquared();
                if (dsq < bestSq) { bestSq = dsq; role = fb.Cells[i].Role; }
            }
            if (role == "cockpit") _pendingGameOver = true;
        }

        BodyColor color = GetBodyColor(ev.Body);
        EmitDustBurst(ev.WorldCentroid,
            ev.WorldCentroid - (_world.IsAlive(ev.Body)
                ? _world.GetComponent<Transform>(ev.Body).Position : ev.WorldCentroid),
            _world.IsAlive(ev.Body) && _world.HasComponent<Velocity>(ev.Body)
                ? _world.GetComponent<Velocity>(ev.Body).Linear : Vector2.Zero,
            ev.Area, color);
        SpawnDebris(ev, color);
    }

    private void OnFractureCompleted(FractureCompletedEvent ev)
    {
        _activeCollisions.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        bool isPlayer     = ev.Body == _player;
        bool isMothership = _world.HasComponent<MothershpId>(ev.Body);
        MothershpId origMid = isMothership ? _world.GetComponent<MothershpId>(ev.Body) : default;
        BodyColor color = GetBodyColor(ev.Body);
        var (savedAim, savedWc, savedWep, savedSk) = isPlayer ? SavePlayerState(ev.Body) : default;
        var fg = MakeFractureGroup(ev.Body);
        bool cockpitFound = false;

        foreach (var f in ev.Fragments)
        {
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnFracturableBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
            if (isMothership)
            {
                bool hasFragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
                if (hasFragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    _world.AddComponent(ne, new SpawnerAccumulator { Value = 0f });
                    _world.AddComponent(ne, new AlienTag());
                    _world.AddComponent(ne, new AlienVariant { Key = "mothership" });
                    _world.AddComponent(ne, new ShootCooldown { Remaining = 999f });
                    if (_world.HasComponent<Collider>(ne))
                    {
                        ref var col = ref _world.GetComponent<Collider>(ne);
                        col.Layer = GameLayers.Alien;
                        col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
                    }
                }
                // Cockpit-less fragment stays as inert asteroid-layer debris (SpawnFracturableBody default).
            }
            else
            {
                CopyTags(ev.Body, ne);
            }
            if (isPlayer && !cockpitFound && f.Body.Cells.Any(c => c.Role == "cockpit"))
            {
                cockpitFound = true;
                TransferPlayerToFragment(ne, f, savedAim, savedWc, savedWep, savedSk);
            }
        }
        _world.DestroyEntity(ev.Body);
        if (isPlayer && !cockpitFound) _pendingGameOver = true;
    }

    private void OnFractureSplit(FractureSplitEvent ev)
    {
        _activeCollisions.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        bool isPlayer     = ev.Body == _player;
        bool isMothership = _world.HasComponent<MothershpId>(ev.Body);
        MothershpId origMid = isMothership ? _world.GetComponent<MothershpId>(ev.Body) : default;
        BodyColor color = GetBodyColor(ev.Body);
        var (savedAim, savedWc, savedWep, savedSk) = isPlayer ? SavePlayerState(ev.Body) : default;
        var fg = MakeFractureGroup(ev.Body);
        bool cockpitFound = false;

        foreach (var p in ev.Pieces)
        {
            var f = p.Spec;
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnFracturableBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
            if (p.Process.HasValue) _world.AddComponent(ne, p.Process.Value);
            if (isMothership)
            {
                bool hasFragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
                if (hasFragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    _world.AddComponent(ne, new SpawnerAccumulator { Value = 0f });
                    _world.AddComponent(ne, new AlienTag());
                    _world.AddComponent(ne, new AlienVariant { Key = "mothership" });
                    _world.AddComponent(ne, new ShootCooldown { Remaining = 999f });
                    if (_world.HasComponent<Collider>(ne))
                    {
                        ref var col = ref _world.GetComponent<Collider>(ne);
                        col.Layer = GameLayers.Alien;
                        col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
                    }
                }
            }
            else
            {
                CopyTags(ev.Body, ne);
            }
            if (isPlayer && !cockpitFound && f.Body.Cells.Any(c => c.Role == "cockpit"))
            {
                cockpitFound = true;
                TransferPlayerToFragment(ne, f, savedAim, savedWc, savedWep, savedSk);
            }
        }
        _world.DestroyEntity(ev.Body);
        if (isPlayer && !cockpitFound) _pendingGameOver = true;
    }

    // ── Boss skill systems ────────────────────────────────────────────────────

    private void UpdateBossSkills(float dt)
    {
        if (!_ctx.Config.Entities.TryGetValue("mothership", out var ec) || ec.Boss == null) return;
        var bc = ec.Boss;

        // Collect living mothership fragments + count living cockpits.
        var msFrags      = new List<Entity>();
        int msFragCount  = 0;
        int livingCockpits = 0;
        Vector2 msCenter = Vector2.Zero;
        _world.ForEach<MothershpId, Transform, FracturableBody>(
            (Entity e, ref MothershpId _, ref Transform t, ref FracturableBody fb) =>
        {
            msCenter += t.Position;
            msFragCount++;
            msFrags.Add(e);
            bool[]? pulv = _world.HasComponent<FractureProcess>(e)
                ? _world.GetComponent<FractureProcess>(e).Pulverized : null;
            for (int i = 0; i < fb.Cells.Length; i++)
                if (fb.Cells[i].Role == "cockpit" && (pulv == null || !pulv[i]))
                    livingCockpits++;
        });

        if (msFragCount == 0 || livingCockpits == 0) { _mothershpKilled = true; return; }
        msCenter /= msFragCount;

        if (!_bossOverdriveTriggered && livingCockpits <= _mothershpInitialCockpits / 2)
            _bossOverdriveTriggered = true;

        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : msCenter;

        // Drift: slow thrust toward player on all fragments.
        foreach (var frag in msFrags)
        {
            if (!_world.IsAlive(frag) || !_world.HasComponent<Velocity>(frag)) continue;
            Vector2 fragPos = _world.GetComponent<Transform>(frag).Position;
            Vector2 toPlayer = playerPos - fragPos;
            float   dist = toPlayer.Length();
            if (dist < 1f) continue;
            float mass = _world.HasComponent<RigidBody>(frag) ? _world.GetComponent<RigidBody>(frag).Mass : 1f;
            ref var vel = ref _world.GetComponent<Velocity>(frag);
            vel.Linear += toPlayer / dist * (bc.DriftThrust / MathF.Max(mass, 0.1f)) * dt;
        }

        // Ram charge: sustained burst toward player while active.
        if (_bossRamChargeActive > 0f)
        {
            _bossRamChargeActive -= dt;
            foreach (var frag in msFrags)
            {
                if (!_world.IsAlive(frag) || !_world.HasComponent<Velocity>(frag)) continue;
                Vector2 fragPos  = _world.GetComponent<Transform>(frag).Position;
                Vector2 toPlayer2 = playerPos - fragPos;
                float   dist2 = toPlayer2.Length();
                if (dist2 < 1f) continue;
                float mass = _world.HasComponent<RigidBody>(frag) ? _world.GetComponent<RigidBody>(frag).Mass : 1f;
                ref var vel = ref _world.GetComponent<Velocity>(frag);
                vel.Linear += toPlayer2 / dist2 * (bc.RamChargeThrust / MathF.Max(mass, 0.1f)) * dt;
            }
        }

        // Shockwave skill.
        _bossShockwaveCd -= dt;
        if (_bossShockwaveCd <= 0f)
        {
            DoShockwave(msCenter, bc);
            _bossShockwaveCd = bc.ShockwaveCooldown;
        }

        // Black hole skill.
        _bossBlackHoleCd -= dt;
        if (_bossBlackHoleCd <= 0f)
        {
            SpawnBlackHole(msCenter, playerPos, bc);
            _bossBlackHoleCd = bc.BlackHoleCooldown;
        }

        // Ram charge trigger.
        _bossRamChargeCd -= dt;
        if (_bossRamChargeCd <= 0f)
        {
            if ((msCenter - playerPos).Length() >= bc.RamChargeMinDist)
                _bossRamChargeActive = bc.RamChargeDuration;
            _bossRamChargeCd = bc.RamChargeCooldown;
        }

        // Spawner cells.
        UpdateSpawners(dt, bc);
    }

    private void DoShockwave(Vector2 center, BossConfig bc)
    {
        float radSq = bc.ShockwaveRadius * bc.ShockwaveRadius;
        var impulses = new List<(Entity e, Vector2 dir, float accel)>();
        _world.ForEach<Transform, RigidBody>((Entity e, ref Transform t, ref RigidBody rb) =>
        {
            Vector2 delta = t.Position - center;
            float dSq = delta.LengthSquared();
            if (dSq < 1f || dSq > radSq) return;
            float dist  = MathF.Sqrt(dSq);
            float force = bc.ShockwaveStrength / (dist + 1f);
            impulses.Add((e, delta / dist, force / MathF.Max(rb.Mass, 0.1f)));
        });
        foreach (var (e, dir, accel) in impulses)
        {
            if (!_world.IsAlive(e) || !_world.HasComponent<Velocity>(e)) continue;
            ref var vel = ref _world.GetComponent<Velocity>(e);
            vel.Linear += dir * accel;
        }
        EmitFlash(center, bc.ShockwaveStrength * 0.005f);
    }

    private void SpawnBlackHole(Vector2 center, Vector2 playerPos, BossConfig bc)
    {
        Vector2 toPlayer = playerPos - center;
        float   tlen     = toPlayer.Length();
        Vector2 dir      = tlen > 1f ? toPlayer / tlen : -Vector2.UnitY;
        var bh = _world.CreateEntity();
        _world.AddComponent(bh, new Transform { Position = center, PreviousPosition = center });
        _world.AddComponent(bh, new Velocity  { Linear = dir * bc.BlackHoleSpeed });
        _world.AddComponent(bh, new BlackHoleTag
        {
            Radius      = bc.BlackHoleRadius,
            Strength    = bc.BlackHoleStrength,
            CrushRadius = bc.BlackHoleCrushRadius,
        });
        _world.AddComponent(bh, new TimeToLive { Remaining = bc.BlackHoleDuration });
    }

    private void UpdateSpawners(float dt, BossConfig bc)
    {
        float interval = _bossOverdriveTriggered ? bc.SpawnInterval * 0.5f : bc.SpawnInterval;
        string spawnType = bc.SpawnType;
        var toSpawn = new List<Vector2>();

        _world.ForEach<MothershpId, FracturableBody, Transform>(
            (Entity e, ref MothershpId _, ref FracturableBody fb, ref Transform t) =>
        {
            bool hasCockpit = false;
            bool[]? pulv = _world.HasComponent<FractureProcess>(e)
                ? _world.GetComponent<FractureProcess>(e).Pulverized : null;
            for (int i = 0; i < fb.Cells.Length; i++)
                if (fb.Cells[i].Role == "cockpit" && (pulv == null || !pulv[i]))
                { hasCockpit = true; break; }
            if (!hasCockpit) return;

            if (!_world.HasComponent<SpawnerAccumulator>(e)) return;
            ref var acc = ref _world.GetComponent<SpawnerAccumulator>(e);
            acc.Value += dt;
            if (acc.Value < interval) return;
            acc.Value = 0f;

            // Use first alive spawner cell as spawn origin.
            float cos = MathF.Cos(t.Rotation);
            float sin = MathF.Sin(t.Rotation);
            Vector2 fragCenter = t.Position;
            for (int i = 0; i < fb.Cells.Length; i++)
            {
                if (fb.Cells[i].Role != "spawner") continue;
                if (pulv != null && pulv[i]) continue;
                Vector2 cen = fb.Cells[i].Centroid;
                Vector2 worldCen = new(
                    cen.X * cos - cen.Y * sin + fragCenter.X,
                    cen.X * sin + cen.Y * cos + fragCenter.Y);
                Vector2 outDir = worldCen - fragCenter;
                float   olen   = outDir.Length();
                if (olen < 1e-4f) outDir = Vector2.UnitX; else outDir /= olen;
                toSpawn.Add(worldCen + outDir * bc.SpawnSafetyMargin);
                break;
            }
        });

        foreach (var pos in toSpawn)
            SpawnAlien(pos, spawnType);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private readonly List<Vector2> _meshVerts = new();
    private readonly List<int>     _meshLens  = new();
    private readonly List<Vector2> _dbuf      = new();

    // Draws every entity that has a FracturableBody + BodyColor (asteroids, player ship, aliens).
    private void DrawBodies(IRenderer r, float alpha)
    {
        _world.ForEach<Transform, FracturableBody, BodyColor>(
            (Entity e, ref Transform t, ref FracturableBody fb, ref BodyColor col) =>
        {
            var (pos, rot) = Interp(t, alpha);
            float c = MathF.Cos(rot), s = MathF.Sin(rot);

            bool[]? broken = null, pulv = null;
            if (_world.HasComponent<FractureProcess>(e))
            {
                ref var fp = ref _world.GetComponent<FractureProcess>(e);
                broken = fp.Broken; pulv = fp.Pulverized;
            }

            _meshVerts.Clear(); _meshLens.Clear();
            for (int ci = 0; ci < fb.Cells.Length; ci++)
            {
                if (pulv?[ci] == true) continue;
                var lv = fb.Cells[ci].Local;
                foreach (var v in lv)
                    _meshVerts.Add(new Vector2(v.X * c - v.Y * s + pos.X, v.X * s + v.Y * c + pos.Y));
                _meshLens.Add(lv.Length);
            }
            r.FillPath(CollectionsMarshal.AsSpan(_meshVerts),
                       CollectionsMarshal.AsSpan(_meshLens), col.Fill);

            if (broken != null && pulv != null)
            {
                var (ol, cr) = ComputeEdgesLive(fb.Cells, fb.Bonds, broken, pulv);
                DrawEdgeSegs(r, ol, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, cr, pos, c, s, CrackColor(col.Fill), 1f);
            }
            else if (_world.HasComponent<RenderOutline>(e))
            {
                ref var ro = ref _world.GetComponent<RenderOutline>(e);
                DrawEdgeSegs(r, ro.Outline, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, ro.Cracks,  pos, c, s, CrackColor(col.Fill), 1f);
            }
        });
    }

    private void DrawDebris(IRenderer r, float alpha)
    {
        _world.ForEach<Transform, DebrisPiece, TimeToLive>(
            (Entity _, ref Transform t, ref DebrisPiece dp, ref TimeToLive ttl) =>
        {
            var (pos, rot) = Interp(t, alpha);
            float c = MathF.Cos(rot), s = MathF.Sin(rot);
            float k = dp.MaxTtl > 0f ? Math.Clamp(ttl.Remaining / dp.MaxTtl, 0f, 1f) : 1f;
            _dbuf.Clear();
            foreach (var lv in dp.Local)
                _dbuf.Add(new Vector2(lv.X * c - lv.Y * s + pos.X, lv.X * s + lv.Y * c + pos.Y));
            r.FillPolygon(CollectionsMarshal.AsSpan(_dbuf), dp.Color.WithAlpha((byte)(dp.Color.A * k)));
        });
    }

    private void DrawBullets(IRenderer r, float alpha)
    {
        _world.ForEach<Transform, BulletTag, BulletVisual>(
            (Entity _, ref Transform t, ref BulletTag _, ref BulletVisual bv) =>
        {
            var (p, _) = Interp(t, alpha);
            Vector2 d = t.Position - t.PreviousPosition;
            Vector2 dir = d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : Vector2.UnitY;
            Vector2 tail = p - dir * 14f;
            r.DrawLine(tail, p, new Color(255, 170, 60, 80), 5f);
            r.DrawLine(tail, p, new Color(255, 240, 165, 220), 2f);
            r.FillCircle(p, 3f, bv.Color);
        });
    }

    private void DrawBlackHoles(IRenderer r, float alpha)
    {
        _world.ForEach<Transform, BlackHoleTag>((Entity _, ref Transform t, ref BlackHoleTag bh) =>
        {
            var (pos, _) = Interp(t, alpha);
            r.FillCircle(pos, bh.CrushRadius * 3f, new Color(20, 0, 40, 100));
            r.FillCircle(pos, bh.CrushRadius * 1.5f, new Color(10, 0, 25, 200));
            r.FillCircle(pos, bh.CrushRadius, new Color(3, 0, 8, 255));
        });
    }

    // Draws the player's aim line (ship body is drawn by DrawBodies).
    private void DrawPlayerAim(IRenderer r, float alpha)
    {
        if (!_world.IsAlive(_player)) return;
        if (!_world.HasComponent<Transform>(_player) || !_world.HasComponent<AimComponent>(_player)) return;
        var (p, _) = Interp(_world.GetComponent<Transform>(_player), alpha);
        var aim    = _world.GetComponent<AimComponent>(_player).Dir;
        r.DrawLine(p, p + aim * 32f, new Color(170, 205, 255, 160), 2f);
    }

    // ── HUD constants ─────────────────────────────────────────────────────────
    // HUD-display scale for ship damage widget (body-local coords × this = HUD pixels).
    private const float HudShipScale = 2.0f;
    // X-center of ship damage widget.
    private const float HudShipCX    = 70f;
    // Y-center of ship damage widget (from screen bottom).
    private const float HudShipCYOff = 55f;
    // Where weapon bars start (from left).
    private const float HudWeapX     = 148f;
    // Where skill bars start (from right).
    private const float HudSkillOffX = 195f;

    private static readonly (string Role, string WeapKey, string Label, Color Color)[] WeaponDefs =
    [
        ("cannon",   "cannon",   "C", new Color(255, 200, 80)),
        ("shotgun",  "shotgun",  "S", new Color(255, 140, 60)),
        ("piercing", "piercing", "P", new Color(180, 120, 255)),
        ("grenade",  "grenade",  "G", new Color(100, 220, 120)),
    ];
    private static readonly (string SkillKey, string Label)[] SkillDefs =
    [
        ("dash",   "Q"),
        ("turbo",  "E"),
        ("slowmo", "R"),
    ];

    private void DrawHud(IRenderer r)
    {
        var font  = new FontSpec("monospace", 13f);
        var small = new FontSpec("monospace", 11f);

        // ── Top-left: timer + score ───────────────────────────────────────────
        int elapsed = (int)_gameTime;
        r.DrawText($"{elapsed / 60:00}:{elapsed % 60:00}   {_ctx.Score.Total:F0} pts",
            new Vector2(12f, 10f), new Color(190, 200, 220), font);

        if (!_world.IsAlive(_player)) return;

        float bY = _ctx.ScreenH - 10f;  // bottom of HUD bar area

        // ── Ship damage widget ─────────────────────────────────────────────────
        var shipCenter = new Vector2(HudShipCX, _ctx.ScreenH - HudShipCYOff);
        DrawShipWidget(r, shipCenter, HudShipScale);

        // ── Weapon cooldown bars ───────────────────────────────────────────────
        DrawWeaponBars(r, HudWeapX, bY, small);

        // ── Skill cooldown bars ────────────────────────────────────────────────
        DrawSkillBars(r, _ctx.ScreenW - HudSkillOffX, bY, small);
    }

    private void DrawShipWidget(IRenderer r, Vector2 center, float scale)
    {
        if (!_world.HasComponent<FracturableBody>(_player)) return;
        ref var fb  = ref _world.GetComponent<FracturableBody>(_player);
        bool[]? pulv = _world.HasComponent<FractureProcess>(_player)
            ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;

        // Fill alive cells (green).
        _meshVerts.Clear(); _meshLens.Clear();
        for (int ci = 0; ci < fb.Cells.Length; ci++)
        {
            if (pulv?[ci] == true) continue;
            var lv = fb.Cells[ci].Local;
            foreach (var v in lv) _meshVerts.Add(center + v * scale);
            _meshLens.Add(lv.Length);
        }
        if (_meshVerts.Count > 0)
            r.FillPath(CollectionsMarshal.AsSpan(_meshVerts), CollectionsMarshal.AsSpan(_meshLens),
                new Color(45, 165, 65, 210));

        // Outlines: alive cells.
        for (int ci = 0; ci < fb.Cells.Length; ci++)
        {
            if (pulv?[ci] == true) continue;
            var lv = fb.Cells[ci].Local;
            for (int i = 0; i < lv.Length; i++)
                r.DrawLine(center + lv[i] * scale, center + lv[(i + 1) % lv.Length] * scale,
                    new Color(100, 200, 110, 200), 1f);
        }

        // Ghost outlines for pulverized / absent cells.
        for (int ci = 0; ci < fb.Cells.Length; ci++)
        {
            if (pulv == null || !pulv[ci]) continue;
            var lv = fb.Cells[ci].Local;
            for (int i = 0; i < lv.Length; i++)
                r.DrawLine(center + lv[i] * scale, center + lv[(i + 1) % lv.Length] * scale,
                    new Color(55, 60, 72, 160), 1f);
        }
    }

    private void DrawWeaponBars(IRenderer r, float startX, float bottomY, in FontSpec font)
    {
        const float BarW = 38f, BarH = 8f, Gap = 46f;

        bool hasFb  = _world.HasComponent<FracturableBody>(_player);
        bool hasFp  = hasFb && _world.HasComponent<FractureProcess>(_player);
        bool[]? pulv = hasFp ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;
        WeaponCooldowns wcd = _world.HasComponent<WeaponCooldowns>(_player)
            ? _world.GetComponent<WeaponCooldowns>(_player) : default;

        float x = startX;
        foreach (var (role, key, label, col) in WeaponDefs)
        {
            bool cellAlive = IsWeaponCellAlive(role, hasFb, pulv);
            Color textC = cellAlive ? col           : new Color(70, 74, 85);
            Color bgC   = new Color(22, 25, 32);
            Color fgC   = cellAlive ? col           : new Color(45, 48, 58);

            r.DrawText(label, new Vector2(x + 1f, bottomY - BarH - 15f), textC, font);
            FillRect(r, x, bottomY - BarH, BarW, BarH, bgC);

            if (cellAlive)
            {
                float cdRem = key switch
                {
                    "cannon"   => wcd.Cannon,
                    "shotgun"  => wcd.Shotgun,
                    "piercing" => wcd.Piercing,
                    "grenade"  => wcd.Grenade,
                    _          => 0f
                };
                float fill = BarW;
                if (_ctx.Config.Weapons.TryGetValue(key, out var wCfg))
                {
                    float maxCd = 1f / MathF.Max(0.001f, wCfg.FireRate);
                    fill = BarW * (1f - Math.Clamp(cdRem / maxCd, 0f, 1f));
                }
                if (fill > 0f) FillRect(r, x, bottomY - BarH, fill, BarH, fgC);
            }
            x += Gap;
        }
    }

    private bool IsWeaponCellAlive(string role, bool hasFb, bool[]? pulv)
    {
        if (!hasFb) return false;
        ref var fb = ref _world.GetComponent<FracturableBody>(_player);
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (fb.Cells[i].Role == role)
                return pulv == null || !pulv[i];
        }
        return false;
    }

    private void DrawSkillBars(IRenderer r, float startX, float bottomY, in FontSpec font)
    {
        const float BarW = 52f, BarH = 8f, Gap = 60f;

        bool hasFb  = _world.HasComponent<FracturableBody>(_player);
        bool hasFp  = hasFb && _world.HasComponent<FractureProcess>(_player);
        bool[]? pulv = hasFp ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;
        bool propOk = HasAlivePropeller(hasFb, pulv);
        SkillState sk = _world.HasComponent<SkillState>(_player)
            ? _world.GetComponent<SkillState>(_player) : default;

        float x = startX;
        foreach (var (key, label) in SkillDefs)
        {
            bool gate = key == "slowmo" || propOk;
            if (!_ctx.Config.Skills.TryGetValue(key, out var sc)) { x += Gap; continue; }

            float cdRem = key switch
            {
                "dash"   => sk.DashCooldown,
                "turbo"  => sk.TurboCooldown,
                "slowmo" => sk.SlowMoCooldown,
                _        => 0f
            };
            float ratio = sc.Cooldown > 0f
                ? 1f - Math.Clamp(cdRem / sc.Cooldown, 0f, 1f)
                : 1f;

            Color col   = key switch
            {
                "dash"   => new Color(100, 180, 255),
                "turbo"  => new Color(255, 150, 50),
                "slowmo" => new Color(180, 100, 255),
                _        => new Color(160, 170, 185)
            };
            Color textC = gate ? col : new Color(70, 74, 85);
            Color bgC   = new Color(22, 25, 32);
            Color fgC   = gate ? col : new Color(45, 48, 58);

            r.DrawText(label, new Vector2(x + 1f, bottomY - BarH - 15f), textC, font);
            FillRect(r, x, bottomY - BarH, BarW, BarH, bgC);
            float fill = BarW * ratio;
            if (fill > 0f && gate) FillRect(r, x, bottomY - BarH, fill, BarH, fgC);
            x += Gap;
        }
    }

    private bool HasAlivePropeller(bool hasFb, bool[]? pulv)
    {
        if (!hasFb || !_world.IsAlive(_player)) return false;
        ref var fb = ref _world.GetComponent<FracturableBody>(_player);
        for (int i = 0; i < fb.Cells.Length; i++)
            if (fb.Cells[i].Role == "propeller" && (pulv == null || !pulv[i]))
                return true;
        return false;
    }

    private static void FillRect(IRenderer r, float x, float y, float w, float h, Color c)
        => r.FillPolygon(stackalloc Vector2[]
            { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) }, c);

    // ── VFX ──────────────────────────────────────────────────────────────────

    private void EmitFlash(Vector2 point, float energy)
    {
        float e = Math.Clamp(energy / 80_000f, 0.25f, 2.5f);
        float sz = 28f * e;
        _fx.Emit(new Particle
        {
            Position = point, Velocity = Vector2.Zero, Drag = 0f,
            Life = 0.18f, MaxLife = 0.18f,
            Size0 = sz * 0.35f, Size1 = sz,
            Color0 = new Color(255, 245, 210, 235), Color1 = new Color(255, 165, 70, 0),
        });
    }

    private void EmitDustBurst(Vector2 centroid, Vector2 dirHint, Vector2 carrier, float area, BodyColor color)
    {
        int n = Math.Clamp((int)(area / 200f), 2, 12);
        Vector2 vdir = dirHint.LengthSquared() > 1e-4f
            ? Vector2.Normalize(dirHint)
            : new Vector2(MathF.Cos((float)_rng.NextDouble() * MathF.Tau),
                          MathF.Sin((float)_rng.NextDouble() * MathF.Tau));
        float baseSz = 3.5f * MathF.Sqrt(MathF.Max(area, 1f) / 1400f);
        for (int i = 0; i < n; i++)
        {
            float ang = ((float)_rng.NextDouble() * 2f - 1f) * MathF.PI * 0.7f;
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            Vector2 dir  = new(vdir.X * ca - vdir.Y * sa, vdir.X * sa + vdir.Y * ca);
            float   spd  = 120f * (0.4f + (float)_rng.NextDouble());
            float   ttl  = 0.6f * (0.6f + 0.6f * (float)_rng.NextDouble());
            float   sz   = baseSz * (0.7f + 0.6f * (float)_rng.NextDouble());
            Vector2 jit  = new((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f);
            _fx.Emit(new Particle
            {
                Position = centroid + jit * baseSz,
                Velocity = dir * spd + carrier,
                Drag = 2.2f, Life = ttl, MaxLife = ttl,
                Size0 = sz, Size1 = sz * 0.1f,
                Color0 = color.Outline.WithAlpha(220), Color1 = color.Outline.WithAlpha(0),
            });
        }
    }

    private void SpawnDebris(in CellPulverizedEvent ev, BodyColor color)
    {
        var verts = ev.WorldVerts;
        if (verts is null || verts.Length < 3) return;
        var pieces = new List<List<Vector2>> { new(verts) };
        int cuts = _rng.Next(1, 3);
        for (int c = 0; c < cuts; c++)
        {
            float ang = (float)(_rng.NextDouble() * Math.PI);
            Vector2 cutDir = new(MathF.Cos(ang), MathF.Sin(ang));
            var next = new List<List<Vector2>>();
            foreach (var poly in pieces)
            {
                var left = new List<Vector2>(); var right = new List<Vector2>();
                SplitByLine(poly, ev.WorldCentroid, cutDir, left, right);
                if (left.Count  >= 3) next.Add(left);
                if (right.Count >= 3) next.Add(right);
            }
            if (next.Count > 0) pieces = next;
        }
        float ttl = 1.4f;
        foreach (var poly in pieces)
        {
            Vector2 cen = Vector2.Zero;
            foreach (var v in poly) cen += v;
            cen /= poly.Count;
            var local = poly.Select(v => v - cen).ToArray();
            Vector2 outward = (cen - ev.WorldCentroid) is var o && o.LengthSquared() > 1e-4f
                ? Vector2.Normalize(o)
                : new Vector2(MathF.Cos((float)(_rng.NextDouble() * MathF.Tau)),
                              MathF.Sin((float)(_rng.NextDouble() * MathF.Tau)));
            Vector2 vel  = ev.CellVelocity + outward * (80f * (0.5f + (float)_rng.NextDouble()));
            float   spin = ev.BodyAngular + (float)(_rng.NextDouble() * 2 - 1) * 4f;
            var de = _world.CreateEntity();
            _world.AddComponent(de, new Transform { Position = cen, PreviousPosition = cen });
            _world.AddComponent(de, new Velocity { Linear = vel, Angular = spin });
            _world.AddComponent(de, new TimeToLive { Remaining = ttl });
            _world.AddComponent(de, new DebrisPiece { Local = local, Color = color.Fill, MaxTtl = ttl });
        }
    }

    private static void SplitByLine(List<Vector2> poly, Vector2 P, Vector2 dir,
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
                float t = sc / (sc - sn);
                Vector2 ip = cur + (nxt - cur) * t;
                left.Add(ip); right.Add(ip);
            }
        }
    }

    // ── Edge helpers ──────────────────────────────────────────────────────────

    private static (Vector2[] outline, Vector2[] cracks) ComputeEdges(Cell[] cells, Bond[] bonds)
    {
        var bonded = new HashSet<(int, int)>();
        foreach (var b in bonds) bonded.Add((Math.Min(b.A, b.B), Math.Max(b.A, b.B)));
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
        var outline = new List<Vector2>(); var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                var key = ((int)MathF.Round(((a + b) * 0.5f).X * 2f),
                           (int)MathF.Round(((a + b) * 0.5f).Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }

    private static (Vector2[] outline, Vector2[] cracks) ComputeEdgesLive(
        Cell[] cells, Bond[] bonds, bool[] broken, bool[] pulverized)
    {
        var bonded = new HashSet<(int, int)>();
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            if (broken[bi]) continue;
            int a = bonds[bi].A, b = bonds[bi].B;
            if (!pulverized[a] && !pulverized[b])
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
        var outline = new List<Vector2>(); var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            if (pulverized[ci]) continue;
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                var key = ((int)MathF.Round(((a + b) * 0.5f).X * 2f),
                           (int)MathF.Round(((a + b) * 0.5f).Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }

    private static void DrawEdgeSegs(IRenderer r, Vector2[] segs, Vector2 pos,
                                     float cos, float sin, Color color, float w)
    {
        for (int k = 0; k + 1 < segs.Length; k += 2)
        {
            Vector2 a = segs[k], b = segs[k + 1];
            r.DrawLine(
                new Vector2(a.X * cos - a.Y * sin + pos.X, a.X * sin + a.Y * cos + pos.Y),
                new Vector2(b.X * cos - b.Y * sin + pos.X, b.X * sin + b.Y * cos + pos.Y),
                color, w);
        }
    }

    private static Color CrackColor(Color fill) =>
        new((byte)(fill.R * 0.35f), (byte)(fill.G * 0.35f), (byte)(fill.B * 0.35f));

    private const float TeleSq = 200f * 200f;
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

// ── Inline systems specific to the game ──────────────────────────────────────

sealed class PlayerControlSystem : ISystem
{
    private readonly GameContext _ctx;
    private readonly Camera      _camera;
    public Entity Player { get; set; }
    public Action<Vector2, Vector2>? OnPiercingFire { get; set; }

    public PlayerControlSystem(GameContext ctx, Camera camera) { _ctx = ctx; _camera = camera; }

    private float ComputeThrustMult(World world)
    {
        if (!world.HasComponent<FracturableBody>(Player)) return 1f;
        ref var fb = ref world.GetComponent<FracturableBody>(Player);
        bool[]? pulv = world.HasComponent<FractureProcess>(Player)
            ? world.GetComponent<FractureProcess>(Player).Pulverized
            : null;
        int total = 0, alive = 0;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (fb.Cells[i].Role != "propeller") continue;
            total++;
            if (pulv == null || !pulv[i]) alive++;
        }
        if (total == 0) return 1f;     // no propellers defined → full thrust
        if (alive == 0) return 0f;     // all propellers gone → no thrust
        if (alive < total) return _ctx.Config.Player.ThrustPartialMult;
        return 1f;
    }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(Player)) return;

        ref var t   = ref world.GetComponent<Transform>(Player);
        ref var aim = ref world.GetComponent<AimComponent>(Player);
        ref var wcd = ref world.GetComponent<WeaponCooldowns>(Player);
        ref var sk  = ref world.GetComponent<SkillState>(Player);

        var inp    = _ctx.Input;
        var skills = _ctx.Config.Skills;
        float fdt  = (float)dt;

        // ── Skill cooldown and active-duration ticking ────────────────────────
        if (sk.DashCooldown  > 0f) sk.DashCooldown  = MathF.Max(0f, sk.DashCooldown  - fdt);
        if (sk.TurboCooldown > 0f) sk.TurboCooldown = MathF.Max(0f, sk.TurboCooldown - fdt);
        if (sk.SlowMoCooldown > 0f) sk.SlowMoCooldown = MathF.Max(0f, sk.SlowMoCooldown - fdt);
        if (sk.DashActive  > 0f) sk.DashActive  = MathF.Max(0f, sk.DashActive  - fdt);
        if (sk.TurboActive > 0f) sk.TurboActive = MathF.Max(0f, sk.TurboActive - fdt);
        if (sk.SlowMoActive > 0f) sk.SlowMoActive = MathF.Max(0f, sk.SlowMoActive - fdt);

        bool hasProp = HasAlivePropeller(world);

        // ── Dash (Q) ─────────────────────────────────────────────────────────
        if (inp.IsPressed(KeyCode.Q) && hasProp && sk.DashCooldown <= 0f
            && skills.TryGetValue("dash", out var dashCfg))
        {
            ref var vel = ref world.GetComponent<Velocity>(Player);
            vel.Linear += aim.Dir * (dashCfg.VelocitySpike ?? 1400f);
            sk.DashActive   = dashCfg.InvincibilityTime ?? 0.35f;
            sk.DashCooldown = dashCfg.Cooldown;
        }

        // ── Turbo (E) ─────────────────────────────────────────────────────────
        if (inp.IsPressed(KeyCode.E) && hasProp && sk.TurboCooldown <= 0f
            && skills.TryGetValue("turbo", out var turboCfgAct))
        {
            sk.TurboActive   = turboCfgAct.Duration;
            sk.TurboCooldown = turboCfgAct.Cooldown;
        }

        // ── Slow-Mo (R) ───────────────────────────────────────────────────────
        if (inp.IsPressed(KeyCode.R) && sk.SlowMoCooldown <= 0f
            && skills.TryGetValue("slowmo", out var slowCfgAct))
        {
            sk.SlowMoActive   = slowCfgAct.Duration;
            sk.SlowMoCooldown = slowCfgAct.Cooldown;
        }

        // ── Movement — thrust degraded by propeller survival ──────────────────
        float thrustMult = ComputeThrustMult(world);
        Vector2 a = Vector2.Zero;
        if (inp.IsHeld(KeyCode.W)) a.Y -= 1; if (inp.IsHeld(KeyCode.S)) a.Y += 1;
        if (inp.IsHeld(KeyCode.A)) a.X -= 1; if (inp.IsHeld(KeyCode.D)) a.X += 1;
        if (a != Vector2.Zero && thrustMult > 0f)
        {
            float turboMult = (sk.TurboActive > 0f && skills.TryGetValue("turbo", out var tc))
                ? (tc.ThrustMult ?? 3f) : 1f;
            float slowBoost = (sk.SlowMoActive > 0f && skills.TryGetValue("slowmo", out var smc))
                ? (smc.PlayerSpeedBoost ?? 1f) : 1f;
            float thrust = _ctx.Config.Player.Thrust * thrustMult * turboMult * slowBoost;
            PhysicsSystem.ApplyForce(world, Player, Vector2.Normalize(a) * thrust);
        }

        // ── Aim toward mouse ──────────────────────────────────────────────────
        Vector2 mouseWorld = _camera.ScreenToWorld(inp.MouseScreen);
        Vector2 toMouse    = mouseWorld - t.Position;
        if (toMouse.LengthSquared() > 1f) aim.Dir = Vector2.Normalize(toMouse);

        // ── Weapon cooldown ticking ───────────────────────────────────────────
        if (wcd.Cannon   > 0f) wcd.Cannon   = MathF.Max(0f, wcd.Cannon   - fdt);
        if (wcd.Shotgun  > 0f) wcd.Shotgun  = MathF.Max(0f, wcd.Shotgun  - fdt);
        if (wcd.Piercing > 0f) wcd.Piercing = MathF.Max(0f, wcd.Piercing - fdt);
        if (wcd.Grenade  > 0f) wcd.Grenade  = MathF.Max(0f, wcd.Grenade  - fdt);

        Vector2 muzzle = t.Position + aim.Dir * 24f;

        // ── Cannon (left-click) ───────────────────────────────────────────────
        if (inp.IsMouseLeft && wcd.Cannon <= 0f && IsWeaponCellAlive("cannon", world)
            && _ctx.Config.Weapons.TryGetValue("cannon", out var ccfg))
        {
            wcd.Cannon = 1f / MathF.Max(0.001f, ccfg.FireRate);
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
            world.AddComponent(b, new Velocity { Linear = aim.Dir * ccfg.ProjectileSpeed });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = new Color(255, 230, 90) });
            world.AddComponent(b, new BulletData { WeaponKey = "cannon", Energy = ccfg.Energy });
            world.AddComponent(b, new TimeToLive { Remaining = ccfg.TimeToLive });
        }

        // ── Shotgun (right-click) ─────────────────────────────────────────────
        if (inp.IsMouseRight && wcd.Shotgun <= 0f && IsWeaponCellAlive("shotgun", world)
            && _ctx.Config.Weapons.TryGetValue("shotgun", out var scfg))
        {
            wcd.Shotgun = 1f / MathF.Max(0.001f, scfg.FireRate);
            int rays  = scfg.Rays ?? 7;
            float half = (scfg.ConeAngle ?? 18f) * 0.5f * MathF.PI / 180f;
            float step = rays > 1 ? half * 2f / (rays - 1) : 0f;
            float baseA = MathF.Atan2(aim.Dir.Y, aim.Dir.X) - half;
            float rayE  = scfg.EnergyPerRay ?? scfg.Energy / MathF.Max(1f, rays);
            for (int i = 0; i < rays; i++)
            {
                float ang = baseA + step * i;
                Vector2 d = new(MathF.Cos(ang), MathF.Sin(ang));
                var b = world.CreateEntity();
                world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
                world.AddComponent(b, new Velocity { Linear = d * scfg.ProjectileSpeed });
                world.AddComponent(b, new BulletTag());
                world.AddComponent(b, new BulletVisual { Color = new Color(255, 160, 80) });
                world.AddComponent(b, new BulletData { WeaponKey = "shotgun", Energy = rayE });
                world.AddComponent(b, new TimeToLive { Remaining = scfg.TimeToLive });
            }
        }

        // ── Piercing (G key) ─────────────────────────────────────────────────
        if (inp.IsPressed(KeyCode.G) && wcd.Piercing <= 0f && IsWeaponCellAlive("piercing", world)
            && _ctx.Config.Weapons.TryGetValue("piercing", out var pcfg))
        {
            wcd.Piercing = 1f / MathF.Max(0.001f, pcfg.FireRate);
            OnPiercingFire?.Invoke(muzzle, aim.Dir);
        }

        // ── Grenade (F key) ───────────────────────────────────────────────────
        if (inp.IsPressed(KeyCode.F) && wcd.Grenade <= 0f && IsWeaponCellAlive("grenade", world)
            && _ctx.Config.Weapons.TryGetValue("grenade", out var gcfg))
        {
            wcd.Grenade = 1f / MathF.Max(0.001f, gcfg.FireRate);
            Vector2 gPos = muzzle;
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = gPos, PreviousPosition = gPos });
            world.AddComponent(b, new Velocity { Linear = aim.Dir * gcfg.ProjectileSpeed });
            world.AddComponent(b, new RigidBody { Mass = 0.5f, Inertia = 0f, LinearDrag = 1.8f, AngularDrag = 0f, Restitution = 0f, Friction = 0f });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = new Color(100, 255, 100) });
            world.AddComponent(b, new BulletData { WeaponKey = "grenade", Energy = 0f });
            world.AddComponent(b, new GrenadeFuse { Remaining = gcfg.FuseTime ?? 1.8f, WeaponKey = "grenade" });
        }
    }

    private bool HasAlivePropeller(World world)
    {
        if (!world.HasComponent<FracturableBody>(Player)) return false;
        ref var fb = ref world.GetComponent<FracturableBody>(Player);
        bool[]? pulv = world.HasComponent<FractureProcess>(Player)
            ? world.GetComponent<FractureProcess>(Player).Pulverized : null;
        for (int i = 0; i < fb.Cells.Length; i++)
            if (fb.Cells[i].Role == "propeller" && (pulv == null || !pulv[i]))
                return true;
        return false;
    }

    private bool IsWeaponCellAlive(string role, World world)
    {
        if (!world.HasComponent<FracturableBody>(Player)) return false;
        ref var fb = ref world.GetComponent<FracturableBody>(Player);
        bool[]? pulv = world.HasComponent<FractureProcess>(Player)
            ? world.GetComponent<FractureProcess>(Player).Pulverized : null;
        for (int i = 0; i < fb.Cells.Length; i++)
            if (fb.Cells[i].Role == role && (pulv == null || !pulv[i]))
                return true;
        return false;
    }
}

sealed class VortexSystem : ISystem
{
    private readonly Vector2      _centre;
    private readonly VortexConfig _cfg;
    private float _time = 0f;

    public VortexSystem(Vector2 centre, VortexConfig cfg) { _centre = centre; _cfg = cfg; }

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        _time += fdt;
        float centripetalK = MathF.Max(0f, _cfg.Centripetal
                             + _cfg.VariationCentripetal * MathF.Sin(_time * MathF.Tau / 11f));
        float tangentialK  = MathF.Max(0f, _cfg.Tangential
                             + _cfg.VariationTangential * MathF.Sin(_time * MathF.Tau / 13f + MathF.PI * 0.5f));
        float deadzone  = _cfg.Deadzone;
        float capFrames = _cfg.CapFrames;

        world.ForEach<Transform, Velocity, RigidBody>(
            (Entity _, ref Transform t, ref Velocity v, ref RigidBody _) =>
        {
            Vector2 toCenter = _centre - t.Position;
            float dist = toCenter.Length();
            if (dist < 1e-3f) return;
            float excess = dist - deadzone;
            if (excess <= 0f) return;

            Vector2 radial  = toCenter / dist;
            Vector2 tangent = new(-radial.Y, radial.X);

            float forceC = centripetalK * excess * fdt;
            float forceT = tangentialK  * excess * fdt;
            float capC   = forceC * capFrames;
            float capT   = forceT * capFrames;

            float vC = Vector2.Dot(v.Linear, radial);
            float vT = Vector2.Dot(v.Linear, tangent);

            if (vC < capC) v.Linear += radial  * MathF.Min(forceC, capC - vC);
            if (vT < capT) v.Linear += tangent * MathF.Min(forceT, capT - vT);
        });
    }
}

sealed class BorderDampSystem : ISystem
{
    private readonly float _worldW, _worldH;
    private const float Zone  = 200f;
    private const float DampK = 20f;

    public BorderDampSystem(float worldW, float worldH) { _worldW = worldW; _worldH = worldH; }

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        world.ForEach<Transform, Velocity>((Entity _, ref Transform t, ref Velocity v) =>
        {
            float x = t.Position.X, y = t.Position.Y;

            float dL = x;
            if (dL < Zone && v.Linear.X < 0f)
                v.Linear.X *= MathF.Exp(-DampK * (1f - dL / Zone) * fdt);

            float dR = _worldW - x;
            if (dR < Zone && v.Linear.X > 0f)
                v.Linear.X *= MathF.Exp(-DampK * (1f - dR / Zone) * fdt);

            float dT = y;
            if (dT < Zone && v.Linear.Y < 0f)
                v.Linear.Y *= MathF.Exp(-DampK * (1f - dT / Zone) * fdt);

            float dB = _worldH - y;
            if (dB < Zone && v.Linear.Y > 0f)
                v.Linear.Y *= MathF.Exp(-DampK * (1f - dB / Zone) * fdt);

            t.Position = Vector2.Clamp(t.Position, Vector2.Zero, new Vector2(_worldW, _worldH));
        });
    }
}

sealed class GhostSystem : ISystem
{
    public void Update(World world, double dt)
    {
        world.ForEach<FractureGhost, Collider>((Entity e, ref FractureGhost g, ref Collider c) =>
        {
            if (g.Done) return;
            g.Remaining -= (float)dt;
            if (g.Remaining <= 0f)
            {
                if (world.HasComponent<PlayerTag>(e))
                {
                    c.Layer = GameLayers.Player;
                    c.Mask  = GameLayers.Asteroid | GameLayers.Alien;
                }
                else if (world.HasComponent<AlienTag>(e))
                {
                    c.Layer = GameLayers.Alien;
                    c.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
                }
                else
                {
                    c.Layer = GameLayers.Asteroid;
                    c.Mask  = GameLayers.Asteroid | GameLayers.Player;
                }
                g.Done = true;
            }
        });
    }
}

sealed class TimeToLiveSystem : ISystem
{
    private readonly List<Entity> _dead = new();
    public void Update(World world, double dt)
    {
        _dead.Clear();
        foreach (var e in world.QueryEntities<TimeToLive>())
        {
            ref var ttl = ref world.GetComponent<TimeToLive>(e);
            if ((ttl.Remaining -= (float)dt) <= 0f) _dead.Add(e);
        }
        foreach (var e in _dead) world.DestroyEntity(e);
    }
}

sealed class FractureGroupSystem : ISystem
{
    public void Update(World world, double dt)
        => world.ForEach<FractureGroup>((Entity e, ref FractureGroup fg) =>
        {
            if (--fg.FramesLeft <= 0) world.RemoveComponent<FractureGroup>(e);
        });
}

sealed class EventFlushSystem : ISystem
{
    private readonly EventBus _bus;
    public EventFlushSystem(EventBus bus) => _bus = bus;
    public void Update(World world, double dt) => _bus.Flush();
}

sealed class RaycastBulletSystem : ISystem
{
    private readonly EventBus       _bus;
    private readonly ParticleSystem _fx;
    private readonly Random         _rng;
    private readonly List<(Entity e, Vector2 from, Vector2 to)> _seg = new();

    public RaycastBulletSystem(EventBus bus, ParticleSystem fx, Random rng)
    { _bus = bus; _fx = fx; _rng = rng; }

    public void Update(World world, double dt)
    {
        _seg.Clear();
        world.ForEach<Transform, BulletTag>((Entity e, ref Transform t, ref BulletTag _)
            => _seg.Add((e, t.PreviousPosition, t.Position)));

        foreach (var (bullet, from, to) in _seg)
        {
            if (!world.IsAlive(bullet)) continue;
            Vector2 d = to - from;
            if (d.LengthSquared() < 1e-4f) continue;

            float ttl = 0.07f + 0.05f * (float)_rng.NextDouble();
            _fx.Emit(new Particle
            {
                Position = to, Drag = 3f, Life = ttl, MaxLife = ttl,
                Velocity = new Vector2((float)_rng.NextDouble() - 0.5f,
                                       (float)_rng.NextDouble() - 0.5f) * 35f,
                Size0 = 1.6f, Size1 = 0.2f,
                Color0 = new Color(255, 235, 130, 200), Color1 = new Color(255, 110, 40, 0),
            });

            // Alien bullets hit the player; player/grenade bullets hit asteroids and aliens.
            int hitMask = world.HasComponent<AlienBulletTag>(bullet)
                ? GameLayers.Asteroid | GameLayers.Player
                : GameLayers.Asteroid | GameLayers.Alien;
            if (PhysicsQueries.Raycast(world, from, to, hitMask, out var hit))
                _bus.Publish(new BulletHitEvent(hit.Entity, bullet, hit.PartIndex,
                                                hit.Point, Vector2.Normalize(d)));
        }
    }
}

sealed class AlienAiSystem : ISystem
{
    private readonly GameContext _ctx;
    private readonly EventBus    _bus;
    private readonly Random      _rng;

    // Cached per-frame lookups.
    private Entity   _playerEntity;
    private Vector2  _playerPos;
    private bool     _playerAlive;

    // 8-direction context steering offsets.
    private static readonly Vector2[] Dirs8;
    static AlienAiSystem()
    {
        Dirs8 = new Vector2[8];
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.Tau / 8f;
            Dirs8[i] = new Vector2(MathF.Cos(a), MathF.Sin(a));
        }
    }

    public AlienAiSystem(GameContext ctx, EventBus bus, Random rng)
    { _ctx = ctx; _bus = bus; _rng = rng; }

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;

        // Locate player for this frame.
        _playerAlive  = false;
        _playerEntity = default;
        _playerPos    = Vector2.Zero;
        world.ForEach<PlayerTag, Transform>((Entity e, ref PlayerTag _, ref Transform t) =>
        {
            _playerAlive  = true;
            _playerEntity = e;
            _playerPos    = t.Position;
        });

        // Collect asteroid positions for context steering danger map.
        var asteroidPos = new List<Vector2>();
        world.ForEach<AsteroidTag, Transform>((Entity _, ref AsteroidTag _, ref Transform t)
            => asteroidPos.Add(t.Position));

        // Run AI for each alien.
        var toFire = new List<(Entity alien, string variantKey, Vector2 dir, Vector2 muzzle)>();
        world.ForEach<AlienTag, AlienVariant, Transform>(
            (Entity e, ref AlienTag _, ref AlienVariant av, ref Transform t) =>
        {
            if (!_ctx.Config.Entities.TryGetValue(av.Key, out var cfg)) return;
            if (av.Key == "mothership") return; // drift movement handled by boss skill system

            Vector2 pos       = t.Position;
            Vector2 facingDir = new Vector2(MathF.Cos(t.Rotation - MathF.PI * 0.5f),
                                            MathF.Sin(t.Rotation - MathF.PI * 0.5f));

            // Choose thrust direction by alien type.
            Vector2 thrustDir;
            if (av.Key == "bruiser")
            {
                // Direct pursuit: thrust toward player.
                thrustDir = _playerAlive ? Vector2.Normalize(_playerPos - pos) : facingDir;
            }
            else
            {
                // Drone: context steering — interest toward player, danger away from asteroids/bounds.
                thrustDir = _playerAlive
                    ? DroneSteering(pos, asteroidPos, cfg)
                    : facingDir;
            }

            // Lateral thrust penalty: forward component full, lateral component reduced.
            ApplyLateralPenalty(ref thrustDir, facingDir, cfg.LateralThrustPenaltyMult);

            // Proportional alien degradation: thrust scales with alive propeller fraction.
            float propFrac = AlivePropellerFraction(world, e);
            float thrust   = cfg.Thrust * propFrac;
            if (thrust > 0f && thrustDir.LengthSquared() > 1e-6f)
                PhysicsSystem.ApplyForce(world, e, Vector2.Normalize(thrustDir) * thrust);

            // Rotate to face player.
            if (_playerAlive && world.HasComponent<Velocity>(e))
            {
                Vector2 targetDir = Vector2.Normalize(_playerPos - pos);
                float targetAngle = MathF.Atan2(targetDir.Y, targetDir.X) + MathF.PI * 0.5f;
                float diff        = NormalizeAngle(targetAngle - t.Rotation);
                float rotSpeed    = av.Key == "bruiser" ? 2.5f : 4f;
                ref var vel = ref world.GetComponent<Velocity>(e);
                vel.Angular += diff * rotSpeed * fdt;
                vel.Angular *= MathF.Exp(-5f * fdt);
            }

            // Fire when player is in range and cooldown allows.
            if (!world.HasComponent<ShootCooldown>(e)) return;
            ref var cd = ref world.GetComponent<ShootCooldown>(e);
            if (cd.Remaining > 0f) { cd.Remaining = MathF.Max(0f, cd.Remaining - fdt); return; }
            if (!_playerAlive) return;

            float distSq = (pos - _playerPos).LengthSquared();
            if (distSq > cfg.DetectionRadius * cfg.DetectionRadius) return;

            float aliveWeaponFrac = AliveWeaponFraction(world, e);
            if (aliveWeaponFrac <= 0f) return;

            cd.Remaining = cfg.ShootCooldown / MathF.Max(0.01f, aliveWeaponFrac);
            Vector2 aimDir = Vector2.Normalize(_playerPos - pos);
            toFire.Add((e, av.Key, aimDir, pos + aimDir * 30f));
        });

        // Spawn shots outside the ForEach loop.
        foreach (var (alien, varKey, dir, muzzle) in toFire)
            FireAlienWeapon(world, alien, varKey, dir, muzzle);
    }

    private Vector2 DroneSteering(Vector2 pos, List<Vector2> asteroids, EntityConfig cfg)
    {
        Span<float> interest = stackalloc float[8];
        Span<float> danger   = stackalloc float[8];

        Vector2 toPlayer   = _playerPos - pos;
        float distToPlayer = toPlayer.Length();
        Vector2 playerDir  = distToPlayer > 1f ? toPlayer / distToPlayer : Dirs8[0];

        float sw = cfg.SteeringWeights?.Pursuit    ?? 1f;
        float av = cfg.SteeringWeights?.Avoidance  ?? 1f;
        float sp = cfg.SteeringWeights?.Separation ?? 1f;

        for (int i = 0; i < 8; i++)
            interest[i] = MathF.Max(0f, Vector2.Dot(Dirs8[i], playerDir)) * sw;

        foreach (var ap in asteroids)
        {
            Vector2 toAst = ap - pos;
            float   distA = toAst.Length();
            if (distA < 1f || distA > 350f) continue;
            Vector2 astDir = toAst / distA;
            float weight = av * (1f - distA / 350f);
            for (int i = 0; i < 8; i++)
                danger[i] += MathF.Max(0f, Vector2.Dot(Dirs8[i], astDir)) * weight;
        }

        var wc    = _ctx.Config.World;
        float bDist = 300f;
        AddBoundaryDanger(danger, pos, new Vector2(0f,       pos.Y),     sp, bDist);
        AddBoundaryDanger(danger, pos, new Vector2(wc.Width, pos.Y),     sp, bDist);
        AddBoundaryDanger(danger, pos, new Vector2(pos.X,    0f),        sp, bDist);
        AddBoundaryDanger(danger, pos, new Vector2(pos.X,    wc.Height), sp, bDist);

        int best = 0; float bestVal = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            float val = interest[i] - danger[i];
            if (val > bestVal) { bestVal = val; best = i; }
        }
        return Dirs8[best];
    }

    private static void AddBoundaryDanger(Span<float> danger, Vector2 pos, Vector2 boundaryPt,
        float weight, float maxDist)
    {
        Vector2 toBound = boundaryPt - pos;
        float dist = toBound.Length();
        if (dist < 1f || dist > maxDist) return;
        Vector2 dir = toBound / dist;
        float w = weight * (1f - dist / maxDist);
        for (int i = 0; i < 8; i++)
            danger[i] += MathF.Max(0f, Vector2.Dot(Dirs8[i], dir)) * w;
    }

    private static float AlivePropellerFraction(World world, Entity e)
    {
        if (!world.HasComponent<FracturableBody>(e)) return 1f;
        ref var fb = ref world.GetComponent<FracturableBody>(e);
        bool[]? pulv = world.HasComponent<FractureProcess>(e)
            ? world.GetComponent<FractureProcess>(e).Pulverized : null;
        int total = 0, alive = 0;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (fb.Cells[i].Role != "propeller") continue;
            total++;
            if (pulv == null || !pulv[i]) alive++;
        }
        return total == 0 ? 1f : (float)alive / total;
    }

    private static float AliveWeaponFraction(World world, Entity e)
    {
        if (!world.HasComponent<FracturableBody>(e)) return 0f;
        ref var fb = ref world.GetComponent<FracturableBody>(e);
        bool[]? pulv = world.HasComponent<FractureProcess>(e)
            ? world.GetComponent<FractureProcess>(e).Pulverized : null;
        int total = 0, alive = 0;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            string? r = fb.Cells[i].Role;
            if (r is not ("cannon" or "shotgun" or "piercing" or "grenade")) continue;
            total++;
            if (pulv == null || !pulv[i]) alive++;
        }
        return total == 0 ? 0f : (float)alive / total;
    }

    private static void ApplyLateralPenalty(ref Vector2 thrustDir, Vector2 facing, float penaltyMult)
    {
        if (thrustDir.LengthSquared() < 1e-6f) return;
        float fwdComp = Vector2.Dot(thrustDir, facing);
        Vector2 fwd   = facing * fwdComp;
        Vector2 lat   = thrustDir - fwd;
        thrustDir = fwd + lat * penaltyMult;
    }

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI)  a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    private void FireAlienWeapon(World world, Entity alien, string varKey, Vector2 dir, Vector2 muzzle)
    {
        string weaponKey = varKey == "bruiser" ? "shotgun" : "cannon";
        if (!_ctx.Config.Weapons.TryGetValue(weaponKey, out var wcfg)) return;

        if (weaponKey == "cannon")
        {
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
            world.AddComponent(b, new Velocity { Linear = dir * wcfg.ProjectileSpeed });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new AlienBulletTag());
            world.AddComponent(b, new BulletVisual { Color = new Color(220, 80, 80) });
            world.AddComponent(b, new BulletData { WeaponKey = "cannon", Energy = wcfg.Energy });
            world.AddComponent(b, new TimeToLive { Remaining = wcfg.TimeToLive });
        }
        else  // shotgun
        {
            int   rays = wcfg.Rays ?? 7;
            float half = (wcfg.ConeAngle ?? 18f) * 0.5f * MathF.PI / 180f;
            float step = rays > 1 ? half * 2f / (rays - 1) : 0f;
            float baseA = MathF.Atan2(dir.Y, dir.X) - half;
            float rayE  = wcfg.EnergyPerRay ?? wcfg.Energy / MathF.Max(1f, rays);
            for (int i = 0; i < rays; i++)
            {
                float ang = baseA + step * i;
                Vector2 d = new(MathF.Cos(ang), MathF.Sin(ang));
                var b = world.CreateEntity();
                world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
                world.AddComponent(b, new Velocity { Linear = d * wcfg.ProjectileSpeed });
                world.AddComponent(b, new BulletTag());
                world.AddComponent(b, new AlienBulletTag());
                world.AddComponent(b, new BulletVisual { Color = new Color(220, 100, 60) });
                world.AddComponent(b, new BulletData { WeaponKey = "shotgun", Energy = rayE });
                world.AddComponent(b, new TimeToLive { Remaining = wcfg.TimeToLive });
            }
        }
    }
}

sealed class BlackHoleSystem : ISystem
{
    public void Update(World world, double dt)
    {
        var holes = new List<(Vector2 pos, float radius, float strength)>();
        world.ForEach<Transform, BlackHoleTag>((Entity _, ref Transform t, ref BlackHoleTag bh) =>
            holes.Add((t.Position, bh.Radius, bh.Strength)));
        if (holes.Count == 0) return;

        float fdt = (float)dt;
        world.ForEach<Transform, Velocity, RigidBody>((Entity _, ref Transform t, ref Velocity v, ref RigidBody rb) =>
        {
            foreach (var (hpos, radius, strength) in holes)
            {
                Vector2 delta = hpos - t.Position;
                float   dSq   = delta.LengthSquared();
                if (dSq < 1f || dSq > radius * radius) continue;
                float dist  = MathF.Sqrt(dSq);
                float accel = strength / ((dist + 1f) * MathF.Max(rb.Mass, 0.1f));
                v.Linear += delta / dist * accel * fdt;
            }
        });
    }
}

sealed class GrenadeSystem : ISystem
{
    private readonly EventBus _bus;
    public GrenadeSystem(EventBus bus) { _bus = bus; }

    public void Update(World world, double dt)
    {
        world.ForEach<Transform, GrenadeFuse>((Entity e, ref Transform t, ref GrenadeFuse f) =>
        {
            if (f.Remaining <= 0f) return;
            f.Remaining -= (float)dt;
            if (f.Remaining <= 0f)
                _bus.Publish(new GrenadeDetonateEvent(e, t.Position, f.WeaponKey));
        });
    }
}

// ── Game events ───────────────────────────────────────────────────────────────

readonly struct BulletHitEvent
{
    public readonly Entity  Target, Bullet;
    public readonly int     StruckCell;
    public readonly Vector2 Point, ShotDir;
    public BulletHitEvent(Entity target, Entity bullet, int cell, Vector2 point, Vector2 shotDir)
    { Target = target; Bullet = bullet; StruckCell = cell; Point = point; ShotDir = shotDir; }
}

readonly struct GrenadeDetonateEvent
{
    public readonly Entity  Grenade;
    public readonly Vector2 WorldPos;
    public readonly string  WeaponKey;
    public GrenadeDetonateEvent(Entity g, Vector2 pos, string key)
    { Grenade = g; WorldPos = pos; WeaponKey = key; }
}
