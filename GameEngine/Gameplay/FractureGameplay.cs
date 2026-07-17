using System.Numerics;
using AsteroidsEngine.Engine.Collision;
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
        if (av.Key != null && _ctx.Config.Entities.TryGetValue(av.Key, out var ecfg) && ecfg.Dash is not null)
            _world.AddComponent(ne, new AlienSkillState { DashCd = ecfg.Dash.Cooldown });
        if (_world.HasComponent<Collider>(ne))
        {
            ref var col = ref _world.GetComponent<Collider>(ne);
            col.Layer = GameLayers.Alien;
            col.Mask  = GameLayers.Asteroid | GameLayers.Player | GameLayers.Alien;
        }
    }

    // A shard of a shattered piercing round inherits the round's behaviour: it stays a sensor (passes
    // through, no collision/jitter) and keeps the PiercingRoundTag so ProjectileSystem still drives it
    // and the body-vs-body fracture path ignores it. GhostSystem promotes it to the bullet layer.
    private void TagPiercingFragment(Entity ne, in PiercingRoundTag parent)
    {
        if (_world.HasComponent<Collider>(ne))
        {
            ref var col = ref _world.GetComponent<Collider>(ne);
            col.Sensor = true;
        }
        // A shard's penetration budget is recomputed from ITS OWN kinetic energy at the parent's
        // KE→power exchange rate — small slow shards are nearly spent, a big fast one stays mean.
        float m = _world.HasComponent<RigidBody>(ne) ? _world.GetComponent<RigidBody>(ne).Mass : 1f;
        float v = _world.HasComponent<Velocity>(ne)  ? _world.GetComponent<Velocity>(ne).Linear.Length() : 0f;
        float power = parent.PowerPerKE * 0.5f * m * v * v;
        _world.AddComponent(ne, new PiercingRoundTag
        {
            Direction    = parent.Direction,
            LateralClamp = parent.LateralClamp,
            PlayerGrace  = 0f,
            Power        = power,
            Power0       = MathF.Max(power, 1e-3f),
            PowerPerKE   = parent.PowerPerKE,
            LastTarget   = default,   // a shard pays for its own cells from scratch
            LastCell     = -1,
        });
    }

    /// <summary>True when the player ship can neither move nor fight: no propeller and no weapon
    /// cell left alive — only the cockpit and generic hull. A drifting wreck is a lost run even
    /// though the cockpit still lives.</summary>
    private bool PlayerIsDerelict()
    {
        if (!_world.IsAlive(Player) || !_world.HasComponent<FracturableBody>(Player)) return false;
        ref var fb = ref _world.GetComponent<FracturableBody>(Player);
        bool[]? pulv = _world.HasComponent<FractureProcess>(Player)
            ? _world.GetComponent<FractureProcess>(Player).Pulverized : null;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (pulv != null && pulv[i]) continue;
            switch (fb.Cells[i].Role)
            {
                case "propeller" or "cannon" or "shotgun" or "piercing" or "grenade":
                    return false;
            }
        }
        return true;
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
        // Real impactor mass: per-weapon Mass, else the global BulletMass. EnergyScale converts to
        // fracture units; per-weapon damage also differs through projectile SPEED (E ∝ v²) and count.
        // Shrapnel carry the "grenade" key (the grenade projectile itself detonates at line 162 and
        // never reaches here), so they take the grenade's dedicated ShrapnelMass.
        float? massOverride = hasData ? _world.GetComponent<BulletData>(ev.Bullet).MassOverride : null;
        float bulletMass = massOverride
                        ?? (weaponKey == "grenade" ? weaponCfg.ShrapnelMass : weaponCfg.Mass)
                        ?? frac.BulletMass;

        float impactE = 0.5f * bulletMass * bulletVel.LengthSquared();
        _effects.EmitFlash(ev.Point, impactE);
        _effects.EmitSparks(ev.Point, ev.ShotDir, impactE);

        // Scale fracture energy by target's impact coefficient.
        float adjMass = bulletMass;
        if (ev.Target == Player)
            adjMass = bulletMass * _ctx.PlayerImpactCoeff;
        else if (_world.HasComponent<AlienVariant>(ev.Target))
        {
            string varKey = _world.GetComponent<AlienVariant>(ev.Target).Key;
            float coeff = _ctx.Config.Entities.TryGetValue(varKey, out var ecfg)
                ? ecfg.AlienImpactCoeff : 1f;
            adjMass = bulletMass * coeff;
        }

        FractureService.BeginFracture(
            _world, ev.Target, ev.StruckCell,
            ev.Point, ev.ShotDir, bulletVel.Length(), adjMass,
            profile, _rng);
    }

    private void OnCollision(CollisionEvent ev)
    {
        Entity eA = ev.EntityA, eB = ev.EntityB;
        if (!_world.IsAlive(eA) || !_world.IsAlive(eB)) return;

        // Grenades (GrenadeSystem) and piercing rounds (ProjectileSystem) own their own contacts.
        if (_world.HasComponent<PiercingRoundTag>(eA) || _world.HasComponent<PiercingRoundTag>(eB)) return;

        bool aIsBody = _world.HasComponent<FracturableBody>(eA);
        bool bIsBody = _world.HasComponent<FracturableBody>(eB);
        if (!aIsBody || !bIsBody) return;

        var pair = eA.Id < eB.Id ? (eA.Id, eB.Id) : (eB.Id, eA.Id);
        if (_activeCollisions.Contains(pair)) return;

        // True PRE-solve closing speed (incl. spin·lever) captured by CollisionSystem before the
        // impulse — not the post-bounce velocity that produced the tiny-energy ricochet bug.
        float approach = ev.ApproachSpeed;
        if (approach < _ctx.Config.Fracture.AsteroidCollisionThreshold) return;

        _activeCollisions.Add(pair);

        float mA = _world.GetComponent<RigidBody>(eA).Mass;
        float mB = _world.GetComponent<RigidBody>(eB).Mass;
        var frac = _ctx.Config.Fracture;
        var weapon = new WeaponProfile
        {
            Directionality = frac.AsteroidDirectionality,
            BlastFraction  = frac.AsteroidBlastFraction,
            Knockback      = 0f,                              // bodies already exchange momentum via collision
        };

        Vector2 cp = ev.Contact.ContactPoint;
        Vector2 n  = ev.Contact.Normal;   // points B→A (into A)

        // Crack direction blends the contact normal with the relative-velocity direction (incl. spin),
        // by AsteroidDirSpin: 0 = pure contact normal, 1 = pure relative velocity. "Into A" is the
        // direction B moves relative to A at the contact; B fractures along the mirror (−dir).
        Vector2 dir = n;
        float dirSpin = Math.Clamp(frac.AsteroidDirSpin, 0f, 1f);
        if (dirSpin > 0f)
        {
            Vector2 relIntoA = VelAtPoint(eB, cp) - VelAtPoint(eA, cp);
            if (relIntoA.LengthSquared() > 1e-4f)
            {
                Vector2 blended = n * (1f - dirSpin) + Vector2.Normalize(relIntoA) * dirSpin;
                if (blended.LengthSquared() > 1e-6f) dir = Vector2.Normalize(blended);
            }
        }

        _effects.EmitFlash(cp, 0.5f * (mA * mB / (mA + mB)) * approach * approach);

        float impCoeff = _ctx.PlayerImpactCoeff;
        float massBForA = eA == Player ? mB * impCoeff : mB;
        float massAForB = eB == Player ? mA * impCoeff : mA;

        // Seed each body at the cell that actually touched (carried on the contact by the narrow
        // phase). Without this the seed fell back to nearest-centroid and cracks started INSIDE
        // the body while the struck face stayed intact.
        bool dashInv = IsDashInvincible();
        if (!(eA == Player && dashInv))
            FractureService.BeginFracture(_world, eA, ev.Contact.PartA, cp, dir, approach, massBForA, weapon, _rng);
        if (!(eB == Player && dashInv))
            FractureService.BeginFracture(_world, eB, ev.Contact.PartB, cp, -dir, approach, massAForB, weapon, _rng);
    }

    // Nearest cell (by centroid) to a world point, in the body's local frame. -1 if no cells.
    private static int NearestCellIndex(in FracturableBody fb, in Transform t, Vector2 worldPoint)
    {
        float cos = MathF.Cos(-t.Rotation), sin = MathF.Sin(-t.Rotation);
        Vector2 d = worldPoint - t.Position;
        Vector2 local = new(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            float dsq = (fb.Cells[i].Centroid - local).LengthSquared();
            if (dsq < bestSq) { bestSq = dsq; best = i; }
        }
        return best;
    }

    // World-space velocity of a body at a point, including spin (ω × r). Missing Velocity = stationary.
    private Vector2 VelAtPoint(Entity e, Vector2 worldPoint)
    {
        if (!_world.HasComponent<Velocity>(e)) return Vector2.Zero;
        ref var v = ref _world.GetComponent<Velocity>(e);
        Vector2 r = worldPoint - _world.GetComponent<Transform>(e).Position;
        return v.Linear + new Vector2(-v.Angular * r.Y, v.Angular * r.X);
    }

    private void OnCellPulverized(CellPulverizedEvent ev)
    {
        _ctx.CellBudget.Remove(1);

        // Score: area × material toughness × weight, scaled by the live kill-chain multiplier —
        // sustained destruction ramps the combo; a lull lets it decay (Score.Update).
        if (ev.Body != Player && _world.IsAlive(ev.Body) && _world.HasComponent<FracturableBody>(ev.Body))
        {
            var sc = _ctx.Config.Scoring;
            float toughness = _world.GetComponent<FracturableBody>(ev.Body).Material.Toughness;
            _ctx.Score.AddKill(ev.Area * toughness * sc.CellScoreAreaWeight,
                               sc.KillChainSteps, sc.KillChainDecay);
        }

        // Map the pulverized cell and disable its collider part, so projectiles and bodies pass
        // through the crater instead of being stopped/consumed by the now-empty cell. Part index ==
        // cell index (see VoronoiTessellator.BuildShape), and pulverized cells stay in the body.
        if (_world.IsAlive(ev.Body) && _world.HasComponent<FracturableBody>(ev.Body)
            && _world.HasComponent<Transform>(ev.Body))
        {
            ref var fb = ref _world.GetComponent<FracturableBody>(ev.Body);
            ref var t  = ref _world.GetComponent<Transform>(ev.Body);
            int cell = NearestCellIndex(fb, t, ev.WorldCentroid);
            if (cell >= 0)
            {
                if (_world.HasComponent<Collider>(ev.Body)
                    && _world.GetComponent<Collider>(ev.Body).Shape is CompoundShape cs)
                    cs.DisablePart(cell);

                // Player loses if its cockpit cell was the one pulverized…
                if (ev.Body == Player && fb.Cells[cell].Role == "cockpit") PendingGameOver = true;
            }
        }

        // …or if the ship is now a derelict: cockpit + generic hull only, no way to move or fight.
        if (ev.Body == Player && !PendingGameOver && PlayerIsDerelict()) PendingGameOver = true;

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
        bool parentInbound = _world.HasComponent<InboundSpawn>(ev.Body);   // still outside the field
        bool cockpitFound = false;
        // Ephemeral bodies (the piercing round, or any TTL'd fragment of one) pass the TTL on
        // to their own shards so later crack generations also fade instead of littering the map.
        bool ephemeral = _world.HasComponent<PiercingRoundTag>(ev.Body) || _world.HasComponent<TimeToLive>(ev.Body);
        bool isPiercing = _world.HasComponent<PiercingRoundTag>(ev.Body);
        PiercingRoundTag parentPierce = isPiercing ? _world.GetComponent<PiercingRoundTag>(ev.Body) : default;

        foreach (var f in ev.Fragments)
        {
            if (f.IsDebris) { _effects.EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, parentVr);
            _world.AddComponent(ne, fg);
            if (parentInbound) _world.AddComponent(ne, new InboundSpawn());   // keep the entry exemption
            if (ephemeral) _world.AddComponent(ne, new TimeToLive { Remaining = PiercingFragmentTtl });
            bool fragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
            if (isMothership)
            {
                // Only a cockpit fragment stays a live (boss) alien; the rest are inert debris.
                if (fragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    TagAlienFragment(ne, new AlienVariant { Key = "mothership" }, 999f);
                    MothershipPrefab.AttachBossBrain(_world, ne, _ctx);   // its own independent boss brain
                }
            }
            else if (isAlien)
            {
                // Only a fragment that still carries a cockpit cell remains a steerable,
                // shooting alien; cockpit-less pieces become inert asteroid-layer debris.
                if (fragCockpit) TagAlienFragment(ne, origAv, 0f);
            }
            else if (isPiercing)
            {
                TagPiercingFragment(ne, parentPierce);
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
        // Lost run: no cockpit fragment survived, or the surviving cockpit fragment is a derelict
        // (cockpit + generic hull only — it can neither move nor fight).
        if (isPlayer && (!cockpitFound || PlayerIsDerelict())) PendingGameOver = true;
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
        bool parentInbound = _world.HasComponent<InboundSpawn>(ev.Body);   // still outside the field
        bool cockpitFound = false;
        bool ephemeral = _world.HasComponent<PiercingRoundTag>(ev.Body) || _world.HasComponent<TimeToLive>(ev.Body);
        bool isPiercing = _world.HasComponent<PiercingRoundTag>(ev.Body);
        PiercingRoundTag parentPierce = isPiercing ? _world.GetComponent<PiercingRoundTag>(ev.Body) : default;

        foreach (var p in ev.Pieces)
        {
            var f = p.Spec;
            if (f.IsDebris) { _effects.EmitDustBurst(f.WorldCentroid, f.Linear, Vector2.Zero, f.Area, color); continue; }
            var ne = SpawnBody(f.Body, f.WorldCentroid, f.Rotation, f.Linear, f.Angular, color, ghost: true);
            _world.AddComponent(ne, parentVr);
            _world.AddComponent(ne, fg);
            if (parentInbound) _world.AddComponent(ne, new InboundSpawn());   // keep the entry exemption
            if (ephemeral) _world.AddComponent(ne, new TimeToLive { Remaining = PiercingFragmentTtl });
            if (p.Process.HasValue) _world.AddComponent(ne, p.Process.Value);
            bool fragCockpit = f.Body.Cells.Any(c => c.Role == "cockpit");
            if (isMothership)
            {
                if (fragCockpit)
                {
                    _world.AddComponent(ne, origMid);
                    TagAlienFragment(ne, new AlienVariant { Key = "mothership" }, 999f);
                    MothershipPrefab.AttachBossBrain(_world, ne, _ctx);   // its own independent boss brain
                }
            }
            else if (isAlien)
            {
                if (fragCockpit) TagAlienFragment(ne, origAv, 0f);
            }
            else if (isPiercing)
            {
                TagPiercingFragment(ne, parentPierce);
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
        // Lost run: no cockpit fragment survived, or the surviving cockpit fragment is a derelict
        // (cockpit + generic hull only — it can neither move nor fight).
        if (isPlayer && (!cockpitFound || PlayerIsDerelict())) PendingGameOver = true;
    }
}
