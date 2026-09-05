# Engine Refinement — the stress model, and what to port

**Status:** the prototype line v5 → v9 is complete and verified. This document is rewritten from
scratch against that outcome. It supersedes the previous version entirely.

**What changed, and why this is a rewrite rather than an edit.** The previous document proposed
*refining* the shipped engine: keep the energy-budget (pure-G) fracture kernel and add stress in
stages. That framing is dead. Building the prototypes showed the missing quantity is not an add-on —
it is the spine — and the resulting model is not the current engine plus features. It is a different
engine. Roughly a third of the old proposals turn out to be **subsumed** (the new model does them by
construction), a few are **withdrawn**, one is **reopened after being withdrawn**, and the rest carry
over unchanged. §6 maps every one of them.

Read §1–§3 to understand what to build, §4 before trusting any of it, and §6 if you are looking for a
specific old proposal.

---

## 1. The model, as prototyped

Reference implementation: `prototypes/stress-fracture-v8.html` (the file kept the v8 name through the
v9 work). Its header carries the full measurement history including the wrong turns; this document
carries the conclusions.

### 1.1 Data

```
WORLD   pxPerMetre, dt = 1/60, substeps, solver sweeps
MATERIAL  authored in SI: rho, c, sigma_tensile, sigma_compressive, Gc
BODY    x, y, rot, vx, vy, w, m, I, budget   — the only thing that integrates position
CELL    rx, ry            fixed rest offset — never moves
        dvx, dvy, dw      velocity deviation — the internal field
        crushLoad, crushWork, stress tensor accumulators
BOND    XPBD compliant constraint, lambda per mode (normal / shear / bending)
        sn, st, sa        accumulated stretch — deformation, bookkept not geometric
        mult              baked strength: heterogeneity x anisotropy
```

**Cells never move relative to their body.** Deformation is bookkept on the bond as accumulated
stretch rather than realised in geometry. This is what buys "no visible wobble, no interpenetration"
and it is the single most consequential structural decision in the model — see §4.1 for what it costs.

### 1.2 One tick

```
per substep (8 per tick):
    reset bond lambdas                      XPBD: lambda accumulates within a substep only
    build contacts
    apply inertial loads                    centrifugal, Euler, Coriolis — BEFORE the solve
    solve bonds       x N sweeps            N=4; one sweep propagates load ONE BOND-HOP
    solve contacts
    damp deviation velocities               Rayleigh; NOT stretch decay
    integrate bodies
    grow active set
    evaluate failure -> split bodies
per tick:
    rebuild contacts, run kinematic backstop (positions only)
```

### 1.3 The physics that matters

**Strength is authored as a velocity.** `sigma = rho * c * v_material`, so `v_crit` and `v_crush` are
particle velocities at failure. Density cancels out of the thresholds — verified exactly (§3.2) — which
makes materials authorable without density and sound speed contaminating strength.

**Failure has three modes, evaluated per bond:**

```
sT = max(0,fn)/L + 6|fa|/L^2     tension + bending
sC = max(0,-fn)/L                compression — loads the CELLS, never separates a bond
sS = |ft|/L                      shear
```

Forces are `lambda/h^2`, which is dt- and iteration-independent by construction. This removes the
entire class of bug that dominated v5–v7 (forces scaling as 1/dt produced a "stress" of 68,000,000
against a strength of 260).

**Two multipliers, deliberately on opposite sides:**

```
overT = sT * amp / (sigma_ten * gate)
overS = sS * amp / (sigma_shear * gate)
```

- `amp` multiplies **stress** — crack-tip and surface bias. These are stress *concentration*: the
  material is unchanged, the geometry of existing damage focuses load.
- `gate` multiplies **strength** — baked anisotropy in `bd.mult`, and rate dependence. These are
  material properties: the same load, genuinely different resistance.

A crack tip does not make rock weaker; it makes local stress higher. A bedding plane genuinely is
weaker. The split keeps that distinction honest.

**Inertial loads.** The body frame rotates, so it is non-inertial and carries pseudo-forces:
centrifugal `w^2 r`, Euler `(alpha*ry, -alpha*rx)`, Coriolis `2w(dvy,-dvx)`. All three sum to zero over
a body because `sum(m_i r_i) = 0` about the centroid, so they load without moving — verified exactly.
Omitting them made rigid rotation completely stress-free, which is wrong and was the single largest
error found in the whole prototype line (§5.1).

**One energy pool.** `body.budget` accumulates the work a contact absorbs (`|f| * v_approach * h`) and
is drawn on by both bond breaking (Griffith, `L*Gc`) and comminution. They compete, as they must — the
joule spent pulverising is not available to crack.

**Comminution** is gated on compressive pressure exceeding `sigma_crush`, then accumulates work at a
constitutive yield rate (not a kinematic closing velocity — see §5.4) until it exceeds a per-mass
threshold, at which point the cell is removed.

---

## 2. What the port must build

Ordered by dependency, not by value.

| | Component | Notes |
|---|---|---|
| 1 | **Bond graph with per-bond stretch state** | `sn/st/sa` + baked `mult`. Replaces the current bond model. |
| 2 | **XPBD constraint solver, N sweeps per substep** | Compliance `alpha = 1/k`, `k = rho*c^2`. Force is `lambda/h^2`. |
| 3 | **Contact as a rigid-body constraint** | Reads rigid velocity only; momentum to the body, concentration to the cell. §5.2. |
| 4 | **Inertial load pass** | Cheap, runs before the solve, needs an activation floor or every spinner engages wholesale. |
| 5 | **Failure evaluation with three modes + shared energy pool** | Tension/shear break bonds; compression loads cells. |
| 6 | **Connected-component splitting** | Fragments inherit parent rigid motion at their own centroid. Already exists in `src/` conceptually. |
| 7 | **Active set** | Grows at `c*dt` through the bond graph. Physics role (finite wave speed) is load-bearing; cost role is not — §4.4. |
| 8 | **SI material table + three scale knobs** | `strengthScale`, `toughnessScale`, `crushScale`. §3.3. |
| 9 | **Kinematic backstop** | Position-only, last resort. Cannot inject energy by construction. |

The existing `FractureService`/`FractureSimulator` split survives in shape — the engine computes and
returns, the game layer spawns. The kernel inside it is replaced.

---

## 3. What is verified

These are measurements, not claims. Each was run headlessly against the prototype.

### 3.1 Invariants

Across all five scenarios: **momentum drift 0.00%** (normalised by `M*v0`, never by `p0` — symmetric
scenarios have `p0 ~ 0` and a relative metric once reported 281% for a 1.8% error), and **final body KE
at or below initial** everywhere, i.e. energy dissipating.

### 3.2 The spin barrier — the strongest result in the model

A body spinning fast enough must shed mass. Measured, it does, and at the analytically predicted rate:

```
omega     0.5   2.5    5     7    10    15
broken      0     0    1     9    25   183        (48 bodies at omega=15)
```

Analytic breakup for this blob is `omega ~ 6.9`. But the sharp test is the **scaling**, which is
independent of shape factor:

- `omega_crit ~ sqrt(sigma/rho)/R` predicts a ratio of `sqrt(2) = 1.4142` per doubling of strength.
  **Measured: 1.415, 1.416, 1.412.**
- Density must cancel exactly. **Measured: `omega_crit` = 6.56 at density 0.5, 1, 2 and 4.**

This is real physics the model reproduces without being told to — rubble-pile asteroids have an
observed spin barrier near a 2.2 h period for exactly this reason.

### 3.3 Mach regime

Participation must complete below the sound speed and be capped above it:

```
   M     v(px/s) | engaged% peak | crushed  bodies
  0.10      260  |     100%      |      0       6
  0.35      910  |      97%      |      5      35
  1.00     2600  |      73%      |     38      65
  2.00     5200  |      61%      |     65      41
  3.00     7800  |      35% *    |     72      39      * 84% single-tick transient at contact
```

Below M=1 participation **completes** (97–100%); above it is **capped** (35–42%) because the impactor
outruns the wave and the bulk never learns. Comminution rises monotonically with Mach (0→72), so the
shock regime is comminution-dominated as required. Wave crossing of a 260 px body measures **6.0 ticks**
at c = 5000 m/s, matching prediction.

### 3.4 Materials differentiate

Authored in SI, converted by `v = sigma/(rho*c)`:

```
material     bodies broken   crush/crit   reading
ice               8     26          5.3   weakest, fragments most
sandstone         4     14         10.0
rock              5     13         15.0
glass             2      2         20.0   too STRONG to initiate — needs surface flaws
steel             2      0          1.0   unbreakable — needs plasticity
```

The last two rows are diagnostic, not failures: they correctly identify the two missing mechanisms.

### 3.5 Load path

A bond must carry the accumulated load of everything outboard of it. Measured at 0.733 with one solver
sweep, converging to 0.972 at sixteen — and independently to 0.972 with a shorter load path (larger
cells). The load path converges; it is simply under-relaxed at low sweep counts.

---

## 4. Limitations and open problems — read this before trusting the model

### 4.1 Behaviour depends on solver sweep count

The most serious outstanding issue. Load carried across a cut runs 0.733 → 0.972 as sweeps go 1 → 16.
Gauss-Seidel propagates load one bond-hop per sweep, so a long chain needs many sweeps. Default is 4
(≈0.915) at ~2.5 ms.

This means **fracture behaviour is a function of a solver parameter**, which is not acceptable in a
shipped engine. Two routes: a hierarchical/multigrid solve over the bond graph (fixes it), or enough
sweeps that the residual is invisible (hides it, and only works if performance allows). The first is
strongly preferred and is the one genuinely algorithmic item on the list.

### 4.2 One global scale cannot serve all scenarios

Both `toughnessScale` and `crushScale` show the same failure. At the value that makes a two-body
collision fragment correctly, the dumbbell neck stops breaking entirely; at the value that pulverises
a slow crush, the projectile vaporises on contact before delivering any load. The scenarios sit at very
different energies and one global knob cannot span them. This looks like scenario-energy calibration
rather than a model defect, but it is **unresolved** and it is the first thing to settle.

### 4.3 Comminution — partly solved, and an earlier claim withdrawn

Confinement is the correct discriminator between crushing and cracking: Mohr-Coulomb failure depends on
`sigma_3`, not the mean stress, and high confinement suppresses brittle fracture in favour of
cataclastic flow.

**A previous version of this section concluded that comminution "cannot be made emergent" in this
model. That conclusion was wrong and is withdrawn.** It rested on a stress tensor accumulated from
incident *bonds only*, with contact forces omitted — an instrument blind to the dominant load at the
impact face, which is precisely where comminution happens. It measured confinement as absent using a
method that could not see it.

With contact forces included:

```
scenario     stressed  both-compressive   max     p75    median
crush             187                35    0.871   0.540    0.290      (was 9 cells, max 0.467)
projectile        114                 4    0.252   0.102    0.030
collide            85                 9    0.438   0.184    0.135
```

Confinement **does** separate the cases. The crush interior reaches 0.871; the projectile face peaks at
0.252 because there is a free surface behind it. The gate is enabled by default at 0.35 and fixes the
pathological case — at `crushScale`=100 the projectile used to vaporise on contact (5 crushed, 1 body,
0 breaks) and now cracks properly (0 crushed, 5 bodies, 9 breaks).

**Still unsolved.** The crush scenario gets no comminution at any gate above 0, because the *confined*
cells (interior) and the *overloaded* cells (contact face) turn out to be different cells — the
intersection of high `crushLoad` and high confinement is empty there. So confinement is currently a
good **negative** filter (it stops unconfined material pulverising) without enabling the positive case.

**Two further hypotheses were then implemented and both failed**, which is worth recording so the
ground is not re-walked:

- *Coulomb friction at contacts* would create platen confinement (contacts were genuinely
  frictionless — only the normal direction was ever constrained). Friction is now implemented, and it
  is **not** the mechanism: confinement is already high without it (128 confined cells, max 0.950) and
  friction slightly *reduces* the count (91 cells, max 0.864). Outcome effect is small — crush goes
  4 → 6 crushed cells.
- *Inertial confinement* (`rho*v^2/sigma_crush`) would rescue fast impacts that a static `sigma_3`
  wrongly vetoes. Measured **no effect at all**: the coefficient 0 vs 1 is identical, and projectile
  comminution is flat across 300–4000 px/s.

**And the premise under both was wrong.** Confinement in the crush scenario is *high* — median 0.347,
max 0.95 — so it was never what blocked comminution. The binding constraint is `sigma_crush` itself: at
the default `crushScale`=5 pressure never reaches threshold in any scenario. The evidence points at the
threshold and the energy pool (§4.2, §12), not the contact model. Both changes were kept on their own
merits — frictionless contacts are unphysical regardless — but neither fixes anything.

The originally-listed candidates remain untested: the 2D formulation structurally under-represents
confinement,
since plane stress has no out-of-plane component at all; and the per-body energy pool may be
mislocating the load relative to the confinement (§12). There is also a genuinely emergent path already
working that this framing overlooked — comminution rises monotonically with Mach number (0 → 72 across
M = 0.1 → 3.0), which is shock-driven pulverisation appearing without being asked for.

### 4.4 The active set does not pay for itself on spinning bodies

Its *physics* role — finite wave speed, growing participation — is load-bearing and must be ported. Its
*cost* role largely is not: inertial load is quasi-static and present everywhere from t=0, so a
spinning body activates wholesale (100% active even at omega=0.5 where stress is 1% of strength). An
activation floor keeps genuinely idle bodies cheap (0% active, 0.37 ms) but above it the cost is real.
Port it for the physics; do not budget for the savings.

### 4.5 Known-wrong or unexplained

- **`omega_crit` does not scale with `c`.** Predicted `sqrt(2)` per doubling; measured 1.338 / 1.172 /
  1.006 — it saturates. Ruled out by measurement: substep count (8→64 does not converge), stretch
  relaxation (removing it made it worse), Euler spikes (on/off is byte-identical). **Unexplained.**
- **Failure strain is 4–8 x 10^-2 against 1.3 x 10^-4 for real rock.** The material is parameterised
  like stiff rubber. This is a realism gap, not a validity one — the model never realises deformation
  geometrically, so there is no geometric nonlinearity and force stays linear in stretch.
- **`collide` overlap reaches 17–20 px.** Accepted for now.
- **Backstop peaks of 11–17 px in a single tick** (mean is 0.6–0.9). Residual twitch, unaddressed.
- **Crack-tip bias is modest** — singles 29.1% → 23.2% on collide, roughly a 20% relative improvement.
- **Surface bias is under-powered.** `surfBonds=4` is too strict for a Voronoi tessellation where most
  cells have 5–7 neighbours. Should be replaced by marking true boundary cells at build time (a cell
  with a polygon edge shared with no neighbour).
- **Fragment fly-apart** — fragments inheriting stored elastic energy — was never implemented, in v7,
  v8 or v9. It *adds* energy, so it needs the ledger trustworthy first.

### 4.6 Material classes the model cannot express

| missing | consequence | cost to add |
|---|---|---|
| **plasticity** | no metal; steel is unbreakable rather than ductile; no brittle/ductile transition | structural — per-bond plastic strain state, changes the solve |
| **cohesionless granular** | no sand; a sand body dissolves into loose cells with no bulk behaviour | moderate — per-material zero cohesion + Coulomb friction on bonds |
| **compaction** | no porous materials; volume loss needs cells to move | hardest — fights the fixed-cell decision directly |

Rate dependence and anisotropy are now **present** (§6, items 2.3 and new). Note that rate dependence
gives rate-dependent *strength*, which is not ice's brittle/ductile transition — that is a change of
failure *mode* and needs plasticity.

---

## 5. What the prototypes taught — including the wrong turns

These are worth more than the successes, because each one cost real time and each one generalises.

### 5.1 Rigid rotation was completely stress-free, and the argument for it was wrong

The justification was: at a shared anchor `w x r_A = w x r_B`, so rigid rotation produces no relative
velocity, hence no stretch. The algebra is correct and it proves the wrong thing. It establishes that
rigidly-rotating cells are not moving *apart*; it says nothing about the **force** needed to hold them
in formation, which is `m*w^2*r` and grows as `w^2`.

**Zero relative velocity is not zero load — a rope at maximum tension has zero relative velocity.**

In the fixed-cell representation that force is supplied silently by the rigid-body integrator, so the
bonds are never asked for it and never register it. At `omega=60` the centrifugal stress was 76x the
tensile strength and the model reported zero stretch, zero active cells, zero breaks. Worse, the "spin
idles perfectly" result had been reported as the model's cleanest pass for an entire working session —
the test had been built to confirm an assumption rather than probe it.

**Generalises to:** any representation that supplies a kinematic constraint for free will hide the
forces that constraint implies.

### 5.2 The contact solver, not the fracture path, was creating the energy

A three-body crush was manufacturing 8137% of its initial energy. Ablating subsystems pointed at the
bond-break/split path. That was wrong. A **per-phase energy ledger** — attributing the body-KE delta to
the phase that caused it — settled it immediately:

```
contact          +1638901858   3200 calls    <- the entire source
split             -249196384     38 calls    <- a net SINK
failure                    0     84 calls    <- breaks move no body KE at all
backstop                   0    400 calls
```

Three real defects, all in the contact solver: a mass matrix that did not match the applied response
(mean overshoot 2.39x); a constraint that read the **virtual** deviation field and answered it with a
**real** impulse, minting momentum every substep; and penetration entering the velocity solve at full
strength, doing real work forever in sustained contact.

**Generalises to:** ablation tells you which subsystem is *involved*; attribution tells you which is
*responsible*. Prefer attribution. Bond breaking was the trigger — it makes fragments, which makes
contacts — but never the source.

### 5.3 Isotropic crack-tip seeking is a cell-isolation machine

v5 amplified stress near existing damage isotropically. Single-cell fragments went from 86% to 94% of
mass, and the feature was defaulted off. The reason: amplifying every bond around a damaged cell
weakens all of them at once and frees the cell.

The directional formulation works — amplify only *along* the crack axis, gated on **coherence** (the
resultant length of the doubled-angle mean: 0 for scattered damage, 1 for a clean line). Singles go
*down*, 29.1% → 23.2%. Coherence is the whole fix; without it the two cases are indistinguishable.

**Generalises to:** direction and coherence are not refinements of a damage-proximity heuristic. They
are what separates "extend this crack" from "dissolve this neighbourhood".

### 5.4 Rules that require motion cannot fire in a fixed-cell model

Comminution work was `f * closing_velocity * h`. In a slow crush the cells cannot move, so no work
accrued, so nothing crushed, so nothing moved. Self-blocking. Measured: pressure at 6.22x
`sigma_crush` while accumulated work reached 0.162 of its threshold, and exactly one cell was lost.

Crushing displacement must be **constitutive** — the material yields at a rate set by overload —
not kinematic. Separately, work accrued only at contacts while the *gate* used bond compression, so
interior cells of a squeezed body could never accumulate work at all however hard they were pressed.

### 5.5 Two writers to one value produce sticky, irreproducible behaviour

`vCrit/vCrush/Gc/density/c` had three writers: material derivation, their own raw sliders, and a
scenario dropdown re-applying old per-scenario constants. Whichever wrote last won, sliders showed
stale numbers, and returning a knob to its default did not restore behaviour. Fix: one writer, which
syncs the controls it drives. **Every derived control must be repositioned by whatever derives it.**

### 5.6 Measurement discipline that earned its place

- **`node --check` after every structural edit.** A `}` swallowed into a comment cost several turns.
- **One change at a time.** Bundling a mass-matrix fix with a compliance change made energy *worse* and
  cost a full cycle to unpick.
- **Bin fragments by mass, never by piece count.** Piece counts over-weight dust ~50:1 and produced a
  conclusion that had to be retracted.
- **Never sum virtual quantities into physical metrics.** Counting the deviation field as real energy
  reported a 7634% blow-up on a collision that was a correct 34%.
- **Print the column you are reasoning about.** "High-speed contacts are being missed" was wrong; peak
  stress had been measured over *surviving* bonds and the vapor column was never printed, so instant
  comminution looked identical to a missed contact.
- **A metric with a near-zero denominator is not a result.** `peak/init` of 4.73 on a slow crush is an
  artefact of `v0 ~ 0`.
- **Sweep wide before concluding a parameter is inert.** `toughnessScale` was declared dead from a
  0.1–16 sweep; the action is at 256–2048.

---

## 6. The old proposals, translated

Status of every item from the previous document.

### Subsumed — the new model does these by construction

| old | item | why |
|---|---|---|
| §2.7 | **Load-bearing bond graph / section loads** | This *is* the model. Section loads emerge from the bond solve; there is nothing to add. Was the old plan's stage 2; it is now the spine. |
| §3.2 | **Stress-gated initiation** (`sigma = AccumN/(A*dt)`) | The new contact solver produces real forces at the cell; the velocity gate is gone with the kernel it belonged to. |
| §3.2 | **Persistent contacts, metered work** | Contacts are persistent constraints with accumulated `lambda`. Reduced mass is fixed for free, as predicted — the "2% of rigid reduced mass at first touch, 89% over ~30 ticks" finding is now the engaged-mass curve. |
| §3.2 | **Impulse clamped by material strength** | Superseded by a correct mass matrix plus a compliance floor. The clamp was treating a symptom of the contact formulation. |
| §3.3b–d | **Crack-front ordering, revisits, eikonal propagation** | The active set *is* a time-of-arrival front growing at `c*dt`. Revisits are natural because bonds are re-evaluated every substep. Cells are no longer processed once. |
| §3.1 | **Trapped fragments crushed, not ejected** | Falls out of comminution plus a contact solver that does not eject. |

### Carried over unchanged — still wanted, still not done

**These were one-line table rows pointing at explanations in the previous document. That document no
longer exists (§10), so each is written out in full here.** Where a detail was in prose I never read
before the overwrite, it is marked *(reconstructed — verify against intent)*.

**§2.1 Per-cell material + `bondAffinity`.** A body should be able to contain more than one material —
an ore vein in rock, an ice core, a metal spar. Each *cell* carries a material id rather than the body
carrying one. A bond spanning two materials takes a strength derived from both, scaled by a
`bondAffinity` coefficient for that material pair, so a rock/ice interface can be deliberately weaker
than either material alone — which is how you get things that shear apart at the seam.
**Under the new model this became more necessary, not less:** failure thresholds are per-bond and
derived from `sigma = rho*c*v`, so the kernel already needs a per-cell `sigma` lookup. Composites are
nearly free once the material table (§1.3) is per-cell instead of global.

**§2.4 `DepositEnergy`.** An explicit entry point for putting energy into a body without a rigid-body
contact — explosions, beams, anything that is not a colliding mass. Confirmed then and confirmed now,
and it has become structurally important: see §11.3, where it is the mechanism by which every
non-kinetic weapon works. It feeds the energy pool, which now has correct units (§5.4).

**§2.6 Restitution and Lloyd relaxation.** Tessellation quality controls, orthogonal to the physics.
Lloyd relaxation evens out Voronoi cell sizes so the grain is uniform; restitution controls how much.
Worth keeping because fragment size distribution is read directly off cell size.
**Non-negotiable detail:** *phantom seeds are mandatory*. Seeds rejected by the shape's `member()` test
must still participate in the clipping pass. Without them a carved shape — the dumbbell is the test
case — comes out as its bounding box, because the cells at the boundary have nothing to clip against.

**§2.9 Runtime cell addition — sockets only.** Cells may be restored into *empty sockets* left by
destroyed cells; new geometry is never created at runtime. This was the resolution to an objection
about repair mechanics generating unbounded new tessellation. The constraint dissolves it: the socket
already has its rest offset, its neighbours and its bond stubs, so restoring it is a state change
rather than a geometry change. Unchanged under the new model.

**§2.11 Heat as a fifth channel.** Alongside tension, shear, compression and bending, a per-cell heat
accumulator that transforms material rather than only damaging it — ice sublimates, rock melts to slag,
metal softens. The point is that it *changes what the material is* instead of subtracting hit points,
so a thermal weapon reads differently from a kinetic one rather than just doing damage at a different
rate. Not implemented in any prototype. *(reconstructed — the original had more on the state-transition
table than I can recover.)*

**§2.12 Parameter blending between material and weapon.** A weapon must be able to alter fracture
*character*, not merely deliver more energy: a shaped charge and a sledgehammer at equal energy should
produce different fracture patterns. The mechanism is a blend table — the weapon supplies modifiers
that combine with the material's values to produce the parameters used for that one impact, so the
fracture kernel never learns what a weapon or a material *is*; it receives resolved numbers.
**This is now the most load-bearing of the carried-over items**, and §11 expands it into a three-layer
ownership model, because the new model exposed that scalars alone are not enough — the impact needs to
carry *geometry* too.

**§2.13 Roles driving behaviour.** Parameters should be grouped by the behaviour they produce
("brittle", "tough", "granular") rather than exposed as a flat list of physical constants, so that
authoring an asteroid is a design act rather than a physics exercise. The SI material table (§1.3) is
the substrate this sits on: materials are authored physically, roles are the designer-facing layer over
them. *(reconstructed.)*

**§2.14 Plumbing.** A set of refactors with no behaviour change — mainly routing parameters through
consistent structures so the above items have somewhere to live. Unchanged.

**§2.2 Remove `BreakPerp`.** Confirmed behaviour-neutral when it was analysed: the flag did not change
outcomes in any measured case. It is a concept that costs comprehension and buys nothing. Trivial
deletion, still valid.

**§2.10 Hierarchical bodies.** Withdrawn then, still withdrawn. Nesting bodies inside bodies added
bookkeeping without solving a problem the flat cell/bond graph could not.

### Changed status

| old | item | new status |
|---|---|---|
| §2.8 | **Elastic/plastic bonds** | **REOPENED.** Was withdrawn because "cells drifting apart and re-seating would read badly for rock, metal and glass". That reason no longer applies: in this model **cells never move**, so plastic strain is bookkept on the bond and is geometrically invisible. The objection was to a rendering artefact that the fixed-cell design has since removed. And it is now *needed* — steel comes out unbreakable rather than ductile, and the brittle/ductile transition is unreachable without it. This is the largest single reversal in the document. |
| §2.5 | **Flaw-seeking** | **IMPLEMENTED, in two parts.** The isotropic form was measured harmful (§5.3) and stays dead. The directional crack-tip bias works. Surface nucleation is implemented but under-powered (§4.5) — and it is the mechanism that makes *glass behave like glass*, since pristine glass is genuinely strong and shatters only from surface flaws. |
| §2.3 | **Cluster types (shell / vein / layer / cleavage)** | **PARTLY SUBSUMED.** Layer and cleavage now come free from anisotropy baked into bond strength at creation, with a per-body grain axis. Shell and vein still need the cluster generator and are still wanted. |
| §4 | **The priority table** | **REPLACED** by §2 (what to port) and §7 (order of work). The old table sequenced refinements to a kernel that is being replaced. |

### New, with no predecessor in the old document

Anisotropy as baked bond strength; rate-dependent strength; inertial loads in the rotating frame; the
SI material table with three scale knobs; the shared energy pool; the confinement measurement; the
per-phase energy ledger as a debugging instrument.

---

## 7. Bugs in the current `src/` engine

Independent of which model ships — these are live defects found while reading the existing code.

1. **`VaporEff` loses energy per cell, not per area.** Energy loss depends on tessellation, so the same
   impact does different damage at different grain sizes. Real bug, trivial fix.
2. **Double-spend in `CollisionSystem`.** The same collision energy is applied twice.
3. **Fixed `GrainArea` gives small fragments an `r^2` damage penalty.** The smaller a body, the harder
   it is for it to receive enough damage — which is exactly the reported symptom that started this work.
4. **`AccumN` is computed and discarded.** The quantity needed for stress-gated initiation already
   exists in the engine and is thrown away.

---

## 8. Order of work

Revised after the confinement correction (§4.3) and the spatial-energy hypothesis (§12).

1. **Make energy spatial** (§12) — merge the budget into the active set. Highest value: it plausibly
   resolves the calibration conflict (§4.2), changes the comminution balance (§4.3), and is what makes
   weapon energy deposition work at all (§11.3). Unverified, so do it as an experiment first.
2. **Hierarchical solve over the bond graph** (§4.1, §11.1). Performance and the sweep-count dependence
   are one problem, and the dependence is the property that should block shipping.
3. **Determinism audit** (§11.2) against the named hazards — before the port, not after.
4. **Port §2 items 1–9.** The model itself.
5. **Fix the four `src/` bugs** (§7) — independent, do them whenever.
6. **Plasticity** (§6, §2.8 reopened). Unlocks metal and the brittle/ductile transition.
7. **Revisit surface bias, crack-tip bias and comminution as a group.** All three are under-powered or
   partly solved, and all three sit downstream of item 1 — tuning them before the energy model settles
   would be fitting to ground that is about to move.
8. **Granular/cohesionless materials** if sand or debris fields are wanted; then heat, cluster shapes
   and parameter blending in any order.

## 9. Open questions

- Why does `omega_crit` not scale with `c`? Three candidates ruled out by measurement (§4.5).
- Is the scenario-energy conflict (§4.2) genuinely calibration, or does it indicate the energy pool
  needs to be spatially local rather than per-body?
- Should the strain regime be brought toward physical values, given it is a realism gap rather than a
  validity one? It would change the stored-elastic-to-fracture-energy ratio and therefore the feel.
- Why are the confined cells and the overloaded cells disjoint in the crush scenario (§4.3)? Is that
  the 2D formulation, the per-body energy pool, or something real about a two-body squeeze?
- Does the deformation field genuinely reconstruct identically after a topology-only snapshot restore
  (§11.2), or does it need a settling period before the sim is trustworthy again?

---

## 10. A note on this document's history

The previous version of this file — roughly 1500 lines, containing a Part I (§A–§H) that developed the
model-level framing from first principles — was **destroyed and is unrecoverable**. It was overwritten
in place during the rewrite, and it had never been committed to git.

Lost with it: the extensive/intensive and G-versus-K development, §B's treatment of cuts and section
loads including a worked example on a long asteroid, §C on failure modes and spalling, §D on fast
versus slow including a measured finding that `c` behaves as an equilibration *rate* rather than a
wavefront speed, §E's answers to two direct design questions, §F's current-versus-proposed comparison,
and the six §G-bis subsections on v5 prototype findings.

The topics all survive here because the heading structure was read before the overwrite; the reasoning
inside them does not. **Commit `ported/` — several documents in it are still untracked.**

---

## 11. Multiplayer, performance and parameter ownership

Raised late and not yet addressed in any prototype. Recorded because all three constrain the port.

### 11.1 Performance is a live problem, not a tuning detail

Measured 0.5–4.0 ms/tick, with the worst case **4.0 ms for a single spinning body** at omega=2.5. At
60 Hz that is a quarter of the frame budget for one asteroid; a wave is not viable as it stands.

Three compounding causes: 4 solver sweeps x 8 substeps = 32 bond passes per tick; the active set stops
paying above the inertial floor (§4.4); and every spinning body is above that floor.

Note that **the sweep-count dependence (§4.1) and the performance problem are the same work** — a
hierarchical solve over the bond graph reduces passes *and* removes the dependence. Doing it as
optimisation alone would be a missed opportunity.

### 11.2 Snapshot cost and determinism

State divides into three parts with very different costs, per ~200-cell asteroid:

| part | contents | size |
|---|---|---|
| topology | which bonds broken, which cells dead | ~1 bit each -> **~90 bytes** |
| rigid state | per body x, y, rot, v, w, m, I, budget | **~60 bytes** |
| deformation field | `sn/st/sa` per bond, `dv` per cell | **~11 KB** |

The deformation field is two orders of magnitude larger than everything else, and it is *transient* —
it re-establishes within a few ticks. So: **synchronise topology and rigid state, never the deformation
field.** That is ~150 bytes per asteroid, which is tractable. It only works if the sim is deterministic
enough that the field reconstructs identically on both ends.

**Determinism hazards specific to this model** (Phase 0 already has fingerprinting infrastructure):

- **Gauss-Seidel is order-dependent.** Sweep order must be fixed and identical on every machine. This
  also means the bond solve cannot be naively parallelised — it needs graph colouring or red-black
  ordering to stay deterministic, which constrains any GPU implementation.
- **`Dictionary`/`HashSet` iteration order is unspecified in C#.** The connected-components pass uses a
  `Map` in the JS prototype, which preserves insertion order; a direct port would be non-deterministic.
  This is a live port hazard, not a theoretical one.
- **Accumulation order** in the stress tensor and the energy pool changes float results.
- Any use of `ForEachParallel` on a reduction path.

### 11.3 Parameter ownership — three layers, and geometry belongs in the event

§2.12's blend table is necessary but insufficient, because it passes only *scalars*.

1. **Material** owns physical constants in SI: rho, c, sigma_tensile, sigma_compressive, Gc.
2. **Weapon** owns a modifier set: multipliers and mode flags applied for one impact.
3. **The impact event** carries resolved numbers **plus geometry** — point, direction, extent.

The kernel never learns what a weapon or material is; it receives resolved values. Layer 3 is what the
old design was missing, and it is what makes the hard cases easy:

**Projectiles that are not rigid bodies** bypass the contact solver's work accumulation and inject
energy directly (§2.4 `DepositEnergy`). With a **per-body** energy pool this fails quietly: "deposit E
at point p" *loses p*, the energy smears over the whole body, and every weapon has the same spatial
character. With **spatial** energy (§12) the deposit has a location and propagates from it, and the
artificial path becomes the same mechanism the contact uses, differently seeded.

**Piercing rounds** deposit along a *segment* through the body with falloff, preferentially breaking
bonds they cross — using the raycast that already exists in the engine (`PhysicsQueries.Raycast`).
Explosives deposit at a point with wide radial falloff. Shaped charges deposit in a cone. All one
primitive.

So the earlier claim that a piercing round needs "machinery and workarounds" holds **only if energy
stays per-body**. With spatial energy it needs neither.

---

## 12. The spatial energy hypothesis — probably the highest-value open change

The energy pool is **per body**. Energy absorbed at one contact is immediately available to break a
bond on the opposite side. That is non-local in a model whose premise is that disturbance propagates at
finite speed: the active set already refuses to let *stress* teleport, while the budget lets *energy*
teleport freely.

**This predicts the per-scenario calibration conflict (§4.2) exactly.** The scenarios differ in how
localised their energy input is — projectile is a point, collide is two broad regions, crush is
sustained over large interfaces. With a per-body pool, a large body struck by a small impactor dilutes
that energy across every bond it owns, so a threshold calibrated for a point impact cannot also serve a
distributed one. The pool scales with the *body*, not with the *affected region*.

The old engine's **crack fronts were a spatial energy propagation mechanism**, and the new model
dropped it. The fix is to unify two things that are already half-built: the active set propagates at
`c` but carries no energy; the budget carries energy but does not propagate. **Make the active set
carry the energy**, consumed by breaking and crushing as it advances. That is the old §3.3d
time-of-arrival proposal arriving from the opposite direction.

If this is right it addresses, with one change: the calibration conflict (§4.2), the comminution
balance (§4.3), and weapon energy deposition (§11.3). That is why it is ranked above the solver work.

**Unverified.** It is a hypothesis with a good mechanism behind it, not a measured result.
