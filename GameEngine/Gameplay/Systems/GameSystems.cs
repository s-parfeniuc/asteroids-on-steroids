using System.Numerics;
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

namespace AsteroidsGame.Gameplay;

// ── Shared gameplay systems (promoted from the game; richer supersets of the
//    demo's earlier copies). Both the game and the demo compose these. ──────────

public sealed class PlayerControlSystem : ISystem
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
            // Dash along the held-movement direction, so you can dash sideways to dodge without
            // rotating; fall back to current travel direction, then aim.
            Vector2 dashDir = Vector2.Zero;
            if (inp.IsHeld(KeyCode.W)) dashDir.Y -= 1f; if (inp.IsHeld(KeyCode.S)) dashDir.Y += 1f;
            if (inp.IsHeld(KeyCode.A)) dashDir.X -= 1f; if (inp.IsHeld(KeyCode.D)) dashDir.X += 1f;
            ref var dashVel = ref world.GetComponent<Velocity>(Player);
            if (dashDir == Vector2.Zero)
                dashDir = dashVel.Linear.LengthSquared() > 100f ? dashVel.Linear : aim.Dir;
            dashVel.Linear += Vector2.Normalize(dashDir) * (dashCfg.VelocitySpike ?? 1400f);
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

        // ── Movement — direct velocity model, thrust degraded by propeller survival ──
        float thrustMult = ComputeThrustMult(world);
        ref var vel = ref world.GetComponent<Velocity>(Player);
        var p = _ctx.Config.Player;

        // Lateral bleed: velocity perpendicular to aim bleeds at LateralDrag s⁻¹.
        Vector2 fwd  = aim.Dir;
        Vector2 rgt  = new(fwd.Y, -fwd.X);
        float vAlong = Vector2.Dot(vel.Linear, fwd);
        float vPerp  = Vector2.Dot(vel.Linear, rgt);
        vPerp *= MathF.Exp(-p.LateralDrag * fdt);
        vel.Linear = fwd * vAlong + rgt * vPerp;

        Vector2 wantDir = Vector2.Zero;
        if (inp.IsHeld(KeyCode.W)) wantDir.Y -= 1; if (inp.IsHeld(KeyCode.S)) wantDir.Y += 1;
        if (inp.IsHeld(KeyCode.A)) wantDir.X -= 1; if (inp.IsHeld(KeyCode.D)) wantDir.X += 1;
        if (wantDir != Vector2.Zero) wantDir = Vector2.Normalize(wantDir);

        if (wantDir != Vector2.Zero && thrustMult > 0f)
        {
            bool freshPress = (inp.IsPressed(KeyCode.W) && wantDir.Y < 0)
                           || (inp.IsPressed(KeyCode.S) && wantDir.Y > 0)
                           || (inp.IsPressed(KeyCode.A) && wantDir.X < 0)
                           || (inp.IsPressed(KeyCode.D) && wantDir.X > 0);
            if (freshPress) vel.Linear += wantDir * p.Impulse;

            float turboMult = (sk.TurboActive > 0f && skills.TryGetValue("turbo", out var tc))
                ? (tc.ThrustMult ?? 3f) : 1f;
            float slowBoost = (sk.SlowMoActive > 0f && skills.TryGetValue("slowmo", out var smc))
                ? (smc.PlayerSpeedBoost ?? 1f) : 1f;
            float maxSpd = p.MaxSpeed * thrustMult * turboMult * slowBoost;
            float step   = p.Thrust * fdt * thrustMult * turboMult * slowBoost;
            Vector2 target = wantDir * maxSpd;
            Vector2 diff   = target - vel.Linear;
            float diffLen  = diff.Length();
            vel.Linear += diffLen <= step ? diff : diff / diffLen * step;
            float spd = vel.Linear.Length();
            if (spd > maxSpd) vel.Linear *= maxSpd / spd;
        }
        else
        {
            vel.Linear *= MathF.Exp(-p.BrakeDrag * fdt);
        }

        // ── Aim toward mouse (rate-limited rotation) ──────────────────────────
        {
            Vector2 mouseWorld = _camera.ScreenToWorld(inp.MouseScreen);
            Vector2 toMouse    = mouseWorld - t.Position;
            if (toMouse.LengthSquared() > 1f)
            {
                float targetAngle  = MathF.Atan2(toMouse.Y, toMouse.X);
                float currentAngle = MathF.Atan2(aim.Dir.Y, aim.Dir.X);
                float delta        = targetAngle - currentAngle;
                while (delta >  MathF.PI) delta -= MathF.Tau;
                while (delta < -MathF.PI) delta += MathF.Tau;
                float rotSpeed = _ctx.Config.Player.RotSpeed;
                if (sk.SlowMoActive > 0f && skills.TryGetValue("slowmo", out var smRot))
                    rotSpeed *= smRot.RotSpeedBoost ?? 1f;   // keep aim snappy while the world crawls
                float maxTurn  = rotSpeed * fdt;
                float turn     = MathF.Abs(delta) <= maxTurn ? delta : MathF.Sign(delta) * maxTurn;
                aim.Dir = new Vector2(MathF.Cos(currentAngle + turn), MathF.Sin(currentAngle + turn));
            }
        }

        // Sync body rotation to aim direction. Shape authored facing up (−Y), +π/2 aligns with engine default.
        t.Rotation = MathF.Atan2(aim.Dir.Y, aim.Dir.X) + MathF.PI * 0.5f;

        // ── Weapon cooldown ticking ───────────────────────────────────────────
        if (wcd.Cannon   > 0f) wcd.Cannon   = MathF.Max(0f, wcd.Cannon   - fdt);
        if (wcd.Shotgun  > 0f) wcd.Shotgun  = MathF.Max(0f, wcd.Shotgun  - fdt);
        if (wcd.Piercing > 0f) wcd.Piercing = MathF.Max(0f, wcd.Piercing - fdt);
        if (wcd.Grenade  > 0f) wcd.Grenade  = MathF.Max(0f, wcd.Grenade  - fdt);

        Vector2 muzzle = t.Position + aim.Dir * 24f;

        // Held auto-fire OR a latched click edge, so a quick tap is never dropped by held-only polling
        // or a 0-step frame. Consume unconditionally (before the ||) so held-fire doesn't leave a stale
        // latch that would fire a phantom shot after release.
        bool leftFire  = inp.ConsumeMouseLeftClick()  | inp.IsMouseLeft;
        bool rightFire = inp.ConsumeMouseRightClick() | inp.IsMouseRight;

        // ── Cannon (left-click) ───────────────────────────────────────────────
        if (leftFire && wcd.Cannon <= 0f && IsWeaponCellAlive("cannon", world)
            && _ctx.Config.Weapons.TryGetValue("cannon", out var ccfg))
        {
            wcd.Cannon = 1f / MathF.Max(0.001f, ccfg.FireRate);
            WeaponEffects.SpawnBullet(world, muzzle, aim.Dir * ccfg.ProjectileSpeed, "cannon",
                ccfg.TimeToLive, new Color(255, 230, 90), Player, WeaponEffects.BulletGrace, ccfg.Drag, alien: false);
        }

        // ── Shotgun (right-click) ─────────────────────────────────────────────
        if (rightFire && wcd.Shotgun <= 0f && IsWeaponCellAlive("shotgun", world)
            && _ctx.Config.Weapons.TryGetValue("shotgun", out var scfg))
        {
            wcd.Shotgun = 1f / MathF.Max(0.001f, scfg.FireRate);
            int rays   = scfg.Rays ?? 7;
            WeaponEffects.SpawnCone(world, muzzle, aim.Dir, rays, scfg.ConeAngle ?? 18f,
                scfg.ProjectileSpeed, scfg.TimeToLive, "shotgun", new Color(255, 160, 80),
                Player, WeaponEffects.BulletGrace, scfg, alien: false, _ctx.Rng);
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
            world.AddComponent(b, new Collider { Shape = new CircleShape(6f), Layer = GameLayers.Bullet, Mask = GameLayers.Asteroid | GameLayers.Alien });
            world.AddComponent(b, new BulletTag());
            world.AddComponent(b, new BulletVisual { Color = new Color(100, 255, 100) });
            world.AddComponent(b, new BulletData { WeaponKey = "grenade", Owner = Player, OwnerGrace = WeaponEffects.BulletGrace });
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

public sealed class VortexSystem : ISystem
{
    private readonly Vector2      _mapCentre;
    private readonly float        _worldW, _worldH;
    private readonly VortexConfig _cfg;
    private float _time = 0f;
    private float _centripetalK, _tangentialK;   // this frame's strengths, for FieldVelocity

    /// <summary>Current vortex centre (updated each frame). Renderers can read it for the pull visual.</summary>
    public Vector2 Centre { get; private set; }

    /// <summary>Terminal drift velocity (px/s) a neutral body riding the field reaches at
    /// <paramref name="pos"/> this frame — the honest thing to advect gust motes along. Zero inside
    /// the deadzone / at the eye; grows with radius (the field is strongest far out).</summary>
    public Vector2 FieldVelocity(Vector2 pos)
    {
        Vector2 toCenter = Centre - pos;
        float dist = toCenter.Length();
        if (dist < 1e-3f) return Vector2.Zero;
        float excess = dist - _cfg.Deadzone;
        if (excess <= 0f) return Vector2.Zero;
        Vector2 radial  = toCenter / dist;
        Vector2 tangent = new(-radial.Y, radial.X);
        float scale = excess * (1f / 120f) * _cfg.CapFrames;   // matches the sim's per-tick cap model
        return radial * (_centripetalK * scale) + tangent * (_tangentialK * scale);
    }

    public VortexSystem(Vector2 mapCentre, float worldW, float worldH, VortexConfig cfg)
    {
        _mapCentre = mapCentre; _worldW = worldW; _worldH = worldH; _cfg = cfg;
        Centre = mapCentre;
    }

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        _time += fdt;

        // Lissajous orbit around the map centre, with amplitudes clamped so the centre never comes
        // within BorderMargin of an edge (max reach from centre is half the map minus the margin).
        float ampX = MathF.Min(MathF.Max(0f, _cfg.MoveAmpX), MathF.Max(0f, _worldW * 0.5f - _cfg.BorderMargin));
        float ampY = MathF.Min(MathF.Max(0f, _cfg.MoveAmpY), MathF.Max(0f, _worldH * 0.5f - _cfg.BorderMargin));
        float px   = MathF.Max(0.1f, _cfg.MovePeriodX);
        float py   = MathF.Max(0.1f, _cfg.MovePeriodY);
        Vector2 centre = _mapCentre + new Vector2(
            ampX * MathF.Sin(_time * MathF.Tau / px),
            ampY * MathF.Sin(_time * MathF.Tau / py + _cfg.MovePhase));
        Centre = centre;

        float centripetalK = MathF.Max(0f, _cfg.Centripetal
                             + _cfg.VariationCentripetal * MathF.Sin(_time * MathF.Tau / 5f));
        float tangentialK  = MathF.Max(0f, _cfg.Tangential
                             + _cfg.VariationTangential * MathF.Sin(_time * MathF.Tau / 13f + MathF.PI * 0.5f));
        _centripetalK = centripetalK; _tangentialK = tangentialK;   // published for FieldVelocity (gust FX)
        float deadzone  = _cfg.Deadzone;
        float capFrames = _cfg.CapFrames;

        world.ForEach<Transform, Velocity, RigidBody, VortexResponse>(
            (Entity _, ref Transform t, ref Velocity v, ref RigidBody _, ref VortexResponse vr) =>
        {
            Vector2 toCenter = centre - t.Position;
            float dist = toCenter.Length();
            if (dist < 1e-3f) return;
            float excess = dist - deadzone;
            if (excess <= 0f) return;

            Vector2 radial  = toCenter / dist;
            Vector2 tangent = new(-radial.Y, radial.X);

            float forceC = centripetalK * vr.CentripetalMult * excess * fdt;
            float forceT = tangentialK  * vr.TangentialMult  * excess * fdt;
            float capC   = forceC * capFrames;
            float capT   = forceT * capFrames;

            float vC = Vector2.Dot(v.Linear, radial);
            float vT = Vector2.Dot(v.Linear, tangent);

            if (vC < capC) v.Linear += radial  * MathF.Min(forceC, capC - vC);
            if (vT < capT) v.Linear += tangent * MathF.Min(forceT, capT - vT);
        });
    }
}

/// <summary>
/// The map-border rim, all config-driven (<see cref="BorderHazardConfig"/>):
///  • <b>Damp</b> — cancels outward velocity near an edge so nothing leaves the map.
///  • <b>Push</b> — an inward shove that grows toward the edge, nudging bodies off the walls
///    (summed per axis, so a corner pushes diagonally toward centre).
///  • <b>Erosion</b> — after a grace period in the hazard rim, the "storm" rips a body's most
///    <i>exposed</i> cell every Tick, energy ramping the longer it lingers. Applies to ALL
///    fracturable bodies (player, aliens, asteroids), so corner-camping is never safe.
/// </summary>
public sealed class BorderHazardSystem : ISystem
{
    private readonly float _worldW, _worldH;
    private readonly BorderHazardConfig _cfg;
    private readonly Random _rng;
    private readonly ParticleEffects _fx;

    private struct Exposure { public float Time; public float TickAcc; }
    private readonly Dictionary<Entity, Exposure> _exposure = new();
    private readonly List<(Entity Body, int Cell, Vector2 Point, Vector2 Dir, float Mass)> _erode = new();
    private readonly List<Entity> _stale = new();

    // Environmental impactor: tight (stays on the exposed face), a little blast, no knockback.
    private static readonly WeaponProfile StormWeapon = new()
        { Directionality = 0.9f, BlastFraction = 0.15f, Knockback = 0f };

    public BorderHazardSystem(float worldW, float worldH, BorderHazardConfig cfg, Random rng, ParticleEffects fx)
    { _worldW = worldW; _worldH = worldH; _cfg = cfg; _rng = rng; _fx = fx; }

    public void Update(World world, double dt)
    {
        if (!_cfg.Enabled) return;
        float fdt = (float)dt;

        // ── Pass 1: damp outward velocity + inward push (every moving body) ──
        float dampZone = _cfg.DampZone, dampK = _cfg.DampStrength;
        float pushZone = _cfg.PushZone, pushK = _cfg.PushStrength;
        world.ForEach<Transform, Velocity>((Entity e, ref Transform t, ref Velocity v) =>
        {
            float x = t.Position.X, y = t.Position.Y;

            // Projectiles aren't contained — the rim EATS them: they fly on freely and are destroyed
            // (with a fizzle) the moment they cross the world edge, instead of being damped/pushed
            // back and piling up against an invisible wall.
            if (world.HasComponent<BulletTag>(e) || world.HasComponent<PiercingRoundTag>(e))
            {
                if (x < 0f || y < 0f || x > _worldW || y > _worldH)
                {
                    _fx.EmitFlash(t.Position, 9000f);
                    world.DestroyEntity(e);
                }
                return;
            }

            // Inbound wave spawns live in the ring OUTSIDE the field: leave them alone until they
            // cross in (damping/clamping would teleport them to the edge), then contain them like
            // everything else. Removal is deferred, so pass 2 still sees the tag this frame.
            if (world.HasComponent<InboundSpawn>(e))
            {
                if (x < 0f || y < 0f || x > _worldW || y > _worldH) return;
                world.RemoveComponent<InboundSpawn>(e);
            }

            float dL = x, dR = _worldW - x, dT = y, dB = _worldH - y;

            if (dampZone > 1f)
            {
                if (dL < dampZone && v.Linear.X < 0f) v.Linear.X *= MathF.Exp(-dampK * (1f - dL / dampZone) * fdt);
                if (dR < dampZone && v.Linear.X > 0f) v.Linear.X *= MathF.Exp(-dampK * (1f - dR / dampZone) * fdt);
                if (dT < dampZone && v.Linear.Y < 0f) v.Linear.Y *= MathF.Exp(-dampK * (1f - dT / dampZone) * fdt);
                if (dB < dampZone && v.Linear.Y > 0f) v.Linear.Y *= MathF.Exp(-dampK * (1f - dB / dampZone) * fdt);
            }

            if (pushZone > 1f && pushK > 0f)
            {
                if (dL < pushZone) v.Linear.X += pushK * (1f - dL / pushZone) * fdt;
                if (dR < pushZone) v.Linear.X -= pushK * (1f - dR / pushZone) * fdt;
                if (dT < pushZone) v.Linear.Y += pushK * (1f - dT / pushZone) * fdt;
                if (dB < pushZone) v.Linear.Y -= pushK * (1f - dB / pushZone) * fdt;
            }

            t.Position = Vector2.Clamp(t.Position, Vector2.Zero, new Vector2(_worldW, _worldH));
        });

        // ── Pass 2: erosion exposure over fracturable bodies ──
        float hz = _cfg.HazardZone;
        _erode.Clear();
        world.ForEach<Transform, FracturableBody>((Entity e, ref Transform t, ref FracturableBody fb) =>
        {
            // Inbound spawns sit OUTSIDE the field, where the depth math below reads as "deep in
            // the rim" and would erode them before they even arrive. Piercing rounds are projectiles
            // (eaten at the edge in pass 1), not camping bodies — don't erode them either.
            if (world.HasComponent<InboundSpawn>(e) || world.HasComponent<PiercingRoundTag>(e)) return;

            float x = t.Position.X, y = t.Position.Y;
            // margin past each inner boundary (>0 = inside that rim); depth = deepest one
            float mL = hz - x, mR = hz - (_worldW - x), mT = hz - y, mB = hz - (_worldH - y);
            float depth = MathF.Max(MathF.Max(mL, mR), MathF.Max(mT, mB));

            _exposure.TryGetValue(e, out var ex);
            if (depth > 0f)
            {
                ex.Time += fdt;
                if (ex.Time > _cfg.Grace)
                {
                    ex.TickAcc += fdt;
                    if (ex.TickAcc >= _cfg.Tick && fb.Cells.Length > 0)
                    {
                        ex.TickAcc = 0f;
                        // outward normal of the nearest border
                        Vector2 n = (mL >= mR && mL >= mT && mL >= mB) ? new Vector2(-1f, 0f)
                                  : (mR >= mT && mR >= mB)             ? new Vector2( 1f, 0f)
                                  : (mT >= mB)                         ? new Vector2( 0f,-1f)
                                  :                                      new Vector2( 0f, 1f);
                        // most-exposed cell = furthest along the outward normal (world space)
                        float c = MathF.Cos(t.Rotation), s = MathF.Sin(t.Rotation);
                        int best = 0; float bestProj = float.NegativeInfinity; Vector2 bestW = t.Position;
                        for (int i = 0; i < fb.Cells.Length; i++)
                        {
                            var cc = fb.Cells[i].Centroid;
                            Vector2 w = new(cc.X * c - cc.Y * s + t.Position.X, cc.X * s + cc.Y * c + t.Position.Y);
                            float proj = Vector2.Dot(w, n);
                            if (proj > bestProj) { bestProj = proj; best = i; bestW = w; }
                        }
                        float extra = ex.Time - _cfg.Grace;
                        float mass  = _cfg.BaseMass * (1f + _cfg.Ramp * extra);
                        _erode.Add((e, best, bestW, -n, mass));   // fracture inward, on the exposed face
                    }
                }
            }
            else
            {
                ex.Time    = MathF.Max(0f, ex.Time - fdt * _cfg.DecayRate);
                ex.TickAcc = 0f;
            }

            if (ex.Time <= 0f) _exposure.Remove(e);
            else               _exposure[e] = ex;
        });

        // Apply erosion after iteration (BeginFracture adds a FractureProcess component).
        foreach (var (body, cell, point, dir, mass) in _erode)
            FractureService.BeginFracture(world, body, cell, point, dir, _cfg.ImpactSpeed, mass, StormWeapon, _rng);

        // Forget bodies that died so the dictionary can't grow unbounded on entity recycle.
        if (_exposure.Count > 0)
        {
            _stale.Clear();
            foreach (var kv in _exposure) if (!world.IsAlive(kv.Key)) _stale.Add(kv.Key);
            foreach (var k in _stale) _exposure.Remove(k);
        }
    }
}

public sealed class GhostSystem : ISystem
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
                else if (world.HasComponent<PiercingRoundTag>(e))
                {
                    // A piercing shard: bullet layer like the round it came from (it's a sensor,
                    // so it passes through; the layer just decides what it pierces). Ghost included
                    // so shards keep biting fragments of a still-splitting target.
                    c.Layer = GameLayers.Bullet;
                    c.Mask  = GameLayers.Asteroid | GameLayers.Alien | GameLayers.Ghost;
                }
                else
                {
                    c.Layer = GameLayers.Asteroid;
                    c.Mask  = GameLayers.Asteroid | GameLayers.Player;
                }
                g.Done = true;
            }
        });

        // Piercing rounds spawn inside the firing ship; restore the Player collision bit
        // once the brief grace expires (the round has cleared the ship's shape by then).
        float fdt = (float)dt;
        world.ForEach<PiercingRoundTag, Collider>((Entity _, ref PiercingRoundTag p, ref Collider c) =>
        {
            if (p.PlayerGrace <= 0f) return;
            p.PlayerGrace -= fdt;
            if (p.PlayerGrace <= 0f) c.Mask |= GameLayers.Player;
        });
    }
}

public sealed class TimeToLiveSystem : ISystem
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

/// <summary>Relaxes per-bond Stress and per-cell Damage over time so a body only cracks/pulverises
/// if hits land faster than the material heals (per-material RelaxRate) — gates sustained fire.</summary>
public sealed class StressRelaxSystem : ISystem
{
    public void Update(World world, double dt)
    {
        float fdt = (float)dt;
        world.ForEach<FracturableBody>((Entity _, ref FracturableBody fb) =>
        {
            float dec = fb.Material.RelaxRate * fdt;
            if (dec <= 0f) return;
            var bonds = fb.Bonds;
            for (int i = 0; i < bonds.Length; i++)
                if (!bonds[i].Broken && bonds[i].Stress > 0f)   // broken bonds stay broken — no healing
                    bonds[i].Stress = MathF.Max(0f, bonds[i].Stress - dec);
            var cells = fb.Cells;
            for (int i = 0; i < cells.Length; i++)
                if (cells[i].Damage > 0f)
                    cells[i].Damage = MathF.Max(0f, cells[i].Damage - dec);
        });
    }
}

public sealed class FractureGroupSystem : ISystem
{
    public void Update(World world, double dt)
        => world.ForEach<FractureGroup>((Entity e, ref FractureGroup fg) =>
        {
            if (--fg.FramesLeft <= 0) world.RemoveComponent<FractureGroup>(e);
        });
}

public sealed class EventFlushSystem : ISystem
{
    private readonly EventBus _bus;
    public EventFlushSystem(EventBus bus) => _bus = bus;
    public void Update(World world, double dt) => _bus.Flush();
}

public sealed class RaycastBulletSystem : ISystem
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

        float fdt = (float)dt;
        foreach (var (bullet, from, to) in _seg)
        {
            if (!world.IsAlive(bullet)) continue;

            // Per-bullet drag (raycast bullets bypass PhysicsSystem) + owner-grace bookkeeping.
            Entity owner = default; bool inGrace = false;
            if (world.HasComponent<BulletData>(bullet))
            {
                ref var bd = ref world.GetComponent<BulletData>(bullet);
                if (bd.Drag > 0f && world.HasComponent<Velocity>(bullet))
                {
                    ref var v = ref world.GetComponent<Velocity>(bullet);
                    v.Linear *= MathF.Exp(-bd.Drag * fdt);   // decays next frame's travel
                }
                if (bd.OwnerGrace > 0f) { inGrace = true; owner = bd.Owner; bd.OwnerGrace -= fdt; }
            }

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

            // Ghost = fresh fracture fragments: exempt from PHYSICS for a beat so newborn siblings
            // don't explode apart, but weapons must still see them — a continuously splitting body
            // (sand!) re-ghosts its pieces every few frames, and without this bit bullets fly
            // straight through it unconsumed.
            int hitMask = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien | GameLayers.Ghost;
            if (PhysicsQueries.Raycast(world, from, to, hitMask, out var hit))
            {
                if (inGrace && hit.Entity == owner) continue;   // let the bullet clear its own hull
                _bus.Publish(new BulletHitEvent(hit.Entity, bullet, hit.PartIndex,
                                                hit.Point, Vector2.Normalize(d)));
            }
        }
    }
}

public sealed class AlienAiSystem : ISystem
{
    private readonly GameContext _ctx;
    private readonly EventBus    _bus;
    private readonly Random      _rng;

    // Cached per-frame lookups.
    private Entity   _playerEntity;
    private Vector2  _playerPos;
    private bool     _playerAlive;
    private float    _time;   // accumulates dt; drives lazy wander headings

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
        _time += fdt;

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

            // Engagement: inside AggroRadius the alien actively engages the player; beyond it (or with
            // no player) it ignores the player and wanders aimlessly. AggroRadius <= 0 = always aggro.
            float distToPlayer = _playerAlive ? (pos - _playerPos).Length() : float.MaxValue;
            bool  aggro        = _playerAlive && (cfg.AggroRadius <= 0f || distToPlayer <= cfg.AggroRadius);

            // Choose thrust direction.
            Vector2 thrustDir;
            if (!aggro)
                thrustDir = WanderDir(e.Id);                                   // aimless drift
            else if (av.Key == "bruiser")
                thrustDir = Vector2.Normalize(_playerPos - pos);               // ram: straight at the player
            else
                thrustDir = DroneSteering(pos, KiteGoal(pos, distToPlayer, cfg), asteroidPos, cfg); // kite

            // Bias thrust direction toward ship facing (lateral penalty) then drive via velocity model.
            ApplyLateralPenalty(ref thrustDir, facingDir, cfg.LateralThrustPenaltyMult);

            if (world.HasComponent<Velocity>(e))
            {
                ref var vel = ref world.GetComponent<Velocity>(e);

                float propFrac = AlivePropellerFraction(world, e);
                float speedMul = aggro ? 1f : 0.45f;   // wander cruises slower than an engaged pursuit
                if (propFrac > 0f && thrustDir.LengthSquared() > 1e-6f)
                {
                    Vector2 desiredVel = Vector2.Normalize(thrustDir) * (cfg.Speed * propFrac * speedMul);
                    Vector2 dvDiff     = desiredVel - vel.Linear;
                    float   dvLen      = dvDiff.Length();
                    float   step       = cfg.Thrust * fdt;
                    if (dvLen > 0f)
                        vel.Linear += dvLen <= step ? dvDiff : dvDiff / dvLen * step;
                }

                // Face the player while engaged. Kinematic turn: drive angular velocity straight at
                // the bearing (clamped to a max turn rate) so an orbiting drone tracks without the
                // steady-state lag a damped PD controller leaves — its nose stays on target.
                if (aggro)
                {
                    Vector2 targetDir = Vector2.Normalize(_playerPos - pos);
                    float targetAngle = MathF.Atan2(targetDir.Y, targetDir.X) + MathF.PI * 0.5f;
                    float diff        = NormalizeAngle(targetAngle - t.Rotation);
                    float maxTurn     = cfg.TurnSpeed;   // rad/s cap (config-driven)
                    vel.Angular = Math.Clamp(diff * 12f, -maxTurn, maxTurn);
                }
                else
                {
                    vel.Angular *= MathF.Exp(-5f * fdt);   // damp residual spin while wandering
                }

                // ── Skill: dash — a high-cooldown lunge toward the player when close (bruiser ram finisher).
                if (aggro && cfg.Dash is { } dash && world.HasComponent<AlienSkillState>(e))
                {
                    ref var ss = ref world.GetComponent<AlienSkillState>(e);
                    ss.DashCd = MathF.Max(0f, ss.DashCd - fdt);
                    // Dash goes along the nose, so only lunge once roughly pointed at the player (~25°),
                    // otherwise it would charge off in the wrong direction.
                    bool aimedAtPlayer = distToPlayer > 1f &&
                        Vector2.Dot(facingDir, Vector2.Normalize(_playerPos - pos)) >= 0.83f;
                    if (ss.DashCd <= 0f && propFrac > 0f && aimedAtPlayer &&
                        distToPlayer <= dash.TriggerRange)
                    {
                        vel.Linear += facingDir * dash.Speed;   // lunge along the nose, not at the player
                        ss.DashCd   = dash.Cooldown;
                    }
                }
            }

            // Fire only while engaged, in range, and when cooldown allows.
            if (!aggro || !world.HasComponent<ShootCooldown>(e)) return;
            ref var cd = ref world.GetComponent<ShootCooldown>(e);
            if (cd.Remaining > 0f) { cd.Remaining = MathF.Max(0f, cd.Remaining - fdt); return; }

            float distSq = distToPlayer * distToPlayer;
            if (distSq > cfg.DetectionRadius * cfg.DetectionRadius) return;

            float aliveWeaponFrac = AliveWeaponFraction(world, e);
            if (aliveWeaponFrac <= 0f) return;

            // Hold fire until the nose is pointed at the player (shots go along facing, so an orbiting
            // drone must be aligned to hit). cd stays ready, so it fires the instant it lines up.
            if (Vector2.Dot(facingDir, Vector2.Normalize(_playerPos - pos)) < 0.97f) return;

            cd.Remaining = cfg.ShootCooldown * _ctx.Difficulty.EnemyFireMult / MathF.Max(0.01f, aliveWeaponFrac);
            Vector2 aimDir = facingDir;   // fire along the nose; rotation steers aim toward the player
            toFire.Add((e, av.Key, aimDir, pos + aimDir * 30f));
        });

        // Spawn shots outside the ForEach loop.
        foreach (var (alien, varKey, dir, muzzle) in toFire)
            FireAlienWeapon(world, alien, varKey, dir, muzzle);
    }

    // A slow, lazily-rotating heading per alien (deterministic from id+time) for aimless wandering.
    private Vector2 WanderDir(int id)
    {
        float a = _time * 0.25f + id * 1.3f;
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    // Kiting goal direction: hold PreferredRange from the player — close in when farther, back off
    // when closer, strafe tangentially when near it. PreferredRange <= 0 = pursue directly.
    private Vector2 KiteGoal(Vector2 pos, float dist, EntityConfig cfg)
    {
        Vector2 toPlayer = _playerPos - pos;
        float   d        = toPlayer.Length();
        Vector2 pdir     = d > 1f ? toPlayer / d : Dirs8[0];

        float pref = cfg.PreferredRange;
        if (pref <= 0f) return pdir;                          // no standoff → pursue

        float band = MathF.Max(40f, pref * 0.15f);
        if (dist > pref + band) return pdir;                  // too far → approach
        if (dist < pref - band) return -pdir;                 // too close → back off
        return new Vector2(-pdir.Y, pdir.X);                  // in the band → strafe (orbit)
    }

    // Context steering: pursue the supplied goal direction while avoiding asteroids and map borders.
    private Vector2 DroneSteering(Vector2 pos, Vector2 goalDir, List<Vector2> asteroids, EntityConfig cfg)
    {
        Span<float> interest = stackalloc float[8];
        Span<float> danger   = stackalloc float[8];

        float sw = cfg.SteeringWeights?.Pursuit    ?? 1f;
        float av = cfg.SteeringWeights?.Avoidance  ?? 1f;
        float sp = cfg.SteeringWeights?.Separation ?? 1f;

        for (int i = 0; i < 8; i++)
            interest[i] = MathF.Max(0f, Vector2.Dot(Dirs8[i], goalDir)) * sw;

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
        // No propeller cells (e.g. a cockpit-only fragment) → cannot thrust, so it just drifts.
        return total == 0 ? 0f : (float)alive / total;
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
        string weaponKey = varKey == "bruiser" ? "shotgun_alien" : "cannon_alien";
        if (!_ctx.Config.Weapons.TryGetValue(weaponKey, out var wcfg)) return;

        if (varKey != "bruiser")
        {
            WeaponEffects.SpawnBullet(world, muzzle, dir * wcfg.ProjectileSpeed, weaponKey,
                wcfg.TimeToLive, new Color(220, 80, 80), alien, WeaponEffects.BulletGrace,
                wcfg.Drag, alien: true);
        }
        else
        {
            int   rays = wcfg.Rays ?? 7;
            WeaponEffects.SpawnCone(world, muzzle, dir, rays, wcfg.ConeAngle ?? 18f,
                wcfg.ProjectileSpeed, wcfg.TimeToLive, weaponKey, new Color(220, 100, 60),
                alien, WeaponEffects.BulletGrace, wcfg, alien: true, _rng);
        }
    }
}

public sealed class BlackHoleSystem : ISystem
{
    public void Update(World world, double dt)
    {
        var holes = new List<(Vector2 pos, float radius, float strength)>();
        world.ForEach<Transform, BlackHoleTag>((Entity _, ref Transform t, ref BlackHoleTag bh) =>
            holes.Add((t.Position, bh.Radius, bh.Strength)));
        if (holes.Count == 0) return;

        float fdt = (float)dt;
        world.ForEach<Transform, Velocity, RigidBody>((Entity e, ref Transform t, ref Velocity v, ref RigidBody _) =>
        {
            if (world.HasComponent<MothershpId>(e)) return;   // the boss isn't pulled by its own hole
            foreach (var (hpos, radius, strength) in holes)
            {
                Vector2 delta = hpos - t.Position;
                float   dSq   = delta.LengthSquared();
                if (dSq < 1f || dSq > radius * radius) continue;
                float dist  = MathF.Sqrt(dSq);
                // Gravity accelerates everything equally regardless of mass (÷mass made it ~0 for heavy
                // asteroids). strength is now an acceleration scale (px/s² at dist≈0).
                float accel = strength / (dist + 1f);
                v.Linear += delta / dist * accel * fdt;
            }
        });
    }
}

/// <summary>Owns grenades: advances their fuse and detonates them on contact (the latter via a
/// CollisionEvent subscription). Detonation itself (shrapnel spawn) is handled by the
/// GrenadeDetonateEvent listener in FractureGameplay.</summary>
public sealed class GrenadeSystem : ISystem
{
    private readonly World    _world;
    private readonly EventBus _bus;
    public GrenadeSystem(World world, EventBus bus)
    {
        _world = world; _bus = bus;
        _bus.Subscribe<CollisionEvent>(OnContact);
    }

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

    // Detonate the moment a grenade contacts anything.
    private void OnContact(CollisionEvent ev)
    {
        Entity g = _world.HasComponent<GrenadeFuse>(ev.EntityA) ? ev.EntityA
                 : _world.HasComponent<GrenadeFuse>(ev.EntityB) ? ev.EntityB
                 : default;
        if (_world.IsAlive(g) && _world.HasComponent<GrenadeFuse>(g))
            _bus.Publish(new GrenadeDetonateEvent(g, ev.Contact.ContactPoint,
                                                  _world.GetComponent<GrenadeFuse>(g).WeaponKey));
    }
}

/// <summary>
/// Owns piercing-round impacts as a swept-segment penetration budget. The round carries a LIFETIME
/// PenetrationPower (set at spawn from its own kinetic energy); each frame we take the cells its
/// travel actually crossed — in order, entry first — and make it PAY for each one (the cell's
/// pulverise threshold plus the residual strength of its intact bonds, × PenetrationCostScale).
/// Damage per cell is its own dial (PierceDamageScale × cost, deposited via DepositEnergy), and
/// speed fades with the budget (v = v₀·(P/P₀)^PierceSpeedExponent) — tunnel length, collateral and
/// deceleration tune independently. When a cell costs more than what's left, the round craters the
/// face it stopped on and SHATTERS there.
/// </summary>
public sealed class ProjectileSystem : ISystem
{
    private readonly World        _world;
    private readonly GameContext  _ctx;
    private readonly EventBus      _bus;
    private readonly Random        _rng;
    private readonly Func<Entity>  _player;   // live player (may transfer to a cockpit fragment)

    private readonly List<(int Part, float T, Vector2 Point)> _crossed = new();   // reused per contact

    public ProjectileSystem(World world, GameContext ctx, EventBus bus, Random rng, Func<Entity> player)
    {
        _world = world; _ctx = ctx; _bus = bus; _rng = rng; _player = player;
        _bus.Subscribe<CollisionEvent>(OnContact);
    }

    public void Update(World world, double dt) { }   // impact-driven; nothing per-frame yet

    private void OnContact(CollisionEvent ev)
    {
        Entity eA = ev.EntityA, eB = ev.EntityB;
        if (!_world.IsAlive(eA) || !_world.IsAlive(eB)) return;

        Entity piercing = _world.HasComponent<PiercingRoundTag>(eA) ? eA
                        : _world.HasComponent<PiercingRoundTag>(eB) ? eB : default;
        if (!_world.IsAlive(piercing)) return;
        Entity target = piercing == eA ? eB : eA;
        if (!_world.IsAlive(target) || !_world.HasComponent<FracturableBody>(target)) return;
        if (!_ctx.Config.Weapons.TryGetValue("piercing", out var pcfg)) return;
        if (!_world.HasComponent<Transform>(piercing) || !_world.HasComponent<Velocity>(piercing)) return;
        if (!_world.HasComponent<Transform>(target)   || !_world.HasComponent<Collider>(target)) return;
        if (_world.GetComponent<Collider>(target).Shape is not CompoundShape shape) return;

        ref var pt = ref _world.GetComponent<PiercingRoundTag>(piercing);
        var ptr = _world.GetComponent<Transform>(piercing);
        var ttr = _world.GetComponent<Transform>(target);

        // Every cell this frame's travel actually crossed, entry first — not one guessed cell per
        // contact, which is what left whole slabs on the line of penetration untouched.
        shape.SegmentParts(ttr.Position, ttr.Rotation, ptr.PreviousPosition, ptr.Position, _crossed);
        if (_crossed.Count == 0) return;

        float roundMass  = _world.HasComponent<RigidBody>(piercing) ? _world.GetComponent<RigidBody>(piercing).Mass : 1f;
        float targetMass = _world.HasComponent<RigidBody>(target)   ? _world.GetComponent<RigidBody>(target).Mass   : 1f;

        // Penetration speed = closing speed along the round's own axis. (The contact normal is a cell
        // FACE normal, often near-perpendicular to travel, so ev.ApproachSpeed is the wrong quantity
        // for a body tunnelling through rather than bouncing off.)
        Vector2 relVel = _world.GetComponent<Velocity>(piercing).Linear
                       - (_world.HasComponent<Velocity>(target) ? _world.GetComponent<Velocity>(target).Linear : Vector2.Zero);
        float speed = MathF.Abs(Vector2.Dot(relVel, pt.Direction));
        if (speed < 1e-3f) return;

        var pProfile    = pcfg.ToWeaponProfile();
        float costScale = pcfg.PenetrationCostScale ?? 1f;
        float dmgScale  = pcfg.PierceDamageScale ?? 1f;
        float dmgMult   = target == _player() ? dmgScale * _ctx.PlayerImpactCoeff : dmgScale;

        ref var fb = ref _world.GetComponent<FracturableBody>(target);

        float p0 = pt.Power;   // budget entering this frame's walk (for the speed fade)

        bool stopped = false;
        Vector2 stopPoint = _crossed[0].Point;

        foreach (var (part, t, point) in _crossed)
        {
            // The cell it is already sitting in was paid for on the frame it entered.
            if (t <= 0f && target == pt.LastTarget && part == pt.LastCell) continue;

            float cost = PenetrationCost(fb, part) * costScale;

            if (pt.Power < cost)
            {
                // Can't get through this cell: dump what's left into its face, then shatter on it.
                stopped   = true;
                stopPoint = point;
                FractureService.DepositEnergy(_world, target, part, point, pt.Direction,
                                              MathF.Max(0f, pt.Power) * dmgMult, pProfile, speed);
                pt.Power = 0f;
                pt.LastTarget = target; pt.LastCell = part;
                break;
            }

            // Pay for it and crack it. Damage is metered independently of the budget (PierceDamageScale):
            // 1 = just enough to carve the tunnel cell, higher shatters around it, lower leaves a needle.
            pt.Power -= cost;
            FractureService.DepositEnergy(_world, target, part, point, pt.Direction,
                                          cost * dmgMult, pProfile, speed);
            pt.LastTarget = target; pt.LastCell = part;
        }

        ref var pv = ref _world.GetComponent<Velocity>(piercing);

        if (stopped)
        {
            // Shatters on the surface: the target's mass drives the reciprocal fracture, and the
            // round's forward motion dies so its shards spray off the face instead of carrying on.
            FractureService.BeginFracture(_world, piercing, -1, stopPoint, -pt.Direction,
                                          speed, targetMass, pProfile, _rng);
            pv.Linear *= StopVelocityRetain;
        }
        else if (pt.Power < p0)
        {
            // Speed fades with the budget: v = v₀·(P/P₀)^exponent, applied incrementally per walk
            // (the per-step ratios compose to exactly that power law).
            float exp     = pcfg.PierceSpeedExponent ?? 0.5f;
            float k       = MathF.Pow(pt.Power / MathF.Max(1e-3f, p0), exp);
            float fwdComp = Vector2.Dot(pv.Linear, pt.Direction);
            float newFwd  = MathF.Max(0f, fwdComp) * k;
            Vector2 lat   = pv.Linear - pt.Direction * fwdComp;
            float latLen  = lat.Length();
            float maxLat  = pt.LateralClamp * newFwd;
            if (latLen > maxLat && latLen > 1e-4f) lat = lat / latLen * maxLat;
            pv.Linear = pt.Direction * newFwd + lat;
        }

        // Nudge the struck body forward — mass-scaled, so heavy asteroids barely move.
        if (target != _player() && _world.HasComponent<Velocity>(target))
        {
            float push = (pcfg.TargetPushCoeff ?? 0f) * roundMass * speed / MathF.Max(1f, targetMass);
            ref var tv = ref _world.GetComponent<Velocity>(target);
            tv.Linear += pt.Direction * push;
        }
    }

    /// <summary>What a cell charges to be punched through: the energy to pulverise it outright plus
    /// the strength left in the bonds still holding it to the body.</summary>
    private static float PenetrationCost(in FracturableBody fb, int cell)
    {
        if ((uint)cell >= (uint)fb.Cells.Length) return 0f;
        var m = fb.Material;
        float cost = MathF.Max(0f, m.CellToughness * fb.Cells[cell].Area
                                 * fb.Cells[cell].DensityMult * m.Density - fb.Cells[cell].Damage);
        for (int i = 0; i < fb.Bonds.Length; i++)
        {
            ref var b = ref fb.Bonds[i];
            if (b.Broken || (b.A != cell && b.B != cell)) continue;
            cost += MathF.Max(0f, b.Strength - b.Stress);
        }
        return cost;
    }

    private const float StopVelocityRetain = 0.05f;   // forward motion left after a failed pierce
}
