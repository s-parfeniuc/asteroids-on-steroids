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
            ref var dashVel = ref world.GetComponent<Velocity>(Player);
            dashVel.Linear += aim.Dir * (dashCfg.VelocitySpike ?? 1400f);
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
                float maxTurn  = _ctx.Config.Player.RotSpeed * fdt;
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

        // ── Cannon (left-click) ───────────────────────────────────────────────
        if (inp.IsMouseLeft && wcd.Cannon <= 0f && IsWeaponCellAlive("cannon", world)
            && _ctx.Config.Weapons.TryGetValue("cannon", out var ccfg))
        {
            wcd.Cannon = 1f / MathF.Max(0.001f, ccfg.FireRate);
            WeaponEffects.SpawnBullet(world, muzzle, aim.Dir * ccfg.ProjectileSpeed, "cannon",
                ccfg.TimeToLive, new Color(255, 230, 90), Player, WeaponEffects.BulletGrace, ccfg.Drag, alien: false);
        }

        // ── Shotgun (right-click) ─────────────────────────────────────────────
        if (inp.IsMouseRight && wcd.Shotgun <= 0f && IsWeaponCellAlive("shotgun", world)
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

        world.ForEach<Transform, Velocity, RigidBody, VortexResponse>(
            (Entity _, ref Transform t, ref Velocity v, ref RigidBody _, ref VortexResponse vr) =>
        {
            Vector2 toCenter = _centre - t.Position;
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

public sealed class BorderDampSystem : ISystem
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
                if (bonds[i].Stress > 0f)
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

            int hitMask = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
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

            // Bias thrust direction toward ship facing (lateral penalty) then drive via velocity model.
            ApplyLateralPenalty(ref thrustDir, facingDir, cfg.LateralThrustPenaltyMult);

            if (world.HasComponent<Velocity>(e))
            {
                ref var vel = ref world.GetComponent<Velocity>(e);

                float propFrac = AlivePropellerFraction(world, e);
                if (propFrac > 0f && thrustDir.LengthSquared() > 1e-6f)
                {
                    Vector2 desiredVel = Vector2.Normalize(thrustDir) * (cfg.Speed * propFrac);
                    Vector2 dvDiff     = desiredVel - vel.Linear;
                    float   dvLen      = dvDiff.Length();
                    float   step       = cfg.Thrust * fdt;
                    if (dvLen > 0f)
                        vel.Linear += dvLen <= step ? dvDiff : dvDiff / dvLen * step;
                }

                if (_playerAlive)
                {
                    Vector2 targetDir = Vector2.Normalize(_playerPos - pos);
                    float targetAngle = MathF.Atan2(targetDir.Y, targetDir.X) + MathF.PI * 0.5f;
                    float diff        = NormalizeAngle(targetAngle - t.Rotation);
                    float rotSpeed    = av.Key == "bruiser" ? 2.5f : 4f;
                    vel.Angular += diff * rotSpeed * fdt;
                    vel.Angular *= MathF.Exp(-5f * fdt);
                }
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

public sealed class GrenadeSystem : ISystem
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
