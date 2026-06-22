# Fracture System Spec

**Scope:** Geometry, physics, and tuning for material-dependent polygon fracturing.  
**Depends on:** `PolygonUtils`, `PolygonShape`, `RigidBody`, `CollisionEvent`, ECS entity lifecycle.  
**Does not cover:** Audio, wave/score logic.

---

## 1. Goals

| Goal | Notes |
|---|---|
| Material-dependent fracture shape | Brittle → uniform shards; ductile → local damage, far side survives |
| Impact-energy dependence | KE, mass ratio, impact angle all drive severity |
| Angular-velocity dependence | Spinning bodies have centrifugal pre-stress; fracture more easily |
| Fracture threshold + accumulation | Low-energy hits accumulate; repeated small hits eventually shatter |
| Micro-debris | Sub-threshold fragments spawn as fading visual-only entities |
| Extensible presets | Glass / Rock / Metal / Ice tunable via one struct |

---

## 2. FractureProperties — Data Model

```csharp
struct FractureProperties
{
    /// <summary>
    /// [0 = fully ductile, 1 = fully brittle/glass-like]
    ///
    /// Drives three linked decisions:
    ///   • numCuts  — brittle gets more cuts
    ///   • spreadAngle — brittle distributes cuts across the whole body;
    ///                   ductile concentrates cuts near the impact, leaving the
    ///                   far side intact as one large piece
    ///   • blastRadius — ductile materials deform/compress more at the crater
    /// </summary>
    float Brittleness;

    /// <summary>
    /// Minimum effective impact energy per unit mass needed to cause any fracture.
    /// Below this threshold the hit is absorbed elastically.
    /// Using mass (from RigidBody.Mass) rather than area means two objects of the
    /// same size but different densities correctly require different energies to fracture.
    /// Units: energy / mass-unit  (dimensionless in-engine; tune empirically).
    /// </summary>
    float Toughness;

    /// <summary>
    /// Number of pre-scored fault angles baked into this entity at spawn time.
    /// More faults → fracture lines follow natural weaknesses in the material.
    /// Glass: 0 (no preferred direction). Rock: 3–5. Ice: 6–8 (cleavage planes).
    /// </summary>
    int FaultCount;

    /// <summary>
    /// Minimum fragment area (px²) below which a polygon piece is treated as
    /// micro-debris (visual-only, fades out) rather than a live collidable fragment.
    /// Brittle materials produce fine debris; ductile materials produce fewer but larger pieces.
    /// </summary>
    float MinFragmentArea;

}
```

### Preset values

| Material | Brittleness | Toughness | FaultCount | MinFragmentArea |
|---|---|---|---|---|
| Glass | 1.00 |    840 | 0 |  40 |
| Ice   | 0.80 |  2 100 | 7 |  80 |
| Rock  | 0.60 |  8 400 | 4 | 180 |
| Metal | 0.15 | 84 000 | 2 | 400 |

> **Toughness calibration:** standard bullet KE ≈ 33 600, reference asteroid mass ≈ 4.
> Rock: threshold = 8 400 × 4 = 33 600 → severity = 1.0 (just fractures on a direct hit).
> Glass: threshold = 840 × 4 = 3 360 → severity ≈ 10 on a standard hit.
> Metal: threshold = 84 000 × 4 = 336 000 → standard bullet gives severity ≈ 0.1 (no fracture; accumulates).

---

## 2b. FractureState — Runtime Component

Mutable per-entity runtime state, kept separate from the immutable `FractureProperties` so presets can be static values shared across entities.

```csharp
// Engine/Components/FractureState.cs
public struct FractureState
{
    /// <summary>
    /// Accumulated impact energy from sub-threshold hits.
    /// Added to the current hit before the threshold check; reset to 0 on fracture.
    /// </summary>
    public float AbsorbedEnergy;

    /// <summary>
    /// Pre-scored fault angles (radians) generated at spawn from FractureProperties.FaultCount.
    /// Cut planes snap toward these angles during Split. Copied with FaultCount-- to sub-fragments.
    /// </summary>
    public float[] FaultAngles;
}
```

---

## 3. Impact Energy Model

### 3.1 Effective kinetic energy

Raw bullet KE ignores the direction of impact and the mass of the target. The transferred energy is:

```
v_rel_n   = dot(v_bullet − v_asteroid, impact_normal)   // normal relative speed
m_reduced = (m_bullet × m_asteroid) / (m_bullet + m_asteroid)
E_eff     = 0.5 × m_reduced × v_rel_n²
```

For the current demo bullet (m=0.1) vs a medium asteroid (m≈4): m_reduced ≈ 0.097, so E_eff ≈ 0.97 × raw bullet KE. The correction is small but keeps the formula physically grounded for future scenarios (heavy projectiles, ramming).

### 3.2 Centrifugal pre-stress from rotation

A spinning polygon accumulates centrifugal stress proportional to ω². This stress lowers the effective fracture threshold:

```
E_spin_equiv = 0.5 × Inertia × ω²          // rotational KE of the asteroid
E_total      = E_eff + SpinContribution × E_spin_equiv
```

`SpinContribution` is a tuning constant ∈ [0, 1] (default 0.35). It expresses what fraction of the rotational energy makes the body more susceptible to fracturing. High-spin asteroids are meaningfully easier to split.

Angular velocity also biases fault angles: the most likely fracture plane in a spinning body is **perpendicular to the current velocity of the impact point in the body's local frame**. After computing the cut directions (§5), the fault-angle snapping step should also consider this "spin-induced fault" direction.

### 3.3 Impact angle

Glancing hits deliver less normal-direction energy. `v_rel_n` already handles this because it projects onto the contact normal. No separate angle term is needed.

---

## 4. Fracture Decision and Severity

### 4.1 Threshold check

```
mass       = RigidBody.Mass                          // already stored on the entity
threshold  = FractureProperties.Toughness × mass
combined   = E_total + fs.AbsorbedEnergy             // fs = FractureState component
```

**Branch A — fracture:** `combined >= threshold`  
&emsp;Proceed with fracture. Reset `fs.AbsorbedEnergy = 0`.

**Branch B — no fracture:** `combined < threshold`  
&emsp;Apply elastic impulse. Accumulate: `fs.AbsorbedEnergy += E_total`.  
&emsp;Spawn a small debris cloud for visual feedback of the hit (see §9.4).

### 4.2 Severity and normalised severity

```
severity  = combined / threshold        // ≥ 1 when fracture occurs
sev_norm  = 1f - 1f / severity          // maps [1, ∞) → [0, 1)
```

`sev_norm` is the single scalar used by all downstream geometry and physics formulas. It saturates smoothly: `sev_norm(1)=0`, `sev_norm(2)=0.5`, `sev_norm(5)=0.8`, `sev_norm(10)=0.9`. This keeps all outputs in bounded ranges regardless of how large severity grows with accumulated hits.

---

## 5. Fracture Geometry Model

The model has three phases. Phase 1 defines the fracture zone by carving off near-face shards with boundary cuts, leaving the far piece intact. Phase 2 fragments the near-face shards with inner cuts. Phase 3 handles blast-zone material.

### 5.1 Fracture zone radius

The zone radius controls how deeply the fracture penetrates the asteroid. It must be bounded by the asteroid's own size.

```
// area computed once, only for the geometric radius.
area               = |ComputeArea(worldVerts)|
R_asteroid         = sqrt(area / π)
fractureZoneRadius = R_asteroid
                   × lerp(0.15f, 1.0f, sev_norm)
                   × lerp(0.25f, 1.0f, Brittleness)
```

`area` is only computed in this one place. The fracture threshold uses `RigidBody.Mass`.

| Brittleness | Severity 2 | Severity 10 | Severity 50 |
|---|---|---|---|
| 0.0 (ductile) | 0.19 R | 0.30 R | 0.45 R |
| 0.5 (rock)    | 0.32 R | 0.51 R | 0.76 R |
| 1.0 (glass)   | 0.44 R | 0.70 R | capped at R |

At minimum severity (just crossing the threshold), even glass only fractures a small near-zone. At extreme severity, glass zones extend to the full asteroid radius (total shatter); ductile material still keeps a large intact far piece.

Because `severity` incorporates `E_total` (which includes impact KE, mass ratio, and centrifugal pre-stress from angular velocity), all those factors drive the zone radius indirectly through the single scalar.

### 5.2 Phase 1 — Boundary cuts (fracture zone definition)

`K` boundary planes are applied to carve the near face into shards while preserving the far piece.

**Number of boundary cuts:**
```
K = clamp(round(lerp(2f, 5f, Brittleness) + sev_norm), 2, 6)
```

**Placement:** boundary planes are spaced angularly across the impact hemisphere — the arc facing the impact direction. They are NOT placed on the far side. Each boundary plane:
- **Plane point:** `impactPoint + impactDir × fractureZoneRadius / cos(angle_i)`  
  where `angle_i = (i − (K−1)/2) × hemisphereSector / (K−1)` fans across ±60° of the impact direction.
- **Plane normal:** points toward `impactPoint` from the plane point (inward, toward impact).

**Sequential application:** Apply boundary cuts one at a time to the residual polygon. For each cut:
- `nearShard = ClipConvexByHalfPlane(residual, planePoint, inwardNormal)` — the near chunk
- `residual  = ClipConvexByHalfPlane(residual, planePoint, outwardNormal)` — the remainder
- Boundary shards with area ≥ MinFragmentArea are retained for Phase 2. Smaller ones go to debris.

After all K cuts, `residual` is the far piece — convex, with K flat edges giving it an irregular near boundary.

**Why this achieves the desired behavior:** the boundary planes only cut across the near face. No plane touches the far side of the asteroid. The far piece is geometrically guaranteed intact regardless of K or brittleness. The "jaggedness" of the far piece's near boundary scales with K (and therefore brittleness/severity).

### 5.3 Phase 2 — Inner cuts (fragmentation of near shards)

Each boundary shard from Phase 1 is independently fragmented by inner cuts. This produces the actual debris.

**Inner cuts per shard:**
```
innerCuts = clamp(round(lerp(1f, 6f, Brittleness) × (0.4f + sev_norm × 0.6f)), 1, 6)
```

Inner cuts pass through the shard's local centroid (not the original impact point). Their angular distribution follows the fault-angle snapping logic from §6 but scoped to the shard's local geometry. `spreadAngle` is still used here to control how uniformly the shard is cut — at high brittleness, the full π fan; at low, a narrow cone. Within a single shard, `spreadAngle` meaningfully changes the shape of the resulting fragments (parallel strips vs. wedges).

**Result:** each of the K boundary shards produces `innerCuts + 1` sub-fragments (minus any discarded as too small). Total surviving fragments ≈ `K × innerCuts` plus 1 far piece.

| Brittleness | K | innerCuts | Approx. total pieces |
|---|---|---|---|
| 0.0 (ductile, sev=5) | 2 | 1 | 3 |
| 0.5 (rock, sev=10)   | 3 | 3 | 10 |
| 1.0 (glass, sev=20)  | 5 | 5 | 26 |

### 5.4 Blast (crater) radius

Material between two offset planes at the impact point is removed — this becomes blast particles (§8.5), not a geometric void.

```
blastRadius = clamp(
    K_blast × R_asteroid × sev_norm × lerp(1.8f, 0.6f, Brittleness),
    4f, 60f
)
```

`K_blast ≈ 0.4`. Ductile materials deform more → larger crater. Brittle materials shatter cleanly → smaller crater.
Rock (B=0.6), sev_norm=0.75, R=55 px: 0.4 × 55 × 0.75 × 1.08 ≈ 18 px.

### 5.5 Concave shapes — the SH decomposition insight

A key property of Sutherland-Hodgman clipping is that **every half-plane clip of a convex polygon produces a convex polygon**. This means the fracture process is already a convex decomposition — no separate algorithm is needed. Every fragment produced by Phase 1 boundary cuts and Phase 2 inner cuts is, by construction, convex.

Apparent concavity arises only at the entity level, never at the individual piece level:

- An **asteroid that has absorbed sub-threshold damage** still exists as one entity, but its visual surface has blast craters carved out of it. That entity's visual representation is a *collection of convex pieces* (what remains after the SH carving), not a single concave polygon. The physics collision shape is unchanged (the original convex polygon).
- A **fully-fractured asteroid** is replaced by individual fragment entities, each carrying one convex SH-output piece as both its collision shape and its visual shape.

The concavity therefore always decomposes into convex pieces through the same SH operations — no additional algorithm required.

**Representation for a scarred (partially-damaged) asteroid:**

```csharp
// Engine/Components/VisualMesh.cs
struct VisualMesh
{
    // Ordered list of centroid-relative convex pieces that compose the
    // visual surface. Gaps between pieces (from carved blast zones) are
    // rendered as-is, giving the scarred appearance.
    // For an undamaged entity this holds a single entry == the collision verts.
    public Vector2[][] ConvexPieces;
}
```

The collision `Collider.Shape` remains the **original** `PolygonShape` for an undamaged/partially-damaged entity — carving visual pieces does not alter the physics hitbox. When full fracture occurs, each `ConvexPieces[i]` becomes the collision shape for a new fragment entity (`PolygonShape` directly, since each piece is convex).

---

## 6. CompoundShape — Collision Extension

`CompoundShape` enables an entity to have multiple convex collision shapes. This is the general mechanism for concave-capable collision without replacing SAT.

### 6.1 Architecture

```csharp
// Engine/Collision/CompoundShape.cs
public sealed class CompoundShape : CollisionShape
{
    private readonly CollisionShape[] _parts;   // all convex (PolygonShape / CircleShape)

    public int PartCount => _parts.Length;

    /// <summary>
    /// Index of the part that produced the deepest contact in the most recent
    /// Intersects() call. -1 if no contact was found. Valid until the next call.
    /// Safe to read in single-threaded event dispatch; do not use across frames.
    /// </summary>
    public int LastHitPartIndex { get; private set; } = -1;

    public CompoundShape(CollisionShape[] parts) { _parts = parts; }

    public CollisionShape GetPart(int index) => _parts[index];

    /// <summary>Returns a new CompoundShape with the part at index removed.</summary>
    public CompoundShape WithoutPart(int index)
    {
        var remaining = _parts.Where((_, i) => i != index).ToArray();
        return new CompoundShape(remaining);
    }

    public override ContactInfo? Intersects(
        Vector2 posA, float rotA, CollisionShape other, Vector2 posB, float rotB)
    {
        ContactInfo? deepest = null;
        LastHitPartIndex = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            var c = _parts[i].Intersects(posA, rotA, other, posB, rotB);
            if (c != null && (deepest == null || c.Depth > deepest.Value.Depth))
            {
                deepest = c;
                LastHitPartIndex = i;
            }
        }
        return deepest;
    }

    public override (Vector2 min, Vector2 max) GetAABB(Vector2 pos, float rot)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var part in _parts)
        {
            var (pmin, pmax) = part.GetAABB(pos, rot);
            min = Vector2.Min(min, pmin);
            max = Vector2.Max(max, pmax);
        }
        return (min, max);
    }
}
```

`CompoundShape` does not add a new entry to the double-dispatch table. It delegates to each part, and each part dispatches normally through `IntersectsPolygon` / `IntersectsCircle`. The existing `PolygonShape`, `CircleShape`, and `AABBShape` require no changes.

### 6.2 Compound-vs-compound

When both shapes are `CompoundShape` the call tree is O(M × N) over their respective part counts. For the fragments this system produces, M and N are at most 4–6, so at most ~36 SAT tests per pair — negligible.

### 6.3 AABB broad phase

`CompoundShape.GetAABB()` returns the union of all part AABBs. The spatial grid uses `GetAABB` for bucketing, so no changes to `SpatialGrid` or `CollisionSystem` are needed.

### 6.4 When each type is used

| Entity state | Collision shape | VisualMesh |
|---|---|---|
| Fresh asteroid (undamaged) | `PolygonShape` (original convex polygon) | single piece = collision verts |
| Scarred asteroid (absorbed damage, ≥2 remaining parts) | `CompoundShape([remaining parts])` | same pieces as the compound parts |
| Scarred asteroid (1 part remaining) | `PolygonShape` (the single remaining part) | same as collision shape |
| Fully-fractured fragment | `PolygonShape` (the fragment's convex SH output) | same as collision shape |
| Multi-part game object (future) | `CompoundShape([part1, part2, ...])` | pieces match the compound parts |

### 6.5 Fracture interaction with CompoundShape

When a `CollisionEvent` involves an entity whose `Collider.Shape` is a `CompoundShape`, the `FractureSystem` uses `LastHitPartIndex` to identify and extract the specific convex part that was struck. `Split` is then called on that part alone — it remains a single convex polygon, so the algorithm is unchanged.

After fracture:

1. Retrieve the hit part vertices: `compound.GetPart(compound.LastHitPartIndex)` cast to `PolygonShape`, then `GetLocalVertices()` transformed to world space.
2. Remove the hit part: `var reduced = compound.WithoutPart(compound.LastHitPartIndex)`.
3. **If** `reduced.PartCount >= 2`: update the entity's collider to `reduced`. Update `VisualMesh.ConvexPieces` to match.
4. **If** `reduced.PartCount == 1`: downgrade to `PolygonShape(reduced.GetPart(0))` — no compound overhead.
5. **If** `reduced.PartCount == 0`: destroy the entity (all material has been fractured away).
6. Spawn fragment entities from `SplitResult` as normal.

**Part mass for fracture calculations:**
The hit part's proportional mass drives the threshold and spread speed rather than the full entity mass:

```
hitPartArea   = |ComputeArea(hitPartWorldVerts)|
totalArea     = sum of |ComputeArea| over all current compound parts
hitPartMass   = rb.Mass × (hitPartArea / totalArea)
```

`hitPartMass` is substituted for `rb.Mass` everywhere in §4 and §8.1 for this fracture event.

---

## 7. Algorithm: SelectCutAngles (revised for inner cuts)

This function is now called once per boundary shard (Phase 2), not once for the whole polygon.

**Signature:**
```csharp
private static float[] SelectCutAngles(
    IReadOnlyList<Vector2> shard,      // the individual near-face shard, not the full asteroid
    Vector2 shardCentroid,             // cut planes pass through this point
    float[] faultAngles,
    int count,
    float spreadAngle,                 // π × lerp(0.3, 1.0, Brittleness) — for inner cuts
    float spinFaultAngle,              // spin-induced fault direction, or NaN
    Random rng)
```

**Algorithm (unchanged in structure):**

1. `baseAngle` = direction from shard centroid toward the original impact point + π/2.
2. `slotWidth = spreadAngle / count`.
3. For slot `i`: `slotCenter = baseAngle + (i − (count−1)/2) × slotWidth`.
4. Snap to nearest fault angle within ±halfSlot (check `faultAngles` and `spinFaultAngle`).
5. Add jitter ±0.2 rad.

---

## 8. Fragment Physics (extensions)

### 8.1 Spread speed — material and mass dependent

Brittle fragments fly apart faster (stored elastic energy releases suddenly). Ductile fragments separate slowly. Heavier asteroids produce slower-moving fragments for the same severity — the same energy distributed across more mass yields lower eject speeds.

```
spreadSpeed = base
            × (0.3f + sev_norm × 0.7f)
            × sqrt(m_reference / partMass)
            × lerp(0.5f, 1.4f, Brittleness)
```

`m_reference = 4f` (mass of the reference asteroid). `partMass` is the hit part's proportional mass (§6.5). A small shard (mass ≈ 0.5) flies ≈ 2.8× faster than a full asteroid (mass ≈ 4) at the same `sev_norm`.

### 8.2 Fragment spin

Brittle fragments inherit more chaotic spin (they tumble). Ductile fragments retain more of the parent's angular momentum.

```
fragAngular = av.Angular
            + random(−1, 1) × lerp(0.5, 2.5, Brittleness)
```

### 8.3 Sub-fragment faultCount inheritance

Fragments can be fractured again. Their `FractureProperties` should be copied from the parent, but `FaultCount` reduced by 1 each generation (minimum 0). At FaultCount=0 the fragment can still be split by impact energy alone, but has no preferred fault planes.

---

## 9. Micro-Debris System

Fragments whose area falls below `FractureProperties.MinFragmentArea` are not silently discarded — they become **micro-debris**: collidable-free visual entities that drift outward, spin, and fade out over a short lifetime.

### 9.1 DebrisVisual component

```csharp
struct DebrisVisual
{
    public float    MaxTTL;       // initial lifetime (same value as TimeToLive.Remaining at spawn)
    public Vector2[] LocalVerts;  // centroid-relative polygon vertices for rendering
}
```

No `Collider` is added. The entity participates in `MovementSystem` (it drifts), `TimeToLiveSystem` (it is destroyed when TTL expires), and the renderer (it is drawn with fading alpha).

### 9.2 Spawn parameters

Debris pieces are the actual small polygon fragments produced by `PolygonUtils.Split`; they just skip the full physics/collider setup.

```
ttl          = lerp(0.4, 1.2, clamp(area / MinFragmentArea, 0, 1))
               // tinier pieces disappear faster

spreadSpeed  = (baseFrag × 1.6) + random × 40    // slightly faster than real fragments

fragAngular  = av.Angular + random(−1,1) × lerp(1.5, 4.0, Brittleness)
               // brittle debris tumbles chaotically
```

Linear drag is set high (≈ 1.5) so debris decelerates visibly — it doesn't fly across the screen.

### 9.3 Renderer: fade by remaining TTL

```csharp
world.ForEach<Transform, DebrisVisual, TimeToLive>((Entity _, ref Transform t,
    ref DebrisVisual dv, ref TimeToLive ttl) =>
{
    float progress = 1f - ttl.Remaining / dv.MaxTTL;  // 0 at spawn → 1 at death
    byte  alpha    = (byte)(int)(220f * (1f - progress));
    DrawDebrisPoly(canvas, dv.LocalVerts, t.Position, t.Rotation, alpha);
});
```

The polygon is drawn with the same rock fill/stroke as a real asteroid fragment but at the computed alpha. No glow is drawn (too small to merit it).

### 9.4 Debris cloud from sub-threshold hits

When a hit is absorbed without fracture (§4.1 Branch B), call `SpawnDebrisCloud` to give visible feedback:

```csharp
void SpawnDebrisCloud(Vector2 origin, float radius, Vector2 impactDir, int count)
```

Each cloud particle is a tiny 3–4 vertex polygon (area ≈ 30–80 px²), velocity = `impactDir × random(60, 140) + perpendicular × random(−50, 50)`, TTL = 0.3–0.6s. This confirms the hit landed without breaking the target.

### 9.5 Blast zone particles

The material stripped from the blast zone (the thin polygon between the two offset planes) is not silently discarded — it becomes the most energetic debris in the fracture.

The blast strip polygon is captured before the geometry discards it. It is then sampled into `N_blast = clamp(round(blastRadius * 0.8), 3, 14)` tiny particles:

- **Geometry:** each particle is a 3-vertex sliver clipped from the strip, or a degenerate point if the strip is too thin
- **Velocity:** `impactDir × random(200, 400) + perpendicular × random(−120, 120)` — much faster than fragment debris, outward from the impact point
- **TTL:** 0.15–0.35s — they appear as a bright flash and disappear quickly
- **Visual:** rendered as bright points or tiny polygons (`DebrisVisual` with `IsBlastParticle = true`); the renderer gives them a distinct hot colour (yellow-white) separate from fragment debris
- **No collider**, high linear drag (≈ 3.0) so they decelerate to a stop within their lifetime

```csharp
struct DebrisVisual
{
    public float     MaxTTL;
    public Vector2[] LocalVerts;
    public bool      IsBlastParticle;   // NEW — drives colour in renderer
}
```

Blast particles require no changes to `PolygonUtils.Split` — the caller captures the strip geometry before discarding it by running an extra `ClipConvexByHalfPlane` pass to isolate the strip region.

---

## 10. Changes to PolygonUtils.Split

**New signature:**
```csharp
public static SplitResult Split(
    IReadOnlyList<Vector2> polygon,
    Vector2 impactPoint,
    Vector2 impactDir,           // NEW — normalized bullet direction
    float[] faultAngles,
    int innerCutsPerShard,       // replaces flat numCuts
    int boundaryCuts,            // NEW — Phase 1 boundary count
    float fractureZoneRadius,    // NEW — replaces spreadAngle as the zone limiter
    float spreadAngle,           // still used for inner cuts within each shard
    float spinFaultAngle,
    Random rng,
    float minAreaThreshold = 50f,
    float blastRadius = 0f)
```

**Return type:**
```csharp
struct SplitResult
{
    // ALL polygon arrays below are guaranteed convex — each is the direct output
    // of one or more ClipConvexByHalfPlane calls on a convex input.
    // No ConvexDecompose step is needed; the SH process IS the decomposition.

    public Vector2[]   FarPiece;            // intact far polygon (convex; null if asteroid fully inside zone)
    public Vector2[][] SurvivingFragments;  // area ≥ minAreaThreshold; each takes a PolygonShape directly
    public Vector2[][] DebrisFragments;     // area < minAreaThreshold; spawn as DebrisVisual
    public Vector2[]   BlastStrip;          // the vaporised strip polygon (convex); caller samples particles
}
```

**Internal structure:**
1. Phase 1: K boundary-plane clips → `FarPiece` (convex) + K `nearShards` (each convex)
2. Phase 2: For each `nearShard` → `innerCutsPerShard` SH clips → classify by area into `SurvivingFragments` / `DebrisFragments` (all convex)
3. Phase 3: Extract `BlastStrip` by clipping the relevant shard(s) by both blast offset planes (convex strip)

**The convex invariant:** because every input to `ClipConvexByHalfPlane` is convex, and SH clipping preserves convexity, every polygon in `SplitResult` is convex. Callers assign `PolygonShape(verts)` directly — no hull computation or decomposition required.

The `ClipConvexByHalfPlane` primitive is unchanged. All new behaviour comes from how it is orchestrated.

> **Future extension — concave inputs:** when a pre-scarred asteroid (represented as `VisualMesh.ConvexPieces`) is fractured, `Split` must accept a list of convex pieces rather than a single polygon. Each piece is clipped independently; the results are merged into the same `SplitResult` structure. This is the subject of the next design section (§14).

---

## 11. Changes to FragmentAsteroid (caller)

```csharp
// 1. Read FractureProperties (fall back to Rock if component absent).
var fp = world.HasComponent<FractureProperties>(asteroid)
       ? world.GetComponent<FractureProperties>(asteroid)
       : FractureProperties.Rock;

// 2. Effective energy.
float vRelN   = Vector2.Dot(bulletVel.Linear - av.Linear, impactNormal);
float mReduced = (BulletMass * rb.Mass) / (BulletMass + rb.Mass);
float eff = 0.5f * mReduced * vRelN * vRelN;
float spin = 0.35f * 0.5f * rb.Inertia * av.Angular * av.Angular;
float eTotal = eff + spin;

// 3. Fracture threshold with accumulation.
// Mass comes from RigidBody.Mass — no area computation needed.
float threshold = fp.Toughness * rb.Mass;
float combined  = eTotal + fp.AbsorbedEnergy;

if (combined < threshold)
{
    // Sub-threshold hit: accumulate damage and spawn micro-debris at impact.
    ref var fpRef = ref _world.GetComponent<FractureProperties>(asteroid);
    fpRef.AbsorbedEnergy += eTotal;
    ApplyElasticImpulse(...);
    SpawnDebrisCloud(impactPoint, blastRadius: 8f, bulletDir, count: 4);
    return;
}

// Past threshold: use combined energy, reset accumulation.
ref var fpMut = ref _world.GetComponent<FractureProperties>(asteroid);
fpMut.AbsorbedEnergy = 0f;

// 4. Severity and derived parameters.
float severity   = combined / threshold;
int   numCuts    = (int)MathF.Max(1f, MathF.Floor(
                       MathF.Pow(severity, 0.55f) * MathF.Lerp(1.8f, 9.0f, fp.Brittleness)));
numCuts = Math.Clamp(numCuts, 1, 12);

float spreadAngle = MathF.PI * MathF.Lerp(0.22f, 1.0f, fp.Brittleness);
float blastRadius = Math.Clamp(
    0.55f * MathF.Pow(severity, 0.4f) * MathF.Lerp(1.8f, 0.6f, fp.Brittleness),
    4f, 60f);

// 5. Spin-induced fault direction.
Vector2 impactVelInBody = new(-av.Angular * (impactPoint - at.Position).Y,
                               av.Angular * (impactPoint - at.Position).X);
float spinFaultAngle = impactVelInBody.LengthSquared() > 1f
    ? MathF.Atan2(impactVelInBody.Y, impactVelInBody.X) + MathF.PI / 2f
    : float.NaN;

// 6. Split.
var fragments = PolygonUtils.Split(
    worldVerts, impactPoint, aData.FaultAngles,
    numCuts, spreadAngle, spinFaultAngle, _rng,
    minAreaThreshold: fp.MinFragmentArea, blastRadius: blastRadius);
```

---

## 12. Implementation Plan

| Phase | Scope | Files touched |
|---|---|---|
| 1 | Add `FractureProperties` (with `AbsorbedEnergy`). Update `FragmentAsteroid`: new energy model, accumulation, severity-derived `fractureZoneRadius`/`blastRadius`/`boundaryCuts`/`innerCutsPerShard`. Keep old flat-cut geometry for now (placeholder). | `Program.cs` |
| 2 | Implement two-phase `Split` in `PolygonUtils`: boundary cuts (Phase 1) + inner cuts per shard (Phase 2). Return `SplitResult`. Validate far-piece survival for B=0; uniform shatter for B=1. | `PolygonUtils.cs` |
| 3 | Add `DebrisVisual` (with `IsBlastParticle`). Spawn debris and blast particles from `SplitResult`. `SpawnDebrisCloud` for sub-threshold hits. Renderer fade pass for debris; hot-colour pass for blast particles. | `Program.cs` |
| 4 | Add `VisualMesh` component and `CompoundShape`. For scarred asteroids: populate `VisualMesh.ConvexPieces` from the SH-carved blast strip geometry; physics shape unchanged. Add renderer pass that draws all `ConvexPieces` in place of the collision verts. | `Program.cs`, `Engine/Components/`, `Engine/Collision/` |
| 5 | Wire `FractureProperties` presets onto asteroids in `SpawnAsteroid`; vary by wave (Rock → Ice → Glass). | `Program.cs` |
| 6 | Tune constants (K_blast, boundary fan angle ±60°, spread lerp bounds, blast particle velocity/TTL). Validate: glancing shots bounce off Metal; repeated hits accumulate and shatter. | tuning pass |

---

## 13. Resolved Questions

1. **Sub-fragment re-fracturing:** Fragments copy parent `FractureProperties` with `FaultCount` decremented by 1 (min 0) and `AbsorbedEnergy` reset to 0.

2. **Toughness scaling:** `threshold = Toughness × mass` (using `RigidBody.Mass`). Equivalent to `× area` at constant density; generalises correctly when density varies. Linear scaling retained.

3. **Impact normal sign convention:** Verify during Phase 1 by checking that `vRelN > 0` on a direct hit. If negative, negate `impactNormal` before use. The SAT normal convention (`B→A`) means for bullet(A) vs asteroid(B) the normal points from asteroid toward bullet — so `vRelN = dot(v_bullet - v_asteroid, -normal)`.

4. **Multiple simultaneous impacts:** Deferred. The `IsAlive` guard handles the second-hit safely.

---

## 14. Engine Changes Summary

| Item | Location | Notes |
|---|---|---|
| `FractureProperties` | `Engine/Components/` | Material data + `AbsorbedEnergy` runtime state |
| `VisualMesh` | `Engine/Components/` | `Vector2[][] ConvexPieces`; renderer uses over collision verts |
| `CompoundShape` | `Engine/Collision/` | Delegates `Intersects` + `GetAABB` to convex parts |
| `FractureEvent` | `Engine/Events/` | Carries `SplitResult`; game handles entity wiring |
| `FractureSystem` | `Engine/Systems/` | Subscribes to `CollisionEvent`; runs energy model + `Split`; emits `FractureEvent` |
| `PolygonUtils.Split` | `Engine/Collision/` | Returns `SplitResult`; all output polygons convex by invariant |

No changes to `PolygonShape`, `SAT`, `SpatialGrid`, or `CollisionSystem`.

---

## 15. Split Algorithm for Pre-Scarred (Compound) Inputs

> **Status: resolved.**

Since `CompoundShape` is a set of convex parts and collision detection runs per-part (§6), the impacted part is always known at fracture time via `LastHitPartIndex`. `Split` is called only on that specific part, which is convex by construction. The algorithm is unchanged — it always operates on a single convex polygon.

**Resolution of the four design questions:**

1. **Input representation:** `Split` signature is unchanged — always a single convex polygon. The caller extracts the hit part from the `CompoundShape` before calling `Split`.

2. **Boundary cut phase:** no change. The hit part is convex, so the standard two-phase boundary + inner cut algorithm applies directly. The `FarPiece` output is one convex polygon; the remaining `CompoundShape` parts are unaffected by the cuts.

3. **Area / mass accounting:** use `hitPartMass` (proportional to the hit part's area within the compound, see §6.5) rather than the full entity mass. `R_asteroid` for the fracture zone radius is computed from the hit part's area, not the full asteroid area. This is geometrically correct — the fracture zone is bounded by the piece being fractured, not the original body.

4. **Fault angle relevance:** fault angles from `FractureProperties` are retained unchanged. Angles that happen to pass through previously-carved voids will either snap to a nearby part boundary (useful) or fall back to the slot center with jitter (harmless). No revalidation needed.
