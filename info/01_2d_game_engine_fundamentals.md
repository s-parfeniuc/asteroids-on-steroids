# 2D Game Engine Fundamentals

## What is a Game Engine?

A game engine is a software framework that abstracts the repetitive, low-level work common to every game — drawing pixels, reading input, tracking time, detecting collisions — so that game developers can focus on game logic instead of infrastructure. Think of it as the "operating system" of a game: it manages resources and provides services that the game code calls into.

A game engine is not the game itself. The engine is the platform; the game is the application running on top of it. This distinction is critical to the architecture of your project: `Engine/` knows nothing about Asteroids, and `Game/` knows nothing about how pixels are drawn.

---

## Core Responsibilities of a 2D Game Engine

### 1. The Game Loop

The most fundamental concept. The engine owns a loop that runs continuously for the lifetime of the application:

```
initialize()
while (running):
    processInput()
    update(deltaTime)
    render()
    sleep(remainingFrameTime)
```

This loop is the heartbeat of everything. Each iteration is called a **frame**. The engine is responsible for:
- Measuring the time elapsed between frames (**delta time**, often abbreviated `dt`)
- Calling update logic with that `dt` so game speed is frame-rate independent
- Triggering a render pass after each update

**Why delta time matters:** If your ship moves 5 pixels per frame and the game runs at 30fps, it moves 150px/s. At 60fps, it moves 300px/s — the game is twice as fast on a faster machine. Using `dt` (in seconds) fixes this: move `speed * dt` pixels per frame, and the ship always moves `speed` pixels per second regardless of frame rate.

**Frame rate independence formula:**
```
position += velocity * deltaTime
```

### 2. Rendering / Drawing System

The engine abstracts all drawing operations. Responsibilities include:
- Clearing the screen each frame (prevents "ghosting" of previous frames)
- Drawing all visible entities in the correct order (painter's algorithm: back to front)
- Managing the graphics context (the surface you draw onto)
- Preventing flicker via **double buffering**

**Double buffering:** Instead of drawing directly to the visible screen (which causes tearing/flicker because the monitor scans line by line while you're still drawing), you draw to a hidden back buffer. Once the frame is fully composed, you **swap** (or blit) the back buffer to the screen in one atomic operation.

**Coordinate system:** In most 2D engines and UI frameworks, the origin `(0,0)` is the **top-left** corner, X increases right, Y increases down. This is the opposite of mathematical convention (where Y goes up). You must keep this in mind for rotation and physics.

### 3. Entity / Object Model

The engine defines a base unit of "thing in the world" — usually called `GameObject`, `Entity`, or `Sprite`. Every game object has at minimum:
- **Position** (x, y)
- **Update(dt)** method — called each frame to advance logic
- **Draw(graphics)** method — called each frame to render itself

The engine maintains a **scene** or **world** — a list of active entities. Each frame, it iterates the list and calls `Update` then `Draw` on each one. This is the **update/draw cycle**.

Common properties on a game object:
- `Velocity` (dx, dy) — how much position changes per second
- `Rotation` (angle in degrees or radians)
- `IsAlive` / `IsActive` — flags to mark objects for removal
- `Bounds` / `Radius` — used for collision detection
- `Tag` or type enum — to identify what kind of object it is

### 4. Input Handling

The engine captures raw input events from the OS and exposes them to game code in a clean, polling-friendly way.

**Two models of input:**

**Event-driven (bad for games):**  
The game reacts to `KeyDown` / `KeyUp` events directly in the event handler. Problem: if the key is held down, you only get one `KeyDown` event (or at the OS repeat rate, which is too slow for games).

**Polling (correct for games):**  
The engine maintains a `HashSet<Key>` of currently pressed keys. On `KeyDown`, add the key; on `KeyUp`, remove it. Each frame, game logic polls: `if (IsKeyHeld(Keys.Left)) ship.RotateLeft(dt)`. This gives smooth, frame-accurate input.

The engine may also expose:
- `IsKeyPressed(key)` — true only on the first frame the key goes down (for one-shot actions like firing)
- `IsKeyReleased(key)` — true only on the frame the key comes up
- Mouse position and button state

### 5. Collision Detection

The engine provides utilities to test whether two objects overlap. For a 2D game like Asteroids, the most common approaches are:

**Circle-circle collision (simplest, used in Asteroids):**
Two objects collide if the distance between their centers is less than the sum of their radii.
```
float dx = a.X - b.X;
float dy = a.Y - b.Y;
float distance = Math.Sqrt(dx*dx + dy*dy);
bool colliding = distance < (a.Radius + b.Radius);
```
Optimization: compare `distance²` to `(a.Radius + b.Radius)²` to avoid the expensive `Sqrt`.

**AABB (Axis-Aligned Bounding Box):**
Rectangle overlap test. Fast and simple, but inaccurate for rotated objects.
```
bool colliding = a.X < b.X + b.Width &&
                 a.X + a.Width > b.X &&
                 a.Y < b.Y + b.Height &&
                 a.Y + a.Height > b.Y;
```

The engine typically does **broad-phase** (quickly eliminate pairs that can't possibly collide) then **narrow-phase** (accurate test on remaining candidates). For a small game like Asteroids, brute-force O(n²) pair checking is fine.

### 6. State Management

Games have distinct modes: main menu, playing, paused, game over. The engine (or game layer) manages which state is active and routes update/draw calls accordingly.

The classic pattern is a **state machine**:
```
enum GameState { MainMenu, Playing, Paused, GameOver }
GameState currentState;

void Update(dt):
    switch (currentState):
        case Playing: updateGame(dt); break;
        case Paused:  updatePauseMenu(dt); break;
        ...
```

More sophisticated engines use a **state stack** (push "PauseMenu" on top of "Playing", pop it to resume) but for Asteroids a simple enum is sufficient.

### 7. Resource / Memory Management

The engine manages assets (images, sounds, fonts) so they are:
- Loaded once, not per-frame
- Released when no longer needed

For a minimal Asteroids engine using only GDI+ drawing (no image files), this mostly means managing `Brush`, `Pen`, and `Font` objects — GDI+ resources that must be explicitly disposed, otherwise you leak OS handles.

**Key rule in C# with GDI+:** Any `IDisposable` graphics resource (`Pen`, `Brush`, `Font`, `Bitmap`, `Graphics`) must be disposed after use, preferably with a `using` block.

---

## What Features Should the Engine Expose to the User (Game Developer)?

A well-designed engine exposes a clean API surface and hides complexity. For your project:

| Feature | Engine provides | Game code calls |
|---|---|---|
| Loop management | Starts/stops the thread, measures dt | `OnUpdate(dt)`, `OnDraw(g)` virtual methods |
| Drawing | Manages Graphics context, clears screen, double buffer | `DrawCircle(x, y, r)`, `DrawPolygon(points)` |
| Input | Maintains key state set | `Input.IsHeld(Keys.Up)`, `Input.IsPressed(Keys.Space)` |
| Collision | Tests pairs | `Collision.CircleCircle(a, b)` |
| Entity lifecycle | Calls Update/Draw on all entities, removes dead ones | Set `IsAlive = false` to destroy |

The game developer (you, writing the Asteroids code) should not need to know about threads, Graphics objects, or event wiring. The engine handles all of that.

---

## How Objects Are Drawn: The Painter's Algorithm

Since 2D games don't have a Z-buffer (depth buffer) like 3D, drawing order determines what appears "on top". The rule is simple: **draw background first, foreground last**.

Each frame:
1. Clear the back buffer (fill with background color)
2. Draw background elements (stars, terrain)
3. Draw game entities (sorted by layer/depth if needed)
4. Draw UI / HUD (score, lives — always on top)
5. Swap buffers (present to screen)

---

## How the Engine Handles Time

The engine uses a high-resolution timer (in C#: `System.Diagnostics.Stopwatch`) to measure time with millisecond or microsecond precision. The pattern:

```csharp
Stopwatch stopwatch = Stopwatch.StartNew();
double lastTime = 0;

while (running)
{
    double currentTime = stopwatch.Elapsed.TotalSeconds;
    double dt = currentTime - lastTime;
    lastTime = currentTime;

    Update(dt);
    Draw();

    // Cap frame rate (e.g. 60fps = ~16.6ms per frame)
    double frameTime = stopwatch.Elapsed.TotalSeconds - currentTime;
    int sleepMs = (int)((TARGET_FRAME_DURATION - frameTime) * 1000);
    if (sleepMs > 0) Thread.Sleep(sleepMs);
}
```

`Thread.Sleep` is imprecise (it sleeps *at least* the requested time, often more). For a student project this is acceptable. Professional engines use busy-waiting or OS-specific high-resolution sleep for tighter frame pacing.

---

## Summary: The Engine's Job in One Sentence

The engine runs a loop, measures time, reads input, calls your update logic, calls your draw logic, and presents the result to the screen — so you only have to write the *what*, not the *how*.

---

---

## Entity Interactions: How Objects Affect Each Other

This is one of the most important design questions in a game engine. When a bullet hits an asteroid, who decides what happens? How does the asteroid know to split? There are several architectural patterns, each with different tradeoffs.

### Pattern 1: Direct Reference (Tight Coupling)

The simplest approach: entity A holds a direct reference to entity B and calls methods on it.

```csharp
class Bullet : GameObject
{
    Ship owner;  // direct reference

    void OnCollide(Asteroid asteroid)
    {
        asteroid.TakeDamage(10);
        this.IsAlive = false;
    }
}
```

**Pro:** Simple, obvious, zero overhead.  
**Con:** Creates a hard dependency. `Bullet` now needs to know `Asteroid` exists. The engine layer can't be generic anymore — it's contaminated by game-specific types. Doesn't scale: if a bullet can hit enemies, shields, and walls, it needs a reference to all of them.

Direct coupling is appropriate for tightly related objects (a rider and their horse) but breaks down for anything general.

### Pattern 2: Interface / Duck Typing

Instead of referencing a concrete type, reference an interface.

```csharp
interface IDamageable
{
    void TakeDamage(int amount);
}

class Bullet : GameObject
{
    void OnCollide(GameObject other)
    {
        if (other is IDamageable target)
            target.TakeDamage(10);
        this.IsAlive = false;
    }
}
```

`Bullet` now works against anything that implements `IDamageable`, without knowing what it is. This is the right first step toward decoupling and is often sufficient for smaller engines.

### Pattern 3: Event / Message Bus (Publish-Subscribe)

A global **event bus** (also called message bus or event dispatcher) lets any entity broadcast an event, and any other entity subscribe to receive it — with no direct dependency between them.

```csharp
// Any entity can fire this
EventBus.Publish(new CollisionEvent { A = bullet, B = asteroid });

// Any system can listen
EventBus.Subscribe<CollisionEvent>(e => {
    if (e.A is Bullet b && e.B is Asteroid a)
        a.Split();
});
```

The bullet doesn't know what reacts to it. The asteroid doesn't know who fired the event. Neither references the other. You can add new reactions (play a sound, increment score, spawn particles) by adding new subscribers without touching existing code.

**This is the most important pattern for decoupled game systems.** Unity's `SendMessage` and Unreal's delegates are both variations of this.

The event bus should support:
- **Typed events** — subscribe to specific event types, not a raw string
- **Priority ordering** — some subscribers run before others
- **Deferred dispatch** — events queued and dispatched at a safe point in the loop (avoids mutating entity lists mid-iteration)

### Pattern 4: Component Communication (ECS)

In an Entity-Component System (covered more below), entities have no methods at all. Communication happens through shared component data. System A writes to a component; System B reads it next frame. This is the most decoupled model but requires a full ECS architecture.

### When to Use Which

| Pattern | Use when |
|---|---|
| Direct reference | Two objects always go together, one owns the other |
| Interface | One object needs to affect "anything of type X" |
| Event bus | Two unrelated systems need to react to the same event |
| ECS components | You want maximum decoupling and data-oriented performance |

---

## Is Everything an Entity? What Is the World Made Of?

No — not everything is an entity, and understanding the distinction is important for clean architecture.

### What IS an entity

An entity is an active, independent object that participates in the update/draw cycle, can be created and destroyed at runtime, and has identity (it IS something specific: this bullet, that asteroid). Entities are dynamic.

### What is NOT an entity

**World geometry / maps / terrain**  
A tiled map or static background is not "alive" in the entity sense. It doesn't move, it doesn't have logic, and it's usually represented as a data structure optimized for spatial queries (a tile grid, a polygon mesh, or a list of static colliders). It's part of the *world* but not the *entity list*. The world holds both.

**Trigger zones / areas**  
These are invisible regions that react to objects entering them (a door, a checkpoint, a damage zone). Conceptually they can be modeled as entities (invisible, no drawing, just collision volume + callback), and this is the common approach. But they're also sometimes managed separately as part of a *trigger system* distinct from the entity system, because they have different performance characteristics (static, many of them, checked against few moving objects).

**Global state (score, lives, current level)**  
This is not an entity. It's *game state* held in a dedicated object — typically a `GameSession` or `World` class that's separate from the entity list.

**Services (input system, audio system)**  
These are singleton-like objects the engine provides. They are not entities.

### The Full Picture: What Makes Up "the World"

The complete runtime state of a game world consists of:

```
World
├── Entity list          → active game objects (ships, bullets, enemies)
├── Static geometry      → tiles, collision meshes, background layers
├── Trigger zones        → areas with enter/exit callbacks
├── Active scene config  → gravity, background color, ambient sound
├── Services (engines)
│   ├── InputSystem      → current key/mouse state
│   ├── AudioSystem      → playing sounds, channel volumes
│   ├── PhysicsSystem    → (if you have one) gravity, constraints
│   └── CollisionSystem  → spatial index, this frame's contacts
└── Game session data    → score, lives, level, time elapsed
```

None of this is "just entities". The entity list is one *part* of the world, not the whole thing.

### ECS: The Modern Answer to "Everything is an Entity"

Entity-Component Systems (ECS) take a different philosophical stance: **everything can be an entity**, but entities are just integer IDs. All data lives in **components** (plain data structs), and all logic lives in **systems** (functions that operate on sets of components).

```
Entity 42 = ID only (just a number)
  has: Position { x=100, y=200 }
  has: Velocity { dx=5, dy=-3 }
  has: Sprite   { imageId="ship" }
  has: Health   { hp=100, maxHp=100 }

System: MovementSystem
  → find all entities with Position + Velocity
  → for each: position += velocity * dt

System: RenderSystem
  → find all entities with Position + Sprite
  → for each: draw sprite at position
```

In ECS, a tilemap *can* be an entity (with a TilemapComponent), a trigger *can* be an entity (with a TriggerComponent), a global score counter *can* be an entity (with a ScoreComponent). The model is philosophically unified. This also unlocks performance benefits: components of the same type are stored contiguously in memory, which is cache-friendly for iteration.

ECS is how Minecraft, Unity (DOTS), and most modern engines are architected. It's more complex to set up but extremely powerful. For your engine, a simpler OOP entity model is the right starting point — but knowing ECS exists and why it was invented is important context.

---

## Collision Detection: Complex Shapes and Optimization

A production-quality collision system has two stages, each with its own concerns.

### Shape Hierarchy

Rather than hard-coding "circle vs circle", design a **shape type system** where each entity declares its collision shape. The system dispatches the right algorithm automatically.

```csharp
abstract class CollisionShape
{
    public abstract bool Intersects(CollisionShape other, out ContactInfo contact);
    public abstract bool ContainsPoint(Vector2 point);
    public abstract AABB GetBoundingBox();
}

class CircleShape   : CollisionShape { float Radius; }
class AABBShape     : CollisionShape { float Width, Height; }
class OBBShape      : CollisionShape { float Width, Height, Angle; }
class PolygonShape  : CollisionShape { Vector2[] Vertices; }  // convex
class CompoundShape : CollisionShape { CollisionShape[] Parts; }
```

Each concrete shape implements `Intersects` using the appropriate algorithm, with **double dispatch** (A asks B to test against itself, and B knows its own type).

### Narrow-Phase Algorithms

**Circle vs Circle** — trivial, distance² < (r1+r2)²

**AABB vs AABB** — axis overlap test on X and Y independently

**Circle vs AABB** — find the closest point on the box to the circle center; collision if distance < radius
```
clampedX = clamp(circle.X, box.Left, box.Right)
clampedY = clamp(circle.Y, box.Top, box.Bottom)
distance = length(circle.center - (clampedX, clampedY))
collision = distance < circle.Radius
```

**OBB vs OBB and Polygon vs Polygon — Separating Axis Theorem (SAT)**

SAT is the standard algorithm for convex shapes. The key insight:
> Two convex shapes do NOT overlap if and only if there exists an axis along which their projections don't overlap.

The candidate axes to test are the face normals of both shapes (for polygons, one per edge). For each axis:
1. Project both shapes onto the axis (find min and max scalar values)
2. If the intervals don't overlap, the shapes are separated — **no collision, stop early**
3. If all axes overlap, the shapes are colliding. The axis with the **smallest overlap** gives you the **collision normal** and **penetration depth**.

```
foreach axis in (shape_a.normals + shape_b.normals):
    [minA, maxA] = project(shape_a, axis)
    [minB, maxB] = project(shape_b, axis)
    if maxA < minB or maxB < minA:
        return no_collision
    overlap = min(maxA, maxB) - max(minA, minB)
    if overlap < minOverlap:
        minOverlap = overlap
        collisionNormal = axis

return collision(normal=collisionNormal, depth=minOverlap)
```

SAT works for any two convex polygons including OBBs, and the output (normal + depth) gives you everything needed to resolve the collision physically.

**Concave shapes**: decompose into convex parts and run SAT on each pair. This is called **convex decomposition**. Libraries like `poly2tri` or `Hertel-Mehlhorn` do this automatically.

**GJK (Gilbert-Johnson-Keerthi)**: A more elegant algorithm for arbitrary convex shapes that works in any dimension. More complex to implement but handles smooth curves and arbitrary convex hulls. Worth knowing about but SAT is sufficient for your engine.

### The Contact Manifold

Raw collision detection only tells you "yes/no". For physics response you need a **contact manifold**:
- **Contact normal** — the direction to push objects apart (unit vector)
- **Penetration depth** — how far they're overlapping
- **Contact point(s)** — where they're touching (for torque calculation)

```csharp
struct ContactInfo
{
    public Vector2 Normal;     // direction to resolve
    public float Depth;        // how much they overlap
    public Vector2 PointA;     // contact point on shape A
    public Vector2 PointB;     // contact point on shape B
}
```

Even if you don't implement full physics, having the normal and depth allows you to correctly push objects apart after a collision (position correction) instead of having them pass through each other.

### Broad-Phase: Spatial Indexing

Checking every entity against every other entity is O(n²). With 100 entities that's 10,000 checks per frame. With 1000 entities it's 1,000,000. Broad phase culls impossible pairs first.

**Uniform Spatial Grid (Spatial Hashing)**

Divide the world into a grid of cells. Each entity is registered in the cells it overlaps. Only check entities that share at least one cell.

```
cell(x, y) = (floor(worldX / cellSize), floor(worldY / cellSize))
```

For a typical game where objects are spread across the world, this reduces checks from O(n²) to O(n) on average. The cell size should be roughly the size of your largest typical entity.

**Quadtree**

Recursively subdivide space into four quadrants. Each node holds a maximum of N entities; when it fills up, it splits into four children. Entities are stored in the deepest node that fully contains them.

Better than a grid for non-uniform distributions (many objects clustered in one area). Used by many 2D engines. A quadtree lookup for a point is O(log n).

```
Root (whole world)
├── NW quadrant
│   ├── NW sub-quadrant (has 3 entities)
│   └── NE sub-quadrant (has 1 entity)
├── NE quadrant (empty)
├── SW quadrant (has 2 entities)
└── SE quadrant (has 7 entities — will split further)
```

**Dynamic BVH (Bounding Volume Hierarchy)**

A tree where each node is an AABB that tightly bounds all its children. Used by Box2D, Bullet, and most professional engines. Very fast for mixed static/dynamic scenes and supports ray casts and shape queries efficiently. More complex to implement correctly (especially the balancing/refit logic for dynamic objects).

**Sweep and Prune**

Sort all entity bounding boxes by their minimum X coordinate. Pairs that don't overlap on X can't possibly overlap — skip them. Then check Y for the remaining pairs. Efficient when objects are mostly spread out on one axis. Used by early physics engines. Simpler than a tree-based approach.

### Collision Layers and Masks

Not every entity should check against every other. A bullet doesn't need to check against other bullets; the player doesn't need to check against their own particles. Define **collision layers** (bitmasks):

```csharp
enum CollisionLayer
{
    Player   = 1 << 0,   // 0001
    Enemy    = 1 << 1,   // 0010
    Bullet   = 1 << 2,   // 0100
    Wall     = 1 << 3,   // 1000
}

// Each entity has: layer (what it IS) and mask (what it checks against)
// Player checks against: Enemy | Bullet | Wall
// Bullet checks against: Enemy | Wall (not other bullets, not player)
```

The collision system only tests pair (A, B) if `A.mask & B.layer != 0`. This is how Unity, Godot, and Box2D all work.

---

## State Management: Beyond the Enum

A proper state machine models each state as an object, not a branch in a switch. The key insight: **a state is a strategy** — it defines behavior for that mode of the game.

### The State Pattern

Define an interface every state implements:

```csharp
interface IGameState
{
    void Enter();                    // called once when state becomes active
    void Exit();                     // called once when state is leaving
    void Update(double dt);
    void Draw(Graphics g);
    void OnKeyDown(Keys key);
}
```

The engine holds a reference to the current state and delegates all calls to it:

```csharp
class StateMachine
{
    IGameState current;

    void TransitionTo(IGameState next)
    {
        current?.Exit();
        current = next;
        current.Enter();
    }

    void Update(double dt) => current?.Update(dt);
    void Draw(Graphics g) => current?.Draw(g);
}
```

Adding a new game mode means adding a new class — no existing code changes. This is the Open/Closed Principle applied to game states.

### The State Stack

Some transitions are not replacements but overlays. Pausing the game should *suspend* the playing state but not destroy it — the world should still be visible behind the pause menu.

A state stack handles this:

```csharp
class StateStack
{
    Stack<IGameState> states = new();

    void Push(IGameState state)   // overlay: new state on top
    {
        states.Peek()?.Pause();   // optionally pause but keep the state below
        states.Push(state);
        state.Enter();
    }

    void Pop()                    // return to previous state
    {
        states.Pop().Exit();
        states.Peek()?.Resume();
    }

    void Update(double dt)
    {
        // Only update the top state (or iterate all if states can be "transparent")
        states.Peek()?.Update(dt);
    }

    void Draw(Graphics g)
    {
        // Draw from bottom to top so overlay states appear on top
        foreach (var state in states.Reverse())
            state.Draw(g);
    }
}
```

A `PlayingState` stays frozen on the stack while `PauseMenuState` sits above it. When the player unpauses, pop `PauseMenuState` and the `PlayingState` resumes exactly where it left off.

### Transition Effects

States can also own transition logic (fade in, slide, etc.) by implementing a transition phase in `Enter()` and `Exit()`. A `Transition` wrapper state can sit between two states on the stack, running an animation, then replacing itself with the target state.

### Hierarchical State Machines

For complex AI, a flat state machine runs out of expressiveness. A character might have top-level states (Idle, Combat, Fleeing) and within Combat, sub-states (Attacking, Blocking, Stunned). Hierarchical state machines (HSM) let states contain other state machines, with transitions that can target states at any level of the hierarchy. This is how game AI is typically built.

---

## Asset Management: Images, Sounds, and Resources

### The Resource Manager Pattern

Never load assets directly in game code. Instead, all assets flow through a central **ResourceManager** (also called AssetRegistry or ContentManager — Microsoft's XNA used that name):

```csharp
class ResourceManager
{
    Dictionary<string, Bitmap> images = new();
    Dictionary<string, Font>   fonts  = new();
    Dictionary<string, Sound>  sounds = new();

    Bitmap GetImage(string id)
    {
        if (!images.ContainsKey(id))
            images[id] = LoadBitmap(id);  // lazy load on first request
        return images[id];
    }

    void Unload(string id) { ... }  // explicit release
    void Clear() { ... }            // release everything (on level change)
}
```

This ensures:
- Assets are loaded once, not per-frame or per-entity
- Easy to hot-reload (swap the image in the dictionary without restarting)
- Easy to track memory usage
- Clean unloading on scene transitions

### Handle-Based Access

For a more robust system, instead of returning raw references (which can dangle), return **handles** — opaque IDs that the resource manager can validate:

```csharp
struct ImageHandle { int Id; }

ImageHandle ship = resources.Load<Bitmap>("ship.png");
// Later:
Bitmap img = resources.Get(ship);  // null if already unloaded
```

This pattern (used by game engines like Godot and custom engines in AAA studios) prevents use-after-free bugs on assets.

### Sprite Sheets and Atlases

Loading one large image containing many sprites is far more efficient than loading many small images — one disk read, one GPU upload, one draw call batch. The resource manager tracks sub-regions:

```csharp
class SpriteAtlas
{
    Bitmap sheet;
    Dictionary<string, Rectangle> regions;

    void Draw(Graphics g, string name, Vector2 pos)
    {
        Rectangle src = regions[name];
        g.DrawImage(sheet, destRect, src, GraphicsUnit.Pixel);
    }
}
```

### Animations

An animation is a sequence of frames from a sprite sheet, played at a given fps. The animation system tracks:
- Current frame index
- Time accumulator (incremented each update, triggers frame advance when it exceeds frame duration)
- Looping / one-shot behavior
- Callbacks (fire an event on a specific frame — useful for hitbox activation)

```csharp
class Animation
{
    int[] frameIndices;     // which atlas frames to show
    float fps;
    float timer;
    int currentFrame;

    void Update(float dt)
    {
        timer += dt;
        if (timer >= 1f / fps)
        {
            timer -= 1f / fps;
            currentFrame = (currentFrame + 1) % frameIndices.Length;
        }
    }
}
```

### Sound

In C#, `System.Media.SoundPlayer` plays `.wav` files synchronously on a background thread. It's simple but limited (one sound at a time, no volume control, no mixing).

For a proper audio system you need a library. **NAudio** (MIT license, NuGet) is the standard choice for C# games:
- Plays multiple sounds simultaneously (mixing)
- Volume and pitch control per channel
- 3D / panned audio (useful even in 2D: sounds louder on left if source is to the left)
- Streaming for background music (don't load the whole file into memory)

The audio system architecture:
```
AudioSystem
├── SoundBank   → loaded sound buffers (short effects)
├── MusicPlayer → streaming player for background music
└── Channels[]  → active playing instances with volume/pitch/pan
```

Game code calls `audio.Play("explosion", volume: 0.8f, pan: -0.3f)` and never touches audio buffers directly.

### Font Rendering

In GDI+: load `Font` objects in the resource manager, call `g.DrawString(text, font, brush, x, y)`. Never create `Font` objects per-frame — they're expensive to allocate. For a more advanced system, pre-render text to a `Bitmap` if the text doesn't change frequently (baked text is faster to draw than re-rasterizing every frame).

---

## The Complete State of a Running Game

Bringing everything together, here is a map of every piece of state a running game engine holds, and where it lives:

```
Engine Runtime
│
├── GameLoop
│   ├── running: bool
│   ├── targetFps: int
│   └── Stopwatch (timing)
│
├── InputSystem
│   ├── heldKeys: HashSet<Keys>
│   ├── pressedThisFrame: HashSet<Keys>    ← cleared each frame
│   ├── releasedThisFrame: HashSet<Keys>   ← cleared each frame
│   └── mousePosition: Vector2
│
├── ResourceManager
│   ├── images: Dictionary<string, Bitmap>
│   ├── sounds: Dictionary<string, SoundBuffer>
│   └── fonts: Dictionary<string, Font>
│
├── AudioSystem
│   └── activeChannels: List<AudioChannel>
│
└── StateStack
    └── [top] PlayingState
        │
        ├── World
        │   ├── entities: List<GameObject>       ← dynamic objects
        │   ├── staticColliders: List<Shape>     ← walls, terrain
        │   ├── triggerZones: List<Trigger>
        │   └── spatialIndex: QuadTree/Grid      ← rebuilt/updated each frame
        │
        ├── CollisionSystem
        │   └── contacts this frame: List<ContactInfo>
        │
        └── GameSession
            ├── score: int
            ├── lives: int
            ├── level: int
            └── elapsedTime: float
```

The key insight: **entity state is just one part of the total runtime state**. The input system, the audio system, the spatial index, and the session data are equally important parts of the game's state — they're just not entities.

---

## Sources

- **Game Programming Patterns** — Robert Nystrom (free online): https://gameprogrammingpatterns.com/game-loop.html  
  The definitive reference for game loop design, delta time, and entity patterns.

- **Game Programming Patterns — Event Queue**: https://gameprogrammingpatterns.com/event-queue.html  
  Nystrom's chapter on decoupled communication between entities.

- **Game Programming Patterns — State**: https://gameprogrammingpatterns.com/state.html  
  The State pattern applied to game modes and character behavior.

- **Game Programming Patterns — Component**: https://gameprogrammingpatterns.com/component.html  
  Introduction to component-based entity design and ECS concepts.

- **Fix Your Timestep!** — Glenn Fiedler (Gaffer on Games): https://gafferongames.com/post/fix_your_timestep/  
  Deep dive into frame timing, delta time, and fixed vs. variable timestep.

- **Introduction to Game Development** — Harvard CS50G (free): https://cs50.harvard.edu/games/  
  Covers game loop, entities, collision, and state management with worked examples.

- **Separating Axis Theorem (SAT) explained** — dyn4j blog: https://dyn4j.org/2010/01/sat/  
  Clear walkthrough of SAT with diagrams, including contact normal extraction.

- **GJK Algorithm** — Casey Muratori (video): https://caseymuratori.com/blog_0003  
  The best explanation of GJK, by one of the people who made it practical for games.

- **Real-Time Collision Detection** — Christer Ericson: ISBN 978-1558607323  
  Authoritative reference for all collision detection algorithms, broad and narrow phase.

- **Introduction to ECS** — Sander Mertens (flecs docs): https://github.com/SanderMertens/ecs-faq  
  Comprehensive FAQ on Entity Component Systems, trade-offs, and practical use cases.

- **NAudio** — GitHub: https://github.com/naudio/NAudio  
  The standard .NET audio library for games and apps; full documentation in the wiki.

- **Microsoft Docs — Double Buffering in WinForms**: https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/double-buffered-graphics  
  Official documentation on GDI+ double buffering.

- **Microsoft Docs — System.Diagnostics.Stopwatch**: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch  
  Reference for high-resolution timing in C#.
