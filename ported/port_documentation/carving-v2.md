# Carving v2 — surface recession on touch records

Status: **all milestones built and measured — data, driver, propagation, area-targeting, corners,
momentum.** Awaiting the by-eye check. Open decisions at the end. Last updated 2026-09-17.

## Why

Carving v1 chose a clip plane **per cell** from a **direction** (the contact normal, steered to reach open
surface). The thing being deformed is the **surface**, which is shared between cells, so every property
needed — continuity across cells, never cutting along an interface, both copies of a shared side
agreeing — was a property of the surface that a per-cell plane could only approximate afterwards. Each
approximation was a guard (steer, shield, half-`BondLen`, relink, continuity blend) and the guards
fought over where the plane went. Measured on the 224/245 repro (collide rock, grain 170, toughness
1.1, continuity 0.75, confine 0.10): steer aimed at a corner 4° off an interface, the shield kept one
end, the guard kept half, the relink missed by 0.003 px, and the neighbour reported half its side
exposed. The state going in was correct; the mechanism made it wrong.

What we want is what plastic displacement gave: a smooth dent whose interfaces move with the
surface, at the cost of surface-only state instead of a DOF per cell.

## Principle

> **A dent is the recession of exposed points.** An exposed point is either the exposed end of a
> touch record — which slides along its interface, shortening it for both cells at once — or an
> interface-less corner, which moves toward its cell's centroid.

A surface vertex where the outline meets an interface *is* the exposed end of a record, and the record
already stores its span along the seed bisector. Sliding that end inward shortens the interface for
**both** cells because they read the same number. Each cell's surface side is the chord between its
consecutive exposed points; a chord of a convex polygon keeps it convex. Continuity is the data
structure, not a term.

By construction:
- neighbouring cells' surfaces meet at the shared record end — no steps;
- a cut can never run along an interface — the cut is the surface, moved;
- both copies of a shared side agree — there is one span;
- interior cells cannot erode — they have no exposed point — they can only become surface;
- erosion through a neck is a span reaching zero — no tolerance;
- a fully receded interface is a **vertex** for every cell that met there, not a side.

## Data

Per touch record (existing `TouchA/B`, `TouchBond`, current span `TouchT0/T1`):
- `TouchS0/S1` — the span as built, immutable. Kept for diagnostics and the bare-stretch view.
- `TouchOpen` — two bits: is the T0 / T1 end exposed. Set at build for ends on the body outline
  (`BodyBuilder.EndOnFreeSide`); set on propagation. **Nothing else ever sets an open bit.**
- Records are in the **fingerprint** (they were not; a span desync would not have moved the hash
  until it moved a polygon).

Per cell: the stored polygon stays and is re-derived in place, so contacts and rendering are
unchanged. It is never a source of truth about adjacency.

Corners carry no state: the vertex position is the state.

`SideTouch` / `PolyBond` labels survive as the derivation's cache for the renderer and the crack rule.

## Driver (`Solver.Dent.cs`)

Inputs unchanged: a contact gives a **location** (`ct.Px/Py`, world) and a **pressure**; the rate law
gives `depth = CrushRate · excess / impedance · h` as in v1. **No direction is used.**

1. Walk the adjacency outward from the loaded cell while cells are within `R + CellRad` of the
   contact point, `R = Material.Dent × CellRad` of the loaded cell. Bodies interpenetrate one to two
   cell layers, so the surface is often two or three rings out; walking one ring left 79% of calls
   with no surface to move.
2. Every exposed point reached gets weight `k = ½(1 + cos(π·d/R))`, zero beyond `R`.
3. **Area-targeting** (built): `A* = depth · perim/4` (v1's target, so `CrushRate`
   keeps its calibration); each point's area per unit motion from the current geometry — a record end
   sweeps `½·|u × (P − V)|` per adjacent surface side, a corner `½·|P₁P₂|·cos φ`; one scale
   `λ = A* / Σ (dA/ds)ᵢ·kᵢ`; motion `λ·kᵢ`, clamped to the record's span. Area removed ≈ `A*`
   regardless of geometry; the kernel decides where, not how much. Before this, motion was
   `depth·kᵢ` directly and the area removed per unit depth spanned an order of magnitude with
   geometry (28% of contacts under 0.2·CellRad per px, tail past 2·CellRad; total 0.41× v1's
   target). After: 99% of contacts remove 0.8–1.2 of `A*`; total 1.00×. A 50% area-fraction guard
   per substep replaces the depth cap; it has never bound. Motions are also capped at one cell
   radius per substep.
4. Record ends slide along their interface; both cells of the record are re-derived. When a span
   reaches zero the side is snapped to the point **before** the record is unlinked, then the record
   retires.
5. **Corners** (built): any vertex between two surface sides moves toward its centroid, on the same
   budget, with `dA/ds = ½·|u × (P₂ − P₁)|`; capped where it would cross the chord between its
   neighbours, and consumed there by the rebuild. Two adjacent corners moving together translate the
   side between them — a face receding along its normal, for free. Measured on the 224/245 config:
   13k corner motions alongside 12k end slides; "insensitive" calls 749 → 2; records spent 104 → 14;
   peak overlap 9.4 → 6.6 px.

Only cells with **no** live record (lone rubble) still take the v1 direction clip, pending the by-eye
check; the corner rule handles them too, and v1 is then deleted entirely. Two-cell fragments are on
v2 (their surface moves through corners), which removed the last `LengthDisagreement` cases.

## Re-derivation (`ReclipToRecords`)

Not a clip. The polygon is rebuilt from its sides:
- **Merge spent sides first**: any zero-length surface side is merged so the point is one vertex.
  This must run on the stored polygon before any target is read, because within one contact pass a
  record can be spent and its neighbour's newly exposed end slid; merging afterwards finds the stub
  already stretched along the old interface, absorbing the recession that should have pivoted the
  face. That stub was the 62/49/35 internal-surface repro (projectile steel-vs-rock, defaults, ticks
  15–16).
- A record side's **exposed** ends are written exactly from the record. **Interior ends are never
  written**: an interior vertex is shared with another record's side, and the two records describe it
  with parameters that agree only to rounding.
- **Splitting**: where two open record ends at one vertex no longer coincide, the vertex becomes two
  with a bare side between — the chord across the cell behind's corner, the third wall of the pit its
  neighbours' pivoting faces opened. (A version that kept the vertex was tried and reverted: it left
  the record narrowed with no material removed — the v1 pathology.)
- Surface vertices the outline has passed (reflex) are consumed.
- Budget: a split adds a vertex; when `PolyCap` is exceeded the least-significant **surface** vertex
  is merged (Visvalingam). Record ends are never dropped.

## Propagation (`ExposeRecordEnds`)

A retired record exposes its endpoints on the records that share them. One rule for a span slid to
nothing, a comminuted neighbour, and a body split — all retire the record through `RetireTouch`.

Found through the **surviving cell's polygon** (the sides adjacent to the retired side), not through
the retired record's geometry: a record's bisector is built from both cells' seeds, and once the other
cell is dead or in another body those seeds are in a different frame and the positions mean nothing.
That was the first version's bug — 79% of calls found no surface because nothing inward was ever
tagged open.

## Momentum (built)

v1's `ShedMass` gives the partner an inelastic impulse `j = μ(v_c − v_o)` and books `dm·v_c − j ≈ dm·v_o`
to the ledger: the dust leaves at the partner's speed, the partner is slowed by the dust it made, and
the body that lost the material barely feels it. That is the 35% / 90% ledger.

Built: dust compacts into the dent. Keep `j` on the partner; deliver the remainder to the **shedding
cell** (its deviation velocity), **clamped so the cell is not pushed faster than the impactor is
closing on it** — an inelastic push ends at co-motion. Without the clamp a cell shedding nearly all
of itself in one substep took the remainder as an absurd velocity (a NaN at 4000 px/s). What the
cell cannot absorb is flung dust and goes to the ledger.

Comminution: the dying cell's momentum has a **rigid share** (carried because it was part of the
body) and a **deviation share** (what the impact put into it). The rigid share leaves with the mass
— keeping it while dropping the mass would speed the body up, creating energy of order m/M, serious
for a small fragment. The deviation share compacts into the cell's live neighbours, mass-weighted.
This is a correction to the plan's wording, which said "the remainder".

Measured on steel-on-steel, `Dent` 2.5: ledger **11.8% → 1.4%** at 900 px/s, **2.8%** at 1500,
**55.9% → 34.7%** at 4000 (hypervelocity: flung dust, dying fast fragments, free spall). Momentum
drift 0.05–0.09%; KE 18–21% of initial on the guardrail scenes — dissipative, no creation.

With this rule the `CellRad/4` depth cap loses its purpose (it existed so momentum could leave
through the contact progressively). It binds on 0.05% of calls at 600 px/s and 0.5% at 4000, by 1.3×.
**Done: dropped on the v2 path; a 50% area-fraction guard per substep remains as a spike guard. It has never bound.**

### Comminution routing (2026-09-18)

Measured on the glass collide (600 px/s, grain 170): 414 of 504 comminuted cells exported their
whole momentum — 19.0M of the body's 39.3M — because the rule routed only to live neighbours **of the
same body**, and glass cracks before it crushes, so a dying front cell's neighbours were already in
other bodies. Now: same-body neighbours get the impact share (as before); with none, the cell presses
its **whole** momentum into its **contact partners**, any body — an inelastic transfer into another
mass, so no energy is created; with neither, it is free spall and goes to the ledger. After: glass
exports 3.0M, delivers 21.1M through contacts; rock exports 0.

What that did not change, and why: glass body 0 still barely slows before it disintegrates. The
back half of a rock body carries 90–150 loaded bonds from tick 20; the back half of a glass body
carries 0–2, and its front half 1–3, at 1× and at 3× failure strain alike. The cells carrying the
load comminute (`Crush` 4e5, `CrushRate` 2.0, `ShedLimit` 0.15) before the bond network transmits
anything. That is a material tuning fact, not a routing one — the retune item.

### Cracks transmit compression (`SimTuning.CrackPush`, on) — 2026-09-18

A broken bond transmitted nothing and same-body cells never contact, so from the substep a bond broke
until the body split at tick end the two sides of a crack had no interaction: a detached front row
slid into the row behind with no resistance. Now a broken bond between live cells keeps its
compressive normal force — only the closing part of its stretch is remembered, so it meets from
zero — and no tension or shear. Rock is unchanged by it (its bonds hold), the correct null result.

### Persistent rubble — tried and removed (2026-09-18)

Comminuted cells were kept as single-cell powder bodies (no carving, no bias) that left when
compacted past a bury depth or when free. It worked as specified (bounded population; compaction the
exit that bound it) and was strictly worse: it did not rescue the glass back rows (body 0 still
powdered by tick 40), and on rock it raised comminution (231 vs 145), raised the ledger (36 → 45%)
and *lowered* the back-row load (97 → 38 loaded bonds) — bias-free powder pressed on its neighbours
and carved them instead of transmitting. Removed entirely; the merge-to-common-velocity comminution
routing it shared stays.

### Delaying fracture so the impact can traverse (2026-09-18)

Tested two physical forms of "let the wave pass before the body lets go":
- **Rate sensitivity** (`RateSens`, existing, off): even at 400% — threshold ×8 at this loading rate —
  glass loses ~300 cells by tick 40 exactly as at 0%. It also cannot work as written: it keys on the
  instantaneous relative velocity, which is zero when a bond's stretch peaks.
- **Split only when the crack opens** (`SimTuning.SplitOnOpen`, off, viewer checkbox): with `CrackPush`
  tracking the closing across a broken bond, a body's components are walked over broken-but-pressed
  bonds too, and the body re-partitions when a pressed crack opens. A pressed fragment is still
  mechanically coupled, so contact impulses on it reach the whole body. Glass keeps more of itself
  in one body (94 fragments vs 122) but body 0 still runs at 281 px/s at tick 30 and is gone by 40;
  rock is unchanged.

Why neither helps glass: over 30 ticks glass body 0 absorbs ~2.5M of impulse, rock ~29M. Glass's
front cells last about one tick of contact before they comminute (`Crush` 4e5 hugely exceeded at
1,150 m/s, rate 2.0, shed 0.15), and every replacement row starts a fresh contact from zero. Delaying
fracture is the right lever for a body that **cracks**; the glass scene **crushes**, and its lever
is `Crush`/`ShedLimit`.

## Unchanged, explicitly

- `ShedLimit` per material: the fraction of built area a cell may lose before comminuting.
- `Crush`, `CrushRate` per material.
- `BondLen` **is now refreshed** from the record span on every slide (it feeds the bending lever of the
  damage model). `BondLenStale` went 256→9, 156→1, 370→2; the rest is v1 fragments.

## Deleted so far / to delete

Gone from the v2 path: `SteerToOpenSurface`, the shield, `BondedGuard`, `ContinuityOffset`,
`RelinkCoincidentSides`, `NarrowTouchRecords`, the side-vanished retire, `CarveMinArea` as behaviour.
They still exist for the v1 fallback and go with it.

## Consistency guard (2026-09-18)

Records are the truth and polygons are derived from them, so every place the two could disagree is
checked directly, in `SideAudit`, as four v2 invariants alongside the fourteen v1 ones:
- `LabelRecordMismatch` — a side carrying a record is labelled with that record's bond (or crack if
  broken, sealed if none), never real.
- `OpenBitWrong` — a record end is marked exposed iff, in at least one of its cells, the vertex at
  that end is adjacent to a surface side. This is propagation checked against geometry every tick.
- `EndOffVertex` — the polygon vertex at a record end is where the record puts it.
- `ZeroLengthSurfaceSide` — no spent interface survives as a notch.

`RecordConsistencyTests` asserts 0 violations at build and over 150 ticks on six scenes — the three
standard ones and the three that each once produced an internal surface (grain 170, grain 255 steel,
steel-on-rock at 900). **A new internal surface becomes a new row there, not a new tolerance.**

Found by the guard on the day it was written: the split threshold was `1e-4` and fired on the 0.04 px
rounding disagreement between two records describing one vertex, leaving a bare sliver on an
uncarved cell (now 0.05 px, the noise floor used everywhere); and build-time sealed slivers whose
neighbour died were promoted to surface without being merged (now merged where they are promoted).

The viewer's surface view marks every vertex for what it is to the driver, through the same
`ClassifyVertex`: filled amber = exposed record end, hollow cyan = corner, small grey = a triangle's
corner (cannot recede). A loaded vertex with no mark is one the driver will not move.

## Build and audit fixes found on the way

- The clipper emitted sub-pixel duplicate vertices; `LinkSide` linked collinear slivers beyond the
  shared span to the wrong record. Both fixed; audit at build is **0 violations on every scene** (was
  1/4/4) and `TouchUnlinked` (a constant 400/run) is gone.
- `OpenSideIsCovered` counted crack faces with a live record — which have material across them by
  definition. It was mostly noise for the whole v1 effort. Re-specified: a bare side (real, or crack
  with no record) must have nothing across it; a crack with a live record must have its neighbour.
- **Labels now follow records at build.** `LabelPolyEdges` and `BuildTouchRecords` matched sides to
  neighbours independently, and at a near-4-valent vertex (two copies 0.04 px apart, grain 255) the
  label pass missed a 3.85 px interface the record pass found: a side labelled REAL with a live
  record and bond. Exposed-end tagging read that REAL side as outline and marked three interior
  records open — the 291/292/267/268/243 internal surface at tick 7. A side with a record now takes
  its label from the record, and exposed ends are tagged only after that.

## Validation state (2026-09-17, all milestones)

- Audit, tick 200, all three scenes: **0 faults of any class**, including at build.
- 224/245 config: 12k end slides + 13k corner motions, 19 records spent, no call fails to find
  surface, 99% of contacts remove 0.8–1.2 of `A*`, total shed 27.5%, peak overlap 6.6 px.
  Steel 218/248 and 62/49/35 repros: no internal surface.
- Suite 102/105 — the three long-standing morphology bands. Momentum drift 0.05–0.09%.
- Suite 108/111 with `RecordConsistencyTests` (6 scenes × 150 ticks, 0 violations).
- Still to look at by eye: the steel dent; `Dent` per material (rock/ice/sandstone 1.5, glass 0.8,
  steel 2.5 are first guesses); the peak-overlap rise on the default-grain collide guardrail (23 px).

## Decisions

Taken:
1. Corners: **move the vertex** toward the centroid (linear area, no vertex growth, one budget with
   record ends); consumed at the chord between its neighbours. Any vertex between two surface sides
   qualifies — build-time, comminution-left, detachment-left. A corner belongs to one cell; coincident
   corners of different cells diverge (Decision 2 as chosen: crack mouths open).
2. Crack faces slide along their record like bonded ones for now; V-mouth variant later if wanted.
3. Momentum: compaction into the shedding cell; ledger for free spall only.
4. Depth cap: dropped; 50% area-fraction guard per substep, spike guard only.
5. `BondLen` follows the span.
6. v1 path: deleted once corners are in and the feel is confirmed.

Open:
- The by-eye check: the steel dent, and grain-170 collide. `Dent` values (rock/ice/sandstone 1.5,
  glass 0.8, steel 2.5) are first guesses.
- Deleting v1 (lone cells) once the feel is confirmed.
- The three morphology guardrails have failed since before v2 and need rebaselining or a decision.
- The default-grain collide guardrail's peak overlap sits at 12–16 px (v1: 10–16); fine, but noted.
