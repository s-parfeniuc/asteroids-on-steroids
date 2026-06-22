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
    private bool  _mothershpSpawned = false;

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

        _systems =
        [
            new PreviousStateSystem(),
            new PlayerControlSystem(ctx, _camera),
            new PhysicsSystem(),
            new VortexSystem(worldCenter, ctx.Config.Vortex),
            new MovementSystem(),
            new BorderDampSystem(wc.Width, wc.Height),
            new RaycastBulletSystem(_bus, _fx, _rng),
            new GhostSystem(),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            new FractureCrackSystem(_bus, _rng),
            new FractureGroupSystem(),
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
            // TODO: AlienAiSystem
            // TODO: SkillSystem
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

        _ctx.CellBudget.Reset();
        _gameTime     = 0f;
        _waveTimer    = 0f;
        _pendingWave  = false;
        _mothershpSpawned = false;

        SpawnPlayer();
        SpawnNextWave();
    }

    public void Exit()
    {
        _ctx.CellBudget.Reset();
    }

    public IGameState? Update(double dt)
    {
        foreach (var s in _systems)
        {
            if (s is PlayerControlSystem pcs) pcs.Player = _player;
            s.Update(_world, dt);
        }
        _world.FlushDeferred();
        _fx.Update((float)dt);

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

        // TODO: Transition to GameOverState when player ship core is destroyed.
        return null;
    }

    public void Draw(IRenderer r, float alpha)
    {
        r.Begin(new Color(8, 9, 14));

        r.PushTransform(_camera.GetViewMatrix());
        DrawAsteroids(r, alpha);
        DrawDebris(r, alpha);
        _fx.Draw(r);
        DrawBullets(r, alpha);
        DrawPlayer(r, alpha);
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
        var bias = new Dictionary<string, float>();
        foreach (var (key, entry) in ws.SpawnBias)
        {
            float t01 = entry.T1 > entry.T0
                ? Math.Clamp((_gameTime - entry.T0) / (entry.T1 - entry.T0), 0f, 1f)
                : (_gameTime >= entry.T0 ? 1f : 0f);
            float w = entry.W0 + (entry.W1 - entry.W0) * t01;
            // Only include asteroid keys here; alien keys are handled in Step 8.
            if (w > 0f && _ctx.Config.Asteroids.ContainsKey(key))
                bias[key] = w;
        }

        var spawns = ChooseAsteroids(budget, bias, sizeBias, cellCap);

        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : new Vector2(wc.Width / 2f, wc.Height / 2f);

        var placed = new List<(Vector2 pos, float r)>();
        foreach (var (key, sizeMult) in spawns)
        {
            float r = _ctx.Config.Asteroids.TryGetValue(key, out var ac) && ac.Procedural != null
                ? ac.Procedural.BaseRadius * sizeMult : 80f * sizeMult;
            Vector2 pos = FindSpawnPosition(r, placed, playerPos);
            placed.Add((pos, r));
            SpawnAsteroid(pos, key, sizeMult);
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
        _world.AddComponent(_player, new RigidBody
        {
            Mass = 12f, Inertia = 0f,
            LinearDrag = 1.2f, AngularDrag = 2f,
            Restitution = 0.2f, Friction = 0.1f,
        });
        _world.AddComponent(_player, new Collider
        {
            // TODO: replace with compound shape built from player_ship ShapeData.
            Shape = new CircleShape(18f),
            Layer = GameLayers.Player,
            Mask  = GameLayers.Asteroid | GameLayers.Alien,
        });
        _world.AddComponent(_player, new AimComponent { Dir = Vector2.UnitX });
        _world.AddComponent(_player, new ShootCooldown());
        _world.AddComponent(_player, new ActiveWeapon { Key = _ctx.Config.Player.StartingWeapon });
        _world.AddComponent(_player, new SkillState());
        _world.AddComponent(_player, new PlayerTag());
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

    private void OnBulletHit(BulletHitEvent ev)
    {
        if (!_world.IsAlive(ev.Target) || !_world.IsAlive(ev.Bullet)) return;
        Vector2 bulletVel = _world.GetComponent<Velocity>(ev.Bullet).Linear;
        _world.DestroyEntity(ev.Bullet);

        string weaponKey = _world.HasComponent<BulletData>(ev.Bullet)
            ? _world.GetComponent<BulletData>(ev.Bullet).WeaponKey
            : _ctx.Config.Player.StartingWeapon;

        if (!_ctx.Config.Weapons.TryGetValue(weaponKey, out var weaponCfg)) return;
        WeaponProfile profile = weaponCfg.ToWeaponProfile();
        float bulletMass = weaponCfg.Energy / MathF.Max(1f, bulletVel.LengthSquared()) * 2f;

        EmitFlash(ev.Point, 0.5f * bulletMass * bulletVel.LengthSquared());

        FractureService.BeginFracture(
            _world, ev.Target, ev.StruckCell,
            ev.Point, ev.ShotDir, bulletVel, bulletMass,
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
        FractureService.BeginFracture(_world, eA, -1, cp, dirAB,
            vRel + vA.Linear, mB, weapon, FractureTiming.Default, _rng);
        FractureService.BeginFracture(_world, eB, -1, cp, -dirAB,
            -vRel + vB.Linear, mA, weapon, FractureTiming.Default, _rng);
    }

    private void OnCellPulverized(CellPulverizedEvent ev)
    {
        _ctx.CellBudget.Remove(1);

        // TODO: award score for vaporised cells (area × material.Density).

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
        BodyColor color = GetBodyColor(ev.Body);
        var fg = MakeFractureGroup(ev.Body);
        foreach (var f in ev.Fragments)
        {
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnFracturableBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
            CopyTags(ev.Body, ne);
        }
        _world.DestroyEntity(ev.Body);
    }

    private void OnFractureSplit(FractureSplitEvent ev)
    {
        _activeCollisions.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        BodyColor color = GetBodyColor(ev.Body);
        var fg = MakeFractureGroup(ev.Body);
        foreach (var p in ev.Pieces)
        {
            var f = p.Spec;
            if (f.IsDebris) { EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnFracturableBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, fg);
            CopyTags(ev.Body, ne);
            if (p.Process.HasValue) _world.AddComponent(ne, p.Process.Value);
        }
        _world.DestroyEntity(ev.Body);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private readonly List<Vector2> _meshVerts = new();
    private readonly List<int>     _meshLens  = new();
    private readonly List<Vector2> _dbuf      = new();

    private void DrawAsteroids(IRenderer r, float alpha)
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

    private void DrawPlayer(IRenderer r, float alpha)
    {
        if (!_world.IsAlive(_player)) return;
        _world.ForEach<Transform, PlayerTag, AimComponent>(
            (Entity _, ref Transform t, ref PlayerTag _, ref AimComponent aim) =>
        {
            var (p, _) = Interp(t, alpha);
            r.FillCircle(p, 18f, new Color(70, 130, 240));
            r.DrawCircle(p, 18f, new Color(170, 205, 255), 2f);
            r.DrawLine(p, p + aim.Dir * 28f, new Color(170, 205, 255), 2f);
        });
    }

    private void DrawHud(IRenderer r)
    {
        var font = new FontSpec("monospace", 14f);
        int s = (int)_gameTime;
        r.DrawText(
            $"{s / 60:00}:{s % 60:00}   Score {_ctx.Score.Total:F0}   Cells {_ctx.CellBudget.Count}",
            new Vector2(12, 10), new Color(190, 200, 220), font);
        r.DrawText(
            "WASD move   Mouse aim   Click fire   Esc quit",
            new Vector2(12, _ctx.ScreenH - 22f), new Color(80, 100, 130), font);
    }

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

    public PlayerControlSystem(GameContext ctx, Camera camera) { _ctx = ctx; _camera = camera; }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(Player)) return;

        ref var t   = ref world.GetComponent<Transform>(Player);
        ref var aim = ref world.GetComponent<AimComponent>(Player);
        ref var cd  = ref world.GetComponent<ShootCooldown>(Player);
        ref var wep = ref world.GetComponent<ActiveWeapon>(Player);

        // Movement
        Vector2 a = Vector2.Zero;
        var inp = _ctx.Input;
        if (inp.IsHeld(KeyCode.W)) a.Y -= 1; if (inp.IsHeld(KeyCode.S)) a.Y += 1;
        if (inp.IsHeld(KeyCode.A)) a.X -= 1; if (inp.IsHeld(KeyCode.D)) a.X += 1;
        if (a != Vector2.Zero)
        {
            float thrust = _ctx.Config.Player.Thrust;
            PhysicsSystem.ApplyForce(world, Player, Vector2.Normalize(a) * thrust);
        }

        // Aim toward mouse — convert screen position to world position via camera.
        Vector2 mouseWorld = _camera.ScreenToWorld(inp.MouseScreen);
        Vector2 toMouse    = mouseWorld - t.Position;
        if (toMouse.LengthSquared() > 1f) aim.Dir = Vector2.Normalize(toMouse);

        // Shoot
        if (cd.Remaining > 0f) cd.Remaining -= (float)dt;
        if (inp.IsMouseLeft && cd.Remaining <= 0f && _ctx.Config.Weapons.TryGetValue(wep.Key, out var wcfg))
        {
            cd.Remaining = 1f / wcfg.FireRate;
            Vector2 muzzle = t.Position + aim.Dir * 24f;
            var b = world.CreateEntity();
            world.AddComponent(b, new Transform { Position = muzzle, PreviousPosition = muzzle });
            world.AddComponent(b, new Velocity { Linear = aim.Dir * wcfg.ProjectileSpeed });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = new Color(255, 230, 90) });
            world.AddComponent(b, new BulletData { WeaponKey = wep.Key, Energy = wcfg.Energy });
            world.AddComponent(b, new TimeToLive { Remaining = wcfg.TimeToLive });
        }

        // TODO: skill activation (Q = dash, E = turbo, R = slow-mo)
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
        world.ForEach<FractureGhost, Collider>((Entity _, ref FractureGhost g, ref Collider c) =>
        {
            if (g.Done) return;
            g.Remaining -= (float)dt;
            if (g.Remaining <= 0f)
            {
                c.Layer = GameLayers.Asteroid;
                c.Mask  = GameLayers.Asteroid | GameLayers.Player;
                g.Done  = true;
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

            int hitMask = GameLayers.Asteroid | GameLayers.Alien;
            if (PhysicsQueries.Raycast(world, from, to, hitMask, out var hit))
                _bus.Publish(new BulletHitEvent(hit.Entity, bullet, hit.PartIndex,
                                                hit.Point, Vector2.Normalize(d)));
        }
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
