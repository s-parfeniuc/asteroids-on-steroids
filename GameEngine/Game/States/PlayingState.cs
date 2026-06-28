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
using AsteroidsGame.Gameplay;

namespace AsteroidsGame.States;

public sealed class PlayingState : IGameState
{
    private readonly GameContext _ctx;

    private readonly World          _world  = new();
    private readonly EventBus       _bus    = new();
    private readonly ParticleSystem _fx     = new();
    private readonly ParticleEffects _effects;
    private readonly WorldRenderer  _worldRenderer = new();
    private readonly ISystem[]      _systems;
    private readonly Camera         _camera;
    private readonly Random         _rng;

    private readonly FractureGameplay _fracture;
    // Player entity and game-over flag live in the shared fracture gameplay (control
    // transfers to a cockpit fragment when the ship breaks up). Exposed as properties so
    // existing call sites are unchanged.
    private Entity _player { get => _fracture.Player; set => _fracture.Player = value; }

    // Wave manager state
    private float _gameTime     = 0f;
    private float _waveTimer    = 0f;   // seconds since last wave spawn
    private bool  _pendingWave  = false;
    private float _waveCountdown = 0f;
    private bool  _mothershpSpawned          = false;
    private bool  _mothershpKilled            = false;
    private int   _mothershpGroupId           = 0;
    private int   _mothershpInitialCockpits   = 3;
    private bool  _pendingGameOver { get => _fracture.PendingGameOver; set => _fracture.PendingGameOver = value; }
    private float _bossShockwaveCd            = 0f;
    private float _bossBlackHoleCd            = 0f;
    private float _bossRamChargeCd            = 0f;
    private float _bossRamChargeActive        = 0f;
    private bool  _bossOverdriveTriggered     = false;


    private const double FixedDt = 1.0 / 120.0;

    // ── Spawn position constants ──────────────────────────────────────────────
    private const float BorderZone = 400f;
    private const float ViewMargin = 100f;

    // ── Construction ─────────────────────────────────────────────────────────

    public PlayingState(GameContext ctx, int startWave = 1)
    {
        _ctx = ctx;
        _rng = new Random(ctx.Rng.Next());
        _effects = new ParticleEffects(_world, _fx, ctx.Config.Vfx, _rng);
        _fracture = new FractureGameplay(_world, _bus, _ctx, _rng, _effects);

        var wc = ctx.Config.World;
        _camera = new Camera(ctx.ScreenW, ctx.ScreenH)
        {
            Position = new Vector2(wc.Width / 2f, wc.Height / 2f),
        };

        var worldCenter = new Vector2(wc.Width / 2f, wc.Height / 2f);

        var pcs = new PlayerControlSystem(ctx, _camera);
        pcs.OnPiercingFire = (from, dir) => PiercingPrefab.Spawn(_world, _ctx, from, dir, _rng);
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
            new StressRelaxSystem(),
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
        ];
    }

    // ── IGameState ────────────────────────────────────────────────────────────

    public void Enter()
    {
        // Fracture-response events are subscribed by the shared FractureGameplay (ctor).
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
        _worldRenderer.Draw(r, _world, _camera, _fx, _player, _ctx.Config.Vfx, alpha);
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
                if (AsteroidPrefab.CellsFor(_ctx, ac, minMult) > remCells) continue;
                candidates.Add((key, ac, w));
            }
            if (candidates.Count == 0) break;

            float total = candidates.Sum(c => c.w);
            float pick  = (float)_rng.NextDouble() * total;
            float cum   = 0f;
            (string chosenKey, AsteroidConfig chosenAc, float _) = candidates[0];
            foreach (var c in candidates) { cum += c.w; if (pick <= cum) { chosenKey = c.key; chosenAc = c.ac; break; } }

            float maxByBudget = remBudget / chosenAc.BaseCost;
            float kUnit       = AsteroidPrefab.CellsFor(_ctx, chosenAc, 1f);
            float maxByCells  = MathF.Sqrt(remCells / kUnit);
            float maxMult     = Math.Min(chosenAc.SizeRange[1], Math.Min(maxByBudget, maxByCells));
            float minMult0    = chosenAc.SizeRange[0];
            if (maxMult < minMult0) break;

            float u        = (float)_rng.NextDouble();
            float sizeMult = minMult0 + MathF.Pow(u, alpha) * (maxMult - minMult0);

            result.Add((chosenKey, sizeMult));
            remBudget -= chosenAc.BaseCost * sizeMult;
            remCells  -= AsteroidPrefab.CellsFor(_ctx, chosenAc, sizeMult);
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
        => AsteroidPrefab.Spawn(_world, _ctx, _rng, pos, typeKey, sizeMult);

    private void SpawnAlien(Vector2 pos, string typeKey)
        => AlienPrefab.Spawn(_world, _ctx, _rng, pos, typeKey);

    private void SpawnMothership()
    {
        if (!_ctx.Config.Entities.TryGetValue("mothership", out var ec)) return;
        if (!_ctx.Shapes.TryGetValue(ec.Shape, out var sd)) return;
        var mat = _ctx.Config.ResolveMaterial(ec.Material, sd);   // shape owns it; config overrides

        float sc    = ec.ShapeScale;
        var outline = sd.Outline.Select(xy => new Vector2(xy[0] * sc, xy[1] * sc)).ToList();
        var seedPos = sd.Seeds.Select(s => new Vector2(s.X * sc, s.Y * sc)).ToList();
        var seedMlt = sd.Seeds.Select(s => s.BondMult).ToList();
        var body    = VoronoiTessellator.BuildFromExplicitSeeds(outline, seedPos, seedMlt, mat, _rng);
        FractureBodyFactory.ApplyShapeSeeds(body, sd.Seeds, sc);

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

        _mothershpGroupId = _fracture.AllocateGroupId();
        _world.AddComponent(e, new AlienTag());
        _world.AddComponent(e, new AlienVariant { Key = "mothership" });
        _world.AddComponent(e, new ShootCooldown { Remaining = 999f });
        _world.AddComponent(e, new MothershpId  { Id = _mothershpGroupId, InitialCockpitCount = _mothershpInitialCockpits });
        _world.AddComponent(e, new SpawnerAccumulator { Value = 0f });
        _world.AddComponent(e, new VortexResponse { CentripetalMult = 1f, TangentialMult = 1f });
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

    // ── Player spawning ───────────────────────────────────────────────────────

    private void SpawnPlayer()
    {
        _player = PlayerPrefab.Spawn(_world, _ctx, _rng);
    }

    // ── Generic body spawning ─────────────────────────────────────────────────

    private Entity SpawnFracturableBody(
        FracturableBody body, Vector2 pos, float rot,
        Vector2 vel, float spin, BodyColor color, bool ghost = false)
    {
        float area    = VoronoiTessellator.TotalArea(body);
        float mass    = MathF.Max(1f, body.Material.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);
        return FractureBodyFactory.Spawn(_world, _ctx.Config.Physics, body, pos, rot,
            vel, spin, mass, inertia, color, ghost, ghostRemaining: 0.04f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int CountLiveCells()
    {
        int total = 0;
        _world.ForEach<AsteroidTag, FracturableBody>(
            (Entity _, ref AsteroidTag _, ref FracturableBody fb) => total += fb.Cells.Length);
        return total;
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
        _effects.EmitFlash(center, bc.ShockwaveStrength * 0.005f);
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
}
