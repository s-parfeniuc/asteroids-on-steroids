# The Fracture / Destruction Model

How a hit turns a `FracturableBody` into fragments and dust, and what every
parameter does. Code lives in `Engine/Destruction/` (`FractureService`,
`FractureSimulator`, `FractureKernel`, `FractureCrackSystem`, `VoronoiTessellator`)
and `Engine/Components/` (`FractureProperties`, `Cell`, `Bond`).

A body is a graph: **cells** (convex polygons) joined by **bonds** (springs along
shared edges). An impact deposits **energy** at the struck cell; that energy floods
the graph and is split, at every cell it touches, into four named channels that
**sum to the input — nothing is lost**:

| channel | what it does |
|---|---|
| **break** | energy consumed creating new crack surface (breaking bonds) |
| **vaporize** | energy that pulverised cells into dust (the crater) |
| **fling** | kinetic energy handed to the fragments (fly-apart + tumble) |
| **transmit** | energy still travelling (≈0 once a hit fully resolves) |

This conservation is the whole point of the model: every parameter moves energy
*between named channels*, so you can always reason about where it went. (This
replaced an older non-conservative flood where brittleness was a lossy decay and
energy silently vanished.)

---

## What's stored

### Per cell (`Cell`)
| field | meaning | set when |
|---|---|---|
| `Local[]` | body-local polygon vertices (centroid-relative) | tessellation; shrunk on single-cell detach |
| `Centroid` | body-local centroid | tessellation |
| `Area` | px² — drives mass, vaporise threshold, fling | tessellation |
| `DensityMult` | per-cell density multiplier (dense core) | clusters / shape seeds |
| `Role` | `cockpit`/`cannon`/`propeller`/… (gameplay) | shape seeds; **preserved through fracture** |

A cell's **mass** is `Area · DensityMult · Density`. Mass is the single
resistance-to-vaporization axis — *armor = dense (and heavy)*. (The old
`BlastResist` is gone.)

### Per bond (`Bond`)
| field | meaning |
|---|---|
| `A`,`B` | the two cell indices |
| `EdgeLength` | length of the shared Voronoi edge |
| `StrengthMult` | per-bond multiplier = √(bondMult\[A]·bondMult\[B]) from clusters |
| `Strength` | stress a bond must accumulate to break = `EdgeLength · Toughness · StrengthMult` |
| `Stress` | **runtime accumulator** — delivered stress sums here; bond breaks at `Stress ≥ Strength`; decays by `RelaxRate` |

`Strength` is computed **once at tessellation and never mutated**. `Stress` is the
per-bond damage accumulator: a hit too weak to break a bond still leaves stress on
it, so **repeated hits accumulate and eventually crack a tough body** (sustained
fire). `StressRelaxSystem` decays `Stress` by the material's `RelaxRate` each frame,
so the fire rate must outpace the relaxation to break through.

---

## Material parameters (`FractureProperties`, `game_config.json` `materials`)
| param | channel it steers | effect |
|---|---|---|
| `Brittleness` | transmit ↔ dump | the central lever. `dump = (1−Brittleness)·e` stays local (fling/vaporize); `transmit = Brittleness·e` travels as cracks. 0 = blunt crater + big fling, short cracks; 1 = long thin cracks, little local damage |
| `Toughness` | break | bond `Strength = EdgeLength · Toughness · StrengthMult` — the stress a bond survives. High toughness ⇒ a single hit can't crack it; sustained fire must accumulate |
| `Density` | vaporize / fling | cell mass `= Area·DensityMult·Density`. Resists vaporization and slows fling. The armor axis |
| `Restitution` (e) | impact | bounce vs fracture: only `(1−e²)` of contact energy becomes the input energy `E` |
| `CrackDirectionality` | transmit | material grain/cleavage; averaged with the weapon's `Directionality` to steer which bonds the transmit is divided across |
| `SpinPreStress` | break | gain on how strongly body spin ω multiplies delivered bond stress (centrifugal weakening at the rim) |
| `RelaxRate` | break | stress/sec the per-bond `Stress` decays; the "sustained-DPS demanded" knob — high for armor, low for brittle |
| `CrackSpeed` | pacing | cells/sec the crack front advances (multi-frame). Replaces the old global `crackSteps`/`crackFrames` |
| `GrainArea` | tessellation | target cell area → cell count, and the fling-speed reference |
| `MinFragmentArea` | fragments | below this, a piece is visual debris, not a physics body |
| `DetachCellScale`/`Jitter` | fragments | shrink a lone detaching cell so it doesn't refill its socket |

## Weapon parameters (`WeaponProfile`, `game_config.json` `weapons`)
Four knobs — everything else is derived from the energy that actually flows.

| param | channel it steers | effect |
|---|---|---|
| `BlastFraction` | vaporize | crater size. Carves the vaporize budget `BlastFraction·e` from the *full* incoming energy (independent of brittleness) |
| `Directionality` | transmit | impact-cone focus, averaged with material `CrackDirectionality` |
| `Knockback` | recoil | one-time push on the struck body along the impact normal |
| `Mass` | impact | impactor mass feeding the energy `E` (a heavy/fast bullet deposits more). Aliens/shrapnel carry a real `Mass` here |

Dropped vs the old model: `Energy`, `EjectFraction`, `ImpactSpin`,
`MomentumTransfer` (→`Knockback`), `Concentration`. Fling speed and tumble are now
**derived** from the fling energy reaching each fragment; there is no ignition
stress, so `Concentration` is gone — toughness + bond-stress accumulation do that
job.

### Tuning constants (`FractureGlobalConfig`)
Model-shaping scalars not worth a per-material knob:
`KVapor` (vaporize threshold scale: `thresh = KVapor · mass`), `SurfaceEnergyPerLen`
(break cost per px of bond), `FlingScale` (fling energy → speed), `AlignExponent`
(directional cone sharpness), `SpinCap` (max spin stress multiplier).

---

## Stage 1 — Impact → energy `E`
`FractureService.ComputeImpact`. The struck cell, impact normal `n`, impactor
velocity & `Mass`, and the body's velocity/mass/inertia give the dissipated energy
fed to the flood:

```
v_n     = |(impactorVel − bodyLinear) · n|              # normal closing speed (incl. ω×r for collisions)
r       = impactPoint − bodyCentroid
1/m_eff = 1/Mass + 1/m_body + (r×n)² / I_body           # effective mass at contact (spin enters here)
E       = ½ · m_eff · v_n² · (1 − e²)                   # dissipated energy → floods the graph
```

There is **no ignition gate** and **no stress `σ`** — a hit always deposits its `E`
and floods. Whether anything *breaks* is decided downstream by bond stress vs
strength, so a weak hit on a tough body simply leaves a little stress behind.
A one-time recoil `kick = n · (impactorSpeed · Knockback)` is applied to the body.

## Stage 2 — Conservative propagation (`FractureKernel`)
The flood is a **priority queue, highest-energy cell first** (a Dijkstra-style order
that guarantees each cell is processed once at its peak — deterministic and
mesh-stable). A cell receiving energy `e` is resolved like this:

# VAPORIZE vs SURVIVE differ only in what the cell keeps LOCALLY; the leftover `transmit`
# travels onward through the SAME bond channel either way.
thresh = KVapor · Area · DensityMult · Density
if BlastFraction · e ≥ thresh:                              # vaporise (carved from FULL e, not brittleness)
    cell vaporises;  vaporize += thresh
    transmit = e − thresh                                   # keeps no local fling (it's dust); ALL surplus travels on
else:                                                       # survive
    dump = (1 − Brittleness) · e ;  fling += dump           # local fling
    transmit = Brittleness · e

# transmit is DIVIDED across intact out-bonds by alignment (directional) and breaks bonds (toughness):
for each unbroken out-bond to neighbour j:
    align  = (dir to j) · (impact dir, body-local)
    w      = lerp(1, max(0,align)^AlignExponent, effectiveDirectionality)
    share  = transmit · w / Σw                              # CONSERVATIVE: Σ shares = transmit
    bond.Stress += share · (1 + spinFactor(bond))           # spin multiplies STRESS, not energy
    if bond.Stress ≥ bond.Strength:
        bond breaks
        eSurf = min(share, SurfaceEnergyPerLen · EdgeLength);  break += eSurf
        deliver (share − eSurf) to j                        # rides the queue, or becomes j's fling if settled
    else:
        break += share                                      # sub-threshold: work stored on the intact bond
```

Key properties:
- **`effectiveDirectionality = (weapon.Directionality + material.CrackDirectionality)/2`**
  steers *which* bonds the transmit is divided across (forward cone vs splash).
- The split is **strictly conservative** — `transmit` is *divided* among neighbours
  (weighted by alignment), never copied. So crack reach is mildly sensitive to local
  cell count, kept stable by the uniform grain + forward focus.
- **`spinFactor(bond) = clamp(SpinPreStress · ω² · (0.3 + 0.7·r/rmax) · tangentiality, 0, SpinCap)`**
  — a fast spinner's tangential rim bonds take more stress per joule, so it shatters
  from a lighter hit. Bond `Strength` is untouched; spin amplifies the *delivered
  stress*.

### Edge cases (no energy sink)
- **Crater expansion.** A vaporised cell consumes only its mass `thresh` and routes the
  rest through its `transmit` bonds (same channel as a crack). Neighbours along that path
  vaporise too if `BlastFraction·share ≥ their thresh`, so the crater grows with energy —
  but **forward** (directionality) and only as far as the energy can keep breaking bonds
  (toughness), not as a uniform radial splash. When the energy thins below thresholds the
  rim cells survive and the leftover becomes their fling. (A big blast carves a big hole;
  energy never dies on a vaporised cell.)
- **Isolated / last cell** (no intact bonds): `transmit` has nowhere to go → becomes
  that cell's fling.
- **Blocked crack** (focused shot, every out-bond points *behind* the crack): default
  **spread** — the equal weights send the transmit sideways (the crack wraps). Optional
  **dump** mode converts the blocked transmit to local fling (a focused shot that can't
  tunnel shoves locally).

## Stage 3 — Fragments + motion (derived)
Connected components over the **surviving** bonds (unbroken, neither cell vaporised)
become fragments. Fling is **derived from the fling energy that actually reached each
cell** — no eject/spin knobs:

```
fragment mass = Σ_cell Area·DensityMult·Density
fragment Ekin = Σ_cell flingE
speed   = clamp(FlingScale · √(2·Ekin / mass), …)            # heavier ⇒ slower
dir     = away from the impact point
omega   = (asymmetry of flingE about the fragment centroid) / inertia   # off-centre dump ⇒ tumble
fragment < MinFragmentArea  →  visual debris, not a physics body
```

The largest surviving component is the remaining body (it takes the `Knockback`
recoil rather than a fling). Vaporised cells are emitted as fading dust.

## Stage 4 — Multi-frame pacing (`FractureProcess` / `FractureCrackSystem`)
The flood doesn't finish in one frame: the front advances at the material's
**`CrackSpeed`** (cells/sec → steps per fixed-step), so cracks visibly race across the
body. Multiple hits push co-propagating fronts that share the accumulating
broken-bond / vaporised / bond-stress state and fuse where they meet. Pieces detach
as the cracks separate them. (The old single-frame `TryFracture`/`Simulate` path and
the `DetachOnSplit` flag are gone — there is only the paced path.)

---

## Procedural asteroid heterogeneity — clusters
Unchanged. Seeds are placed **uniformly** (count from `GrainArea`); heterogeneity
comes from **clusters** (`AsteroidPrefab.ApplyClusters`): pick `ClusterCount` centre
cells (radius `(1−ClusterCentrality)·R`), and from each run a Dijkstra over
inter-centroid distances out to a per-cluster reach `ClusterSpread·R`. A cell's
contribution `c = (reach − graphDist)/reach` accumulates across clusters into `A`:
```
bondMult     = 1 + A·BondGain        # → re-weights bond Strength + StrengthMult
DensityMult  = 1 + A·DensityGain     # → heavier, vaporise-resistant
```
- **Dense core**: few high-`Centrality` clusters, high `DensityGain`.
- **Armored shell**: many low-`Centrality` (surface) clusters, high `BondGain`.
- **Veins / nodules**: a few mid clusters.

(`BlastGain` / `BlastResist` are removed — density now carries vaporize-resistance.)

---

## Summary of the currency
One currency end to end: **energy**, conserved into break / vaporize / fling. There
is no separate surface budget, no ignition stress, no kinetic-vs-surface split — a
hit deposits `E`, and every cell it reaches divides that `E` among the channels until
it is spent. Toughness gates breaking, brittleness partitions travel-vs-dump, blast
sizes the crater, density resists vaporization, and bond stress accumulates so
sustained fire wins where one shot can't.
