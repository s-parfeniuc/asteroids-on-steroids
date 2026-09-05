# v5 — the algorithm, in full

A mechanical walkthrough of `prototypes/stress-fracture-v5.html`: what the data is, what happens on every
tick, what an iteration actually iterates on, where wave speed comes from, and where mass enters.

Written because the "wave speed" label in the prototype turned out to be **misleading** — see §4, which
corrects it against measurement.

---

## 1. The data

```
CELL  (a full 2D rigid body — this is the whole conceptual change from v1–v4)
      poly[]        convex polygon, body-local, centroid-relative     (fixed)
      x, y, rot     world pose
      vx, vy, w     world velocity
      m, im         mass = area × density,  im = 1/m
      I, ii         rotational inertia,     ii = 1/I
      area, perim   geometry
      body          which connected component it currently belongs to
      crushLoad     peak compressive pressure seen this tick           (scratch)
      crushWork     accumulated comminution work                       (persistent)
      struct        structural weight −1…+1 (clusters/veins/fractal)   (fixed)

BOND  (a weld constraint between two cells that share a Voronoi edge)
      a, b          cell indices
      ra, rb        anchor offset in each cell's LOCAL frame — the shared-edge midpoint
      restAng       the relative angle the weld tries to hold
      len           shared edge length L — this is the bond's "cross-section"
      mult          structural strength multiplier
      nx, ny        bond axis A→B, in A's local frame
      Px, Py, Pa    impulse accumulated THIS SUBSTEP                   (scratch, zeroed each substep)
      fatigue       accumulated sub-threshold damage                   (persistent)
      broken        permanent

CONTACT  (rebuilt from scratch every substep — no warm starting)
      a, b          cell indices, always in DIFFERENT bodies
      nx, ny        normal, A→B
      depth         penetration
      ra, rb        contact point offset from each cell
      Pn, Pt        accumulated normal / tangential impulse
      excess        impulse the material REFUSED to carry (see §3.4)
```

**There is no energy parcel, no queue, no crack front, and no graph walk.** The only graph traversal in
the whole model is the connected-components flood fill, and it runs only when something breaks.

---

## 2. One tick, top to bottom

```
step(dt = 1/120):

  ── choose substeps so a fast impactor cannot tunnel through a cell ──
  maxV = max speed over all live cells
  sub  = clamp(ceil(maxV·dt / (0.6·cellSize)), 1, 8)
  h    = dt / sub

  repeat `sub` times:

    A.  for every bond:  Px = Py = Pa = 0          ← impulse accumulators are PER SUBSTEP
    B.  buildContacts()                            ← broad phase + SAT, from scratch

    C.  repeat `iterations` times:                 ← THE SOLVE (see §3)
          for each unbroken bond   : solveWeld(bond, h)
          for each contact         : solveContact(contact, h)

    D.  integrate:  x += vx·h,  y += vy·h,  rot += w·h

    E.  evaluateFailure(h)                         ← read the accumulated impulses, break things
```

Note the ordering: **failure is evaluated once per substep, after integration, from impulses accumulated
across all iterations.** Nothing breaks during the solve. §7 explains why that is a defect.

---

## 3. What one iteration actually does

An iteration is a single Gauss–Seidel sweep: visit every constraint once, and **apply its correction
immediately** so the next constraint in the list sees the updated velocities.

### 3.1 `solveWeld` — one bond

The weld's job is to make its two cells move as if rigidly attached at the shared-edge midpoint.

```
1.  rotate the anchors into world:   ra = R(A.rot)·bd.ra,   rb = R(B.rot)·bd.rb
2.  velocity of each cell AT THE ANCHOR (linear + ω×r):
        vA = (A.vx − A.w·ra.y,  A.vy + A.w·ra.x)
        vB = (B.vx − B.w·rb.y,  B.vy + B.w·rb.x)
3.  positional error — how far the anchors have drifted apart:
        e = (B.pos + rb) − (A.pos + ra)
4.  the constraint violation the solver wants to cancel:
        Cdot = (vB − vA) + (β/h)·e + γ·P_accumulated
5.  effective mass matrix K (2×2), from both cells' inverse mass and inertia, + γ on the diagonal
6.  impulse:  P = −K⁻¹·Cdot
7.  apply:    A.v −= P·A.im ;  A.w −= A.ii·(ra × P)
              B.v += P·B.im ;  B.w += B.ii·(rb × P)
8.  accumulate: bd.Px += P.x, bd.Py += P.y
9.  same again for the ANGULAR constraint (ω_B − ω_A → restAng), accumulating into bd.Pa
```

`β` and `γ` come from `softParams(k, mEff, h)` and encode **compliance**: the constraint behaves as a
stiff spring-damper rather than a hard weld, which is what makes the reported force a material quantity
rather than an artifact of `dt`.

**Step 7 is where propagation happens.** Correcting bond A–B changes B's velocity. When the sweep
reaches bond B–C, it reads that new velocity and passes some of it on. That is the entire propagation
mechanism.

### 3.2 Why sequential order matters — and a caveat

Because corrections apply immediately, a disturbance can travel **more than one cell per iteration** if
the bond list happens to be ordered along the direction of travel, and only **one cell per iteration**
if it is ordered against it.

`bonds[]` is in creation order, which is roughly spatial, so **propagation is measurably faster in one
direction than another**. That is a Gauss–Seidel artifact, not physics. It does not break determinism
(the order is fixed) but it is a real anisotropy, and a red–black or graph-coloured ordering would remove
it at some cost in convergence.

### 3.3 `solveContact` — one contact

Same shape, but a one-sided (non-penetration) constraint plus Coulomb friction, no restitution:

```
vn = closing speed along the normal
dPn = −(vn − (β/h)·penetration + γ·Pn) / (kn + γ)
Pn  = max(0, Pn + dPn)            ← one-sided: contacts push, never pull
   …clamped, see 3.4…
apply; then friction along the tangent, clamped to μ·Pn
```

`penetration` is clamped to `MAX_PUSHOUT` so an overlap inherited from a just-separated fragment oozes
apart over frames instead of being converted into kinetic energy in one tick.

### 3.4 The impulse clamp — the one place a constraint is limited

```
jMax = σ_crush · lc · h · fracCost
if Pn > jMax:  excess += Pn − jMax ;  Pn = jMax
```

The contact can only transmit what the material at it can bear. The **excess is recorded, not
discarded** — it feeds the crush pressure in §5. Discarding it caps pressure at exactly σ_crush so
nothing ever exceeds the threshold and nothing ever crushes.

**Welds have no equivalent clamp.** See §7.

---

## 4. Wave speed — what it actually does

The prototype labels `c` as *"how fast a disturbance crosses the material"*. **Measurement says that is
wrong.** Here is the mean cell speed by distance from the impact, 5 ticks after contact, with strengths
set to infinity so nothing breaks and only propagation is visible:

| c | 0–60 px | 60–120 | 120–200 | 200–300 | 300+ | far ÷ near |
|---|---|---|---|---|---|---|
| **200** | 49.8 | 7.1 | 0.3 | 0.0 | 0.0 | 0.00 |
| **400** | 65.3 | 17.3 | 2.3 | 0.1 | 0.0 | 0.00 |
| **1400** | 39.6 | 32.4 | 18.8 | 6.8 | 2.3 | 0.06 |
| **4000** | 7.7 | 9.8 | 16.4 | 21.9 | 23.4 | **3.04** |
| **10000** | 9.8 | 12.8 | 16.6 | 20.9 | 22.4 | 2.29 |

And one tick after contact:

| c | 0–60 | 60–120 | 120–200 | 200–300 | 300+ |
|---|---|---|---|---|---|
| 200 | 11.3 | 0.5 | 0.0 | 0.0 | 0.0 |
| 1400 | 42.0 | 13.2 | 3.1 | 0.3 | 0.0 |
| 10000 | 22.9 | 27.5 | 22.2 | 14.1 | 10.0 |

### What this shows

**`c` is an equilibration rate, not a wavefront speed.**

- **Low c** — the disturbance stays *trapped* near the impact. At c = 200, five ticks in, cells past
  120 px have not moved at all. All the momentum is concentrated in a handful of cells → enormous local
  stress → **crater**.
- **High c** — the body reaches rigid-body motion almost immediately. At c = 10000, *one tick* after
  contact, the far side is already moving at 10 px/s and by five ticks the whole body moves *faster at
  the far side than at the impact* (`far/near = 3.04`, because the near cells are still being
  decelerated by the contact). Nothing concentrates → **it just moves**.

So the fast/slow distinction is real and `c` is the knob that controls it — but the mechanism is
"how quickly does this body stop behaving like separate pieces and start behaving like one rigid body",
not "how fast does a wavefront advance".

There is no sharp front to measure, because Gauss–Seidel smears one: any nonzero velocity reaches far
cells almost immediately (see §3.2), it is the **amplitude** that decays with distance, and the decay
rate is what `c` sets.

### Does iteration count matter?

Yes, as a **ceiling**, not as the primary control:

```
solver ceiling ≈ iterations × cellSize / dt
     1 iteration  →   3,600 px/s
     2            →   7,200
    12            →  43,200
```

Measured on the projectile at fixed strengths:

| | iters 1 | 2 | 4 | 12 | 30 |
|---|---|---|---|---|---|
| **c = 1400** (below ceiling) | 14 | 12 | 12 | 9 | 11 bonds broken |
| **c = 6000** (above the low-iteration ceiling) | 8 | 6 | 7 | 3 | 2 |

At c = 1400 we sit below the ceiling even at one iteration, so results are stable and stiffness governs.
At c = 6000 the ceiling bites at low iteration counts and the answer moves a lot. **Iterations should be
set high enough to stay off the ceiling, then forgotten.** They are a numerical parameter, not a
material one.

---

## 5. Failure — `evaluateFailure`

Runs once per substep, after integration.

```
σ_ten   = ρ · c · v_crit          ← acoustic relation; see §6
σ_shear = σ_ten · shearMul
σ_crush = ρ · c · v_crush

PASS 1 — measure, break nothing
  for each unbroken bond:
      force  = P_accumulated / h          (P is a whole substep's worth)
      axial  = −(P·n)/h      + tension, − compression
      shear  = |P × n|/h
      moment = |Pa|/h
      σ_T = max(0,axial)/L + 6·moment/L²      ← axial + BENDING (beam formula)
      σ_C = max(0,−axial)/L
      σ_S = shear/L
      compression → cells' crushLoad (peak, not sum). It never breaks a bond.
      fatigue: accumulate above an onset fraction, relax below it
      if over threshold → push onto `cand` with its overload ratio

PASS 2 — break in order of overload, while the energy budget lasts
  sort cand by overload, descending
  for each: cost = L·σ_ten·mult·fracCost
            if cost > fractureBudget: STOP     ← overstressed but UNBROKEN → this is what
            budget -= cost; break it              holds multi-cell chunks together

CONTACTS → pressure and work
  press = (Pn + excess)/h / lc
  crushWork += F · v_closing · h          ← comminution is WORK, not time

CRUSHING
  if press > σ_crush and crushWork > comminutionEnergy × cellMass:
      cell dies; its momentum leaves with the dust

if anything broke → rebuildBodies()      ← connected components; each becomes its own body
```

---

## 6. Where mass comes in, and what ρ is

### ρ is density — mass per unit area — and it is a genuine material property

In the prototype it is one global slider. In a real implementation it should be **per material**, and
per cell via a `DensityMult` (which the C# engine already has).

### Density is *neutral for fracture*, and that is by construction

Measured — identical results at density 0.5, 1, 2, 5:

```
strength   σ = ρ·c·v_crit    ∝ ρ
stiffness  k = ρ·c²          ∝ ρ
mass       m = area·ρ        ∝ ρ
impulse    J = m·Δv          ∝ ρ
stress     σ = J/(h·L)       ∝ ρ        →  the RATIO is constant
```

Comminution too: threshold `comm × mass ∝ ρ`, work `F·Δx ∝ ρ`.

**What this means practically:** a denser version of the same material is heavier and carries more
momentum, but *fractures identically*. To make a genuinely different material you change **c** (how
stiff) or **v_crit** (how strong) — ρ, c and v_crit are three independent axes, exactly as they are for
real materials. Lead is dense and weak; carbon fibre is light and strong. Neither is expressible by
moving ρ alone, and that is correct.

### Mass is *everywhere* — it is only the uniform scaling of it that cancels

| where | what mass does |
|---|---|
| contact effective mass `1/kn` | reduced mass of the two contacting **cells** — this is why only the surface participates at first (measured: **2.0%** of the rigid-body reduced mass at contact, growing to 88.9% over ~30 ticks) |
| momentum available | a heavier impactor sustains the contact force longer → more penetration → more work → bigger crater (measured: 1 → 1 → 2 → 3 cells as impactor mass goes ×0.5 → ×6) |
| weld effective-mass matrix `K` | how much impulse it takes to correct a given velocity error |
| comminution threshold | `comminutionEnergy × cellMass` — a bigger cell costs more to pulverise |
| `crushLoad` divisors | stress is force ÷ length, and length is geometry, so cell size enters here too |

So: **mass ratios and absolute momentum decide everything; uniformly scaling all masses decides
nothing.** That is the correct physical statement and it is why density reads as inert.

---

## 7. Known defects

Listed because they are load-bearing for any conclusion drawn from this prototype.

### 7.1 Welds have unlimited authority

`solveWeld` applies whatever impulse zeroes the relative velocity. **There is no clamp.** Breaking is
decided *retroactively* in §5 from the impulse the weld already applied.

Consequences:

- A bond that should have failed mid-substep keeps holding for the remaining iterations and does work it
  physically could not.
- A weld can accelerate an arbitrarily heavy neighbour arbitrarily hard, because its authority does not
  depend on the mass it is moving.

**Fix:** clamp the weld impulse the same way contacts already are — `|P| ≤ σ_crit · L · h`. Then a bond
physically cannot transmit more than its strength, saturation *is* the break signal (causal instead of
post-hoc), and a bond trying to drag a very heavy neighbour saturates and breaks — which is exactly the
slim-column case, so the physics improves as a side effect.

### 7.2 `compStiff` is a hack

Stiffening compression reduces intra-body interpenetration (12.8 px → 3.2 px) but it does so by
**distorting the material** to paper over a collision-detection gap, and it is not monotone: bond
breaking rises again above ×5 (103 → 156 → 182 at ×10, ×20).

**Proper fix:** a **positional correction pass** — after the velocity solve, a separate position solve
that satisfies the welds without touching velocities, so drift is removed with no energy injected.
`compStiff` then returns to 1 and stiffness goes back to being purely `c`.

### 7.3 Intra- and inter-body compression are not calibrated alike

```
intra-body:  σ_C = bondForce / bd.len            ← the real shared edge
inter-body:  σ   = contactForce / (perim × 0.25) ← a proxy
```

Different divisors, so a cell crushed by its neighbours and one crushed by an external contact are
judged against effectively different thresholds. Needs a real contact-patch length from the SAT result.

### 7.4 `fracCost` does two unrelated jobs

It scales **both** the contact impulse clamp **and** the bond surface-energy cost. One slider, two
mechanisms — the exact hidden-coupling problem this document elsewhere warns about. Split into `G_c`
(energy per unit crack length) and no multiplier at all on the clamp.

For the record, `G_c` is *not* the same axis as `v_crit / v_crush`:

- **`v_crit / v_crush`** is a **stress** ratio — *which mode triggers first*, cracking or crushing.
- **`G_c`** is an **energy** — *how far failure propagates once triggered*.

Griffith needs both: `σ > σ_c` for initiation, `G ≥ 2γ` for propagation. A material can be easy to start
cracking but hard to keep cracking (tough) or the reverse (brittle), and only `G_c` expresses that.

### 7.5 Gauss–Seidel ordering makes propagation directionally biased

See §3.2. Fixed order, so determinism holds, but it is an anisotropy with no physical basis.
