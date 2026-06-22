# Project Spec — 2D Game Engine + Asteroids Demo

**Stack:** C# / .NET 8, SDL2 (via Silk.NET), SkiaSharp rendering  
**Goal:** A reusable 2D game engine built on ECS architecture, demonstrated via progressively complex games — currently a two-player physics sandbox and the upcoming *Asteroids on Steroids* single-player game.

> **Platform note (updated):** The original spec targeted WinForms + GDI+ + NAudio. The stack has since moved to SDL2 (cross-platform windowing/input via Silk.NET) and SkiaSharp (cross-platform 2D rendering). The engine layer itself is rendering-agnostic; the platform-specific window + render loop lives in each demo's `SdlGameWindow.cs`. WinForms remains a planned future backend for Windows. NAudio is deferred; audio is not yet implemented.

---

## 1. Project Directory Structure

```
AsteroidsEngine/
│
├── Engine/                         ← reusable engine layer; knows nothing about Asteroids
│   │
│   ├── Core/
│   │   ├── Entity.cs               ← Entity struct: int Id + int Version
│   │   ├── World.cs                ← ECS world: component storage, queries, entity lifecycle
│   │   ├── ISystem.cs              ← interface all systems implement
│   │   └── GameLoop.cs             ← background thread, Stopwatch timing, system orchestration
│   │
│   ├── Components/                 ← engine-level components (game-agnostic)
│   │   ├── Transform.cs            ← Position (Vector2), Rotation (float radians)
│   │   ├── Velocity.cs             ← Linear (Vector2) + angular (float) velocity
│   │   ├── RigidBody.cs            ← Mass, Inertia, drag, AccumulatedForce/Torque
│   │   ├── Collider.cs             ← CollisionShape reference + Layer + Mask bitmask
│   │   ├── FractureProperties.cs   ← Brittleness, Toughness, FaultCount, MinFragmentArea
│   │   ├── FractureState.cs        ← AbsorbedEnergy (runtime), FaultAngles[]
│   │   ├── VisualMesh.cs           ← ConvexPieces[][]; visual surface distinct from collision
│   │   ├── Sprite.cs               ← ImageId (string key), Layer (int), Offset, Tint
│   │   ├── Health.cs               ← Current (int), Max (int)
│   │   └── Tags.cs                 ← DestroyTag, DisabledTag (zero-data marker structs)
│   │
│   ├── Systems/                    ← engine-level systems
│   │   ├── MovementSystem.cs       ← Transform += Velocity * dt
│   │   ├── PhysicsSystem.cs        ← force + torque integration; drag
│   │   ├── CollisionSystem.cs      ← broad phase + narrow phase; separation; emits CollisionEvent
│   │   ├── RenderSystem.cs         ← collects Transform+Sprite; applies camera; draws
│   │   └── AnimationSystem.cs      ← advances Animator component frame counter
│   │
│   ├── Collision/
│   │   ├── CollisionShape.cs       ← abstract base; Intersects(other) → ContactInfo?
│   │   ├── CircleShape.cs          ← circle-circle, circle-polygon tests
│   │   ├── PolygonShape.cs         ← convex polygon; narrow phase via SAT
│   │   ├── CompoundShape.cs        ← multi-part convex body; delegates to parts; LastHitPartIndex
│   │   ├── AABBShape.cs            ← axis-aligned box (fast, used as broad-phase fallback)
│   │   ├── ContactInfo.cs          ← Normal (Vector2), Depth (float), ContactPoint (Vector2)
│   │   ├── SpatialGrid.cs          ← uniform spatial hash; broad-phase bucket lookup
│   │   └── PolygonUtils.cs         ← geometry: GenerateConvex, ClipConvexByHalfPlane, Split,
│   │                                  SplitResult, NearestPointOnBoundary, ComputeArea/Centroid/Inertia
│   │
│   ├── Events/
│   │   ├── EventBus.cs             ← typed pub/sub; deferred dispatch queue; Flush()
│   │   ├── CollisionEvent.cs       ← EntityA, EntityB, ContactInfo
│   │   ├── EntityDestroyedEvent.cs ← Entity, string Tag
│   │   └── SceneChangedEvent.cs    ← Previous, Next scene names
│   │
│   ├── Input/
│   │   └── InputSystem.cs          ← HashSet<Keys>: Held, PressedThisFrame, ReleasedThisFrame
│   │
│   ├── Audio/
│   │   └── AudioSystem.cs          ← NAudio wrapper: Play(id, vol, pan), streaming music
│   │
│   ├── Resources/
│   │   └── ResourceManager.cs      ← lazy load/cache: Get<Bitmap>, Get<Font>, Get<SoundBuffer>
│   │
│   ├── State/
│   │   ├── IGameState.cs           ← Enter(), Exit(), Update(dt), Draw(g), OnInput(key)
│   │   └── StateStack.cs           ← Push/Pop; Draw iterates bottom→top; Update top only
│   │
│   └── Rendering/
│       ├── Camera.cs               ← Position, Zoom; WorldToScreen(Vector2) transform
│       └── GameWindow.cs           ← WinForms Form; double-buffer setup; bridges UI↔game thread
│
├── Game/                           ← Asteroids-specific layer; uses Engine, knows about the game
│   │
│   ├── Components/                 ← game-specific components
│   │   ├── ShipComponent.cs        ← ThrustPower, RotationSpeed, FireCooldown, FireCooldownTimer
│   │   ├── AsteroidComponent.cs    ← Size (Large/Medium/Small), PointValue
│   │   └── BulletComponent.cs      ← Lifetime (float), OwnerEntity
│   │
│   ├── Systems/                    ← game-specific systems
│   │   ├── ShipControlSystem.cs    ← polls InputSystem → applies thrust/rotation to ship
│   │   ├── BulletLifetimeSystem.cs ← decrements Bullet.Lifetime; marks DestroyTag when expired
│   │   ├── AsteroidSplitSystem.cs  ← subscribes to CollisionEvent; splits or destroys asteroids
│   │   ├── CombatSystem.cs         ← subscribes to CollisionEvent; applies damage via Health
│   │   ├── ScreenWrapSystem.cs     ← teleports entities at screen edges (ship, asteroids, bullets)
│   │   └── HUDSystem.cs            ← draws score, lives, level as UI overlay
│   │
│   ├── States/
│   │   ├── PlayingState.cs         ← owns the World; wires all systems; spawns initial entities
│   │   ├── PauseState.cs           ← overlay; world frozen; draws pause menu on top
│   │   └── GameOverState.cs        ← shows score; listens for restart input
│   │
│   ├── Prefabs/
│   │   ├── ShipPrefab.cs           ← creates ship entity with all required components
│   │   ├── AsteroidPrefab.cs       ← creates asteroid entity for a given size + position
│   │   └── BulletPrefab.cs         ← creates bullet entity at ship's nose, inheriting velocity
│   │
│   └── AsteroidsGame.cs            ← entry point: creates GameWindow, StateStack, pushes PlayingState
│
├── Program.cs                      ← Application.Run(new AsteroidsGame())
└── AsteroidsEngine.csproj
```

---

## 2. Engine Components

### Core

| Component | Responsibility |
|---|---|
| `Entity` | Immutable struct: `Id` (int) + `Version` (int). Version increments on recycle, preventing stale-reference bugs. |
| `World` | Stores all components by type + entity ID. Provides typed queries. Buffers deferred create/destroy operations and flushes them at a safe point each frame. |
| `ISystem` | Interface: `Update(World, double dt)`. Systems declare ordering dependencies via attributes. |
| `GameLoop` | Runs on a background thread. Owns the `Stopwatch`. Each frame: snapshot input → flush events → run systems in order → trigger render → sleep remainder. |

### Engine-Level Components (data only, no logic)

| Component | Fields |
|---|---|
| `Transform` | `Vector2 Position`, `float Rotation` |
| `Velocity` | `Vector2 Linear`, `float Angular` |
| `RigidBody` | `float Mass`, `float LinearDrag`, `float AngularDrag` |
| `Collider` | `CollisionShape Shape`, `int Layer`, `int Mask` |
| `Sprite` | `string ImageId`, `int Layer`, `Vector2 Offset`, `Color Tint` |
| `Health` | `int Current`, `int Max` |
| `Tags` | `DestroyTag` (zero-data), `DisabledTag` (zero-data) |

### Engine-Level Systems

| System | Queries | What it does |
|---|---|---|
| `MovementSystem` | Transform + Velocity | `position += velocity * dt`, `rotation += angularVelocity * dt` |
| `PhysicsSystem` | Transform + Velocity + RigidBody | Applies accumulated forces, applies drag, resets forces |
| `CollisionSystem` | Transform + Collider | Broad phase (spatial grid) → narrow phase (SAT/circle) → emit CollisionEvent |
| `RenderSystem` | Transform + Sprite | Sorts by layer, applies camera transform, draws each sprite |
| `AnimationSystem` | Animator component | Advances frame timer, updates Sprite.ImageId to current frame |

---

## 3. Architectural Decisions

### 3.1 ECS Over OOP Inheritance

**Decision:** All game objects are entities (integer IDs). All data lives in components (plain structs). All logic lives in systems (functions that query components).

**Rationale:** OOP inheritance breaks when game design demands arbitrary combinations of features. A ship that is also a camera target, has a health bar, emits particles, and plays footstep sounds cannot cleanly inherit from a single hierarchy. ECS expresses this as one entity with five components — no hierarchy required. It also scales better: adding a new behavior means adding a new component + system, not modifying existing classes.

### 3.2 Event Bus with Deferred Dispatch

**Decision:** Cross-system communication uses a typed publish/subscribe event bus. Events are queued during system updates and dispatched at the end of the frame (after all systems have run).

**Rationale:** Direct references between systems create tight coupling — `CollisionSystem` would need to know about `ScoreSystem`, `AudioSystem`, `ParticleSystem`, etc. The bus inverts this: the collision system publishes a fact; all interested systems react independently. Deferred dispatch prevents the bug where a subscriber destroys an entity mid-iteration of the collision loop.

### 3.3 Manual Game Loop (No WinForms Timers)

**Decision:** The game loop runs on a dedicated background thread using `Thread` + `Stopwatch`. WinForms `Timer` controls are not used.

**Rationale:** WinForms `Timer` fires on the UI thread at ~15ms minimum resolution, can be delayed by UI operations, and cannot maintain consistent frame timing. A background thread with `Stopwatch` measures real elapsed time and gives consistent delta-time values regardless of UI activity.

### 3.4 Polling-Based Input

**Decision:** `InputSystem` maintains three `HashSet<Keys>`: `Held`, `PressedThisFrame`, `ReleasedThisFrame`. Game systems poll these each frame. `PressedThisFrame` and `ReleasedThisFrame` are cleared at the end of each frame.

**Rationale:** WinForms `KeyDown` fires once on press and then at OS repeat rate (~500ms delay, then ~30ms repeat). This is unusable for continuous movement. Polling a `Held` set gives smooth, frame-accurate input for held keys, while `PressedThisFrame` gives clean single-frame detection for actions like firing.

### 3.5 SAT + Spatial Grid for Collision

**Decision:** Broad phase uses a uniform spatial grid (spatial hashing). Narrow phase uses the Separating Axis Theorem for convex polygons and direct math for circles. The narrow phase returns a full `ContactInfo` (normal, depth, contact point), not just a boolean.

**Rationale:** O(n²) brute-force collision is fine for Asteroids but breaks at scale. A spatial grid reduces average collision checks to O(n). SAT handles arbitrary convex polygons and produces the contact manifold needed for physically correct collision response (not just overlap detection). The `ContactInfo` output is essential for sound panning, particle spawn position, and future physics response.

### 3.6 State Stack for Game States

**Decision:** Game states (`IGameState`) are pushed/popped on a stack. `Draw` renders all states bottom-to-top; `Update` runs only the top state.

**Rationale:** A flat enum+switch cannot express overlay states. Pausing a game should suspend logic but keep the world visible behind the pause menu. The stack models this naturally: `PauseState` sits above `PlayingState`; popping it resumes play without rebuilding the world.

### 3.7 Resource Manager for All Assets

**Decision:** All assets (images, sounds, fonts) are loaded and cached by a central `ResourceManager`. No code outside `ResourceManager` calls file I/O or allocates GDI+ resources.

**Rationale:** GDI+ objects (`Bitmap`, `Pen`, `Font`) are OS-handle-backed and expensive to allocate. Creating them per-frame or per-entity causes measurable performance degradation and handle leaks. The resource manager ensures each asset is loaded once, reused everywhere, and properly disposed on shutdown.

### 3.8 NAudio for Sound

**Decision:** Audio is handled via NAudio (NuGet package, MIT license).

**Rationale:** `System.Media.SoundPlayer` only plays one sound at a time synchronously. Asteroids needs overlapping explosion sounds, bullet fire, and background music simultaneously. NAudio supports mixing, volume/pitch/pan per channel, and streaming for music.

### 3.9 Separation of Engine and Game Layers

**Decision:** The `Engine/` directory contains no Asteroids-specific code. The `Game/` directory contains no engine implementation details. They communicate only through the engine's public API (World, EventBus, ISystem, IGameState).

**Rationale:** This enforces that the engine is genuinely reusable — if the engine layer compiles without `Game/`, it is general-purpose. It also makes each layer independently testable.

### 3.10 Component Storage: Sparse Sets

**Decision:** Each component type is stored in a sparse set: a `sparse[]` array of size MAX_ENTITIES (maps entity ID → dense index, or -1 if absent) plus a packed `dense[]` array of entity IDs and a parallel `data[]` array of component values. Both `dense` and `data` contain only entities that actually have the component — no gaps, no sentinel values.

**Rationale:** Sparse sets give O(1) lookup by entity ID (two array reads: `sparse[id]` → `data[dense_idx]`), O(n) cache-friendly iteration (sequential scan of packed `data[]`), and O(1) add/remove. No memory is wasted on absent components — unlike fixed arrays where every entity reserves a slot for every component type. Archetype tables were considered but rejected: they are faster for iteration but make add/remove O(k) (must copy all k existing components to the new archetype table), which matters for Asteroids where bullets and asteroids are created and destroyed constantly.

**Future extension:** Archetype storage can be added as an alternative `ISparseSet` implementation later if profiling shows a bottleneck.

### 3.11 Variable Timestep

**Decision:** The game loop passes real elapsed delta time (`dt` in seconds, measured by `Stopwatch`) to all systems each frame. No fixed accumulator is used.

**Rationale:** Sufficient for Asteroids — no stiff constraints or stacking physics where variable dt causes instability. Simpler to implement and reason about. Fixed timestep will be added as a `GameLoop` configuration option in a future phase.

### 3.12 Full Camera

**Decision:** The `Camera` class holds `Position` (Vector2), `Zoom` (float), and `Rotation` (float). `RenderSystem` applies a camera transform to all world-space coordinates before drawing. Defaults to identity (position=0, zoom=1, rotation=0).

**Rationale:** Building the full camera now costs little and enables screen shake (temporary position offset), zoom-to-action, and following the player without architectural changes later.

### 3.13 Symplectic Euler Integration

**Decision:** `PhysicsSystem` updates velocity before position each frame:
```
velocity += acceleration * dt   // velocity updated first
position += velocity * dt       // then position uses the NEW velocity
```
Verlet integration is deferred as a future extension.

**Rationale:** Symplectic Euler is energy-stable (unlike standard Euler which drifts energy upward over time) at identical computational cost. The update-velocity-first order is the only difference from naive Euler. Verlet integration offers no meaningful advantage for Asteroids-style physics and adds complexity.

### 3.14 Double-Buffered Bitmap for Rendering Thread

**Decision:** The game loop runs on a background thread and renders to an off-screen `Bitmap` (back buffer). After each frame, it atomically swaps the back buffer with a front buffer via `Interlocked.Exchange`. The WinForms `OnPaint` handler reads the front buffer and blits it to the form surface. The two threads never block each other.

**Rationale:** Option A (single bitmap, game thread draws, UI thread blits) causes tearing when the game thread writes to the bitmap while the UI thread is reading it. Option B (game thread invokes on UI thread) blocks the UI thread during draw, causing jank. Option C (atomic swap) decouples frame rates: the UI can repaint at its own cadence while the game thread runs at full speed.

### 3.15 System.Numerics.Vector2

**Decision:** All 2D vector math uses `System.Numerics.Vector2` (the built-in .NET struct). No custom vector type will be written.

**Rationale:** `System.Numerics.Vector2` is a `readonly struct` with SIMD acceleration via the .NET JIT. It provides all needed operations: `+`, `-`, `*`, `/`, `Length()`, `LengthSquared()`, `Normalize()`, `Dot()`, `Distance()`, `Lerp()`, `Reflect()`, `Transform()`. Writing a custom type would duplicate this work with no benefit.

### 3.16 Convex-Only Polygon Shapes

**Decision:** `PolygonShape` supports only convex polygons. SAT is the narrow-phase algorithm. Concave shapes are out of scope for now.

**Rationale:** All shapes in Asteroids are convex. A `CompoundShape` type (a list of convex sub-shapes tested as a unit) will be added as a future extension when concave shapes are needed.

### 3.17 Uniform Spatial Grid for Broad-Phase Collision

**Decision:** The broad-phase uses a uniform spatial grid. The world is divided into cells of fixed size (≈ 1.5× the diameter of the largest commonly-spawned entity). Each entity is registered in every cell its AABB overlaps. Collision candidates are entities sharing at least one cell. The grid is fully rebuilt each frame. The grid is accessed through an `ISpatialIndex` interface.

**Rationale:** Simpler and faster to implement than a quadtree. O(1) insert and lookup. Performs as well as or better than a quadtree for scenes where entity sizes are roughly uniform (all Asteroids entities are within a ~4× size range). The `ISpatialIndex` interface ensures the implementation can be swapped for a quadtree without touching any caller.

---

## 4. Future Extensions

All items below were considered and deliberately deferred. Each has a designated extension point in the architecture so it can be added without restructuring existing code.

### 4.1 Fixed Timestep

**What:** Accumulate elapsed time and run the physics/update step in fixed increments (e.g. 16ms), decoupled from the render rate. Render with interpolation between the last two physics states.

**Why deferred:** Variable timestep is sufficient for Asteroids-style physics. Fixed timestep is a `GameLoop` configuration change and does not affect any system interface.

**Extension point:** `GameLoop` — add a `FixedTimestep` mode alongside the existing variable mode.

---

### 4.2 Verlet Integration

**What:** Replace symplectic Euler with Verlet integration in `PhysicsSystem`. Verlet uses the previous position (`pos_prev`) rather than explicit velocity, giving better energy conservation for oscillating/spring systems.

**Why deferred:** Symplectic Euler is energy-stable for the physics in this engine. Verlet only matters for constrained or spring-based simulations.

**Extension point:** `PhysicsSystem` — swap the integrator internally. No component or system interface changes needed (though a `PreviousPosition` component would be added).

---

### 4.3 Archetype Component Storage

**What:** Replace the per-component sparse sets with archetype tables: entities are grouped by their exact component combination, with all component arrays for that group stored contiguously. Gives better iteration performance for large entity counts with stable component compositions.

**Why deferred:** Sparse sets are O(1) add/remove, which matters for a game where entities are created and destroyed constantly. Archetype tables make add/remove O(k). Measurable benefit only at entity counts beyond what Asteroids needs.

**Extension point:** `World` — the public API (`AddComponent`, `GetComponent`, `Query`) is already designed as an abstraction over storage. Swapping the internal storage strategy requires no changes to any system or game code.

---

### 4.4 Quadtree Broad-Phase

**What:** Replace the uniform spatial grid with a quadtree that adaptively subdivides space. Better handles scenes with large size variation between entities (e.g. a tiny bullet vs a room-filling boss).

**Why deferred:** The uniform grid is sufficient and simpler for scenes with roughly uniform entity sizes.

**Extension point:** `ISpatialIndex` — implement `QuadTree : ISpatialIndex` and pass it to the `CollisionSystem` constructor. Zero changes to any other code.

---

### 4.5 CompoundShape for Concave Colliders

**What:** A `CompoundShape` type that holds a list of convex sub-shapes (circles and/or convex polygons) and runs collision tests against all parts. Enables concave objects without implementing a full concave decomposition algorithm.

**Why deferred:** All shapes in Asteroids are convex. SAT already handles all needed cases.

**Extension point:** `CollisionShape` hierarchy — `CompoundShape : CollisionShape` added alongside the existing `CircleShape`, `PolygonShape`, `AABBShape`.

---

### 4.6 Wave-Based Parallel System Scheduling

**What:** Analyse declared read/write component accesses across all systems, build a dependency DAG, group systems into waves of non-conflicting systems, and run each wave in parallel using the .NET thread pool.

**Why deferred:** Requires each system to declare component access metadata, adds scheduling infrastructure, and only pays off with many systems and many entities. Single-threaded system execution with `Parallel.ForEach` within hot systems covers the near-term need.

**Extension point:** `GameLoop` — the system registration and execution order is already managed there. Wave scheduling replaces the linear `foreach (system) system.Update()` loop.

---

### 4.7 Double-Buffered Component Stores for Full Frame Parallelism

**What:** Give every component type two backing arrays (ping/pong). All systems read from the previous frame's buffer and write to the current frame's buffer. No read/write hazards exist between any two systems — they can all run fully in parallel. Swap buffers at end of frame.

**Why deferred:** Introduces one frame of latency for all state changes. Requires the wave scheduler (4.6) to be in place first to be worthwhile. Significant implementation complexity.

**Extension point:** `World` — the double-buffer swap is an internal storage detail behind the same component access API.

---

## Roadmap (Rough)

```
Phase 1 — Engine Core
  ├── Entity + World (ID allocation, component storage, basic queries)
  ├── GameLoop (background thread, Stopwatch, dt)
  └── InputSystem (key state sets, WinForms wiring)

Phase 2 — Rendering
  ├── GameWindow (WinForms form, double buffer, OnPaint → blit)
  ├── RenderSystem (collect + sort drawables, apply camera, draw)
  └── Camera (identity transform; position + zoom)

Phase 3 — Physics & Collision
  ├── Transform + Velocity + RigidBody components
  ├── MovementSystem + PhysicsSystem
  ├── CollisionShape hierarchy (Circle, AABB, Polygon)
  ├── SAT narrow phase + ContactInfo
  └── SpatialGrid broad phase

Phase 4 — Event Bus + State
  ├── EventBus (typed subscribe/publish, deferred queue, flush)
  ├── IGameState + StateStack
  └── ResourceManager (images, fonts)

Phase 5 — Asteroids Game
  ├── Game components (ShipComponent, AsteroidComponent, BulletComponent)
  ├── Prefabs (Ship, Asteroid, Bullet)
  ├── Game systems (ShipControl, BulletLifetime, AsteroidSplit, ScreenWrap, Combat)
  ├── HUD (score, lives)
  └── States (Playing, Paused, GameOver)

Phase 6 — Audio + Polish
  ├── AudioSystem (NAudio, channels, music)
  ├── Screen shake (camera offset)
  ├── Particle effects (simple engine-level emitter)
  └── Performance review (profile, optimize if needed)
```

---

## 5. Intra-System Parallelism

> **Status: decided, to be implemented.** This is not a future extension — it is a planned feature for the current engine. It is distinct from the inter-system wave scheduler described in section 4.6, which remains a future extension.

### 5.1 What it means

*Intra-system* parallelism runs iterations of a single system's `ForEach` loop concurrently across hardware threads. A system like `PhysicsSystem` iterates N entities and integrates each one's velocity independently — no entity reads another entity's data. This is a textbook data-parallel workload.

*Inter-system* parallelism (section 4.6) is different: it runs multiple distinct systems concurrently. That requires declaring read/write sets per system and building a DAG. Much more complex, deferred.

### 5.2 Why it is safe

ECS sparse sets store component data in packed dense arrays (`T[] data`). Entity i's data lives at `data[i]`. Two threads writing to `data[i]` and `data[j]` where i ≠ j never touch the same memory location. .NET's memory model guarantees this is safe — no data race.

The only hazard is **false sharing**: when two logically-independent elements share a physical cache line (64 bytes), competing writes from different threads cause cache-line ping-pong between CPU cores. For Asteroids-scale entity counts (300–500 peak), false sharing overhead is negligible compared to the parallelism gain. At 10,000+ entities with very small component types (e.g., 8-byte structs), padding or chunking would be considered.

### 5.3 Safety conditions for a system to use ForEachParallel

1. **Per-entity isolation**: each iteration reads and writes only the components of the entity it was given. It does not read another entity's component via a direct handle.
2. **No entity creation or destruction inside the body**: all create/destroy goes through `world.DestroyEntity` (deferred queue), which is not thread-safe to call from parallel bodies. Collect entities to destroy in a `ConcurrentBag<Entity>`, then destroy sequentially after the parallel loop.
3. **No unguarded shared mutable state**: if the body accumulates a result (e.g., total count), use `Interlocked` operations or thread-local accumulators and reduce afterward.

### 5.4 API design

Two overloads are added to `World`:

```
World.ForEachParallel<T>(EcsAction<T> body)
World.ForEachParallel<T1, T2>(EcsAction<T1, T2> body)
```

The body signature is **identical** to the existing sequential `ForEach` — callers do not need to know they are being parallelised. The implementation difference is internal:

```
// Sequential (existing)
for i in 0..count:
    body(entities[i], ref data[i])

// Parallel (new)
Parallel.For(0, count, i =>
    body(entities[i], ref data[i]))
```

`Parallel.For` with a lambda that captures `ref data[i]` is valid C# — each invocation creates a fresh ref pointing to a distinct array slot, and no ref escapes the invocation.

For multi-component queries (`ForEachParallel<T1, T2>`), the implementation iterates the smaller of the two dense sets and performs a sparse lookup for the second component on each entity. The sparse lookup is two array reads (O(1)) and safe to call from parallel context since no write path is involved.

### 5.5 Which systems use ForEachParallel

| System | Parallel? | Reason |
|---|---|---|
| `PhysicsSystem` | ✓ | Per-entity force/drag integration; no cross-entity reads |
| `MovementSystem` | ✓ | Per-entity position integration; no cross-entity reads |
| `WrapSystem` | ✓ | Per-entity boundary check; no cross-entity reads |
| `TimeToLiveSystem` | ✓ (with care) | TTL decrement is per-entity; collect expired in `ConcurrentBag<Entity>`, destroy after loop |
| `CollisionSystem` — broad phase | ✗ | Spatial grid insertion is shared mutable state |
| `CollisionSystem` — narrow phase | ✗ (for now) | Candidate pair list is shared; future: parallel pair processing with result aggregation |
| `AsteroidFragmentationSystem` | ✗ | Entity spawn/destroy; event bus publish |
| `PolygonRenderSystem` | ✗ | SkiaSharp `SKCanvas` is single-threaded |
| `CameraFollowSystem` | ✗ | Single-entity read into camera; no iteration |
| `WaveManagerSystem` | ✗ | Single-frame state update; no heavy iteration |

### 5.6 Performance expectations

At 300–500 entities on a 4-core machine, `PhysicsSystem` and `MovementSystem` see roughly **2–3× speedup** in practice (thread pool overhead + cache effects reduce the theoretical 4× maximum). The main bottleneck is expected to be the `CollisionSystem` narrow phase (polygon SAT is O(N vertices) per pair), which is not parallelised in the initial implementation but is the primary candidate for a future optimisation pass.

---

### 5.7 System Computation Model Taxonomy

Before designing abstractions, we classify every system by its *computation model* — the pattern that determines what parallelism strategy applies. This taxonomy is stable; new systems slot into an existing model or introduce a new one, and the corresponding scheduling machinery is already accounted for.

#### Model 1 — Independent per-entity transform

**Pattern:** Iterate over all entities with a fixed component set; each iteration reads and writes only its own components. No global state touched, no events emitted.

**Parallelism:** Embarrassingly parallel — `ForEachParallel` with no additional machinery.

**Examples:** `MovementSystem`, `PhysicsSystem` (force integration only), `WrapSystem`, `BallSpawnSystem` (per-entity radius reset).

---

#### Model 2 — Aggregating iteration

**Pattern:** Iterate over entities with a component set; each iteration *also* writes a shared accumulator (count, sum, extremum).

**Parallelism:** Parallel bodies use `Interlocked` operations or thread-local accumulators that are reduced after the loop. The sequential path needs no change.

**Examples:** `CircleDrawSystem` (counts drawn entities for HUD), any debug overlay counting entities.

---

#### Model 3 — Independent iteration with event publication

**Pattern:** Each iteration reads its own entity's components, decides something, and optionally enqueues an event. No cross-entity reads; no shared mutable state beyond the event queue.

**Parallelism:** The body is per-entity isolated, but `EventBus.Publish<T>` touches `_queue`. Swapping `Queue<>` to `ConcurrentQueue<>` inside `EventBus` is the only change required — no API surface changes. `Flush()` is always called sequentially after the parallel loop.

**Thread-safety note:** `EventBus._queue` is currently `Queue<(Type, object)>` (not thread-safe). Replacing it with `ConcurrentQueue<(Type, object)>` is a single-line change with no observable behaviour difference in the sequential path.

**Examples:** `TimeToLiveSystem` (emits `EntityExpiredEvent`), `PlayerInputSystem` (emits `ShootEvent`), `HealthSystem` (emits `EntityDiedEvent`).

---

#### Model 4 — Event-consuming iteration

**Pattern:** The system reacts to events dispatched by `EventBus.Flush()`. Event handlers may touch *any* entity involved in the event — there is no guarantee that different events name disjoint entities.

**Parallelism:** Not directly parallelisable without ownership tracking. Handlers are always run sequentially by `Flush()`. Future option: sort events by the entity pair they name and bin-pack non-overlapping groups into parallel tasks — but this complexity is deferred indefinitely.

**Examples:** `AsteroidFragmentationSystem` (handles `BulletHitEvent`), `CollisionResponseSystem` (handles `CollisionEvent`), `DeathSystem` (handles `EntityDiedEvent`).

---

#### Model 5 — Singleton-like (no entity iteration)

**Pattern:** The system reads or writes a single piece of global state — typically an input device, camera, audio context, or wave counter — and does not iterate over entities at all (or iterates once to find a unique entity).

**Parallelism:** Not applicable; these systems are always sequential and fast. They run before or after the parallel wave.

**Examples:** `InputSystem`, `CameraFollowSystem`, `WaveManagerSystem`, `AudioSystem`.

---

#### Model 6 — Cross-entity read

**Pattern:** Each iteration reads the components of its *own* entity but also performs a look-up into another entity's components (e.g., "find the player and read its position"). The looked-up entity is *not written*.

**Parallelism:** Safe to parallelise as long as the looked-up entity's component is not written during the same parallel wave. In practice, cache the looked-up value once before `ForEachParallel` and capture it as a local in the lambda.

**Examples:** `EnemyAISystem` (reads player `Transform` on every iteration), `CameraFollowSystem` (degenerate case: reads one entity, writes camera — effectively Model 5).

---

#### Model 7 — Custom parallel (CollisionSystem)

Collision detection is the most compute-intensive system and does not fit any of the above models. It requires a bespoke four-stage pipeline; see §5.9.

---

### 5.8 ISystemMetadata — Declarative Parallelism Hints

Each system may optionally implement `ISystemMetadata` to declare its computation model and component access pattern. This interface is *advisory* — the engine does not enforce it at runtime — but it enables future tooling (a parallel wave scheduler, a debug overlay, automated tests) to reason about systems without inspecting their source.

```csharp
// Engine/Core/ISystemMetadata.cs

/// <summary>
/// Optional interface for systems that want to declare their computation model
/// and component access pattern for future parallel scheduling.
/// </summary>
public interface ISystemMetadata
{
    /// <summary>
    /// Primary computation model. Determines which parallelism strategy applies.
    /// </summary>
    ComputationModel Model { get; }

    /// <summary>
    /// Declares which component types this system reads or writes.
    /// Used by a future wave scheduler to detect write-write and write-read conflicts.
    /// Null means "unknown / opt out of scheduling".
    /// </summary>
    ComponentAccess[]? DeclaredAccess { get; }
}

public enum ComputationModel
{
    IndependentTransform   = 1,   // Model 1
    Aggregating            = 2,   // Model 2
    EventPublishing        = 3,   // Model 3
    EventConsuming         = 4,   // Model 4
    Singleton              = 5,   // Model 5
    CrossEntityRead        = 6,   // Model 6
    CustomParallel         = 7,   // Model 7
}

public readonly struct ComponentAccess
{
    public Type ComponentType { get; init; }
    public AccessMode Mode     { get; init; }

    public static ComponentAccess Read<T>()  => new() { ComponentType = typeof(T), Mode = AccessMode.Read  };
    public static ComponentAccess Write<T>() => new() { ComponentType = typeof(T), Mode = AccessMode.Write };
}

public enum AccessMode { Read, Write }
```

**Usage example** (a system that wants to self-describe):

```csharp
public sealed class MovementSystem : ISystem, ISystemMetadata
{
    public ComputationModel  Model          => ComputationModel.IndependentTransform;
    public ComponentAccess[]? DeclaredAccess => [
        ComponentAccess.Read<Velocity>(),
        ComponentAccess.Write<Transform>(),
    ];

    public void Update(World world, double dt) { /* ... */ }
}
```

Systems that do *not* implement `ISystemMetadata` are treated as opaque by any future scheduler and will be placed in their own sequential slot.

**Design note:** `ISystemMetadata` is intentionally passive. It carries no `Update` variant; it does not change how `World.ForEach` works. It is a pure annotation layer. When a parallel wave scheduler is eventually built, it queries `ISystemMetadata` on each registered system, builds a conflict graph, and groups non-conflicting Model-1/2/3/6 systems into a `Parallel.Invoke` call.

---

### 5.9 CollisionSystem Custom Parallel Strategy (Model 7)

The `CollisionSystem` is a four-stage pipeline. Each stage has different parallelism characteristics.

#### Stage 1 — Spatial grid rebuild (sequential)

Insert every entity with a `Transform` + collision shape into a uniform spatial hash grid. Cells are sized to 2× the largest entity radius.

**Why sequential:** Grid is shared mutable state (dictionary of cell → entity list). Lock-free concurrent insertion into a `ConcurrentDictionary<CellKey, ConcurrentBag<Entity>>` is possible but the overhead is unlikely to be worthwhile — entity count is small and this stage is O(N) with tiny per-entity cost.

**Future option:** Build N per-thread partial grids and merge — only worthwhile at >10,000 entities.

#### Stage 2 — Candidate pair generation (parallelisable per cell)

For each occupied cell, enumerate all entity pairs within the cell (and between the cell and its 8 neighbours to avoid duplicate enumeration with a canonical pair ordering: `entityA.Id < entityB.Id`).

**Parallelism:** Cells are independent. `Parallel.ForEach` over the cell list; each thread appends to a thread-local `List<(Entity, Entity)>` and a final sequential reduce concatenates the partial lists. Duplicate-pair filtering by canonical ordering is stateless and safe in parallel.

**Result:** A deduplicated flat `List<(Entity A, Entity B)>` of candidate pairs.

#### Stage 3 — SAT narrow phase (embarrassingly parallel)

For each candidate pair, run the full SAT test (polygon-polygon or circle-polygon). Each pair is independent of every other. Pairs that pass the test are written into a thread-local `List<CollisionEvent>` and reduced into a single flat list after the loop.

```
Parallel.For(0, pairs.Count, () => new List<CollisionEvent>(),
    (i, _, localList) => {
        var (a, b) = pairs[i];
        if (SATOverlap(a, b, out var contact))
            localList.Add(new CollisionEvent(a, b, contact));
        return localList;
    },
    localList => lock(results) results.AddRange(localList));
```

This is the most expensive stage (O(N_pairs × N_vertices)) and benefits most from parallelism.

#### Stage 4 — Event dispatch (sequential)

Call `EventBus.Publish<CollisionEvent>` for each result. This is sequential by design — `Flush()` drives subscriber callbacks, which may modify entity state. Keeping dispatch sequential avoids the need for ownership tracking in Model-4 subscribers.

#### Stage summary

| Stage | Strategy | Parallel? |
|---|---|---|
| Grid rebuild | Sequential insert | ✗ |
| Candidate generation | Per-cell parallel, thread-local lists | ✓ |
| SAT narrow phase | Per-pair parallel, thread-local lists | ✓ |
| Event dispatch | Sequential `Publish` + `Flush` | ✗ |

#### Activation plan

Stages 1–4 are implemented sequentially first. Stages 2 and 3 are parallelised in a later optimisation pass when profiling confirms they are the bottleneck. No API changes to `World` or `EventBus` are needed to activate the parallel path — it is a pure implementation detail of `CollisionSystem.Update`.

---

### 5.10 System–Model Mapping (current and planned)

| System | Model | Parallel-ready? | Notes |
|---|---|---|---|
| `PhysicsSystem` | 1 — Independent | ✓ | Drop-in `ForEachParallel` |
| `MovementSystem` | 1 — Independent | ✓ | Drop-in `ForEachParallel` |
| `WrapSystem` | 1 — Independent | ✓ | Drop-in `ForEachParallel` |
| `TimeToLiveSystem` | 3 — Event-publishing | ✓ (after ConcurrentQueue) | Collect expired in `ConcurrentBag`, destroy after |
| `PlayerInputSystem` | 3 — Event-publishing | ✓ (after ConcurrentQueue) | Single-player: effectively Model 5 in practice |
| `ShootSystem` | 4 — Event-consuming | ✗ | Handles `ShootEvent`; spawns bullet entities |
| `AsteroidFragmentationSystem` | 4 — Event-consuming | ✗ | Handles `BulletHitEvent`; polygon split + spawn |
| `CollisionSystem` | 7 — Custom parallel | see §5.9 | Four-stage pipeline |
| `EnemyAISystem` | 6 — Cross-entity read | ✓ (cache player pos) | Reads player `Transform` once before loop |
| `WaveManagerSystem` | 5 — Singleton | ✗ | Wave counter, spawn scheduling |
| `InputSystem` | 5 — Singleton | ✗ | SDL event poll → `InputState` |
| `CameraFollowSystem` | 5 — Singleton | ✗ | Reads one entity, writes camera |
| `PolygonRenderSystem` | 5 — Singleton | ✗ | `SKCanvas` is single-threaded |
| `HealthBarRenderSystem` | 5 — Singleton | ✗ | HUD overlay; screen-space only |

---

## 6. Asteroids on Steroids

### 6.1 Game Overview

A single-player arcade game. The player pilots a ship in a large toroidal world (wraps at all edges). Waves of asteroids and enemy ships spawn progressively. Destroying asteroids and enemies scores points. Score + wave reached are saved to a local leaderboard.

| Parameter | Value |
|---|---|
| World size | 10× screen (e.g. 12800 × 7200 for a 1280 × 720 viewport) |
| World topology | Toroidal (all entities wrap at edges) |
| Camera | Follows player ship; smooth lag |
| Waves | Infinite progression; each wave harder than the last |
| Enemies | Enemy ships from wave 3 onward |
| Core mechanic | Physics-based polygon fragmentation |

### 6.2 Destruction Model — Physics-Based Polygon Fracturing

Each asteroid starts as one convex polygon. On bullet impact, `PolygonUtils.Split` applies a structured series of Sutherland-Hodgman clips to produce fragments, debris particles, and an updated surviving asteroid entity. The model has three zones:

- **Fracture zone** — the near-impact half of the asteroid, defined by the first perpendicular cut. Contains all subsequent geometry operations.
- **Impact zone** — the innermost remnant of the fracture zone after radial secondary cuts. Gets the final inner fragmentation.
- **Blast zone** — innermost impact zone fragments (centroid ≤ `blastRadius` from impact) → visual particles only.

**Key properties:**
- Zone radii are derived from absolute energy and material properties, not normalised by asteroid size. The same bullet energy produces the same absolute zone on any asteroid; large asteroids are harder to shatter because they have higher threshold energy.
- Sub-threshold hits accumulate damage (`FractureState.AbsorbedEnergy`). Repeated small impacts eventually fracture the target.
- The surviving entity is updated **in-place** — no destroy/create. Its collision shape becomes a `CompoundShape` of the surviving convex pieces, which appears visually concave.
- Every polygon produced by SH clipping is convex by invariant — `CompoundShape` is its own convex decomposition.

Full algorithm detail: see `docs/physics_spec.md §6`.

### 6.3 Engine Extensions — Status

| Extension | Status | Notes |
|---|---|---|
| `RigidBody.Inertia` + `AccumulatedTorque` | ✅ Done | Torque integrated in `PhysicsSystem` |
| `PhysicsSystem.ApplyForceAtPoint` | ✅ Done | Accumulates force + cross-product torque |
| `PolygonUtils` — `GenerateConvex`, `ClipConvexByHalfPlane`, `RecenterVertices`, `ComputeArea/Centroid/Inertia` | ✅ Done | Engine/Collision/PolygonUtils.cs |
| `PolygonUtils.Split` — 3-phase fracture algorithm | ✅ Done (partial) | Primary cut + inner cuts implemented; secondary radial cuts pending |
| `PolygonUtils.NearestPointOnBoundary` | ✅ Done | Impact point clamped to polygon surface |
| `CompoundShape` | ✅ Done | Full double-dispatch; `LastHitPartIndex`; `WithoutPart` |
| `FractureProperties` / `FractureState` / `VisualMesh` components | ✅ Done | Engine/Components/ |
| `World.ForEachParallel` | ✅ Done | Three overloads (T, T1+T2, T1+T2+T3) |
| `EventBus` — `ConcurrentQueue` | ✅ Done | Safe for parallel event publish |
| Full rotational collision impulse in `CollisionSystem` | ⏳ Planned | Requires per-collision contact point + inertia lookup; see `physics_spec.md §1.4` |
| Angular impulse from bullet hits | ⏳ Planned | r × J / I applied to survivor + fragments |
| Secondary radial cuts in `Split` | ⏳ Planned | `physics_spec.md §6.4` |
| `VisualMesh` rendering pass | ⏳ Planned | Renderer queries VisualMesh before Collider.Shape |
| Sub-threshold visual scarring | ⏳ Planned | Carve blast zone from VisualMesh on absorbed hits |

`Camera.WorldToScreen` and `Camera.ScreenToWorld` are used for mouse picking and screen-space HUD elements — these already work correctly.

### 6.4 Game-Layer Components

| Component | Fields | Usage |
|---|---|---|
| `AsteroidData` | `int Generation`, `float[] FaultAngles` | `Generation` prevents infinite splitting (cap = 3). `FaultAngles`: 2–3 pre-scored angles that bias cut direction toward natural-looking fracture lines. |
| `ShipData` | `float ThrustPower`, `float RotSpeed`, `float FireCooldown`, `float FireCooldownTimer` | Player ship parameters |
| `BulletData` | `float Lifetime`, `int OwnerTag` | `OwnerTag` prevents a player's bullet hitting their own ship |
| `EnemyData` | `float AggroRange`, `float FireRange`, `float FireRate`, `float FireCooldownTimer`, `EnemyState State` | Enemy AI state machine parameters |
| `PolygonVisual` | `SKColor Fill`, `SKColor Outline`, `float OutlineWidth` | Polygon rendering data; distinct from `CircleVisual` |
| `DebrisTag` | zero-data marker | Marks short-lived particle debris (no collision, TTL only) |

`EnemyState` is a C# enum: `Idle`, `Chase`, `Attack`, `Evade`.

**Engine component additions:**

| Component | Added Fields |
|---|---|
| `RigidBody` | `float Inertia`, `float AccumulatedTorque` |

### 6.5 Systems

| System | Parallel | Description |
|---|---|---|
| `ShipControlSystem` | ✗ | Reads input → `PhysicsSystem.ApplyForce` on ship, manage `FireCooldownTimer`, spawn bullets |
| `AsteroidSpawnSystem` | ✗ | Spawns wave asteroids: calls `PolygonUtils.GenerateConvex`, assigns random velocity + angular velocity |
| `AsteroidFragmentationSystem` | ✗ | Subscribes to `CollisionEvent`; runs the full fragmentation pipeline (see 6.7) |
| `EnemyAISystem` | ✗ | State machine per enemy: steers toward/away from player via `ApplyForce`; fires at `FireRate` |
| `WaveManagerSystem` | ✗ | Counts alive `WaveTag` entities each frame; when 0, starts countdown to next wave spawn |
| `CameraFollowSystem` | ✗ | Smoothly moves `camera.Position` toward player ship's `Transform.Position` each frame |
| `WrapSystem` | ✓ | Toroidal edge wrap for all entities with `Transform` + `Velocity`; replaces `WallBounceSystem` |
| `PhysicsSystem` | ✓ | Force/drag integration (existing; gains torque support) |
| `MovementSystem` | ✓ | Position/rotation integration (existing; no change needed) |
| `CollisionSystem` | ✗ | Existing system; unchanged |
| `EventFlushSystem` | ✗ | Existing; flushes `CollisionEvent` to `AsteroidFragmentationSystem` and `CombatSystem` |
| `TimeToLiveSystem` | ✓ | Existing; handles bullets, debris |
| `PolygonRenderSystem` | ✗ | Renders `PolygonVisual` entities; applies camera transform; also renders `CircleVisual` and HUD |

### 6.6 Directory Structure Addition

```
Demos/
├── TwoPlayerGame/          ← existing two-player sandbox (SDL2 + SkiaSharp)
│   ├── Program.cs
│   └── SdlGameWindow.cs    ← may be promoted to a shared layer later
│
└── AsteroidsGame/          ← new single-player game
    ├── AsteroidsGame.csproj
    ├── Program.cs           ← top-level: window, input, state machine, render loop
    │
    ├── Components/
    │   ├── AsteroidData.cs
    │   ├── ShipData.cs
    │   ├── BulletData.cs
    │   ├── EnemyData.cs
    │   └── PolygonVisual.cs
    │
    ├── Systems/
    │   ├── ShipControlSystem.cs
    │   ├── AsteroidSpawnSystem.cs
    │   ├── AsteroidFragmentationSystem.cs
    │   ├── EnemyAISystem.cs
    │   ├── WaveManagerSystem.cs
    │   ├── WrapSystem.cs
    │   ├── CameraFollowSystem.cs
    │   └── PolygonRenderSystem.cs
    │
    ├── Utils/
    │   └── Leaderboard.cs   ← read/write JSON; top-10 by score
    │
    └── States/
        ├── MenuState.cs
        ├── PlayingState.cs  ← owns World, EventBus, systems array, Camera instance
        ├── PauseState.cs
        └── GameOverState.cs
```

**Engine addition:**
```
Engine/Collision/
└── PolygonUtils.cs          ← GenerateConvex, ClipConvexByHalfPlane, Split, ApplyCuts,
                               ComputeCentroid, ComputeArea, ComputeInertia
```

### 6.7 Fragmentation Pipeline (detailed)

This is the most complex game-side algorithm. It runs inside `AsteroidFragmentationSystem` in response to a `CollisionEvent` where one entity has `BulletData` and the other has `AsteroidData`.

#### Stage 1 — Impact assessment

```
impactForce = |Dot(bulletVel − asteroidVel, contact.Normal)| × bulletMass

if impactForce < ChipThreshold:        // glancing hit
    asteroid.Health.Current -= ScaleDamage(impactForce)
    DestroyEntity(bullet)
    return

if asteroid.Generation >= MaxGeneration:  // rubble — no further splitting
    asteroid.Health.Current -= ScaleDamage(impactForce)
    DestroyEntity(bullet)
    return

numCuts = impactForce < MidThreshold ? 1
        : impactForce < HighThreshold ? 2
        : 3
```

#### Stage 2 — Cut plane selection

```
baseAngle = Atan2(contact.Normal) + π/2   // perpendicular to impact direction

for i in 0..numCuts:
    rawAngle  = baseAngle + i × 65°        // spread cuts apart
    fault     = faultAngles.MinBy(f => AngleDiff(f, rawAngle))
    cutAngle  = Lerp(rawAngle, fault, 0.4) // 40% pulled toward nearest fault line

    t        = (i == 0) ? 0.25f : 0.5f    // first cut: near impact; rest: near centroid
    cutPoint = Lerp(contact.Contact, asteroid.Position, t)
             + RandomOffset(asteroidRadius × 0.15f)

    cuts[i] = (cutPoint, AngleToVector(cutAngle))
```

The `faultAngles` bias ensures splits tend to follow "natural" fracture directions set at asteroid generation, giving a less random and more geologically plausible look.

#### Stage 3 — Polygon clipping

```
// Bring asteroid polygon into world space before cutting
worldVerts = localVerts.Select(v => Rotate(v, asteroid.Rotation) + asteroid.Position)

pieces = PolygonUtils.ApplyCuts(worldVerts, cuts)
// ApplyCuts: for each cut, splits every existing piece; collects only valid pieces (≥3 vertices)
```

Result: 2 pieces (1 cut), 3–4 pieces (2 cuts), or 4–7 pieces (3 cuts). In practice, 3 cuts give 4–5 pieces because not every cut passes through every existing piece.

#### Stage 4 — Fragment spawn

For each piece in `pieces`:
```
newCentroid = PolygonUtils.ComputeCentroid(piece)      // world space
localVerts  = piece.Select(v => v − newCentroid)       // shift to centroid-relative
area        = PolygonUtils.ComputeArea(piece)
massRatio   = area / originalArea
newMass     = originalMass × massRatio
newInertia  = PolygonUtils.ComputeInertia(localVerts, newMass)

// Velocity: original linear + rotational component at this fragment's centroid + spread
r           = newCentroid − originalCentroid
rotVel      = Perp(r) × originalAngularVelocity        // ω × r (2D cross product)
spreadDir   = Normalize(newCentroid − contact.Contact)
spreadSpeed = impactForce × massRatio × SpreadFactor   // tunable ~0.25

newLinear   = originalLinear + rotVel + spreadDir × spreadSpeed
newAngular  = originalAngular + Random(−SpinRange, SpinRange)

if area < MinFragmentArea:
    // Too small to be a physics entity — spawn visual debris only
    SpawnDebrisParticle(world, newCentroid, newLinear, TTL: 1.5f)
    continue

e = world.CreateEntity()
world.AddComponent(e, new Transform  { Position = newCentroid, Rotation = asteroid.Rotation })
world.AddComponent(e, new Velocity   { Linear = newLinear, Angular = newAngular })
world.AddComponent(e, new RigidBody  { Mass = newMass, Inertia = newInertia,
                                       LinearDrag = 0.02f, AngularDrag = 0.01f })
world.AddComponent(e, new Collider   { Shape = new PolygonShape(localVerts),
                                       Layer = Layers.Asteroid, Mask = Layers.Bullet | Layers.Ship })
world.AddComponent(e, new AsteroidData { Generation = originalGeneration + 1,
                                         FaultAngles = GenerateNewFaults() })
world.AddComponent(e, new Health     { Current = (int)(baseHealth × massRatio),
                                       Max = (int)(baseHealth × massRatio) })
world.AddComponent(e, new PolygonVisual { ... })
world.AddComponent(e, new WaveTag())
```

After all pieces are spawned:
```
world.DestroyEntity(originalAsteroid)
world.DestroyEntity(bullet)
```

**Critical invariant:** Every asteroid's `PolygonShape` vertices are in local space centered at the centroid. `Transform.Position` is the world-space centroid. This must hold for every spawned fragment — `localVerts` above must be centroid-relative, and the entity position must be set to `newCentroid`.

### 6.8 Enemy Ship Design

Enemy ships use the same `Transform`, `Velocity`, `RigidBody`, and `Collider` components as the player ship. Their behavior is governed by a simple state machine stored in `EnemyData.State`:

| State | Trigger in | Trigger out | Behavior |
|---|---|---|---|
| `Idle` | Wave spawn | Player within `AggroRange` | Drift slowly; rotate randomly |
| `Chase` | Player enters `AggroRange` | Player within `FireRange` | `ApplyForce` toward player; rotate to face player |
| `Attack` | Player within `FireRange` | Player outside `FireRange` × 1.5 | Fire at `FireRate`; continue steering |
| `Evade` | Health < 25% | Health ≥ 25% or dead | `ApplyForce` away from player |

Steering is a simple force toward/away from the player position — no pathfinding. Enemies navigate through the asteroid field by collision response (they bounce off asteroids naturally).

### 6.9 Wave Progression

`WaveManagerSystem` tracks wave state in local fields (not ECS components — single instance, no reason for it to be a component).

```
Wave N parameters:
  asteroidCount = clamp(3 + N × 2, 3, 20)
  asteroidRadius = lerp(60, 150, min(N/10, 1))   // larger over time
  asteroidHealth = 50 + N × 15
  enemyCount    = max(0, N − 2)                  // enemies from wave 3
  enemyHealth   = 80 + N × 20
  enemyFireRate = lerp(0.5, 2.0, min(N/15, 1))   // shots/second
```

Wave end condition: zero entities with `WaveTag` remaining. A 4-second delay follows before the next wave spawns, during which a "Wave N complete" overlay is shown.

### 6.10 Leaderboard

Stored at `~/.config/asteroids/leaderboard.json` (platform-agnostic path via `Environment.GetFolderPath`).

```json
[
  { "name": "ACE", "score": 14200, "wave": 7, "date": "2026-05-28" },
  ...
]
```

Rules: top 10 entries by score; written on game over; read on menu display. `Leaderboard.cs` in `Utils/` handles (de)serialisation via `System.Text.Json` (built into .NET 8, no dependency required).

### 6.11 System Execution Order

```
─── Input phase ───────────────────────────────────────────────
ShipControlSystem          (sequential) — thrust, rotation, shoot
EnemyAISystem              (sequential) — steering forces

─── Physics phase ─────────────────────────────────────────────
PhysicsSystem              [PARALLEL]  — force + drag integration
MovementSystem             [PARALLEL]  — position + rotation integration
WrapSystem                 [PARALLEL]  — toroidal edge wrap

─── Camera ────────────────────────────────────────────────────
CameraFollowSystem         (sequential) — smooth camera position

─── Collision phase ───────────────────────────────────────────
CollisionSystem            (sequential) — broad + narrow phase → event queue
EventFlushSystem           (sequential) — dispatch:
    AsteroidFragmentationSystem → split polygons, spawn fragments
    CombatSystem            → bullet-enemy damage, ship-asteroid damage

─── Cleanup phase ─────────────────────────────────────────────
TimeToLiveSystem           [PARALLEL]  — bullet/debris expiry
WaveManagerSystem          (sequential) — wave progress check, next wave spawn

─── Render ────────────────────────────────────────────────────
PolygonRenderSystem        (sequential) — camera transform + draw all entities + HUD
```

`world.FlushDeferred()` is called after all systems, exactly once per frame, committing all deferred entity creates and destroys.

---

## 7. Updated Roadmap

```
Phase 1–5 — COMPLETE
  Engine core, rendering, physics, events, state, two-player sandbox

Phase 6 — Engine Extensions (COMPLETE)
  ├── RigidBody: Inertia + AccumulatedTorque
  ├── PhysicsSystem: torque integration, ApplyForceAtPoint
  ├── World: ForEachParallel (3 overloads), EventBus ConcurrentQueue
  ├── FractureProperties, FractureState, VisualMesh components
  ├── CompoundShape: multi-part convex body, LastHitPartIndex, WithoutPart
  └── PolygonUtils: GenerateConvex, ClipConvexByHalfPlane, Split (3-phase),
                    NearestPointOnBoundary, ComputeArea/Centroid/Inertia, RecenterVertices

Phase 7 — Asteroid Demo (IN PROGRESS)
  ├── ✅ Player (WASD circle), asteroids (polygon physics), F-key bullets
  ├── ✅ Fracture: energy model, threshold+accumulation, zone/blast radii
  ├── ✅ Fracture: primary cut + inner cuts, SplitResult, blast zone particle filter
  ├── ✅ Surviving asteroid updated in-place (CompoundShape)
  ├── ✅ DebrisVisual: fading fragments + blast particles
  ├── ✅ config.json: all physics + fracture constants externalised
  ├── ✅ Wave progression with per-wave material presets
  ├── ⏳ Secondary radial cuts (the "bite" shaped fracture zone)  ← next
  ├── ⏳ Full rotational collision impulse in CollisionSystem
  ├── ⏳ Angular impulse from bullet hits
  ├── ⏳ VisualMesh rendering (concave surface from compound parts)
  └── ⏳ Sub-threshold visual scarring

Phase 8 — Asteroids on Steroids (full game)
  ├── Player ship (polygon, not circle), weapons, shields
  ├── Enemy ships + AI state machine
  ├── Wave manager + difficulty progression
  ├── Camera follow system
  ├── Menu / Playing / Pause / GameOver states
  └── Leaderboard (System.Text.Json, local file)

Phase 9 — Audio + Polish
  ├── AudioSystem (cross-platform)
  ├── Screen shake
  └── Performance profiling (collision narrow phase is primary candidate)
```

> **Physics spec:** The full 2D rigid body physics and fracture system design is documented in `docs/physics_spec.md`, which supersedes `docs/fracture_spec.md`.
