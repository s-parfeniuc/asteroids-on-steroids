// Asteroid Demo — polygon physics destruction
//
// Controls:
//   WASD     — thrust
//   F        — shoot
//   Escape   — quit
//
// Build & run:
//   cd GameEngine/Demos/AsteroidDemo && dotnet run

using System.Diagnostics;
using System.Numerics;
using AsteroidDemo;
using AsteroidsEngine.Platform.Sdl;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Events;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Engine.Systems;

var cfg = GameConfig.Load();
int W = cfg.Window.Width;
int H = cfg.Window.Height;

using var window = new SdlGameWindow(cfg.Window.Title, W, H);
var input = new InputSystem();
window.KeyDown += k => input.OnKeyDown(k);
window.KeyUp += k => input.OnKeyUp(k);

var session = new DemoSession(W, H, input, cfg);

const double FixedDt = 1.0 / 120.0;
var fixedStep = new FixedTimestep(FixedDt);

var sw = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;

while (!window.ShouldClose)
{
    window.PollEvents();

    long now = sw.ElapsedTicks;
    double frameTime = (double)(now - lastTicks) / Stopwatch.Frequency;
    lastTicks = now;

    input.BeginFrame();
    if (input.IsPressed(KeyCode.Escape)) break;

    // Fixed-step simulation; the render interpolates between sim states via Alpha.
    int steps = fixedStep.Advance(frameTime);
    for (int i = 0; i < steps; i++)
        session.Update(FixedDt);

    session.Draw(window.Renderer, fixedStep.Alpha);
    window.Present();

    // Cap render rate (~60 FPS); the simulation rate is fixed and independent.
    double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
    int sleep = (int)((1.0 / 60.0 - elapsed) * 1000);
    if (sleep > 1) Thread.Sleep(sleep);
}


// =============================================================================
// DemoSession
// =============================================================================

class DemoSession
{
    private readonly World       _world;
    private readonly EventBus    _bus;
    private readonly ISystem[]   _systems;
    private readonly DemoRenderer _renderer;
    private readonly Random      _rng = new(42);
    private readonly int         _w, _h;
    private readonly GameConfig  _cfg;

    private int   _wave      = 1;
    private int   _score     = 0;
    private float _nextWaveCD = -1f;

    public DemoSession(int w, int h, InputSystem input, GameConfig cfg)
    {
        _w = w; _h = h; _cfg = cfg;
        _world    = new World();
        _bus      = new EventBus();
        _renderer = new DemoRenderer(w, h, cfg.Player.Radius);

        Entity player = SpawnPlayer(_world, new Vector2(w / 2f, h / 2f), cfg.Player);
        SpawnWave(cfg.Waves.InitialCount);

        _systems =
        [
            new PreviousStateSystem(),
            new PlayerInputSystem(input, player, cfg.Player.Thrust),
            new PhysicsSystem(),
            new MovementSystem(),
            new WrapSystem(w, h),
            new ShootSystem(input, player, cfg.Bullet),
            new CollisionSystem(new SpatialGrid(128f), _bus) { ResolveOverlap = true },
            new EventFlushSystem(_bus),
            new TimeToLiveSystem(),
        ];

        _bus.Subscribe<CollisionEvent>(HandleCollision);
    }

    // -------------------------------------------------------------------------

    public void Update(double dt)
    {
        foreach (var sys in _systems) sys.Update(_world, dt);
        _world.FlushDeferred();

        int remaining = _world.Count<AsteroidData>();
        if (remaining == 0)
        {
            if (_nextWaveCD < 0f) _nextWaveCD = _cfg.Waves.NextWaveDelaySecs;
            else
            {
                _nextWaveCD -= (float)dt;
                if (_nextWaveCD <= 0f)
                {
                    _wave++;
                    SpawnWave(_cfg.Waves.InitialCount + (_wave - 1) * _cfg.Waves.CountPerWave);
                    _nextWaveCD = -1f;
                }
            }
        }
        else
        {
            _nextWaveCD = -1f;
        }
    }

    public void Draw(IRenderer r, float alpha)
        => _renderer.Draw(_world, r, _wave, _score, _nextWaveCD, alpha);

    // -------------------------------------------------------------------------
    // Collision event handler
    // -------------------------------------------------------------------------

    private void HandleCollision(CollisionEvent ev)
    {
        if (!_world.IsAlive(ev.EntityA) || !_world.IsAlive(ev.EntityB)) return;

        bool aIsBullet = _world.HasComponent<BulletTag>(ev.EntityA);
        bool bIsBullet = _world.HasComponent<BulletTag>(ev.EntityB);
        bool aIsAsteroid = _world.HasComponent<AsteroidData>(ev.EntityA);
        bool bIsAsteroid = _world.HasComponent<AsteroidData>(ev.EntityB);

        if ((aIsBullet && bIsAsteroid) || (bIsBullet && aIsAsteroid))
        {
            Entity bullet = aIsBullet ? ev.EntityA : ev.EntityB;
            Entity asteroid = aIsAsteroid ? ev.EntityA : ev.EntityB;
            FragmentAsteroid(asteroid, bullet, ev.Contact.ContactPoint);
            return;
        }

        // Player vs asteroid: elastic impulse (no health damage in this demo)
        bool aIsPlayer = _world.HasComponent<PlayerTag>(ev.EntityA);
        // Player↔asteroid impulse is now handled engine-side in CollisionSystem.TryApplyImpulse
        // using RigidBody.Restitution from each entity. No game-layer response needed here.
    }

    private float MassRef => _cfg.Fracture.MassReference;

    private void FragmentAsteroid(Entity asteroid, Entity bullet, Vector2 impactPoint)
    {
        if (!_world.IsAlive(asteroid) || !_world.IsAlive(bullet)) return;
        if (!_world.HasComponent<FractureProperties>(asteroid)) return;

        var at  = _world.GetComponent<Transform>(asteroid);
        var av  = _world.GetComponent<Velocity>(asteroid);
        var rb  = _world.GetComponent<RigidBody>(asteroid);
        var fp  = _world.GetComponent<FractureProperties>(asteroid);
        var col = _world.GetComponent<Collider>(asteroid);
        if (col.Shape is not PolygonShape poly) return;

        Vector2[] worldVerts = poly.GetWorldVertices(at.Position, at.Rotation);

        // Correct the impact point: the SAT contact point can be inside the polygon
        // when the bullet moves faster than one asteroid-radius per frame (tunnelling).
        // The nearest point on the boundary is always on the surface.
        var bulletAt = _world.GetComponent<Transform>(bullet);
        impactPoint  = PolygonUtils.NearestPointOnBoundary(worldVerts, bulletAt.Position);

        // ── Energy model ─────────────────────────────────────────────────────
        var     bulletVel    = _world.GetComponent<Velocity>(bullet);
        float   BulletMass   = _cfg.Bullet.Mass;          // single source of truth
        float   bulletSpeedMag = bulletVel.Linear.Length();
        Vector2 bulletDir    = bulletSpeedMag > 1f ? bulletVel.Linear / bulletSpeedMag : Vector2.UnitX;
        Vector2 impactNormal = -bulletDir;
        float   vRelN    = MathF.Abs(Vector2.Dot(bulletVel.Linear - av.Linear, impactNormal));
        float   mReduced = BulletMass * rb.Mass / (BulletMass + rb.Mass);
        float   eEff     = 0.5f * mReduced * vRelN * vRelN;
        float   eSpin    = _cfg.Fracture.SpinEnergyFraction * 0.5f * rb.Inertia * av.Angular * av.Angular;
        float   eTotal   = eEff + eSpin;

        // ── Threshold + accumulation ──────────────────────────────────────────
        float threshold = fp.Toughness * rb.Mass;
        ref var fs = ref _world.GetComponent<FractureState>(asteroid);
        float combined = eTotal + fs.AbsorbedEnergy;

        _world.DestroyEntity(bullet);   // bullet always consumed on impact

        if (combined < threshold)
        {
            fs.AbsorbedEnergy += eTotal;
            SpawnDebrisCloud(impactPoint, bulletDir, _cfg.Fracture.DebrisCloud.Count);
            return;
        }
        fs.AbsorbedEnergy = 0f;

        // ── Physics-based zone parameters ────────────────────────────────────
        var fc = _cfg.Fracture;

        float area      = MathF.Abs(PolygonUtils.ComputeArea(worldVerts));
        float rAsteroid = MathF.Sqrt(area / MathF.PI);
        float density   = _cfg.Asteroid.Density;

        // Zone radius: energy that can fracture / energy per unit area
        float eFracture       = combined - threshold;
        float zoneAreaRaw     = eFracture / (fp.Toughness * density);
        float zoneRadiusRaw   = MathF.Sqrt(MathF.Max(0f, zoneAreaRaw) / MathF.PI);
        float zoneRadius      = Math.Clamp(zoneRadiusRaw,
                                           fc.MinFractureZoneFraction * rAsteroid,
                                           rAsteroid);

        // energyFactor ∈ [0,1]: how intense is this hit relative to the asteroid's size
        float energyFactor = zoneRadius / rAsteroid;

        // Blast radius: fraction of zone, brittleness-dependent
        float blastRadius = Math.Clamp(
            zoneRadius * float.Lerp(fc.BlastFractionDuctile, fc.BlastFractionBrittle, fp.Brittleness),
            fc.BlastMin, MathF.Min(fc.BlastMax, zoneRadius * 0.9f));

        // Cut counts driven by energyFactor and brittleness
        int secondaryCuts = Math.Clamp(
            (int)MathF.Round(float.Lerp(fc.SecondaryCutsMin, fc.SecondaryCutsMax, fp.Brittleness) * energyFactor),
            0, fc.MaxSecondaryCuts);
        int innerCuts = Math.Clamp(
            (int)MathF.Round(float.Lerp(fc.InnerCutsMin, fc.InnerCutsMax, fp.Brittleness) * energyFactor),
            1, fc.MaxInnerCuts);

        float spreadAngle   = MathF.PI * float.Lerp(fc.SpreadAngleMin, fc.SpreadAngleMax, fp.Brittleness);
        float coneHalfAngle = fc.ConeHalfAngleDeg * MathF.PI / 180f;

        Vector2 impactOffset = impactPoint - at.Position;
        Vector2 spinVel      = new(-av.Angular * impactOffset.Y, av.Angular * impactOffset.X);
        float   spinFault    = spinVel.LengthSquared() > 1f
                               ? MathF.Atan2(spinVel.Y, spinVel.X) + MathF.PI / 2f
                               : float.NaN;

        // ── Split ─────────────────────────────────────────────────────────────
        SplitResult result = PolygonUtils.Split(
            worldVerts, impactPoint, bulletDir,
            fs.FaultAngles,
            secondaryCuts, innerCuts,
            zoneRadius, zoneRadius, coneHalfAngle,   // fractureZoneDepth = fractureRadius = zoneRadius
            blastRadius, spreadAngle, spinFault, _rng,
            minAreaThreshold: fp.MinFragmentArea);

        // Bullet impulse J = momentum transferred at the impact point.
        Vector2 bulletJ = bulletDir * (bulletSpeedMag * BulletMass * fc.MomentumTransfer);

        // ── Update surviving asteroid in-place ────────────────────────────────
        bool asteroidSurvives = UpdateSurvivingAsteroid(
            asteroid, result.PrimaryFarPiece, result.SecondaryFarPieces,
            area, at, av, rb, fp);
        if (asteroidSurvives)
        {
            // Off-centre hit imparts spin + linear push to the surviving body.
            ref var sv  = ref _world.GetComponent<Velocity>(asteroid);
            ref var srb = ref _world.GetComponent<RigidBody>(asteroid);
            ref var st  = ref _world.GetComponent<Transform>(asteroid);
            Vector2 rSurv = impactPoint - st.Position;
            sv.Linear += bulletJ / srb.Mass;
            if (srb.Inertia > 0f)
                sv.Angular += (rSurv.X * bulletJ.Y - rSurv.Y * bulletJ.X) / srb.Inertia;
        }
        else
        {
            _world.DestroyEntity(asteroid);
        }
        _score++;

        // Impact flash
        float ImpactTTL = fc.ImpactFlashTtl;
        Entity fxE = _world.CreateEntity();
        _world.AddComponent(fxE, new Transform { Position = impactPoint });
        _world.AddComponent(fxE, new ImpactEffect { BlastRadius = blastRadius, MaxTTL = ImpactTTL });
        _world.AddComponent(fxE, new TimeToLive { Remaining = ImpactTTL });

        // Blast particles at impact point
        SpawnDebrisCloud(impactPoint, bulletDir, _cfg.Fracture.DebrisCloud.Count, isBlast: true);

        // Momentum kick applied to near-zone fragments
        Vector2 kickedLinear = av.Linear + bulletDir * (bulletSpeedMag * BulletMass / rb.Mass * fc.MomentumTransfer);

        foreach (var frag in result.SurvivingFragments)
            SpawnLiveFragment(frag, at, av, rb, fp, fs, kickedLinear, energyFactor, area, isfarPiece: false);

        foreach (var frag in result.DebrisFragments)
            SpawnDebrisFrag(frag, at, av, impactPoint, bulletDir, energyFactor, rb.Mass, isBlast: false);
    }

    /// <summary>
    /// Collects all surviving pieces (primary + secondary far pieces), filters by
    /// MinFragmentArea, then updates the asteroid entity in-place:
    ///   1 piece  → PolygonShape
    ///   2+ pieces → CompoundShape
    ///   0 pieces  → returns false (caller destroys the entity)
    ///
    /// Local vertices are expressed in the entity's local coordinate space
    /// (centroid-relative and un-rotated) so PolygonShape.TransformVertices gives
    /// the correct world position at the entity's current rotation.
    /// </summary>
    private bool UpdateSurvivingAsteroid(
        Entity asteroid,
        Vector2[]? primaryFar, Vector2[][] secondaryFarPieces,
        float originalArea,
        Transform at, Velocity av, RigidBody rb, FractureProperties fp)
    {
        // Collect all large enough pieces (world-space vertex arrays)
        var pieces = new List<Vector2[]>(1 + secondaryFarPieces.Length);
        if (primaryFar is { Length: >= 3 } && MathF.Abs(PolygonUtils.ComputeArea(primaryFar)) >= fp.MinFragmentArea)
            pieces.Add(primaryFar);
        foreach (var p in secondaryFarPieces)
            if (p.Length >= 3 && MathF.Abs(PolygonUtils.ComputeArea(p)) >= fp.MinFragmentArea)
                pieces.Add(p);

        if (pieces.Count == 0) return false;

        // ── Compute compound centre of mass ──────────────────────────────────
        float totalArea = 0f;
        Vector2 compoundCentroid = Vector2.Zero;
        foreach (var p in pieces)
        {
            float pArea = MathF.Abs(PolygonUtils.ComputeArea(p));
            compoundCentroid += PolygonUtils.ComputeCentroid(p) * pArea;
            totalArea        += pArea;
        }
        compoundCentroid /= totalArea;

        float massFrac = originalArea > 0f ? totalArea / originalArea : 1f;
        float newMass  = rb.Mass * massFrac;

        // ── Build local verts (centroid-relative, un-rotated by entity's rotation) ──
        // Required so PolygonShape.TransformVertices(pos, rot) gives the correct
        // world polygon without double-rotation.
        float cosR = MathF.Cos(-at.Rotation), sinR = MathF.Sin(-at.Rotation);
        CollisionShape newShape;
        float newInertia;

        if (pieces.Count == 1)
        {
            var localVerts = BuildLocalVerts(pieces[0], compoundCentroid, cosR, sinR);
            newInertia = PolygonUtils.ComputeInertia(localVerts, newMass);
            newShape   = new PolygonShape(localVerts);
        }
        else
        {
            // Compound — parallel axis theorem for combined inertia
            newInertia = 0f;
            var parts  = new CollisionShape[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                float   pArea      = MathF.Abs(PolygonUtils.ComputeArea(pieces[i]));
                float   pMass      = newMass * (pArea / totalArea);
                Vector2 pCentroid  = PolygonUtils.ComputeCentroid(pieces[i]);
                var     localVerts = BuildLocalVerts(pieces[i], compoundCentroid, cosR, sinR);
                float   iOwn       = PolygonUtils.ComputeInertia(localVerts, pMass);
                float   d2         = (pCentroid - compoundCentroid).LengthSquared();
                newInertia += iOwn + pMass * d2;
                parts[i]    = new PolygonShape(localVerts);
            }
            newShape = new CompoundShape(parts);
        }

        // ── Apply to entity ───────────────────────────────────────────────────
        ref var t2   = ref _world.GetComponent<Transform>(asteroid);
        t2.Position  = compoundCentroid;

        ref var rb2  = ref _world.GetComponent<RigidBody>(asteroid);
        rb2.Mass     = newMass;
        rb2.Inertia  = newInertia;

        ref var col2 = ref _world.GetComponent<Collider>(asteroid);
        col2.Shape   = newShape;

        ref var fs2  = ref _world.GetComponent<FractureState>(asteroid);
        fs2.AbsorbedEnergy = 0f;
        int newFaultCount  = Math.Max(0, fs2.FaultAngles.Length - 1);
        var newFaults      = new float[newFaultCount];
        for (int i = 0; i < newFaultCount; i++)
            newFaults[i] = (float)(_rng.NextDouble() * 2 * MathF.PI);
        fs2.FaultAngles = newFaults;

        return true;
    }

    /// <summary>
    /// Converts world-space polygon vertices to the entity's local coordinate space:
    /// translate by -origin then un-rotate by -entityRotation.
    /// </summary>
    private static Vector2[] BuildLocalVerts(
        IReadOnlyList<Vector2> worldVerts, Vector2 origin, float cosNegRot, float sinNegRot)
    {
        var local = new Vector2[worldVerts.Count];
        for (int i = 0; i < worldVerts.Count; i++)
        {
            var offset = worldVerts[i] - origin;
            local[i] = new Vector2(
                offset.X * cosNegRot - offset.Y * sinNegRot,
                offset.X * sinNegRot + offset.Y * cosNegRot);
        }
        return local;
    }

    private void SpawnLiveFragment(
        Vector2[] worldFragVerts,
        Transform parentAt, Velocity parentAv, RigidBody parentRb,
        FractureProperties fp, FractureState parentFs,
        Vector2 kickedLinear, float energyFactor, float parentArea,
        bool isfarPiece)
    {
        // Fragments spawn at Rotation=0: local verts = worldVerts - centroid, no un-rotation.
        // TransformVertices(centroid, 0) correctly places them in world space at spawn.
        // Angular velocity drives spin from there; no double-rotation issue.
        var (centroid, localVerts) = PolygonUtils.RecenterVertices(worldFragVerts);

        float fragArea = MathF.Abs(PolygonUtils.ComputeArea(localVerts));
        float massFrac = parentArea > 0f ? fragArea / parentArea : 1f;
        float fragMass = parentRb.Mass * massFrac;
        float inertia  = PolygonUtils.ComputeInertia(localVerts, fragMass);

        Vector2 r         = centroid - parentAt.Position;
        Vector2 rotVel    = new(-parentAv.Angular * r.Y, parentAv.Angular * r.X);
        Vector2 spreadDir = r.LengthSquared() > 0.5f
            ? Vector2.Normalize(r)
            : Vector2.Normalize(new Vector2((float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5)));

        var   ffc       = _cfg.Fracture.Fragment;
        float baseSpeed = isfarPiece
            ? ffc.FarPieceBaseSpeed
            : ffc.NearBaseSpeedMin + (float)(_rng.NextDouble() * ffc.NearBaseSpeedRange);
        float massScale   = MathF.Sqrt(MathF.Max(0.1f, MassRef / fragMass));
        float spreadSpeed = baseSpeed
                          * (ffc.SpreadNormMin + energyFactor * (1f - ffc.SpreadNormMin))
                          * massScale
                          * float.Lerp(0.5f, 1.4f, fp.Brittleness);

        Vector2 fragLinear  = kickedLinear + rotVel + spreadDir * spreadSpeed;
        float   fragAngular = parentAv.Angular
                            + (float)(_rng.NextDouble() - 0.5) * float.Lerp(0.5f, 2.5f, fp.Brittleness);

        int newFaultCount = Math.Max(0, parentFs.FaultAngles.Length - 1);
        var newFaults     = new float[newFaultCount];
        for (int i = 0; i < newFaultCount; i++)
            newFaults[i] = (float)(_rng.NextDouble() * 2 * MathF.PI);

        Entity fe = _world.CreateEntity();
        _world.AddComponent(fe, new Transform { Position = centroid, Rotation = 0f });  // ← not parent rotation
        _world.AddComponent(fe, new Velocity { Linear = fragLinear, Angular = fragAngular });
        _world.AddComponent(fe, new RigidBody
        {
            Mass = fragMass, LinearDrag = ffc.LinearDrag, AngularDrag = ffc.AngularDrag,
            Inertia = inertia, Restitution = parentRb.Restitution,
        });
        _world.AddComponent(fe, new Collider
        {
            Shape = new PolygonShape(localVerts),
            Layer = Layers.Asteroid,
            Mask  = Layers.Player | Layers.Bullet,
        });
        _world.AddComponent(fe, new AsteroidData());
        _world.AddComponent(fe, fp);   // same material as parent
        _world.AddComponent(fe, new FractureState { AbsorbedEnergy = 0f, FaultAngles = newFaults });
    }

    private void SpawnDebrisFrag(
        Vector2[] worldFragVerts,
        Transform parentAt, Velocity parentAv,
        Vector2 impactPoint, Vector2 bulletDir,
        float energyFactor, float parentMass, bool isBlast)
    {
        var (centroid, localVerts) = PolygonUtils.RecenterVertices(worldFragVerts);

        Vector2 r         = centroid - parentAt.Position;
        Vector2 rotVel    = new(-parentAv.Angular * r.Y, parentAv.Angular * r.X);
        Vector2 spreadDir = r.LengthSquared() > 0.5f ? Vector2.Normalize(r) : bulletDir;

        var   dc        = _cfg.Fracture.Debris;
        float baseSpeed = isBlast
            ? dc.BlastSpeedMin  + (float)(_rng.NextDouble() * dc.BlastSpeedRange)
            : dc.NormalSpeedMin + (float)(_rng.NextDouble() * dc.NormalSpeedRange);
        float speed = baseSpeed * (0.4f + energyFactor * 0.6f);

        Vector2 vel   = parentAv.Linear + rotVel + spreadDir * speed;
        float   omega = parentAv.Angular + (float)(_rng.NextDouble() - 0.5) * 4f;
        float   ttl   = isBlast
            ? float.Lerp(dc.BlastTtlMin,  dc.BlastTtlMax,  (float)_rng.NextDouble())
            : float.Lerp(dc.NormalTtlMin, dc.NormalTtlMax, (float)_rng.NextDouble());

        Entity de = _world.CreateEntity();
        _world.AddComponent(de, new Transform { Position = centroid, Rotation = 0f });  // ← not parent rotation
        _world.AddComponent(de, new Velocity { Linear = vel, Angular = omega });
        _world.AddComponent(de, new RigidBody { Mass = dc.Mass, LinearDrag = isBlast ? dc.BlastLinearDrag : dc.NormalLinearDrag });
        _world.AddComponent(de, new DebrisVisual
        {
            MaxTTL = ttl, LocalVerts = localVerts, IsBlastParticle = isBlast,
        });
        _world.AddComponent(de, new TimeToLive { Remaining = ttl });
    }

    private void SpawnDebrisCloud(Vector2 origin, Vector2 dir, int count, bool isBlast = false)
    {
        var cc   = _cfg.Fracture.DebrisCloud;
        var perp = new Vector2(-dir.Y, dir.X);
        int n    = count > 0 ? count : cc.Count;
        var dc = _cfg.Fracture.Debris;
        for (int i = 0; i < n; i++)
        {
            // Blast particles radiate in all directions; surface-hit sparks favour bullet direction.
            float speedBase = isBlast
                ? dc.BlastSpeedMin  + (float)(_rng.NextDouble() * dc.BlastSpeedRange)
                : cc.SpeedMin + (float)(_rng.NextDouble() * (cc.SpeedMax - cc.SpeedMin));
            float angle = isBlast
                ? (float)(_rng.NextDouble() * 2 * MathF.PI)               // radial
                : 0f;
            Vector2 blastDir = isBlast
                ? new Vector2(MathF.Cos(angle), MathF.Sin(angle))
                : dir + perp * (float)(_rng.NextDouble() - 0.5) * cc.LateralSpread / speedBase;
            float spread = isBlast ? 0f : (float)(_rng.NextDouble() - 0.5) * cc.LateralSpread;
            Vector2 vel  = isBlast ? blastDir * speedBase : dir * speedBase + perp * spread;
            float ttl    = isBlast
                ? dc.BlastTtlMin + (float)(_rng.NextDouble() * (dc.BlastTtlMax - dc.BlastTtlMin))
                : cc.TtlMin + (float)(_rng.NextDouble() * (cc.TtlMax - cc.TtlMin));

            float s   = cc.SizeMin + (float)(_rng.NextDouble() * (cc.SizeMax - cc.SizeMin));
            var   pts = new Vector2[]
            {
                new(0f, -s), new(s * 0.87f, s * 0.5f), new(-s * 0.87f, s * 0.5f),
            };

            Entity de = _world.CreateEntity();
            _world.AddComponent(de, new Transform { Position = origin });
            _world.AddComponent(de, new Velocity { Linear = vel, Angular = (float)(_rng.NextDouble() - 0.5) * 6f });
            _world.AddComponent(de, new RigidBody { Mass = cc.Mass, LinearDrag = cc.LinearDrag });
            _world.AddComponent(de, new DebrisVisual { MaxTTL = ttl, LocalVerts = pts, IsBlastParticle = isBlast });
            _world.AddComponent(de, new TimeToLive { Remaining = ttl });
        }
    }

    // -------------------------------------------------------------------------
    // Spawning
    // -------------------------------------------------------------------------

    private void SpawnWave(int count)
    {
        string matName = _cfg.MaterialForWave(_wave);
        float  safeR   = _cfg.Waves.SafeCentreRadius;
        var    ac      = _cfg.Asteroid;
        for (int i = 0; i < count; i++)
        {
            Vector2 pos;
            do
            {
                pos = new Vector2(
                    (float)_rng.NextDouble() * _w,
                    (float)_rng.NextDouble() * _h);
            } while ((pos - new Vector2(_w / 2f, _h / 2f)).Length() < safeR);

            float radius = ac.RadiusMin + (float)_rng.NextDouble() * (ac.RadiusMax - ac.RadiusMin);
            SpawnAsteroid(_world, _rng, pos, radius, _cfg, matName);
        }
    }

    private static Entity SpawnPlayer(World world, Vector2 pos, PlayerConfig pc)
    {
        Entity e = world.CreateEntity();
        world.AddComponent(e, new Transform { Position = pos });
        world.AddComponent(e, new Velocity());
        world.AddComponent(e, new RigidBody { Mass = pc.Mass, LinearDrag = pc.LinearDrag, Restitution = pc.Restitution });
        world.AddComponent(e, new Collider
        {
            Shape = new CircleShape(pc.Radius),
            Layer = Layers.Player,
            Mask  = Layers.Asteroid,
        });
        world.AddComponent(e, new PlayerTag());
        world.AddComponent(e, new LastFacing { Dir = new Vector2(0, -1) });
        world.AddComponent(e, new ShootCooldown());
        return e;
    }

    private static void SpawnAsteroid(World world, Random rng, Vector2 pos, float radius,
                                       GameConfig cfg, string materialName = "rock")
    {
        FractureProperties fp = cfg.GetMaterial(materialName);
        var ac    = cfg.Asteroid;
        int sides = 8 + rng.Next(5);
        var (localVerts, faultAngles) = PolygonUtils.GenerateConvex(
            sides, radius, rng, faultCount: fp.FaultCount);

        float mass    = radius * radius * MathF.PI * ac.Density;
        float inertia = PolygonUtils.ComputeInertia(localVerts, mass);

        float speed = ac.SpeedMin + (float)(rng.NextDouble() * ac.SpeedRange);
        float dir   = (float)(rng.NextDouble() * 2 * MathF.PI);
        float omega = (float)(rng.NextDouble() - 0.5) * ac.SpinMax;

        Entity e = world.CreateEntity();
        world.AddComponent(e, new Transform { Position = pos });
        world.AddComponent(e, new Velocity
        {
            Linear  = new Vector2(MathF.Cos(dir), MathF.Sin(dir)) * speed,
            Angular = omega,
        });
        world.AddComponent(e, new RigidBody
        {
            Mass = mass, LinearDrag = ac.LinearDrag, AngularDrag = ac.AngularDrag,
            Inertia = inertia, Restitution = ac.Restitution,
        });
        world.AddComponent(e, new Collider
        {
            Shape = new PolygonShape(localVerts),
            Layer = Layers.Asteroid,
            Mask  = Layers.Player | Layers.Bullet,
        });
        world.AddComponent(e, new AsteroidData());
        world.AddComponent(e, fp);
        world.AddComponent(e, new FractureState { AbsorbedEnergy = 0f, FaultAngles = faultAngles });
    }
}

// =============================================================================
// Collision layers
// =============================================================================

static class Layers
{
    public const int Player = 1;
    public const int Asteroid = 2;
    public const int Bullet = 4;
}

// =============================================================================
// Demo-specific components
// =============================================================================

struct PlayerTag { }
struct BulletTag { }
struct AsteroidData { }   // tag — identity only; material/state in FractureProperties + FractureState

struct LastFacing     { public Vector2 Dir; }
struct ShootCooldown  { public float Remaining; }
struct TimeToLive     { public float Remaining; }
struct ImpactEffect   { public float BlastRadius; public float MaxTTL; }

struct DebrisVisual
{
    public float     MaxTTL;
    public Vector2[] LocalVerts;
    public bool      IsBlastParticle;
}

// =============================================================================
// PlayerInputSystem — WASD thrust + face-direction tracking
// =============================================================================

class PlayerInputSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity      _player;
    private readonly float       _thrust;

    public PlayerInputSystem(InputSystem input, Entity player, float thrust)
    { _input = input; _player = player; _thrust = thrust; }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(_player)) return;

        float ax = 0f, ay = 0f;
        if (_input.IsHeld(KeyCode.W)) ay -= _thrust;
        if (_input.IsHeld(KeyCode.S)) ay += _thrust;
        if (_input.IsHeld(KeyCode.A)) ax -= _thrust;
        if (_input.IsHeld(KeyCode.D)) ax += _thrust;

        if (ax != 0f || ay != 0f)
            PhysicsSystem.ApplyForce(world, _player, new Vector2(ax, ay));

        // Update LastFacing so bullets fly in the direction of movement.
        ref var v = ref world.GetComponent<Velocity>(_player);
        if (v.Linear.LengthSquared() > 400f)
        {
            ref var lf = ref world.GetComponent<LastFacing>(_player);
            lf.Dir = Vector2.Normalize(v.Linear);
        }
    }
}

// =============================================================================
// WrapSystem — toroidal world for all entities
// =============================================================================

class WrapSystem : ISystem
{
    private readonly float _w, _h;
    public WrapSystem(int w, int h) { _w = w; _h = h; }

    public void Update(World world, double dt)
    {
        world.ForEach<Transform>((Entity _, ref Transform t) =>
        {
            if (t.Position.X < 0) t.Position = new Vector2(_w, t.Position.Y);
            if (t.Position.X > _w) t.Position = new Vector2(0, t.Position.Y);
            if (t.Position.Y < 0) t.Position = new Vector2(t.Position.X, _h);
            if (t.Position.Y > _h) t.Position = new Vector2(t.Position.X, 0);
        });
    }
}

// =============================================================================
// ShootSystem — F key fires a bullet in the LastFacing direction
// =============================================================================

class ShootSystem : ISystem
{
    private readonly InputSystem _input;
    private readonly Entity      _player;
    private readonly BulletConfig _bc;
    private static readonly Color BulletColor = new(255, 230, 80);

    public ShootSystem(InputSystem input, Entity player, BulletConfig bc)
    { _input = input; _player = player; _bc = bc; }

    public void Update(World world, double dt)
    {
        if (!world.IsAlive(_player)) return;

        ref var cd = ref world.GetComponent<ShootCooldown>(_player);
        if (cd.Remaining > 0f) cd.Remaining -= (float)dt;

        if (!_input.IsHeld(KeyCode.F)) return;
        if (cd.Remaining > 0f) return;
        cd.Remaining = _bc.Cooldown;

        ref var t  = ref world.GetComponent<Transform>(_player);
        ref var lf = ref world.GetComponent<LastFacing>(_player);
        Vector2 dir     = lf.Dir;
        Vector2 spawnAt = t.Position + dir * (_bc.Radius * 4f);

        Entity b = world.CreateEntity();
        world.AddComponent(b, new Transform { Position = spawnAt });
        world.AddComponent(b, new Velocity { Linear = dir * _bc.Speed });
        // No RigidBody — bullets fly at constant velocity (MovementSystem only needs Velocity).
        // Without RigidBody the CollisionSystem cannot apply elastic impulse to bullets,
        // so they never bounce. Mass comes from config in FragmentAsteroid.
        world.AddComponent(b, new Collider
        {
            Shape = new CircleShape(_bc.Radius),
            Layer = Layers.Bullet,
            Mask  = Layers.Asteroid,
        });
        world.AddComponent(b, new BulletTag());
        world.AddComponent(b, new TimeToLive { Remaining = _bc.Ttl });
        world.AddComponent(b, new BulletVisual { Color = BulletColor });
    }
}

// =============================================================================
// TimeToLiveSystem — remove expired entities
// =============================================================================

class TimeToLiveSystem : ISystem
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

// =============================================================================
// EventFlushSystem
// =============================================================================

class EventFlushSystem : ISystem
{
    private readonly EventBus _bus;
    public EventFlushSystem(EventBus bus) { _bus = bus; }
    public void Update(World world, double dt) => _bus.Flush();
}

// =============================================================================
// Visual marker (just for the renderer to find bullets)
// =============================================================================

struct BulletVisual { public Color Color; }

// =============================================================================
// DemoRenderer — draws everything via the engine's IRenderer (backend-agnostic)
// =============================================================================

class DemoRenderer
{
    private readonly int   _w, _h;
    private readonly float _playerRadius;

    // Palette (backend-agnostic engine colours).
    private static readonly Color Bg         = new(8, 8, 16);
    private static readonly Color Grid       = new(255, 255, 255, 14);
    private static readonly Color RockFill   = new(62, 55, 50);
    private static readonly Color RockStroke = new(145, 135, 120);
    private static readonly Color RockGlow   = new(80, 70, 60, 22);
    private static readonly Color PlayerFill = new(70, 130, 240);
    private static readonly Color PlayerEdge = new(160, 200, 255);
    private static readonly Color NoseFill   = new(220, 240, 255);

    private static readonly FontSpec HudFont  = new("monospace", 20f, bold: true);
    private static readonly FontSpec BigFont  = new("monospace", 36f, bold: true);
    private static readonly FontSpec HintFont = new("monospace", 13f);

    // Render interpolation: snap instead of lerp when the per-step delta is huge
    // (screen wrap, fresh spawns) so entities don't streak across the screen.
    private const float TeleportThresholdSq = 120f * 120f;

    public DemoRenderer(int w, int h, float playerRadius) { _w = w; _h = h; _playerRadius = playerRadius; }

    private static (Vector2 pos, float rot) Interp(in Transform t, float alpha)
    {
        Vector2 d = t.Position - t.PreviousPosition;
        if (d.LengthSquared() > TeleportThresholdSq) return (t.Position, t.Rotation);
        return (t.PreviousPosition + d * alpha, LerpAngle(t.PreviousRotation, t.Rotation, alpha));
    }

    private static float LerpAngle(float a, float b, float t)
    {
        float diff = b - a;
        while (diff >  MathF.PI) diff -= MathF.Tau;
        while (diff < -MathF.PI) diff += MathF.Tau;
        return a + diff * t;
    }

    public void Draw(World world, IRenderer r, int wave, int score, float nextWaveCD, float alpha)
    {
        // Background + grid.
        r.Begin(Bg);
        for (int x = 0; x <= _w; x += 80) r.DrawLine(new Vector2(x, 0), new Vector2(x, _h), Grid);
        for (int y = 0; y <= _h; y += 80) r.DrawLine(new Vector2(0, y), new Vector2(_w, y), Grid);

        // Asteroids — glow, fill, outline. PolygonShape draws one piece;
        // CompoundShape (fractured survivor) draws each convex part.
        world.ForEach<Transform, Collider, AsteroidData>(
            (Entity _, ref Transform t, ref Collider c, ref AsteroidData _) =>
        {
            var (pos, rot) = Interp(t, alpha);
            if (c.Shape is PolygonShape poly)
                DrawAsteroid(r, poly.GetWorldVertices(pos, rot));
            else if (c.Shape is CompoundShape compound)
                for (int i = 0; i < compound.PartCount; i++)
                    if (compound.GetPart(i) is PolygonShape part)
                        DrawAsteroid(r, part.GetWorldVertices(pos, rot));
        });

        // Bullets — glow + bright dot.
        world.ForEach<Transform, BulletTag, BulletVisual>(
            (Entity _, ref Transform t, ref BulletTag _, ref BulletVisual bv) =>
        {
            var (pos, _) = Interp(t, alpha);
            r.FillCircle(pos, 10f, bv.Color.WithAlpha(60));
            r.FillCircle(pos, 5f, bv.Color);
        });

        // Debris fragments and blast particles — fading polygons.
        world.ForEach<Transform, DebrisVisual, TimeToLive>(
            (Entity _, ref Transform t, ref DebrisVisual dv, ref TimeToLive ttl) =>
        {
            float progress = 1f - MathF.Max(0f, ttl.Remaining / dv.MaxTTL);
            Color col;
            if (dv.IsBlastParticle)
            {
                // Blast particles: hot amber colour, fade faster.
                byte a2 = (byte)(int)MathF.Max(0f, 255f * (1f - progress * 1.4f));
                col = new Color(255, 200, 80, a2);
            }
            else
            {
                byte a = (byte)(int)MathF.Max(0f, 220f * (1f - progress));
                col = new Color(120, 110, 100, a);
            }
            var (pos, rot) = Interp(t, alpha);
            DrawDebrisPoly(r, dv.LocalVerts, pos, rot, col);
        });

        // Impact effects — expanding ring + inner flash.
        world.ForEach<Transform, ImpactEffect, TimeToLive>(
            (Entity _, ref Transform t, ref ImpactEffect fx, ref TimeToLive ttl) =>
                DrawImpact(r, t.Position, fx, ttl.Remaining));

        // Player — circle with nose indicator.
        world.ForEach<Transform, PlayerTag, LastFacing>(
            (Entity _, ref Transform t, ref PlayerTag _, ref LastFacing lf) =>
        {
            var (pos, _) = Interp(t, alpha);
            DrawPlayer(r, pos, lf.Dir);
        });

        // HUD.
        DrawHud(r, wave, score, world.Count<AsteroidData>(), nextWaveCD);

        r.End();
    }

    // ---- Private draw helpers ----

    private void DrawImpact(IRenderer r, Vector2 pos, ImpactEffect fx, float remaining)
    {
        // progress: 0 at spawn → 1 at death
        float progress = 1f - MathF.Max(0f, remaining / fx.MaxTTL);

        // Inner flash: bright white-orange disk that shrinks as the blast dissipates.
        float flashR = fx.BlastRadius * (1f - progress * 0.85f);
        byte  flashA = (byte)(int)MathF.Max(0f, 230f * (1f - progress * 1.6f));
        r.FillCircle(pos, flashR, new Color(255, 210, 120, flashA));

        // Shockwave ring: expands from blastRadius to 2× blastRadius, fades out.
        float ringR = fx.BlastRadius * (1f + progress);
        byte  ringA = (byte)(int)MathF.Max(0f, 200f * (1f - progress));
        float ringW = MathF.Max(1f, 3.5f * (1f - progress));
        r.DrawCircle(pos, ringR, new Color(255, 160, 60, ringA), ringW);
    }

    private void DrawDebrisPoly(IRenderer r, Vector2[] localVerts, Vector2 pos, float rot, Color color)
    {
        if (localVerts.Length < 3) return;
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Span<Vector2> world = localVerts.Length <= 16
            ? stackalloc Vector2[localVerts.Length]
            : new Vector2[localVerts.Length];
        for (int i = 0; i < localVerts.Length; i++)
        {
            var v = localVerts[i];
            world[i] = new Vector2(v.X * cos - v.Y * sin + pos.X, v.X * sin + v.Y * cos + pos.Y);
        }
        r.FillPolygon(world, color);
    }

    private void DrawAsteroid(IRenderer r, Vector2[] worldVerts)
    {
        if (worldVerts.Length < 3) return;

        // Glow: rough centroid estimate via vertex average (close enough for glow radius).
        Vector2 approxCentre = Vector2.Zero;
        foreach (var v in worldVerts) approxCentre += v;
        approxCentre /= worldVerts.Length;

        float roughR = 0f;
        foreach (var v in worldVerts)
            roughR = MathF.Max(roughR, (v - approxCentre).Length());

        r.FillCircle(approxCentre, roughR + 8f, RockGlow);
        r.FillPolygon(worldVerts, RockFill);
        r.DrawPolygon(worldVerts, RockStroke, 1.5f);
    }

    private void DrawPlayer(IRenderer r, Vector2 pos, Vector2 facing)
    {
        float R = _playerRadius;
        r.FillCircle(pos, R, PlayerFill);
        r.DrawCircle(pos, R, PlayerEdge, 2f);
        Vector2 nose = pos + facing * (R - 3f);
        r.FillCircle(nose, 4f, NoseFill);
    }

    private void DrawHud(IRenderer r, int wave, int score, int remaining, float nextWaveCD)
    {
        r.DrawText($"Wave {wave}   Score {score}   Asteroids: {remaining}",
                   new Vector2(14f, 18f), new Color(180, 195, 220, 220), HudFont);

        if (remaining == 0 && nextWaveCD > 0)
        {
            string msg = $"Wave {wave + 1} incoming in {nextWaveCD:F1}s";
            Vector2 size = r.MeasureText(msg, BigFont);
            r.DrawText(msg, new Vector2((_w - size.X) / 2f, (_h - size.Y) / 2f), Color.White, BigFont);
        }

        // Controls hint (bottom-left).
        r.DrawText("WASD move   F shoot   Esc quit",
                   new Vector2(14f, _h - 18f), new Color(100, 110, 130, 180), HintFont);
    }
}
