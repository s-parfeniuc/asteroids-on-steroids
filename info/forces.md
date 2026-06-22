# Forces, impulses & velocities — reference + logging

Every place the engine or game changes a body's **velocity** or **spin** (angular velocity),
with the exact formula, the intuition behind it, and the tunable that controls it. Pairs with
the in-game force logger.

---

## Using the logger

- **Toggle:** press **L** in the demo. Writes to `forces.log` (in the run directory) — one
  line per velocity/spin/force application, stamped with the fixed-step frame number.
- **Line format:** `[f<frame>] <CATEGORY> e<entityId> <result = term + term + …>`
- **Filtering** (`ForceLog` static, set in code or a debugger):
  - `ForceLog.Categories` — a `[Flags]` mask. Default = everything **except** `Integration`
    and `Drag` (those fire for every body every step — the firehose). Set
    `ForceLog.Categories = ForceCat.All` to include them, or e.g.
    `ForceCat.Fling | ForceCat.Recoil | ForceCat.Energy` to isolate fracture tuning.
  - `ForceLog.EntityFilter` — set to an entity id to log only that body (tames the per-step
    channels); `-1` = all.
- Fixed step is **1/120 s** (`FixedDt`), so `dt = 0.00833`. All velocities are **px/s**,
  spins **rad/s**, energies in the abstract unit `mass·(px/s)²`.

The categories: `Integration, Drag, Thrust, Contact, Separation, Recoil, Fling, Debris, Spawn, Energy`.

---

## 0. Concepts & terms

### Two parallel worlds: linear (movement) and angular (rotation)

Every body has **two independent kinds of motion**, and every quantity below comes in a
linear flavour and a matching angular flavour. They obey the same maths; only the names change.

| Concept | Linear (moving through space) | Angular (spinning in place) |
|---|---|---|
| **position** | `Transform.Position` (px) | `Transform.Rotation` (radians) |
| **velocity** | `Velocity.Linear` — px/s, a 2-D vector | `Velocity.Angular` (ω) — rad/s, a scalar |
| **what resists change** (inertia) | `Mass` (m) | `Inertia` (I) — "rotational mass" |
| **what causes change** | `Force` (F) — a push, 2-D vector | `Torque` (τ) — a twist, scalar |
| **instant change of velocity** | `Impulse` (P) — 2-D vector | angular impulse — scalar |
| **acceleration** | `F / m` | `τ / I` |

In 2-D, **rotation is a single number** (clockwise/counter-clockwise), so angular velocity,
torque, and angular impulse are *scalars*, not vectors. Position and linear velocity are 2-D
vectors `(x, y)`.

### Force vs. impulse vs. velocity — the three things people conflate

- **Velocity** is the *state*: how fast a body is moving right now. It's what we ultimately
  read and what makes things look fast or slow.
- **Force** is a *continuous push*. It does **not** change velocity directly — it changes it
  *over time*: `Δv = (F / m) · dt`. A force applied for a longer time changes velocity more.
  Forces are accumulated during a step and integrated once (§1). Gravity, thrust, drag.
- **Impulse** is an *instantaneous* velocity change — a force concentrated into zero time:
  `Δv = P / m`. Used for collisions and the fracture kick, where the change must happen *now*,
  this step, not ramp up. An impulse is "a whole force's worth of effect, applied at once".

Rule of thumb in this engine: **springs/engines/drag → forces** (§1–3); **impacts/collisions
→ impulses** (§4, §8). Both ultimately edit `Velocity`.

### Geometry terms (collisions)

- **Contact point** — the world-space point where two shapes touch.
- **Normal (`n`)** — the unit vector *perpendicular* to the touching surface; the direction
  along which bodies must separate. Here it always points **from B toward A**. The "head-on"
  push direction.
- **Tangent (`t`)** — the unit vector *along* the surface (90° from the normal); the direction
  things slide. Friction acts here.
- **Lever arm (`r`)** — the offset from a body's centre to the contact point
  (`r = contactPoint − centre`). It's why an off-centre hit *spins* a body: the same impulse
  produces more rotation the longer the lever arm.
- **Penetration depth** — how far two shapes overlap; what positional separation (§6) removes.

### 2-D cross product (appears everywhere rotation meets position)

In 2-D the cross product of two vectors is a **scalar**: `a × b = a.x·b.y − a.y·b.x`.
Two uses:
- **Torque from a force** at offset `r`: `τ = r × F`. (A push far from centre twists more.)
- **`ω × r`** (here written as the vector `(−ω·r.y, ω·r.x)`): the **linear velocity a spinning
  body has at point `r`**. A point on the rim of a spinning rock is moving even though the
  rock's centre is still — this term is how a fragment "inherits" the parent's spin as linear
  motion when it detaches.

### Other terms

- **Reduced mass** `mRed = mA·mB/(mA+mB)` — the effective mass of a two-body impact. If one
  body is huge, `mRed ≈ the small one`; if equal, `mRed = m/2`. Governs how much energy a
  collision delivers (§7).
- **Restitution (`e`)** — bounciness, 0 = no bounce (inelastic), 1 = perfect bounce (elastic).
- **Friction (`μ`)** — resistance to sliding; caps the tangent impulse relative to the normal one.
- **Symplectic Euler** — the integration ordering "update velocity, *then* position", which is
  stable for games (energy doesn't blow up over time).

---

## 1. Integration — `PhysicsSystem` (cat `Integration`)

Symplectic Euler, once per body per step:

```
v.Linear  += (AccumulatedForce  / Mass)    · dt
v.Angular += (AccumulatedTorque / Inertia) · dt
```

**Intuition:** Newton's second law, `a = F/m`, integrated over one step. Velocity is updated
*before* position (`MovementSystem` runs next), which is the stable "symplectic" ordering.
`AccumulatedForce/Torque` are summed by `ApplyForce*` during the step and zeroed after.
Gravity is `0` in this game.

---

## 2. Drag — `PhysicsSystem` (cat `Drag`)

```
v.Linear  *= e^(-LinearDrag  · dt)
v.Angular *= e^(-AngularDrag · dt)
```

**Intuition:** exponential decay, not subtraction — a body loses a *fraction* of its speed
per unit time, so it asymptotes toward zero without overshooting to negative. `LinearDrag`
is a rate in s⁻¹: at `drag=1`, after 1 s the body retains `e⁻¹ ≈ 37%` of its speed.
Tunables: **Linear drag**, **Angular drag** (per-body, live).

---

## 3. Applied force / thrust — `PhysicsSystem.ApplyForce` (cat `Thrust`)

```
AccumulatedForce += force          // integrated by §1 this step
```

Player thrust: `force = normalize(input) · Thrust` (**Thrust** tunable). An off-centre force
(`ApplyForceAtPoint`) also adds torque `τ = r × F = rx·Fy − ry·Fx`.

**Intuition:** forces accumulate during the step and convert to a velocity change via §1, so a
force is mass-scaled (heavy bodies accelerate less). Distinct from the *impulse-based* fracture
kick (§7), which is applied directly to velocity.

---

## 4. Contact impulses — `CollisionSystem.SolveContact` (cat `Contact`)

Sequential-impulse solver, **6 iterations** per step (logged as the converged **net** per
contact). For each contact with unit normal `n` (B→A) and tangent `t`:

**Effective masses** (how much a unit impulse changes the closing speed, including the lever
arm of rotation):
```
kn = 1/mA + 1/mB + (rA×n)²/IA + (rB×n)²/IB        NormalMass  = 1/kn
kt = 1/mA + 1/mB + (rA×t)²/IA + (rB×t)²/IB        TangentMass = 1/kt
```

**Normal impulse** (removes the approach velocity `vn`, plus restitution bias §5):
```
vn  = dot(vA@contact − vB@contact, n)
dPn = NormalMass · (bias − vn)
AccumN = max(AccumN + dPn, 0)          // never pull bodies together
```

**Friction impulse** (Coulomb-clamped to the normal force):
```
vt  = dot(vRel, t)
dPt = TangentMass · (−vt)
AccumT = clamp(AccumT + dPt, −μ·AccumN, +μ·AccumN)
μ = sqrt(FrictionA · FrictionB)
```

**Applied to velocity** (`P` = total impulse on A):
```
vA.Linear += P/mA ;   vA.Angular += (rA × P)/IA
vB.Linear −= P/mB ;   vB.Angular −= (rB × P)/IB
```

**Intuition:** instead of one big correction, the solver nudges every contact a little, 6
times, so stacked/coupled contacts converge instead of fighting. The `max(·,0)` clamp means
contacts can only push apart. Lever arm `r×n` is why an off-centre hit imparts spin.
Tunables: **Restitution**, **Friction** (per body); `Iterations = 6`.

---

## 5. Restitution (bounce) — `CollisionSystem.GatherContact` (part of `Contact`)

```
e    = min(RestitutionA, RestitutionB)
bias = (vn0 < −30 px/s  AND  depth < 6 px) ? −e · vn0 : 0
```

**Intuition:** the bounce is injected as a target separating velocity in the normal solve.
Two guards: only above a **30 px/s** approach (`RestitutionVelThreshold`, so resting stacks
don't jitter), and only for shallow overlaps (`DeepNoBounce = 6 px`, so a body that *spawns*
deep inside another oozes out instead of exploding).

---

## 6. Positional separation — `CollisionSystem.TrySeparate` (cat `Separation`)

Position-only (Baumgarte), no velocity change:
```
corr = min( max(0, depth − slop) · CorrectionPercent , MaxCorrection )
A.Position += n · corr · (mB/(mA+mB))
B.Position −= n · corr · (mA/(mA+mB))
```
`slop = 0.5 px`, `CorrectionPercent = 0.4`, `MaxCorrection = 8 px/step`.

**Intuition:** the velocity solver removes approach speed but leaves residual overlap; this
gently pushes bodies apart, heavier body moving less. The `MaxCorrection` cap is critical —
deep overlaps (a fragment born inside a crater, a teleported body) ooze apart over several
frames instead of teleporting and flinging. Logged under `Separation` because it's a position
change, not a force, but it affects feel.

---

## 7. Impact energy & fracture budget — `FractureService.BeginFracture` / `ComputeBudget` (cat `Energy`)

```
mRed    = mImpactor · mBody / (mImpactor + mBody)      // reduced mass
vRelN   = |(impactorVel − bodyVel) · dir|              // closing speed along impact
eImpact = ½ · mRed · vRelN² · EnergyScale
```
Then the threshold + split:
```
struckMass = mBody · (struckCellArea / totalArea)
threshold  = Toughness · struckMass
eAvail     = (eImpact + AbsorbedEnergy) − threshold        // < 0 → absorb, no fracture
kFrac      = lerp(KineticFraction, KineticFraction·0.3, Brittleness)
eSurface   = SurfaceEfficiency · (1 − kFrac) · eAvail      // crack budget
eKinetic   = kFrac · eAvail                                // fling budget
```

**Intuition:**
- **Reduced mass** is why a bullet hitting a huge asteroid deposits ≈ the bullet's own KE
  (`mRed ≈ mBullet`), while two equal asteroids deposit far more (`mRed = m/2`).
- **`EnergyScale`** rescales the coupling between bulk KE and fracture for regimes the bullet
  calibration didn't cover — `1` for bullets, `~0.0002` for asteroid-on-asteroid (raw KE there
  is ~10⁹, which would shatter everything). See `info/meta_notes` on the Hertz long-term fix.
- **Threshold** rises with the struck cell's mass, so bigger targets need harder hits; below it
  the energy is *accumulated* (`AbsorbedEnergy`) so repeated taps eventually break it.
- The available energy splits into a **surface** budget (spent breaking bonds = cracks) and a
  **kinetic** budget (how hard fragments fly). Brittle materials put more into surface.

Tunables: **Bullet mass/speed**, **Energy x**, **Toughness**, **Surface eff.**, **Kinetic frac**,
**Brittleness**; **Ast E scale** for collisions.

---

## 8. Recoil kick — `FractureService.BeginFracture` (cat `Recoil`)

```
kick = dir · (impactorSpeed · MomentumTransfer)
body.Velocity.Linear += kick                  // at the instant of impact
```

**Intuition:** the struck body reacts *immediately* (not when the fracture finishes). Scaled to
impactor **speed**, not physical momentum, because a light bullet's true momentum is negligible
against a heavy rock — this is a feel knob. Fragments inherit it through `BodyLinear`, so it is
**not** re-added per fragment. Tunable: **Bullet push** (`MomentumTransfer`). For asteroid
collisions it's `0` — the contact solver (§4) already exchanges momentum.

---

## 9. Fragment fling — `FractureSimulator.FragmentMotion` (cat `Fling`)

**Linear:**
```
rotVel = ω_parent × r           r = fragmentCentroid − bodyCentre   (carries the parent's spin)
spread = normalize(fragmentCentroid − impactPoint)                  (radially away from the hit)
boost  = sqrt(refArea / max(area, refArea·0.1))                     (smaller piece → faster)
spd    = EjectSpeed · boost · (0.6 + 0.8·rand) · (debris ? 1.6 : 1)
linear = parentLinear + rotVel + spread · spd
```

**Angular:**
```
shear   = cross(spread, impactDir) = spread.x·dir.y − spread.y·dir.x
angular = parentAngular + ImpactSpin · shear + (rand−0.5)·lerp(0.4, 1.2, Brittleness)
result  = −angular        // sign chosen so the spin reads correctly vs the shot
```

**Intuition:**
- Fragments keep the velocity the parent surface already had at that point (`parentLinear +
  ω×r`), then scatter **outward from the impact point** at `spd`.
- **`boost`** makes small shards fly faster than big chunks (same energy, less mass).
- **Shear spin**: a fragment off to one side of the shot axis gets a consistent spin (think of
  the bullet "wiping" past it) — `cross(spread, impactDir)` is positive on one side, negative on
  the other, so debris on each side spins the matching way. Plus a brittleness-scaled random
  wobble.

Tunables: **Eject speed** (`EjectFraction`), **Impact spin**, **Brittleness**.

---

## 10. Continuer velocity — `FractureSimulator.BuildComponentSpec` (`fling = false`, cat `Fling`)

```
linear = parentLinear + ω_parent × r          // rigid-body velocity of this piece's centre
angular = parentAngular                        // unchanged
```

**Intuition:** the largest piece of a mid-spread split (the one that keeps cracking) must *not*
get a scatter impulse — otherwise it would jitter every time it sheds a chunk. It simply keeps
moving exactly as that part of the rigid body was already moving.

---

## 11. Polygon debris — `DemoSession.SpawnCellDebris` (cat `Debris`)

```
cellVel = bodyLinear + ω_parent × (cellCentroid − bodyCentre)     // computed at vaporise time
outward = normalize(pieceCentroid − cellCentroid)
vel     = cellVel + outward · (DebrisScatter · (0.5 + rand))
spin    = bodyAngular + (rand·2 − 1) · 4
```

**Intuition:** a vaporised cell is cut into 2–4 convex chunks (1–2 random lines through its
centroid). Each chunk inherits the cell's surface velocity, then scatters outward from the cell
centre so the pieces visibly burst apart, with a randomized spin around the parent's. No
collider; fades over **Debris ttl**. Tunables: **Debris ttl**, **Debris scatter**.

---

## 12. Asteroid-on-asteroid trigger — `DemoSession.OnCollision` (cat `Energy`)

```
vA@contact = vA.Linear + ωA × rA          (rA = contactPoint − A.centre)
vB@contact = vB.Linear + ωB × rB
vRel       = vB@contact − vA@contact
approach   = −dot(vRel, contactNormal)              // > 0 = closing
if approach < AsteroidCollisionThreshold (20 px/s): no fracture
```
Then each body is fractured with the other as impactor (§7–9). The crack **direction** into A:
```
impactDirA = normalize( lerp(contactNormal, normalize(vRel), AstDirSpin) )
```

**Intuition:** the relative velocity at the contact **includes both bodies' spin** (`ω×r`), so
a fast-spinning asteroid grazing another cracks tangentially, not head-on. **`AstDirSpin`** = 0
makes the crack follow the pure contact normal (head-on); = 1 follows the full relative velocity
(max spin influence). The **threshold** gates out gentle grazes so only real collisions fracture.

---

## 13. Spawn velocities — `DemoSession` (cat `Spawn`)

- **Asteroid:** `vel = randomDir · (rand · AstSpeed)`, `spin = (rand·2−1) · AstSpin`.
- **Bullet:** `vel = aimDir · BulletSpeed`.

Initial conditions only; thereafter §1–§12 govern motion.

---

## Force flow per frame (order matters)

```
PreviousStateSystem   snapshot pose (for render interpolation)
PlayerControlSystem   §3 thrust, bullet spawn §13
PhysicsSystem         §1 integrate forces, §2 drag
MovementSystem        position += v·dt ; rotation += ω·dt
RaycastBulletSystem   bullet hits → §7–9 (BeginFracture)
CollisionSystem       §4 impulses, §5 restitution, §6 separation, asteroid hits §12
FractureCrackSystem   advance cracks → §9–11 on split/finalise
EventFlushSystem      dispatches hit/pulverise/split events → demo spawns + §11 debris
```
