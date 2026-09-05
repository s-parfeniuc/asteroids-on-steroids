# Prototype Plan & Engine Refinement

An intermediary document between the run design and the port. Three parts:

1. **The plan** — what gets built, in what order (§1)
2. **The prototype** — scope, architecture, and the technical decisions it forces (§2–§4)
3. **Engine refinement** — limitations, extensions and reworks worth doing (§5)

Companion to [run-design.md](run-design.md) (what the game is) and [PORT_PLAN.md](PORT_PLAN.md) /
[PHASE1.md](PHASE1.md) (how the port works).

---

## 1. The plan

### 1.1 Sequence

```
  ┌─ NOW ──────────────────────────────────────────────────────────────┐
  │  A. Refine the simulation in src/    ← engine-refinement.md        │
  │  B. Port the refined sim to ported/  ← PHASE1, unchanged           │
  │  C. Build the prototype in Godot     ← §2–§4                       │
  └────────────────────────────────────────────────────────────────────┘
```

The prototype is built **in the ported codebase**, in Godot. The whole simulation is ported first — but
it is the *refined* simulation, not the current one.

### 1.2 Why refine before porting

An earlier draft of this section proposed folding engine changes into the port, split into
behaviour-neutral changes (safe during) and behaviour changes (only after). **That scheduling is
withdrawn**, for one reason that outweighs it:

> **These are feel changes, and only the legacy build is playable.**

Refining in `src/` lets every change be played the day it lands. Porting a refined model costs the same
as porting the current one, and the diff harness then validates *the model we chose* rather than the
model we inherited — which is what makes building the harness worth the effort.

PHASE1 is preserved exactly as written. Its exit criteria simply describe a better baseline.

The Bucket A/B/C split in [engine-refinement.md](engine-refinement.md) therefore survives only as a
**priority ordering**, not a schedule.

### 1.3 What the prototype includes

**In:** the solar system map · the freeze / active-range LOD system · analytic orbits · force fields ·
procedural generation of the world and its encounters · **multiplayer**.

**Out:** Instances (no transitions, no persistence) · XP and skill trees · items · loot sites · repair ·
ammo · meta-progression · Sites and Instances as authored content kinds.

This is deliberately the *systemic* half. It builds the things whose feel cannot be predicted on paper
and whose cost is unknown, and skips everything that is a content or tuning problem.

---

## 2. What the prototype is for

Five questions. Every scope decision below serves one of them; anything serving none is cut.

| # | Question | Fails if |
|---|---|---|
| **Q1** | Does a solar system at this scale feel like a **place** worth flying around? | it reads as empty space with a map screen |
| **Q2** | Does the **radial gradient** create real decisions? | players find one optimal depth and sit there |
| **Q3** | Does **freeze/LOD** hold together at 150–250 screens? | visible popping, or the frame budget dies |
| **Q4** | Does **deterministic lockstep multiplayer** work with this simulation? | desyncs, or input delay makes it unplayable |
| **Q5** | Is the **destruction still fun** at this scale, with forces acting on everything? | the fracture reads as noise rather than as an event |

**Q4 is the highest-risk item in the entire project** and the reason multiplayer is in the prototype
rather than deferred. Every determinism decision so far — `SimMath`, `DetRng`, the fingerprint gate —
was made to serve it, and it has never been tested against a real simulation.

---

## 3. Prototype architecture

### 3.1 What runs where

Unchanged from PORT_PLAN §3: `AsteroidsSim` owns everything deterministic; Godot renders and provides
transport. The prototype adds three subsystems to the sim.

```
AsteroidsSim/
  World/          ★ new — the solar system
    SystemGen.cs      procedural generation, seeded
    Orbits.cs         analytic position from (t)
    Fields.cs         force fields as placed entities
    Lod.cs            active bubble, promote/demote, freeze
  Sim/
    SimState.cs       + the frozen set, + field table, + orbit table
```

### 3.2 The three new systems

**Orbits.** A table of `(centre, radius, ω, θ₀, bandId)`. Position is a closed-form function of the
tick. Zero simulation cost, exactly reproducible, no integration error accumulating over a 25-minute
run. Orbiting bodies are never integrated — they are *evaluated*.

**Fields.** Placed entities with a shape, a falloff and a strength, not global constants. The prototype
needs one kind: a radial well at the system centre. The design wants several (§5), but one is enough to
answer Q1 and Q2.

**LOD.** Four levels, promoted and demoted by distance from **player simulation positions** — plural
(§4.2). L0 full physics · L1 kinematic only · L2 analytic (orbiting bodies) · L3 frozen in place.
Nothing is destroyed by distance; the memory arithmetic in run-design §4 says we can afford it.

### 3.3 Procedural generation

One seeded generator producing: band radii, orbital assignments with the non-overlap constraint
(run-design §5.2), body distribution and density per band, material distribution, and encounter
placement. **The entire system is a pure function of `(seed, contentHash)`** — which is also what makes
it shareable across clients for free, since each client generates the identical world from the seed
rather than receiving it.

That is worth stating plainly: **procedural generation is a netcode feature.** A 250-screen system costs
one integer on the wire.

### 3.4 Encounters in the prototype

No authored Sites, no Instances. Encounters are **procedural territory**: mob packs placed by the
generator with density and tier scaled by band, plus ambient hazards. Enough to answer Q2 and Q5;
nothing more.

---

## 4. Technical decisions the prototype forces

Each is a real fork with a recommendation, not a formality.

### 4.1 Multiplayer model for the prototype

**Recommendation: deterministic lockstep with fixed input delay, listen server, 2–4 players.**

It is the simplest *correct* thing, it is what the architecture was built for, and it directly answers
Q4. Prediction and rollback are strictly harder and can be added later against a working baseline.
Fixed delay (not adaptive) keeps the first version debuggable.

**[DECISION NEEDED]** Confirm 2–4 rather than up to 16 for the prototype. Higher counts multiply active
bubbles (§4.2) and make desync bisection much harder.

### 4.2 Multiple players means multiple active bubbles

Consequence nobody has costed yet: **the active region is the union of per-player bubbles.** Two players
80 screens apart means two full L0 regions, doubling the active-body budget. Four separated players
quadruple it.

Options: budget for N bubbles (honest, expensive) · soft-tether players · let the LOD budget degrade
gracefully when players separate. **Recommendation: budget for N and measure.** This is a Q3/Q4 finding
worth having early.

### 4.3 Couch co-op and a 250-screen map are in tension

Local co-op was specified with a **shared zoom-to-fit camera**. On a map this size that is untenable the
moment players separate — either they are tethered to one screen's worth of space, or couch co-op needs
split-screen after all.

**Recommendation: prototype online multiplayer only.** Defer couch co-op until the map's real scale is
known from Q1. Flagging it because it is a design constraint discovered by scale, not a bug.

### 4.4 Rendering 250 screens

The camera sees ~1 screen of a 150–250 screen world. Needed: a zoomable tactical view, the system map as
a separate UI surface (run-design §3.4), and off-screen indicators. **The renderer only ever draws the
L0/L1 set**, so render cost tracks the bubble, not the world.

**[DECISION NEEDED]** Is the system map a full-screen overlay, or a persistent minimap? The former is a
planning surface; the latter is a navigation aid. The design leans planning surface.

### 4.5 Player movement and forces

Forces must be able to move the player, which the current movement controller prevents by clamping
velocity whenever a direction is held (PORT_PLAN §5, finding 5). **The prototype needs an
external-velocity channel that decays rather than being clamped** — this is a prerequisite for Q1, not
an enhancement.

**[DECISION NEEDED]** Ship top speed and the field's strength are the two numbers that decide whether
the system feels vast or tedious. They must be tuned together (run-design §3.1), and cruise mode is
deferred until this is measured.

### 4.6 Tick rate and the netcode wire format

60 Hz fixed (PORT_PLAN §2.7). Under lockstep the tick rate is a **protocol** parameter, so it must be
settled before the wire format exists. Already decided; restated because the prototype is the first
thing that makes it irreversible.

### 4.7 What the prototype does *not* need

**Static/kinematic bodies are not required.** They are needed for Temples, caves and planet surfaces —
all Instances, all out of scope. This removes the single most expensive engine item (§5) from the
critical path, which is a significant scheduling win.

---

## 5. Engine refinement

**Moved to [engine-refinement.md](engine-refinement.md)**, which supersedes this section. It carries the
decisions taken since — per-cell material and `bondAffinity`, cluster shapes, the three-stage contact
refactor (stress-gated initiation → persistent metered contacts → impulse clamped by material strength),
time-of-arrival crack propagation, `BreakPerp`'s removal, and the withdrawal of elastic bonds and
hierarchical bodies.

The two structural limits that motivated everything are restated here because the rest of this document
refers to them.

### 5.0 The two structural limits behind most of the others

**One material per body.** `FracturableBody` carries a single `FractureProperties`. Brittleness,
toughness, crackSpeed, relaxRate and cellToughness are all body-wide. This one fact makes composite
structures — armour over a soft core, crust/mantle/core, a reinforced door in a plain wall, ore veins —
*inexpressible*, and it is upstream of half the design's ambitions.

**Structure is isotropic.** Cluster heterogeneity comes from a Dijkstra ball over inter-centroid
distances with a linear falloff — a soft radial blob and nothing else. There is no direction, no grain,
no layering. **Veins, bedding planes, cleavage directions and weld seams are not expressible at all.**

Almost every "reads as a structure you can learn" idea in the design traces back to one of these two.
Both are addressed in [engine-refinement.md](engine-refinement.md) §2.1 and §2.3.

---

## 6. Risks

| Risk | Why it matters | Mitigation |
|---|---|---|
| **Lockstep desync in a real simulation** | Q4; the whole determinism apparatus is unproven against actual gameplay | Fingerprint every tick in dev builds; bisect on first divergence; keep the replay harness from PHASE1 |
| **Refinement destabilises a working game** | Stage A changes feel, and `src/` is the only playable build | One change per playtest; keep each behind a config value so it can be reverted without a rebuild |
| **The refined model is harder to port** | Energy coupling and eikonal fronts touch systems PHASE1 already scoped | Land refinements *before* the corresponding PHASE1 step, never during |
| **Multiple bubbles blow the frame budget** | §4.2 — untested and unbudgeted | Measure with players deliberately separated, early |
| **The system feels empty** | Q1 — the failure mode of any large map | Prototype the field and gradient *before* content; if flying an empty system is boring, content will not save it |
| **Scale/speed tuning eats the schedule** | §4.5 — two coupled numbers that decide everything | Expose both live; treat as the first playtest, not the last |

---

## 7. Sequencing

| Stage | Content | Gate |
|---|---|---|
| **A** | Engine refinement **in `src/`** — [engine-refinement.md](engine-refinement.md) §4, items 1–6 | Each change playtested and kept; asteroid-on-asteroid feels right |
| **B** | Complete PHASE1 port of the refined sim | PHASE1 exit criteria |
| **C** | Fields + external-velocity channel + one radial well | Flying an empty system is interesting (**Q1**) |
| **D** | Orbits + generation + the system map | The map is a planning surface (**Q1**) |
| **E** | LOD + freeze at full scale | No popping, budget holds (**Q3**) |
| **F** | Encounters + the gradient | Depth is a decision (**Q2**, **Q5**) |
| **G** | Lockstep multiplayer, 2–4 players | Zero desyncs over a full session (**Q4**) |
| **H** | Remaining refinement items (§4, 7–10), informed by C–G | — |

**Stage C is the cheapest kill-switch in the project.** If flying a radial field in an empty system
isn't interesting, the archetype's premise is wrong and everything after it is wasted — and finding
that out costs days, not months.

---

## 8. Decisions needed before starting

1. **Refinement scope** — how far down [engine-refinement.md](engine-refinement.md) §4 before porting?
   My vote: items 1–6, which are the ones that change how the game *feels*.
2. **Prototype player count** — 2–4, or higher? (§4.1)
3. **Couch co-op** — prototype it, or defer given §4.3?
4. **System map** — full-screen planning surface, or persistent minimap? (§4.4)
5. **The heat channel** — the one proposal not yet ruled on
   ([engine-refinement.md](engine-refinement.md) §2.11). Still my vote for the ambitious swing.
