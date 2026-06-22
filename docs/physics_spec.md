# Physics & Fracture System Spec

**Scope:** Engine-level 2D rigid body dynamics and the polygon fracture subsystem built on top of it.
**Supersedes:** `fracture_spec.md` (archived).
**Cross-references:** `spec.md §6`, `modules.md §18–22`.

---

## 1. 2D Rigid Body Dynamics

### 1.1 State representation

Every physically simulated entity carries three components:

| Component | Fields | Role |
|---|---|---|
| `Transform` | `Vector2 Position`, `float Rotation` | World-space pose |
| `Velocity` | `Vector2 Linear`, `float Angular` | Rate of change of pose (px/s, rad/s) |
| `RigidBody` | `float Mass`, `float Inertia`, `float LinearDrag`, `float AngularDrag`, `Vector2 AccumulatedForce`, `float AccumulatedTorque` | Mass properties and per-frame force/torque accumulator |

`Inertia` is the moment of inertia about the body's centre of mass (kg·px²). It is computed once at spawn from the polygon geometry using `PolygonUtils.ComputeInertia`.

### 1.2 Integration — symplectic Euler

Two systems run in fixed order each frame:

**`PhysicsSystem`** — integrates forces and torques into velocities:
```
v.Linear  += (rb.AccumulatedForce  / rb.Mass)    × dt
v.Angular += (rb.AccumulatedTorque / rb.Inertia) × dt
v.Linear  *= (1 − rb.LinearDrag  × dt)
v.Angular *= (1 − rb.AngularDrag × dt)
rb.AccumulatedForce  = 0
rb.AccumulatedTorque = 0
```

**`MovementSystem`** — integrates velocities into position and rotation:
```
t.Position += v.Linear  × dt
t.Rotation += v.Angular × dt
```

Symplectic Euler (velocity before position) gives better energy conservation than standard Euler at the same cost, which matters for stacked objects and slow-drift simulation.

### 1.3 Applying forces and torques

**Force at the centre of mass** — no torque produced:
```csharp
rb.AccumulatedForce += force;
```

**Force at an offset point** — produces both linear and angular effect:
```csharp
rb.AccumulatedForce  += force;
rb.AccumulatedTorque += contactOffset.X * force.Y - contactOffset.Y * force.X;
// contactOffset = contactPoint − Transform.Position
```

The scalar `r × F` (2D cross product) is the torque. Positive torque → counter-clockwise acceleration in standard math convention; the engine uses Y-down screen space so the sign convention is flipped.

### 1.4 Collision impulse with rotation

The current `ApplyElasticImpulse` only handles linear velocity. The correct 2D rigid-body impulse formula includes rotational contributions from both bodies:

**Velocity at the contact point** (accounting for rotation):
```
v_contact_A = v_A.Linear + (-ω_A × r_A.Y,  ω_A × r_A.X)
v_contact_B = v_B.Linear + (-ω_B × r_B.Y,  ω_B × r_B.X)
v_rel       = v_contact_A − v_contact_B
v_rel_n     = dot(v_rel, normal)
```

Where `r_A = contactPoint − posA` and `r_B = contactPoint − posB`.

**Impulse scalar:**
```
        −(1 + e) × v_rel_n
j = ──────────────────────────────────────────────────
    1/mA + 1/mB + (rA × n)²/IA + (rB × n)²/IB
```

Where `r × n = r.X × n.Y − r.Y × n.X` (2D cross product, scalar).

**Apply to both bodies:**
```
v_A.Linear  += j × normal / mA         v_B.Linear  -= j × normal / mB
v_A.Angular += (rA × j×normal) / IA    v_B.Angular -= (rB × j×normal) / IB
```

This is the standard form from Baraff & Witkin (1997). It reduces to the current linear-only formula when `IA = IB = ∞` (no rotation).

> **Status:** Not yet implemented in `CollisionSystem`. `ApplyElasticImpulse` in the demo still uses the linear-only form. The architecture is ready (contact point is in `ContactInfo`, `Inertia` is on `RigidBody`). Planned for the next engine pass.

### 1.5 Moment of inertia

**Single convex polygon** (vertices centroid-relative):
```
I = (ρ/12) Σ |cross(vᵢ, vᵢ₊₁)| × (vᵢ·vᵢ + vᵢ·vᵢ₊₁ + vᵢ₊₁·vᵢ₊₁)
where ρ = mass / |area|
```

Implemented in `PolygonUtils.ComputeInertia`. O(N) for an N-vertex polygon.

**Compound shape** — parallel axis theorem:
```
I_compound = Σᵢ (Iᵢ_own + mᵢ × dᵢ²)
```

Where `Iᵢ_own` is each part's inertia about its own centroid, `mᵢ` its mass, and `dᵢ` the distance from that part's centroid to the compound's centre of mass. This must be recomputed whenever the compound changes (damage, fracture).

---

## 2. Fracture System — Design Goals

| Goal | Description |
|---|---|
| Energy-driven | Zone radii and cut count derived from physical energy and material properties, not normalised by asteroid size |
| Material variety | `FractureProperties` separates immutable material description from runtime state |
| Accumulated damage | Sub-threshold hits accumulate; repeated small impacts eventually fracture |
| Concave compound survivor | The surviving entity after fracture is a `CompoundShape` of convex pieces, visually concave |
| Blast zone | Innermost fragments near the impact point become visual particles |
| Rotational response | Off-centre bullet hits produce angular impulse on both the survivor and fragments |

---

## 3. Components

### 3.1 FractureProperties (immutable material)

```csharp
// Engine/Components/FractureProperties.cs
public struct FractureProperties
{
    float Brittleness;      // [0 = ductile, 1 = brittle/glass]
    float Toughness;        // energy per unit mass to begin fracturing
    int   FaultCount;       // number of pre-scored weak angles generated at spawn
    float MinFragmentArea;  // px²; pieces below this → DebrisVisual
}
```

Static presets calibrated so a standard bullet on a reference asteroid (mass ≈ 4) gives severity ≈ 1 for Rock:

| Material | Brittleness | Toughness | FaultCount | MinFragmentArea |
|---|---|---|---|---|
| Glass | 1.00 | 840 | 0 | 40 |
| Ice | 0.80 | 2 100 | 7 | 80 |
| Rock | 0.60 | 8 400 | 4 | 180 |
| Metal | 0.15 | 84 000 | 2 | 400 |

### 3.2 FractureState (runtime mutable)

```csharp
// Engine/Components/FractureState.cs
public struct FractureState
{
    float   AbsorbedEnergy; // accumulated from sub-threshold hits; reset on fracture
    float[] FaultAngles;    // generated at spawn from FaultCount; inherited by sub-fragments
}
```

Decoupled from `FractureProperties` so presets can be shared (struct) without carrying per-entity mutable state.

### 3.3 VisualMesh

```csharp
// Engine/Components/VisualMesh.cs
public struct VisualMesh
{
    Vector2[][] ConvexPieces; // centroid-relative convex polygons; gaps are visual craters
}
```

When present, the renderer draws `ConvexPieces` instead of the collision shape. Enables the visual surface to differ from the physics hitbox (e.g., showing blast craters on an asteroid that hasn't yet fully fractured). Not yet wired in the current demo.

### 3.4 CompoundShape

```csharp
// Engine/Collision/CompoundShape.cs
public sealed class CompoundShape : CollisionShape
{
    CollisionShape[]  _parts;           // all convex (PolygonShape / CircleShape)
    int               LastHitPartIndex; // updated by Intersects(); -1 if no contact
    
    ContactInfo? Intersects(...)    // fans across parts; returns deepest contact
    (min, max)   GetAABB(...)      // union of part AABBs
    void         IntersectsCircle/Polygon/AABB(...)  // double-dispatch targets
    
    CompoundShape WithoutPart(int index)  // returns new compound minus that part
    CollisionShape GetPart(int index)
}
```

A `CompoundShape` is a single rigid body — it has one `Transform`, one `Mass`, one `Inertia` (parallel axis sum). Parts do not move independently. `LastHitPartIndex` allows the fracture system to identify and extract the struck part.

**Dispatch note:** `CompoundShape` does not add a new entry to the double-dispatch table. It delegates each part through the existing `PolygonShape`/`CircleShape` dispatch path. Compound-vs-compound works naturally (A fans → calls B.IntersectsPolygon for each B part).

---

## 4. Energy Model

### 4.1 Effective impact energy

Raw bullet KE ignores normal direction and mass ratio. Transferred energy:

```
v_rel_n  = |dot(v_bullet − v_asteroid, impact_normal)|
m_reduced = m_bullet × m_asteroid / (m_bullet + m_asteroid)
E_eff     = 0.5 × m_reduced × v_rel_n²
```

For a bullet much lighter than the asteroid, `m_reduced ≈ m_bullet`.

### 4.2 Centrifugal pre-stress from rotation

A spinning asteroid accumulates centrifugal stress proportional to ω². This is modelled as additional effective energy:

```
E_spin = SpinEnergyFraction × 0.5 × Inertia × ω²
E_total = E_eff + E_spin
```

`SpinEnergyFraction` (config, default 0.35) expresses what fraction of rotational energy contributes as fracture pre-stress.

### 4.3 Threshold and accumulation

```
threshold = FractureProperties.Toughness × RigidBody.Mass
combined  = E_total + FractureState.AbsorbedEnergy
```

**Below threshold:** `FractureState.AbsorbedEnergy += E_total`. Spawn a surface-spark debris cloud. Bullet destroyed.

**At or above threshold:** Fracture proceeds. `FractureState.AbsorbedEnergy = 0`. Accumulated energy contributes to a more severe fracture.

**Bullet mass** — read from the bullet entity config (`bullet.mass`). Not a separate field in `FractureProperties`.

---

## 5. Zone Computation — Physics-Based Formulas

All zone radii are derived from absolute energy and material properties. The asteroid's size appears only as a **clamp**, not as a multiplier, so the same bullet energy produces the same absolute zone radius regardless of asteroid size.

### 5.1 Fracture zone radius

```
E_fracture          = combined − threshold              [energy available for fracturing]
toughness_per_area  = Toughness × density               [energy per unit area; density = mass / (π × r²)]
zone_area           = E_fracture / toughness_per_area
zone_radius_raw     = sqrt(zone_area / π)
zone_radius         = clamp(zone_radius_raw,
                            MinFractureZoneFraction × r_asteroid,   ← minimum (surface scratch)
                            r_asteroid)                              ← maximum (full shatter)
```

**Why this has the right behaviour:**
- Large bullet, small asteroid: `E_fracture >> material resistance` → `zone_radius_raw > r_asteroid` → clamped to full shatter
- Small cumulative hits on big asteroid: small `E_fracture`, high threshold → small zone → surface scratch
- Brittleness does NOT affect zone size — it only affects how the zone fractures

### 5.2 Blast radius

```
blast_radius = zone_radius × lerp(BlastFractionDuctile, BlastFractionBrittle, Brittleness)
blast_radius = clamp(blast_radius, BlastMin, zone_radius × 0.9)
```

Ductile: energy concentrates at impact → larger blast fraction. Brittle: energy spreads as cracks → smaller blast fraction. The `× 0.9` ensures blast is always strictly inside the fracture zone.

### 5.3 Cut counts — energy factor

A single scalar, `energyFactor ∈ [0, 1]`, captures "how intense is this impact" in a size-aware way:

```
energyFactor = clamp(zone_radius / r_asteroid, 0, 1)
```

Both cut counts scale with it, with brittleness setting the ceiling:

```
secondaryCuts = clamp(round(lerp(SecondaryCutsMin, SecondaryCutsMax, B) × energyFactor), 0, MaxSecondaryCuts)
innerCuts     = clamp(round(lerp(InnerCutsMin,     InnerCutsMax,     B) × energyFactor), 1, MaxInnerCuts)
```

At `energyFactor = 0` (minimum zone): no secondary cuts, minimum inner cuts. At `energyFactor = 1` (full shatter): maximum cuts for the material.

---

## 6. The Split Algorithm — `PolygonUtils.Split`

All inputs and outputs are in world space. Every output polygon is **convex** — this is the fundamental invariant of Sutherland-Hodgman clipping. No `ConvexDecompose` function is needed because the SH algorithm is itself the decomposition.

### 6.1 Signature

```csharp
public static SplitResult Split(
    IReadOnlyList<Vector2> polygon,
    Vector2 impactPoint,       // on the polygon boundary (use NearestPointOnBoundary)
    Vector2 impactDir,         // normalized bullet travel direction
    float[] faultAngles,       // from FractureState; bias inner cuts toward natural planes
    int  secondaryCuts,        // K-1: number of radial cuts inside the fracture zone
    int  innerCuts,            // cuts applied to the final impact zone
    float fractureZoneDepth,   // depth of the primary cut from the impact surface
    float fractureRadius,      // ≈ fractureZoneDepth; distance of secondary cut planes from impact
    float coneHalfAngle,       // half-angle of the secondary cut fan (radians)
    float blastRadius,         // centroid-distance threshold for blast zone filter
    float spreadAngle,         // π × lerp(SpreadAngleMin, SpreadAngleMax, Brittleness)
    float spinFaultAngle,      // spin-induced preferred fault direction; NaN if none
    Random rng,
    float minAreaThreshold)
```

### 6.2 SplitResult

```csharp
public readonly struct SplitResult
{
    Vector2[]?  PrimaryFarPiece;     // cut-1 far side; goes to surviving compound; null if degenerate
    Vector2[][] SecondaryFarPieces;  // cut-2..K far sides (petals); attached to surviving compound
    Vector2[][] SurvivingFragments;  // impact zone pieces: area ≥ threshold AND centroid > blastRadius
    Vector2[][] DebrisFragments;     // impact zone pieces: area < threshold OR centroid ≤ blastRadius
}
```

**All polygons are convex** — each is the direct output of one or more `ClipConvexByHalfPlane` calls. Callers pass directly to `new PolygonShape(verts)`.

### 6.3 Phase 1 — Primary cut

Separates the fracture zone (near impact side) from the surviving body (far side).

```
cutDir          = normalize(centroid → impactPoint)         [from centroid outward to impact]
primaryCutPoint = impactPoint − cutDir × fractureZoneDepth
primaryFarPiece = Clip(polygon, primaryCutPoint, −cutDir)   [far side; → surviving compound]
fractureZone    = Clip(polygon, primaryCutPoint,  cutDir)   [near side; → further processing]
```

### 6.4 Phase 2 — Secondary radial cuts (the "bite")

K-1 radial cuts are applied sequentially to `fractureZone`. Each cut is perpendicular to a ray from the impact point, placed at `≈ fractureRadius` from the impact, within a cone facing the centroid.

```
innerDir = −cutDir                         [from impact toward centroid]
for i = 0 .. secondaryCuts − 1:
    angle_i    = (i − (secondaryCuts−1)/2) × (2×coneHalfAngle / max(secondaryCuts−1, 1))
    ray_i      = rotate(innerDir, angle_i)
    planePoint = impactPoint + ray_i × fractureRadius
    petalPiece   = Clip(fractureZone, planePoint,  ray_i)   [far side → surviving compound]
    fractureZone = Clip(fractureZone, planePoint, −ray_i)   [remaining near side]
```

Each `petalPiece` is a convex "wedge" of the fracture zone. Together with `primaryFarPiece`, they form the compound shape of the surviving asteroid.

**Shape of the "bite":** The surviving compound has K straight-line faces on its impact-facing side (1 from the primary cut + K-1 from the radial cuts), approximating a concave crater.

### 6.5 Phase 3 — Impact zone fragmentation

`impactZone` = `fractureZone` after all secondary cuts (the innermost remnant).

Inner cuts pass through `impactPoint` at `innerCuts` evenly distributed angles within `spreadAngle`, biased toward `faultAngles`:

```
for each cut angle α:
    normal = rotate90(direction α)
    Clip(fragments, impactPoint,  normal) → fragment A
    Clip(fragments, impactPoint, −normal) → fragment B
```

**Blast zone filter** — after all inner cuts, each resulting fragment is classified:
```
for each fragment:
    centroid = ComputeCentroid(fragment)
    if |centroid − impactPoint| ≤ blastRadius → DebrisFragments (blast particle)
    elif |ComputeArea(fragment)| < minAreaThreshold → DebrisFragments (dust)
    else → SurvivingFragments
```

---

## 7. Surviving Compound Asteroid

### 7.1 Building the compound

After `Split` returns, `UpdateSurvivingAsteroid` collects all surviving pieces:

```
pieces = filter([PrimaryFarPiece] + SecondaryFarPieces, area ≥ MinFragmentArea)

if pieces.Count == 0: DestroyEntity(asteroid)
if pieces.Count == 1: PolygonShape → update Collider.Shape, Transform.Position, RigidBody
if pieces.Count ≥ 2: CompoundShape (see below)
```

### 7.2 Compound centre of mass and inertia

```
totalArea = Σ |ComputeArea(piece)|
centroid  = Σ (ComputeCentroid(piece) × |ComputeArea(piece)|) / totalArea
mass      = rb.Mass × (totalArea / originalArea)
```

Moment of inertia — parallel axis theorem:
```
I_compound = Σᵢ (ComputeInertia(localVertsᵢ, massᵢ)  +  massᵢ × |centroidᵢ − centroid|²)
```

### 7.3 Local vertices for each part

Each `PolygonShape` in the compound stores vertices in the entity's local space. Since the asteroid has a non-zero rotation `θ`, world verts must be un-rotated:

```
for each piece (world-space verts):
    partCentroid = ComputeCentroid(piece)
    for each vert:
        offset   = vert − compoundCentroid
        localVert = rotate(offset, −θ)        [un-rotate by entity's current rotation]
    PolygonShape(localVerts) → part of CompoundShape
```

### 7.4 Entity update (in-place — no destroy/create)

```
Transform.Position = compoundCentroid
RigidBody.Mass     = newMass
RigidBody.Inertia  = I_compound
Collider.Shape     = CompoundShape([part₁, part₂, ...])
FractureState.AbsorbedEnergy = 0
FractureState.FaultAngles    = newly generated (FaultCount--, min 0)
```

The surviving entity keeps its `Velocity` (linear and angular), `FractureProperties`, and `AsteroidData` tag unchanged.

---

## 8. Fragment Physics

### 8.1 Spawning live fragments

For each polygon in `SurvivingFragments`:

```
(centroid, localVerts) = RecenterVertices(worldVerts)
fragArea  = |ComputeArea(localVerts)|
massFrac  = fragArea / originalArea
fragMass  = parentMass × massFrac
inertia   = ComputeInertia(localVerts, fragMass)

Transform  = { Position: centroid, Rotation: 0f }   ← NOT parent rotation; see note below
RigidBody  = { Mass: fragMass, Inertia: inertia, ... }
Collider   = { Shape: new PolygonShape(localVerts), Layer: Asteroid, ... }
FractureProperties = parent FractureProperties (copied)
FractureState      = { AbsorbedEnergy: 0, FaultAngles: new (FaultCount--) }
```

**Rotation = 0f:** The world verts from SH clipping already encode the parent's rotation. Setting `Rotation = parent.Rotation` would double-rotate local verts when `PolygonShape.TransformVertices` is called. Fragments spawn with the correct world-space polygon at rotation 0, then spin via their inherited angular velocity.

### 8.2 Fragment velocity

Linear velocity:
```
r         = centroid − parentTransform.Position
rotVel    = (-parentω × r.Y, parentω × r.X)        [tangential velocity from parent spin]
spreadDir = normalize(r) or random if |r| < ε
spreadSpd = baseFrag × (SpreadNormMin + energyFactor × (1 − SpreadNormMin))
          × sqrt(MassRef / fragMass)                 [lighter fragments fly faster]
          × lerp(0.5, 1.4, Brittleness)

fragLinear = kickedLinear + rotVel + spreadDir × spreadSpd
```

Where `kickedLinear = parentLinear + bulletDir × (bulletSpd × bulletMass / parentMass × MomentumTransfer)`.

Angular velocity:
```
fragAngular = parentAngular + random(−1, 1) × lerp(0.5, 2.5, Brittleness)
```

### 8.3 Angular impulse from bullet hit

Not yet applied. When implemented:
```
r = impactPoint − parentTransform.Position
J = bulletDir × (bulletSpd × bulletMass × MomentumTransfer)
angular_imp = r.X × J.Y − r.Y × J.X
kickedAngular = parentAngular + angular_imp / parentInertia
```

This replaces the current `+ random` spin with a physically motivated one. It is additive with the random spin applied to each fragment independently.

---

## 9. Concave Shapes — Invariants

**Key property:** Every `ClipConvexByHalfPlane` of a convex polygon produces a convex polygon. All polygons in `SplitResult` are therefore convex — no additional decomposition algorithm is needed.

**Apparent concavity** is always a compound of convex pieces:
- An asteroid that has survived multiple fractures is a `CompoundShape` of convex pieces with gaps between them (where fractured zones used to be)
- A single fragment entity is always a single convex `PolygonShape`

**VisualMesh (future):** A `VisualMesh` component can store the true (potentially concave) visual polygon for rendering, while `Collider.Shape` remains the convex `CompoundShape`. The renderer queries `VisualMesh` if present, otherwise falls back to the collision shape. Not yet wired in the current demo.

---

## 10. Engine-Level Implementation Guide

### 10.1 Separation of responsibilities

| Responsibility | Layer | System/Method |
|---|---|---|
| Impact energy computation | Engine | `FractureSystem.ComputeEnergy` |
| Threshold check + accumulation | Engine | `FractureSystem.Update` |
| Zone and cut parameter derivation | Engine | `FractureSystem.ComputeParameters` |
| Polygon splitting geometry | Engine (utility) | `PolygonUtils.Split` |
| Emitting `FractureEvent` | Engine | `FractureSystem` |
| Entity wiring (spawn fragments, update survivor, spawn particles) | Game | `FractureEventHandler` |
| Bullet destruction | Game | `FractureEventHandler` |
| Wave scoring | Game | `FractureEventHandler` |

### 10.2 FractureEvent

```csharp
// Engine/Events/FractureEvent.cs
public readonly struct FractureEvent
{
    public readonly Entity       Fractured;       // the asteroid entity (may be updated in-place)
    public readonly Entity       Impactor;        // the bullet entity (already scheduled for destroy)
    public readonly SplitResult  Split;           // all geometry output
    public readonly float        Severity;        // combined / threshold; > 1
    public readonly float        EnergyFactor;    // zone_radius / r_asteroid; ∈ [0,1]
    public readonly Vector2      ImpactPoint;     // on asteroid boundary
    public readonly float        BlastRadius;
    public readonly FractureProperties Material;
}
```

### 10.3 FractureSystem (engine)

Subscribes to `CollisionEvent`. For each bullet→fracturable pair:

1. Verify both entities alive; read `FractureProperties` and `FractureState`
2. Correct impact point: `NearestPointOnBoundary(worldVerts, bulletPos)`
3. Compute `E_eff`, `E_spin`, `E_total`
4. Check threshold; accumulate or proceed
5. Compute zone/blast radius and cut counts (§5)
6. Call `PolygonUtils.Split`
7. Emit `FractureEvent` on the bus

The system does NOT modify any entities itself — it only emits the event. Entity changes are the game layer's responsibility.

### 10.4 CompoundShape fracture path

When the fractured entity already has a `CompoundShape` (it has been previously fractured):

1. Read `compound.LastHitPartIndex` immediately after the `CollisionEvent`
2. Extract the hit part: `compound.GetPart(lastHitIdx)` → cast to `PolygonShape` → `GetWorldVertices(pos, rot)`
3. Compute `E_total` and threshold using the **hit part's proportional mass**:
   ```
   hitPartArea  = |ComputeArea(hitPartWorldVerts)|
   totalArea    = Σ |ComputeArea(part)| over all compound parts
   hitPartMass  = rb.Mass × (hitPartArea / totalArea)
   threshold    = Toughness × hitPartMass
   ```
4. Call `Split` on the hit part's world verts (single convex polygon — SH invariant)
5. Remove the hit part from the compound: `compound.WithoutPart(lastHitIdx)`
6. Rebuild the surviving compound from the reduced compound + `PrimaryFarPiece` + `SecondaryFarPieces`

Fault angles pass through normally (the global `FractureState.FaultAngles`). Some angles may pass through voids from previous fractures; this is acceptable — `SelectCutAngles` jitter handles it gracefully.

### 10.5 System execution order

```
PhysicsSystem          — force + torque integration
MovementSystem         — position + rotation integration
WrapSystem             — toroidal edge wrap
CollisionSystem        — broad + narrow phase → CollisionEvent queue
EventFlushSystem       — dispatch events:
    FractureSystem     — energy model → FractureEvent
    FractureHandler    — entity wiring (spawn/update/destroy)
TimeToLiveSystem       — bullet + debris expiry
world.FlushDeferred()  — commit all deferred entity changes
```

`FractureSystem` must run inside `EventFlushSystem` (or a dedicated flush step) so that `CollisionEvent` data is available and entity liveness can be checked.

---

## 11. Configuration Reference

All values live in `config.json` under `"fracture"` and `"bullet"`.

### Energy model
| Key | Default | Meaning |
|---|---|---|
| `bullet.mass` | 0.1 | Bullet mass (also used for reduced-mass formula) |
| `spinEnergyFraction` | 0.35 | Fraction of I×ω² treated as fracture pre-stress |
| `momentumTransfer` | 0.55 | Fraction of bullet momentum transferred as linear kick |
| `massReference` | 4.0 | m_ref for spread-speed normalisation |

### Zone geometry
| Key | Default | Meaning |
|---|---|---|
| `minFractureZoneFraction` | 0.05 | Minimum zone as fraction of r_asteroid |
| `blastFractionDuctile` | 0.40 | blast/zone ratio for ductile materials |
| `blastFractionBrittle` | 0.10 | blast/zone ratio for brittle materials |
| `blastMin` | 2.0 | Absolute minimum blast radius (px) |
| `blastMax` | 60.0 | Absolute maximum blast radius (px) |
| `coneHalfAngleDeg` | 90.0 | Half-angle of the secondary cut fan |

### Cut counts
| Key | Default | Meaning |
|---|---|---|
| `secondaryCutsMin` | 0 | K-1 at Brittleness=0 |
| `secondaryCutsMax` | 5 | K-1 at Brittleness=1 |
| `maxSecondaryCuts` | 7 | Hard cap |
| `innerCutsMin` | 1 | Inner cuts at Brittleness=0 |
| `innerCutsMax` | 7 | Inner cuts at Brittleness=1 |
| `maxInnerCuts` | 10 | Hard cap |
| `spreadAngleMin` | 0.3 | lerp lower bound for π × lerp(..., B) |
| `spreadAngleMax` | 1.0 | lerp upper bound |

### Material presets
Defined under `"fracture.materials"` as a dictionary keyed by name. Each entry specifies `brittleness`, `toughness`, `faultCount`, `minFragmentArea`. Wave-to-material mapping is in `"fracture.waveMaterials"` (array of material names, last entry repeats).

---

## 12. Planned Extensions

### 12.1 Full rotational collision response
Move impulse resolution (including angular) into `CollisionSystem` so all entities benefit automatically. Requires `RigidBody.Inertia` (already present). The formula is in §1.4.

### 12.2 Angular impulse from bullet hits
Apply `r × J / I` to the surviving asteroid and distribute the torque to fragments based on their centroid offsets from the asteroid's CoM (§8.3).

### 12.3 VisualMesh concave rendering
Populate `VisualMesh.ConvexPieces` during `UpdateSurvivingAsteroid` with the world-space pieces, stored as centroid-relative local verts. The renderer draws all pieces; gaps between them show craters. The collision shape remains the `CompoundShape` (convex parts, correct physics).

### 12.4 Sub-threshold visual scarring
On sub-threshold hits: in addition to accumulating `AbsorbedEnergy`, carve the blast zone from the entity's `VisualMesh` using `ClipConvexByHalfPlane`. The entity's physics shape is unchanged; only the visual surface shows the accumulating damage. When the entity finally fractures, the `VisualMesh` pieces become the starting geometry for `Split`.

### 12.5 CompoundShape progressive damage
As a compound asteroid accumulates hits from different directions, more parts are fractured and replaced. The surviving compound shrinks with each hit. When `PartCount == 0`, the entity is destroyed. Enables an asteroid that chips away progressively rather than all-or-nothing fracture.
