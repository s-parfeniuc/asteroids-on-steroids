# Asteroids on Steroids — Port Plan

**Status:** Phase 0 complete. Phase 1 in progress.
**Target:** Godot 4 + C# (.NET), replacing the bespoke C# engine in `../src/`.
**This directory is temporary.** `ported/` lives inside the current repo only for the duration of the
port. It must build, run and test with no reference to anything outside itself, and will be extracted
into its own repository once the port is complete. See [§11 Independence](#11-independence-rules).

> Once the port lands, the architecture sections of this file become `ARCHITECTURE.md` in the standalone
> repo and the roadmap sections are deleted.

---

## 0. Settled constraints

**No browser support.** The game-design sketch listed "playable in the browser" as a pillar; it is dropped.
This is what makes C# + Godot viable — Godot cannot export C# to the web at all, and the .NET runtime does
not run in its web sandbox. If browser ever returns as a requirement it is a **language decision** (Rust +
Godot, or Rust + Bevy), not a netcode or architecture one, and this document would need rewriting rather
than amending.

**Co-op now, competitive later.** The shipped mode is co-op, but **the fluidity and lobby-size requirements
are unchanged** — controls must feel immediate, and lobbies stay in the 4–16 range. The netcode must remain
**server-authoritative and cheat-resistant enough to extend to competitive**, so no trusting-peer shortcuts
and the dedicated-server path stays viable. See [§4.1](#41-the-fluidity-tension), which this constrains
significantly.

---

## 1. Why we are porting

The current game is ~16.6k LOC of C#/.NET 8 on a hand-written engine: sparse-set ECS, event bus, platform
abstraction layer, rigid-body solver, spatial grid, immediate-mode renderer. That engine layer exists only
because there wasn't one. Maintaining it costs time that should go into the game, it can't be shared with
a team, and it blocks multiplayer, controllers and console/mobile export.

**The destruction model is the product and it survives as-is.** Nearly everything else changes.

### Requirements driving the design
1. **Couch co-op** — 2–4 local players.
2. **Multiplayer** — networked co-op, 4–16 player lobbies, server-authoritative, **extensible to
   competitive**. Controls must stay fluid at full lobby size.
3. **Maximum live body count.** The target is not a number — it is "as many as possible, so performance
   never constrains game design."
4. The destruction model preserved exactly.
5. The architecture must not need replacing as the game grows (more mechanics, content, maps).

### Where the game is going

The design has moved to a **bullet-heaven roguelike with Diablo-style loot**: swarm combat, run
escalation, rolled affixes and rarity tiers, and a ship physically assembled from looted cells.
Destruction is not a side feature — it is both the damage model and the loot delivery mechanism.
**Cells are simultaneously the unit of damage and the unit of gear.**

Three consequences the architecture must absorb — see [§3.7](#37-loot-and-assembly):
- **Cells become items** (rarity, rolled affixes, persistence across runs).
- **Ship assembly means *adding* cells and bonds at runtime.** The current model only ever removes them.
- **Swarm counts mean tiering applies to enemies**, not just to fragments.

### Explicitly dropped
- **Rust.** Dropped once the engine choice settled on Godot + C#. **Reopens if browser is required** (§0).
- **Web/WASM.** See §0 — this is now a blocking decision rather than a settled one.
- **Unity.** Rejected by the team despite a strong Burst/DOTS fit.
- **Bevy.** Pre-1.0 with ~3 breaking releases/year — at odds with "build the foundation once."

---

## 2. Decisions

Grouped by area. Rationale is compressed; the long-form analysis lives in `../info/godot_rust_evaluation.md`.

### 2.1 Engine and language
| Decision | Rationale |
|---|---|
| **Godot 4** (current stable 4.7.x) | Stable, real editor, `Control` UI, best-in-class input/gamepad handling, export templates, large community. The Servers architecture accommodates a custom simulation cleanly. |
| **C# (.NET)**, not GDScript | The existing 16.6k lines are C#; the destruction model transfers almost verbatim. GDScript is too slow for the fracture kernel. |
| **Godot is the shell, not the framework** | Window, input, audio, UI, assets, editor, export, network transport. It does **not** own gameplay objects, physics, or the game loop's work. |

### 2.2 Simulation architecture
| Decision | Rationale |
|---|---|
| **`AsteroidsSim` is a separate assembly with zero Godot references** | Compiler-enforced boundary. Testable headless, snapshot-able, portable. Runs on a dedicated server with no engine. |
| **One `SimRoot` node drives an explicit ordered system list** | Ordering visible in code, not implicit in scene layout. One FFI crossing per tick instead of N. |
| **Sim systems are stateless** | Any field a system holds isn't in `SimState`, so it isn't snapshotted, so it desyncs under rollback. |
| **Swarm bodies are NOT Godot nodes; hero bodies get pooled *view* nodes** | See the split below. Views are always *derived* from `SimState`, never authoritative. |
| **`SetPhysicsProcess(false)` on every view node** | 1,000 C# nodes with an overridden `_PhysicsProcess` is 1,000 managed→native transitions per tick. |

**Which entities get a Godot node**

| | Node? | Why |
|---|---|---|
| Player ships (1–4), boss, named elites | **yes** — pooled view node | Few enough that FFI cost is irrelevant, and they want attached children: VFX emitters, audio, UI anchors, damage-number origins |
| Swarm enemies, asteroids, fragments, debris, projectiles | **no** | Thousands, high churn, batched rendering. Bullet-heaven spawn/despawn churn is precisely Godot's worst case |

**Rollback makes this decision stronger, not weaker.** Restoring the world to tick N−k is a `memcpy` when
bodies are array rows. When they are nodes it becomes scene-graph reconciliation — destroy, recreate, reset
every transform — and nodes carry state that cannot be snapshotted (internal transform caches, physics
RIDs, signal connections). Sim state is plain data; the view is reconciled from it each frame. If a
rollback deletes body 47, the view layer notices next frame and recycles its visual.

> **Superseded:** earlier discussion had one Godot node per fracturable body so `MultiplayerSpawner` /
> `MultiplayerSynchronizer` could replicate transforms. Choosing lockstep removed the need to replicate
> state at all, so that layer is dropped. Godot's networking is used for **transport, RPC and sessions only**.

### 2.3 Physics
| Decision | Rationale |
|---|---|
| **We own the physics** ✅ *measured, not assumed* | Port `CollisionSystem.cs` (395) + `CompoundShape.cs` (355) + `SpatialGrid.cs` (155) ≈ 900 LOC we already wrote and tuned. Required for determinism *and* for snapshot/restore — **and it is also the fastest of the three contenders measured: 4.73 ms/tick at representative density, unoptimised, vs Rapier 16.6 ms and Godot DEFAULT 34.4 ms.** |
| **Godot Physics is not used for the sim** | Not deterministic (order-dependent solver fed by pointer-hashed containers; different platform builds are different compilations), no state snapshotting, no promise of stability across patch releases — **and measured 7.3× slower than ours** on the identical scenario. |
| **`godot-rapier-physics` evaluated and rejected** | 2–3.3× faster than Godot DEFAULT, but **3.5× slower than our existing solver**, and at representative density its median already consumes the entire 16.7 ms frame budget. Decisively: the cross-platform-deterministic flavour disables SIMD and parallel solving, so it is *slower* still. Full numbers in [PHASE0.md](PHASE0.md). |

### 2.4 Netcode
| Decision | Rationale |
|---|---|
| **Server-arbitrated delay-based lockstep** | Only inputs on the wire. Destruction replicates for free — the state is far too large to send, the causes are ~16 bytes. |
| **Rollback deferred, but designed for** | With shared cell/bond arrays the whole world snapshot is ~200 KB ≈ 20 µs to `memcpy`. The blocker was never our state; it was the physics engine's internal caches, which owning the solver removes. |
| **Listen server first, dedicated later** | The sim is engine-free, so "dedicated" is a build flag, not an architecture. |
| **Determinism is a hard contract** | See [§6](#6-determinism-contract). |

### 2.5 Data model
| Decision | Rationale |
|---|---|
| **Shared cell/bond arrays + per-fragment bitmasks** | Splits become allocation-free; `PartitionFront`'s index remapping mostly disappears because cell indices never change; cell identity is stable across the whole body lineage, which the netcode wants. |
| **Per-fragment `CentroidOffset`** | `Cell.Local` is centroid-relative and `Transform.Position == world centroid` is load-bearing. Fragments read `Local[i] - offset` rather than mutating shared vertices. |
| **Copy-on-write for `DetachCellScale`** | Lone-cell shrink-on-detach mutates one cell's vertices; that cell is copied out rather than mutated in place. |
| **Lazy compaction** | Compact when `live/total < 0.5` **and** no crack front is active. Bounds memory without paying a compaction cost mid-cascade. |
| **Pooled arrays** | Measured: ~2 KB and ~110 objects allocated *per hit* at 100 cells. Rent/return by size class. |

### 2.6 Content and extensibility
| Decision | Rationale |
|---|---|
| **Godot `Resource` subclasses (`.tres`) as the authoring format** | Inspector becomes the tuning UI, deleting the need for `tools/Editor` (2,581 LOC). Typed cross-references, hot-reload, text diffs. |
| **The sim never sees a `Resource`** | Defs convert to plain POCOs in a flat table addressed by `int` id. Keeps the sim assembly Godot-free and network-safe. |
| **Content ids are stable and hashed** | Sorted by string `Id` at load; the table hash is validated on join. Ids go over the wire, never strings or paths. |
| **Effect composition, not type switches** | `IFireEffect` / `IHitEffect` lists in data. New weapon = a `.tres`; new *kind* of behaviour = one small class. |
| **Modifier stacks for all mutable stats** | Upgrades, role-gating (losing a propeller/cannon cell) and timed skill effects all become emitted modifiers rather than hardcoded string matching. |
| **Maps are content** | World size, vortex, border, spawn tables and wave script live in a `MapDef`. |

### 2.7 Tick rate and states
| Decision | Rationale |
|---|---|
| **60 Hz fixed tick** (down from 120) | Exactly 2× on every sim cost. Under lockstep the tick rate is a protocol parameter — fix it before the wire format exists. |
| **Shell states in Godot; match phases in `SimState`** | Match phases must be snapshotted and must agree across peers. Shell flow (menu, lobby, loading) must not be. |
| **Overlay stack, not sibling states** | Pause / upgrade-pick / scoreboard are overlays on `InMatch`. Avoids state explosion. |

---

## 3. Architecture

### 3.1 Who owns what

| Godot owns | We own |
|---|---|
| Window, DisplayServer, fullscreen | **All of `SimState`** — bodies, cells, bonds, crack fronts, tiers, timers, RNG |
| OS event pump; keyboard/mouse/**gamepad** input, `InputMap`, device ids | The fracture engine |
| GPU submission, shaders, **rendering materials**¹, canvas items | Physics: broadphase, narrow phase, solver, integration |
| **UI** — `Control` nodes, layout, theming, fonts | Gameplay: player, weapons, skills, AI, boss, waves, vortex, border, scoring |
| Audio mixing and buses | Geometry generation: triangulation, batch buffers, culling |
| Asset import, loading, hot-reload | Determinism: `SimMath`, seeded RNG, ordering discipline |
| Network **transport**, RPC, sessions, headless export | Snapshot / restore / fingerprint, lockstep + rollback logic |
| Editor, export templates | Content table, ids, hashing |

¹ **"Rendering material" ≠ "fracture material."** Godot's `ShaderMaterial` / `CanvasItemMaterial` are GPU
shading state and Godot owns them. Our *fracture materials* — rock, ice, metal, glass, titanium — are sim
content and we own them entirely. Unrelated concepts, unfortunate word collision.

Shared, with explicit rules: **camera** (we compute the transform, Godot applies it — it must never feed
back into the sim), **audio** (we decide what and how loud, Godot plays it), **content** (Godot authors,
we convert to POCOs at load).

### 3.1a What we actually use Godot for

"Godot is the shell" is not "we ignore Godot." The split is between using it as a *platform and toolkit*
(yes) versus as a *game framework whose object model is our game's object model* (no).

**Used heavily:** the whole rendering pipeline — shaders, blend modes, viewports, post-processing,
`MultiMesh` instancing (we submit geometry, Godot rasterizes it programmably) · **`Control` UI** · input
and gamepad handling · audio buses and effects · the asset pipeline and Resource authoring · the editor
and Inspector as the tuning UI · export templates · network transport, RPC and sessions.

**Not used:** the scene tree as a gameplay object model · Godot physics · `MultiplayerSpawner` /
`MultiplayerSynchronizer` · node-per-entity for swarm entities.

> For a Diablo-loot game the `Control` system is worth more than it was under the old design: inventory
> grids, item tooltips, affix displays, side-by-side comparison, drag-and-drop hull assembly. Months of
> work, free.

### 3.2 Directory layout

```
ported/
├── AsteroidsOnSteroids.sln
├── PORT_PLAN.md                    ← this file
├── README.md
│
├── AsteroidsSim/                   ★ C# class library — NO Godot reference
│   ├── Math/                       SimMath (deterministic transcendentals), Vec2, DetRng
│   ├── Geometry/                   PolygonUtils port
│   ├── Fracture/                   Cell, Bond, CrackFront, Kernel, Simulator, Voronoi   ★ invariant
│   ├── Physics/                    broadphase, SAT, compound, sequential-impulse solver
│   ├── State/                      SimState, arenas, masks, Snapshot/Restore, Fingerprint
│   ├── Content/                    def POCOs, ContentTable, stable ids
│   ├── Systems/                    ISimSystem implementations
│   └── Effects/                    IFireEffect, IHitEffect, IStatusEffect    ← extension points
│
├── AsteroidsSim.Tests/             xUnit. Golden traces, replay determinism, unit tests
│
├── AsteroidsGame/                  Godot project (C#)
│   ├── project.godot
│   ├── Sim/                        SimRoot.cs — the single node that drives Step()
│   ├── Content/                    Resource subclasses + converters → sim POCOs
│   ├── Presentation/               batched rendering, particles, debris, camera, audio
│   ├── Net/                        lockstep driver, transport, lobby, desync detection
│   ├── UI/                         Control scenes
│   ├── Flow/                       shell state machine
│   └── assets/
│       └── content/                weapons/ materials/ asteroids/ aliens/ maps/  (*.tres)
│
└── tools/
    ├── Replay/                     headless replay runner + fingerprint diff
    └── Convert/                    one-shot game_config.json → .tres converter
```

Dependency direction is strictly one-way: `AsteroidsGame → AsteroidsSim`. `AsteroidsSim` references
nothing but the BCL.

### 3.3 One frame

```
Godot _PhysicsProcess (fixed 60 Hz)
 └─ SimRoot
     ├─ gather local input  → PlayerInput (quantised)          ~4 FFI in
     ├─ net: submit local input; block until tick N inputs complete
     ├─ foreach system in _systems: system.Update(ref state, in inputs, dt)
     │     ── 100% C#, zero FFI, deterministic, single-threaded ──
     └─ drain SimEvents → presentation queues (audio, particles, score)

Godot _Process (once per rendered frame)
 └─ alpha = accumulator / fixedDt
     ├─ build vertex + instance buffers from SimState, interpolated
     └─ batched submission:                                     ~10 FFI out
          canvas_item_clear
          canvas_item_add_triangle_array   ← body fills
          canvas_item_add_triangle_array   ← cracks / outlines
          multimesh_set_buffer             ← debris
          multimesh_set_buffer             ← particles
          camera transform · HUD property sets
```

Total FFI traffic: **~15 calls per frame**, regardless of entity count.

### 3.4 Systems

```csharp
public interface ISimSystem {
    void Update(ref SimState s, in SimInputs inputs, float dt);   // stateless
}
```

Two families, kept strictly apart:

| | Sim systems | Presentation systems |
|---|---|---|
| Live in | `AsteroidsSim`, plain C#, ordered array | `AsteroidsGame`, Godot nodes |
| State | only in `SimState` | anything |
| Deterministic | **required** | irrelevant |
| Examples | fracture, physics, AI, waves, match flow | VFX, audio, camera, HUD, score popups |

The current 21-entry ordered list in `PlayingState` is the right shape and carries over. The
`EventFlushSystem`-before-**and**-after-`FractureCrackSystem` ordering becomes two adjacent lines in a
function rather than a scheduling constraint.

### 3.5 Data model

`SimRoot` owns exactly one `SimState`. It is **not a general ECS** — it is a handful of hand-designed
tables sized to their populations, which makes snapshot a `memcpy` of a known set of arrays.

```csharp
public struct SimState {
    // bodies — SoA
    public int      BodyCount;
    public Vec2[]   Pos;   public float[] Rot;
    public Vec2[]   Vel;   public float[] AngVel;
    public float[]  Mass;  public float[] Inertia;
    public Tier[]   Tier;  public ushort[] Generation;   // handle validation
    public int[]    ArenaId;          // which cell/bond arena this body views
    public BitMask[] Membership;      // which cells of it belong to this body
    public Vec2[]   CentroidOffset;

    public CellArena[] Arenas;        // Cell[] + Bond[] + CSR adjacency, shared by a lineage

    // side tables, sized to population
    public PlayerData[]    Players;      // 1–4
    public AiData[]        Ais;          // hundreds
    public ProjectileBatch Projectiles;  // SoA, thousands
    public DebrisBatch     Debris;       // SoA, tens of thousands

    public MatchPhase Phase; public uint Tick; public DetRng Rng; public Tuning Tuning;
}
```

Identity is `BodyHandle { int Index; ushort Generation; }` — the same shape as the existing
`Entity(int Id, int Version)`.

- A body is a **view**: a reference to a shared `Cell[]` / `Bond[]`, a membership `BitMask`, and a
  `CentroidOffset`.
- **Vaporise** = clear a bit + mark the cell. O(1), no reallocation.
- **Split** = allocate new masks over the same arrays. No copy, no index remap; live crack fronts stay
  valid because indices never change.
- Bond membership is implied: a bond belongs to a fragment iff both endpoints do — and cross-fragment
  bonds are necessarily broken, so `StepFront`'s existing `if (broken[bk]) continue` already confines the
  flood. No membership test in the hot loop.
- **Compact** lazily; return arrays to the pool on body death.

### 3.6 Content pipeline

```
*.tres (Godot Resource)  ──load──▶  Def POCO  ──▶  ContentTable[int id]  ──▶  SimState
   authored in Inspector              plain C#        sorted by Id,            refers to
                                                      hashed for join          ids only
```

### 3.7 Loot and assembly

The design makes cells simultaneously the destruction engine's unit of damage and the loot engine's unit
of gear. Three requirements follow.

**Cells are items — but keep the hot struct small.** Sim `Cell` stays geometry, mass, damage, role. Item
data (rarity, rolled affixes, provenance, cosmetic) lives in a **side table keyed by `ItemId`**, with the
cell holding only that id. Otherwise every snapshot and every cache line carries loot metadata through the
fracture kernel, which is the hottest loop in the game.

Affixes resolve through the **modifier stack** (§2.6) — an affixed armour cell emits
`{HullToughnessMult, Mul(1.25), Capability(Bumper)}` exactly like an upgrade does. Diablo affixes and
role-gating share one mechanism.

**Assembly means *adding* cells and bonds at runtime.** The current model only ever removes them, and the
shared-arena + mask design assumes arrays only shrink. Assembly **builds a new arena** — acceptable
because it happens between combat, not per tick, but it is a real new subsystem:

- attachment sockets (or bond generation for arbitrary hulls)
- validity rules — connectivity, mass limits, role placement
- the assembly UI (`Control` nodes, drag and drop)
- recomputing mass, inertia, colliders and the render mesh for the new hull

**Persistence.** Meta-progression across runs needs an inventory and save layer. It lives **outside**
`SimState` — a run *starts* by converting inventory into an arena, and *ends* by converting drops back
into inventory. Nothing about persistence is in the deterministic path.

### 3.8 States

```
Shell (Godot):   Menu → Lobby → Loading → InMatch → Results → Menu
                                              └─ overlay stack: [Pause] [UpgradePick] [Scoreboard]

Match (SimState): enum MatchPhase { Warmup, WaveActive, WaveComplete, UpgradeSelect, BossPhase, MatchOver }
                  advanced by MatchFlowSystem, inside the ordered system list
```

Pause is a shell overlay; in multiplayer the sim keeps stepping underneath it.

---

## 4. Netcode

**Model:** single authoritative server; clients submit inputs; the server broadcasts the complete input
set for tick N; every peer simulates tick N identically.

```
CLIENT tick N   sample input → send to server → (wait for tick N input set)
SERVER tick N   collect all inputs (missing → repeat last) → broadcast set → step
CLIENT          on receipt of complete set for tick N → step
```

**Input delay** ≈ `ceil(max_RTT/2 / tick_time)` + jitter buffer. At 60 Hz: ~3–4 ticks (60 ms) at 100 ms
RTT, ~6–7 ticks (110 ms) at 200 ms. Lockstep advances at the speed of the slowest link, so: regional
lobbies, an RTT cap at matchmaking, dynamic delay adjustment, and AI-substitution for a peer that falls
below threshold.

**On the wire**
| Message | Direction | Transport | Size |
|---|---|---|---|
| `PlayerInput { tick, thrust, strafe, aim, buttons }` | C→S | unreliable-ordered, last 3 ticks redundant | ~10 B |
| Input set for tick N | S→C | reliable-ordered | ~10 B × players |
| `Join { protocolVersion, contentHash }` | C→S | reliable | — |
| `Fingerprint { tick, hash32 }` | C→S every 32 ticks | unreliable | 8 B |
| Full `SimState` resync | S→C on desync | reliable | ~200 KB |

### 4.1 The fluidity tension

**"Fluid controls" and "16-player lobbies" pull against delay-based lockstep, and this is the single
biggest open risk in the netcode.** Lockstep advances at the speed of the slowest link, so at 16 players
the delay is set by the *worst* connection in the lobby — and the odds of at least one bad link approach
certainty. At 200 ms RTT that's ~110 ms of input delay on every action, for everyone.

Three models, and the requirements pick one:

| Model | Local input delay | Destruction sync | Complexity |
|---|---|---|---|
| Delay-based lockstep | **max_RTT/2** — 60–110 ms, worse at 16 players | free | low |
| **Lockstep + rollback** | **zero** | free | medium — needs snapshots, which we have |
| Server-auth + prediction | zero for own ship | needs impact-event replication + a correction channel | medium |

**Lockstep + rollback is the end state.** It is the only model that delivers zero local delay *and* keeps
destruction replicating for free *and* stays cheat-resistant enough for a competitive extension. It is
feasible precisely because `SimState` is KB-order — a snapshot is a `memcpy` of contiguous arrays, ~200 KB
and ~20 µs, so an 8-tick ring buffer costs ~1.6 MB and re-simulating 8 ticks is well inside budget.

**Plan:** ship delay-based lockstep first because it is the simplest correct thing and it is what you can
debug. Phase 7 ends with a **fluidity gate** — playtest at target lobby size and realistic RTT. If it
fails, rollback becomes the next phase (§8, Phase 8a) rather than an optional later.

Mitigations that apply to the delay-based build regardless: regional lobbies, an RTT cap at matchmaking,
per-lobby configurable delay tuned by playtest, and AI-substitution for a peer that falls below threshold.

---

## 5. Performance model

Measured on this codebase (see `../info/` and the benchmark in this repo's history):

- `FractureKernel.StepFront` costs **~95 ns/pop** at 100 cells. At 1–3 pops per body per tick and 50
  bodies cracking, that's **~14 µs/tick — 0.2% of budget**. It is *not* the bottleneck, and a Rust rewrite
  of it bought only 1.3–1.4×.
- **Allocation is the bottleneck.** ~2 KB and ~110 objects per hit at 100 cells (`CrackFront.Seed` plus
  `BeginFracture`'s `List<int>[] adj` — one `List` per cell). A grenade landing ~600 hits in a frame is
  ~3 MB and 60k objects — the plausible mechanism for the known grenade frame drop.

**Levers, in order of payoff:**
1. **Tiering** — ~10× on physics. 20k cells → ~2.1k solver bodies.
2. **Persistent broadphase** with frozen sleeping proxies — 3–5×. (Today `SpatialGrid` is cleared and
   refilled every frame: `Dictionary<long, List<Entity>>`, ~18k dictionary ops/frame at 2k bodies.)
3. **Instanced rendering** — upload cell geometry once in body-local space, pass per-body transforms as
   instance data. ~50× on render CPU vs rebuilding 120k vertices/frame.
4. **Arena allocation** in the fracture path — removes the grenade spike.
5. **120 → 60 Hz** — exactly 2×.
6. **SoA layout** — `CompoundShape` is currently ~201 heap objects per 100-cell asteroid.
7. **SIMD** on flat predicate/AABB loops — predicates and transforms only, never accumulators.

### 5.1 Tiering

**The problem.** A 100-cell asteroid is one physics body with 100 convex shapes. A grenade shatters it
into 60 pieces → 60 new physics bodies. Do that to 20 asteroids and you have created 1,200 bodies in a
single frame. But most of those pieces are tiny chips nobody looks at that don't affect gameplay.
Simulating them at full fidelity is waste.

**Tiering = a piece's simulation cost matches its gameplay importance, and pieces demote over time.**

| Tier | What | Collider | Solver | Broadphase | Fracturable |
|---|---|---|:--:|:--:|:--:|
| T0 Cell | row in a shared arena, not an entity | one part of its body | via parent | via parent | yes — it *is* the graph |
| T1 Dust | pulverised cell, VFX only | none | ✘ | ✘ | ✘ |
| T2 Debris | point body in a SoA batch | **none** | ✘ | ✘ | ✘ |
| T3 Simple | rigid body, **one** convex collider, empty bond graph | 1 convex | ✔ | ✔ | ✔ (≤K cells) |
| T4 Compound | full per-cell compound + bond graph | N convex | ✔ | ✔ | ✔ |
| T5 Actor | player / boss / named elite | as T4 | ✔, never sleeps | ✔ | ✔ |

**The life of one asteroid:**

1. Spawns **T4** — 100 cells, full bond graph, one body with 100 convex shapes.
2. You shoot it. Cells vaporise → **T1 Dust** (pure VFX, gone in a second). It splits into four pieces.
3. The 60-cell and two 15-cell pieces stay **T4**.
4. A single-cell piece becomes **T3** — still a rigid body, but one convex collider and no bond graph.
   Far cheaper. Still collides, still hurts you.
5. It drifts off-screen and sleeps → **T2 Debris**: no collider at all. Position and velocity in a flat
   array, integrated in a tight loop, instanced-rendered, colliding with nothing but the map border.
6. TTL expires → gone.

**T2 is one-way.** It is the pressure valve that bounds solver body count regardless of what a grenade
does. **T3→T4 never happens** — bodies only lose cells.

**`debrisArea` — a new per-material knob.** A detaching piece below it **skips T3 entirely and spawns as
T2**. A 200 px² chip was never going to matter for collision; don't give it a collider. It must be a new
knob because `minFragmentArea` already means something else — they answer different questions:

| Threshold | Question | Effect |
|---|---|---|
| `debrisArea` | Is this worth being a physics object at all? | below → T2, never gets a collider |
| `minFragmentArea` | Is this so small that any hit should destroy it? | below → `Fragile`: one hit vaporises the whole body |

`debrisArea` is strictly the smaller. Suggested start: `0.25 × minFragmentArea`.

**Tiering applies to enemies too.** Bullet-heaven swarm counts mean not every enemy can be a 20-cell
fracturable hull. Trash mobs are 1–3 cells (T3), elites and bosses get full hulls (T4/T5). Same ladder,
applied at spawn.

### 5.2 The active region

T3↔T3 collision is the quadratic term — many small bodies testing against each other. Nobody cares whether
two chips 3,000 px from any player bump into each other. So **T3↔T3 pairs are skipped unless at least one
body is inside a region around the players.** This is the single largest cheap win in the broadphase.

**It must be deterministic.** The obvious implementation is "whatever the camera sees" — but in couch co-op
the camera depends on local player count and window aspect ratio, so two clients would compute different
regions, simulate different collisions, and desync.

**Definition:** the union of fixed-size **world-space** rectangles centred on each player's *simulation*
position. Identical on every machine, completely independent of rendering. The camera never feeds the sim.

---

## 6. Determinism contract

Non-negotiable. Every rule below is enforceable by test or lint.

1. **All sim math goes through `SimMath`.** Direct `MathF.*` calls in `AsteroidsSim` are a build error
   (Roslyn analyzer or banned-API list).
2. **Transcendentals need a managed implementation for cross-platform play.** `MathF.Sin` calls glibc on
   Linux and ucrtbase on Windows. Our surface is tiny — across `Destruction/` and `Collision/` it is
   `Sin`×11, `Cos`×11, `Sqrt`×7, `Pow`×2, `Atan2`×2. `Sqrt` is IEEE-exact and safe, so **four functions**
   need vendoring. Decide in Phase 0: vendor them (cross-play works) or restrict to same-platform lobbies.
3. **No `Dictionary` / `HashSet` iteration in sim code.** Sorted arrays or index tables.
   (`FractureSimulator.BuildComponentSpec` currently iterates a `Dictionary<int,int> remap` — becomes a `Vec`.)
4. **Stable sorts only.**
5. **No wall clock, no frame rate, no camera, no window size, no `static` mutable state inside `Step()`.**
   `FractureTuning`'s 17 mutable statics become a `Tuning` field of `SimState`.
   `FractureProcess.DefaultFixedDt` becomes a parameter.
6. **Seeded RNG with named substreams** (`Tessellation`, `Waves`, `Clusters`, `Ai`, `Fx`). Today everything
   shares one `Random` via `GameContext`, so adding a particle effect silently shifts wave rolls.
   FX/presentation streams live outside `SimState`.
7. **Never produce NaN.** Preserve every guard from the current code (`MathF.Max(mass, 1f)`, `> 1e-6f`)
   byte for byte. `Debug.Assert(state.AllFinite())` at end of tick.
8. **Threads never touch the deterministic path.**
9. **Body creation order is part of the contract** — `FractureSimulator` seeds per-body RNG from the global
   stream.
10. **Content ids and effect ids are stable and ordered**; the table hash is validated on join.

---

## 7. Port triage

### Hand-port near-1:1 — the invariant core (~2,000 LOC)
`Engine/Collision/PolygonUtils.cs` (336) · `Engine/Destruction/VoronoiTessellator.cs` (552) ·
`FractureKernel.cs` (227) · `FractureSimulator.cs` (416) · `FractureService.cs` (275) ·
`FractureCrackSystem.cs` (191) · `Cell/Bond/FracturableBody/FractureProcess/FractureContract` ·
`Components/FractureProperties.cs`, `FractureState.cs` · `Gameplay/CellColorizer.cs` (117) +
`FractureMesh.cs` (96) · the `ApplyClusters` Dijkstra half of `AsteroidPrefab.cs`.

Changes are structural only: shared arrays + masks, `Tuning` as state, `SimMath` routing, pooling.

### Port the semantics, restructure the code
`Engine/Systems/CollisionSystem.cs` + `Collision/CompoundShape.cs` + `SpatialGrid.cs` → our physics, SoA,
persistent broadphase · `Gameplay/FractureGameplay.cs` (522) — the *policy* ports 1:1 in intent (cockpit
rules, control transfer, fragment tagging, crater collider disabling), the plumbing is rewritten ·
`Systems/GameSystems.cs` (1,181) → one module per system, all stats via modifier stacks ·
`BossSystem.cs` (254, retuned) · `Prefabs/*` + `FractureBodyFactory.cs` → spawn functions ·
`WorldRenderer.cs` (372) + `ParticleSystem.cs` → `Presentation/` · `GameConfig/**` → content defs ·
`Input/*` → quantised `PlayerInput` · `Rendering/Camera.cs`.

### Rewrite fresh
`GameCore/States/PlayingState.cs` (1,557) → ~8 focused modules (`WaveDirector`, `SpawnPlanner`,
`AntiCamping`, `Camera`, `Hud`, `Pause`, `Hitstop`, `Score`). The wave/spawn model is rebuilt against
`../info/game_design.md`, not transcribed — `meta_notes.md` already flags pacing as *"far too chaotic."*
Also new: netcode, replay, content pipeline, effects/modifiers, shell FSM.

### Delete, do not port
`GameCore/States/WaveCompleteState.cs` + `GameOverState.cs` (dead; `WaveCompleteState.cs:39` is the only
reader of the `waves` config block, so 127 dead scalars go with them) · `src/Platform.*` + `apps/*` ·
`Engine/Rendering/I*.cs` (the PAL — Godot is the PAL) · `Engine/Core/*` (World, SparseSet) ·
`Engine/Events/EventBus.cs` · `Diagnostics/ForceLog.cs` · `Components/Health.cs` and `VisualMesh.cs`
(verified dead, zero references; the design confirms "no HP bar") · `Engine/Audio/*` (stubs) ·
`tools/Editor/` (2,581 — replaced by Resources + Inspector).

### Corrections to carry (the source docs are wrong)
- **`MinFragmentArea` is the `Fragile` threshold, not a debris threshold.**
  `FractureSimulator.cs:247` says so; `docs/fracture_model.md:71` is stale.
  `IsDebris = idxs.Count == 1 && area < 40f` — degenerate slivers only.
- **`CellPulverizedEvent` must carry the cell index.** `FractureGameplay.cs:365` re-derives it by
  nearest-centroid — a latent mis-attribution bug and a per-event allocation.
- **Trust order for the design docs:** `fracture_model.md` > `engine_guide.md` >
  `destruction_engine_spec.md` > `forces.md` §7/§9 (documents a dead energy model) > `physics_spec.md` /
  `fracture_spec.md` (superseded twice).

---

## 8. Roadmap

Each phase ends with something runnable and a written exit criterion. Sizes assume one developer;
C#→C# makes the core port far cheaper than a cross-language rewrite would have been.

### Phase 0 — Foundations and spikes · **2 wk**
Solution skeleton, `AsteroidsSim` with the no-Godot boundary enforced, Godot project shell, CI.
**`SimMath` with vendored deterministic `Sin`/`Cos`/`Pow`/`Atan2`** plus the banned-API analyzer, tested
bit-identical across Linux and Windows. Spikes: (a) batched `canvas_item_add_triangle_array` throughput at
~120k verts + 20k `MultiMesh` instances; (b) our-solver vs `godot-rapier` deterministic build on 2,000
mixed bodies, scoring snapshot/restore fidelity equally with tick time.

**Exit:** `SimMath` produces identical bits on two platforms; both spikes have numbers; physics path
chosen; a triangle renders from `SimRoot`.

### Phase 1 — Sim core: geometry + fracture · **3–4 wk**
`Geometry/`, `Fracture/`, `State/` with shared arrays + masks + `CentroidOffset`, pooling, `Tuning` as
state, `Snapshot`/`Restore`/`Fingerprint`. Content loaded from the existing `game_config.json` unchanged.

**Differential test harness:** reference the *old* engine assembly directly and diff old vs new in-process
on identical inputs — the same technique the `StepFront` benchmark used, which produced bit-identical
checksums. This is the port's correctness definition. (Temporary scaffold — see §11.)

**Exit:** all five authored shapes tessellate to identical topology; 200 scripted fracture scripts produce
identical break/pulverise/partition sets; `Restore(Snapshot(s))` round-trips to an identical fingerprint.

### Phase 2 — Physics and tiering · **3–4 wk**
SoA compound shapes, persistent broadphase with frozen sleeping proxies, sequential-impulse solver,
raycast queries. Tier machine T1–T5, collision matrix, deterministic active region, `debrisArea`.

**Exit:** 2,000 bodies at 60 Hz single-threaded; a stress scene at 10× current cell count holds frame rate;
fingerprint stable across 10,000 ticks.

### Phase 3 — Godot shell · **3–4 wk**
Batched rendering (fills, cracks, debris, particles), `CellColorizer`, camera with trauma shake, quantised
input incl. gamepads, content pipeline (JSON → `.tres` converter, Resource defs, `ContentTable`), audio
event taxonomy emitted.

**Exit:** shoot a `bruiser.tres` apart on screen — multi-frame cracking, splitting, craters later shots
pass through. Tune `brittleness` in the Inspector and see it next fracture.

### Phase 4 — Gameplay · **5–6 wk**
Player control with role-gated capability via modifier stacks, control transfer on cockpit loss, four
weapons incl. the piercing round, skills, alien AI, boss, vortex, border hazard, anti-camping, wave
director (rebuilt), scoring, hitstop, HUD, shell FSM + overlay stack.

**Exit:** feature parity with the current build, playable at 60 Hz, single-player.

### Phase 5 — Determinism hardening and replay · **2 wk**
Replay harness: `(seed, contentHash, input stream)`. Headless runner in `tools/Replay/`. Fingerprint CI job.
Audit every §6 rule.

**Exit:** a recorded 10-minute session replays to an identical fingerprint; CI fails on any drift.
This artifact is the regression suite, the bug-report format and the desync debugger.

### Phase 6 — Couch co-op · **1–2 wk**
`PlayerInput` frozen as a wire format. Multi-device input, "press a button to join," per-player action maps,
remap UI, shared zoom-to-fit camera with `max_zoom_out`, tether and off-screen indicators.

**Exit:** 4 gamepads + kbm on one machine, one camera, one HUD; the session replays bit-identically.

### Phase 7 — Netcode · **5–6 wk**
Lockstep driver, ENet transport, listen server, lobby, join validation (protocol + content hash), input
delay negotiation, fingerprint desync detection, full-state resync fallback, headless dedicated export.

**Exit — two gates.** *Correctness:* 8 clients run a 10-minute match with zero desyncs, and a deliberately
corrupted client is detected and recovers via resync. *Fluidity (§4.1):* playtest at target lobby size and
realistic RTT. If controls don't feel immediate, Phase 8a is next.

### Phase 8a — Rollback · **3–4 wk** · *conditional on the Phase 7 fluidity gate*
Snapshot ring buffer, remote-input prediction, re-simulation on mismatch, input-delay → 0. Cheap because
`Snapshot`/`Restore` shipped in Phase 1 and the state is KB-order. **Likely required at 16 players.**

**Exit:** zero local input delay; a forced 200 ms spike on one peer is invisible to the others.

### Phase 8b — Extensibility and content · **2–3 wk**
Effect composition (`IFireEffect`/`IHitEffect`) with registration, upgrade system on modifier stacks,
`MapDef`, second map, two new weapons authored **without touching code** as the acceptance test.

**Exit:** a new weapon ships as a single `.tres`.

### Phase 9 — Audio, polish, extraction · **3–4 wk**
Audio backend wired to the Phase-3 taxonomy, settings persistence, packaging, and the §11 independence
gate. `ported/` becomes its own repository.

**Total: ~32–40 weeks** including rollback. Single-player playable around week 16; networked around week 28.

**Cut levers, in order:** cap lobbies at 8 players (makes delay-based lockstep viable and may remove Phase
8a entirely) · defer Phase 8b to post-launch, keeping the seams · ship single-player + couch co-op as a
real release before starting Phase 7. **Note the transcendental work is no longer a cut lever** — dropping
it would forfeit cross-platform play, which a 16-player lobby needs.

---

## 9. Questions — resolved and open

### Resolved
1. **Transcendentals — implement them.** Vendor managed, deterministic implementations of the four
   functions we actually need (`Sin`, `Cos`, `Pow`, `Atan2`; `Sqrt` is IEEE-exact and needs nothing) so
   cross-platform play is possible. Phase 0 deliverable, gated by a banned-API analyzer (§6 rule 1).
2. **Our solver vs `godot-rapier`** — decided by the Phase 0 benchmark, weighting snapshot/restore
   fidelity equally with tick time, since rollback depends on it.
3. **RTT threshold — configurable, tuned by playtest.** Input delay is a setting, not a constant; the
   acceptable ceiling is a feel judgement. **Rollback with input prediction stays explicitly open** — the
   sim state is KB-order, so a snapshot is a `memcpy` (~20 µs), and nothing in this design may foreclose it.
4. **Body-count target — as high as achievable.** No fixed number. Performance must not constrain game
   design, which makes tiering (§5.1) a core feature rather than an optimization.
5. **Listen server first, dedicated later.** *Listen server*: a player's process also runs the authoritative
   sim — free, no infrastructure, but the host has a latency advantage, the match dies if they quit, and
   NAT traversal is needed. *Dedicated*: a rented headless process — equal latency for all, matches survive
   anyone leaving, but it costs money per concurrent match and needs deployment and matchmaking. Since the
   sim is engine-free both run the *same code*; it is a build target, not an architecture. The design
   sketch reads co-op, which makes listen server clearly right first.

6. **No browser support** (§0). C# + Godot stands.
7. **Co-op now, competitive later** (§0). Lobby size and control fluidity are unchanged from the
   competitive assumption, which is what makes §4.1 the biggest netcode risk and pulls rollback forward
   from "optional" to "likely required."

### Open — deferred to post-port phases
Both are game-design decisions, not architecture. Nothing in the port may assume an answer to either.

1. **Hull assembly model** — attachment sockets on a grid, or free-form bond generation for arbitrary
   hulls? Sockets are dramatically simpler and probably better for readability ("read the silhouette, know
   the build"); free-form is more expressive. Needed before §3.7 ships, not before the port starts.
2. **World size, grain, and cell-count targets.** Nothing in the design may assume 2,000 cells — that
   number is a consequence of today's missing tier system, not a design target.

---

## 10. What we are deliberately not doing

- No custom editor. Resources + Inspector. (Deletes 2,581 LOC.)
- No ECS framework. `SimState` arenas are simpler, snapshot-able, and already the shape we have.
- No Godot physics in the sim path.
- No `MultiplayerSpawner` / `MultiplayerSynchronizer` — lockstep replicates no state.
- No rollback **in the first netcode build** — but it is a planned phase (8a), not a maybe. Snapshot/restore
  is a Phase 1 deliverable and the physics choice is weighted on snapshot fidelity, precisely so 8a is cheap.
- No web export. Settled (§0); it is what makes C# viable.
- No threading in the deterministic path.
- No general ECS framework — `SimState` is hand-designed tables sized to their populations (§3.5), which
  makes snapshot a `memcpy` of a known set of arrays rather than per-component-type serialization.

---

## 11. Independence rules

`ported/` must be extractable at any time. Enforced by a CI job from Phase 0:

1. **No path reference outside `ported/`** in any `.csproj`, `project.godot`, or source file — with one
   exception below.
2. **Assets are copied, never linked.** `AsteroidsGame/assets/` is the source of truth from Phase 3.
3. **The one exception:** Phase 1's differential test harness references the old engine assembly. It is
   isolated in `AsteroidsSim.Tests/Legacy/`, guarded by a `LEGACY_DIFF` compilation symbol, and **deleted
   at the Phase 5 gate** — replaced by the replay harness, which needs nothing external.
4. **The independence gate:** `git clone` only `ported/` into a clean directory; solution builds, tests
   pass, game runs. Run it at the end of every phase, not just at the end.
