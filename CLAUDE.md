# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Asteroids on Steroids — a C# / .NET 8.0 game with a custom ECS engine, built around physics-based destruction of asteroid bodies composed of convex polygon cells. The map is ~10× the screen; waves of asteroids and enemy ships become progressively harder.

Controls: WASD thrust · Mouse aim · Left-click fire · Q/E/R skills (dash/turbo/slow-mo) · Esc quit.

## Build & run commands

All projects live under `GameEngine/`. There is no solution file — work with individual `.csproj` files.

```bash
# Run the game
cd GameEngine/Game && dotnet run

# Run the asteroid demo / interactive editor
cd GameEngine/Demos/AsteroidDemo && dotnet run

# Build just the engine library
dotnet build GameEngine/AsteroidsEngine.csproj

# Build a specific project
dotnet build GameEngine/Game/AsteroidsGame.csproj
```

There are no test projects in this repository.

## Repository layout

```
GameEngine/
  AsteroidsEngine.csproj        # Engine class library (no UI dependencies)
  Engine/                       # Engine subsystems
    Core/                       # ECS: World, Entity, SparseSet, GameLoop, ISystem
    Components/                 # Engine-level components (Transform, RigidBody, Velocity, Collider, …)
    Collision/                  # Shape hierarchy + SpatialGrid broad phase
    Destruction/                # Fracture engine (FractureService, FractureSimulator, VoronoiTessellator)
    Events/                     # EventBus (typed publish-subscribe, deferred flush)
    Input/                      # InputSystem, KeyCode
    Rendering/                  # IRenderer, IGameWindow interfaces, Camera, Color, FontSpec
    State/                      # IGameState, StateStack (engine-layer versions — see note below)
  Platform/Sdl/                 # SDL2 + SkiaSharp backend implementing IGameWindow / IRenderer
  GameConfig/                   # GameConfigLoader, GameConfig model, ShapeData
  Game/                         # Executable (AsteroidsGame.csproj)
    Program.cs                  # Entry point & main loop
    GameContext.cs              # Shared services (config, shapes, input, RNG, score, cell budget)
    States/                     # MainMenuState, PlayingState, WaveCompleteState, GameOverState
    Components/GameComponents.cs # Game-specific components (TimeToLive, BodyColor, SkillState, …)
  Assets/                       # game_config.json + shapes/*.json
  Demos/AsteroidDemo/           # Standalone demo & shape editor (large Program.cs)
```

## Architecture

### ECS core (`Engine/Core/`)

`World` is the ECS world. All game state lives in components; there are no OOP entity hierarchies.

- Components are **structs** stored per-type in a `SparseSet<T>` (dense array + sparse index).
- Entities are `Entity(int Id, int Version)` value types; version increments on recycle.
- Component mutation uses `ref` returns: `ref var t = ref world.GetComponent<Transform>(e)`.
- Destruction is deferred: `world.DestroyEntity(e)` / `world.RemoveComponent<T>(e)` queue work applied by `world.FlushDeferred()` once per frame after all systems run.
- `World.ForEach<T1,T2>(action)` and `World.ForEachParallel<T1,T2>(action)` are the primary iteration APIs.

```csharp
// Typical system body
world.ForEach<Transform, Velocity>((Entity e, ref Transform t, ref Velocity v) =>
{
    t.Position += v.Linear * (float)dt;
    t.Rotation += v.Angular * (float)dt;
});
world.FlushDeferred(); // after all systems
```

### System interfaces (`Engine/Core/ISystem.cs`)

```csharp
interface ISystem       { void Update(World world, double dt); }
interface IDrawSystem   { void Draw(World world, IRenderer renderer); }
```

Systems optionally implement `ISystemMetadata` to declare their `ComputationModel` (used by a future parallel scheduler). Models range from `IndependentTransform` (safe for `ForEachParallel`) through `EventConsuming` and `Singleton` (sequential only).

### Main game loop (`Game/Program.cs`)

The game does **not** use the engine's `GameLoop` class (which exists but is used only in demos). Instead, `Program.cs` owns a hand-written loop:

1. `window.PollEvents()` — SDL event pump.
2. `input.BeginFrame()` — advance held/just-pressed state.
3. `FixedTimestep.Advance(frameTime)` — returns the number of fixed steps to run at 1/120 s each.
4. For each step: `state.Update(FixedDt)` — returns `IGameState?`; non-null triggers a transition.
5. `state.Draw(renderer, alpha)` — render at sub-step alpha for smooth interpolation.
6. `window.Present()` — swap buffers.

### State machine (`Game/States/`)

`IGameState` (the **game-layer** version in `Game/States/IGameState.cs`) has:

```csharp
void   Enter();
void   Exit();
IGameState? Update(double dt);  // returns next state on transition, null to stay
void   Draw(IRenderer renderer, double alpha);
```

Transitions are a simple pointer swap in the main loop. States: `MainMenuState → PlayingState ↔ WaveCompleteState → GameOverState → MainMenuState`.

Each `PlayingState` owns its own `World` and system list; tearing down a state destroys all entities.

> **Note:** `Engine/State/IGameState.cs` and `Engine/State/StateStack.cs` are engine-layer designs that are **not used** by the game. The game's `Game/States/IGameState.cs` has a different signature (returns `IGameState?`). The engine versions are reference implementations for alternative hosting.

### Platform Abstraction Layer (PAL)

The engine has zero dependency on any UI toolkit. Platform-specific code is isolated in:

- `IRenderer` (`Engine/Rendering/IRenderer.cs`) — immediate-mode 2D draw API (lines, convex polygons, circles, text, transform stack).
- `IGameWindow` — window lifecycle + event callbacks.
- **Active backend**: `Platform/Sdl/SdlGameWindow.cs` + `SkiaRenderer.cs` (SDL2 via `Silk.NET.SDL`, 2D rendering via `SkiaSharp`).
- A dormant WinForms/GDI+ backend (`Engine/Rendering/GameWindow.cs`) is excluded from the engine build (compile-only under the `WINDOWS` symbol).

### Destruction engine (`Engine/Destruction/`)

The headline feature. Asteroid/ship bodies are `FracturableBody` — an array of `Cell[]` (convex polygon chunks in body-local space) joined by `Bond[]` (spring-like connections between adjacent cells).

Two fracture paths:

| Path | Entry point | Behaviour |
|---|---|---|
| Atomic | `FractureService.TryFracture(...)` | Fractures completely in one frame |
| Multi-frame | `FractureService.BeginFracture(...)` | Seeds a `CrackFront`; `FractureCrackSystem` advances it over several frames |

Flow: impactor hit → `FractureService` computes reduced-mass impact energy → passes `FractureInput` to `FractureSimulator` → breaks bonds, extracts connected fragment groups → caller spawns fragment entities (engine does not spawn entities itself).

Key types:
- `WeaponProfile` — impactor-side params (directionality, momentum transfer, blast fraction).
- `FractureProperties` — material params on the body (toughness, brittleness, crack directionality).
- `FractureState` — accumulated sub-threshold damage.
- `VoronoiTessellator` — generates Voronoi-based cell decomposition from seed points.

### Collision (`Engine/Collision/` + `Engine/Systems/CollisionSystem.cs`)

- Shape hierarchy: `CollisionShape → CircleShape | AABBShape | PolygonShape | CompoundShape`.
- Broad phase: `SpatialGrid` (uniform grid implementing `ISpatialIndex`).
- `CollisionSystem` detects contacts and applies velocity impulses (elastic, using `RigidBody.Mass`). Entities without `RigidBody` are treated as immovable.
- Semantic responses (damage, splits, score) live in game-layer systems subscribed to `CollisionEvent` via `EventBus`.

### EventBus (`Engine/Events/EventBus.cs`)

Typed pub-sub with deferred dispatch. `Publish<T>()` enqueues into a `ConcurrentQueue` (safe from `ForEachParallel`). `Flush()` drains the queue sequentially — call once per frame after all systems, before `FlushDeferred()`.

```csharp
bus.Subscribe<CollisionEvent>(OnCollision);
bus.Publish(new CollisionEvent { A = e1, B = e2, Contact = info });
// later in the frame:
bus.Flush();
```

### Config & assets

`GameConfigLoader.FindAssetsDir(AppContext.BaseDirectory)` walks up the directory tree to find the `Assets/` folder — no hardcoded path. Files:
- `Assets/game_config.json` — main tuning data (camelCase JSON, trailing commas and comments OK).
- `Assets/shapes/*.json` — shape definitions, loaded by filename stem (e.g. `player_ship.json` → key `"player_ship"`).

`GameContext` (shared across all states) holds `Config`, `Shapes`, `InputSystem`, screen dimensions, `Score`, `CellBudget`, and a shared `Random`.

### Prefabs (convention)

Entity creation uses static factory methods by convention (no serialised prefab assets):

```csharp
static class AsteroidPrefab {
    public static Entity Create(World world, Vector2 pos, AsteroidSize size) { ... }
}
```

`AsteroidSplitSystem` calls `AsteroidPrefab.Create(...)` — it doesn't need to know what components an asteroid requires.

## Key design decisions

- **`ref`-based component mutation** is intentional and required for performance. Never copy a component struct when you mean to mutate it.
- **Deferred entity destruction** — never call `DestroyImmediate` inside a `ForEach` loop. Use `DestroyEntity` / `RemoveComponent` and let `FlushDeferred()` clean up.
- `ForEachParallel` is safe only when the body reads/writes only its own entity's components and calls no `CreateEntity`/`DestroyEntity`. `EventBus.Publish` is safe inside parallel bodies; `Flush()` must be called sequentially afterward.
- The engine spawns no entities during fracture — `FractureService` returns a `FractureResult`; the game layer is responsible for spawning fragment entities. This keeps the destruction engine free of game-specific logic.
- `GameLoop` (in `Engine/Core/`) is not wired into the main game — it is used by demos. The game's timing and loop are in `Game/Program.cs`.
