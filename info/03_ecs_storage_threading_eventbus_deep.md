# Deep Dive: Event Bus Targeting, Multi-threaded Loop, and ECS Storage

---

## 1. The Event Bus: Does Every Entity See Every Event?

Short answer: every *subscriber* receives every event of the type it subscribed to, but that doesn't mean every entity gets called. Three layers of filtering exist.

### Layer 1: Type Filtering (Automatic)

The bus is typed. Subscribing to `CollisionEvent` means you never receive `ScoreChangedEvent`. The dispatch table routes by exact type, so this filtering is free and always applies.

### Layer 2: Content Filtering (Manual, in the handler)

Within a handler for `CollisionEvent`, you receive all collision events, then decide whether they involve you:

```csharp
// In a system that manages ship behavior:
bus.Subscribe<CollisionEvent>(e =>
{
    if (e.EntityA != shipEntity && e.EntityB != shipEntity)
        return;  // not my collision, ignore
    HandleShipCollision(e);
});
```

This is the standard pattern. It's not expensive — the handler runs once per event, and the check is a single integer comparison. Most handlers filter like this.

### Layer 3: Don't Use the Bus at All (Same-System Logic)

The event bus is for *cross-system* communication — when the producer and consumer belong to different, otherwise unrelated systems. If a single system already has access to both entities (because it queries for them), using the bus is the wrong tool. Just manipulate the components directly:

```csharp
// CombatSystem — already iterates bullets and asteroids
// No need for the bus here; it has direct access to both
class CombatSystem : ISystem
{
    public void Update(World world, double dt)
    {
        foreach (var contact in world.CollisionContacts)
        {
            bool isBulletVsAsteroid =
                world.HasComponent<BulletComponent>(contact.A) &&
                world.HasComponent<AsteroidComponent>(contact.B);

            if (isBulletVsAsteroid)
            {
                ref var health = ref world.GetComponent<Health>(contact.B);
                health.Current -= 10;
                world.AddComponent(contact.A, new DestroyTag());
            }
        }
    }
}
```

The bus is used when the *reaction* logically lives in a different system than the *cause* — for example, the AudioSystem reacting to a collision it didn't detect, the ScoreSystem reacting to an entity being destroyed by the CombatSystem. The bus decouples these.

### Targeted Events

Nothing prevents you from putting a target entity ID in an event:

```csharp
record DamageEvent(Entity Target, int Amount, Entity Source);
```

Subscribers check `if (e.Target != myEntity) return`. This is a common pattern for ability effects, projectile homing, and status effects — any situation where an interaction is clearly directed at one specific entity.

### Summary

| Interaction type | Right tool |
|---|---|
| System A causes something, System B (unrelated) reacts | EventBus |
| System already has access to both entities | Direct component mutation |
| Directed "do this to that specific entity" | EventBus with target ID in the event |
| All entities of a type should react | EventBus, subscribers filter by component presence |

---

## 2. Multi-threaded Game Loop

### The Baseline: Why Systems Can Parallelize

Within a single frame, each system reads component data from the *same state* (the state at the start of the frame) and writes results to components that no other concurrently-running system touches. This is the key insight: if two systems don't share any components — one reads/writes Transform+Velocity, another reads/writes AIState+Waypoint — they are completely independent and can run simultaneously.

### System Dependency Graph

Formalize the dependencies between systems as a directed acyclic graph (DAG). An edge A → B means "B must wait for A because B reads something A writes."

For each system, declare:
```csharp
class MovementSystem : ISystem
{
    public ComponentAccess[] Accesses => new[]
    {
        ComponentAccess.Write<Transform>(),
        ComponentAccess.Read<Velocity>(),
    };
}

class RenderSystem : ISystem
{
    public ComponentAccess[] Accesses => new[]
    {
        ComponentAccess.Read<Transform>(),
        ComponentAccess.Read<Sprite>(),
    };
}
```

Build the conflict matrix: two systems conflict if one writes a component the other reads or writes (the standard read/write hazard rules). Systems with no conflict path between them can run in parallel.

```
Frame structure (example):

InputSystem ──────────────────────────────┐
                                          │
AISystem ──────────────────────────┐      │
(reads Transform, writes AIState)  │      │
                                   ↓      ↓
PhysicsSystem → CollisionSystem → CombatSystem → RenderSystem
(writes Transform)  (reads Transform,   (reads Health)   (reads Transform,
                     writes contacts)                      reads Sprite)

ParticleSystem ──────────────────────────→
(reads ParticleEmitter, writes ParticleState)
```

`AISystem` and `ParticleSystem` have no shared components with `PhysicsSystem` — they can all run in the same wave. `RenderSystem` depends on `Transform` (written by Physics) so it runs in a later wave.

The scheduler groups systems into **waves** — sets of mutually non-conflicting systems — and submits each wave to a thread pool:

```csharp
foreach (var wave in systemScheduler.Waves)
{
    var tasks = wave.Select(system => Task.Run(() => system.Update(world, dt)));
    Task.WaitAll(tasks.ToArray());
    eventBus.Flush();  // safe to flush between waves
}
```

### Intra-System Parallelism (Chunk Parallelism)

Even a single system can parallelize internally. `MovementSystem` processes N entities — each entity's update is independent of the others. Use `Parallel.ForEach` or partition the work across threads:

```csharp
class MovementSystem : ISystem
{
    public void Update(World world, double dt)
    {
        var entities = world.Query<Transform, Velocity>();

        Parallel.ForEach(entities, entity =>
        {
            ref var pos = ref world.GetComponent<Transform>(entity);
            ref var vel = ref world.GetComponent<Velocity>(entity);
            pos.X += vel.Linear.X * (float)dt;
            pos.Y += vel.Linear.Y * (float)dt;
        });
    }
}
```

This requires that the system's component data is thread-safe to write — which it is when each entity maps to a distinct memory location (i.e., using flat arrays indexed by entity ID, not shared data structures).

### The Render Thread Separation

The most impactful threading optimization in practice is separating the update thread from the render thread. They run on independent cadences:

```
Thread 1 — Game Update:
  loop:
    update all systems (physics, AI, etc.)
    write state snapshot to shared buffer
    signal renderer: new frame ready

Thread 2 — Render:
  loop:
    wait for new frame signal
    read state snapshot
    draw to back buffer
    present
```

The snapshot is a lightweight copy of the renderable state (transforms + sprites). The render thread reads last frame's snapshot while the game thread is already computing the next frame. They never block each other.

For a C# WinForms engine, the game thread writes to an off-screen `Bitmap` (the back buffer). The UI thread (WinForms paint event) blits that `Bitmap` to the form. These operate on different buffers — no lock needed if you use a double-buffer swap.

### Double-Buffered Component Stores: No Conflicts at All

The most elegant approach to eliminating all write hazards: give every component type two buffers (a ping and a pong). All systems read from the previous frame's buffer and write to the current frame's buffer. After all systems finish, swap the buffers.

```
Frame N:
  MovementSystem: reads positions_prev[*], writes positions_curr[*]
  AISystem:       reads positions_prev[*], writes ai_state_curr[*]
  CombatSystem:   reads health_prev[*],    writes health_curr[*]
  (All three run fully in parallel — zero conflicts)

End of frame N:
  swap(positions_prev, positions_curr)
  swap(ai_state_prev,  ai_state_curr)
  swap(health_prev,    health_curr)
```

**Tradeoff:** Every state change has one frame of latency. If the player fires a bullet at frame N, the bullet's effects are visible at frame N+1. For Asteroids at 60fps this is 16ms — imperceptible. For a competitive fighting game with frame-perfect collision, it matters.

### What Not to Parallelize

Not everything benefits from parallelism:
- **Entity creation/destruction** — modifying the entity list while systems iterate it is unsafe. Always defer these operations.
- **EventBus dispatch** — subscribers may mutate shared state; run flush serially between waves.
- **Rendering** — GDI+ is not thread-safe; all draw calls must happen on one thread.
- **Audio system** — NAudio manages its own internal threading; call it serially from the game thread.

### Practical Starting Point

For this project, the pragmatic approach is:

1. **Start single-threaded** — correctness first.
2. **Add `Parallel.ForEach` inside MovementSystem and PhysicsSystem** — these are the hottest loops and trivially parallelizable.
3. **Separate the render thread** from the update thread — this gives the biggest perceived smoothness improvement.
4. **Add wave-based system scheduling** only if profiling shows it's needed.

---

## 3. ECS Storage: SoA, Sparse Sets, and Archetypes

### Conceptually: An Entity Is a Set of Components

An entity ID is just an integer. Conceptually, entity 42 "is" the set of all components that have been attached to it. The ID is just the key you use to find those components. There is no "entity object" in memory — only the ID and the scattered component data it indexes into.

### SoA: The Layout

Structure of Arrays means one flat array per component type, indexed by entity ID:

```
Entity IDs:    0    1    2    3    4    5   ...
positions[]:  p0   p1   p2   p3   p4   p5   ...
velocities[]: v0   v1   --   v3   --   v5   ...
healths[]:    --   --   h2   h3   --   --   ...
```

Entity 3 has all three components: `positions[3]`, `velocities[3]`, `healths[3]`.
Entity 4 has only `positions[4]`. Its velocity and health slots are unused.

**Yes, the entity is physically scattered across different arrays.** This is by design. A system that processes movement only touches `positions[]` and `velocities[]` — two sequential memory regions. It never loads health data into cache. This is the cache locality benefit.

**All arrays are the same length (MAX_ENTITIES).** This is the direct-indexing trade-off.

### The "None" Problem and Bitmask Solution

For struct components (which you want for cache efficiency), you can't use null. The standard solution is a **component presence bitmask** — a `ulong` per entity where each bit corresponds to one component type:

```csharp
ulong[] componentMask = new ulong[MAX_ENTITIES];

const ulong TRANSFORM_BIT = 1UL << 0;
const ulong VELOCITY_BIT  = 1UL << 1;
const ulong HEALTH_BIT    = 1UL << 2;
// ... up to 64 component types with ulong

bool HasTransform(int entityId) => (componentMask[entityId] & TRANSFORM_BIT) != 0;
```

Querying "all entities with Transform + Velocity" iterates `componentMask[]` looking for entries where both bits are set:

```csharp
for (int i = 0; i < MAX_ENTITIES; i++)
{
    if ((componentMask[i] & (TRANSFORM_BIT | VELOCITY_BIT)) == (TRANSFORM_BIT | VELOCITY_BIT))
    {
        ref var t = ref transforms[i];
        ref var v = ref velocities[i];
        // process
    }
}
```

This is extremely fast — the inner loop is two array reads, one AND, one comparison. The arrays are small structs; the CPU prefetcher handles sequential access well.

**Memory cost:** If you have 1000 component types but each entity only has 10, you waste ~99% of the array space for rarely-used components. For 64-component bitmasks with 10,000 entity slots, the overhead is manageable. For highly varied component sets at large scale, use sparse sets instead.

### Sparse Sets: Dense Data + O(1) Lookup

A sparse set gives you cache-friendly iteration *without* wasting memory on None slots. It uses two arrays per component type:

```
Sparse set for Velocity component:
  sparse[0..MAX_ENTITIES]: index into dense, or INVALID (-1)
  dense[0..count]:         packed list of entity IDs that have Velocity
  data[0..count]:          packed Velocity values, in same order as dense
```

```
Example — entities 3, 7, 42 have Velocity:
  sparse[3]  = 0     sparse[7]  = 1     sparse[42] = 2
  sparse[*]  = -1    (all others)
  dense  = [3, 7, 42]
  data   = [vel_of_3, vel_of_7, vel_of_42]
```

**Lookup entity 42's velocity:** `data[sparse[42]]` = `data[2]` — two array reads, O(1).

**Iterate all entities with Velocity:**
```csharp
for (int i = 0; i < velocitySet.Count; i++)
{
    int entityId = velocitySet.Dense[i];
    ref var vel = ref velocitySet.Data[i];
    // process
}
```
Sequential memory access. `data[]` is packed — no gaps, no None values. Only entities that actually have Velocity are visited.

**Add Velocity to entity 99:**
```csharp
velocitySet.Dense[velocitySet.Count] = 99;
velocitySet.Data[velocitySet.Count]  = new Velocity { ... };
velocitySet.Sparse[99] = velocitySet.Count;
velocitySet.Count++;
```

**Remove Velocity from entity 7:**  
Swap entity 7 with the last element (42 → position 1), update sparse[42] = 1, sparse[7] = INVALID, decrement count. O(1) removal with no holes in the dense array.

Sparse sets are used by **EnTT** (the C++ ECS library used in many indie and AA games) and are considered the gold standard for dynamic ECS workloads.

### Archetype Storage: The Unity DOTS Approach

Group entities by their exact combination of components — their **archetype**. All entities of the same archetype are stored together in a compact table.

```
Archetype [Transform, Velocity, Sprite]:
  entities: [3, 7, 12, ...]
  transform[]: [t3, t7, t12, ...]
  velocity[]:  [v3, v7, v12, ...]
  sprite[]:    [s3, s7, s12, ...]

Archetype [Transform, Sprite]:         (no velocity — static objects)
  entities: [1, 9, 44, ...]
  transform[]: [t1, t9, t44, ...]
  sprite[]:    [s1, s9, s44, ...]
```

A query for `Transform + Velocity` iterates exactly one archetype table (or a small set if multiple archetypes match). The data is perfectly packed — maximum cache efficiency.

**The cost:** Adding or removing a component from an entity changes its archetype, requiring a **move operation** — copy all existing components from the old table to the new table, O(number of components on the entity). This is why archetype ECS is fast for stable compositions and slow for entities that constantly add/remove components.

### Comparison Table

| Strategy | Lookup | Iteration | Memory | Add/Remove | Best for |
|---|---|---|---|---|---|
| Dict<Entity, object> | O(1) avg | O(n) with boxing | Low (only stores what exists) | O(1) | Learning / prototyping |
| Direct arrays + bitmask | O(1) | O(MAX_ENTITIES) with skips | Wastes unused slots | O(1) | Small fixed entity count |
| Sparse sets | O(1) | O(n) dense, cache-friendly | Only stores what exists | O(1) | Dynamic entities, varied components |
| Archetype tables | O(1) w/ archetype lookup | O(n) perfectly dense | Only stores what exists | O(components) on archetype change | Large counts, stable compositions |

### Recommendation for This Engine

Use **sparse sets**. They give you:
- O(1) lookup (needed for event handlers that receive an entity ID and need its components)
- Cache-friendly dense iteration (needed for systems like MovementSystem that touch all entities)
- No wasted memory (entities only consume space for components they actually have)
- O(1) add/remove (Asteroids creates and destroys bullets and asteroids frequently)

Design the `World` API to hide which storage strategy is used. Start with the dictionary approach if you want to get something running quickly, then swap in sparse sets behind the same interface once the API is stable.

```csharp
// The World API stays the same regardless of internal storage
world.AddComponent<Velocity>(entity, new Velocity { Linear = dir * speed });
ref var vel = ref world.GetComponent<Velocity>(entity);
foreach (var e in world.Query<Transform, Velocity>()) { ... }
```

---

## Sources

- **EnTT documentation — Sparse Sets**: https://github.com/skypjack/entt/wiki/Crash-Course:-entity-component-system  
  The canonical explanation of sparse sets in ECS, by the author of one of the most-used ECS libraries.

- **ECS FAQ — Storage strategies**: https://github.com/SanderMertens/ecs-faq#what-are-the-different-ways-to-implement-an-ecs  
  Side-by-side comparison of archetypes, sparse sets, and bitset approaches.

- **Our Machinery Blog — Data structures for ECS**: https://ruby0x1.github.io/machinery_blog_archive/  
  Deep technical articles on ECS storage trade-offs from a professional game engine team.

- **Unity DOTS — Archetype documentation**: https://docs.unity3d.com/Packages/com.unity.entities@latest/manual/concepts-archetypes.html  
  Unity's explanation of the archetype model with diagrams.

- **Game Engine Architecture** — Jason Gregory, Chapter 15 (Gameplay Systems): ISBN 978-1138035454  
  Covers multi-threaded system scheduling and component storage in production engines.

- **Parallelism in ECS** — Sander Mertens: https://ajmmertens.medium.com/why-storing-state-machines-in-ecs-is-a-bad-idea-742de7a18e59  
  Discusses system scheduling, read/write hazards, and threading models in ECS.
