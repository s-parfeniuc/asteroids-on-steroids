# Engine Architecture, ECS, and the Event Bus

---

## Part 1: The Engine as a Collection of Subsystems

A game engine is not a monolith — it is a set of cooperating subsystems, each with a single clear responsibility. Understanding the boundaries between them is the most important architectural skill you can develop, because most bugs and design headaches come from blurred boundaries: a rendering concern leaking into game logic, physics being driven from inside an entity's `Update`, and so on.

Here is the full set of subsystems in a reasonably complete 2D engine:

```
┌─────────────────────────────────────────────────────────┐
│                        GameLoop                         │
│  (orchestrates all subsystems each frame)               │
└────┬──────────┬──────────┬────────────┬────────────┬────┘
     │          │          │            │            │
 InputSys   Physics    Collision   RenderSys   AudioSys
             System     System
               │          │
           ──────────────────
           │  ECS World     │
           │  (entities,    │
           │  components,   │
           │  systems)      │
           ──────────────────
                   │
               EventBus
           (horizontal, used
            by all subsystems)
```

The **ECS World** is the data layer — it stores all entity/component state. The subsystems (Physics, Collision, Rendering, etc.) are **Systems** in ECS terms: they read and write components each frame. The **EventBus** sits horizontally, available to all systems and entities, handling cross-system communication without creating dependencies.

---

### Subsystem Responsibilities

#### GameLoop
- Owns the thread and the timing (`Stopwatch`)
- Orchestrates the frame: input → update → physics → collision → render → audio flush
- Enforces frame rate cap
- Knows about all subsystems; all other subsystems know nothing about each other

**The update order within a frame matters.** A typical order:
1. `InputSystem.BeginFrame()` — snapshot key state
2. `EventBus.Flush()` — dispatch any queued events from last frame
3. `[User systems].Update(dt)` — game logic, AI, movement
4. `PhysicsSystem.Step(dt)` — integrate velocities, apply forces
5. `CollisionSystem.Detect()` — find contacts, resolve, emit collision events
6. `EventBus.Flush()` — dispatch collision events immediately
7. `AnimationSystem.Update(dt)` — advance animation frames
8. `RenderSystem.Draw()` — collect drawables and render
9. `AudioSystem.Update()` — start/stop/update playing sounds
10. `InputSystem.EndFrame()` — clear pressed/released sets

This order is not arbitrary. Physics must run before collision (otherwise you're detecting against last frame's positions). Collision must run before render (so you see the resolved positions). Input must snapshot before user systems run (so all systems see the same input state for this frame).

#### InputSystem
- Subscribes to OS key/mouse events (WinForms events wired once at startup)
- Maintains: `held` (keys down this frame), `pressed` (went down this frame), `released` (went up this frame)
- `pressed` and `released` sets are cleared at the end of each frame — they are only true for exactly one frame
- Exposes polling API to game systems: `Input.IsHeld(Keys.A)`, `Input.IsPressed(Keys.Space)`
- Never calls game logic directly; game logic polls it

#### PhysicsSystem
- Operates on entities that have `Transform` + `RigidBody` components
- Applies forces and integrates: `velocity += force/mass * dt`, `position += velocity * dt`
- Applies drag/damping
- Does NOT detect collisions — that is the CollisionSystem's job
- May apply gravity as a global force
- For Asteroids-style games, this is simple Euler integration. For platformers, you'd add a more sophisticated integrator and constraints.

#### CollisionSystem
- Operates on entities with `Transform` + `Collider` components
- Runs broad phase (spatial index), then narrow phase (SAT / circle tests)
- On contact: publishes `CollisionEvent { EntityA, EntityB, ContactInfo }` to EventBus
- Does NOT decide what happens on collision — that is game logic's job, via the event
- Optionally resolves overlap (pushes objects apart) if entities have a `RigidBody`
- Rebuilds/updates spatial index each frame before running queries

#### RenderSystem
- Operates on entities with `Transform` + `Sprite` (or `Drawable`) components
- Collects all drawables, sorts by layer/depth
- Applies camera transform (viewport offset and zoom)
- Draws to the back buffer, then presents
- Does NOT know what entities are — it just sees "draw this image at this transform"

#### AudioSystem
- Maintains a pool of audio channels
- Listens for `PlaySoundEvent` from the EventBus (or exposes direct API)
- Handles: start/stop, volume, pitch, pan, looping
- Music is streamed (file read in chunks); effects are fully buffered in memory

#### ResourceManager
- Loads and caches assets (images, sounds, fonts, tilemaps, shader data)
- Lazy-loads on first request; returns the same instance on subsequent requests
- Exposes a typed API: `resources.Get<Bitmap>("ship")`
- Called by RenderSystem and AudioSystem — never called from entity logic

#### SceneManager / StateStack
- Manages which game states are active (Playing, Paused, MainMenu, etc.)
- Owns the ECS World for each scene (or resets a shared world on scene transition)
- Handles transitions (fade, slide) between states

#### Camera
- Defines the viewport: position, zoom, rotation
- RenderSystem applies the camera transform to all world-space drawables
- Game logic moves the camera (follow player, cutscenes)
- Can support multiple cameras (split screen, mini-map)

---

## Part 2: The Event Bus

### The Core Problem

Consider a bullet hitting an asteroid. Who should respond?
- The **score** should increase
- The **asteroid** should split or die
- A **sound effect** should play
- A **particle explosion** should spawn
- The **combo meter** should tick

If the bullet directly calls all of these, it needs to know about every system that cares about it. Worse, every time you add a new reaction (screen shake? achievement unlock?), you must modify the bullet's code. This is called **shotgun surgery** — a single logical event requires changes across many classes.

The event bus inverts this: the bullet fires an event and knows nothing about what reacts to it.

### The Architecture

```
Publisher                EventBus               Subscriber
─────────               ─────────              ─────────
bullet.Destroy()  →  Publish(BulletHitEvent)
                         │
                         ├──→  ScoreSystem.OnBulletHit(e)
                         ├──→  AudioSystem.OnBulletHit(e)
                         ├──→  ParticleSystem.OnBulletHit(e)
                         └──→  AchievementSystem.OnBulletHit(e)
```

### Event Types

Events are plain data classes — they carry information, no logic:

```csharp
// Events are just data. No methods, no logic.
record CollisionEvent(Entity EntityA, Entity EntityB, ContactInfo Contact);
record EntityDestroyedEvent(Entity Entity, string Tag);
record ScoreChangedEvent(int OldScore, int NewScore);
record GameStateChangedEvent(IGameState Previous, IGameState Next);
```

Using C# `record` types is ideal: they're immutable, value-comparable, and require minimal boilerplate.

### Implementation

A type-safe event bus in C# uses `Dictionary<Type, List<Delegate>>`:

```csharp
class EventBus
{
    // Maps event type → list of handler delegates
    private Dictionary<Type, List<object>> subscribers = new();

    public void Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        if (!subscribers.ContainsKey(type))
            subscribers[type] = new List<object>();
        subscribers[type].Add(handler);
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        if (subscribers.TryGetValue(type, out var list))
            list.Remove(handler);
    }

    public void Publish<T>(T evt)
    {
        var type = typeof(T);
        if (subscribers.TryGetValue(type, out var list))
            foreach (var handler in list)
                ((Action<T>)handler).Invoke(evt);
    }
}
```

### Synchronous vs. Deferred Dispatch

The simple `Publish` above is **synchronous** — subscribers are called immediately when the event is published. This is simple but dangerous: if a collision event is published during the collision detection loop, and the subscriber destroys an entity, you're modifying the entity list while iterating it. This causes bugs that are hard to track down.

**Deferred dispatch** queues events and dispatches them at a safe point (typically the beginning of the next frame, or at an explicit flush point):

```csharp
class EventBus
{
    private Dictionary<Type, List<object>> subscribers = new();
    private Queue<object> pendingEvents = new();  // deferred queue
    private Queue<Type>   pendingTypes   = new();

    // Queue for later
    public void Publish<T>(T evt)
    {
        pendingEvents.Enqueue(evt);
        pendingTypes.Enqueue(typeof(T));
    }

    // Called at a safe point in the game loop
    public void Flush()
    {
        while (pendingEvents.Count > 0)
        {
            var evt  = pendingEvents.Dequeue();
            var type = pendingTypes.Dequeue();
            if (subscribers.TryGetValue(type, out var list))
                foreach (var handler in list)
                    ((dynamic)handler)(evt);  // type-safe via dynamic dispatch
        }
    }

    // For truly immediate events (use sparingly)
    public void PublishImmediate<T>(T evt) { ... }
}
```

**Rule of thumb:** use deferred dispatch by default. Use immediate only for events that must be processed within the same frame tick (e.g., input consumed events where the first subscriber "claims" the input).

### Subscription Lifetime Management

A subtle but important problem: if a subscriber is destroyed (entity is removed from the world) but is still registered in the bus, the bus holds a reference to it — preventing garbage collection and causing calls to dead objects. You must unsubscribe when an entity or system is deactivated.

Two patterns to handle this:

**Manual unsubscription:**
```csharp
// In entity's Initialize
bus.Subscribe<CollisionEvent>(OnCollision);

// In entity's Destroy — easy to forget!
bus.Unsubscribe<CollisionEvent>(OnCollision);
```

**Subscription tokens (more robust):**
```csharp
// Subscribe returns a token
var token = bus.Subscribe<CollisionEvent>(OnCollision);

// Disposing the token unsubscribes automatically
token.Dispose();  // or use in a 'using' block
```

**Weak references (automatic, but complex):**
Store subscribers as `WeakReference<Action<T>>`. Dead subscribers are automatically skipped and removed. Harder to implement correctly but removes the manual lifecycle burden.

For your engine, manual unsubscription called from entity `Destroy()` is the simplest correct approach.

---

## Part 3: Entity Component System (ECS)

### Why OOP Inheritance Fails for Games

A traditional object-oriented entity hierarchy seems natural at first:

```
GameObject
├── DrawableObject
│   ├── Sprite
│   │   ├── AnimatedSprite
│   │   │   ├── Enemy
│   │   │   │   ├── FlyingEnemy     ← needs physics
│   │   │   │   └── GroundEnemy     ← needs pathfinding
│   │   │   └── Player
│   │   └── Bullet
│   └── TileMap
└── InvisibleObject
    └── Trigger
```

The problem emerges when game design demands combinations that don't fit the hierarchy:
- An enemy that can fly **and** cast spells — do you inherit from `FlyingEnemy` and `SpellCaster`? C# doesn't support multiple inheritance.
- A chest that is also destructible and can be possessed by the player's magic — now the hierarchy gets absurd.
- A door that has a sprite, collision, and plays a sound when opened — you end up with a `Door` class that inherits six levels deep just to get the right mix of features.

This is the **fragile base class problem**: fat base classes accumulate every possible feature, most objects carry data they don't use, and adding new feature combinations requires modifying the hierarchy.

### The ECS Solution

ECS decouples **identity**, **data**, and **behavior**:

| Concept | What it is | Example |
|---|---|---|
| **Entity** | A unique ID. Nothing more. | `Entity 42` |
| **Component** | A plain data struct attached to an entity | `Position { x=100, y=50 }` |
| **System** | A function that processes all entities with a specific set of components | `MovementSystem` processes entities with `Position + Velocity` |

An entity is whatever components it has. A "flying enemy that casts spells" is just an entity with `Position`, `Velocity`, `Sprite`, `Health`, `AIBehavior`, and `SpellCaster` components attached to the same ID. No inheritance required.

```
Entity 42
  └── Position    { x=100, y=200 }
  └── Velocity    { dx=5.0, dy=0 }
  └── Sprite      { image="ship", layer=1 }
  └── Health      { hp=100, maxHp=100 }
  └── Collider    { shape=Circle(radius=16) }
  └── InputTag    { }        ← just a tag, no data
```

```
Entity 87
  └── Position    { x=300, y=150 }
  └── Velocity    { dx=-2.0, dy=1.5 }
  └── Sprite      { image="asteroid_large", layer=1 }
  └── Health      { hp=3, maxHp=3 }
  └── Collider    { shape=Polygon([...]) }
  └── Asteroid    { size=Large }
```

### Components: Pure Data

Components should contain **only data** — no logic, no methods (except maybe simple constructors). This is the most important discipline to maintain in ECS.

```csharp
// Good: pure data
struct Position   { public float X, Y; }
struct Velocity   { public float Dx, Dy; }
struct Health     { public int Current, Max; }
struct Sprite     { public string ImageId; public int Layer; }
struct Collider   { public CollisionShape Shape; }

// Tag components (zero data, used as flags)
struct PlayerTag  { }
struct DestroyTag { }   // mark for removal this frame
```

If you find yourself wanting to put a method on a component, it's almost always a sign that logic belongs in a system instead.

### Systems: Pure Logic

A system queries the ECS world for entities that have a specific set of components, then processes each one.

```csharp
class MovementSystem : ISystem
{
    public void Update(World world, float dt)
    {
        // Query: give me all entities with both Position and Velocity
        foreach (var (pos, vel) in world.Query<Position, Velocity>())
        {
            pos.X += vel.Dx * dt;
            pos.Y += vel.Dy * dt;
        }
    }
}

class RenderSystem : ISystem
{
    ResourceManager resources;

    public void Draw(World world, Graphics g)
    {
        // Query: entities with Position and Sprite, sorted by layer
        foreach (var (pos, sprite) in world.Query<Position, Sprite>().OrderBy(s => s.sprite.Layer))
        {
            var image = resources.Get<Bitmap>(sprite.ImageId);
            g.DrawImage(image, pos.X, pos.Y);
        }
    }
}
```

Systems are stateless with respect to entities — they carry no entity data themselves. This makes them trivially testable: create a world, add some test entities, run the system, assert on the resulting component values.

### The World: The ECS Container

The `World` (sometimes called `Registry` or `Scene`) is the central data store. It:
- Allocates and recycles entity IDs
- Stores components, indexed by type and entity ID
- Provides the query interface for systems
- Handles deferred entity/component creation and destruction

```csharp
class World
{
    // Create an entity and return its ID
    public Entity CreateEntity();

    // Add a component to an entity
    public void AddComponent<T>(Entity e, T component) where T : struct;

    // Get a component (returns ref for mutation)
    public ref T GetComponent<T>(Entity e) where T : struct;

    // Check if entity has a component
    public bool HasComponent<T>(Entity e) where T : struct;

    // Remove a component
    public void RemoveComponent<T>(Entity e) where T : struct;

    // Destroy an entity (removes all its components)
    public void DestroyEntity(Entity e);

    // Query: returns all entities that have ALL of the listed component types
    public IEnumerable<(T1, T2)> Query<T1, T2>() where T1 : struct where T2 : struct;

    // Flush deferred operations (called at a safe point each frame)
    public void FlushDeferred();
}
```

### Deferred Operations in ECS

The same problem as the event bus: you cannot destroy an entity while a system is iterating over a query that includes it. The solution is to defer:

```csharp
// During system update:
world.DestroyDeferred(entity);  // marks for destruction, doesn't destroy yet

// At end of frame / safe point:
world.FlushDeferred();  // actually destroys marked entities
```

Similarly, components added during a query iteration become visible in the next frame's query, not the current one.

### Component Storage: The Memory Layout Question

This is where ECS really shines for performance, and it's worth understanding even if your first implementation won't optimize it.

**Naive approach — Dictionary per component type:**

```csharp
Dictionary<Entity, Position>  positions;
Dictionary<Entity, Velocity>  velocities;
Dictionary<Entity, Sprite>    sprites;
```

This works but is cache-unfriendly. When `MovementSystem` iterates positions, the `Position` values are scattered across memory wherever the dictionary stored them.

**Array-of-Structs (AoS) approach:**

Store components of the same type in a contiguous array, indexed by entity ID:

```csharp
Position[] positions = new Position[MAX_ENTITIES];
Velocity[] velocities = new Velocity[MAX_ENTITIES];
```

Now iterating all positions is a sequential memory scan — extremely cache-friendly. The MovementSystem reads `positions[0], positions[1], positions[2]...` in a tight loop with no cache misses. Modern CPUs prefetch sequential memory; scattered accesses defeat the prefetcher.

**Archetype-based storage (used by Unity DOTS, Bevy):**

Group entities by their exact set of component types (their "archetype"). All entities with the same archetype are stored in a compact table together.

```
Archetype [Position, Velocity, Sprite]:
  pos:  [100, 200, 50, ...]
  vel:  [5.0, -3.0, 2.0, ...]
  sprite: [ship, asteroid, bullet, ...]

Archetype [Position, Sprite]:      (static objects — no velocity)
  pos:  [300, 400, ...]
  sprite: [wall, pillar, ...]
```

Queries iterate one archetype table at a time — sequential memory access with no branching. Adding or removing a component moves the entity to a different archetype table. This is the most cache-optimal approach known for game engines.

For your C# engine, start with the dictionary approach (correctness first), then consider moving to flat arrays if you observe performance issues.

### System Ordering and Dependencies

Systems must run in a defined order each frame. Some orderings are mandatory (you can't render before moving), others are configurable. Declare dependencies explicitly:

```csharp
[RunAfter(typeof(MovementSystem))]
[RunAfter(typeof(PhysicsSystem))]
class CollisionSystem : ISystem { ... }

[RunAfter(typeof(CollisionSystem))]
class RenderSystem : ISystem { ... }
```

The engine topologically sorts systems at startup based on these declarations. This replaces the fragile "manually call systems in the right order in the game loop" approach.

### Queries: The ECS API

A query is the primary way systems access data. It returns only entities that have **all** specified component types:

```csharp
// Two-component query
foreach (var (pos, vel) in world.Query<Position, Velocity>())
{
    pos.X += vel.Dx * dt;
}

// With optional component (has Position + Velocity, optionally has Health)
foreach (var (pos, vel, health) in world.QueryWithOptional<Position, Velocity, Health>())
{
    // health may be null
}

// Filtered query (has Position + Sprite, does NOT have DestroyTag)
foreach (var (pos, sprite) in world.Query<Position, Sprite>().Excluding<DestroyTag>())
{
    ...
}
```

Queries are the declarative heart of ECS: a system says *what data it needs*, not *which entities to look at*. This is fundamentally different from OOP where you hold a list of specific objects.

### Tags: Zero-Data Components

A tag is a component with no data fields. It acts as a flag or marker. Common uses:

```csharp
struct PlayerTag   { }   // exactly one entity has this; InputSystem queries for it
struct EnemyTag    { }   // CollisionSystem uses this to identify sides
struct DestroyTag  { }   // marks entity for removal this frame
struct GodModeTag  { }   // physics system skips entities with this
```

Tags are checked in queries just like data components:
```csharp
// Find the player entity
var (pos, vel) = world.Query<Position, Velocity, PlayerTag>().Single();
```

### Prefabs: Entity Templates

Creating an asteroid every time it splits requires repeating the same `AddComponent` calls. A **prefab** is a template that defines a set of components and their default values:

```csharp
class AsteroidPrefab
{
    public static Entity Instantiate(World world, Vector2 position, AsteroidSize size)
    {
        var e = world.CreateEntity();
        world.AddComponent(e, new Position { X = position.X, Y = position.Y });
        world.AddComponent(e, new Velocity { Dx = Random(-2f, 2f), Dy = Random(-2f, 2f) });
        world.AddComponent(e, new Collider { Shape = CircleShape(RadiusForSize(size)) });
        world.AddComponent(e, new Sprite  { ImageId = SpriteForSize(size), Layer = 1 });
        world.AddComponent(e, new Health  { Current = HpForSize(size) });
        world.AddComponent(e, new Asteroid { Size = size });
        return e;
    }
}

// Usage
AsteroidPrefab.Instantiate(world, hitPosition, AsteroidSize.Medium);
```

---

## Part 4: How ECS and the Event Bus Work Together

ECS and the event bus serve different communication needs:

| Need | Mechanism |
|---|---|
| System A reads data System B produced | Component — B writes it, A reads it next frame |
| Something happened and multiple unrelated systems need to react | EventBus — publish once, N subscribers react |
| One entity needs to change a specific component on another entity | Direct component mutation via World (if the system manages both) |
| One-time lifecycle signals (entity created, destroyed, scene loaded) | EventBus |
| Per-frame data sharing between systems | Components |

**Example: Bullet-Asteroid Collision**

1. `CollisionSystem` runs, detects `BulletEntity` overlapping `AsteroidEntity`
2. `CollisionSystem` publishes (deferred): `CollisionEvent { BulletEntity, AsteroidEntity, contact }`
3. `EventBus.Flush()` is called at end of frame
4. `CombatSystem` subscriber receives event → sets `Health.Current -= 10` on the asteroid, marks bullet with `DestroyTag`
5. `AsteroidSystem` subscriber receives event → if `Health.Current <= 0`, spawns two smaller asteroids, marks original with `DestroyTag`
6. `AudioSystem` subscriber receives event → plays "explosion" sound
7. `ParticleSystem` subscriber receives event → spawns particle emitter at contact point
8. `World.FlushDeferred()` → all `DestroyTag` entities are removed

Notice: the bullet code never mentions asteroids. The asteroid code never mentions bullets. The audio system never mentions either. They are completely decoupled, reacting to the same event through the bus.

---

## Part 5: Practical Implementation Notes for C#

### Entity as a Struct

An entity is just an ID. A common pattern adds a version number to detect stale references:

```csharp
readonly struct Entity : IEquatable<Entity>
{
    public readonly int Id;
    public readonly int Version;  // incremented when ID is recycled

    public bool IsValid => Id > 0;
    public static readonly Entity Null = new(0, 0);
}
```

When entity 42 is destroyed and its ID is recycled for a new entity, the version increments from 1 to 2. Any old `Entity { Id=42, Version=1 }` reference is now stale — the world returns null for it. This prevents "ghost entity" bugs.

### Generics and Reflection for Component Storage

In C#, the simplest correct implementation uses a dictionary keyed by component type and entity ID:

```csharp
// One dictionary per component type, lazily created
Dictionary<Type, Dictionary<Entity, object>> storage = new();

public void AddComponent<T>(Entity e, T component) where T : struct
{
    var type = typeof(T);
    if (!storage.ContainsKey(type))
        storage[type] = new Dictionary<Entity, object>();
    storage[type][e] = component;
}

public ref T GetComponent<T>(Entity e) where T : struct
{
    // Note: can't return ref to boxed value — this is a limitation of the naive approach
    // For mutable access, use GetComponent + SetComponent, or use arrays
}
```

The limitation: `Dictionary<Entity, object>` boxes structs, which defeats the performance advantages of structs. For a learning project this is fine. For a performant engine, use `Dictionary<Type, Array>` with typed arrays per component type and index by entity ID.

### The Query Implementation

A minimal query over two component types:

```csharp
public IEnumerable<(T1, T2)> Query<T1, T2>() where T1 : struct where T2 : struct
{
    if (!storage.TryGetValue(typeof(T1), out var store1)) yield break;
    if (!storage.TryGetValue(typeof(T2), out var store2)) yield break;

    // Iterate the smaller set
    foreach (var (entity, comp1) in store1)
    {
        if (store2.TryGetValue(entity, out var comp2))
            yield return ((T1)comp1, (T2)comp2);
    }
}
```

This is O(n) in the smaller component set, which is correct and efficient.

### Integrating with WinForms

WinForms owns the main thread (the UI thread). Your game loop runs on a background thread. The only point of contact is the render step, which must call `form.Invalidate()` or draw to a `Bitmap` that the UI thread then blits.

The clean pattern: the game loop draws to an off-screen `Bitmap` (the back buffer). The WinForms `Form.OnPaint` event handler simply copies that `Bitmap` to the form's surface using `e.Graphics.DrawImage`. This way the game loop never touches UI thread objects directly except for one thread-safe `Invalidate()` call.

```
Game Thread                          UI Thread
───────────                          ─────────
Update(dt)
Draw → render to backBufferBitmap
Interlocked.Exchange(frontBuffer, backBufferBitmap)
form.Invalidate()          →→→    OnPaint: g.DrawImage(frontBuffer, 0, 0)
```

---

## Sources

- **Game Programming Patterns — Component**: https://gameprogrammingpatterns.com/component.html  
  Nystrom's explanation of why inheritance fails and how components solve it.

- **Game Programming Patterns — Event Queue**: https://gameprogrammingpatterns.com/event-queue.html  
  Deferred dispatch, why synchronous events are dangerous mid-frame.

- **ECS FAQ** — Sander Mertens: https://github.com/SanderMertens/ecs-faq  
  The most comprehensive resource on ECS concepts, storage strategies, and trade-offs.

- **Overwatch's ECS talk** — Timothy Ford, GDC 2017: https://www.gdcvault.com/play/1024001/Overwatch-Gameplay-Architecture-and-Scaling  
  How Blizzard built a production ECS for a competitive shooter. Dense but extremely valuable.

- **Bevy ECS documentation**: https://bevyengine.org/learn/book/getting-started/ecs/  
  Bevy (Rust game engine) has one of the clearest ECS introductions available. Language doesn't matter — the concepts transfer directly.

- **Unity DOTS documentation**: https://docs.unity3d.com/Packages/com.unity.entities@latest  
  Unity's archetype-based ECS. Good reference for the archetype storage model.

- **Data-Oriented Design** — Richard Fabian (free online): https://www.dataorienteddesign.com/dodbook/  
  The book behind the cache-locality arguments for ECS over OOP.

- **Fix Your Timestep!** — Glenn Fiedler: https://gafferongames.com/post/fix_your_timestep/  
  System update ordering and fixed vs. variable timestep, essential companion to understanding the game loop section above.
