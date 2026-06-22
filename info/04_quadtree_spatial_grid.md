# Broad-Phase Collision: Quadtree vs Spatial Grid

---

## The Problem They Both Solve

Before testing two shapes for actual overlap (narrow phase), you need to quickly eliminate pairs that are obviously too far apart to collide. Without this, you check every entity against every other: O(n²) checks per frame. With 200 entities that's 20,000 checks. With 1,000 it's 1,000,000. A spatial data structure reduces this to O(n) on average by answering: *"given this entity's bounding box, which other entities could possibly overlap it?"*

---

## Uniform Spatial Grid

### How It Works

Divide the world into a regular grid of cells. Each entity is registered in every cell its bounding box overlaps. To find collision candidates for entity A, look up all other entities in A's cells.

```
World divided into 4×4 cells:
┌───┬───┬───┬───┐
│   │ B │   │   │
├───┼───┼───┼───┤
│   │ A │ A │   │   ← entity A spans two cells
├───┼───┼───┼───┤
│ C │   │   │ D │
├───┼───┼───┼───┤
│   │   │ D │ D │   ← entity D spans two cells
└───┴───┴───┴───┘

Cell (1,1) contains: [A, B]   → check A vs B
Cell (1,2) contains: [A]      → nothing to check
```

The data structure is a `Dictionary<(int cellX, int cellY), List<Entity>>`. The cell coordinates are derived from world position: `cellX = (int)(worldX / cellSize)`.

**Each frame:**
1. Clear all cells
2. For each entity with a Collider, compute which cells its AABB overlaps, insert the entity into each of those cells
3. For each entity, collect all entities in its cells, deduplicate, run narrow phase against each

**Insertion is O(1) per cell** (hash table insert). An entity spanning k cells costs O(k). For a grid cell size equal to the average entity diameter, k is typically 1–4.

### Cell Size Tuning

Cell size is the one critical parameter:
- **Too small:** each entity spans many cells → O(k) insertion cost grows; memory overhead from many small lists
- **Too large:** many entities per cell → back to O(n²) within large cells
- **Sweet spot:** roughly equal to the diameter of the largest common entity

For Asteroids: large asteroids are ~80px wide, small asteroids ~20px, bullets ~5px. A cell size of ~100px works well. The ship and large asteroids fit in one cell; bullets span one cell trivially.

### The Mixed-Size Problem

If entities vary greatly in size (a small bullet vs a large room-filling platform), one good cell size does not exist:
- Size it for bullets → platform spans hundreds of cells, inserting it is O(thousands)
- Size it for platforms → hundreds of bullets per cell, defeating the purpose

This is the main weakness of the uniform grid.

---

## Quadtree

### How It Works

A quadtree recursively subdivides 2D space into four equal quadrants. Each node has a capacity N (typically 4–8). When a node's entity count exceeds N, it **splits**: creates four child nodes and re-distributes its entities among them.

```
Before split (capacity = 4):
┌─────────────────┐
│  A  B           │
│     C   D   E   │  ← 5 entities, exceeds capacity → split
└─────────────────┘

After split:
┌────────┬────────┐
│  A  B  │        │   NW: [A, B]
│        │        │   NE: []
├────────┼────────┤
│  C     │  D  E  │   SW: [C]
│        │        │   SE: [D, E]
└────────┴────────┘
```

If SE then gets more entities, it splits again — recursion continues until a maximum depth is reached (typically 8–10 levels, giving a minimum cell size of `worldSize / 2^depth`).

### Insertion

```
function insert(node, entity):
    if entity does not fit entirely within node.bounds:
        return false   ← caller handles it (store in parent)

    if node is a leaf:
        add entity to node.entities
        if node.entities.count > CAPACITY and node.depth < MAX_DEPTH:
            split(node)
            re-insert all entities into children
        return true

    for each child in node.children:
        if insert(child, entity):
            return true

    add entity to node.entities   ← entity straddles multiple children; store here
    return true
```

An entity that straddles a boundary is stored in the **smallest node that fully contains it**. This is important: a large entity near the centre of the world will be stored high in the tree (possibly at the root) because no quadrant fully contains it.

### Querying (Collision Candidates)

```
function query(node, queryBounds) → List<Entity>:
    if node.bounds does not overlap queryBounds:
        return []

    results = node.entities   ← entities stored at this node level always included

    if node has children:
        for each child:
            results += query(child, queryBounds)

    return results
```

For collision detection: query the tree with each entity's bounding box, deduplicate results, run narrow phase.

### Rebuilding vs. Incremental Update

For **static geometry** (trees, walls): insert once, never rebuild. Very fast to query.

For **dynamic entities** (everything in Asteroids): entities move every frame. Options:

**Full rebuild every frame:**
- Clear the tree, re-insert all entities
- O(n log n) per frame
- Simple and correct
- Fast enough for <10,000 dynamic entities

**Incremental update (remove + re-insert moving entities):**
- Only update entities that moved
- O(log n) per moved entity
- Requires tracking which node each entity is stored in
- More complex, but avoids rebuilding the whole tree when most entities are static

For a game where all entities are dynamic (Asteroids), full rebuild every frame is the right approach.

### The Boundary-Straddling Problem

An entity straddling two or more quadrant borders is stored in the lowest common ancestor — potentially near the root. In a worst case, many entities cluster near quadrant boundaries and all end up at the root, degrading to O(n²). 

Mitigations:
- **Loose quadtree:** expand each node's bounds by some factor (e.g., 2×) so entities near borders fit inside a child rather than straddling it. Nodes overlap, but entities are stored lower in the tree.
- **Store in all overlapping nodes:** an entity can appear in multiple leaf nodes. Requires deduplication during query.

---

## Side-by-Side Comparison

| Property | Uniform Spatial Grid | Quadtree |
|---|---|---|
| **Insert (single entity)** | O(1) amortized | O(log n) |
| **Query (candidates for one entity)** | O(1) + candidates | O(log n) + candidates |
| **Full rebuild** | O(n) | O(n log n) |
| **Mixed entity sizes** | Poor (cell size is a compromise) | Good (small entities go deep, large stay high) |
| **Implementation complexity** | Low | Medium |
| **Memory** | Fixed (grid size × bucket overhead) | Dynamic (tree nodes allocated on demand) |
| **Predictability** | High (behaviour depends only on n and cell size) | Lower (degenerate distributions can cause bad behaviour) |
| **Cache friendliness** | High (flat arrays) | Low (pointer-chasing through tree nodes) |

---

## Which to Choose for This Engine

The right answer depends on what kind of games you want to support beyond Asteroids.

**Spatial grid is better if:**
- Entity sizes are roughly uniform (all enemies are similar size, all bullets are similar size)
- The world size is fixed and known at startup (easy to size the grid)
- You want maximum simplicity and predictability
- Maximum entity count is in the hundreds to low thousands

**Quadtree is better if:**
- Entity sizes vary by more than ~5× (a tiny bullet vs a large ship vs a room-sized boss)
- The world is very large or procedurally generated (grid would need too many cells)
- Most entities are static (build once, query many times cheaply)
- Maximum entity count is in the tens of thousands

**For this engine:** The spatial grid is the better first implementation. It is simpler, faster to implement correctly, and performs equally well or better for the kinds of scenes a 2D arcade game produces. The quadtree becomes advantageous when entity sizes vary dramatically or the world is very large — both cases that can be added later behind the `ISpatialIndex` interface.

**Recommended cell size strategy:** `cellSize = diameter of the largest commonly-spawned entity × 1.5`. For Asteroids: large asteroid ~80px → cell size ~120px.

---

## Sources

- **Game Programming Gems 1** — Andrew Kirmse, "Loose Octrees" (same concept applies to 2D loose quadtrees): ISBN 978-1584500490

- **Real-Time Collision Detection** — Christer Ericson, Chapter 7 (Spatial Partitioning): ISBN 978-1558607323  
  Authoritative treatment of quadtrees, grids, and BVHs with complexity analysis.

- **Quadtree tutorial with diagrams** — pvigier's blog: https://pvigier.github.io/2019/08/04/quadtree-collision-detection.html  
  Clear walkthrough of a C++ quadtree for collision detection with benchmark comparisons against brute force.

- **Spatial hashing for games** — Matthias Müller (Ten Minute Physics): https://matthias-research.github.io/pages/tenMinutePhysics/  
  Series 11 covers spatial hashing with practical C++ implementation. Concepts transfer directly to C#.
