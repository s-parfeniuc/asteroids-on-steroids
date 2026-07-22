# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Asteroids on Steroids — a C# / .NET 8.0 game with a custom ECS engine, built around physics-based destruction of asteroid bodies composed of convex polygon cells. The map is ~10× the screen; waves of asteroids and enemy ships become progressively harder.

Controls: WASD thrust · Mouse aim · Left-click fire · Q/E/R skills (dash/turbo/slow-mo) · Esc quit.

## Build & run commands

`AsteroidsOnSteroids.sln` (workspace root) bundles the seven cross-platform (`net8.0`) projects. The two
`net8.0-windows` WinForms projects are **not** in the solution (they can't be loaded/built without the
Windows Desktop SDK); build those directly on Windows.

```bash
# Build everything cross-platform (Linux source of truth)
dotnet build AsteroidsOnSteroids.sln

# Run the game (SDL2 + Skia)
cd apps/Game.Sdl && dotnet run

# Run the interactive shape/config editor
cd tools/Editor && dotnet run

# On Windows only — the WinForms (Skia) build:
dotnet build apps/Game.WinForms/AsteroidsGame.WinForms.csproj

# Self-contained distributables (→ dist/<rid>/, natives bundled, Assets/ copied beside the exe):
build/linux.sh          # SDL build   → dist/linux-x64
build/macos.sh          # SDL build   → dist/osx-arm64  (arg osx-x64 for Intel)
build/windows.ps1       # WinForms    → dist/win-x64    (Windows only)
```

There are no test projects in this repository.

## Repository layout

```
AsteroidsOnSteroids.sln         # cross-platform projects (the net8.0-windows WinForms apps build separately)
Assets/                         # game_config.json + shapes/*.json (loader walks up from the exe to find it)
src/
  Engine/                       # UI-free engine class library (AsteroidsEngine.csproj) — folder holds ONLY engine source
    Core/                       # ECS: World, Entity, SparseSet, ISystem
    Components/                 # Engine-level components (Transform, RigidBody, Velocity, Collider, …)
    Collision/                  # Shape hierarchy + SpatialGrid broad phase
    Destruction/                # Fracture engine (FractureService, FractureSimulator, VoronoiTessellator)
    Events/                     # EventBus (typed publish-subscribe, deferred flush)
    Input/                      # InputSystem, KeyCode
    Rendering/                  # IRenderer, IPostEffects, IGameWindow interfaces, Camera, Color, FontSpec
    Audio/                      # IAudioBackend + SoundId — STUBS for a future audio sprint (no audio yet)
  GameConfig/                   # GameConfigLoader, GameConfig model, ShapeData
  Gameplay/                     # Shared gameplay library (systems/components/prefabs/fracture/VortexFx/WorldRenderer)
  GameCore/                     # Shared game library (no platform): States/ + GameHost (loop)
  Platform.Skia/                # Backend-neutral SkiaRenderer (net8.0) — shared by both windows below
  Platform.Sdl/                 # SDL2 window (net8.0) — builds the GL SKSurface, hands it to SkiaRenderer
  Platform.WinForms/            # WinForms window via SKGLControl (net8.0-windows) — Windows-only sibling
apps/
  Game.Sdl/                     # SDL executable — thin Program.cs → GameHost.Run (Linux/macOS + Windows)
  Game.WinForms/                # WinForms executable (net8.0-windows) — thin Program.cs → GameHost.Run
tools/
  Editor/                       # Shape/config editor + sandbox (SDL-only); owns the immediate-mode Ui/ toolkit
```

> **Note:** the engine is deliberately UI-free — the immediate-mode UI toolkit (`Ui`, `EditorDef`) lives in
> `tools/Editor/Ui/`, not the engine. The old `Demos/` and the dead engine-layer state machine / `GameLoop`
> were removed in the restructure.

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

### Main game loop (`GameCore/GameHost.cs`)

The game does **not** use the engine's `GameLoop` class (which exists but is used only in demos). Instead, the shared `GameHost.Run(IGameWindow)` owns a hand-written loop (each executable's `Program.cs` just constructs its window and calls it):

1. `window.PollEvents()` — OS event pump (SDL pump / WinForms `Application.DoEvents`).
2. `input.BeginFrame()` — advance held/just-pressed state.
3. `FixedTimestep.Advance(frameTime)` — returns the number of fixed steps to run at 1/120 s each.
4. For each step: `state.Update(FixedDt)` — returns `IGameState?`; non-null triggers a transition.
5. `state.Draw(renderer, alpha)` — render at sub-step alpha for smooth interpolation.
6. `window.Present()` — swap buffers.

### State machine (`GameCore/States/`)

`IGameState` (the **game-layer** version in `GameCore/States/IGameState.cs`) has:

```csharp
void   Enter();
void   Exit();
IGameState? Update(double dt);  // returns next state on transition, null to stay
void   Draw(IRenderer renderer, double alpha);
```

Transitions are a simple pointer swap in the main loop. States: `MainMenuState → PlayingState ↔ WaveCompleteState → GameOverState → MainMenuState`.

Each `PlayingState` owns its own `World` and system list; tearing down a state destroys all entities.

> **Note:** the game's state machine is `GameCore/States/IGameState.cs` (`namespace AsteroidsGame.States`, `Update` returns `IGameState?`). There is no engine-layer state machine (an old unused `Engine/State/*` was removed in the restructure).

### Platform Abstraction Layer (PAL)

The engine has zero dependency on any UI toolkit. Platform-specific code is isolated in:

- `IRenderer` (`Engine/Rendering/IRenderer.cs`) — immediate-mode 2D draw API (lines, convex polygons, circles, text, transform stack).
- `IPostEffects` (`Engine/Rendering/IPostEffects.cs`) — **optional** screen-space warp (`Distort`), feature-detected via `renderer as IPostEffects`; backends may skip it.
- `IParticleBatch`, `IMeshBatch`, `IRenderStats` (`Engine/Rendering/*.cs`) — more **optional** capabilities, same `renderer as …` feature-detect pattern. `IParticleBatch.DrawSprites` draws all particles in one `DrawAtlas` submission; `IMeshBatch.FillMesh` draws the whole fracturable-body field as one seamless per-vertex-coloured `DrawVertices` (`WorldRenderer.DrawBodyFills` fan-triangulates all live cells into it — no per-cell `FillPolygon` storm, no union-underlay overdraw); `IRenderStats.DrawCallCount` exposes a running primitive count for the profiler overlay. Each has a CPU fallback (`ParticleSystem`/`WorldRenderer`) for backends that don't implement it.
- `IGameWindow` — window lifecycle + event callbacks (`Width/Height/ShouldClose`, `Renderer`, 5 input events, `PollEvents`/`Present`).
- **Shared renderer + two windows.** `SkiaRenderer` (in `src/Platform.Skia`, net8.0, backend-neutral) implements `IRenderer` + all the optional capabilities over any `SKSurface`. Each window builds a GPU `SKSurface` on its GL context and hands it to that one renderer: `src/Platform.Sdl` (SDL2, net8.0, cross-platform — Linux/macOS + Windows) and `src/Platform.WinForms` (WinForms via `SKGLControl`, **net8.0-windows, Windows-only**). So the WinForms build gets every renderer feature (batching, warp, …) for free; there is no separate GDI+ renderer. `KeyCode`/`MouseButton` values equal `System.Windows.Forms.Keys`/`MouseButtons` so the WinForms backend maps keys by a direct cast.
- **Backend selection**: no factory/`#if` — each executable (`apps/Game.Sdl`=SDL, `apps/Game.WinForms`=WinForms) is a thin `Program.cs` that constructs its own concrete window and calls the shared `GameHost.Run(IGameWindow)` in `src/GameCore`. The WinForms projects **cannot build on Linux** (need the Windows Desktop SDK); never name their `.csproj` in a Linux build.

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
- Broad phase: one shared `SpatialGrid` (uniform grid implementing `ISpatialIndex`), rebuilt once per frame by `BroadPhaseSystem` (must run after movement, before consumers). Both `CollisionSystem` (AABB `GetCandidates`) and the bullet raycast (`ISpatialIndex.QuerySegment`, a DDA cell-walk, via `PhysicsQueries.Raycast(world, index, …)`) read that same index — so the world is indexed exactly once and raycasts are O(colliders near the ray), independent of entity/bullet count.
- `CollisionSystem` detects contacts and applies velocity impulses (elastic, using `RigidBody.Mass`). Entities without `RigidBody` are treated as immovable. It no longer builds the index — it only queries the one `BroadPhaseSystem` built.
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
- `GameLoop` (in `Engine/Core/`) is not wired into the main game — it is used by demos. The game's timing and loop are in `GameCore/GameHost.cs` (shared by both the SDL and WinForms executables).
