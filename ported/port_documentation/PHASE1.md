# Phase 1 — The Destruction Core

**Duration:** ~3–4 weeks · **Depends on:** Phase 0 (complete)
**Parent:** [PORT_PLAN.md](port_documentation/PORT_PLAN.md)

Phase 1 ports the part of the game that cannot be bought, rebuilt or approximated: the cell/bond
fracture engine. **2,274 lines** of C#, moving from `../src/Engine` into `AsteroidsSim`.

Everything else in the port is replaceable. This is not, and it is the reason the game exists — so the
governing constraint is not speed of delivery but **provable behavioural equivalence**.

## Exit criteria

1. All five authored shapes (`player_ship`, `drone`, `bruiser`, `mothership`, `piercing_round`)
   tessellate to **topologically identical** output — same cell count, same bond graph, same roles.
2. **200 scripted fracture scenarios** produce identical break / pulverise / connected-component
   partitions against the legacy engine, with scalars within 1e-4 relative.
3. `Restore(Snapshot(s))` round-trips to an **identical fingerprint**.
4. The determinism gate stays green on all three platforms.
5. No allocation in the steady-state fracture path (measured, not asserted).

---

## What is being ported

| Source | Lines | Notes |
|---|---:|---|
| `Collision/PolygonUtils.cs` | 336 | Pure math. 8 public functions. Port first — everything depends on it |
| `Destruction/Cell.cs` · `Bond.cs` · `FracturableBody.cs` | 79 | Data model. Reshaped for the arena + mask design |
| `Components/FractureProperties.cs` · `FractureState.cs` | 92 | Material params |
| `Destruction/FractureContract.cs` · `FractureProcess.cs` | 106 | The engine↔game boundary types |
| `Destruction/FractureKernel.cs` | 227 | **The conservative energy kernel.** The most load-bearing file |
| `Destruction/VoronoiTessellator.cs` | 552 | Tessellation, concavity carving, void filling, bond derivation |
| `Destruction/FractureSimulator.cs` | 416 | Union-find components, fragment rebuild, `DerivedMotion`, `SplitLive` |
| `Destruction/FractureService.cs` | 275 | Impact energy, `BeginFracture`, `DepositEnergy` |
| `Destruction/FractureCrackSystem.cs` | 191 | Multi-frame driver, per-front pacing |
| **Total** | **2,274** | |

---

## The differential harness — build this first

Because this is C#→C#, correctness can be established far more strongly than a cross-language port would
allow: **reference the legacy assembly and run both implementations in the same process, on the same
inputs, comparing results directly.** No golden files to drift, no serialisation format to get wrong.

This is exactly the technique that produced bit-identical checksums when benchmarking `StepFront` earlier
in the port, so it is known to work on this code.

```
AsteroidsSim.Tests/Legacy/          ← guarded by the LEGACY_DIFF symbol
    LegacyBridge.cs                 converts legacy Cell[]/Bond[] ↔ ours
    TessellationDiffTests.cs        exit criterion 1
    FractureDiffTests.cs            exit criterion 2
    ScenarioScripts.cs              the 200 scripted hits, generated from DetRng
```

`tools/SpikePhysicsBaseline` already carries the sanctioned legacy reference and the whitelist entry in
`scripts/check_independence.py`; this extends the same pattern. **Deleted at the Phase 5 gate**, when the
replay harness makes it redundant.

**Tolerances.** Exact equality on everything discrete — cell count, bond topology, which bonds broke,
which cells pulverised, the connected-component partition, event order. 1e-4 relative on floats. Exact
float equality is not a goal: `SimMath` deliberately does not reproduce the platform libm bit-for-bit,
which is the entire point of it existing.

---

## Structural changes from the legacy code

These are deliberate and each has to be justified against "port it as-is". Everything else is transcribed.

### 1. Shared arenas + per-fragment bitmasks
PORT_PLAN.md §2.5. Cells and bonds live in one arena shared by a body's whole lineage; a fragment is a
`BitMask` over it plus a `CentroidOffset`. Splits become allocation-free, and `PartitionFront`'s index
remapping — the fiddliest code in `FractureSimulator` — largely disappears because indices never change.

Two consequences to handle explicitly:
- `Cell.Local` is centroid-relative, so fragments read `Local[i] - offset` rather than mutating shared
  vertices. The `Transform.Position == world centroid` invariant is preserved.
- `DetachCellScale` mutates one cell's vertices, so that cell is copied out (copy-on-write).

### 2. `FractureTuning` becomes state, not statics
17 mutable statics in `FractureKernel.cs` become a `Tuning` struct inside `SimState`. A static is
invisible to snapshot, restore and the network — rule 5 of the determinism contract.

### 3. `Cell.Role` becomes an enum
`string` today, compared per-cell per-spawn and per-pulverise event. An enum removes the managed
reference from the hot struct and the string comparisons from the hot path.

### 4. `CellPulverizedEvent` carries the cell index
Today `FractureGameplay.cs:365` re-derives it by nearest-centroid from a world point — a latent
mis-attribution bug and a per-event allocation.

### 5. Arena pooling
Measured at ~2 KB and ~110 objects **per hit** at 100 cells (`CrackFront.Seed` plus `BeginFracture`'s
`List<int>[] adj`, one `List` per cell). Rent/return by size class. Exit criterion 5.

### 6. `Math` → `SimMath`
The banned-API analyzer enforces it. The transcendental surface across these files is small:
`Sin` ×11, `Cos` ×11, `Sqrt` ×7, `Pow` ×2, `Atan2` ×2.

### What is NOT changing

**`StepFront`'s linear frontier max-scan stays.** It is O(F) per pop and a binary heap would be faster —
but a heap changes tie-break order, and ties happen. Bit-identical by construction beats asymptotically
better. Cap cells-per-body instead. This is precisely the class of "optimisation" that silently breaks
lockstep, and it is called out in PORT_PLAN.md Phase 1.

---

## Sequencing

Dependency order, each step landing green before the next starts.

| # | Deliverable | Validation |
|---|---|---|
| 1 | `Geometry/` — `PolygonUtils` port (8 functions) | Differential vs legacy over randomised polygons |
| 2 | `Fracture/` data model — `Cell`, `Bond`, arena, `BitMask`, `FractureProperties` | Unit tests; arena/mask invariants |
| 3 | `VoronoiTessellator` | **Exit criterion 1** — five authored shapes, identical topology |
| 4 | `FractureKernel` — `StepFront` | Differential on scripted floods; the `StepFront` benchmark already proved this transcribes exactly |
| 5 | `FractureSimulator` — union-find, fragment build, `DerivedMotion`, `SplitLive` | Differential on split scenarios |
| 6 | `FractureService` + `FractureCrackSystem` | **Exit criterion 2** — 200 scripted scenarios |
| 7 | `SimState` + `Snapshot`/`Restore`/`Fingerprint` | **Exit criterion 3** |
| 8 | Arena pooling + allocation audit | **Exit criterion 5** |

Steps 1–2 are mechanical. Step 3 is the first real risk (the tessellator has fiddly concavity and
void-filling logic). Steps 4–6 are the crown jewels. Step 7 is where the netcode groundwork lands.

---

## Risks

| Risk | Mitigation |
|---|---|
| The arena+mask redesign changes behaviour subtly while looking correct | It is the *only* structural change made during the port of each file; everything else transcribes literally. The differential harness compares against the un-redesigned original |
| Tessellator concavity/void-fill logic is order-dependent in ways the source doesn't document | Port literally first, restructure only after criterion 1 is green |
| `SimMath` differences accumulate through a long flood and cross a threshold, flipping a discrete outcome | Exactly what criterion 2's 200 scenarios exist to detect. If it happens, that is a real finding about the model's sensitivity, not just a port bug |
| Scope creep into gameplay | `FractureGameplay.cs` is **not** in this phase. The engine spawns no entities; it publishes events |
