# AsteroidsEngine — Module Reference

This document describes every module in the engine: its purpose, responsibilities, how to use it, and how it is implemented in C#.

---

## Table of Contents

1. [Core — Entity](#1-core--entity)
2. [Core — SparseSet\<T\>](#2-core--spargesett)
3. [Core — World](#3-core--world)
4. [Core — ISystem / IDrawSystem](#4-core--isystem--idrawsystem)
5. [Core — EcsAction delegates](#5-core--ecsaction-delegates)
6. [Core — GameLoop](#6-core--gameloop)
7. [Components](#7-components)
8. [Events — EventBus](#8-events--eventbus)
9. [Events — CollisionEvent](#9-events--collisionevent)
10. [Input — KeyCode / MouseButton](#10-input--keycode--mousebutton)
11. [Input — InputSystem](#11-input--inputsystem)
12. [Rendering — Camera](#12-rendering--camera)
13. [Rendering — GameWindow](#13-rendering--gamewindow)
14. [Resources — ResourceManager](#14-resources--resourcemanager)
15. [Collision — CollisionShape (abstract)](#15-collision--collisionshape-abstract)
16. [Collision — CircleShape](#16-collision--circleshape)
17. [Collision — AABBShape](#17-collision--aabbshape)
18. [Collision — PolygonShape](#18-collision--polygonshape)
19. [Collision — ContactInfo](#19-collision--contactinfo)
20. [Collision — ISpatialIndex / SpatialGrid](#20-collision--ispatialindex--spatialgrid)
21. [Systems — MovementSystem](#21-systems--movementsystem)
22. [Systems — PhysicsSystem](#22-systems--physicssystem)
23. [Systems — CollisionSystem](#23-systems--collisionsystem)
24. [Systems — RenderSystem](#24-systems--rendersystem)
25. [State — IGameState / StateStack](#25-state--igamestate--statestack)
26. [Collision — CompoundShape](#26-collision--compoundshape)
27. [Collision — PolygonUtils (updated)](#27-collision--polygonutils-updated)
28. [Components — FractureProperties / FractureState / VisualMesh](#28-components--fractureproperties--fracturestate--visualmesh)

---

## 1. Core — `Entity`

**File:** `Engine/Core/Entity.cs`

### Purpose
An entity is nothing more than an integer ID with a version number attached. It does not hold any data. It is the "name tag" that ties components together in the ECS world.

### How it works
The ID is a slot index in the World's component stores. The version counter guards against **stale handles**: when an entity is destroyed and its ID is recycled for a new entity, the version is incremented. Any code still holding the old `Entity` value (old ID + old version) will fail `World.IsAlive()` because the version no longer matches.

```csharp
Entity e = world.CreateEntity();
// e.Id = 5, e.Version = 0

world.DestroyEntity(e);
// internal: _versions[5]++

Entity e2 = world.CreateEntity();  // reuses slot 5
// e2.Id = 5, e2.Version = 1

world.IsAlive(e)  // false — version mismatch
world.IsAlive(e2) // true
```

`Entity.Null` (`Id = 0, Version = 0`) is the sentinel "no entity" value, similar to a null pointer in C or `None` in Rust's `Option`.

### C# features used
- **`readonly struct`** — allocated on the stack with no heap overhead; copies are cheap (two ints). Like a C `struct` passed by value.
- **`IEquatable<Entity>`** — enables value-based equality (`==`/`!=`) without boxing. The engine explicitly implements `Equals`, `GetHashCode`, and the two operators.
- **`HashCode.Combine`** — standard library utility for composing hash codes from multiple fields.

---

## 2. Core — `SparseSet<T>`

**File:** `Engine/Core/SparseSet.cs`

### Purpose
The component storage primitive. One `SparseSet<T>` exists per component type. It maps entity IDs to component values and supports O(1) insert, lookup, and delete while keeping data **packed** (no gaps) for cache-friendly iteration.

### How it works
Three parallel arrays:

```
sparse[entityId]  → index into dense[], or -1 if absent
dense[i]          → entity ID of the i-th stored element
data[i]           → component value for dense[i]
```

**Removal** is O(1) via swap-and-pop: the target slot is overwritten with the last element, and `Count` is decremented. No gaps are ever left in `dense` or `data`. This is the same trick used in Rust ECS libraries like `hecs` and `bevy`.

**Iteration** walks `dense[0..Count]` sequentially — cache-friendly because `data` is also contiguous.

### C# features used
- **`internal sealed class`** — only visible inside the assembly (`internal`); cannot be subclassed (`sealed`).
- **`ISparseSetEraser`** — a non-generic interface trick: `World` stores all sparse sets as `object`, and needs to call `Remove` without knowing `T`. The interface provides a type-erased `RemoveById(int)` entry point. This is the C# equivalent of using `void*` + a function pointer in C, or a trait object in Rust.
- **`ref T GetByDenseIndex(int)`** — returns a managed reference (like a `&mut T` in Rust). The caller mutates the component value in place without copying it.
- **`ReadOnlySpan<int> DenseIds`** and **`ReadOnlySpan<T> DenseData`** — zero-copy views of the underlying arrays, like Rust's `&[T]` slices.
- **`Array.Resize`** / **`Array.Fill`** — standard library helpers for growing arrays.

---

## 3. Core — `World`

**File:** `Engine/Core/World.cs`

### Purpose
The ECS container. Owns all entity IDs and all component stores. The only way to create entities, attach/detach/query components, and schedule deferred operations.

### Responsibilities
- **Entity lifecycle:** create, destroy (deferred or immediate), check aliveness.
- **Component storage:** `AddComponent`, `GetComponent` (by ref), `TryGetComponent`, `HasComponent`, `RemoveComponent`.
- **Iteration:** `ForEach<T>`, `ForEach<T1,T2>`, `ForEach<T1,T2,T3>` — the primary way systems access components.
- **Snapshot queries:** `QueryEntities<T>()` — a safe copy for when you need to destroy entities while iterating.
- **Deferred flush:** `FlushDeferred()` — applies all pending destroys and component removals at a safe point between frames.

### How to use

```csharp
var world = new World();

// Create an entity and attach components
Entity ship = world.CreateEntity();
world.AddComponent(ship, new Transform { Position = new Vector2(100, 200) });
world.AddComponent(ship, new Velocity  { Linear = new Vector2(50, 0) });

// Iterate all entities with both components — ref access, no copy
world.ForEach<Transform, Velocity>((Entity e, ref Transform t, ref Velocity v) =>
{
    t.Position += v.Linear * (float)dt;
});

// Schedule entity removal (safe during iteration)
world.DestroyEntity(ship);

// Apply all pending removals — call once per frame
world.FlushDeferred();
```

### How deferred destruction works
`DestroyEntity` only appends to a `_pendingDestroy` list. The actual removal (incrementing the version, recycling the ID, removing all components) happens in `FlushDeferred()`. This prevents iterator invalidation: a system can call `DestroyEntity` on any entity while `ForEach` is running without corrupting the iteration.

### C# features used
- **`sealed class`** — no subclassing.
- **`Dictionary<Type, object> _stores`** — a heterogeneous map from component type to its `SparseSet<T>`. The runtime `Type` object is the key; each value is cast back to `SparseSet<T>` in the private `Store<T>()` helper. In C you'd use an array of `void*` or tagged unions; in Rust this requires `Any` or a macro.
- **`ref T GetComponent<T>`** — returns a managed reference into the sparse set's data array. Mutations go directly to the stored value.
- **Generic constraints `where T : struct`** — enforces that all components are value types (structs), which keeps them in the packed arrays rather than on the heap.
- **`Queue<int> _recycled`** — the free list of recycled entity IDs.

---

## 4. Core — `ISystem` / `IDrawSystem`

**File:** `Engine/Core/ISystem.cs`

### Purpose
The interfaces every system must implement.

- `ISystem.Update(World, double dt)` — logic update, called once per frame.
- `IDrawSystem.Draw(World, Graphics g)` — rendering, called after all updates complete.

### How to use
```csharp
public sealed class MovementSystem : ISystem
{
    public void Update(World world, double dt) { ... }
}

public sealed class RenderSystem : IDrawSystem
{
    public void Draw(World world, Graphics g) { ... }
}

loop.AddSystem(new MovementSystem());
// RenderSystem is wired separately via GameWindow.OnGameDraw
```

### C# features used
- **Interface** with a default method implementation (`bool UpdatesBelow => false` in `IGameState`) — C# 8+ allows interfaces to provide default implementations, similar to Rust trait default methods.
- **`System.Drawing.Graphics`** — GDI+ drawing context, passed down to drawing systems.

---

## 5. Core — `EcsAction` delegates

**File:** `Engine/Core/QueryResults.cs`

### Purpose
Typed delegate signatures for `World.ForEach`. They allow lambdas to receive component data by `ref` — mutating components in place without copying them.

```csharp
// Delegate definitions
delegate void EcsAction<T>(Entity e, ref T c) where T : struct;
delegate void EcsAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2) where T1, T2 : struct;
delegate void EcsAction<T1, T2, T3>(...) where T1, T2, T3 : struct;
```

### Why custom delegates instead of `Action<>`?
`Action<T>` cannot express `ref` parameters. These custom delegate types are the only way in C# to pass a `ref`-taking lambda.

### C# features used
- **`delegate`** — a named, typed function pointer signature. In C this would be a `typedef` of a function pointer; in Rust a `fn` trait bound.
- **`ref` parameters in delegates** — allows the callback to mutate the caller's data in place.

---

## 6. Core — `GameLoop`

**File:** `Engine/Core/GameLoop.cs`

### Purpose
Drives the simulation at a fixed target framerate on a dedicated background thread. Sequences the frame: input → systems → flush → draw → sleep.

### Frame sequence (per tick)
```
1. Measure dt with Stopwatch
2. InputSystem.BeginFrame()      — commit pending input state
3. ISystem.Update(world, dt)     — all logic systems, in registration order
4. World.FlushDeferred()         — apply pending entity/component removals
5. OnDraw()                      — GameWindow creates Graphics, renders, swaps buffers
6. Thread.Sleep(remaining budget)
```

### How to use
```csharp
var loop = new GameLoop(world, input);
loop.TargetFps = 60;
loop.AddSystems(new PhysicsSystem(), new MovementSystem(), new CollisionSystem(...));
loop.OnDraw = () => window.DrawFrame();   // wired automatically by GameWindow
loop.Start();  // spins up a background thread
```

### Design decision — why a background thread?
WinForms requires its UI thread for window events (paint, resize, input). The game loop runs on a second thread so the game update rate is decoupled from the UI thread's message pump. `InputSystem` bridges the two via a lock (see section 11).

### C# features used
- **`Thread`** — explicit OS thread, not a thread-pool task. `IsBackground = true` means the CLR will not keep the process alive for this thread if the main thread exits.
- **`volatile bool _running`** — ensures the game thread sees writes from the UI thread (e.g. `Stop()`) without a full lock. Comparable to `std::atomic<bool>` in C++ or `AtomicBool` in Rust.
- **`Stopwatch`** — high-resolution timer backed by `QueryPerformanceCounter` on Windows. More precise than `DateTime.Now`.
- **`Action? OnDraw`** — a nullable delegate (function pointer). `GameWindow` sets it during construction.
- **`event Action? Stopped`** — a multicast delegate; multiple subscribers can listen for loop termination.

---

## 7. Components

**Files:** `Engine/Components/`

Components are plain data structs — no methods beyond simple computed properties. They have no knowledge of systems or world.

| Component | Fields | Purpose |
|---|---|---|
| `Transform` | `Position: Vector2`, `Rotation: float` (radians) | World-space position and orientation |
| `Velocity` | `Linear: Vector2` (units/s), `Angular: float` (rad/s) | Rate of change of Transform |
| `RigidBody` | `Mass`, `LinearDrag`, `AngularDrag`, `AccumulatedForce` | Physics properties; consumed by PhysicsSystem |
| `Collider` | `Shape: CollisionShape`, `Layer: int`, `Mask: int` | Collision shape + layer filter bitmask |
| `Sprite` | `ImageId: string`, `Layer: int`, `Offset: Vector2`, `Tint: Color` | Visual representation; consumed by RenderSystem |
| `Health` | `Current: int`, `Max: int` | Hit points; `IsDead` and `Fraction` are computed properties |
| `DestroyTag` | *(empty)* | Marker — entity will be removed next `FlushDeferred()` |
| `DisabledTag` | *(empty)* | Marker — entity is skipped by all systems |

### How to use
```csharp
// Attach components when spawning an entity
Entity asteroid = world.CreateEntity();
world.AddComponent(asteroid, new Transform { Position = spawnPos, Rotation = 0f });
world.AddComponent(asteroid, new Velocity  { Linear = new Vector2(80, -30) });
world.AddComponent(asteroid, new Collider  { Shape = new CircleShape(24f), Layer = Layers.Asteroid, Mask = Layers.Player | Layers.Bullet });
world.AddComponent(asteroid, new Health    { Current = 3, Max = 3 });
world.AddComponent(asteroid, new Sprite    { ImageId = "asteroid_large", Layer = 1, Tint = Color.White });

// Mark for removal without destroying immediately
world.AddComponent(asteroid, new DestroyTag());
```

### C# features used
- **`struct`** — value types stored directly in the SparseSet arrays (no heap allocation per component).
- **Computed properties** (`Health.IsDead`, `Health.Fraction`) — getter-only properties on structs that derive a value from stored fields. No backing field needed.
- **`static` factory method** (`Health.Full(int max)`, `Sprite.Create(string id)`) — a convenience constructor pattern since structs cannot have parameterless constructors with non-zero defaults (in older C# versions).
- **Marker structs** (`DestroyTag`, `DisabledTag`) — zero-size structs used purely as type-level flags. Querying `HasComponent<DestroyTag>(e)` is O(1); no runtime data is stored.

---

## 8. Events — `EventBus`

**File:** `Engine/Events/EventBus.cs`

### Purpose
A typed publish-subscribe system. Systems publish events (e.g. collision occurred, enemy died) without needing a direct reference to the subscribers. Events are queued during the update phase and dispatched at a safe point.

### How to use
```csharp
// Subscribe (usually in game state initialization)
bus.Subscribe<CollisionEvent>(ev =>
{
    Console.WriteLine($"{ev.EntityA} hit {ev.EntityB}");
});

// Publish (from any system — deferred until Flush)
bus.Publish(new CollisionEvent(entityA, entityB, contact));

// Flush — call once per frame after all systems have run
bus.Flush();

// Unsubscribe when a state is exited
bus.Unsubscribe<CollisionEvent>(handler);
```

### Deferred vs immediate
- `Publish<T>` — queues the event. Handler runs on the next `Flush()`. Safe to call from inside a `ForEach` loop.
- `PublishImmediate<T>` — dispatches right now, synchronously. Use only when the handler must react within the same frame tick and you are certain it won't mutate world state being iterated.

### C# features used
- **`Dictionary<Type, List<object>>`** — stores handlers keyed by event type. Each value is a list of `Action<T>` delegates boxed as `object` (since the dictionary is not generic).
- **`Queue<(Type, object)>`** — the deferred event queue. A value tuple `(Type type, object evt)` pairs the runtime type with the boxed event data.
- **`DynamicInvoke`** — calls a delegate when you only have it as a `Delegate` base reference (the generic `T` is not known at the call site). This involves reflection and boxing, which is a known performance cost. In a high-throughput engine you'd replace this with source-generated code or a different dispatch pattern.
- **`Action<T>`** — a built-in delegate type for void-returning functions with one parameter.

---

## 9. Events — `CollisionEvent`

**File:** `Engine/Events/CollisionEvent.cs`

### Purpose
The data payload emitted by `CollisionSystem` when two entities make contact.

```csharp
public record CollisionEvent(Entity EntityA, Entity EntityB, ContactInfo Contact);
```

### C# features used
- **`record`** — a C# 9 feature that auto-generates a constructor, `Equals`, `GetHashCode`, `ToString`, and a `with` expression. Because `CollisionEvent` is a record (reference type by default), equality is value-based (compares all fields). Equivalent to a Rust struct with `#[derive(PartialEq, Clone, Debug)]`.

---

## 10. Input — `KeyCode` / `MouseButton`

**File:** `Engine/Input/KeyCode.cs`

### Purpose
Engine-internal key identifiers, independent of WinForms. Games reference `KeyCode.Space`, not `Keys.Space`, keeping game code decoupled from the windowing library.

### Design decision — values match `System.Windows.Forms.Keys`
The numeric values are deliberately identical to WinForms `Keys` enum values. `GameWindow` casts directly: `(KeyCode)(int)e.KeyCode`. No lookup table needed.

### C# features used
- **`enum` with explicit integer values** — identical to a C `enum`. C# enums are named integer constants; they do not carry data like Rust enums.

---

## 11. Input — `InputSystem`

**File:** `Engine/Input/InputSystem.cs`

### Purpose
Bridges WinForms input events (UI thread) with game system polling (game thread) in a thread-safe way.

### How it works
Two sets of state exist simultaneously:
- **`_pending`** — written by the UI thread via `OnKeyDown`/`OnKeyUp`. Protected by a lock.
- **`_committed`** — the snapshot the game thread reads. Updated once per frame by `BeginFrame()`.

`BeginFrame()` (called by `GameLoop` at the top of each tick) swaps pending into committed under the lock and computes delta sets (`_pressedThisFrame`, `_releasedThisFrame`).

### How to use
```csharp
// In a system's Update:
if (input.IsPressed(KeyCode.Space))   // true for exactly one frame
    FireBullet(world);

if (input.IsHeld(KeyCode.W))          // true every frame while held
    ApplyThrust(world, ship);

if (input.IsReleased(KeyCode.Escape)) // true for exactly one frame
    PauseGame();
```

### C# features used
- **`HashSet<KeyCode>`** — O(1) add, remove, and contains for the key state. Used for all three sets (pending, committed, pressed-this-frame, released-this-frame).
- **`lock (_lock)`** — mutual exclusion around the `_pending` state. The `_lock` object is a dedicated `object` instance (a C# convention for lock targets). Equivalent to `pthread_mutex_lock` in C or `Mutex::lock()` in Rust.
- **`volatile`** on scalar fields would not help here because multiple fields must be updated atomically — hence the full lock.

---

## 12. Rendering — `Camera`

**File:** `Engine/Rendering/Camera.cs`

### Purpose
Defines the viewport: what part of the world is visible, and where on screen. Converts between world space and screen space.

### How it works
The camera applies a GDI+ affine transform matrix to the `Graphics` context:
1. Translate world origin → screen centre
2. Scale by `Zoom`

```
screen_x = (world_x - camera.X) * Zoom + ScreenWidth  / 2
screen_y = (world_y - camera.Y) * Zoom + ScreenHeight / 2
```

### How to use
```csharp
// In a draw method:
camera.ApplyTo(g);
// ... draw world-space entities ...
camera.ResetTransform(g);   // back to screen space
// ... draw HUD (fixed position) ...

// Mouse picking:
Vector2 worldPos = camera.ScreenToWorld(input.MouseScreen);
```

### C# features used
- **`System.Drawing.Drawing2D.Matrix`** — GDI+ 3×3 affine transform matrix. `TranslateTransform` and `Scale` build the composite matrix.
- **`g.Save()` / `g.Restore(GraphicsState)`** — saves and restores the full graphics state (transform, clip region) around a block of drawing code. Used by `RenderSystem` to isolate per-entity transforms.
- **Properties with `get`/`set`** — `Position`, `Zoom`, `ScreenWidth`, `ScreenHeight` are auto-properties.

---

## 13. Rendering — `GameWindow`

**File:** `Engine/Rendering/GameWindow.cs` *(compiled only on Windows — `#if WINDOWS`)*

### Purpose
The OS window. Owns the two bitmaps used for double-buffering, forwards input events to `InputSystem`, and blits the completed frame to the screen.

### How it works — double buffering
Two `Bitmap` objects exist: `_backBuffer` (game thread draws into it) and `_frontBuffer` (UI thread reads from it in `OnPaint`).

Each frame:
1. Game thread draws into `_backBuffer` via `Graphics.FromImage`.
2. `Interlocked.Exchange` atomically swaps `_backBuffer` and `_frontBuffer`.
3. `Invalidate()` tells WinForms to schedule a repaint.
4. UI thread's `OnPaint` blits `_frontBuffer` to the form surface via `DrawImage`.

The swap is atomic (one pointer-width operation), so the UI thread always reads a fully-rendered frame.

### How to use
```csharp
var window = new GameWindow(800, 600, input, loop);
window.OnGameDraw = g =>
{
    renderSystem.Draw(world, g);
    hudSystem.Draw(world, g);
};
Application.Run(window);
```

### C# features used
- **`Form`** (WinForms) — the base class for an OS window. `GameWindow` inherits from it.
- **`Interlocked.Exchange`** — atomic pointer swap, no lock needed. Equivalent to `std::atomic::exchange` in C++ or `AtomicPtr::swap` in Rust.
- **`Volatile.Read`** in `OnPaint` — prevents the JIT from caching `_frontBuffer` in a register across the exchange. Equivalent to `std::atomic_load` with relaxed ordering.
- **`ControlStyles`** flags — disables WinForms' default background painting so we can fill every pixel ourselves. Without this, the window would flicker.
- **`#if WINDOWS`** preprocessor directive — conditionally compiles the file. On non-Windows targets (Linux/macOS), the rendering layer would use a different backend.
- **`IDisposable.Dispose`** — called by WinForms when the form closes; frees the two bitmaps explicitly rather than waiting for GC.

---

## 14. Resources — `ResourceManager`

**File:** `Engine/Resources/ResourceManager.cs`

### Purpose
Loads and caches images and fonts by string ID. Prevents loading the same asset twice. Disposes all GDI+ objects on shutdown.

### How to use
```csharp
var resources = new ResourceManager();
resources.LoadImage("asteroid_large", "assets/asteroid.png");
resources.RegisterImage("bullet", GenerateBulletBitmap());

// In RenderSystem:
Bitmap? img = resources.GetImage("asteroid_large");

// Font — created and cached on first request
Font font = resources.GetFont("Arial", size: 14f);

// On shutdown
resources.Dispose();
```

### C# features used
- **`IDisposable`** — the `Dispose()` method explicitly releases all `Bitmap` and `Font` GDI+ objects. These wrap unmanaged Win32 handles; if not disposed, they leak until the GC finalizes them (non-deterministic). The `using` pattern or explicit `Dispose()` is the C# equivalent of `free()` in C.
- **`Dictionary<string, Bitmap>`** — key-value store for asset lookup by string ID.
- **`bool _disposed` guard** — the standard pattern for making `Dispose()` idempotent (safe to call twice).

---

## 15. Collision — `CollisionShape` (abstract)

**File:** `Engine/Collision/CollisionShape.cs`

### Purpose
The base class for all collision shapes. Provides the interface for narrow-phase intersection tests and AABB computation.

### How it works — double dispatch
Naive collision dispatch would require an `if/else` type-switch on both shapes, growing as O(N²) with shape types. This engine uses **double dispatch** instead:

```
A.Intersects(B)
  → B.IntersectsCircle(A)    if A is CircleShape
  → B.IntersectsAABB(A)      if A is AABBShape
  → B.IntersectsPolygon(A)   if A is PolygonShape
```

Each concrete shape knows how to handle every specific pair. The correct algorithm is selected through two virtual dispatch steps with no `switch` in the caller.

### C# features used
- **`abstract class`** — cannot be instantiated directly; forces subclasses to implement `Intersects` and `GetAABB`. In Rust this maps to a trait with no default implementations.
- **`internal abstract` methods** — the double-dispatch helpers (`IntersectsCircle`, `IntersectsPolygon`, `IntersectsAABB`) are visible only within the assembly (`internal`), hiding the implementation detail from game code.
- **`ContactInfo?` (nullable return)** — `null` means no collision. The `?` makes the absence explicit in the type, similar to Rust's `Option<ContactInfo>`.

---

## 16. Collision — `CircleShape`

**File:** `Engine/Collision/CircleShape.cs`

### Purpose
Circular collision shape. Defined by a single radius. Cheapest shape for narrow-phase tests.

### Algorithms
- **Circle vs Circle** — compare squared distance to sum-of-radii squared (avoids a `sqrt` until needed).
- **Circle vs AABB** — clamp circle centre to AABB bounds; test clamped distance against radius.
- **Circle vs Polygon** — delegates to `PolygonShape.IntersectsCircle` (SAT-based).

### How to use
```csharp
world.AddComponent(entity, new Collider
{
    Shape = new CircleShape(radius: 32f),
    Layer = Layers.Enemy,
    Mask  = Layers.Bullet,
});
```

---

## 17. Collision — `AABBShape`

**File:** `Engine/Collision/AABBShape.cs`

### Purpose
Axis-aligned bounding box. Defined by half-width and half-height. Ignores `Transform.Rotation` — stays axis-aligned regardless of entity orientation. Useful for UI triggers or objects that never rotate.

### Algorithms
- **AABB vs AABB** — overlap test on both axes; pick the axis of minimum overlap for the contact normal.
- **AABB vs Circle** — delegates to `CircleShape.IntersectsAABB` with flipped normal.
- **AABB vs Polygon** — converts itself to a `PolygonShape` (four vertices) and runs SAT.

---

## 18. Collision — `PolygonShape`

**File:** `Engine/Collision/PolygonShape.cs`

### Purpose
Convex polygon collision shape. Vertices are defined in local space (centred at origin). Supports rotation via `Transform.Rotation`.

**Constraint:** must be convex. Concave polygons will produce incorrect results.

### Algorithms
- **Polygon vs Polygon** — Separating Axis Theorem (SAT): test all edge normals of both polygons as potential separating axes. Returns on the first separating axis found (early exit). The axis with the minimum overlap is the contact normal.
- **Polygon vs Circle** — SAT using edge normals plus the axis from the circle centre to the nearest vertex.
- **Polygon vs AABB** — converts AABB to a polygon and runs polygon vs polygon SAT.

### How to use
```csharp
// Ship as a triangle
world.AddComponent(ship, new Collider
{
    Shape = new PolygonShape(new[]
    {
        new Vector2( 0,  -20),  // nose
        new Vector2( 12,  15),  // bottom-right
        new Vector2(-12,  15),  // bottom-left
    }),
    Layer = Layers.Player,
    Mask  = Layers.Enemy | Layers.Asteroid,
});
```

### C# features used
- **`params` (implicit array in callers)** — vertices are passed as a plain array.
- **`MathF`** — single-precision math functions (`MathF.Cos`, `MathF.Sin`). Prefer over `Math` when working with `float` to avoid implicit widening to `double`.
- **`ref` local variables** — not used here, but the SAT helpers use `out float min, out float max` — output parameters that let a method return multiple values without allocating a tuple.

---

## 19. Collision — `ContactInfo`

**File:** `Engine/Collision/ContactInfo.cs`

### Purpose
The result of a successful narrow-phase test. Carries the contact normal, penetration depth, and an approximate contact point.

```csharp
public readonly struct ContactInfo
{
    public readonly Vector2 Normal;       // unit vector, from B toward A
    public readonly float   Depth;        // penetration distance
    public readonly Vector2 ContactPoint; // world-space contact location
}
```

`Flipped()` returns the same contact with the normal negated — used when the double-dispatch chain reverses the A/B roles.

### C# features used
- **`readonly struct`** — immutable value type. All fields are `readonly`, preventing accidental mutation. Passed by value with zero heap allocation.

---

## 20. Collision — `ISpatialIndex` / `SpatialGrid`

**Files:** `Engine/Collision/ISpatialIndex.cs`, `Engine/Collision/SpatialGrid.cs`

### Purpose
The broad phase of collision detection. Instead of testing every entity pair (O(N²)), entities are bucketed by spatial location and only nearby pairs are forwarded to the narrow phase.

`ISpatialIndex` defines the contract. `SpatialGrid` is the current implementation. A quadtree or BVH could replace it without changing `CollisionSystem`.

### How `SpatialGrid` works
The world is divided into a uniform grid of square cells (default: 128×128 units). Each entity is inserted into every cell its AABB overlaps. Candidate lookup returns all entities sharing at least one cell with the query AABB.

**Cell key:** two 32-bit cell coordinates packed into one 64-bit integer (`((long)(uint)cx << 32) | (uint)cy`). This avoids a struct key allocation and gives a unique hash for every cell pair.

**List pooling:** `SpatialGrid` maintains a `Stack<List<Entity>>` of cleared lists. On `Clear()`, all per-cell lists are returned to the pool instead of being garbage-collected. On `Insert()`, a pooled list is reused if available. This eliminates GC pressure in the allocation-heavy per-frame rebuild.

### How to use
```csharp
var grid = new SpatialGrid(cellSize: 128f);
var collisionSystem = new CollisionSystem(grid, eventBus);
loop.AddSystem(collisionSystem);
// CollisionSystem handles Insert/Clear/GetCandidates internally.
```

### C# features used
- **`Dictionary<long, List<Entity>>`** — the cell map. `long` key avoids struct hashing overhead.
- **`Stack<List<Entity>>`** — the list pool. `Stack` is LIFO which is fine for pooling.
- **`interface`** — `ISpatialIndex` decouples `CollisionSystem` from the implementation. Swap `SpatialGrid` for any other implementation at construction time.

---

## 21. Systems — `MovementSystem`

**File:** `Engine/Systems/MovementSystem.cs`

### Purpose
Integrates position and rotation from velocity each frame. Simple forward Euler integration.

```
position += linear  * dt
rotation += angular * dt
```

### Design boundary
`MovementSystem` does not apply forces or drag — that is `PhysicsSystem`'s job. It only reads `Velocity` and writes `Transform`. This clean separation means you can have moving entities without physics (scripted movement, UI elements) by omitting `RigidBody`.

### Required system order
`PhysicsSystem` must run before `MovementSystem` so the velocity updated by forces is used for this frame's position change.

---

## 22. Systems — `PhysicsSystem`

**File:** `Engine/Systems/PhysicsSystem.cs`

### Purpose
Applies accumulated forces and drag to entities' velocities. Uses **symplectic Euler** integration (velocity updated before position), which conserves energy better than classic forward Euler.

### Per-frame sequence for each entity with `Velocity + RigidBody`
1. Add gravity as a force: `AccumulatedForce += Gravity * Mass`
2. Integrate velocity: `v += (F / m) * dt`
3. Apply exponential drag: `v *= e^(-drag * dt)` — physically correct decay, frame-rate independent
4. Reset `AccumulatedForce = 0`

### How to apply forces
```csharp
// Thrust — call before PhysicsSystem.Update runs
PhysicsSystem.ApplyForce(world, ship, thrustDirection * thrustMagnitude);

// Set drag on the RigidBody component
world.AddComponent(ship, new RigidBody { Mass = 1f, LinearDrag = 0.8f, AngularDrag = 2f });
```

`LinearDrag = 0` means no slowdown; higher values stop the entity faster. The exponential formula `e^(-drag * dt)` is frame-rate independent — unlike multiplying by a fixed fraction each frame.

### C# features used
- **`MathF.Exp`** — single-precision exponential. Matches `expf()` in C.
- **`static` method `ApplyForce`** — a utility that does not require a `PhysicsSystem` instance. Callable from any system.
- **`Vector2` arithmetic** — `System.Numerics.Vector2` is a SIMD-accelerated type on .NET 6+; `+`, `-`, `*`, `/` operators are hardware-intrinsic where possible.

---

## 23. Systems — `CollisionSystem`

**File:** `Engine/Systems/CollisionSystem.cs`

### Purpose
Detects collisions between entities with `Transform + Collider` components. Emits `CollisionEvent` via the `EventBus`. Optionally separates overlapping entities.

### Per-frame sequence
1. **Rebuild spatial index** — `Clear()` then `Insert()` each collidable entity's AABB.
2. **Broad phase** — for each entity, call `GetCandidates()` to find nearby entities.
3. **Deduplication** — a `HashSet<(int,int)>` of canonical ordered pairs prevents testing A→B and B→A separately.
4. **Layer/mask filter** — `(A.Mask & B.Layer) == 0 && (B.Mask & A.Layer) == 0` skips uninteresting pairs (e.g. bullets don't collide with other bullets).
5. **Narrow phase** — `shape.Intersects(...)` using double dispatch.
6. **Overlap resolution** — if `ResolveOverlap = true`, entities are pushed apart along the contact normal, weighted by inverse mass.
7. **Event publish** — `bus.Publish(new CollisionEvent(...))` queues the event for dispatch after systems complete.

### How to respond to collisions
```csharp
bus.Subscribe<CollisionEvent>(ev =>
{
    if (world.HasComponent<Health>(ev.EntityA))
    {
        ref var hp = ref world.GetComponent<Health>(ev.EntityA);
        hp.Current -= 10;
        if (hp.IsDead) world.DestroyEntity(ev.EntityA);
    }
});
```

### C# features used
- **`HashSet<(int, int)>`** — the tested-pair deduplication set. The tuple `(int, int)` is a value type with structural equality — no custom `IEqualityComparer` needed.
- **`ref var`** — `ref var cB = ref world.GetComponent<Collider>(entityB)` holds a managed reference into the SparseSet, avoiding a copy of the component.
- **Reused buffers** (`_candidates`, `_testedPairs`) — both are cleared and reused each frame to avoid per-frame allocations.

---

## 24. Systems — `RenderSystem`

**File:** `Engine/Systems/RenderSystem.cs`

### Purpose
Draws all entities that have `Transform + Sprite`. Sorts by `Sprite.Layer` (painter's algorithm). Applies the camera transform before drawing and resets it after.

### Per-frame sequence
1. Collect all visible (non-disabled, non-empty `ImageId`) entities into `_drawList`.
2. Sort `_drawList` by `Sprite.Layer` ascending.
3. Apply camera transform to `g`.
4. For each entry: look up the `Bitmap` in `ResourceManager`, transform `g` to the entity's position and rotation, draw.
5. Restore `g` to screen space.

### Tinting
If `Sprite.Tint != Color.White`, a `ColorMatrix` is applied via `ImageAttributes`. The matrix scales each channel (R, G, B, A) by the tint's normalized component. This avoids creating a new `Bitmap` per tinted entity — the original image is drawn through the matrix.

### C# features used
- **`IDrawSystem`** — implements `Draw(World, Graphics)`, not `Update`. Called from `GameWindow.OnGameDraw`, not from `GameLoop.AddSystem`.
- **`g.Save()` / `g.Restore()`** — saves the entire `GraphicsState` (transform, clipping region, rendering hints) and restores it. Each sprite gets an isolated transform without affecting others.
- **`ColorMatrix`** (GDI+ `System.Drawing.Imaging`) — a 5×5 matrix applied to RGBA pixel values at draw time.
- **`List<(float, float, float, Sprite)>` value tuple list** — reused each frame (`_drawList.Clear()`); avoids per-frame `new List<>`.
- **`List.Sort` with lambda comparator** — `_drawList.Sort((a, b) => a.sprite.Layer.CompareTo(b.sprite.Layer))`.

---

## 25. State — `IGameState` / `StateStack`

**Files:** `Engine/State/IGameState.cs`, `Engine/State/StateStack.cs`

### Purpose
Organises the game into discrete modes: `MainMenuState`, `PlayingState`, `PausedState`, `GameOverState`, etc. The `StateStack` manages transitions and delegates `Update`/`Draw` to the active states.

### `IGameState` interface

```csharp
interface IGameState
{
    void Enter();               // called when state becomes active
    void Exit();                // called when state is deactivated
    void Update(double dt, InputSystem input);
    void Draw(Graphics g);
    bool UpdatesBelow => false; // if true, state below also receives Update
}
```

### `StateStack` behaviour
- **`Push(state)`** — overlays a new state. The state below is suspended but not exited (e.g. pause menu overlaid on the game).
- **`Pop()`** — removes the top state; state below resumes.
- **`Replace(state)`** — exits current top and pushes a new one (e.g. transitioning from menu to game).
- **`Update`** — propagates downward while `UpdatesBelow = true`. A pause menu sets `UpdatesBelow = false` to freeze the game below it.
- **`Draw`** — renders all states bottom-to-top. The top state is drawn last (on top).

### How to use
```csharp
var stack = new StateStack();
stack.Push(new MainMenuState(world, bus, resources));

// In MainMenuState.Update, when "Play" is pressed:
stack.Replace(new PlayingState(world, bus, resources));

// When Escape is pressed in PlayingState:
stack.Push(new PausedState());   // game below still visible but frozen
```

### C# features used
- **`interface` with default implementation** — `bool UpdatesBelow => false` provides a default so implementing classes don't have to override it unless they need the non-default behaviour. This is a C# 8+ feature.
- **`List<IGameState>` used as a stack** — `_stack[^1]` (index-from-end operator, C# 8+) accesses the top element without `Peek()`. `RemoveAt(_stack.Count - 1)` removes it.
- **`[^1]` index-from-end syntax** — `^1` is syntactic sugar for `Length - 1`. Equivalent to `stack[stack.Count - 1]`.

---

## 26. Collision — `CompoundShape`

**File:** `Engine/Collision/CompoundShape.cs`

### Purpose
A collision shape composed of multiple convex child shapes. Enables concave-capable collision for fractured asteroids without replacing the SAT pipeline. All parts share the entity's `Transform` (same position and rotation). The compound is a single rigid body — parts do not move independently.

### How it works
`Intersects` fans across all parts and returns the deepest contact. Double-dispatch works naturally: each part is a `PolygonShape` or `CircleShape` and dispatches through the existing table. `CompoundShape` overrides all four `Intersects*` methods to fan across its parts (required for it to be the "B" shape in a collision pair).

`LastHitPartIndex` is set during `Intersects` to record which part produced the deepest contact. The fracture system reads this immediately after the collision event to identify and extract the struck part.

### Key API
```csharp
int                   PartCount
int                   LastHitPartIndex        // -1 if no contact
CollisionShape        GetPart(int index)
CompoundShape         WithoutPart(int index)  // returns new compound minus that part
ContactInfo?          Intersects(...)         // fans across parts
(Vector2, Vector2)    GetAABB(...)            // union of part AABBs
```

### Moment of inertia for compound shapes
Use the parallel axis theorem when computing `RigidBody.Inertia` for a compound entity:
```
I_compound = Σᵢ (Iᵢ_own + mᵢ × dᵢ²)
```
Where `Iᵢ_own = PolygonUtils.ComputeInertia(localVertsᵢ, mᵢ)` and `dᵢ = |centroidᵢ − compoundCoM|`.

---

## 27. Collision — `PolygonUtils` (updated)

**File:** `Engine/Collision/PolygonUtils.cs`

### Purpose
Pure-geometry utilities for convex polygon generation, clipping, splitting, and physical property computation. No ECS, no rendering dependencies. All methods operate on `Vector2[]` arrays in the caller's coordinate space.

### Key invariant
Every output of `ClipConvexByHalfPlane` applied to a convex polygon is convex. All polygons in `SplitResult` are therefore convex — no separate convex decomposition step is needed.

### Methods

| Method | Purpose |
|---|---|
| `GenerateConvex(sides, radius, rng, ...)` | Random **strictly convex** polygon (Valtr's algorithm), centred at origin, CW winding, scaled to mean radius. (Sorted-angle + random-radius placement does *not* guarantee convexity, so Valtr's is used.) |
| `ClipConvexByHalfPlane(polygon, planePoint, planeNormal)` | Sutherland-Hodgman single half-plane clip |
| `Split(polygon, impactPoint, impactDir, ...)` | 3-phase fracture algorithm → `SplitResult` |
| `NearestPointOnBoundary(polygon, point)` | Nearest point on any polygon edge; always on the surface |
| `RecenterVertices(worldVerts)` | Returns `(centroid, centroidRelativeLocalVerts)` |
| `ComputeArea(verts)` | Signed shoelace area |
| `ComputeCentroid(verts)` | Area-weighted centroid |
| `ComputeInertia(centroidRelativeVerts, mass)` | Moment of inertia about centroid (Mirtich formula) |

### `SplitResult`

```csharp
struct SplitResult
{
    Vector2[]?  PrimaryFarPiece;     // cut-1 far side; update surviving entity; null if degenerate
    Vector2[][] SecondaryFarPieces;  // cut-2..K far sides; attach to surviving compound
    Vector2[][] SurvivingFragments;  // area ≥ threshold AND centroid > blastRadius
    Vector2[][] DebrisFragments;     // area < threshold OR centroid ≤ blastRadius → particles
}
```

All arrays contain convex polygons in the caller's world coordinate space.

> Full algorithm detail: `docs/physics_spec.md §6`.

---

## 28. Components — `FractureProperties` / `FractureState` / `VisualMesh`

**Files:** `Engine/Components/FractureProperties.cs`, `FractureState.cs`, `VisualMesh.cs`

### FractureProperties
Immutable material description. Shared across entities of the same material via static presets (`FractureProperties.Rock`, `.Glass`, `.Ice`, `.Metal`). Controls how much energy is needed to fracture (`Toughness`), how the fracture propagates (`Brittleness`), and how fine the debris is (`MinFragmentArea`).

### FractureState
Per-entity mutable runtime state. Decoupled from `FractureProperties` so the immutable material can be a shared value. `AbsorbedEnergy` accumulates from sub-threshold hits and is reset to zero when fracture occurs. `FaultAngles` are pre-scored weak directions generated at spawn; they bias inner cut planes toward natural-looking fracture lines and are inherited (with `FaultCount--`) by sub-fragments.

### VisualMesh
Optional visual-only component. When present, the renderer draws `ConvexPieces` instead of the collision shape, allowing the visible surface to differ from the physics hitbox. Intended for showing accumulated blast-zone craters on an asteroid whose collision shape is still the original convex polygon. Not yet wired in the current demo.

> Full design: `docs/physics_spec.md §3`.
