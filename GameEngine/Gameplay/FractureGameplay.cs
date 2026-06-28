using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// The shared fracture-response gameplay — bullet/collision/grenade/piercing impacts,
/// cell pulverisation (scoring, cockpit→game-over), and fragment spawning on
/// fracture-complete / mid-fracture split (incl. player-transfer and mothership tracking).
///
/// Identical in the game and the demo: both construct one of these so the demo's weapons
/// and destruction behave exactly like the game. The owning shell reads <see cref="Player"/>
/// (it changes when the player ship loses its cockpit and control transfers to a fragment)
/// and <see cref="PendingGameOver"/>.
/// </summary>
public sealed class FractureGameplay
{
    private const float PiercingFragmentTtl = 2.5f;   // round shards fade rather than litter the map

    private readonly World           _world;
    private readonly EventBus        _bus;
    private readonly GameContext     _ctx;
    private readonly Random          _rng;
    private readonly ParticleEffects _effects;

    private int _nextGroupId = 1;
    private readonly HashSet<(int, int)> _activeCollisions = new();

    /// <summary>The live player entity. Changes when control transfers to a surviving
    /// cockpit fragment after the ship breaks up.</summary>
    public Entity Player { get; set; }

    /// <summary>Set when the player's cockpit is destroyed (cell pulverised or no cockpit
    /// fragment survived a break-up). The game transitions to game-over; the demo may ignore it.</summary>
    public bool PendingGameOver { get; set; }

    public FractureGameplay(World world, EventBus bus, GameContext ctx, Random rng, ParticleEffects effects)
    {
        _world = world; _bus = bus; _ctx = ctx; _rng = rng; _effects = effects;
        _bus.Subscribe<BulletHitEvent>(OnBulletHit);
        _bus.Subscribe<CollisionEvent>(OnCollision);
        _bus.Subscribe<CellPulverizedEvent>(OnCellPulverized);
        _bus.Subscribe<FractureCompletedEvent>(OnFractureCompleted);
        _bus.Subscribe<FractureSplitEvent>(OnFractureSplit);
        _bus.Subscribe<GrenadeDetonateEvent>(OnGrenadeDetonate);
    }

    /// <summary>Allocates the next fracture-group id. Shared with the shell's own
    /// group-tracking (e.g. mothership spawn) so ids never collide.</summary>
    public int AllocateGroupId() => _nextGroupId++;

    // ── Body spawning ─────────────────────────────────────────────────────────

    private Entity SpawnBody(FracturableBody body, Vector2 pos, float rot,
        Vector2 vel, float spin, BodyColor color, bool ghost = false)
    {
        float area    = VoronoiTessellator.TotalArea(body);
        float mass    = MathF.Max(1f, body.Material.Density * area);
        float inertia = VoronoiTessellator.ComputeInertia(body, mass);
        return FractureBodyFactory.Spawn(_world, _ctx.Config.Physics, body, pos, rot,
            vel, spin, mass, inertia, color, ghost, ghostRemaining: 0.04f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        Player = ne;
    }

    // Asteroid fragments inherit the asteroid tags. (Alien/mothership fragments are tagged
    // separately — only cockpit-bearing pieces stay live aliens; see TagAlienFragment.)
    private void CopyTags(Entity source, Entity target)
    {
        if (!_world.IsAlive(source) || !_world.HasComponent<AsteroidTag>(source)) return;
        _world.AddComponent(target, new AsteroidTag());
        if (_world.HasComponent<AsteroidVariant>(source))
            _world.AddComponent(target, _world.GetComponent<AsteroidVariant>(source));
    }

    // Makes a fragment a live alien: tag, variant, fire cooldown, and the alien collider
    // layer. Movement/fire then self-gate on surviving propeller/weapon cells (AlienAiSystem).
    private void TagAlienFragment(Entity ne, AlienVariant av, float shootCooldown)
    {
        _world.AddComponent(ne, new AlienTag());
        _world.AddComponent(ne, av);
        _world.AddComponent(ne, new ShootCooldown { Remaining = shootCooldown });
        if (_world.HasComponent<Collider>(ne))
        {
            ref var col = ref _world.GetComponent<Collider>(ne);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
    }

    private bool IsDashInvincible() =>
        _world.IsAlive(Player) && _world.HasComponent<SkillState>(Player)
        && _world.GetComponent<SkillState>(Player).DashActive > 0f;

    // ── Fracture event handlers ───────────────────────────────────────────────

    private void OnGrenadeDetonate(GrenadeDetonateEvent ev)
    {
        float flashEnergy = WeaponEffects.SpawnShrapnel(_world, _ctx.Config, ev.Grenade, ev.WorldPos, ev.WeaponKey, _rng);
        if (flashEnergy > 0f) _effects.EmitFlash(ev.WorldPos, flashEnergy);
    }

    private void OnBulletHit(BulletHitEvent ev)
    {
        if (!_world.IsAlive(ev.Target) || !_world.IsAlive(ev.Bullet)) return;
        Vector2 bulletVel = _world.GetComponent<Velocity>(ev.Bullet).Linear;
        _world.DestroyEntity(ev.Bullet);

        // Dash invincibility — bullet still consumed but no fracture applied to player.
        if (ev.Target == Player && IsDashInvincible()) return;

        bool hasData  = _world.HasComponent<BulletData>(ev.Bullet);
        string weaponKey = hasData ? _world.GetComponent<BulletData>(ev.Bullet).WeaponKey
                                   : _ctx.Config.Player.StartingWeapon;

        // Grenade hits detonate instead of fracturing directly.
        if (hasData && _world.HasComponent<GrenadeFuse>(ev.Bullet))
        {
            var fuse = _world.GetComponent<GrenadeFuse>(ev.Bullet);
            _bus.Publish(new GrenadeDetonateEvent(ev.Bullet, ev.Point, fuse.WeaponKey));
            return;
        }

        if (!_ctx.Config.Weapons.TryGetValue(weaponKey, out var weaponCfg)) return;
        WeaponProfile profile = weaponCfg.ToWeaponProfile();
        var frac = _ctx.Config.Fracture;
        // Real impactor mass: the weapon's own Mass, else the global BulletMass. EnergyScale converts
        // to fracture units; per-weapon damage also differs through projectile SPEED (E ∝ v²) and count.
        float bulletMass = weaponCfg.Mass ?? frac.BulletMass;

        _effects.EmitFlash(ev.Point, 0.5f * bulletMass * bulletVel.LengthSquared());

        // Scale fracture energy by target's impact coefficient.
        float adjMass = bulletMass;
        if (ev.Target == Player)
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
            profile, _rng);
    }

    private void OnCollision(CollisionEvent ev)
    {
        Entity eA = ev.EntityA, eB = ev.EntityB;
        if (!_world.IsAlive(eA) || !_world.IsAlive(eB)) return;

        // Grenade detonates on contact — check before FracturableBody guard.
        Entity grEnt = _world.HasComponent<GrenadeFuse>(eA) ? eA
                     : _world.HasComponent<GrenadeFuse>(eB) ? eB
                     : default;
        if (_world.IsAlive(grEnt) && _world.HasComponent<GrenadeFuse>(grEnt))
        {
            var fuse = _world.GetComponent<GrenadeFuse>(grEnt);
            _bus.Publish(new GrenadeDetonateEvent(grEnt, ev.Contact.ContactPoint, fuse.WeaponKey));
            return;
        }

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
                // Real impactor mass (the round's physical mass) — EnergyScale handles the units.
                float pMass = pcfg.Mass ?? _ctx.Config.Fracture.BulletMass;
                Vector2 pVel = _world.HasComponent<Velocity>(piercing)
                    ? _world.GetComponent<Velocity>(piercing).Linear : Vector2.Zero;
                float adjMass = target == Player ? pMass * _ctx.Config.Player.PlayerImpactCoeff : pMass;
                Vector2 pcp = ev.Contact.ContactPoint;

                // The round drives a focused crack into the target …
                FractureService.BeginFracture(_world, target, -1, pcp,
                    pt.Direction, pVel, adjMass, pProfile, _rng);

                // … and takes the reciprocal impact itself, so the round cracks/sheds on hit
                // instead of staying inert. Use the WEAPON's own mass (not the target's huge
                // mass) as the impactor — otherwise the reduced-mass energy vaporises the round.
                Vector2 tVel = _world.HasComponent<Velocity>(target)
                    ? _world.GetComponent<Velocity>(target).Linear : Vector2.Zero;
                FractureService.BeginFracture(_world, piercing, -1, pcp,
                    -pt.Direction, tVel, pMass, pProfile, _rng);

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
        if (approach < _ctx.Config.Fracture.AsteroidCollisionThreshold) return;

        _activeCollisions.Add(pair);

        float mA = _world.GetComponent<RigidBody>(eA).Mass;
        float mB = _world.GetComponent<RigidBody>(eB).Mass;
        var frac = _ctx.Config.Fracture;
        _ctx.Config.Weapons.TryGetValue(_ctx.Config.Player.StartingWeapon, out var wc);
        var weapon = new WeaponProfile
        {
            Directionality = wc?.Directionality ?? 0.40f,
            BlastFraction  = frac.AsteroidBlastFraction,
            Knockback      = 0f,                              // bodies already exchange momentum via collision
        };

        _effects.EmitFlash(cp, 0.5f * (mA * mB / (mA + mB)) * approach * approach);

        Vector2 vRelDir    = vRel.LengthSquared() > 1f ? Vector2.Normalize(vRel) : ev.Contact.Normal;
        Vector2 blended    = Vector2.Lerp(ev.Contact.Normal, vRelDir, frac.AsteroidDirSpin);
        Vector2 impactDirAB = blended.LengthSquared() > 1e-6f ? Vector2.Normalize(blended) : ev.Contact.Normal;

        // Real masses — the global EnergyScale converts physical KE to fracture units, so collisions
        // and bullets are on one consistent scale (no per-interaction fudge). Gentle bumps fall below
        // bond strength naturally; AsteroidCollisionThreshold trims the very-low-speed end.
        float impCoeff = _ctx.Config.Player.PlayerImpactCoeff;
        float massBForA = eA == Player ? mB * impCoeff : mB;
        float massAForB = eB == Player ? mA * impCoeff : mA;

        bool dashInv = IsDashInvincible();
        if (!(eA == Player && dashInv))
            FractureService.BeginFracture(_world, eA, -1, cp, impactDirAB,
                vRel + vA.Linear, massBForA, weapon, _rng);
        if (!(eB == Player && dashInv))
            FractureService.BeginFracture(_world, eB, -1, cp, -impactDirAB,
                -vRel + vB.Linear, massAForB, weapon, _rng);
    }

    private void OnCellPulverized(CellPulverizedEvent ev)
    {
        _ctx.CellBudget.Remove(1);

        // Score: area × material density (denser materials score more).
        if (ev.Body != Player && _world.IsAlive(ev.Body) && _world.HasComponent<FracturableBody>(ev.Body))
        {
            float density = _world.GetComponent<FracturableBody>(ev.Body).Material.Density;
            _ctx.Score.Add(ev.Area * density);
        }

        // Check if a cockpit cell was pulverized on the player entity.
        if (ev.Body == Player && _world.IsAlive(Player) && _world.HasComponent<FracturableBody>(Player))
        {
            ref var fb = ref _world.GetComponent<FracturableBody>(Player);
            ref var t  = ref _world.GetComponent<Transform>(Player);
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
            if (role == "cockpit") PendingGameOver = true;
        }

        BodyColor color = GetBodyColor(ev.Body);
        _effects.EmitDustBurst(ev.WorldCentroid,
            ev.WorldCentroid - (_world.IsAlive(ev.Body)
                ? _world.GetComponent<Transform>(ev.Body).Position : ev.WorldCentroid),
            _world.IsAlive(ev.Body) && _world.HasComponent<Velocity>(ev.Body)
                ? _world.GetComponent<Velocity>(ev.Body).Linear : Vector2.Zero,
            ev.Area, color);
        _effects.SpawnDebris(ev, color);
    }

    private void OnFractureCompleted(FractureCompletedEvent ev)
    {
        _activeCollisions.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        bool isPlayer     = ev.Body == Player;
        bool isMothership = _world.HasComponent<MothershpId>(ev.Body);
        bool isAlien      = !isMothership && _world.HasComponent<AlienTag>(ev.Body);
        MothershpId origMid = isMothership ? _world.GetComponent<MothershpId>(ev.Body) : default;
        AlienVariant origAv = isAlien && _world.HasComponent<AlienVariant>(ev.Body)
            ? _world.GetComponent<AlienVariant>(ev.Body) : default;
        BodyColor color = GetBodyColor(ev.Body);
        var (savedAim, savedWc, savedWep, savedSk) = isPlayer ? SavePlayerState(ev.Body) : default;
        var fg = MakeFractureGroup(ev.Body);
        var parentVr = _world.HasComponent<VortexResponse>(ev.Body)
            ? _world.GetComponent<VortexResponse>(ev.Body)
            : new VortexResponse { CentripetalMult = 1f, TangentialMult = 1f };
        bool cockpitFound = false;
        // Ephemeral bodies (the piercing round, or any TTL'd fragment of one) pass the TTL on
        // to their own shards so later crack generations also fade instead of littering the map.
        bool ephemeral = _world.HasComponent<PiercingRoundTag>(ev.Body) || _world.HasComponent<TimeToLive>(ev.Body);

        foreach (var f in ev.Fragments)
        {
            if (f.IsDebris) { _effects.EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, parentVr);
            _world.AddComponent(ne, fg);
            if (ephemeral) _world.AddComponent(ne, new TimeToLive { Remaining = PiercingFragmentTtl });
            bool fragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
            if (isMothership)
            {
                // Only a cockpit fragment stays a live (boss) alien; the rest are inert debris.
                if (fragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    _world.AddComponent(ne, new SpawnerAccumulator { Value = 0f });
                    TagAlienFragment(ne, new AlienVariant { Key = "mothership" }, 999f);
                }
            }
            else if (isAlien)
            {
                // Only a fragment that still carries a cockpit cell remains a steerable,
                // shooting alien; cockpit-less pieces become inert asteroid-layer debris.
                if (fragCockpit) TagAlienFragment(ne, origAv, 0f);
            }
            else
            {
                CopyTags(ev.Body, ne);   // asteroids
            }
            if (isPlayer && !cockpitFound && fragCockpit)
            {
                cockpitFound = true;
                TransferPlayerToFragment(ne, f, savedAim, savedWc, savedWep, savedSk);
            }
        }
        _world.DestroyEntity(ev.Body);
        if (isPlayer && !cockpitFound) PendingGameOver = true;
    }

    private void OnFractureSplit(FractureSplitEvent ev)
    {
        _activeCollisions.RemoveWhere(p => p.Item1 == ev.Body.Id || p.Item2 == ev.Body.Id);
        bool isPlayer     = ev.Body == Player;
        bool isMothership = _world.HasComponent<MothershpId>(ev.Body);
        bool isAlien      = !isMothership && _world.HasComponent<AlienTag>(ev.Body);
        MothershpId origMid = isMothership ? _world.GetComponent<MothershpId>(ev.Body) : default;
        AlienVariant origAv = isAlien && _world.HasComponent<AlienVariant>(ev.Body)
            ? _world.GetComponent<AlienVariant>(ev.Body) : default;
        BodyColor color = GetBodyColor(ev.Body);
        var (savedAim, savedWc, savedWep, savedSk) = isPlayer ? SavePlayerState(ev.Body) : default;
        var fg = MakeFractureGroup(ev.Body);
        var parentVr = _world.HasComponent<VortexResponse>(ev.Body)
            ? _world.GetComponent<VortexResponse>(ev.Body)
            : new VortexResponse { CentripetalMult = 1f, TangentialMult = 1f };
        bool cockpitFound = false;
        bool ephemeral = _world.HasComponent<PiercingRoundTag>(ev.Body) || _world.HasComponent<TimeToLive>(ev.Body);

        foreach (var p in ev.Pieces)
        {
            var f = p.Spec;
            if (f.IsDebris) { _effects.EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, parentVr);
            _world.AddComponent(ne, fg);
            if (ephemeral) _world.AddComponent(ne, new TimeToLive { Remaining = PiercingFragmentTtl });
            if (p.Process.HasValue) _world.AddComponent(ne, p.Process.Value);
            bool fragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
            if (isMothership)
            {
                if (fragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    _world.AddComponent(ne, new SpawnerAccumulator { Value = 0f });
                    TagAlienFragment(ne, new AlienVariant { Key = "mothership" }, 999f);
                }
            }
            else if (isAlien)
            {
                if (fragCockpit) TagAlienFragment(ne, origAv, 0f);
            }
            else
            {
                CopyTags(ev.Body, ne);   // asteroids
            }
            if (isPlayer && !cockpitFound && fragCockpit)
            {
                cockpitFound = true;
                TransferPlayerToFragment(ne, f, savedAim, savedWc, savedWep, savedSk);
            }
        }
        _world.DestroyEntity(ev.Body);
        if (isPlayer && !cockpitFound) PendingGameOver = true;
    }
}
