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
    private readonly VortexSystem   _vortex;   // held for the environment overlay (pull visual)
    private readonly VortexFx       _vortexFx = new();
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
    private bool[] _specialFired              = System.Array.Empty<bool>();
    private int   _mothershpGroupId           = 0;
    private int   _mothershpInitialCockpits   = 3;
    private bool  _pendingGameOver { get => _fracture.PendingGameOver; set => _fracture.PendingGameOver = value; }

    // Game-over overlay: the run ends but the state stays live — the world keeps simulating
    // underneath while score/timer/waves freeze and the verdict fades in over the playfield.
    private bool  _gameOverActive;
    private float _deadTimer;          // seconds since death
    private bool  _gameOverWon;
    private bool  _newBest;
    private float _finalScore;
    private const float GameOverFadeIn = 1.6f;   // scrim ramp before the headline appears
    private const float GameOverTextIn = 0.6f;   // headline/hint fade after that

    // Wave / boss announcement banner.
    private int    _waveNumber;
    private string _bannerText  = "";
    private float  _bannerTimer;
    private float  _bannerMax;
    private bool   _bannerBoss;
    private const float WaveBannerTime = 2.2f;
    private const float BossBannerTime = 4.0f;

    private void ShowBanner(string text, float dur, bool boss = false)
    {
        _bannerText = text; _bannerTimer = dur; _bannerMax = dur; _bannerBoss = boss;
    }


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
            ShakeIntensity = ctx.ShakeIntensity,   // from settings
        };

        var worldCenter = new Vector2(wc.Width / 2f, wc.Height / 2f);

        // Screen-shake feedback: a jolt when the player is hit, a lighter kick per destroyed cell,
        // and a solid thump on grenade detonations.
        _bus.Subscribe<CellPulverizedEvent>(OnPulverizeShake);
        _bus.Subscribe<GrenadeDetonateEvent>(_ => { _camera.AddTrauma(0.5f); AddHitstop(_ctx.Config.Vfx.HitstopGrenade); });

        var pcs = new PlayerControlSystem(ctx, _camera);
        pcs.OnPiercingFire = (from, dir) => PiercingPrefab.Spawn(_world, _ctx, from, dir, _rng);
        _vortex = new VortexSystem(worldCenter, ctx.Config.World.Width, ctx.Config.World.Height, ctx.Config.Vortex);
        _systems =
        [
            new PreviousStateSystem(),
            pcs,
            new AlienAiSystem(ctx, _bus, _rng),
            new BossSystem(ctx, _effects, _camera, _rng, () => _fracture.Player),
            new PhysicsSystem(),
            _vortex,
            new MovementSystem(),
            new BorderHazardSystem(wc.Width, wc.Height, ctx.Config.BorderHazard, _rng, _effects),
            new RaycastBulletSystem(_bus, _fx, _rng),
            new GrenadeSystem(_world, _bus),
            new ProjectileSystem(_world, ctx, _bus, _rng, () => _fracture.Player),
            new BlackHoleSystem(),
            new GhostSystem(),
            new CollisionSystem(new SpatialGrid(160f), _bus) { ResolveOverlap = true, EnableSleeping = false },
            // Flush the frame's IMPACTS (bullet / collision / piercing / grenade) BEFORE the crack
            // system, so every hit seeds its front on a live body and is advanced this same frame.
            // Flushed afterwards, a hit landing on the frame its target split or finalised would be
            // handed a body already marked Done and destined for destruction — and simply vanish.
            new EventFlushSystem(_bus),
            new FractureCrackSystem(_bus, _rng),
            new FractureGroupSystem(),
            new StressRelaxSystem(),
            // …then flush the crack system's OUTPUT (pulverise / split / complete) so fragments spawn.
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
        ];
    }

    private void OnPulverizeShake(CellPulverizedEvent ev)
    {
        var vfx = _ctx.Config.Vfx;
        bool isPlayer = _world.IsAlive(_fracture.Player) && ev.Body == _fracture.Player;
        _camera.AddTrauma(isPlayer ? 0.4f : MathF.Min(0.12f, ev.Area * 0.00015f));

        if (isPlayer) AddHitstop(vfx.HitstopPlayerHit);
        else if (ev.Area >= vfx.HitstopBigArea) AddHitstop(vfx.HitstopBigFracture);

        // Score popup (aggregated): show the ACTUAL points awarded (FractureGameplay.OnCellPulverized
        // runs first for this event and records it), so popups scale with cellScoreAreaWeight + combo.
        if (!isPlayer && _world.IsAlive(ev.Body) && _world.HasComponent<FracturableBody>(ev.Body))
            QueueScorePopup(_ctx.Score.LastAward, ev.WorldCentroid);
    }

    // Accumulate score into a single popup so rapid hits merge instead of spamming per-cell numbers.
    private void QueueScorePopup(float value, Vector2 pos)
    {
        if (value <= 0f) return;
        _popPos   = _popAccum > 0f ? (_popPos * _popAccum + pos * value) / (_popAccum + value) : pos;
        _popAccum += value;
        _popTimer = _ctx.Config.Vfx.PopupFlushWindow;
        if (_popAccum >= _ctx.Config.Vfx.PopupMinValue) FlushPopup();
    }

    private void FlushPopup()
    {
        if (_popAccum <= 0f) return;
        var vfx = _ctx.Config.Vfx;
        float t    = Math.Clamp(_popAccum / MathF.Max(1f, vfx.PopupRefValue), 0f, 1f);
        float size = vfx.PopupMinSize + (vfx.PopupMaxSize - vfx.PopupMinSize) * t;
        float ttl  = vfx.PopupTtl * (0.8f + 0.6f * t);
        _popups.Add(new Popup { Pos = _popPos, Value = _popAccum, Remaining = ttl, MaxTtl = ttl, Size = size });
        _popAccum = 0f; _popTimer = 0f;
    }

    private void UpdatePopups(float dt)
    {
        if (_popAccum > 0f) { _popTimer -= dt; if (_popTimer <= 0f) FlushPopup(); }
        float rise = _ctx.Config.Vfx.PopupRiseSpeed;
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            var p = _popups[i];
            p.Remaining -= dt;
            p.Pos.Y     -= rise * dt;
            if (p.Remaining <= 0f) _popups.RemoveAt(i);
            else _popups[i] = p;
        }
    }

    private void DrawPopups(IRenderer r)
    {
        foreach (var p in _popups)
        {
            float k = Math.Clamp(p.Remaining / MathF.Max(0.01f, p.MaxTtl), 0f, 1f);
            var col = new Color(255, 230, 130, (byte)(230 * k));
            var font = new FontSpec("monospace", p.Size, bold: true);
            string s = $"+{p.Value:F0}";
            Vector2 screen = _camera.WorldToScreen(p.Pos);
            Vector2 half   = r.MeasureText(s, font) * 0.5f;
            r.DrawText(s, screen - half, col, font);
        }
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
        _waveNumber               = 0;
        _bannerTimer              = 0f;
        _gameOverActive           = false;
        _deadTimer                = 0f;
        _gameOverWon              = false;
        _newBest                  = false;
        _finalScore               = 0f;
        _specialFired             = new bool[_ctx.Config.WaveSystem.SpecialWaves.Count];
        _spawnQueue.Clear();
        _campTime                 = 0f;
        _campInZone               = false;
        _vortexFx.Reset();

        SpawnPlayer();
        SpawnNextWave();
        DrainSpawnQueue();   // first wave's due-now bodies appear immediately
    }

    public void Exit()
    {
        _ctx.CellBudget.Reset();
    }

    public IGameState? Update(double dt)
    {
        var input = _ctx.Input;

        // Pause menu (Esc toggles). While paused the simulation is frozen; the menu navigates itself.
        if (input.ConsumePress(KeyCode.Escape)) { _paused = !_paused; _pauseSel = 0; }
        if (_paused)
        {
            if (input.ConsumePress(KeyCode.Up)   || input.ConsumePress(KeyCode.W)) _pauseSel = (_pauseSel + PauseOpts.Length - 1) % PauseOpts.Length;
            if (input.ConsumePress(KeyCode.Down) || input.ConsumePress(KeyCode.S)) _pauseSel = (_pauseSel + 1) % PauseOpts.Length;
            if (input.ConsumePress(KeyCode.Enter) || input.ConsumePress(KeyCode.Space))
            {
                switch (_pauseSel)
                {
                    case 0: _paused = false; break;
                    case 1: return new PlayingState(_ctx);                  // restart
                    case 2: return new MainMenuState(_ctx);                 // to main menu
                }
            }
            return null;   // frozen
        }

        // Hitstop: freeze the simulation for a brief, punchy beat on big impacts; keep shake + popups
        // ticking so the frozen frame still reads as alive.
        if (_hitstop > 0f)
        {
            _hitstop -= (float)dt;
            _camera.UpdateShake((float)dt);
            UpdatePopups((float)dt);
            return null;
        }

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
            // Once the run is over the hulk just drifts — no more steering, thrust or firing.
            if (s is PlayerControlSystem pcs)
            {
                if (_gameOverActive) continue;
                pcs.Player = _player;
            }
            s.Update(_world, gameDt);
        }
        _world.FlushDeferred();
        _fx.Update((float)gameDt);

        // Death ends the run but not the state: from here the world keeps simulating while the
        // score, the clock and the wave director are frozen and the verdict fades in over the field.
        if (!_gameOverActive && (_pendingGameOver || !_world.IsAlive(_player)))
        {
            _gameOverActive  = true;
            _deadTimer       = 0f;
            _gameOverWon     = _mothershpKilled;
            _finalScore      = _ctx.Score.Total;
            _newBest         = _ctx.SubmitScore(_finalScore);   // persists if it beat the record
            _ctx.Score.Frozen = true;                           // no further awards or chain decay
        }

        if (!_gameOverActive) _ctx.Score.Update(dt, _ctx.Config.Scoring.KillChainDecay);

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
        _camera.UpdateShake((float)dt);
        UpdatePopups((float)dt);
        if (_bannerTimer > 0f) _bannerTimer -= (float)dt;

        // Vortex gust motes ride the live field; tick with the simulated clock (so slow-mo slows them
        // too) and keep going under the game-over overlay while the world drifts.
        Vector2 vHalf = new(_ctx.ScreenW * 0.5f / _camera.Zoom, _ctx.ScreenH * 0.5f / _camera.Zoom);
        _vortexFx.Update((float)gameDt, _vortex.Centre, _vortex.FieldVelocity, _ctx.Config.VortexFx,
                         _camera.Position - vHalf, _camera.Position + vHalf);

        // Run over: clock/waves stay frozen; only the overlay ticks. Leave once it's fully up.
        if (_gameOverActive)
        {
            _deadTimer += (float)dt;
            if (_deadTimer >= GameOverFadeIn &&
                (input.ConsumePress(KeyCode.Space) || input.ConsumePress(KeyCode.Enter)))
            {
                _ctx.Score.Reset();
                _ctx.CellBudget.Reset();
                return new MainMenuState(_ctx);
            }
            return null;
        }

        // Wave manager
        _gameTime  += (float)dt;
        _waveTimer += (float)dt;

        DrainSpawnQueue();   // release queued wave bodies whose time has come (SpawnDuration trickle)
        UpdateCampingResponse((float)dt);

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

        // Special scripted waves (one-shot time gates, own weights/budget/banner).
        for (int i = 0; i < ws.SpecialWaves.Count && i < _specialFired.Length; i++)
        {
            if (_specialFired[i] || _gameTime < ws.SpecialWaves[i].TriggerTime) continue;
            _specialFired[i] = true;
            FireSpecialWave(ws.SpecialWaves[i]);
        }

        // Mothership spawn (one-shot time gate)
        if (!_mothershpSpawned && _gameTime >= ws.MothershpSpawnTime)
        {
            SpawnMothership();
            _mothershpSpawned = true;
        }
        // Win when no cockpit-bearing boss fragment remains (BossSystem drives skills/movement now).
        if (_mothershpSpawned && !_mothershpKilled)
        {
            bool anyBoss = false;
            _world.ForEach<BossBrain>((Entity _, ref BossBrain _) => anyBoss = true);
            if (!anyBoss) _mothershpKilled = true;
        }

        return null;
    }

    public void Draw(IRenderer r, float alpha)
    {
        r.Begin(new Color(8, 9, 14));
        _worldRenderer.Draw(r, _world, _camera, _fx, _player, _ctx.Config.Vfx, alpha);

        // Vortex gust streaks (world space), then the swirl warp (screen space, reads the frame so far).
        r.PushTransform(_camera.GetViewMatrix());
        _vortexFx.DrawStreaks(r, _vortex.Centre, _ctx.Config.VortexFx);
        r.PopTransform();
        var post = r as IPostEffects;
        _vortexFx.DrawWarp(post, _camera, _vortex.Centre, _ctx.Config.VortexFx);
        DrawBorderCue(r, post);
        if (!_gameOverActive) DrawCampCue(r);

        DrawPopups(r);
        DrawBanner(r);
        DrawHud(r);
        if (_gameOverActive) DrawGameOverOverlay(r);
        if (_paused) DrawPauseMenu(r);
        r.End();
    }

    /// <summary>
    /// The border rim's cues. Primary: a full-screen red tint that deepens as the PLAYER goes into
    /// the rim (this is the signal the player reads). Secondary: a subtle heat-haze warp on whichever
    /// world edges are currently on-screen — thematic, and self-gated (edges are only visible when the
    /// camera is near a border, i.e. exactly when it matters).
    /// </summary>
    private void DrawBorderCue(IRenderer r, IPostEffects? post)
    {
        var hz = _ctx.Config.BorderHazard;
        if (!hz.Enabled || hz.HazardZone <= 1f) return;
        var wc = _ctx.Config.World;
        float zone = hz.HazardZone;

        // ── Secondary: heat-haze shimmer on the on-screen world edges ────────────
        if (post != null && hz.WarpEnabled && hz.WarpStrength > 0.5f)
        {
            float amp   = hz.WarpStrength * _camera.Zoom;
            float phase = _gameTime * hz.WarpSpeed;
            float zonePx = zone * _camera.Zoom;
            float sw = _ctx.ScreenW, sh = _ctx.ScreenH;

            // Vertical edges (left x=0, right x=W): a vertical strip, displaced horizontally.
            void VEdge(float edgeWorldX, float inwardSign)
            {
                float sEdge  = _camera.WorldToScreen(new Vector2(edgeWorldX, _camera.Position.Y)).X;
                float sInner = sEdge + inwardSign * zonePx;
                float x0 = MathF.Min(sEdge, sInner), x1 = MathF.Max(sEdge, sInner);
                if (x1 <= 0f || x0 >= sw) return;   // off-screen
                post.Distort(new Vector2(x0, 0f), new Vector2(x1, sh), Math.Max(2, hz.WarpGrid / 3), hz.WarpGrid, p =>
                {
                    float wx = _camera.ScreenToWorld(p).X;
                    float depth = Math.Clamp((zone - MathF.Abs(wx - edgeWorldX)) / zone, 0f, 1f);
                    float dx = amp * depth * MathF.Sin(p.Y * hz.WarpFreq + phase);
                    return new Vector2(p.X + dx, p.Y);
                });
            }
            // Horizontal edges (top y=0, bottom y=H): a horizontal strip, displaced vertically.
            void HEdge(float edgeWorldY, float inwardSign)
            {
                float sEdge  = _camera.WorldToScreen(new Vector2(_camera.Position.X, edgeWorldY)).Y;
                float sInner = sEdge + inwardSign * zonePx;
                float y0 = MathF.Min(sEdge, sInner), y1 = MathF.Max(sEdge, sInner);
                if (y1 <= 0f || y0 >= sh) return;
                post.Distort(new Vector2(0f, y0), new Vector2(sw, y1), hz.WarpGrid, Math.Max(2, hz.WarpGrid / 3), p =>
                {
                    float wy = _camera.ScreenToWorld(p).Y;
                    float depth = Math.Clamp((zone - MathF.Abs(wy - edgeWorldY)) / zone, 0f, 1f);
                    float dy = amp * depth * MathF.Sin(p.X * hz.WarpFreq + phase);
                    return new Vector2(p.X, p.Y + dy);
                });
            }
            VEdge(0f, +1f); VEdge(wc.Width, -1f);
            HEdge(0f, +1f); HEdge(wc.Height, -1f);
        }

        // ── Primary: red tint by the player's depth into the rim ─────────────────
        if (hz.TintMaxAlpha > 0.5f && _world.IsAlive(_player) && _world.HasComponent<Transform>(_player))
        {
            Vector2 p = _world.GetComponent<Transform>(_player).Position;
            float dist = MathF.Min(MathF.Min(p.X, wc.Width - p.X), MathF.Min(p.Y, wc.Height - p.Y));
            float depth = Math.Clamp((zone - dist) / zone, 0f, 1f);
            if (depth > 0.01f)
                FillRect(r, 0f, 0f, _ctx.ScreenW, _ctx.ScreenH,
                    new Color(190, 30, 30, (byte)(hz.TintMaxAlpha * depth)));
        }
    }

    /// <summary>
    /// The hunter-zone exposure cue: a full-screen GREY filter (distinct from the red erosion tint)
    /// that appears the moment the player enters the camping band and deepens as the hunter timer
    /// fills, plus a countdown to the next hunter wave. Tells the player they're being watched.
    /// </summary>
    private void DrawCampCue(IRenderer r)
    {
        var cr = _ctx.Config.WaveSystem.CampingResponse;
        if (!cr.Enabled || cr.TriggerSeconds <= 0f) return;
        if (_campTime <= 0.05f && !_campInZone) return;

        float progress  = Math.Clamp(_campTime / cr.TriggerSeconds, 0f, 1f);
        float intensity = MathF.Max(_campInZone ? 0.30f : 0f, progress);   // present in-zone, deepens with the timer

        if (cr.TintMaxAlpha > 0.5f && intensity > 0.01f)
            FillRect(r, 0f, 0f, _ctx.ScreenW, _ctx.ScreenH,
                new Color(165, 170, 180, (byte)(cr.TintMaxAlpha * intensity)));

        // Countdown to the next hunter wave, top-centre.
        int secs = (int)MathF.Ceiling(MathF.Max(0f, cr.TriggerSeconds - _campTime));
        string s = $"EXPOSED — HUNTERS {secs}s";
        var font = new FontSpec("monospace", 18f, bold: true);
        byte a   = (byte)(160 + 95 * intensity);
        var col  = new Color(210, 215, 225, a);
        Vector2 sz = r.MeasureText(s, font);
        r.DrawText(s, new Vector2(_ctx.ScreenW / 2f - sz.X / 2f, 14f), col, font);
    }

    /// <summary>The game-over verdict, drawn over a still-running playfield: a scrim that darkens
    /// as the death sinks in, then the headline/score fading in once it has settled.</summary>
    private void DrawGameOverOverlay(IRenderer r)
    {
        float scrim = Math.Clamp(_deadTimer / GameOverFadeIn, 0f, 1f);
        FillRect(r, 0f, 0f, _ctx.ScreenW, _ctx.ScreenH, new Color(6, 8, 12, (byte)(150 * scrim)));

        float txt = Math.Clamp((_deadTimer - GameOverFadeIn) / GameOverTextIn, 0f, 1f);
        if (txt <= 0f) return;

        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        byte a = (byte)(255 * txt);
        var big  = new FontSpec("monospace", 48f);
        var med  = new FontSpec("monospace", 22f);
        var hint = new FontSpec("monospace", 14f);

        string headline = _gameOverWon ? "YOU WIN" : "GAME OVER";
        var headlineC   = _gameOverWon ? new Color(120, 255, 160, a) : new Color(255, 100, 90, a);
        r.DrawText(headline,                      new Vector2(cx - 120f, cy - 80f), headlineC, big);
        r.DrawText($"Score  {_finalScore:F0}",    new Vector2(cx - 80f,  cy - 10f), new Color(220, 230, 255, a), med);
        if (_newBest)
            r.DrawText("NEW BEST!",               new Vector2(cx - 62f,  cy + 22f), new Color(255, 220, 90, a), med);
        else
            r.DrawText($"Best  {_ctx.HighScore:F0}", new Vector2(cx - 72f, cy + 24f), new Color(150, 165, 195, a), hint);
        r.DrawText("SPACE / ENTER for main menu", new Vector2(cx - 145f, cy + 60f), new Color(120, 140, 175, a), hint);
    }

    // ── Wave management ───────────────────────────────────────────────────────

    private void SpawnNextWave()
    {
        var ws = _ctx.Config.WaveSystem;
        var wc = _ctx.Config.World;

        var diff = _ctx.Difficulty;
        int budget = (int)((ws.BaseBudget
            + (int)(_gameTime / ws.GrowthIntervalSeconds) * ws.BudgetGrowthPerInterval) * diff.BudgetMult);
        int currentCap = (int)(Math.Min(
            ws.BaseCellCap + (int)(_gameTime / ws.GrowthIntervalSeconds) * ws.CellCapGrowthAmount,
            ws.MaxCellCap) * diff.CapMult);
        float sizeBias = ws.SizeBiasRampEnd > 0f
            ? ws.SizeBiasStart + (ws.SizeBiasEnd - ws.SizeBiasStart)
              * Math.Clamp(_gameTime / ws.SizeBiasRampEnd, 0f, 1f)
            : ws.SizeBiasEnd;

        int liveCells = CountLiveCells();
        float cellCap = MathF.Max(0f, currentCap - liveCells);
        if (cellCap < 3f) return;

        _waveNumber++;
        ShowBanner($"WAVE {_waveNumber}", WaveBannerTime);

        // One combined weighted pool of spawnables (asteroids AND aliens), time-lerped from SpawnBias.
        var bias = new Dictionary<string, float>();
        foreach (var (key, entry) in ws.SpawnBias)
        {
            float t01 = entry.T1 > entry.T0
                ? Math.Clamp((_gameTime - entry.T0) / (entry.T1 - entry.T0), 0f, 1f)
                : (_gameTime >= entry.T0 ? 1f : 0f);
            float w = entry.W0 + (entry.W1 - entry.W0) * t01;
            if (w > 0f && (_ctx.Config.Asteroids.ContainsKey(key) || _ctx.Config.Entities.ContainsKey(key)))
                bias[key] = w;
        }

        var spawns = ChooseSpawns(budget, bias, sizeBias, cellCap);
        EnqueueWave(spawns, ws.Pattern);
    }

    private void FireSpecialWave(SpecialWaveConfig sw)
    {
        var bias = new Dictionary<string, float>();
        foreach (var (k, w) in sw.Weights)
            if (w > 0f && (_ctx.Config.Asteroids.ContainsKey(k) || _ctx.Config.Entities.ContainsKey(k)))
                bias[k] = w;
        EnqueueWave(ChooseSpawns(sw.Budget, bias, sw.SizeBias, sw.CellCap),
                    sw.Pattern ?? _ctx.Config.WaveSystem.Pattern);
        ShowBanner(sw.Banner, BossBannerTime, boss: true);
    }

    private Vector2 WavePlayerPos()
    {
        var wc = _ctx.Config.World;
        return _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : new Vector2(wc.Width / 2f, wc.Height / 2f);
    }

    // ── Spawn patterns: each wave is a PLAN (shape + aim + timing) and a queue of releases ──────
    // Bodies are enqueued at wave start and released spread across the pattern's SpawnDuration;
    // positions and aim are resolved at RELEASE time, so "atPlayer" tracks the live player and
    // overlap rejection sees the world as it is then, not as it was when the wave fired.

    private enum PatternKind { Scattered, Burst, Wall, Pincer }
    private enum AimKind     { Inward, AtPlayer, Random, Fixed }

    private sealed class SpawnPlan
    {
        public PatternKind Pattern;
        public AimKind     Aim;
        public float FixedAngleRad;
        public int   Side;         // 0 top · 1 bottom · 2 left · 3 right (chosen once per wave)
        public float AnchorT;      // 0..1 along the side (burst anchor / wall centre)
        public float BurstRadius, Spread, SpeedMult, AimJitter;
        public int   Counter;      // pincer: alternates the side per release
    }

    private struct QueuedSpawn
    {
        public float Due; public bool IsAlien; public string Key; public float SizeMult; public SpawnPlan Plan;
    }

    private readonly List<QueuedSpawn> _spawnQueue = new();
    private readonly List<(Vector2 pos, float r)> _frameSpawned = new();   // per-frame overlap scratch

    // Anti-camping: accrued seconds inside the border camping band (decays outside, never resets).
    private float _campTime;
    private bool  _campInZone;   // is the player in the band THIS frame (for the exposure cue)

    private void EnqueueWave(List<(bool IsAlien, string Key, float SizeMult)> spawns, SpawnPatternConfig pc)
    {
        if (spawns.Count == 0) return;

        // nearPlayer: enter from the border closest to the player, anchored at their projection
        // onto it — the wave arrives right next to (but off-screen from) wherever they're hiding.
        int side; float anchorT;
        if (string.Equals(pc.Side, "nearPlayer", StringComparison.OrdinalIgnoreCase))
        {
            var wc = _ctx.Config.World;
            Vector2 pp = WavePlayerPos();
            float dTop = pp.Y, dBot = wc.Height - pp.Y, dLeft = pp.X, dRight = wc.Width - pp.X;
            float min = MathF.Min(MathF.Min(dTop, dBot), MathF.Min(dLeft, dRight));
            side    = min == dTop ? 0 : min == dBot ? 1 : min == dLeft ? 2 : 3;
            anchorT = side <= 1 ? pp.X / wc.Width : pp.Y / wc.Height;
            anchorT = Math.Clamp(anchorT + ((float)_rng.NextDouble() - 0.5f) * 0.1f, 0.05f, 0.95f);
        }
        else
        {
            side    = _rng.Next(4);
            anchorT = 0.2f + (float)_rng.NextDouble() * 0.6f;   // keep anchors off the corners
        }

        var plan = new SpawnPlan
        {
            Pattern = pc.Pattern?.ToLowerInvariant() switch
            {
                "burst" => PatternKind.Burst,
                "wall" => PatternKind.Wall,
                "pincer" => PatternKind.Pincer,
                _ => PatternKind.Scattered,
            },
            Aim = pc.Direction?.ToLowerInvariant() switch
            {
                "atplayer" => AimKind.AtPlayer,
                "random" => AimKind.Random,
                "fixed" => AimKind.Fixed,
                _ => AimKind.Inward,
            },
            FixedAngleRad = pc.FixedAngle * MathF.PI / 180f,
            Side          = side,
            AnchorT       = anchorT,
            BurstRadius   = MathF.Max(40f, pc.BurstRadius),
            Spread        = Math.Clamp(pc.Spread, 0.05f, 1f),
            SpeedMult     = MathF.Max(0.05f, pc.SpeedMult),
            AimJitter     = MathF.Max(0f, pc.AimJitter),
        };

        float dur = MathF.Max(0f, pc.SpawnDuration);
        for (int i = 0; i < spawns.Count; i++)
        {
            float due = spawns.Count > 1 ? _gameTime + dur * i / (spawns.Count - 1) : _gameTime;
            _spawnQueue.Add(new QueuedSpawn
            {
                Due = due, IsAlien = spawns[i].IsAlien, Key = spawns[i].Key,
                SizeMult = spawns[i].SizeMult, Plan = plan,
            });
        }
    }

    private void DrainSpawnQueue()
    {
        if (_spawnQueue.Count == 0) return;
        Vector2 playerPos = WavePlayerPos();
        _frameSpawned.Clear();

        for (int i = 0; i < _spawnQueue.Count; )
        {
            var q = _spawnQueue[i];
            if (q.Due > _gameTime) { i++; continue; }
            _spawnQueue.RemoveAt(i);

            float r = q.IsAlien
                ? 80f
                : _ctx.Config.Asteroids.TryGetValue(q.Key, out var ac) && ac.Procedural != null
                    ? ac.Procedural.BaseRadius * q.SizeMult : 80f * q.SizeMult;

            Vector2 pos = ResolvePatternPosition(q.Plan, r, playerPos);
            _frameSpawned.Add((pos, r));
            Vector2 aim = ResolveAim(q.Plan, pos, playerPos);

            Entity e = q.IsAlien
                ? AlienPrefab.Spawn(_world, _ctx, _rng, pos, q.Key, aim, q.Plan.SpeedMult)
                : AsteroidPrefab.Spawn(_world, _ctx, _rng, pos, q.Key, q.SizeMult, aim, q.Plan.SpeedMult);
            TagIfInbound(e, pos);
        }
    }

    /// <summary>Marks a body spawned in the off-screen ring outside the playable field, so the
    /// border hazard leaves it alone until it flies in (see <see cref="InboundSpawn"/>).</summary>
    private void TagIfInbound(Entity e, Vector2 pos)
    {
        if (!_world.IsAlive(e)) return;
        var wc = _ctx.Config.World;
        if (pos.X < 0f || pos.Y < 0f || pos.X > wc.Width || pos.Y > wc.Height)
            _world.AddComponent(e, new InboundSpawn());
    }

    /// <summary>Tracks time spent hugging the border (within borderHazard.hazardZone + ZoneDepth of
    /// any edge). The timer decays while outside — it never hard-resets — and at TriggerSeconds a
    /// hunter wave is sent at the player from their nearest side, repeating every RepeatSeconds
    /// for as long as they stay camped.</summary>
    private void UpdateCampingResponse(float dt)
    {
        var cr = _ctx.Config.WaveSystem.CampingResponse;
        if (!cr.Enabled || !_world.IsAlive(_player) || !_world.HasComponent<Transform>(_player)) return;

        Vector2 p  = _world.GetComponent<Transform>(_player).Position;
        _campInZone = CampZoneDepth(p) > 0f;

        if (_campInZone) _campTime += dt;
        else             _campTime  = MathF.Max(0f, _campTime - dt * cr.DecayRate);

        if (_campTime < cr.TriggerSeconds) return;

        // Fire the hunters and re-arm: staying camped brings the next batch in RepeatSeconds.
        _campTime = MathF.Max(0f, cr.TriggerSeconds - cr.RepeatSeconds);
        var bias = new Dictionary<string, float>();
        foreach (var (k, w) in cr.Weights)
            if (w > 0f && (_ctx.Config.Asteroids.ContainsKey(k) || _ctx.Config.Entities.ContainsKey(k)))
                bias[k] = w;
        EnqueueWave(ChooseSpawns(cr.Budget, bias, cr.SizeBias, cr.CellCap), cr.Pattern);
        ShowBanner(cr.Banner, WaveBannerTime, boss: true);
    }

    /// <summary>How exposed the player is to the hunter zone at <paramref name="p"/>: 0 outside,
    /// →1 at the very edge/corner. The zone is a band of width <c>hazardZone + ZoneDepth</c> off each
    /// edge, PLUS a deeper square in each corner (side = band·CornerScale), so a corner is caught
    /// sooner and reads as more dangerous than a flat wall.</summary>
    private float CampZoneDepth(Vector2 p)
    {
        var cr = _ctx.Config.WaveSystem.CampingResponse;
        var wc = _ctx.Config.World;
        float band = _ctx.Config.BorderHazard.HazardZone + cr.ZoneDepth;
        if (band <= 1f) return 0f;

        float dx = MathF.Min(p.X, wc.Width  - p.X);   // distance to nearest vertical edge
        float dy = MathF.Min(p.Y, wc.Height - p.Y);   // distance to nearest horizontal edge

        float edge = Math.Clamp((band - MathF.Min(dx, dy)) / band, 0f, 1f);

        float cornerBand = band * MathF.Max(1f, cr.CornerScale);
        float corner = 0f;
        if (dx < cornerBand && dy < cornerBand)   // near a corner: both edges close
            corner = Math.Clamp((cornerBand - MathF.Max(dx, dy)) / cornerBand, 0f, 1f);

        return MathF.Max(edge, corner);
    }

    /// <summary>A point in the spawn ring OUTSIDE a side of the playable field: t ∈ 0..1 along it,
    /// random depth into the ring. Off-screen by construction — the camera never sees past the
    /// playable bounds — so waves can enter beside the player wherever they are.</summary>
    private Vector2 SidePoint(int side, float t)
    {
        var wc = _ctx.Config.World;
        float depth = 40f + (float)_rng.NextDouble() * MathF.Max(40f, wc.SpawnMargin - 80f);
        t = Math.Clamp(t, 0f, 1f);
        return side switch
        {
            0 => new Vector2(t * wc.Width, -depth),                   // above the top edge
            1 => new Vector2(t * wc.Width, wc.Height + depth),        // below the bottom edge
            2 => new Vector2(-depth, t * wc.Height),                  // left of the left edge
            _ => new Vector2(wc.Width + depth, t * wc.Height),        // right of the right edge
        };
    }

    private Vector2 ResolvePatternPosition(SpawnPlan plan, float radius, Vector2 playerPos)
    {
        if (plan.Pattern == PatternKind.Scattered)
            return FindSpawnPosition(radius, _frameSpawned, playerPos);

        int side = plan.Pattern == PatternKind.Pincer && (plan.Counter++ & 1) == 1
            ? OppositeSide(plan.Side) : plan.Side;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            Vector2 pos = plan.Pattern switch
            {
                PatternKind.Burst => SidePoint(side, plan.AnchorT)
                    + new Vector2((float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1))
                    * plan.BurstRadius,
                // wall / pincer: a slot along the side, spread around the anchor
                _ => SidePoint(side, plan.AnchorT + ((float)_rng.NextDouble() - 0.5f) * plan.Spread),
            };
            // Clamp to the EXTENDED bounds (field + spawn ring) — clamping to the field would drag
            // ring positions back inside and put them on-screen.
            var wc = _ctx.Config.World;
            float m = wc.SpawnMargin;
            pos = Vector2.Clamp(pos, new Vector2(radius - m, radius - m),
                                new Vector2(wc.Width + m - radius, wc.Height + m - radius));
            if (SpawnSpotClear(pos, radius, playerPos)) return pos;
        }
        // Pattern spot never cleared (crowded corner, viewport…) → classic scattered fallback.
        return FindSpawnPosition(radius, _frameSpawned, playerPos);
    }

    private static int OppositeSide(int side) => side switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };

    /// <summary>The same rejection rules FindSpawnPosition applies, for a caller-chosen spot.</summary>
    private bool SpawnSpotClear(Vector2 pos, float radius, Vector2 playerPos)
    {
        Vector2 sp = _camera.WorldToScreen(pos);
        if (sp.X > -ViewMargin && sp.X < _ctx.ScreenW + ViewMargin &&
            sp.Y > -ViewMargin && sp.Y < _ctx.ScreenH + ViewMargin) return false;

        float playerClear = radius + 18f + 150f;
        if ((pos - playerPos).LengthSquared() < playerClear * playerClear) return false;

        foreach (var (p, r) in _frameSpawned)
        {
            float minDist = radius + r + 20f;
            if ((pos - p).LengthSquared() < minDist * minDist) return false;
        }

        return !PhysicsQueries.OverlapsCircle(_world, pos, radius + 20f,
                    GameLayers.Asteroid | GameLayers.Alien | GameLayers.Player);
    }

    private Vector2 ResolveAim(SpawnPlan plan, Vector2 pos, Vector2 playerPos)
    {
        var wc = _ctx.Config.World;
        Vector2 dir = plan.Aim switch
        {
            AimKind.AtPlayer => playerPos - pos,
            AimKind.Random   => new Vector2(MathF.Cos((float)(_rng.NextDouble() * MathF.Tau)),
                                            MathF.Sin((float)(_rng.NextDouble() * MathF.Tau))),
            AimKind.Fixed    => new Vector2(MathF.Cos(plan.FixedAngleRad), MathF.Sin(plan.FixedAngleRad)),
            _                => new Vector2(wc.Width / 2f, wc.Height / 2f) - pos,   // inward
        };
        if (dir.LengthSquared() < 1e-6f) dir = Vector2.UnitX;
        dir = Vector2.Normalize(dir);

        float jitter = ((float)_rng.NextDouble() * 2f - 1f) * plan.AimJitter;
        float c = MathF.Cos(jitter), s = MathF.Sin(jitter);
        return new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
    }

    // Stateless budget-packing over a combined asteroid + alien pool: weighted pick → consume budget +
    // cell cap → repeat, until neither fits. Weights are relative (normalised by their sum at pick).
    private List<(bool IsAlien, string Key, float SizeMult)> ChooseSpawns(
        int budget, Dictionary<string, float> bias, float sizeBias, float cellCap)
    {
        var result    = new List<(bool, string, float)>();
        float remBudget = budget;
        float remCells  = cellCap;
        float alpha   = MathF.Pow(2f, -sizeBias);  // 1=uniform, 0.5=large-biased, 2=small-biased

        while (true)
        {
            var cands = new List<(string key, bool alien, AsteroidConfig? ac, EntityConfig? ec, float w)>();
            foreach (var (key, w) in bias)
            {
                if (_ctx.Config.Asteroids.TryGetValue(key, out var ac) && ac.Procedural != null)
                {
                    float minMult = ac.SizeRange[0];
                    if (ac.BaseCost * minMult > remBudget) continue;
                    if (AsteroidPrefab.CellsFor(_ctx, ac, minMult) > remCells) continue;
                    cands.Add((key, false, ac, null, w));
                }
                else if (_ctx.Config.Entities.TryGetValue(key, out var ec))
                {
                    if (ec.BaseCost > remBudget || ec.CellCount > remCells) continue;
                    cands.Add((key, true, null, ec, w));
                }
            }
            if (cands.Count == 0) break;

            float total = cands.Sum(c => c.w);
            float pick  = (float)_rng.NextDouble() * total;
            float cum   = 0f;
            var chosen  = cands[0];
            foreach (var c in cands) { cum += c.w; if (pick <= cum) { chosen = c; break; } }

            if (chosen.alien)
            {
                result.Add((true, chosen.key, 1f));
                remBudget -= chosen.ec!.BaseCost;
                remCells  -= chosen.ec!.CellCount;
            }
            else
            {
                var ac = chosen.ac!;
                float maxByBudget = remBudget / ac.BaseCost;
                float kUnit       = AsteroidPrefab.CellsFor(_ctx, ac, 1f);
                float maxByCells  = MathF.Sqrt(remCells / kUnit);
                float maxMult     = Math.Min(ac.SizeRange[1], Math.Min(maxByBudget, maxByCells));
                float minMult0    = ac.SizeRange[0];
                if (maxMult < minMult0) break;   // (guards above ensure this shouldn't hit)

                float u        = (float)_rng.NextDouble();
                float sizeMult = minMult0 + MathF.Pow(u, alpha) * (maxMult - minMult0);
                result.Add((false, chosen.key, sizeMult));
                remBudget -= ac.BaseCost * sizeMult;
                remCells  -= AsteroidPrefab.CellsFor(_ctx, ac, sizeMult);
            }
        }
        return result;
    }

    private Vector2 FindSpawnPosition(float radius, List<(Vector2 pos, float r)> placed, Vector2 playerPos)
    {
        float playerClear = radius + 18f + 150f;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            // Scattered spawns live in the OFF-SCREEN ring outside the playable field (SidePoint),
            // so they never pop into view and can enter from any side, wherever the player is.
            Vector2 pos = SidePoint(_rng.Next(4), (float)_rng.NextDouble());

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
            if (!clear) continue;

            // Also reject overlap with bodies already in the world (prior waves, drifters) — spawning
            // on top of an existing body is what produces the stuck-inside jitter.
            if (PhysicsQueries.OverlapsCircle(_world, pos, radius + 20f,
                    GameLayers.Asteroid | GameLayers.Alien | GameLayers.Player)) continue;

            return pos;
        }

        // Fallback: somewhere in the top spawn ring.
        return SidePoint(0, (float)_rng.NextDouble());
    }

    // Spawns a typed asteroid from GameConfig.Asteroids using the procedural pipeline.
    private void SpawnAsteroid(Vector2 pos, string typeKey, float sizeMult)
        => AsteroidPrefab.Spawn(_world, _ctx, _rng, pos, typeKey, sizeMult);

    private void SpawnAlien(Vector2 pos, string typeKey)
        => AlienPrefab.Spawn(_world, _ctx, _rng, pos, typeKey);

    private void SpawnMothership()
    {
        if (!_ctx.Config.Entities.TryGetValue("mothership", out var ec)) return;
        var wc = _ctx.Config.World;
        Vector2 playerPos = _world.IsAlive(_player) && _world.HasComponent<Transform>(_player)
            ? _world.GetComponent<Transform>(_player).Position
            : new Vector2(wc.Width / 2f, wc.Height / 2f);
        Vector2 pos    = FindSpawnPosition(220f, new List<(Vector2, float)>(), playerPos);
        Vector2 dir    = new Vector2(wc.Width / 2f, wc.Height / 2f) - pos;
        float   len    = dir.Length();
        Vector2 vel    = len > 1f ? dir / len * ec.Speed : Vector2.Zero;

        _mothershpGroupId = _fracture.AllocateGroupId();
        var boss = MothershipPrefab.Spawn(_world, _ctx, _rng, pos, vel, _mothershpGroupId, out _mothershpInitialCockpits);
        TagIfInbound(boss, pos);   // it spawns in the off-screen ring and cruises in

        ShowBanner("ALIEN BOSS HAS APPEARED", BossBannerTime, boss: true);
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


    // ── Rendering ─────────────────────────────────────────────────────────────

    private readonly List<Vector2> _meshVerts = new();
    private readonly List<int>     _meshLens  = new();
    private readonly List<Vector2> _cellBuf   = new();   // one HUD cell polygon (reused)
    private float[]                _cellDmg   = System.Array.Empty<float>();  // per-cell damage (reused)

    // ── Pause menu ────────────────────────────────────────────────────────────
    private bool _paused;
    private int  _pauseSel;
    private static readonly string[] PauseOpts = { "Resume", "Restart", "Main Menu" };

    // ── Juice: hitstop + floating score popups ────────────────────────────────
    private float _hitstop;                              // seconds of simulation freeze remaining
    private struct Popup { public Vector2 Pos; public float Value, Remaining, MaxTtl, Size; }
    private readonly List<Popup> _popups = new();
    private float   _popAccum;                           // score accumulating toward the next popup
    private Vector2 _popPos;                             // value-weighted world position of the accumulator
    private float   _popTimer;                           // idle countdown to flush a partial accumulator

    private void AddHitstop(float s) =>
        _hitstop = MathF.Min(_ctx.Config.Vfx.HitstopMax, MathF.Max(_hitstop, s));
    // ── HUD constants ─────────────────────────────────────────────────────────
    // HUD-display scale for ship damage widget (body-local coords × this = HUD pixels).
    private const float HudShipScale = 2.8f;
    // X-center of ship damage widget.
    private const float HudShipCX    = 92f;
    // Y-center of ship damage widget (from screen bottom).
    private const float HudShipCYOff = 74f;
    // Where weapon bars start (from left).
    private const float HudWeapX     = 200f;
    // Cooldown-bar geometry (weapons and skills share it so the HUD reads as one row).
    private const float HudBarW      = 82f;
    private const float HudBarH      = 13f;
    // Right-edge padding for the (right-aligned) skill row.
    private const float HudRightPad  = 14f;
    // Column pitch is computed from the widest label so names never overlap (HudColumnWidth).

    // Labels carry the bound key so the HUD can never drift from PlayerControlSystem's bindings.
    private static readonly (string Role, string WeapKey, string Label, string Key, Color Color)[] WeaponDefs =
    [
        ("cannon",   "cannon",   "CANNON",  "LMB", new Color(255, 200, 80)),
        ("shotgun",  "shotgun",  "SHOTGUN", "RMB", new Color(255, 140, 60)),
        ("piercing", "piercing", "PIERCE",  "G",   new Color(180, 120, 255)),
        ("grenade",  "grenade",  "GRENADE", "F",   new Color(100, 220, 120)),
    ];
    private static readonly (string SkillKey, string Label, string Key)[] SkillDefs =
    [
        ("dash",   "DASH",    "Q"),
        ("turbo",  "TURBO",   "E"),
        ("slowmo", "SLOW-MO", "R"),
    ];

    private static readonly FontSpec HudLabelFont = new("monospace", 14f, bold: true);
    private static readonly FontSpec HudKeyFont   = new("monospace", 12f);

    /// <summary>Uniform column pitch for the cooldown row: the widest "NAME [KEY]" label (or the bar,
    /// whichever is larger) plus padding, so no two widgets' text can overlap regardless of wording.</summary>
    private float HudColumnWidth(IRenderer r)
    {
        float widest = 0f;
        foreach (var d in WeaponDefs)
            widest = MathF.Max(widest, r.MeasureText(d.Label, HudLabelFont).X + 6f + r.MeasureText($"[{d.Key}]", HudKeyFont).X);
        foreach (var d in SkillDefs)
            widest = MathF.Max(widest, r.MeasureText(d.Label, HudLabelFont).X + 6f + r.MeasureText($"[{d.Key}]", HudKeyFont).X);
        return MathF.Max(HudBarW, widest) + 16f;
    }

    private void DrawHud(IRenderer r)
    {
        // ── Top-left: timer (big) / score (big) / best / combo ────────────────
        int elapsed = (int)_gameTime;
        r.DrawText($"{elapsed / 60:00}:{elapsed % 60:00}",
            new Vector2(14f, 10f), new Color(225, 232, 250), new FontSpec("monospace", 32f, bold: true));
        r.DrawText($"{_ctx.Score.Total:F0} pts",
            new Vector2(14f, 50f), new Color(255, 228, 130), new FontSpec("monospace", 22f, bold: true));
        r.DrawText($"best {_ctx.HighScore:F0}",
            new Vector2(14f, 78f), new Color(150, 165, 195), new FontSpec("monospace", 14f));

        var steps = _ctx.Config.Scoring.KillChainSteps;
        int tier  = _ctx.Score.ChainTier;
        if (tier > 0 && steps.Length > 0)
        {
            float mult = tier < steps.Length ? steps[tier] : steps[^1];
            float f    = steps.Length > 1 ? tier / (float)(steps.Length - 1) : 1f;   // 0→1 across tiers
            var comboC = new Color(255, (byte)(220 - 120 * f), (byte)(90 - 60 * f));  // yellow → red
            r.DrawText($"x{mult:0.#} COMBO", new Vector2(14f, 100f), comboC, new FontSpec("monospace", 19f, bold: true));
        }

        if (!_world.IsAlive(_player)) return;

        float bY = _ctx.ScreenH - 12f;  // bottom of HUD bar area

        // ── Ship damage widget ─────────────────────────────────────────────────
        var shipCenter = new Vector2(HudShipCX, _ctx.ScreenH - HudShipCYOff);
        DrawShipWidget(r, shipCenter, HudShipScale);

        // Uniform column pitch, so labels can't overlap; skills right-aligned to the same pitch.
        float col = HudColumnWidth(r);

        // ── Weapon cooldown bars (from the left) ────────────────────────────────
        DrawWeaponBars(r, HudWeapX, bY, col);

        // ── Skill cooldown bars (right-aligned) ─────────────────────────────────
        float skillStart = _ctx.ScreenW - HudRightPad - SkillDefs.Length * col;
        DrawSkillBars(r, skillStart, bY, col);
    }

    private void DrawShipWidget(IRenderer r, Vector2 center, float scale)
    {
        if (!_world.HasComponent<FracturableBody>(_player)) return;
        ref var fb  = ref _world.GetComponent<FracturableBody>(_player);
        bool[]? pulv = _world.HasComponent<FractureProcess>(_player)
            ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;

        // Per-cell damage = comminution toward the vaporise threshold (Cell.Damage). It accumulates on
        // hits and HEALS over time via relaxRate (StressRelaxSystem), so the ship reddens then recovers
        // — matching the fracture model, unlike instantaneous bond stress.
        int nCells = fb.Cells.Length;
        if (_cellDmg.Length < nCells) _cellDmg = new float[nCells];
        var mat = fb.Material;
        for (int i = 0; i < nCells; i++)
        {
            float threshold = mat.CellToughness * fb.Cells[i].Area * fb.Cells[i].DensityMult * mat.Density;
            _cellDmg[i] = threshold > 1e-3f ? Math.Clamp(fb.Cells[i].Damage / threshold, 0f, 1f) : 0f;
        }

        // Fill alive cells, coloured green → orange → red by their damage.
        for (int ci = 0; ci < nCells; ci++)
        {
            if (pulv?[ci] == true) continue;
            var lv = fb.Cells[ci].Local;
            _cellBuf.Clear();
            foreach (var v in lv) _cellBuf.Add(center + v * scale);
            r.FillPolygon(CollectionsMarshal.AsSpan(_cellBuf), DamageColor(_cellDmg[ci]));
        }

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

    // Damage 0→1 mapped green → orange → red for the HUD ship widget.
    private static Color DamageColor(float d)
    {
        d = Math.Clamp(d, 0f, 1f);
        Color green  = new(45, 165, 65,  210);
        Color orange = new(225, 140, 40, 210);
        Color red    = new(205, 45, 45,  210);
        return d < 0.5f ? LerpColor(green, orange, d / 0.5f)
                        : LerpColor(orange, red, (d - 0.5f) / 0.5f);
    }

    private static Color LerpColor(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        (byte)(a.A + (b.A - a.A) * t));

    private void DrawBanner(IRenderer r)
    {
        if (_bannerTimer <= 0f) return;
        float k = Math.Clamp(_bannerTimer / MathF.Max(0.01f, _bannerMax), 0f, 1f);
        float a = k > 0.8f ? (1f - k) / 0.2f : k / 0.8f;   // quick fade-in, slow fade-out
        a = Math.Clamp(a, 0f, 1f);
        var  col  = _bannerBoss ? new Color(255, 90, 90, (byte)(255 * a)) : new Color(230, 235, 255, (byte)(230 * a));
        var  font = new FontSpec("monospace", _bannerBoss ? 34f : 42f, bold: true);
        Vector2 sz = r.MeasureText(_bannerText, font);
        r.DrawText(_bannerText, new Vector2(_ctx.ScreenW / 2f - sz.X / 2f, _ctx.ScreenH * 0.20f), col, font);
    }

    private void DrawPauseMenu(IRenderer r)
    {
        FillRect(r, 0f, 0f, _ctx.ScreenW, _ctx.ScreenH, new Color(6, 8, 12, 175));
        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        r.DrawText("PAUSED", new Vector2(cx - 72f, cy - 96f), new Color(220, 235, 255), new FontSpec("monospace", 34f));
        var opt = new FontSpec("monospace", 20f);
        for (int i = 0; i < PauseOpts.Length; i++)
        {
            bool sel = i == _pauseSel;
            var col  = sel ? new Color(255, 220, 90) : new Color(150, 165, 195);
            r.DrawText((sel ? "> " : "  ") + PauseOpts[i], new Vector2(cx - 60f, cy - 22f + i * 30f), col, opt);
        }
        r.DrawText("W/S move   ENTER select   ESC resume",
            new Vector2(cx - 168f, cy + 92f), new Color(80, 100, 130), new FontSpec("monospace", 13f));
    }

    private void DrawWeaponBars(IRenderer r, float startX, float bottomY, float columnW)
    {
        bool hasFb  = _world.HasComponent<FracturableBody>(_player);
        bool hasFp  = hasFb && _world.HasComponent<FractureProcess>(_player);
        bool[]? pulv = hasFp ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;
        WeaponCooldowns wcd = _world.HasComponent<WeaponCooldowns>(_player)
            ? _world.GetComponent<WeaponCooldowns>(_player) : default;

        float x = startX;
        foreach (var (role, key, label, bind, col) in WeaponDefs)
        {
            bool cellAlive = IsWeaponCellAlive(role, hasFb, pulv);
            Color textC = cellAlive ? col : new Color(70, 74, 85);
            Color keyC  = cellAlive ? new Color(190, 200, 220) : new Color(70, 74, 85);
            Color bgC   = new Color(22, 25, 32);
            Color fgC   = cellAlive ? col : new Color(45, 48, 58);

            // Name + bound key above the bar: "CANNON [LMB]".
            r.DrawText(label, new Vector2(x + 1f, bottomY - HudBarH - 22f), textC, HudLabelFont);
            Vector2 nameSz = r.MeasureText(label, HudLabelFont);
            r.DrawText($"[{bind}]", new Vector2(x + 1f + nameSz.X + 5f, bottomY - HudBarH - 20f), keyC, HudKeyFont);
            FillRect(r, x, bottomY - HudBarH, HudBarW, HudBarH, bgC);

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
                float fill = HudBarW;
                if (_ctx.Config.Weapons.TryGetValue(key, out var wCfg))
                {
                    float maxCd = 1f / MathF.Max(0.001f, wCfg.FireRate);
                    fill = HudBarW * (1f - Math.Clamp(cdRem / maxCd, 0f, 1f));
                }
                if (fill > 0f) FillRect(r, x, bottomY - HudBarH, fill, HudBarH, fgC);
            }
            x += columnW;
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

    private void DrawSkillBars(IRenderer r, float startX, float bottomY, float columnW)
    {
        bool hasFb  = _world.HasComponent<FracturableBody>(_player);
        bool hasFp  = hasFb && _world.HasComponent<FractureProcess>(_player);
        bool[]? pulv = hasFp ? _world.GetComponent<FractureProcess>(_player).Pulverized : null;
        bool propOk = HasAlivePropeller(hasFb, pulv);
        SkillState sk = _world.HasComponent<SkillState>(_player)
            ? _world.GetComponent<SkillState>(_player) : default;

        float x = startX;
        foreach (var (key, label, bind) in SkillDefs)
        {
            bool gate = key == "slowmo" || propOk;
            if (!_ctx.Config.Skills.TryGetValue(key, out var sc)) { x += columnW; continue; }

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
            Color keyC  = gate ? new Color(190, 200, 220) : new Color(70, 74, 85);
            Color bgC   = new Color(22, 25, 32);
            Color fgC   = gate ? col : new Color(45, 48, 58);

            // Name + bound key above the bar: "DASH [Q]".
            r.DrawText(label, new Vector2(x + 1f, bottomY - HudBarH - 22f), textC, HudLabelFont);
            Vector2 nameSz = r.MeasureText(label, HudLabelFont);
            r.DrawText($"[{bind}]", new Vector2(x + 1f + nameSz.X + 5f, bottomY - HudBarH - 20f), keyC, HudKeyFont);
            FillRect(r, x, bottomY - HudBarH, HudBarW, HudBarH, bgC);
            float fill = HudBarW * ratio;
            if (fill > 0f && gate) FillRect(r, x, bottomY - HudBarH, fill, HudBarH, fgC);
            x += columnW;
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
