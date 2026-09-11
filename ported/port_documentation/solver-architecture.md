# Solver architecture: what every stage does, and why it is the way it is

A companion to `solver-anatomy.md`. That document is a performance census — where the time goes and
what is wasted. This one is the reference: what each stage *is*, what it iterates, what it reads and
writes, which stages carry order dependence and why, what is cached and when it dies, and for every
architectural decision that costs something, the reason it was taken.

Read `design-overview.md` first for the physics. This is the machine.

---

## 0. The model in one page

A body is a set of **rigid convex cells** joined by **bonds**. A cell never moves relative to its
body — it has no independent position. All deformation is bookkept on the bonds and realized as a
per-cell displacement `u`.

Three spaces, and confusing them is the source of most bugs in this file:

| space | what lives there | changes when |
|---|---|---|
| **rest** | `CellRx, CellRy, CellPhi`, the polygon `PolyX/PolyY` | only on split or plastic rebake |
| **deformed** | rest + `u` (`CellUx, CellUy, CellUth`) | every substep |
| **world** | body pose applied to deformed | every substep |

The solver works in rest space; the collider works in deformed space; the renderer works in world
space. `u` is the bridge, and it is derived, never integrated directly from forces — forces act on
bond *stretch*, and stretch is realized into `u` once per substep.

**The deviation field.** Each cell carries `CellDvx, CellDvy, CellDw` — a velocity *relative to its
body's rigid motion*. Bond forces push this field around; `DecomposeMotion` continuously extracts
whatever part of it is a rigid body motion and hands it to the body proper, leaving the field
zero-mean. That decomposition is what lets a fragment fly off with the right velocity the instant it
separates, without anyone computing "the fragment's velocity" explicitly.

---

## 1. Data model

Everything is flat parallel arrays (`SimState`), indexed by integer. There is no ECS, no per-entity
object, no pointer.

| population | index | scale |
|---|---|---|
| bodies | `b` | hundreds to thousands |
| cells | `c` | thousands to tens of thousands |
| bonds | `k` | ~2.7 per cell |
| shared-vertex groups | `g` | ~1.4 per cell |

**Three CSR-style index tables** map between populations. Each is an offset array, a length array
and a dense payload:

* `BodyCellOff/Len` → `BodyCells` — the cells of a body
* `BodyBondOff/Len` → `BodyBonds` — the bonds of a body
* `AdjOff/Len` → `AdjBond` — the bonds touching a cell

`Reindex()` rebuilds all three by scanning bonds in **index order**, and that ordering is
load-bearing well beyond tidiness: it is what makes per-body iteration bit-identical to global
iteration, which is what makes the parallel decomposition safe (§6.7).

**Why SoA and not objects.** Snapshot for rollback is a `memcpy` of a known set of arrays rather than
a graph walk, and the hot loops touch three or four arrays rather than pulling whole objects through
cache. The cost is that "a cell" is not a thing you can hold — it is an index and a discipline.

---

## 2. The tick, stage by stage

`Solver.Step()`. Substeps default to 9; **everything inside the substep loop therefore runs nine
times per tick**, which is the single most important fact about the cost of this solver.

```
Step():
  open cache epoch                                     §4
  BuildPairs()            once per tick                broadphase
  BuildManifold()         once per tick                narrow phase
  for substep in 0..8:
      refresh or rebuild manifold                      §5.4
      ApplyInertialLoads
      BondForces                pass 1
      SolveContact × contacts   SEQUENTIAL             §3.1
      BondIntegrate             pass 2
      DecomposeMotion
      Euler-torque cancellation
      Realize (u, cap, damping)
      body pose integration
      UpdateDamage
  RebuildBodies + DecomposeMotion                      once per tick  §6.5
  inertia update (conserve L)
  rubble positional nudge
  plastic rebake
  deep-overlap census → ConvertDust
  close cache epoch
```

### 2.1 BuildPairs — broadphase

**Iterates** every live cell twice: once to hash into a uniform grid, once to scan the
half-neighbourhood (own bucket plus the four buckets "ahead", so each pair is visited once).
**Emits** an array of candidate cell pairs.

Grid cell size is `largest body's cell size + margin`, where the margin is how far anything can
travel in a tick. Rejections in order: index dedupe (own bucket only), same body, circle radius with
per-body margins.

**Stateless.** Output depends only on current positions.

*Measured at 2,100 cells dense: 0.72–0.77 ms, ~8% of the tick.*

### 2.2 BuildManifold — narrow phase

**Iterates** the candidate pairs. Per pair: dead check, same-body check, circle test, then skin both
cells, then an exact AABB test, then SAT.

Produces a `Contact` array: cell pair, normal, contact point, depth, plus a reference pose
(`Depth0, Ax0, Ay0, Bx0, By0`) used by the cheap refresh.

Two things worth knowing:

* **The speculative margin.** Pairs are admitted with *negative* depth if they are merely close, so a
  contact that closes mid-tick is already in the manifold. This is why the SAT miss rate is 5% rather
  than 36% — near-misses are recorded once instead of rediscovered and thrown away nine times.
* **The deep-overlap normal guard.** SAT returns the axis of minimum penetration, and past about half
  a cell that axis flips to the far side and the contact pushes the impactor *through*. Beyond that
  depth the centre-to-centre direction is the only trustworthy normal.

**Stateless** within a tick (a fresh manifold each build), but the impulses it carries are not — see
§3.1.

*Measured: 2.36–2.73 ms, ~27% of the tick. The largest single stage.*

### 2.3 ApplyInertialLoads

**Iterates** bodies, then cells within each. Applies centrifugal, Euler and Coriolis accelerations in
the rotating body frame into the deviation field.

Two corrections live here, both of which were free-energy sources before they were found:

* **Centrifugal work is charged to rotational energy.** A body that expands under its own rotation
  slows down. With ω treated as a free parameter the load was unbounded.
* **The Euler torque is fictitious and is cancelled after decomposition.** Its field is exactly a
  rigid rotational acceleration, so it injects angular momentum that `DecomposeMotion` promotes into
  the body's own spin — and α is computed from that spin's change, closing a loop with gain ≈ −1 that
  sits on the stability boundary and grows geometrically. The load still reaches the bonds in full;
  only its rigid component is removed.

**Per body, independent.** Parallelised.

### 2.4 BondForces / BondIntegrate — two passes, and why

This is the core of the material model and it is **two separate loops over the same bonds**, not one.

* **Pass 1 (`BondForces`)** reads each bond's stretch `(sn, st, sa)`, computes force from stiffness
  and damage, and accumulates impulses into both cells' deviation velocities.
* **Pass 2 (`BondIntegrate`)** reads the *updated* deviation velocities and integrates each bond's
  stretch forward.

**Why two passes.** Every bond touching a cell must contribute to that cell's velocity before any
bond integrates stretch from it. One fused loop would let a bond see a velocity that half its
neighbours had not yet contributed to — and the propagation speed of a disturbance would then depend
on bond *index order* rather than on the material. Split into two passes, the update is **Jacobi**:
every bond sees the same, fully-assembled velocity field, and propagation at the material's wave
speed is emergent rather than imposed.

That is also why these two passes are **not** Gauss-Seidel and *can* be parallelised, unlike the
contact solve.

*Measured: bond forces 1.05–1.24 ms, integrate 0.65–0.78 ms; ~21% of the tick together.*

### 2.5 SolveContact — the one sequential stage

**Iterates** contacts in manifold order, applying a velocity impulse per contact. XPBD form:

```
dl = ((sep - vn) * h - alpha/h² * Ln) / (w + alpha/h²)
Ln = max(0, Ln + dl)                    one-sided: contacts push, never pull
```

then Coulomb friction on the tangent, solved *after* the normal so it sees corrected velocities.

Contact stiffness has a **floor independent of material wave speed** (`ContactCMin`): impenetrability
is kinematic, so a soft material must fracture rather than interpenetrate.

**This stage is Gauss-Seidel** — see §3.1.

*Measured: 0.45–1.52 ms, 5–15% depending on how much is in contact.*

### 2.6 DecomposeMotion

**Iterates** bodies, then their cells twice. Computes the deviation field's net linear and angular
momentum, adds it to the body's rigid motion, and subtracts it from the field — leaving the field
zero-mean.

This is what makes fragmentation work without special-casing. A piece about to separate already
carries its velocity in the field; when the bonds go, decomposition promotes that to the new body's
rigid motion. Nobody computes "the fragment's velocity".

It also keeps `u` free of rigid content, so the collider stays centred on the pose.

### 2.7 Realize

**Iterates** bodies, then cells. Integrates `u += dv·h`, applies the displacement cap, and applies
Rayleigh damping (`Relax`) to the field — which is what makes wave attenuation a material property.

**The cap is a constraint, not a clamp.** Scaling `u` back while leaving `dv` alone lets the field
accelerate into a wall forever, which is free kinetic energy. At the limit the outward velocity must
go too — and the removed velocity is *handed to the body*, making the limit an internal inelastic
collision: momentum exact, energy strictly down.

### 2.8 UpdateDamage

**Iterates** bonds. Cohesive-zone damage from history-max equivalent stretch, viscoplastic flow, and
bond breaking.

The fast path matters: an L1 bound on the equivalent stretch, taken before any transcendental or body
lookup, exits **93%** of visits. It is exact — rate sensitivity only raises thresholds and
compression below yield does nothing, so it cannot skip a bond that would act.

Breaking a bond sets `CellCracked` on both cells (a break *is* new surface), releases the stored
cohesive energy as a recoil impulse, and marks the body dirty.

*Measured: 1.11–1.49 ms, 13–15% of the tick.*

### 2.9 RebuildBodies — once per tick

Connected-component walk over the cells of **dirty bodies only**, appending new fragments in
parent-index order, re-anchoring bonds, recomputing mass and inertia, compacting emptied records, and
reindexing.

**Why once per tick and not per substep.** Breaking a bond stops it transmitting force immediately,
which is the part that matters physically. Re-partitioning the body is bookkeeping. Per substep it
meant up to nine component passes, nine membership rebuilds and nine broadphase rebuilds to reach a
state that only needed computing once. Separation velocity is not lost in the meantime — it lives in
the deviation field.

*Measured: 0.09–0.48 ms, ~2%. Was 4.0 ms before it became incremental.*

### 2.10 Settle: inertia, rubble nudge, rebake

* **Inertia follows the deformed shape, and `L` is the invariant.** Material that moves outward
  increases `I`, and angular momentum — not ω — is conserved. With `I` frozen at rest there is a free
  spin-up loop: centrifugal drives radial deviation, Coriolis turns it tangential, decomposition
  promotes it to spin, which strengthens centrifugal.
* **The rubble positional nudge** is the *only* positional correction in the solver, and it applies
  only when **both** sides are single bond-less cells. Such a cell has no bond network and therefore
  no deformation outlet — `u` stays zero and overlap has nothing to convert into. Everything else
  resolves overlap by deforming.
* **The plastic rebake** solves for the plastic displacement field and *redistributes* it between
  rest and `u`, making bends permanent. It moves rest offsets and rotations differentially, which is
  why it invalidates caches (§4) and why cells stop agreeing about shared rest corners afterwards.

### 2.11 ConvertDust — a classifier, not a physics channel

Runs on fracture **output** only: single-cell bodies that are either crushed (sustained deep overlap,
so the rubble has nowhere to go) or free (touched nothing for a few ticks). It can never touch a
bonded load-carrying cell, which is the structural reason comminution cannot short-circuit a stress
wave.

Crushed material hands its momentum to whatever presses on it as a perfectly inelastic transfer;
free material exports to a ledger so conservation still balances.

---

## 3. What is stateful, and why

### 3.1 The contact solve is Gauss-Seidel — the one hard sequential dependency

`SolveContact` reads cell velocities, computes an impulse, and **writes those velocities back before
the next contact is solved**. Contact *i+1* therefore sees the correction contact *i* just made.

That is deliberate and it is what makes stacks work. Under Jacobi (all contacts computed against the
same velocity snapshot, then applied), a cell squeezed between two others gets both corrections at
full strength, over-corrects, and the pile jitters or explodes. Gauss-Seidel converges far faster for
contact and is the standard choice for exactly this reason.

The price is that **contact order is part of the answer**. Change the order pairs are emitted in and
you change the result — not wrongly, but differently. This is why:

* the broadphase's half-neighbourhood scan was a behaviour change, not a free optimisation;
* the contact solve is the one stage that stayed sequential when everything else was parallelised;
* parallelising it would need graph colouring (partition contacts into sets that share no cell, solve
  each set in parallel), which is a real option and not yet taken.

### 3.2 Impulses persist within a substep, and reset between them

`Ln`/`Lt` accumulate across the *iterations within* a substep and are **cleared at the start of every
substep**. This is XPBD: λ is the multiplier for one timestep, and the compliance term `−α·λ/h²`
represents how much of the constraint this step has already answered.

Carrying λ into the next substep — which sounds like standard warm starting — makes that term
suppress new impulse rather than stiffen the contact. Measured, it drove peak penetration from 8.8 px
to 20.4 px. Warm starting is a sequential-impulse idea and does not transfer to this formulation
unchanged.

### 3.3 Everything else is order-independent

Bond forces and integration are Jacobi (§2.4). Inertial loads, decomposition, realize and damage all
write only state belonging to one body. That is what made the parallel decomposition possible, and it
is asserted rather than assumed: `BondsNeverCrossBodies` checks the premise, and
`ParallelMatchesSequentialExactly` checks the conclusion.

### 3.4 Damage is history-dependent by design

`BondLmax` is a running maximum and `BondDmg` never decreases. That is the physics — a cohesive zone
does not heal — but it means damage is genuinely stateful and a bond's response depends on everything
that has ever happened to it. It is also why the **stress** view and the **damage** view in the
viewer show different things: damage is a high-water mark, stress is the present moment.

---

## 4. Caches: what, keyed on what, dies when

Five caches, all keyed on a monotonically increasing counter rather than a boolean, so invalidation
is "bump the counter" and can never be forgotten for an individual entry.

| cache | holds | keyed on | rebuilt |
|---|---|---|---|
| `_bodySin/_bodyCos` | body pose trig | `_rotEpoch` | every substep, and at both tick boundaries |
| `CellCa/CellSa` | cell rest-rotation trig | `_s.Substep` | per substep; rebake clears the stamp directly |
| `_locX/_locY` | cell-local vertices (rest + u, rotated) | `_s.Substep` | per substep, on demand |
| `_skinX/_skinY` | skinned world polygon + AABB | `_s.Substep` | per substep, on demand |
| `_bondChunkBounds` | parallel chunk split | `(Tick, BodyCount)` | per tick, or when bodies change |

**Why epochs and not flags.** There are thousands of entries and a dozen places that could
invalidate. A per-entry flag has to be cleared everywhere; a counter compare cannot be forgotten.

**The skinning cache is the interesting one.** A cell in a crowded pile takes part in many candidate
pairs and presents the same polygon to all of them, so it is computed once per substep and reused.
Underneath it, `EnsureLocal` is a second layer: each shared vertex is the *average* of where the
cells sharing it put it, so skinning one cell reads its neighbours' local vertices, and those get
cached too. Measured hit rates: skinning 36–60%, local transforms 85–90%.

**The bug this shape caused, and the fix.** A tick used to open with a manifold build at the *same*
substep number the previous tick ended on. Between those two points the end-of-tick work had run —
splits re-centre cells, rebakes move rest offsets, the pose integrates — so the first contacts of
every tick after a topology change were built from where the geometry *used to be*. Momentum, energy
and the shared-vertex gap all stayed green, because the stale polygons were internally consistent,
just in the wrong place. The tick now **opens and closes** a cache epoch, and
`CollidersStayOnTheCellsTheyBelongTo` asserts the invariant directly.

---

## 5. How a contact is built, end to end

1. **Grid insert.** Every live cell is hashed into a uniform grid by its deformed centre. Grid cell
   size is the largest body's cell size plus a speed margin.
2. **Half-neighbourhood scan.** For each cell: its own bucket (index-ordered to dedupe) plus four
   buckets ahead. Rejections: same body, then a circle test using **per-body** margins — each body
   carries its own reach, so one fast bullet no longer inflates every test in the scene.
3. **Skin.** Each surviving pair's two cells are skinned: for every polygon vertex, average the
   positions its shared-vertex group members give it, then transform to world. This averaging is what
   makes a gap between two bonded cells *unrepresentable* — the model cannot show cracks that are not
   really there.
4. **Exact AABB**, computed as a by-product of skinning.
5. **SAT** on the two convex polygons, with the speculative margin, returning the minimum-penetration
   axis. Then the deep-overlap normal guard.
6. **Refresh, not rebuild.** For subsequent substeps, depth is recomputed from how far the two cells
   have translated along the held normal — a handful of operations against ~490 ns for a full pair
   test. The normal and the cell pairing are held.
7. **Staleness check.** Held normals go stale when things move, so before each substep the solver asks
   how far any body has travelled since the manifold was built. Past a threshold (`ManifoldDrift`,
   0.02 of a cell) it re-derives instead. In a settled pile that fires once a tick; under a fast
   impactor, every substep — which is what correctness there costs.

**Consumers of a contact.** `SolveContact` (the impulse); the rubble positional nudge (both sides
single cells); and the deep-overlap census that feeds `ConvertDust`. Contacts are *not* rebuilt when
topology changes — they reference cell indices, which are stable, and `SolveContact` re-reads
`CellBody` each substep so a contact whose cells merged into one body is skipped.

---

## 6. Architectural decisions, and what each one costs

### 6.1 Rigid cells, deformation on the bonds

**Cost:** a cell can never change shape, so all deformation is inter-cell. Fine detail needs more
cells, and cells are the expensive population.
**Why:** a deformable cell needs its own integrator, its own collision geometry per frame, and its
own stability limit. Rigid cells make the collider a fixed convex polygon and the body a pose plus a
field — which is what makes snapshot cheap and rollback plausible.

### 6.2 Shared-vertex skinning

**Cost:** skinning a cell costs a walk over its vertices' groups, roughly 3× a naive transform, and
it is the single largest component of the narrow phase.
**Why:** it makes gaps between bonded cells geometrically impossible. The alternative — each cell
drawing its own polygon — shows cracks opening between cells that are still bonded, which was the
single most damaging visual artefact in the prototype line.

### 6.3 No positional solver for bonded bodies

**Cost:** overlap resolves only as fast as the material can deform, so a hard impact penetrates
visibly before it stops.
**Why:** overlap **is** deformation in this model, consumed elastically by `u` and permanently by the
plastic rebake. A positional push would teleport material and break the rest+u invariant that the
collider depends on. The one exception is bond-less rubble, which has no deformation outlet at all.

### 6.4 Fixed substep count

**Cost:** this is the big one — see §7. Nine substeps means everything in the substep loop costs nine
times, which is 54% of the frame, and the number is a constant while the stability limit it exists to
satisfy scales with cell size.
**Why it was taken:** it matched the prototype and it is simple. It is now the clearest architectural
debt in the solver.

### 6.5 Topology settled once per tick

**Cost:** a body is "wrong" for up to nine substeps after a bond breaks — still one body when it is
really two.
**Why:** the break stops force transmission immediately, which is the physical part. The rest is
bookkeeping, and doing it per substep cost 4.0 ms of a 12.4 ms tick to reach a state that only needed
computing once.

### 6.6 Uniform grid broadphase

**Cost:** the grid sizes itself to the *largest* body's cell size, so mixed scales are badly
proportioned for the small end. 94k of 332k bucket visits are same-body — cells of one body are
neighbours in the grid by construction.
**Why:** it is O(1) insert and probe, allocation-free, and trivially deterministic. A two-level
broadphase over body bounds would remove the same-body waste entirely, but at 11 cells per body the
cell-versus-cell inner loop costs more than it saves. It becomes right when bodies get large — which
is also when the multi-resolution problem arrives, so the two belong together.

### 6.7 Determinism as a hard constraint

**Cost:** no hash-container iteration, no wall clock, no `Array.Sort` (unstable), a software libm, and
threads confined to one file with a fixed partition. Several obvious optimisations are unavailable.
**Why:** lockstep multiplayer. A single differing bit desyncs a match. The constraint is enforced by
a banned-API analyzer, a three-platform fingerprint gate in CI, and a test asserting that one worker
and twelve produce identical bytes.

### 6.8 float32 state

**Cost:** ~7 significant digits, and world coordinates in the thousands leave ~3e-4 px of resolution.
**Why:** half the memory bandwidth on the hot arrays and half the snapshot size. Validated by a
precision spike before it was adopted.

---

## 7. The scaling law, and the CFL trap

**Cost scales as 1/h³, where h is cell size.** Cell count goes as 1/h², and the stable timestep of an
explicit integrator goes as h — so required substeps go as 1/h. Halving the cell size is eight times
the work, not four.

That second factor is not currently automatic, and that is a bug rather than a trade-off. Substeps
are a fixed constant; cell size is a slider. The CFL number — how far a stress wave travels in one
substep as a fraction of a cell — is

```
CFL = c · Dt / (substeps · h)
```

For rock (`c` = 2600 px/s) at 9 substeps:

| grain | cell size | CFL | spin scenario, 150 ticks |
|---:|---:|---:|---|
| 900 | 30.0 px | 0.16 | 1 body, 0 broken, 99.1% energy |
| 400 | 20.0 px | 0.24 | 1 body, 0 broken |
| 200 | 14.1 px | 0.34 | 1 body, 0 broken |
| 100 | 10.0 px | 0.48 | 1 body, 0 broken |
| 60 | 7.7 px | 0.62 | 1 body, **2 broken** |
| 30 | 5.5 px | 0.88 | **349 bodies, 4337 broken, 74% energy** |

The spin scenario is a single body rotating in vacuum with no contacts. It should be stable forever
at any grain. At grain 30 it disintegrates — and raising substeps fixes it completely:

| substeps at grain 30 | CFL | result |
|---:|---:|---|
| 9 | 0.88 | 349 bodies, 4337 broken |
| 12 | 0.66 | 1 body, 1 broken |
| 16 | 0.49 | 1 body, **0 broken**, 99.0% energy |

**So bonds are not weaker at fine grain.** The bond formulas are scale-invariant, and provably so:
stiffness is `ρc²·(L_side/h)` and failure stretch is `ε·h`, so failure *stress* is `ρc²ε`,
independent of h. What fails is the integrator. The deviation field diverges numerically, and the
damage model — which cannot tell numerical divergence from real load — faithfully reports it as
fracture.

**Convergence needs CFL ≈ 0.25, not 0.5.** A gentle spin is stable at 0.5, but a hypervelocity impact
is not: at grain 900 the projectile scenario breaks 181 bonds at 2 substeps, 69 at 3, and settles to
~35 by 6. Impacts carry a sharp wavefront and need the tighter bound.

**Two consequences.**

1. **Fine grain is under-integrated today.** Anything below about grain 200 at 9 substeps is
   over-fracturing, and below grain 60 it is producing fracture that is purely numerical.
2. **Coarse grain is over-integrated.** At grain 900 the CFL is 0.16 where 0.25 would do — so ~6
   substeps suffice where 9 are spent. That is a third off 54% of the frame, available for free.

The fix is to derive substeps from the CFL condition and the smallest live cell, with the tuning knob
becoming a safety factor rather than a count. The viewer now displays the CFL number and the substeps
it implies, so the trap is at least visible while that is outstanding.
