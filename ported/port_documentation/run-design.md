# Run Design

The run and intra-run progression. Meta-progression (atlas, crafting, trading, expeditions) is out of
scope — it sits on top of this and only earns its complexity if the run underneath is good.

**Status:** nothing is committed. Sections are tagged:

| tag | meaning |
|---|---|
| **[SETTLED]** | decided |
| **[LEANING]** | stated preference, not committed |
| **[PROPOSED]** | proposal awaiting judgement |
| **[DEFERRED]** | deliberately postponed |
| **[OPEN]** | undecided — listed in §14 |

---

## 0. Posture

**The current prototype is a proof of concept, not a foundation.** Its weapons, skills, materials, ship
and tuning are evidence that the destruction model is fun — not constraints on what the game becomes.

**The one thing that must survive:** the cell/bond fracture model. Everything below exists to make it
*matter mechanically*, not just look good.

Where this document asks the engine for something it doesn't have, that is a feature request (§13).

---

## 1. What a run is

**[SETTLED]**

- **20–30 minutes.** The number of engagements is **not fixed** — it depends on how much of the system
  the player explores and how many POIs they enter and complete. A cautious run and a greedy run are
  different lengths and different shapes.
- **Hybrid combat.** Chaff mobs at ~10–15 cells each; **physics-skill mobs as the primary threat** —
  enemies whose abilities alter the environment and force manoeuvring; **destructible incoming
  projectiles** you shoot down; **screen-sized structural set-pieces** as terrain or objectives.
- **The ship is fracturable.** No HP bar; damage is structural and removes *functions*. Full recovery
  during a run **is** possible — the mechanism is deferred (§11).
- **Run-to-run variety is non-negotiable.** Same ship, same system, same meta state must still produce a
  different run. The hardest requirement here; everything is measured against it (§15).

### The spine

1. **The build assembles across the run** from two sources: the ship's **skill tree** (directed, chosen)
   and **items** (random, found). See §10.
2. The hull degrades structurally, and can be recovered at a cost.
3. A retire decision exists and is legible.
4. A core objective sits at the centre.
5. **There are exactly two ways out** — and they are not equivalent (§3.4).

### The central arc

**Your build assembles while your ship falls apart.** Two opposing curves; the run is the race between
them, and every decision to go deeper is read against both.

---

## 2. Structure

**[SETTLED]** There is **one primary map archetype: the solar system.** Everything else — temples,
binary-planet systems, derelicts, caves — exists as **POIs inside it** (§6), not as competing map types.

This is a change from an earlier draft that treated them as sibling archetypes. Making them POIs is
better: the solar system is expressive enough to carry a run on its own, and POIs are where new ideas
get explored cheaply without designing a whole map type around each one.

**[SETTLED — naming]** "Side pockets" is retired. They are **POIs**, because they are not side content —
they are one of the main attractions of a run.

---

## 3. The solar system

### 3.1 Scale and traversal

**[LEANING]** ~150–250 screens across (at 1920×1080: 288,000–480,000 px).

**[DEFERRED — cruise mode.]** Direct flight to the core is ~2–5 minutes depending on size and ship
speed. That is acceptable, especially because completing the core objective grants an immediate exit
(§3.4) rather than requiring the trip back. Tune **system size, ship speed and the strength of the
inward pull** together during prototyping; only add a cruise mode if playtesting says the flight is
dead time.

### 3.2 The gradient — corrected

**[SETTLED]** Approaching the core is **not free**. The system becomes *more chaotic* toward the centre:
denser mobs, more hazards, more debris, more interference. Depth costs on the way in as well as out.

What the inward pull does is **assist travel**, not remove danger:

- **Inbound:** assisted by the pull, but through escalating chaos.
- **Outbound:** resisted by the pull, through everything you stirred up, in a damaged ship.

So the earlier framing ("going in is free, coming out is work") was wrong about danger and right about
travel. Both directions cost; they cost differently.

```
        ╭───────────── RIM ─────────────╮      retire zone
      ╭──────────── OUTER ────────────╮        sparse, calm
    ╭─────────── MIDDLE ───────────╮          contested
  ╭────────── INNER ──────────╮               chaotic
 ╭──────── CORE ────────╮                     the objective
```

**Difficulty is self-balancing.** Weak builds work the outer bands; strong ones dive. The geometry is
the curve — no authored escalation ladder required.

**The player's route is the run's structure.** Two players in the same seeded system have genuinely
different runs with no difference in generated content.

### 3.3 POI tier is intrinsic, not positional

**[SETTLED]** A POI's difficulty and reward are **properties of the POI**, not functions of its current
orbital radius. A titanium world is a titanium world wherever it happens to be.

This resolves two problems at once: it keeps persistent POI state coherent (a POI's contents can't
"become harder" while you're not looking), and it makes narrative sense.

**Depth still equals reward** — because high-tier POIs are *seeded* into inner orbits and stay in their
band (§5.2). The gradient comes from *distribution*, not from a per-POI scaling function.

### 3.4 Two exits, and they are not equivalent

**[SETTLED]** This replaces the earlier "extraction is always a long journey home," which made the
climax anti-climactic.

| exit | availability | cost |
|---|---|---|
| **Rim retire** | always | the journey out — outbound against the pull, through everything you disturbed |
| **Core extraction** | only after completing the core objective | **immediate** |

The core objective — likely a boss — **is** the culmination of the run. Beating it should not be
followed by four minutes of travel. Completing it opens an immediate way out.

This also sharpens the retire decision rather than softening it: bailing early costs you the trip home;
finishing costs you the dive but pays out instantly. **[SETTLED]** Retiring at the rim without the core
objective is a fully legitimate run — it banks meta resources and is how a player farms toward being
strong enough to take the core.

---

## 4. Simulating a system that can't be simulated

**[SETTLED — the model]** Bodies are **frozen, not destroyed**, when far from the player. Everything you
break stays broken and stays where you left it, for the whole run. This gives a lived-in system and
removes the question of what counts as "significant" wreckage — everything counts.

Four levels, promoted and demoted by distance from the player's **simulation** position (never the
camera — that would desync in co-op):

| level | what runs | applies to |
|---|---|---|
| **L0 — Active** | full physics, fracture, collision, AI | inside the action bubble |
| **L1 — Near** | kinematic integration only; no fracture, no mutual collision; cheap AI | a band outside the bubble, wide enough that nothing pops |
| **L2 — Analytic** | **no simulation** — position is a closed-form function of time | bodies on orbits: planets, stations, anything permanent |
| **L3 — Frozen** | nothing; full state retained in place | everything else outside the bands |

**The load-bearing insight: almost everything permanent in a solar system is on an orbit, and orbits are
analytic.** `pos = centre + r·(cos(θ₀+ωt), sin(θ₀+ωt))` — no integration, exactly reproducible, free,
and trivially deterministic for lockstep. You are not simulating a solar system; you are simulating a
bubble and evaluating a formula for everything else.

**[DEFERRED — the memory cap.]** An earlier draft proposed collapsing small distant fragments into a
visual-only debris field to bound memory. The arithmetic says it isn't needed yet: at ~80 B per cell,
~32 B per bond and ~128 B per body, even a heavily-explored run holds up comfortably.

| frozen bodies | avg cells | cells | bonds | total |
|---:|---:|---:|---:|---:|
| 1,000 | 12 | 12,000 | 30,000 | **2 MB** |
| 3,000 | 12 | 36,000 | 90,000 | **6 MB** |
| 6,000 | 15 | 90,000 | 225,000 | **15 MB** |
| 12,000 | 20 | 240,000 | 600,000 | **40 MB** |

A fully-explored system is tens of megabytes, not hundreds. **Freeze everything, cap nothing** — revisit
only if it becomes a measured problem.

---

## 5. Orbits

### 5.1 Band ownership protects the gradient — not a speed limit

The risk was never that a body can outrun the ship. **It is that a low-tier body on an eccentric orbit
could carry the player inward, skipping the escalating chaos that makes depth cost something.**

The fix is a generation constraint, not a speed cap:

> **Every POI owns a band. Its orbit — however eccentric — stays inside that band.**

Riding a body then moves you *within* a band and skips nothing. Two consequences:

- **Orbital speed no longer needs to be capped.** A body may orbit faster than the ship; using it as
  transport is not a problem, because it cannot take you anywhere that matters. The period table from
  the previous draft is dropped.
- **The park rule is dropped.** An Instance can keep orbiting while the player is inside it, because
  arriving at a different point *in the same band* is not an exploit.

### 5.2 Orbits never overlap — by construction

Bodies on distinct circular orbits can never collide. Give each POI its own orbital radius with a
minimum separation greater than the sum of their influence radii, and overlap is impossible at
generation time with no runtime checks. Band-bounded eccentricity (§5.1) keeps orbits from crossing.

### 5.3 Planets are not obstacles at system scale

**[PROPOSED]** A body drifting toward a stationary player must not force evasive flying. Dodging
slow-moving planets is not the intended game, and planets are large.

**In the system view a planet is a gravity well and a landing envelope, not a solid collider.** You fly
across its footprint freely. Its destructible mass lives in its Instance, at a different scale — the two
representations were already at different scales (§7), and this makes that explicit.

Combined with player-initiated transitions (§7.1), a planet sweeping past is a spectacle rather than a
hazard, and it can never pull you inside.

---

## 6. POI taxonomy — three tiers

**[SETTLED]** Not every POI generates a new map. Some are authored locations, objects or enemies that
live **directly in the solar system** — orbiting or parked — with no instance and no transition.

This split matters more than it first appears: **every hard problem in §7 applies only to the instanced
tier.** Transitions, carried pursuers, entry/exit points, persistence, orbital riding — none of it
touches in-world content. And it keeps the solar system *the game* rather than a transit layer between
instances, which is the single biggest risk of a large map.

### The rule for which is which

> **Instance when the content needs a different physics context. Stay in-world when it is made of the
> same stuff as space.**

A planet surface has ground, gravity, terrain and a boundary. A temple has walls, rooms and *no forces*.
Neither can exist in open vacuum — they need their own force regime and static geometry. A derelict
hulk, a minefield, a gun platform, an elite patrol, a loot site, an asteroid cluster are all just bodies
in vacuum, and belong in the system.

This makes the taxonomy self-explaining rather than a per-POI judgement call.

### The three tiers

| tier | what it is | destination? | transition | persistence |
|---|---|---|---|---|
| **Ambient** | procedural world content — mob patrols, drifting hazards, debris, loose asteroids | no | — | frozen in place (§4) |
| **Site** | notable in-world content, authored or generated, **orbiting or parked** — derelicts, platforms, loot sites, named elites, structures | yes; marked or secret | **none** | frozen in place (§4) |
| **Instance** | content needing its own space — planet surfaces, temples, binaries, caves | yes | committed (§7) | full snapshot (§7.2) |

**Sites are where secret POIs naturally live.** Spotting a wreck while flying past is a far better
discovery moment than an unmarked icon that opens a loading transition.

**[PROPOSED — the mix ratio is the real design lever.]** Roughly **3–5 instances and 15–30 sites** per
run, with ambient content everywhere. Instance-heavy makes the system a hub; site-heavy makes it a
place. Site-heavy also makes each instance *land* — a deliberate transition earns its cost when it is
rare.

### POIs are a main attraction, and need not be dangerous

**[SETTLED]** POIs are not optional detours. They are the most flexible part of the design and where new
ideas get explored — including the **Temple** (enclosed rooms, destructible architecture, no forces) and
**Binary planets** (two bodies orbiting a barycentre, the gap as hazard), which are POI *kinds*, not map
archetypes.

A POI can be a threat, a puzzle, a safe harbour, a resource site, or something else entirely. Variety of
*kind* is the point.

Kinds identified so far — **Site:** derelict, platform, loot site, named elite, minefield, structure ·
**Instance:** surface, body, temple, binary, cave. **[DEFERRED]** *interior* (temple-at-planet-scale
inside a hollow world).

**[OPEN — naming.]** "Site" and "Instance" state the technical difference plainly but are not diegetic.
The distinction matters; the words do not, and can change freely.

**[OPEN] Is the core objective a Site or an Instance?** In-world keeps the run seamless and stages the
climax in the most chaotic part of the map, using the system's own hazards and the debris you have
accumulated as the arena. An Instance gives authored control over the boss arena. Leaning in-world.

## 7. Instancing

**Applies only to the Instance tier (§6).** Sites and ambient content never transition.

### 7.1 The transition, concretely

**[PROPOSED]** Six phases. The load-bearing property: **the transition is player-initiated and, once
started, it completes.** Proximity alone never triggers it (§5.3), so a planet drifting over you does
nothing.

| phase | what happens |
|---|---|
| **1 · Approach** | The player enters the body's landing envelope; an indicator appears. **No commitment.** |
| **2 · Initiate** | The player deliberately triggers descent — a held input, or flying a descent vector. |
| **3 · Capture** | Everything inside a *transition bubble* around the player — mobs, fragments, projectiles — is marked **carried**, and its offset from the player is recorded. |
| **4 · Descent** | The animation plays. The carried set is held out of simulation. Not cancellable. |
| **5 · Instantiate** | The Instance is created, or restored from its snapshot. Carried bodies are re-injected at their recorded offsets from the player's entry point — sizes unchanged, relative positions preserved 1:1. |
| **6 · Resume** | Simulation runs. The fight you brought with you continues. |

**Why carrying is cheap.** `SimState` is our own data structure, with snapshot/restore already built for
netcode. Moving a subset of bodies between two simulation contexts is an array operation over known
tables plus a coordinate offset — not a special case inside the physics.

**Why it matters.** It closes instancing-as-escape-hatch: you cannot break contact by diving into a
planet, and enemies never vanish mid-animation.

### 7.2 While you're inside

**[PROPOSED]**

| | behaviour |
|---|---|
| **The Instance's body** | **keeps orbiting** — band ownership (§5.1) makes this harmless |
| **Debris captured in its well** | moves with it, as a frozen set parented to the body's frame |
| **Everything else** | advances analytically; frozen bodies stay where they are |

Debris near a planet travelling *with* the planet is physically motivated — it sits in the body's
gravity well — and it means the neighbourhood you left is the neighbourhood you return to, without
having to stop time anywhere.

The result is the right combination: the system has advanced at the macro scale while your immediate
surroundings are exactly as you left them.

### 7.3 Exit is the way you came in

**[PROPOSED — replaces the boundary-as-horizon idea]** An earlier draft let the player leave by flying
past the Instance boundary. That is a balance and readability hazard: free egress from anywhere lets you
bail out of any fight at any moment, and it sits badly against entry being a commitment.

Instead: **entering marks an exit point at your arrival position, and that is where you leave.**

- **Egress costs travel.** Bailing from a fight deep inside means crossing back to the door.
- **Re-entry is deterministic.** The same approach vector returns you to the same point — which answers
  "where does the player start when re-entering a body that is accessible from all sides."
- **Exit is symmetric with entry:** a short committed animation, with anything in the transition bubble
  carried back out with you.

**[OPEN]** For large bodies, approach angle could select between several distinct entry points, each
opening a separately-persisted region. One region per Instance is enough to prototype.

## 8. Encounter content

**[SETTLED]** Good enough for now; expected to grow.

| Type | What it is | What it exercises |
|---|---|---|
| **Territory** | Mob packs holding a region. **The bread and butter** — mobs are the primary threat | movement, physics-skill mobs |
| **Barrage** | Incoming *destructible projectiles* to shoot down while something else happens | destruction as **defence** |
| **Siege** | A large structural body to dismantle, with role-bearing sections | set-pieces, reading a structure |
| **Site** | A tiered destructible container with a loot core (§9) | greed, weapon choice |
| **Hazard** | Environmental, no enemies — debris storm, collapsing structure | traversal under pressure |
| **Core** | The system's final objective, likely a boss | everything |

**Objectives must be cell-count independent.** "Fully destroy this body" varies enormously by weapon,
making it a weapon *gate* rather than a weapon *choice*. Prefer: kill the core cell, sever N structural
bonds, survive N waves, hold a location.

**[SETTLED]** Mobs will be **tailored to their environment** — a temple mob and an open-space mob are
different designs. Universal AI across temples, open space and gravity-heavy maps is not a goal.

---

## 9. Loot

**[SETTLED — model]** Risk of Rain 2 / Wizard of Legend in spirit: many small items that combine.
**[SETTLED]** Stacking is **not multiplicative** — that escalates out of control. Additive with
occasional threshold or synergy effects.

**[DEFERRED]** In-run currency is not excluded, just postponed.

**[SETTLED — acquisition]** Tiered destructible **loot sites**, with the item on a **`loot`-role cell at
the site's core**. This turns *"pulverise everything"* into **"dig to the core"** — skill expression,
reads visually, respects each weapon differently, and scales by tier through the shell's construction
rather than its size. It is also what makes fracture depth matter for progression: you are rewarded for
*structural* destruction, not just for killing.

---

## 10. The build: skill tree + items

**[SETTLED]** This is the second axis of variety and the earlier drafts missed it.

- Each **ship** has its own **skill tree**, which **resets at the start of every run**.
- Progressing it requires an in-run advancement resource — **[OPEN]** call it XP; earned from
  destruction, kills, objectives, or some mix.
- The ship also **starts with a set loadout** of weapons and modules.

**The build is the combination of the tree and the items** — one directed and chosen, one random and
found. That pairing is what makes runs feel authored *and* surprising, and it means two runs with the
same ship diverge along two independent axes rather than one.

**[OPEN]** What generates XP is a real design lever. Tying it to *structural* destruction rather than
kills would reinforce the same thing the loot-core mechanic reinforces.

---

## 11. Hull damage and death

**[SETTLED]** Damage is felt and costs something. Full recovery during a run is possible.
**[DEFERRED]** Repair mechanics are out of the prototype and will be designed later — but
**[SETTLED]** repair will **not be free**.

**[SETTLED]** Ammo is a needed mechanic. **[OPEN]** its shape.

**The attrition problem** remains: ~11 binary cells isn't a curve, it's a handful of hits each deleting
a *function*. For "build vs decay" to be a real arc, damage must be **graded** — more and smaller hull
cells, and a real armour role that absorbs and sheds.

**Death [SETTLED].** Losing the cockpit's propulsion grants a **guaranteed cockpit thruster**, but you
must **eject and abandon the ship**. You can fly the cockpit out and bank your loot — at the meta cost
of losing the ship. That makes the last moments of a failing run playable, and gives the player a real
choice between a desperate save and going down with the hull.

---

## 12. What varies between runs

The §1 requirement, made explicit — this is what the design must deliver:

| axis | source |
|---|---|
| **System layout** | which Sites and Instances are seeded, at which radii, with which orbital phases |
| **POI contents** | each POI's internal generation |
| **Route** | player choice — which POIs, in what order, how deep, how long |
| **Items** | which drop, from which sites, in what order |
| **Skill tree** | which path the player invests in, given the XP they earn |
| **Damage history** | which functions you lost and when — reshapes the rest of the run |

Six axes, four of them player-driven. That is where variety has to come from, because the map archetype
is fixed.

---

## 13. Engine extensions this asks for

Ordered by design value per unit of work.

1. **Per-cell materials.** A body currently has one material. Per-cell material makes **composite
   structures** expressible: planets with crust/mantle/core, walls with reinforced doors, armour over a
   soft interior, ore veins, chain-detonating cells. Turns "shoot the thing" into "read the thing, then
   shoot the right part" — the model's actual thesis, currently inexpressible. **Highest value.**
2. **Static and kinematic fracturable bodies.** Walls, hulls and crust that don't drift, spin or get
   pulled. Required for anything you fight *inside* — temples, caves, derelicts, planet surfaces. Needs
   a non-integrating body kind plus an incremental crack-geometry cache.
3. **Simulation LOD + instance persistence (§4, §7).** Analytic orbits make this far cheaper than it
   looks, and instance snapshots reuse the netcode snapshot machinery. The genuinely new work is the
   promote/demote bubble, freezing, well-capture of local debris, and carrying bodies across the
   transition (§7.1).
4. **Fields as content, not constants.** The system's inward pull, a binary POI's two wells, a temple's
   null — placed entities with shape and falloff, authored per map and per POI.
5. **An external-velocity channel for the player.** Forces must be able to *move you*, via a channel
   that decays rather than being clamped away by the movement controller. Without it every field is
   scenery.
6. **Very large bodies.** Planets and screen-spanning structures — depends on the tiering work already
   planned, probably on a body internally partitioned so a planet reads as one thing until broken.
7. **Runtime cell/bond addition.** For repair, salvage and ship growth. Deferred with repair; note the
   fracture model currently only *removes*, so this is a real change, not a parameter.

---

## 14. Open questions

**Structural**
- **What generates XP** for the skill tree (§10) — and should it favour structural destruction?
- **Ammo's shape** (§11), and whether it shares a resource with skills.
- **Does the system "tighten" over a run?** An earlier draft had bands migrating outward as an
  anti-camping clock. With the gradient now costing on the way in (§3.2) and ammo pressure likely, it
  may be unnecessary — and it fights band ownership (§5.1), which is what keeps the gradient honest.
  **Suggest: drop it unless playtesting shows camping is a problem.**
- **Multiple entry points per large Instance** (§7.3), each opening a separately-persisted region?
- **Is the core objective a Site or an Instance?** (§6) — leaning in-world.
- **The Site/Instance mix ratio** (§6). Proposed 3–5 Instances, 15–30 Sites; this number decides
  whether the system reads as a place or a hub.
- **Naming** for the two destination tiers (§6).

**Content scope**
- **How many Site kinds, Instance kinds, encounter types and mob archetypes before launch?** This
  replaces "how many map archetypes" now that there is one primary map, and it is the real
  content-scope question. Note Sites are much cheaper per unit than Instances.

**Deferred, listed so they aren't forgotten**
- Repair mechanics (§11) · in-run currency (§9) · cruise mode (§3.1) · interior POIs (§6) ·
  runtime cell addition (§13.7)

---

## 15. A sample run

**[PROPOSED]** Concrete enough to build. ~200-screen system, target 25 minutes. Engagement count is
emergent, not designed — this is one possible shape.

| t | beat | what it exercises |
|---|---|---|
| **0:00** | Enter at the rim. System map shows ~20 destinations with orbits and positions — mostly **Sites**, a handful of **Instances**. Several more are unmarked. High-tier destinations sit in inner bands. | the map as a planning surface |
| **0:00–4:00** | Work the outer band entirely **in-world**: two Territory fights, a derelict **Site** picked clean, one tier-1 loot site — dig to the core. No transitions at all. **2 items**, first XP. | the system carrying the run itself (§6) |
| **4:00** | Spot an unmarked **Site** while flying — a drifting hulk with a named elite aboard. Take it or leave it. | discovery through flying, not icons |
| **4:00–9:00** | First **Instance**: a mid-band planet surface. Two chasing mobs **follow you through the transition** and the fight continues on the ground. Complete the objective; lose a weapon cell. Cross back to your entry point to leave — two surviving mobs are carried out with you. **3 items.** | §7.1, §7.3, graded damage |
| **9:00** | Outside, the neighbourhood is exactly as you left it; the wider system has advanced. Your own debris from 4:00 is still drifting where you left it. | §4 freeze, §7.2 |
| **9:00–14:00** | Back in-world for three more **Sites** — a platform, a minefield, a loot site. **2 items**, more XP. Second **Instance** available nearby: a derelict temple. Skip it, or commit? | site-heavy pacing; instancing as a deliberate beat |
| **14:00** | **Decision point.** Seven items, a mid skill tree, one weapon cell gone. Rim is ~90 s away. Core is deeper, through escalating chaos, and bailing later costs the whole trip back. | the central arc |
| **14:00–21:00** | Push inward. Chaos rises — denser mobs, a Barrage encounter, a hazard field. One inner-band loot site for a tier-3 item. | the gradient (§3.2) |
| **21:00–25:00** | **The core.** A Siege on a screen-sized structure into the boss, fought amid the system's own hazards and the wreckage you left on the way in. Completing it opens **immediate extraction** — the fight is the climax, not the prologue to a commute. | §3.4 |

Note the shape: **roughly 2 Instances and 8 Sites**, with the majority of the run spent flying and
fighting *in the system*. Transitions are punctuation.

**The alternative shape:** never dive. Work the outer and middle bands, clear six POIs, retire at the
rim with a full haul and no core kill. A legitimate run that banks meta resources toward a future
attempt.

---

## 16. What to prototype, and how to judge it

Each step answers a question that invalidates the next if wrong.

1. **The field + the player velocity channel.** Fly a radial field in an otherwise empty system. *Does
   the pull make movement interesting, and is outbound meaningfully harder than inbound?*
2. **Analytic orbits + the system map.** *Is the map a planning surface, or just a destination picker?*
3. **The gradient.** Mob density and hazard by depth. *Does choosing a depth feel like a decision?*
4. **Simulation LOD + freezing.** *Does a 200-screen system hold together without visible popping, and
   does returning to your own debris field feel good?*
5. **One Instance, end to end** — player-initiated transition with carried pursuers, orbiting while
   inside, well-captured debris, entry-point exit, persistence across re-entry. *Does leaving and
   returning feel continuous, and are the seams exploitable?*
6. **Two exits.** Core completion granting immediate extraction vs the rim journey. *Does finishing feel
   like a climax and bailing feel like a cost?*

### Judging it

- **Build divergence.** Ten runs with the same ship should produce builds a player describes
  differently — across *both* axes (§10). The primary measure of success.
- **Route divergence.** Ten runs on the same seeded system should produce visibly different routes and
  different moments of commitment.
- **The 20-minute test.** Does one system hold up, or go stale?
- **No exploitable seams.** Specifically: can instancing be used to break contact (§7.1), reset a fight
  (§7.2), skip the difficulty gradient (§5.1), or bail from a losing fight for free (§7.3)? Each is
  answered by design; verify each holds in play.
- **No accidental weapon gates.** Every objective completable by every weapon archetype.
