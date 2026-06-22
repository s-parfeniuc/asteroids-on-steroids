# Destruction Engine Spec — Cell/Bond Fracture Model + Engine v2

**Status:** Design (pre-implementation).
**Supersedes:** the cut-based fracture in `physics_spec.md §5–8` and all of `fracture_spec.md` (archived). The rigid-body dynamics in `physics_spec.md §1` remain valid and are referenced, not replaced.
**Design reference:** the interactive browser prototype `prototypes/fracture-cells-v3.html` is the executable specification of the fracture *behaviour*. Where prose and prototype disagree, the prototype's intent wins; this document is the engine-grade formalisation.

---

## 1. Scope & Intent

The engine's defining character is **specialisation in physically-based destruction**. This spec defines:

1. The new **cell/bond-graph fracture model** (replaces runtime polygon splitting).
2. The **engine changes** needed to support it well: a Platform Abstraction Layer (UI-agnostic, WinForms-ready), fixed timestep, raycast queries, a contact solver, and compound-shape acceleration.
3. The **engine↔game contract** that keeps the engine reusable: the engine simulates destruction physics and *emits results*; the game decides what fragments *mean*.

Out of scope: the full game (waves, AI, leaderboard) — those live in the game layer and are specified in `spec.md §6`, updated to consume this model.

---

## 2. Boundary Principles

Two rules resolve almost every "engine or game?" question.

> **P1 — The engine owns the *physics* of destruction; the game owns the *meaning* of what breaks.**
> The engine knows cells, bonds, energy, stress, fragments, mass, inertia, collision. It does not know what an "asteroid" is, what "rock" means, or that small fragments score points.

> **P2 — The engine never references a UI toolkit.**
> All windowing, rendering, input, asset loading, and audio go through the Platform Abstraction Layer. SDL+Skia, WinForms+GDI+, and any future backend are interchangeable.

### 2.1 Responsibility map

| Concern | Engine | Game |
|---|---|---|
| ECS, world, fixed-step loop, timing | ✅ | |
| Rigid-body dynamics, collision, contacts, raycast | ✅ | |
| Destruction module: cells, bonds, fracture sim, energy | ✅ | material constants & catalog |
| `FractureResult` event contract | ✅ (emit) | ✅ (consume → spawn entities) |
| Material presets (rock/ice/metal), wave→material map | | ✅ |
| Asteroid shape/grain authoring, concave carving | | ✅ |
| Fragment → entity wiring, scoring, tags | | ✅ |
| Bullets as a weapon, enemy AI, waves, HUD, leaderboard | | ✅ |
| Platform Abstraction Layer (interfaces) | ✅ | |
| PAL backends (SDL/Skia, WinForms/GDI+) | separate assemblies | |

---

## 3. Engine Architecture Changes

### 3.1 Module layout

```
Engine.Core         ECS (World, Entity, SparseSet), fixed-step GameLoop, time
Engine.Physics      RigidBody, integration, collision, contact solver, raycast
Engine.Destruction  cells, bond graph, FractureSimulator, energy model   ← signature module
Engine.Rendering    IRenderer abstraction, Camera, render systems (PAL-facing)
Engine.Platform     PAL interfaces: IRenderer, IResourceManager, IAudioBackend, input, window
─ adapters (separate assemblies) ─
Engine.Platform.Sdl      SDL2 window + input, SkiaSharp renderer, OpenAL/SDL audio
Engine.Platform.WinForms WinForms Form + input, GDI+ renderer, NAudio audio
```

The core and destruction modules reference **only** `Engine.Platform` interfaces, never an adapter.

### 3.2 Platform Abstraction Layer (PAL)

A coherent boundary for everything platform-specific.

| Interface | Responsibility | SDL backend | WinForms backend |
|---|---|---|---|
| `IRenderer` | immediate-mode 2D primitives + transform stack | SkiaSharp | GDI+ |
| input feed | OS events → engine `KeyCode`, thread-safe snapshot | SDL poll | Form key/mouse events |
| `IResourceManager` | id→asset cache, ref-count, lifetime | SkiaSharp decode | `Bitmap`/`Font` |
| `IAudioBackend` | `Play(id, vol, pan)`, streaming music | OpenAL / SDL_mixer | NAudio |
| `IGameWindow` | window lifecycle, present, drives the loop host | SDL window | `Form` |

#### `IRenderer` (immediate-mode)

```csharp
public interface IRenderer
{
    void Begin(in Color clear);
    void PushTransform(in Matrix3x2 m);   // camera / entity transforms compose here
    void PopTransform();

    void DrawPolygon(ReadOnlySpan<Vector2> verts, in Color stroke, float width);
    void FillPolygon(ReadOnlySpan<Vector2> verts, in Color fill);
    void DrawCircle(Vector2 c, float r, in Color stroke, float width);
    void FillCircle(Vector2 c, float r, in Color fill);
    void DrawLine(Vector2 a, Vector2 b, in Color color, float width);
    void DrawText(string text, Vector2 pos, in Color color, FontId font);
    void DrawImage(ImageId image, in Matrix3x2 transform, in Color tint);

    void End();   // backend flushes/presents
}
```

`Color`, `ImageId`, `FontId`, `SoundId` are engine value types. Convex polygons (cells) map directly to `FillPolygon`/`DrawPolygon`; a `CompoundShape` is drawn as one `FillPolygon` per part. Render systems target `IRenderer` exclusively — the asteroid demo's direct SkiaSharp calls move behind it.

#### Input feed

OS input originates in the UI adapter; `InputSystem` stays platform-free.

```
Adapter (UI thread or SDL poll) → maps OS keys to engine KeyCode → writes a thread-safe InputSnapshot
GameLoop (fixed step) → samples InputSnapshot once per tick
InputSystem → derives Held / PressedThisFrame / ReleasedThisFrame from successive snapshots
```

`KeyCode`/`MouseButton` are engine enums; the adapter owns the OS→enum mapping. The snapshot handoff is double-buffered so the UI thread and loop thread never tear.

#### `IResourceManager` / `IAudioBackend`

```csharp
public interface IResourceManager
{
    ImageId LoadImage(string path);   // decoded + cached by the backend; engine holds the id
    FontId  LoadFont (string path, float size);
    SoundId LoadSound(string path);
    void    Release(in AssetId id);   // ref-counted; backend frees the native handle
}

public interface IAudioBackend
{
    void PlaySound(SoundId id, float volume = 1f, float pan = 0f);
    void PlayMusic(SoundId id, bool loop = true);
    void StopMusic();
}
```

The engine keeps the cache/ref-count logic; the backend owns decode and native handles. This splits today's GDI+-coupled `ResourceManager` into engine-side cache + backend-side loader.

### 3.3 Fixed timestep + interpolation

Replaces the variable-`dt` loop. Required once fragments stack and rest on each other.

```
accumulator += realDelta
while accumulator >= FIXED_DT:
    StorepreviousState()                 // PreviousPosition/PreviousRotation per body
    StepSimulation(FIXED_DT)             // input snapshot, physics, collision, fracture flush
    accumulator -= FIXED_DT
alpha = accumulator / FIXED_DT
Render(alpha)                            // interpolate pose = lerp(prev, curr, alpha)
```

- `FIXED_DT` default `1/120 s` (configurable). Physics and fracture run only inside the fixed step.
- Bodies gain `PreviousPosition`/`PreviousRotation`; the render pass interpolates so motion is smooth regardless of display rate.
- A max-steps clamp (e.g. 5) prevents the spiral-of-death on a stall.
- **Determinism:** with fixed `dt` + seeded RNG, the simulation is reproducible (replays, leaderboard integrity).

### 3.4 Raycast / shape-cast queries

Bullets become rays, not bodies — eliminates tunnelling and the `NearestPointOnBoundary` patch, and yields an exact impact point + surface normal.

```csharp
public readonly struct RayHit
{
    public Entity Entity;
    public Vector2 Point;     // world-space impact on the surface
    public Vector2 Normal;    // outward surface normal at the hit
    public float   T;         // 0..1 along the segment
    public int     PartIndex; // which CompoundShape cell was struck (-1 if simple)
}

bool Raycast(Vector2 from, Vector2 to, int layerMask, out RayHit hit);   // nearest hit
int  RaycastAll(Vector2 from, Vector2 to, int layerMask, Span<RayHit> hits);
```

Implemented against the broad-phase (segment vs cell AABBs) then exact ray-vs-convex per candidate. `PartIndex` feeds the fracture sim directly (the struck cell). Grenades / physical projectiles can still exist as bodies; raycast is the default for guns.

### 3.5 Contact solver, friction, sleeping

`physics_spec.md §1.4`'s rotational impulse is already implemented. Add:

- **Solver iterations:** resolve contacts in a small fixed number of passes (default 4–8) per step so stacked fragments settle without jitter.
- **Friction:** tangential impulse (Coulomb, clamped to `μ·jₙ`); add `Friction` to `RigidBody`.
- **Sleeping:** bodies below linear/angular velocity thresholds for N steps deactivate (skipped by integration/solver until touched). Essential when a field fills with settled debris.

### 3.6 CompoundShape internal broad-phase

The real performance lever at scale (dozens of asteroids × ~100 cells = many-part compounds).

- Today `CompoundShape.Intersects` fans across all parts: compound-vs-compound is O(partsA × partsB).
- Add a per-compound **bounding-volume hierarchy** (or grid) over its cells, built once when the shape is created/rebuilt. Narrow phase first prunes by the other shape's AABB → only overlapping cells are SAT-tested. Brings the common case to ≈ O(parts) and makes 100-cell bodies practical.
- `LastHitPartIndex` is retained (the deepest-contact cell), feeding fracture re-targeting (§4.9).

### 3.7 Concurrency & determinism

A scoped, profiling-gated position — **not** the speculative system-scheduler taxonomy (the 7 computation models, `ISystemMetadata`, wave scheduler, double-buffered stores from `spec.md §5`), which is **cut**: it targets entity-iteration parallelism, and entity counts here are dozens–hundreds of bodies, not thousands. Cells are not entities.

What *is* worth parallelising, in priority order:

1. **Algorithmic first (mandatory):** the §3.6 internal broad-phase. A bigger win than threads, and required regardless.
2. **Collision narrow phase (optional, after profiling):** `Parallel.For` over candidate pairs; each writes its contact into a **deterministic, pre-indexed slot** (no order-dependent accumulation), reduced sequentially. This preserves reproducibility.
3. **Fracture spikes (optional):** a mass-destruction frame can fire many fractures; these are independent per struck body and can run on a worker, with entity spawning deferred to the sync point.

Hard rule: **no parallel path may change results vs the sequential path.** Determinism (fixed step + seeded RNG + deterministic reduction) is a product requirement, not a nicety. Everything here is gated behind a profiler showing the narrow phase is the bottleneck; the data structures (deferred entity ops, no shared mutable state in hot loops) are already shaped to allow it.

---

## 4. The Cell/Bond Fracture Model

All geometry is convex; all kernels (clip, SAT, area, centroid, inertia) operate on convex inputs only. The body's true (possibly concave) silhouette is the *union* of its convex cells.

### 4.1 Cells & tessellation

A fracturable body is pre-fractured **once at spawn** into convex Voronoi cells.

- **Seeds:** scattered by a jittered grid whose spacing derives from a **grain** (target cell area, a material property). Constant grain ⇒ large bodies get more cells; small bodies fewer — a constant material grain size.
- **Cell construction:** each cell = the bounding polygon clipped by the perpendicular bisector half-plane against every other seed (`ClipConvexByHalfPlane`). Each cell is convex by construction.
- **Concavity = absent cells:** tessellate the full convex bound using *all* seeds, then discard cells whose seed fails a shape-membership predicate. Kept cells retain their true size (their bisectors with removed neighbours still bound them); removed cells leave gaps. A crater is missing cells; a dumbbell neck is a thin column of cells. Concavity and damage share one representation.

```csharp
// Engine.Destruction
public static CellSet Tessellate(
    IReadOnlyList<Vector2> convexBound,   // generous convex region
    ReadOnlySpan<Vector2>  seeds,
    Func<Vector2,bool>     membership,    // game-supplied; concave carving lives here
    float                  minCellArea);
```

Grain, seed scatter, and `membership` are **game/content** inputs; the tessellation algorithm is engine.

### 4.2 Bond graph & `FracturableBody`

```csharp
// Engine.Destruction (component)
public struct FracturableBody
{
    public Cell[] Cells;          // convex, vertices in body-local space (centroid-relative)
    public Bond[] Bonds;          // current adjacency (shrinks as cracks form)
    public FractureProperties Material;
    public FractureState State;   // absorbed energy, RNG seed
}

public struct Cell  { public Vector2[] Local; public Vector2 Centroid; public float Area; }
public struct Bond  { public int A, B; public float EdgeLength; public float Strength; }
```

- A `FracturableBody` is always **one connected component** of cells = one rigid body with one `CompoundShape` (one `PolygonShape` per cell).
- Bonds derive from shared Voronoi edges; `Strength = EdgeLength × Material.Toughness`.
- A body may carry **cracks** — internal bonds removed by a prior hit that did *not* disconnect it. Cracks weaken it for future hits (progressive damage) without changing its collision shape until cells actually separate.

### 4.3 Material model

```csharp
public struct FractureProperties
{
    public float Toughness;       // energy per unit bond length to break (bond strength scale)
    public float Brittleness;     // [0 ductile … 1 brittle] → crack reach + kinetic split
    public float GrainArea;       // target cell area (px²) at tessellation
    public float MinFragmentArea; // below → debris particles, not physics bodies
    public float Density;
    public float KineticFraction; // share of impact energy → fragment KE (vs surface) at B=0
}
```

| Material | Toughness | Brittleness | Grain | Character |
|---|---|---|---|---|
| Glass | low | 1.0 | small | shatters far into many shards |
| Ice | med | 0.8 | small | |
| Rock | high | 0.6 | med | breaks into a few chunks |
| Metal | very high | 0.15 | large | dents/chips locally, big shove |

Presets and wave→material mapping are **game** data (`config.json`).

### 4.4 Energy model

On a confirmed bullet→body hit (raycast gives point + normal + `PartIndex`):

```
v_rel_n   = |dot(v_bullet − v_body, normal)|
m_reduced = m_bullet · m_body / (m_bullet + m_body)
E_impact  = 0.5 · m_reduced · v_rel_n²
E_spin    = SpinEnergyFraction · 0.5 · I_body · ω²          // rotation contributes pre-stress energy
E_total   = E_impact + E_spin
threshold = Toughness · m_struck                             // m_struck = struck cell's proportional mass
combined  = E_total + State.AbsorbedEnergy
```

- **Below threshold:** `AbsorbedEnergy += E_total`; emit a surface-spark event; no topology change. (Accumulation → repeated small hits eventually fracture.)
- **At/above:** fracture proceeds; `AbsorbedEnergy = 0`.

### 4.5 Energy budget & kinetic/surface split

The conservation backbone. The available fracture energy is **partitioned**, not double-spent:

```
E_avail   = combined − threshold
kFrac     = lerp(KineticFraction, KineticFraction·0.3, Brittleness)   // brittle → more surface, less fling
E_kinetic = kFrac · E_avail        // becomes fragment spread KE (§4.8)
E_surface = (1 − kFrac) · E_avail  // the BUDGET that breaks bonds
E_loss    = absorbed by dissipation coefficient (heat/sound), implicit
```

Identity: `E_impact + E_spin ≈ E_surface + E_kinetic + E_loss`.

- **`E_surface` is the budget**: breaking a bond costs its `Strength`; total broken ≈ `E_surface / mean(Strength)`. This makes **force determine *how much* breaks**.
- **`E_kinetic`** is distributed to fragments by mass (lighter → faster) when they separate (§4.8), so the fling speeds are *derived from conserved energy*, not cosmetic.
- Refinement (open, §9): close the loop — measure actual Σ½mv² of spawned fragments and reconcile against `E_kinetic`.

### 4.6 Propagation

A stress flood from the struck cell, spending the surface budget. Mirrors `prototypes/fracture-cells-v3.html` `propagate()`.

```
budget = E_surface
energy[struck] = E_total
transmission = lerp(0.18, 0.96, Brittleness)        // per-hop reach — brittle travels far
frontier = { struck }
while frontier not empty and budget > 0:
    i = pop highest-energy cell; e = energy[i]
    gather intact bonds of i; for each, weight wₖ = lerp(1, max(0,alignₖ)^1.6, Directionality)
        alignₖ = dot(dir(i→k), impactDir)           // impactDir from the ray
    norm = count / Σwₖ                               // mean weight 1: REDISTRIBUTE, don't attenuate
    sort bonds by wₖ desc                            // spend budget on most-aligned first
    for each bond (to j), while budget > 0:
        deliver = e · min(wₖ·norm, 3)                // forward boosted (capped), off-axis starved
        if deliver > bond.EffStrength:
            break bond; budget −= bond.Strength
            tr = min((deliver − EffStrength) · transmission, e)   // child ≤ parent (no runaway)
            energy[j] = max(energy[j], tr); push j
```

The three axes are now orthogonal:

| Axis | Source | Controls |
|---|---|---|
| **Energy** (budget) | force | *how many* bonds break (total surface) |
| **Directionality** + impact angle | shot direction | *which* bonds — radial splash ↔ forward channel (Hertzian) |
| **Brittleness** (transmission) | material | per-hop *reach* — local chip ↔ far-travelling crack |

#### Spin pre-stress

Rotation primes the body to cleave into radial wedges (grindstone-burst). Per bond, before propagation:

```
m   = midpoint(cellA, cellB) − CoM;  r = |m|
tangentiality = |dot(dir(A→B), perp(m/r))|          // 1 = circumferential bond
preStress     = Kspin · ω² · (0.3 + 0.7·r/Rmax) · tangentiality
EffStrength   = max(ε, Strength − preStress)
```

`Kspin` auto-scales to mean strength (so the effect is material-independent in magnitude). Spinning bodies need less energy and cleave radially — emergent, replacing the old scalar `E_spin` bonus and `spinFaultAngle` hack.

### 4.7 Connected components → fragments

After propagation, run union-find over **surviving** bonds:

- **1 component** → the body stays whole (it absorbed cracks but didn't split); update its `CompoundShape` only if cells were removed.
- **N components** → the largest stays as the (updated) body; each other component becomes a new fragment body. Singletons below `MinFragmentArea` → debris particles (visual only).

Thin necks are graph bridges, so "shoot the neck → it splits" is automatic. This also subsumes compound progressive damage (`physics_spec §12.5`).

### 4.8 Fragment physics

For each separated component:

```
cells_c       = its cells;  area_c = Σ area
mass_c        = parentMass · area_c / parentArea
centroid_c    = area-weighted centroid (body-local → world)
inertia_c     = Σ (ComputeInertia(cell_local, cell_mass) + cell_mass · d²)   // parallel axis
# linear velocity
r             = centroid_c − parentCoM
rotVel        = (−parentω·r.y, parentω·r.x)         // ω×r tangential carry-over
spreadDir     = normalize(centroid_c − impactPoint)
spreadSpeed   = sqrt(2 · E_kinetic_share_c / mass_c) // derived from the conserved kinetic budget
linear_c      = parentLinear + rotVel + spreadDir · spreadSpeed
# angular velocity from off-centre bullet impulse + scatter
angular_c     = parentω + (r × J)/inertia_c + rand·lerp(0.5,2.5,Brittleness)
```

`E_kinetic` (§4.5) is split across components ∝ a chosen weighting (e.g. by inverse mass so light shards fly faster), so Σ fragment KE ≈ `E_kinetic`. Local verts are centroid-relative; **Transform.Position = world centroid** (the critical invariant), rotation 0 for fresh fragments (world verts already encode orientation).

### 4.9 Compound (already-fractured) re-fracture

The normal case once a body has cells. Today the demo returns early on a `CompoundShape` — that path becomes:

1. Raycast gives `PartIndex` = struck cell (or `LastHitPartIndex` from the contact).
2. Threshold uses the **struck cell's proportional mass**.
3. Propagation runs over the body's current bond graph starting at the struck cell.
4. Components recomputed; body updated / fragments spawned as §4.7–4.8.

No special "extract the part and split it" geometry — the struck cell is just the seed of the flood. Cleaner than the old `WithoutPart` rebuild.

### 4.10 Option C — cell re-split (optional, no hard granularity floor)

If a single struck cell absorbs more energy than the sum of its own bond strengths, it shatters into sub-cells (a local Voronoi inside the cell), spliced into the graph (sub-cells bond to each other and inherit bonds to the original cell's surviving neighbours by shared-edge test). Gives unbounded refinement on hard, concentrated hits while the common case stays cheap. **Caveat (open):** the neighbour re-bonding is approximate (§9).

### 4.11 Concave invariants

- Every cell is convex → all kernels stay on convex inputs; collision is the §3.6 compound.
- Apparent concavity is always a union/compound; cracks and craters are absent bonds/cells.
- A concave body's CoM may lie in empty space — the parallel-axis sum and spin field handle it; produces correct lopsided tumbling.
- Generation caveat (open, §9): boundary cells clip to the convex bound, so concave silhouettes are slightly ragged at coarse grain — finer grain or a post-clip cleans it; does not affect bond/connectivity behaviour.

---

## 5. Engine ↔ Game Contract

**Thin engine + events** (confirmed). The engine computes everything and emits a result; the game wires entities.

### 5.1 `FractureResult` / `FractureEvent`

```csharp
public readonly struct FragmentSpec
{
    public Vector2[] LocalVerts;   // centroid-relative, convex (single cell) or per-cell set
    public Vector2   Centroid;     // world
    public Vector2   Linear;
    public float     Angular;
    public float     Mass, Inertia, Area;
    public bool      IsDebris;     // below MinFragmentArea → particle, not a body
}

public readonly struct FractureResult
{
    public Entity        Body;            // the struck body (updated in place if it survives)
    public bool          BodySurvives;    // false → game destroys Body
    public FragmentSpec  UpdatedBody;     // new shape/mass/inertia for the survivor
    public FragmentSpec[] Fragments;      // new bodies to spawn
    public Vector2       ImpactPoint;
    public float         BlastRadius;
    public float         EnergySurface, EnergyKinetic;   // for FX/scoring/telemetry
    public FractureProperties Material;
}

public readonly struct FractureEvent { public FractureResult Result; }
```

### 5.2 `FractureSystem` (engine)

Subscribes to confirmed hits. Per hit: read material/state, compute energy (§4.4), partition (§4.5), propagate (§4.6), components (§4.7), build fragment physics (§4.8). Emits `FractureEvent`. **Modifies no entities itself** — purely computes and publishes.

### 5.3 `FractureHandler` (game)

Consumes `FractureEvent`: spawns fragment entities, attaching game components (`AsteroidTag`, `Health`, `PolygonVisual`, score value, `WaveTag`); updates or destroys `Body`; spawns debris particles; plays SFX; increments score. The engine never names these components.

### 5.4 Determinism

`FractureState` carries a per-body RNG seed; all stochastic choices (seed scatter, scatter spin) draw from it. Fixed step + seeded RNG ⇒ reproducible fractures.

---

## 6. Components (added / changed)

| Component | Layer | Change |
|---|---|---|
| `Transform` | engine | + `PreviousPosition`, `PreviousRotation` (interpolation) |
| `RigidBody` | engine | + `Friction`; sleeping fields (`SleepTimer`, `Asleep`) |
| `FracturableBody` | engine | **new** — cells, bonds, material, state (§4.2) |
| `CompoundShape` | engine | + internal broad-phase (BVH/grid over cells) (§3.6) |
| `FractureProperties` / `FractureState` | engine | aligned to §4.3 (toughness, brittleness, grain, kineticFraction) |
| `AsteroidTag`, `PolygonVisual`, weapon/bullet data | game | unchanged in spirit; bullets become raycast weapons |

---

## 7. System Execution Order (fixed step)

```
─ sample input snapshot ───────────────────────────────
ShipControlSystem / EnemyAISystem        (sequential) — forces, fire intents
WeaponSystem                             (sequential) — raycast bullets → hit events
─ physics step (FIXED_DT) ─────────────────────────────
PhysicsSystem                            — force/torque integration
MovementSystem                           — pose integration (stores Previous*)
WrapSystem                               — toroidal wrap
─ collision ───────────────────────────────────────────
CollisionSystem                          — broad + narrow (compound internal BVH) → contacts/events
ContactSolver                            — N iterations: impulse + friction; sleeping
─ destruction ─────────────────────────────────────────
FractureSystem                           — energy → budget → propagation → FractureEvent
FractureHandler (game)                   — spawn/update/destroy entities
─ cleanup ─────────────────────────────────────────────
TimeToLiveSystem                         — bullets/debris expiry
WaveManagerSystem (game)                 — progression
world.FlushDeferred()                    — commit creates/destroys
─ render (per display frame, interpolated by alpha) ────
RenderSystem → IRenderer                 — interpolated poses; compound = per-cell FillPolygon; HUD
```

---

## 8. Configuration Reference (`config.json`)

```
fixedDt              1/120
fracture.spinEnergyFraction   0.35
fracture.kineticFractionBase  per-material
fracture.directionalityDefault 0.4    (gameplay/weapon may override per shot)
fracture.transmissionRange    [0.18, 0.96]   (mapped by brittleness)
fracture.boostCap             3.0
fracture.budgetCoefficient    1.0     (E_surface → bond-break units; calibration §9)
fracture.materials            { rock:{toughness,brittleness,grainArea,minFragmentArea,density,kineticFraction}, ... }
fracture.waveMaterials        [ ... ]
physics.solverIterations      6
physics.sleepLinearThreshold  …
physics.frictionDefault       …
```

---

## 9. Open Calibration Questions

1. **Budget units** — `E_surface` and bond `Strength` share an abstract scale (`budgetCoefficient`). Calibrate so a reference bullet on a reference rock gives a satisfying mid break.
2. **Kinetic closure** — coefficient split (§4.5) vs measuring actual Σ½mv² and reconciling. Start with coefficient; measure later.
3. **Option C neighbour re-bonding** — sub-cells inheriting the parent cell's external bonds is approximate; verify it doesn't create floating sub-cells.
4. **Concave-clip cleanliness** — ragged boundary cells at coarse grain; decide whether to post-clip to the true silhouette or accept it.
5. **Parallel narrow-phase determinism** — confirm the pre-indexed-slot reduction is bit-reproducible before enabling.

---

## 10. Phased Implementation Roadmap

```
Phase A — Platform Abstraction Layer
  IRenderer + SDL/Skia adapter (port the demo onto it); input feed; IResourceManager; IAudioBackend stub.
  → engine compiles with zero UI-toolkit references.

Phase B — Loop & physics foundation
  Fixed timestep + interpolation; Previous* on Transform.
  Raycast/shape-cast query. Contact solver iterations + friction + sleeping.
  CompoundShape internal broad-phase.

Phase C — Destruction module (the signature)
  Tessellate (cells, concave membership). FracturableBody + bond graph.
  Energy model + budget + kinetic split. Propagation (transmission, directionality, spin pre-stress).
  Connected components → fragment specs. FractureResult/Event. FractureSystem.

Phase D — Game integration
  Material catalog + config. FractureHandler (entity wiring). Raycast weapon. Asteroid spawn (shape/grain).
  Debris particles. VisualMesh/cell rendering with craters.

Phase E — Asteroids on Steroids
  Waves, enemy AI, camera, states, leaderboard, HUD, audio.

Phase F — Optimisation (profiling-gated)
  Parallel narrow phase (deterministic). Object pooling for fragments/debris. Perf review.

Cut: spec.md §5 parallelism taxonomy (ISystemMetadata, wave scheduler, double-buffered stores).
```

> **WinForms note:** supported by construction — it is one PAL backend (`Engine.Platform.WinForms`: Form + GDI+ `IRenderer` + NAudio `IAudioBackend`). No engine or game code changes to add it.
