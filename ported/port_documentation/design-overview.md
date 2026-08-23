# Asteroids on Steroids — Design Overview (Reconciled)

Living document. This reconciles [`design-overview-vladastos.md`](design-overview-vladastos.md)
and [`design-overview-sebastos.md`](design-overview-sebastos.md) into a single design, plus a
run-scope clarification (galaxies vs. solar systems) given after both were written. Those two
files are kept as historical individual input, not maintained further — **this document is the
current source of truth.**

Where the two source docs agreed, this is a synthesis. Where they conflicted, the resolution is
marked **[RESOLVED]** with the reasoning; conflicts not yet settled are marked **[OPEN]** and
also listed in §11.

---

## 1. Vision

An extraction roguelite built on top of an asteroids-style physics/destruction core. The player
pilots a fracturable ship out from the mothership, into a galaxy, through a procedurally
generated chain of solar systems and planets, fighting and salvaging — and decides, every time a
system is cleared, whether to push deeper for better loot or bank what they have. Loss on death
is real and specific: a run's unbanked loot, and potentially the ship itself. Progress across
runs is not lost: it's banked into a persistent meta-game of ship-building, crafting, and trading
back at the mothership.

**Primary references and what we take from each:**

| Inspiration | What we borrow |
|---|---|
| Enter the Gungeon | Per-projectile weapon identity, per-run build variety from item pickups, meta-quests/unlocks that span runs, "one more run" pacing |
| Path of Exile 2 | Endgame map/tablet system as a model for run modifiers, the atlas-style node graph for structuring content, town/trading hub loop |
| (implicit) Extraction genre | The core tension: banked loot vs. greed — die and lose what you're carrying, potentially the ship carrying it |

**Core tension the whole design serves:** every layer of a run is a new "keep going or bank it"
decision. The inter-system layer makes this explicit, but planet encounters and solar-system
exploration should carry the same undertone — the player is always sitting on something they
haven't secured yet.

---

## 2. Two systems, one loop

- **The run** — a single foray, from mothership departure to either death (loot, and possibly
  the ship, lost) or voluntary retirement (loot banked). A run takes place inside one **galaxy**
  chosen from the persistent atlas; everything generated for that run — the small graph of solar
  systems inside the galaxy, the planet layouts, the encounters, the loot rolls — is freshly
  generated each time and discarded afterward (§3).
- **The meta-progression** — persistent, mothership-side. Ships, blueprints, crafting materials,
  currency, reputation, the atlas of galaxies, and unlocked content all live here and persist
  across runs regardless of any single run's outcome.

The only channel between them is **what the player extracts**: resources, loot, and crafting
materials carried out of a run become raw material for meta-progression; meta-progression
choices (ship, build, galaxy modifiers, expeditions) in turn determine what a run is capable of.
Neither system should be legible or completable in isolation.

```
 MOTHERSHIP (meta)  --ship + modifiers-->  RUN (in one galaxy)  --extracted loot-->  MOTHERSHIP
        ^                                        |
        |                                        v
   (persistent)                    (lost on death / banked on retirement)
```

---

## 3. Two graphs, two scopes

**[RESOLVED — this is the run-scope clarification given after both source docs.]** Sebastos's
doc implied a run covers a single solar system; vladastos's doc had a run push through a chain of
several. Both were right at different zoom levels — there are two graphs, not one:

### 3.1 The atlas graph (persistent, nodes = galaxies)

The top-level, mothership-side structure. Nodes are **galaxies** — each a standing, persistent
meta-progression asset, closer to a PoE2 atlas map node than to a level.

- A galaxy's position in the atlas, its connections, and its broad identity (which planet
  variants and resources it tends to produce, its danger tier) are stable meta-state.
- **Settlements live on the atlas, not inside a run.** Inhabited trading hubs (§7) are attached
  to galaxy nodes and reachable directly from the mothership without starting a run — no combat,
  no death risk. This only works because galaxy identity is persistent; a settlement anchored to
  content that gets discarded every run wouldn't make sense as a place NPCs live and trade.
- **Growth**: the atlas starts small (near the mothership) and expands as runs succeed — clearing
  a galaxy's own boss for the first time reveals its outward atlas edges.
- **Danger/distance as an exploration incentive**: an atlas edge's danger rating and distance
  from the mothership are what make trade hubs (§7) worth seeking out — hubs off the shortest
  path can offer better orders precisely because reaching them costs more.
- **Run modifiers are applied to a galaxy before entering it** — the PoE1-scarab/PoE2-tablet
  analogy (§6.4) lives here specifically: juice an atlas node, then run it.
- **Expedition targets** (§8) are atlas galaxies, not solar systems — see §8 for why.

### 3.2 The per-run system graph (ephemeral, nodes = solar systems)

When the player departs into a chosen galaxy, that galaxy's *interior* — a small graph of solar
systems — is procedurally generated fresh for that run alone and discarded afterward. Running the
same galaxy twice produces two different system graphs. This is what the inter-system layer
(§5.3) traverses:

- After a system is cleared, the player chooses to retire toward the mothership (banking loot) or
  push along an edge to another generated system in the same galaxy.
- All paths through a run's system graph converge on that galaxy's own final system and boss —
  the galaxy's endgame encounter. (A galaxy's atlas position, §3.1, is what determines how tough
  that encounter is — the atlas is where game-wide endgame tiering lives, not the per-run graph.)
- Because this graph is ephemeral, it carries none of the "persistent place" weight the atlas
  does — disposable structure in service of one run's pacing, like a roguelike floor layout.

**[OPEN]** Can a single run continue across a galaxy boundary — after clearing a galaxy's final
system, push directly along an atlas edge into a connected galaxy without returning to the
mothership — or does clearing a galaxy always end the run and return the player to the
mothership/atlas screen? Not yet decided; see §11.

---

## 4. Loot taxonomy: Items vs. Modules

**[RESOLVED — sebastos's split, adopted wholesale; it fills a gap the vladastos doc didn't
cover.]** The vladastos doc only had persistent, deliberately-weak "skill-modules." Sebastos drew
a sharper, more useful line between two categories that behave completely differently, and that
distinction is adopted as-is:

- **Items** — found during a run, occupy no inventory space (unlimited, stack freely), and
  disappear at the end of the run regardless of outcome. Not stat upgrades; they should carry
  real upsides *and* downsides and change how the run is played, not just scale numbers up. This
  is the per-run build-variety layer (Gungeon-style) — mostly for variety, occasionally for
  a genuinely broken, lucky combination.
- **Modules** — meta-progression items that attach to a specific ship's slots, persist beyond a
  single run, and are randomly generated with affixes/tiers/bases (Diablo-style). A cell can be
  a weapon or a skill; unique modules and unique affixes can change a weapon/skill's *mechanics*,
  not just its stats. Unlike items, modules are explicitly allowed to be strong and to scale
  aggressively — they are the long-term power layer, not just variety.
- **Stakes**: a module is tied to the ship it's attached to. It's lost — permanently — only if that
  ship is lost (§6.2). It is not part of a given run's extractable loot; it's already a
  persistent asset before the run starts.

**[RESOLVED — naming collision.]** Sebastos's original term for the meta-progression item was
"Cell", which collided with the prototype's long-standing use of "Cell" for a ship's physical
hull-structure pieces (cockpit, propellers, cannons, bumper). **The meta-progression item is now
called a Module; "Cell" means a hull piece and nothing else.**

Resolved in favour of the hull piece for three reasons: it is technically the correct term (a hull
piece *is* a Voronoi cell); it is load-bearing across the prototype's code, docs and the fracture
literature; and the sim `Cell` is a hot struct in the fracture arena touched millions of times per
second, whereas a Module is a persistent object with affixes and rarity — sharing a name across that
gap would confuse every future code review. Renaming the meta side cost one find-replace in this
document; renaming the sim side would have cost a rewrite of ~2,300 lines of ported engine code plus
its differential test harness.

---

## 5. The run: layer structure

A run is a stack of four layers. The player is always in exactly one; moving between them is a
deliberate, visible transition (not a menu-driven cut) so the run feels continuous rather than
level-select.

```
Mothership (meta) ─▶ Inter-system layer ─▶ Solar system layer ─▶ Planet layer
                            ▲                       │                  │
                            └───────────────────────┴──────────────────┘
                                  (return up a layer when done / retire)
```

### 5.1 Solar system layer

**[Merged — vladastos's structural framing + sebastos's concrete travel/POI mechanics.]**

The "overworld" of a single generated solar system (§3.2). Space here is mostly empty — a
procedural spawn/despawn system seeds stray asteroids and rare random events as the player moves
and clears them once they're far enough behind and enough time has passed. The intent is to
*feel* the vastness of the system, while accepting that slow manual flight across all of it isn't
the primary way to travel it.

- **Primary travel: fast-travel.** A live system map lets the player set a course to a known
  planet/POI; a travel-line animation plays out over a few seconds while the ship covers the
  distance. This can be interrupted by events (an alien swarm, a debris storm) and, depending on
  the destination's surroundings, the final approach may require manually flying through a
  hazard — e.g. fast-traveling to a planet with a dense asteroid ring drops the player into
  manual flight through that ring on arrival.
- **Points of interest**, entered via one of two transition types (same underlying instance
  system, different presentation):
  1. **Atmosphere-entry** — the camera zooms in, the background becomes a top-down view of the
     destination's terrain (mountains, ruined structures, an energy field), and the player is
     "on" the planet. This is the normal case for the planet layer (§5.2).
  2. **In-space, object-scale** — the camera zooms in but the player is still nominally in
     space; the POI itself is a real physical object in the new instance rather than a
     backdrop. Covers things like a small planet and its satellite (both large destructible
     bodies with a gravity well toward their centroid) or a big planet's asteroid ring. Scale is
     smaller than a full atmosphere-entry map, but the fracture/physics core is identical.
  Both types share the same generation and gameplay systems underneath — what differs is entry
  animation, visual framing, physical scale, and how "grounded" the encounter feels.
- **Objective**: the system's own goal, which is *usually* to find and kill a boss hidden on one
  of the system's planets, but is not fixed to that — a system's objective can instead be to
  destroy an energy core, retrieve an object, or similar. Whatever the objective, clearing it is
  what marks the system "cleared" and unlocks the inter-system-layer choice (§5.3).

**Design intent:** breadth and momentum — fast decisions about which POI is worth the detour,
given the risk budget the player is currently carrying.

### 5.2 Planet layer

**[Merged — vladastos's biome-variant framing + sebastos's broader objective/planet-type range.]**

The core combat/exploration layer, procedurally generated, entered via the atmosphere-entry
transition (§5.1) rather than a load screen.

- **Variants**: forest, ocean, ancient-civilization ruins, several distinct kinds of alien
  civilization, etc. — biome-driven, each with its own resource pool, hazard palette, and visual
  identity, informative to the player before they commit to landing.
- **A planet need not be a biome at all.** Some planets are themselves giant destructible bodies
  with a gravity well at the core — essentially very large, very tough asteroids with a dense
  core and their own gravitational pull, fought and dismantled directly with the fracture engine
  rather than walked around on.
- **Structure**: organic but not open-world — generation should read as a sequence of encounters
  connected by traversal, closer to a hand-authored dungeon crawl than a sandbox. A generated
  critical path with optional side branches for risk/reward is the likely concrete shape (a
  POE-style layout, not a fully open plane). Cf. `docs/spec.md`'s existing wave-based encounter
  pacing in the prototype as a candidate model for individual encounter beats within a planet.
- **Resources**: variant-specific — a forest planet's resources should feel meaningfully
  different from an ancient-ruins planet's, both mechanically and in what they feed into
  meta-progression crafting.
- **End state**: each planet has a mini-objective, most commonly a mini-boss, but — per the
  system-layer objective variety above — not exclusively; "destroy all enemies," "find an
  object," or similar are also valid endings. Clearing it is what makes the planet's
  loot/resources "safe" to leave with, at least back up to the solar-system layer (extraction
  risk, §9).

### 5.3 Inter-system layer

The connective tissue and the decision point that gives the run its extraction-game teeth.
Traverses the ephemeral per-run system graph (§3.2), not the persistent atlas.

- After a system is cleared, the player chooses: **retire to the mothership** (bank everything
  accumulated so far) or **push to another connected system in the same galaxy** (keep playing,
  keep risking).
- Presented as a map/graph view scoped to the current run's galaxy — the player sees (some
  amount of) the branching structure ahead before choosing a path.
- All paths converge on the galaxy's own final system and boss (§3.2).

**Design intent:** every retirement decision should be a real decision. The game needs to keep
legible, at this screen, both (a) what's at stake if the player dies on the next system and
(b) what upside the next system offers (cf. §6.4's galaxy modifiers).

### 5.4 Mothership layer

**[Merged — vladastos's "meta-base" framing + sebastos's concrete named locations.]**

The persistent meta-base — not part of "the run" in the sense that death doesn't touch it, but
it's where a run begins and (if the player survives or retires) ends. Concrete facilities:
ship synthesizer (build/assign modules to a ship, §6.1), item/resource stash, craft bench (§6.3),
and the atlas/galaxy map (§3.1) used for both launching a run and safely visiting settlements
(§7). Full pillar detail in §6.

**[OPEN]** Sebastos calls this location "the mega-spaceship"; the prototype and vladastos's doc
call it "the mothership." Same concept, two names — pick one before writing further docs. This
document defaults to "mothership" since that term is already established in the prototype's own
docs (`CLAUDE.md`, `info/game_design.md`) and in earlier design-overview work; changing it later
is cheaper than reconciling it against existing prototype terminology now.

---

## 6. Meta-progression

Five pillars, all mothership-anchored:

1. **Ship building**
2. **Resource refinement**
3. **Crafting**
4. **Run planning**
5. **Trading** (§7)

### 6.1 Ship building

The ship is the primary long-term build target — the thing all other pillars ultimately feed.

- **Blueprints** define a ship's polygon/cell-formation shape and a set of predefined structural
  roles that can't be changed after the fact (matches the prototype's compound-cell body model).
  Within that fixed shape, some slots are generic — the player personalizes the ship by assigning
  weapons and skills (as Modules, §4) to those free slots. Implicit stats can also be partially
  fixed by blueprint and partially procedurally rolled per instance (Diablo-style), so two ships
  of the same blueprint are recognizably the same "class" but not identical.
- **Skill tree**: partially predefined (blueprint identity), partially randomized per instance.
  The primary *per-run* progression mechanic — spent/activated across a run — though the tree's
  *shape* (what's available to spend into) is a persistent asset tied to the ship instance.
  **[OPEN]** does per-run tree investment reset every run, or partially carry over across runs
  the ship survives? See §11.
- **Unique skills**: each blueprint has a signature ability unavailable elsewhere (time-freeze,
  black-hole, etc.) — the headline reason to pick one blueprint over another.

#### 6.1.1 Ship acquisition, and what death costs

**[RESOLVED — sebastos's explicit rule, adopted; it answers an open question the vladastos doc
had left unresolved.]**

- **Baseline ship**: free, always available, no acquisition gate. Its quality ceiling is
  raiseable via meta-progression investment, but must never close the gap with premium ships —
  it's the fallback, not a competitive alternative.
- **Advanced ships**: first *discovered* (found/unlocked through run content or
  meta-progression), then **built once**, consuming crafting materials (§6.3) — not summoned
  fresh each run for free. Once built, a ship is a standing inventory asset the player can pilot
  across multiple runs.
- **Dying in a run destroys the ship you're piloting, permanently, along with every Module (§4)
  attached to it.** This is the sharp edge sebastos's doc adds that vladastos's left as an open
  "is the ship itself at risk" question — adopted directly: it's real permadeath for the ship
  and its full loadout, not just for the current run's unbanked loot. This makes "which ship am I
  willing to risk on this run" a first-class decision alongside "how deep do I push."
- This reframes the earlier "crafted per use" language from the vladastos draft: a ship isn't
  re-crafted trivially every run, but building an advanced ship at all is still the primary
  long-term resource sink, because ships are a consumable *asset* (destroyed on death) rather
  than a one-time unlock.

#### 6.1.2 Weapons

Ships have a variable number of weapon slots (a per-blueprint stat), filled with weapon Modules
(§4) — predefined base stats plus a procedurally generated affix component, same pattern as
ships one level down.

### 6.2 Resource refinement

Raw resources extracted from runs (variant-specific planet resources, asteroid/meteor materials)
feed a refinement/smelting step before they're crafting-ready — likely where rarity tiers get
established/upgraded. Raw resources, refined resources, and finished crafted goods are three
distinct tradeable tiers — see §7.

### 6.3 Crafting

Turns refined resources into ship instances and Modules. Gated by discovery (a blueprint being
"discovered" is what unlocks its recipe) and by having the refined resources to pay for it.

### 6.4 Run planning

Pre-run setup at the mothership: choosing a ship/loadout, and applying **galaxy-level modifiers**
before departure — analogous to PoE1 scarabs / PoE2 tablets, biasing what that galaxy's generated
content produces (which planet variants appear, boosted drop rates, extra elites/bosses, raised
risk for raised reward). Applied to a specific atlas node (§3.1) before entering it. The natural
place to hang meta-quests spanning multiple runs (Gungeon-style) — a quest might specifically
require running a modifier that biases toward ancient-ruin planets.

*(Named "run planning," not "expedition planning," to avoid collision with §8's Expeditions — a
separate, later-game system that dispatches ships without the player piloting anything.)*

---

## 7. Trading

**[RESOLVED — scope correction.]** Trading is **not** a simulated economy. Its earlier framing
(three tradeable tiers, per-settlement stock, supply/demand drift, regional price arbitrage) has
been dropped — it was scoped far past what the feature is actually for. Trading exists for one
reason: to give the player a concrete incentive to detour off the atlas's critical path and
explore galaxies they'd otherwise have no reason to visit.

**Mechanic: the Trade Hub.** A subset of settlements attached to atlas galaxy nodes (§3.1) are
Trade Hubs, reachable directly from the mothership without a run in progress. Each hub has NPCs
posting **orders** — standing requests for a specific item (a raw resource, a refined resource,
or a crafted good/Module, sometimes with a rarity or affix requirement), not a general buy/sell
market:

- **Fulfilling an order** means delivering the exact requested item; in return the player gets a
  payout — currency, reputation, or something not obtainable any other way (a blueprint
  discovery, a unique Module, a run/galaxy modifier). The reward for reaching a hub is the point;
  there's no ongoing price-arbitrage loop to play.
- **No stock, no dynamic pricing, no background economic simulation.** Orders are discrete and
  either fulfilled or not; a hub's order board refreshes periodically rather than tracking
  per-good inventory or simulated supply/demand.
- **Orders are the lever that rewards exploring off the main path.** A hub can request an item
  variant-locked to a specific, distant galaxy (e.g., a relic only found on ancient-ruin planets
  several hops away) — giving the player a concrete reason to seek out and run a specific
  far-off galaxy rather than always taking the shortest/safest path through the atlas. Hubs
  further from the mothership, or off the atlas's critical path, can offer rarer requests and
  better payouts specifically to justify the detour (§3.1).

This intentionally keeps trading small in scope: it's an exploration-incentive layer bolted onto
the atlas, not a second economy game running alongside the ship-building loop.

---

## 8. Expeditions (late-game system)

A distinct system from a normal run — the player pilots nothing, and there's no death/loot-loss
risk in the run-extraction sense. Instead, the player dispatches **spare ships** from their fleet
on unmanned missions to gather resources, unlocking once the ship-building/crafting loop has
produced enough surplus to spare a ship for it.

- **Destinations are atlas galaxies (§3.1), not solar systems.** This follows directly from §3:
  solar systems are ephemeral, regenerated fresh per run, so there's no stable address to send an
  unmanned mission to. Galaxies are persistent, so "send a ship to galaxy X, known to be rich in
  resource Y" is a legible, informed choice, backed by what the player has already learned about
  that galaxy from running it or from a Trade Hub order requesting that resource (§7).
- **Not player-piloted**: queued, resolves later, checked back in on rather than played live.
  Duration scales with the target galaxy's atlas distance/danger.
  **[OPEN]** what "duration" is measured in isn't decided. Two options:
  - **Real/wall-clock time gating** — an expedition resolves after N real minutes/hours, PoE2-
    league or mobile-idle-game style. Simple to build, but risks incentivizing the player to
    just leave the game open/check back periodically rather than play — exactly the passive
    "waiting for a timer" pattern worth avoiding.
  - **Action-based time units** — duration measured in player actions instead of wall-clock time
    (e.g., "resolves after the player completes N more runs," or after N systems/galaxies
    cleared). This ties expedition progress to actually playing rather than to real time, and —
    if the same action-based unit is used elsewhere (e.g., Trade Hub order refreshes, §7) — gives
    the whole meta-game a single consistent "clock" driven by play instead of by the wall clock.
  Leaning toward action-based for the reason above, but not decided; see §11.
- **Risk differs in kind from a run's**: an expedition can fail or come back
  damaged/diminished — losing the ship and/or crew, or a reduced haul — rather than threatening
  the player's *current run's* loot. A resource/ship sink with favorable-but-not-guaranteed
  expected return, not an extraction-stakes decision.
- **[OPEN]** Expeditions consume fleet ships, which raises a direct tension with §6.1.1's "dying
  destroys the ship" rule for the player's own piloted ship — is an expedition ship the same
  built asset a player could otherwise pilot (real opportunity cost, thematically tighter), or a
  separate, cheaper unmanned-hull tier built specifically for this (safer for balance, avoids
  expeditions and piloted runs competing for the same scarce ships)? Leaning toward the second
  option for balance, but not decided. See §11.

**Design intent:** the idle/passive counterweight to a run's moment-to-moment extraction tension —
keeps the economy and resource curve moving between runs, and gives the fleet a second, lower-
stakes use beyond being piloted.

---

## 9. Extraction stakes and rarity

- **Procedural loot/resources of varying rarities** — applies to everything extractable: planet
  resources, weapon/skill Module affixes, item rolls, run/galaxy modifiers.
- **Death costs two things, not one**: the run's accumulated, unbanked loot (§1), *and* — per
  §6.1.1 — the piloted ship itself, plus every Module attached to it. Items (§4) are already
  forfeit at run-end regardless of survival, so they're not part of this calculus.
- **[OPEN]** Is *all* of a run's unbanked loot at risk on death, or is progress banked per cleared
  system within the galaxy (e.g., loot from already-cleared systems auto-banks, only the
  in-progress system's take is at risk)? This materially changes how punishing/how frequently
  retirement is expected, independent of the now-resolved ship-loss question. See §11.
  (Expeditions, §8, are exempt from this — their risk is fleet/resource loss, not run-loot loss.)

---

## 10. Relationship to the prototype

`rusteroids-on-steroids` currently implements a **self-contained wave-survival arcade mode**: one
map, escalating waves of asteroids/aliens, a wave-10 mothership boss, then endless mode. It has
none of the layer structure, meta-progression, or run/extraction concepts above yet — its own
`info/game_design.md` should be read as **the current implementation target for the planet-layer
combat loop**, not a competing vision.

Concretely:

- The prototype's destruction engine, movement, weapons, and wave/spawn-budget system are the
  direct foundation for **planet-layer encounters** (§5.2) — an individual planet's "sequence of
  encounters" is plausibly a sequence of prototype-style wave beats, and the prototype's wave-10
  mothership is a reasonable template for a planet's mini-boss or a system boss. The world's
  existing vortex/orbit force field is a reasonable candidate for solar-system-layer asteroid
  placement too.
- The prototype's `docs/spec.md`, `docs/physics_spec.md`, `docs/fracture_spec.md`, and
  `docs/destruction_engine_spec.md` remain the authoritative low-level specs for the shared
  combat core across layers — this document doesn't re-derive them.
- The prototype's structural "Cell" terminology (hull pieces) is retained; this document's
  meta-progression item was renamed to **Module** to resolve the collision (§4).
- Not yet represented in the prototype at all: the solar-system layer, planet variants, the atlas
  graph, per-run system graphs, the mothership meta-base, ship blueprints/skill trees/crafting,
  the trading economy, expeditions, and the extraction/loss-on-death rules. This is the gap
  between "current prototype" and "the game described here."

---

## 11. Open questions

Carried over, resolved, or newly introduced by this reconciliation:

- **Cross-galaxy run continuation** (§3.2): can a single run push from one galaxy's final system
  directly into a connected galaxy, or does clearing a galaxy always return the player to the
  mothership?
- **Mothership vs. mega-spaceship naming** (§5.4): pick one term.
- **Extraction granularity** (§9): is all unbanked run loot at risk on death, or is it banked
  incrementally per cleared system? Probably the highest-leverage remaining question — it shapes
  pacing for every other system.
- **Skill tree persistence** (§6.1): does per-run skill-tree investment fully reset each run, or
  partially carry over per ship instance across runs it survives?
- **Expedition ship sourcing** (§8): do expeditions consume the same built ships a player could
  otherwise pilot, or a separate cheaper unmanned-hull tier?
- **Expedition time unit** (§8): should expedition duration be gated by real/wall-clock time, or
  by an action-based unit (runs completed, systems/galaxies cleared)? Wall-clock is simpler but
  risks rewarding idly waiting/checking back rather than playing; an action-based unit avoids
  that and could double as the refresh clock for Trade Hub orders (§7) too, giving the meta-game
  one consistent pacing mechanism instead of two.
- **Boss-finding on the solar-system layer** (§5.1): does the player need full exploration to
  locate the system's objective, or are there in-run signals (scans, distress calls, a "getting
  warmer" mechanic) so it doesn't become tedious backtracking as systems get larger?
- **Planet layout generation** (§5.2): concrete algorithm target for "organic but not open" — a
  branching critical path, a hand-authored room graph with procedural dressing, or something
  else? Large implication for planet-layer content-authoring workload.
- **Discovery mechanic for advanced ships** (§6.1.1): what specifically constitutes "discovering"
  a blueprint — a run-content drop, a meta-progression research/unlock spend, or both?
- **Trade Hub order tuning** (§7): how orders are generated and refreshed (per-hub timer? tied to
  atlas progress?), and how payout/rarity scales with a hub's distance/danger, still need concrete
  numbers — the mechanic is scoped now, but not tuned.
- **Items-vs-Modules power balance** (§4): sebastos's items are explicitly "not overpowered" while
  allowing rare crazy combinations, and modules are explicitly allowed to be strong — worth a pass
  once itemization is prototyped to make sure the two categories stay visually/mechanically
  distinguishable to the player, not just distinguishable on paper.
