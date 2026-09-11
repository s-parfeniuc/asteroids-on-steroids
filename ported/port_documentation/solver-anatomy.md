# Solver anatomy: what a tick actually does, and what it wastes

Measured on `ported/AsteroidsSim`, i5-13420H, single-threaded, Release. Reproduce with
`dotnet run --project tools/FractureBench -c Release -- --detail --scene dense`.

Reference scene unless stated otherwise: **192 asteroid bodies, 2,135 cells, 4,058 bonds, 9
substeps, bodies just touching**. Two neighbours for context: `sparse` (same bodies, spread out) and
`worst` (bodies start half-interpenetrating).

| scene | cells | before P1+P2 | now | cells inside an 8 ms budget |
|---|---|---|---|---|
| sparse | 2,135 | 6.1 ms | 2.3 ms | 4,100 → 7,700 |
| dense | 2,135 | 12.4 ms | 3.3 ms | 1,300 → 5,100 |
| worst | 2,135 | 20.2 ms | 5.2 ms | 940 → 3,000 |

**At 20k cells**, which is the target, on this machine and one thread:

| shape | cells | ms/tick | cells inside 8 ms |
|---|---|---|---|
| many small bodies, spread out | 18,576 | 19.5 | 7,650 |
| many small bodies, touching | 18,576 | 28.3 | 5,250 |
| few large bodies (140 cells each), spread out | 24,724 | 30.9 | 6,350 |
| few large bodies, touching | 24,724 | 43.4 | 4,375 |

Scaling is linear in cells across the whole range, so the shortfall is a constant factor: **2.6× to
4.6× depending on scene shape**, and it does not get worse with size. Large bodies are the harder
shape per cell, not the easier one — they carry 2.63 bonds per cell against 1.90, and the bond
passes are the largest block of the frame.

The work counters cost nothing measurable (12.37 ms with them, 12.58 ms without). The coherence
measurement in `--detail` does cost ~25%, so timings quoted here come from the plain run.

---

## 1. The data model

Everything is flat parallel arrays indexed by integer. There are four populations:

| population | count here | indexed by | what it is |
|---|---|---|---|
| cells | 2,135 | `c` | a convex Voronoi polygon, rigid, never moves relative to its body |
| bonds | 4,058 | `k` | a *shared side* between two cells, carrying stretch |
| bodies | 192→383 | `b` | a rigid frame: pose, velocity, material |
| shared-vertex groups | ~2,900 | `g` | the ~3 cells that own one Voronoi corner |

Three derived index tables are rebuilt whenever topology changes: `BodyCells` (which cells belong to
a body), `BodyBonds`, and `AdjBond` (which bonds touch a cell). All three are built by scanning in
**index order**, which is what makes every later loop deterministic.

The one thing to hold onto: **the solver works in rest space, the collider works in deformed
space.** A cell's rest offset `(rx, ry)` never changes except when the body splits or plastically
rebakes. Deformation lives in two places — `u` per cell (realized displacement, what you see) and
the bond stretches `sn/st/sa` (what produces force). Shared-vertex skinning reconciles them.

---

## 2. One tick, top level

```
Step():
  BuildPairs()                      ← once per tick          0.69 ms  12%
  BuildManifold()                   ← once per tick          2.35 ms  40%  (shared with the rebuilds below)
  for substep in 0..8:              ← nine times
      if the scene moved: BuildManifold()  else: RefreshManifold()
      ... see §3 ...                                         2.51 ms  43%
  inertia/L update, rubble nudge, rebake                     0.02 ms   0%
  ConvertDust() [+ RebuildBodies]                            0.16 ms   3%
```

### BuildPairs — the broad phase

Hashes every live cell into a uniform grid (cell size = largest body's cell size + a
speed-dependent margin), then for each cell scans the 3×3 neighbourhood and emits candidate pairs.

Measured per tick: **365,277 bucket visits → 15,747 pairs.** 96% of the visits produce nothing:

| outcome | per tick | why |
|---|---|---|
| rejected by `o <= c` | 189,151 | **the symmetric-pair dedupe** — every pair is visited twice |
| rejected same body | 54,548 | cells of one body can never contact each other |
| rejected by radius | 106,334 | circles don't reach |
| emitted as a pair | 15,747 | |

It is called **6 times per tick**, not once: once at the top, and once more after every substep in
which a bond broke. At ~123 µs per call that is ~0.6 ms hidden inside the `Split` phase.

The cost is dominated by `Dictionary.TryGetValue`: 9 probes per cell per call ≈ 19,000 probes per
call, 115,000 per tick.

---

## 3. One substep, in order

Nine of these per tick. Counts are **per substep** for the reference scene.

| # | stage | iterates | count | cost |
|---|---|---|---|---|
| 1 | `BuildContacts` | candidate pairs | 2,749 | **0.91 ms** |
| 2 | `ApplyInertialLoads` | bodies, then their cells | 295 / 2,109 | 0.014 ms |
| 3 | `BondForces` | all bonds | 4,058 | 0.06 ms |
| 4 | `SolveContact` | contacts, in build order | 533 | 0.04 ms |
| 5 | `BondIntegrate` | all bonds | 4,058 | 0.03 ms |
| 6 | `DecomposeMotion` + Euler cancel | bodies, then their cells | 449 / 3,210 | 0.02 ms |
| 7 | realize `u`, cap, damp, integrate | all cells, then all bodies | 2,109 | 0.02 ms |
| 8 | `UpdateDamage` | all bonds | 3,688 | 0.06 ms |
| 9 | on a break: `RebuildBodies` + `BuildPairs` + `DecomposeMotion` | ~0.55 calls | | 0.44 ms |

**The physics is cheap.** Stages 2–8 — the entire bonded-particle model, inertial loads, cohesive
damage, plasticity and rigid decomposition — come to about **0.25 ms per substep, 2.2 ms/tick, 18%
of the frame.** Everything else is collision detection and topology bookkeeping.

### Stage 1 — the contact manifold, 40% of the frame

The manifold is built **once per tick**, not once per substep, and refreshed in between. A refresh
re-derives each contact's depth from how far its two cells have translated since the build, projected
on the contact normal — a handful of operations against roughly 410 ns for a pair test. The normal
and the cell pairing are held.

Held normals go stale when things move, so the substep loop checks first: if any body has moved more
than `ManifoldDrift` cells since the build (0.02, so 0.6 px), the narrow phase runs again instead.
In this scene that fires about once per tick, giving 2 builds per tick against the previous 9.

For each candidate pair the build rejects on: dead cell, same body, circle radius, then AABB, then
runs SAT. Pairs are admitted with a **speculative margin** equal to the broadphase margin, so a pair
that is merely close is already in the manifold, with a negative depth, when it closes mid-tick.

Per tick: 5,748 pair tests → 1,737 SAT calls → 1,656 contacts.

| gate | rejects/tick | share of tests |
|---|---|---|
| circle radius | 2,240 | 39% |
| AABB | 1,771 | 31% |
| **SAT says separated** | **82** | 1% |
| contact produced | 1,656 | 29% |

The interesting line is the third. SAT was 36% miss before; it is now **5%**. That is the speculative
margin paying for itself: a call that would previously have found "separated by 3 px" and thrown the
answer away now records a contact that the next eight substeps refresh for free.

Skinning is cached: 7,016 requests, 60% served from cache. Underneath, the cell-local vertex
transform is cached too: 37,533 requests, 90% hits.

### Stage 9 — splitting, 2% of the frame

`RebuildBodies` is incremental and dirty-flag driven: breaking a bond marks only its own body, and
the connected-component walk visits only marked bodies' cells. Bond re-anchoring and `Reindex` are
restricted the same way, and the whole thing is batched to once per tick rather than running after
every substep that broke something.

Per tick here: 2 walks, 57 cells walked, 176 bonds re-anchored — against 10,961 cells walked and
9,837 bonds re-anchored before. The measured cost fell from 4.0 ms to 0.09 ms.

Renumbering is part of the determinism contract: new fragments are appended in parent-index order,
and `CompactBodies` removes emptied records order-preservingly. The empty-body case is not
hypothetical — dust always empties a single-cell body, and the first version of this leaked those
records, producing `M = 0` bodies and 214,015% kinetic energy. The conservation guardrails caught it.

### Stage 8 — damage

The L1 early-out is doing its job: of 33,188 bond visits per tick, **24,102 (73%) exit on the cheap
bound** before any transcendental or body lookup. 7,554 reach the cohesive law, and essentially all
of them accumulate damage without breaking — which is correct physics, not waste.

---

## 4. Where the avoidable work is, ranked

At 18.5k cells the frame divides roughly in half: **collision 12.8 ms (43%)**, **the physics
itself 15.1 ms (51%)**, topology and the rest 1.8 ms. That is a reversal of the 2k-cell picture,
where the physics was 18%. Nothing here is bit-safe unless it says so; everything else needs the §5
treatment.

### Landed

| change | effect | bit-safe |
|---|---|---|
| Persistent manifold (P1) | narrow phase 8.2 → 2.4 ms | no — validated by ensemble |
| Incremental topology (P2) | Split 4.0 → 0.48 ms | no — validated by ensemble |
| Half-neighbourhood broadphase | bucket visits 567k → 332k, BuildPairs 5.8 → 3.9 ms | no — changes pair order |
| Per-body broadphase margin | pair tests 36.1k → 29.3k | **yes** |
| Packed broadphase fields | BuildPairs −0.24 ms | **yes** |
| Per-body bond iteration | none alone; it is what makes the bond passes parallelisable | **yes** |
| Deterministic worker pool | 2.8-3.3x on 55% of the frame | **yes** — asserted, see §4 |
| Dead code in `ConvertDust` | one cell pass and one contact pass per tick | **yes** |

The per-body margin is worth a note as the shape of a good optimisation here: the broadphase sized
every pair's reach from the FASTEST body in the scene, so one bullet inflated every test. Giving
each body its own reach removed 19% of pair tests and could not remove a pair that might touch, so
the fingerprint did not move.

### Tried and rejected

* **A skin-free conservative box before the narrow phase.** The idea was sound and the bound is
  provably conservative, but it is a wash: it removes 30% of the skinning and the box costs what the
  skinning saved. It also needs a per-cell bake invalidated on every plastic rebake, which is a
  maintenance hazard for no gain. Removed. It did find the bug in §4.1, which was worth the trip.
* **Warm-starting contact impulses across substeps.** See §1 — wrong for XPBD.

### 1. Everything is nine times

`Substeps = 9`, and every per-cell and per-bond pass runs inside that loop: bond forces, integration,
damage, realize, decompose, inertial loads and the manifold refresh together are **15.9 ms, 54% of
the frame**, all of it multiplied by nine. The substeps exist for the bond CFL condition — the
stress wave must not cross a cell in one step — which is a property of a body that is *carrying* a
wave. A body that is not gets nine passes to compute nothing.

This is the largest single lever left and it is what sleeping (§3) actually buys.

### 2. The broadphase still visits 332,000 buckets to emit 30,000 pairs

Of the 332k visits, **94k are same-body** — cells of one body are neighbours in the grid by
construction — and **175k are out of range**, which is inherent to sizing a uniform grid to the pair
reach. A two-level broadphase over body bounding circles removes the same-body waste entirely, but
at this grain (11 cells per body) the cell-versus-cell inner loop costs more than it saves. It
becomes the right structure when bodies are large, which is also when the multi-resolution problem
(§6) arrives, so the two belong together.

### 3. Nothing sleeps

In the sparse scene 44% of cells have a quiet deformation field and 31% of bodies are quiet and
untouched for three ticks; at 18.5k dense it is 20% and 16%. A sleeping body would skip the whole of
§1 for itself. This is also what carries the "far more frozen" half of the target, and it shares its
per-body activity bookkeeping with the manifold's staleness test, which is currently a **global**
maximum — one fast body forces every contact in the scene to be re-derived.

### 4. Parallelism: 2.8x to 3.3x on the dispatched work

`SimJobs` is a fork-join parallel iterator: a fixed partition of the body array, chunks claimed
dynamically by a persistent worker pool, per-chunk accumulators merged in chunk order. Six passes go
through it — inertial loads, bond forces, bond integration, rigid decomposition, realize, and
cohesive damage — which is **55% of the frame** at 18.5k cells and 57% at 27.7k.

| scene | dispatched work, sequential | best parallel | speedup | whole tick |
|---|---|---|---|---|
| 18,576 cells | 16.2 ms | 5.8 ms (32 chunks, 6-11 workers) | **2.8x** | 29.5 → 22.1 ms |
| 27,674 cells | 26.1 ms | 8.0 ms (64 chunks, 6 workers) | **3.3x** | 45.5 → 31.3 ms |

Bigger scenes scale better, and the surface is flat between 8 and 256 chunks — anything in that band
is within noise of the best. Below 8 chunks load imbalance bites; above 512 the dispatch overhead
does. The default is 32.

#### What the first attempt got wrong

This was initially measured as a *loss*, and reported as one, on the strength of a synthetic
benchmark that scaled 5.8x at light load and 0.73x at heavy load — which looked like a thermally
limited laptop refusing to sustain all-core clocks. The clock does fall under load, from ~4.6 GHz to
around 1 GHz, but that was not the cause. Two real bugs were:

* **`SpinWait.SpinOnce` escalates to `Thread.Sleep(1)`.** The completion wait used it. The caller
  reaches that wait having already run its own chunks, so it is waiting microseconds on a straggler
  — and instead it parked for a millisecond, eighteen times a tick. This is what produced the
  signature that looked like thermal throttling: at 2 chunks the dispatched work measured **2.7x
  slower** than sequential, and the penalty faded as chunk counts rose only because more chunks left
  the caller less idle time in which to fall asleep. Fixed by pausing and then yielding, never
  sleeping. Workers still *block* between dispatches, deliberately: 45% of the frame is sequential,
  and an earlier version that spun there made the whole frame 33% slower by stealing those cores.
* **Fork-join state reuse.** `RunChunks` cached the delegate and bounds in locals on entry, before
  claiming a chunk. A worker finishing the last chunk of dispatch N would loop, find the claim
  counter already reset by dispatch N+1, and run a new chunk with the old delegate. It desynced
  about one run in five. Fixed by reading the job description *after* the claim, with the caller
  publishing it before opening claiming.

The synthetic benchmark was also the wrong test: it was a tight FMA loop, and the bond passes are
gather-bound at 19 ns for roughly thirty flops. A core stalled on a cache miss is not spending the
power budget, which is why memory-bound work parallelises here better than compute-bound work.

#### What determinism actually requires

Narrower than first documented, and worth stating precisely:

1. **Chunks must own disjoint bodies.** This is correctness, not determinism: a bond writes both of
   the cells it couples, so partitioning by cell or by bond would hand one cell to two chunks.
   Partitioning by body is safe because a bond never crosses one — asserted by
   `GuardrailTests.BondsNeverCrossBodies`, not assumed.
2. **Cross-chunk float reductions must merge in a fixed order.** Float addition is commutative but
   not associative, so regrouping a sum changes it and the chunk count becomes part of the answer.
   Integer counters are associative and need no such care.
3. Given those, **chunk count is a pure performance knob today** — nothing reaching the fingerprint
   is reduced across chunks, and 2 through 1024 produce identical bytes.

`GuardrailTests.ParallelMatchesSequentialExactly` asserts the whole thing across five worker/chunk
configurations, and a 48-run stress across six configurations is clean. That test is the reason the
threading ban can be lifted for one file at all.

#### What is left sequential

The remaining 43-45% is the broadphase query, the narrow phase, the contact refresh, the contact
solve, and topology. Only the contact solve is genuinely sequential — it is Gauss-Seidel across
contacts, and would need graph colouring. The broadphase query and narrow phase are per-cell and
per-pair and decompose the same way the bond passes do, emitting into per-chunk buffers concatenated
in chunk order; together they are ~12 ms at 18.5k. Doing them would take the parallel share to about
92% and the whole-frame speedup from 1.35x to roughly 2.3x, which is the difference between 8,000
and 13,000 cells inside the budget.

### 4.1 The bug the box bound found

Worth recording, because it survived every existing guardrail. The skinned collider polygons and the
cell-local vertex transforms are cached per substep, and the trig cache per rotation epoch. A tick
used to open with a manifold build at the **same substep number the previous tick ended on** — and
between those two points the end-of-tick work had run: splits re-centre a body's cells, a plastic
rebake moves rest offsets and rotations, dust removes cells, the pose integrates. So the first
contacts of every tick following a topology change were built from where the geometry used to be.
The same held for any reader calling into the solver after `Step` returned, which includes the
renderer.

Momentum, energy, the shared-vertex gap and every morphology band stayed green throughout: the stale
polygons were internally consistent, just in the wrong place. What exposed it was a bound that could
not be violated being violated — a cell whose rest offset was `(0,0)` and whose polygon spans 18 px
produced a collider vertex 214 px away, still carrying the offset it had had inside the body it was
cut out of.

The fix opens a fresh cache epoch at the start of a tick and closes one at the end.
`GuardrailTests.CollidersStayOnTheCellsTheyBelongTo` now asserts the invariant directly: a collider
vertex within a small multiple of its own cell's circumscribed radius. Healthy is 1.0–1.7; the bug
read 6.4 with half the fix and 12 with none.

### 4.2 The crash fine grain found

Every reference scenario runs at grain 900 — 30 px cells — and at that size a body *cracks*: it
comes apart into a few tens of pieces over many ticks. Drop the grain to 30, giving 5.5 px cells, and
it does not crack, it **disintegrates**: a 3,800-cell target goes from two bodies to over a thousand,
and several hundred of them can appear in a single tick.

`RebuildBodies` reserved a fixed 256 slots of headroom in `_parentOf` at the top of the call, sized
from the body count on entry. That is a bet on how much one tick can shatter, and a fine-grain body
loses it — `IndexOutOfRangeException` in `InheritBody`, at tick 24 of a plain projectile shot. Every
other per-body scratch array in that file already grew on demand; this one was the exception, and now
grows the same way.

Two things worth taking from it. The reference scenarios are all coarse, so **no amount of running
them would have found this** — it took a knob that goes somewhere they do not. And it is not an
exotic case: any violent hit on a finely-grained body reaches it, which is to say the game would
have. `GuardrailTests.AViolentShatterDoesNotOutrunItsScratchArrays` now runs a grain-30 projectile
past the point it used to die.

### 5. Presentation is not the constraint — measured, not assumed

`AsteroidsGame/Viewer` runs the real solver on the reference scenarios and times three stages
separately: the tick, the geometry build, and the `RenderingServer` submission. Headless, it prints
that readout and exits, so the split can be collected scenario by scenario
(`--headless -- --scenario N --cols C --rows R`).

Field scenario, 120 ticks, this machine:

| cells | tick | build | submit | render share |
|---:|---:|---:|---:|---:|
| 326 | 5.09 ms | 0.30 | 0.09 | 7.1% |
| 1,179 | 6.08 ms | 0.55 | 0.16 | 10.5% |
| 2,117 | 7.67 ms | 0.98 | 0.30 | 14.3% |
| 3,551 | 11.95 ms | 1.66 | 0.49 | 15.2% |
| 5,800 | 19.15 ms | 3.64 | 1.67 | 21.7% |

Those numbers include the viewer's debug overlay — a line per cell edge and a line per bond, which
the game will not draw. With fills only, 5,800 cells costs 2.38 ms build + 0.76 ms submit, a **14.3%
share**.

**Decision: do not move drawing off the main thread.** The rule set before measuring was to split the
sim onto its own thread only if build+submit were a material share *and* physics were otherwise
inside budget. The second half fails, and not marginally: at 5,800 cells the tick alone is 19 ms
against a 16.7 ms frame. Making drawing entirely free would raise the cell budget by roughly the
render share and no more, while costing a snapshot per tick, a frame of latency, and contention
between the render thread and the `SimJobs` workers. Physics is the constraint by six to one.

Worth revisiting when a tick fits the frame — the decomposition is not hard, and §4's remaining
sequential work is the better target first.

#### A correction to Spike A

Spike A projected 1.17 ms to build 20,000 cells of geometry, from a spike whose cell polygons were
cached in body-local space **once at startup**. That is right for rigid cells and wrong for these:
every cell in this model deforms every tick, and the shared-vertex skin has to be re-derived from
its neighbours each time. Measured here the build costs ~0.41 µs per cell, which puts 20,000 cells
at roughly **8 ms, not 1.2 ms**. The batched submission path Spike A validated is unaffected — that
conclusion stands — but the geometry build is genuinely per-tick work and should be budgeted as
such.

---

---

## 5. What constrains all of this

Four properties make optimisation harder here than in a normal physics engine. Three are deliberate;
the fourth is inherent and was underestimated.

1. **Gauss-Seidel everywhere.** Contacts are solved sequentially in build order, and bonds are
   visited in index order. Any change to *which* order things are visited in is a change to the
   answer. This is why "obviously equivalent" reorderings are not equivalent.
2. **Determinism is a hard requirement.** No hash-container iteration, no threading, no wall clock.
   The `Dictionary` in the broadphase is safe only because it is probed and never enumerated.
3. **No positional solver.** Overlap is deformation, consumed elastically by `u` and permanently by
   the plastic rebake. That is what makes contact *depth* physically meaningful — and what makes
   stale depths riskier here than in an engine that just pushes bodies apart.
4. **The outcome is chaotic, so a single run proves nothing.**

### Why point comparisons do not work here

Multiplying the impact speed by 1.000001 — one part per million, below any perceptible difference —
moves the glass scene from 9 fragments to 6 and its big-mass share from 70% to 83%. Across a
thousandfold range of such perturbations, with everything else identical:

| scene | fragments | bonds broken | big-mass % | peak overlap |
|---|---|---|---|---|
| collide | 5–7 | 216–227 | 68–90 | 17.4–24.6 |
| projectile | 2–5 | 20–23 | 97–99 | 8.8–11.2 |
| glass | 6–17 | 268–286 | 40–83 | 20.8–23.2 |
| steel | 2 | 7–11 | 97–98 | 15.8–20.6 |

A bond that breaks one substep earlier redirects a crack, and everything downstream of it differs.
That is the model working, not a defect — but it means an optimisation compared against one recorded
run will look like a regression about as often as not, and the first read of the manifold change
did exactly that: fragment counts fell, overlap rose, and most of it turned out to be the scene
landing in a different one of its natural outcomes.

Two consequences, both now built in:

* **Morphology is asserted over an ensemble** of imperceptibly perturbed runs, and only properties
  that hold across all of them are asserted at all (`GuardrailTests.Ensemble`). One test measures the
  spread itself, so the claim stays honest if the model's sensitivity ever changes.
* **Metrics are read as ranges.** `--manifold` and `--sensitivity` in FractureBench report ranges
  over an ensemble for exactly this reason. Reading ranges is what located the manifold threshold:
  peak penetration turned out to be the one metric that responds *systematically* rather than
  chaotically, stepping from 8.9–10.4 px at 0.02 to 12.9–21.0 at 0.05, and that step is what set the
  value.

Determinism is untouched by any of this. The same build on the same input must still produce one
fingerprint, on three platforms; chaos is sensitivity to *inputs*, not to execution.
