# Asteroids on Steroids — Game Design

Living design document. Iterate freely — nothing here is final.

---

## 1. Concept

Single-player, arcade, wave-based survival with a win condition. The player pilots a ship in a
fracturable asteroid field that escalates over ~10 waves into a mothership boss encounter.
Destroy the mothership to "win" the run; an endless mode continues afterward with faster scaling.

Core loop: manage your ship's structural damage, clear waves efficiently, survive long enough
to face the mothership. The destruction engine is the feature — every asteroid dies differently,
fragments become new hazards, chain reactions cascade.

---

## 2. World & Camera

**World size:** 3840 × 2160 (8× the reference 1920×1080 window). Fixed regardless of actual
window size. May be expanded after playtesting if it feels cramped at peak density.

**Camera:** follows the player with smooth lag (lerp toward player position). Biased slightly
toward the mouse cursor so the player can see ahead of their aim. Never shows outside world bounds.

**Boundary — soft repulsion field (primary):**
A 200 px zone at each edge applies an inward exponential force to everything that enters it:
`F = k · e^(depth/scale)` pointing toward the world center. Negligible at the edge, strong when
deep. Feels like an invisible elastic wall. Applies to asteroids and aliens too, so debris
naturally drifts back toward the play area over time.

**Boundary — repulsive anchor ring (alternative, more physical):**
Place N invisible repulsive point masses arranged in a ring just outside the world boundary.
Each repulsor pushes nearby objects away from itself (anti-gravity: `F = −G·m / d²`).
An object near the center has all pushes cancel to near-zero. An object drifting toward one
side gets pushed back by the nearby repulsors. Objects can briefly penetrate the ring but
are continuously pushed back. Optionally render faint "warning" glows around each anchor.
Both approaches produce the same player experience; the field is simpler to implement.

---

## 3. Player Ship

**Structure:** 6–8 compound cells, each with a specific function. Destroying a cell removes
that function for the rest of the run. No HP bar — damage is structural and visible.

| Cell | Function | Effect if destroyed |
|------|----------|---------------------|
| Cockpit (1) | Run condition | Ship destroyed → run over |
| Propellers (2) | Thrust and rotation | Lose one → reduced thrust/turn rate; lose both → no thrust |
| Cannons (2) | Primary fire | Lose one → halved fire rate; lose both → no shooting |
| Bumper (1–2) | Tough armor cells, high toughness, low blast | Absorbs impacts before reaching vital cells |

The bumper cells have higher toughness than the rest — they're the front shield. The cockpit is
tucked centrally. Propellers and cannons are on the outside and more exposed.

**Cell repair / salvage:** Aliens occasionally drop one of their cells as a drifting pickup when
destroyed. The player flies over it to attach it to the nearest open bond site on their hull.
An attached alien cell restores the function of the slot it fills (a dropped alien cannon cell
restores cannon fire; an alien engine cell restores a propeller). Alien cells may have slightly
different visual properties but identical mechanics.

**Movement:** thrust-based with deliberate inertia, responsive feel.
- Strong thrust force, low drag: you keep drifting but can counter quickly.
- Rotation: mouse-aimed. The ship always points toward the cursor; strafing is natural.
- "Snappy but with lag" = high thrust + low drag + soft speed cap.

**Death:** run ends when the cockpit cell is pulverized or detached. No respawns.

---

## 4. Weapons

All weapons use the fracture engine — only `WeaponProfile` and physics behavior differ.

**Cannon (default, unlimited ammo)**
Standard fast bullet (physical projectile or raycast). High `Directionality`. Good against
large asteroids at any range.

**Shotgun (limited ammo)**
N fast raycast rays in a spread cone. At short range most rays land on the same cluster; at
range only 1–2 connect. Primary use: clear a cloud of small debris cells in one shot that
would each need a separate cannon round. Implemented as raycast (not physical pellets) to
avoid adding to entity count.

**Piercing round (limited ammo)**
A heavy physical projectile. Very high mass relative to asteroids — the reduced-mass formula
naturally limits how much the bullet slows per impact: against light debris `mRed ≈ mDebris`
(bullet barely slows); against a boulder `mRed ≈ mBullet` (bullet absorbs the reaction and
stops). The bullet ignores or greatly reduces velocity changes perpendicular to its travel
direction (lateral impulse clamped) so it doesn't deflect off glancing hits — it bores
straight through and keeps fracturing along the path until something massive stops it.

**Grenade (limited ammo)**
A slow physical projectile. On contact or after a short fuse, it explodes: spawns a burst of
N fast, weak shrapnel projectiles in a ring (`BeginFracture` calls on everything each shrapnel
hits). Radius ~150 px. Good for clearing clustered debris or triggering asteroid chain reactions.

**Ammo:** Cannon = unlimited. Special weapons share a pool of charges replenished by alien drops
and occasional pickups. Starting loadout: Cannon only; Shotgun and Grenade unlocked early via
wave progression or salvage.

---

## 5. Skills

| Skill | Effect | Cooldown |
|-------|--------|----------|
| **Dash** | Instant velocity spike in current facing direction. Brief invincibility window — the ship passes through debris without taking structural damage, but still applies fracture impulses to anything it contacts (the ship rams through). | Short |
| **Turbo** | Multiplies thrust force for ~2 seconds. Useful for crossing the large world quickly or escaping a collapsing debris field. | Medium |
| **Slow-mo** | Scales world physics timestep to ~0.3× for ~1.5 seconds. During this window the ship's angular drag is reduced and turn torque is increased, giving the player finer directional control at near-normal responsiveness even though the world moves slowly. The point is not reaction time — it's precision: navigating through tight debris fields, aiming at specific cells, threading the gap between two spinning boulders. | Long |

---

## 6. Asteroids

| Variant | Properties | Introduced |
|---------|------------|------------|
| Standard | Baseline rock. Medium toughness, medium speed. | Wave 1 |
| Gravel | Small (6–8 cells), brittle, fast. Spawns in clusters. | Wave 1 |
| Spinner | Medium size, high angular velocity (±6–10 rad/s), medium toughness. | Wave 2 |
| Boulder | Large (20–30 cells), very high toughness, slow. | Wave 3 |
| Armored | Metal material. High toughness, low blast fraction. Resists vaporization. | Wave 5 |
| Unstable | Low toughness, high KineticFraction. First significant hit causes all fragments to fly far and fast. | Wave 6 |

---

## 7. Wave System

**Pacing:** ~60 seconds per wave. A new wave triggers either on the timer OR when the total
area obliterated in the current wave crosses a threshold (the player is rewarded for clearing
fast). A 5-second gap between waves serves as a brief repair/pickup window.

**Entity cap:** a hard cap on live entities (asteroids + fragments + aliens + projectiles).
When the cap is reached, new wave spawns are deferred until entities fall below it.
This creates a gameplay incentive: clear debris aggressively to let the next wave fully arrive.

**Spawn budget:** each wave has a point budget. Variants cost different amounts. The wave
director assigns a bias profile that shifts toward harder variants as the run progresses.

| Variant | Budget cost |
|---------|-------------|
| Gravel | 1 |
| Standard | 3 |
| Spinner | 4 |
| Unstable | 5 |
| Boulder | 8 |
| Armored | 10 |

All wave spawns happen at once at wave start, spread over a few frames to avoid a single-frame
spike. Spawn placement: outside the visible viewport, inside world bounds, never overlapping
existing bodies.

**Predefined deterministic waves** (override the budget system for variety):

| Wave | Theme | Budget override |
|------|-------|-----------------|
| 1 | Tutorial density — sparse Standards and Gravel | Low |
| 2 | First spinners — several Spinners mixed with Standards | Medium |
| 3 | Boulder wave — 3–4 Boulders plus Gravel filler | High |
| 5 | Debris storm — dense Gravel horde (high count, low budget cost) | Very high count |
| 7 | Armored field — mostly Armored, few Standards | Medium |
| 9 | Chaos wave — mix of Unstable + Spinners, few Boulders | High |
| 10 | **Mothership** — see §9 |

Waves not listed above use the budget + bias system.

**Bias progression:**
- Waves 1–3: mostly Gravel + Standard
- Waves 4–6: Spinner and Unstable increase
- Waves 7–9: Boulder and Armored dominate
- Post-boss endless: budget grows rapidly, all variants active, shorter wave timer

---

## 8. Aliens

Introduced starting wave 4, increasing frequency thereafter.

**Drone**
8–12 cells, elongated body. Fractureable (same engine as asteroids).
AI: **context steering** — each tick, cast N rays in a circle; weight each direction by
(attraction toward player) minus (repulsion from nearby asteroids and other entities). Steers
toward the highest-weighted direction. Naturally navigates around debris fields without
pathfinding. Fires at the player (with aim lead) every 2–3 seconds when in range.
Drops: 1 cell pickup occasionally.

**Bruiser**
20+ cells, wide body, high toughness. Fractureable.
AI: simple pursuit — thrust directly at player, ignores obstacles. The mass and momentum are
the weapon; it plows through lighter debris naturally (the fracture engine handles what it breaks).
Drops: guaranteed cell pickup.

**Alien bullets:** physical projectiles, same fracture system as the player's cannon. They can
accidentally hit asteroids and trigger chain reactions.

---

## 9. Mothership (Wave 10 Boss)

Large preconstructed compound body (40–60 cells). Has distinct functional regions:
- **Core**: destroying it ends the fight
- **Shield cells**: high-toughness outer layer protecting the core
- **Weapon turrets**: actively fire at the player; each turret is a named cell group

The mothership moves slowly, orbiting the world center. It spawns Drones periodically (less
frequently as its weapon turret cells are destroyed). Destroying all turrets disables spawning;
destroying the core ends the run as a win.

**Win state:** run ends with a "cleared" flag and the full score recorded. The local leaderboard
distinguishes cleared runs from survival runs.

**Post-boss endless mode:** continues from the same session. Waves restart from wave 1 scaling
but the budget multiplier doubles each wave (much faster escalation). No further win condition —
pure survival score.

---

## 10. Environmental Anomalies *(deferred)*

Gravity wells, repulsor pulses, drag/spin fields, debris trails. Planned for after core gameplay
is stable. Refinement postponed.

---

## 11. Damage & Health

Driven by the collision impulse the engine already computes:
`eImpact = ½ · mRed · vRelN²`

- Below threshold: no damage (gentle graze).
- Above threshold: bonds in the struck cell region weaken proportionally to `eImpact`.
- Visual: cracks appear on the hit cell before it detaches.
- Cell detaches when its absorbed energy crosses the material threshold.
- Run ends when the cockpit cell is gone.

No HP number. Damage is fully structural and visible.

---

## 12. Scoring

| Source | Points |
|--------|--------|
| Asteroid cell destroyed | Cell area × material toughness multiplier |
| Asteroid fully cleared | Bonus ×1.5 |
| Kill chain (N bodies in short window) | Stack multiplier up to ×4 |
| Alien Drone killed | Flat + time-alive bonus |
| Alien Bruiser killed | 3× Drone flat |
| Mothership core destroyed | Large flat bonus |
| Wave survived | Points per second × wave number |

**Leaderboard:** local top 10. Each entry: score, wave reached, cleared (yes/no), run duration, date.

---

## 13. Menus & Settings

**Main menu:** Start run, Leaderboard, Settings, Quit.

**Settings (persistent):**
- Audio volume (placeholder until audio is added)
- Window resolution / fullscreen
- Key bindings

**Run settings (per-run, shown before starting):**
- Starting wave (for practice — wave 1 to 5)
- Entity cap (performance tuning)
- Anomalies enabled/disabled (once implemented)

No tutorial screen at MVP — the ship structure and weapon labels are visible in the HUD.

---

## 14. Vibe & Feel Targets

- **Chaos that reads.** At peak intensity dozens of moving, spinning, cracking objects fill the
  screen. Large = slow and heavy; small debris = fast and sharp. Player needs to read trajectories.
- **Weight.** A boulder should feel massive. A spinner should feel dangerous even if small.
- **Reward pattern recognition.** The physics is deterministic enough that a skilled player
  learns to predict chain reactions, use the grenade to trigger cascade events, aim at structural
  cells of the mothership.
- **Death should feel fair.** The structural damage system makes it visible: the player can see
  their ship degrading. Death should be the result of a readable mistake, not randomness.

---

## Open Questions

- Alien cell drops: random chance or guaranteed from Bruisers only?
- Does the Piercing round have a max pierce count, or does it run purely on momentum until it stops?
- Slow-mo: is there a visual cue (desaturation, blur) or purely a speed change?
- HUD: should individual ship cells be shown on a mini-diagram so the player knows which
  functions are still intact?


## New notes that may change other parts

A typical run lasts around 12 minutes, at 10 minutes, independently on how many waves the player cleared, the mothership spawns. Special waves (explicit waves) spawn at fixed points during the game. After the boss fight endless mode starts, with fast scaling of waves.

### World

The world is 5760x3240. All asteroids "orbit" around the center and are pulled laterally and back into the center exponentially more the greater their distance from the world center. This would give movement to asteroids and make them concentrate in the center of the map.
This would be just a system that applies forces to all bodies in the game based on their distance from the center of the map.
The border of the map has a flat bounce as fallback for when objects reach it, shouldn't happen normally though.

### Waves

Max live cells: as the game progresses, the cap on the number of live cells raises, so the later waves are far more chaotic than the beginning. Start with 300 max live cells, increment at the end of each wave by around 30. No more than 2000.

Wave budget: asteroid types have a cost, multiplied by their "size_multiplier". Incremented at the end of each wave.

WaveSystem: has internal timer. every 5 seconds or so, check the number of current live cells -> if <= 30% current_max_cells: spawn_new wave and reset timer. 
if 30 seconds passed from last wave spawn: spawn new wave and reset timer.

After every spawn raise wave_budget and max_live_cells

SpawnWave: the most difficult and flexible part. 
1. Choosing the asteroids: based on how much time has passed since the beginning of the run, the pool of available asteroids/aliens is increased.
2. Each asteroid/alien type has a cost, paid with the wave budget. For asteroids there is a "size_multiplier" (or something like that) that multiplies its area and number of vertices and cells. It increases its cost accordingly.
3. Bias when choosing: the wave manager must maximize the wave cost, the count of new asteroids and new cells. Constraints: wave_budget, current_max_cells, wave_count_max. Need to also guarantee variety in some way. 
4. Spawn position: For each asteroid select a position: close to the border (so the orbit system gives him movement), strictly outside of the camera view, no other body collides with it.
5. Special waves: explicit waves are based purely based on how much time has passed from the beginning of the run:
Something like: around 3 minutes in the first horde wave spawns, 5 minutes in a large group of heavily spinning small asteroids, at 7 minutes a big group of alien ships spawn all together, at 10 minutes the mothership spawns.

### Upgrade system

We can stub this one for now.
At the end of each wave (beginning of a new one) the game pauses and the user can choose an upgrade chosen at random from a pool (faster fire rate, more energy, new weapons, ammo for some weapon etc.). This will give a feeling of progression to the player and enables various "builds".

### Open questions answered
1. Player death ends the run, simple as that.
2. The vortex force field with quadratic and a distance cap. 
3. The camera system was already in the game design doc, it is essential for a game where the world is not just the visible screen. What's the design decision here? A camera following the player with some lag to feel the acceleration.
4. The wave system polling isn't important, an "artificial dead zone" shouldn't be a problem since the player doesn't know if there's asteroids outside his screen too far from him, and they spawn outside the view either way.
5. Aliens will be introduced once the whole world feels right.

### Budget estimates

Suppose we have a table of costs as follows:

| Variant | Budget cost |
|---------|-------------|
| Gravel | 1 |
| Standard | 3 |
| Spinner | 4 |
| Unstable | 5 |
| Boulder | 8 |
| Armored | 10 |

The unlock timing is:
0 min: gravel, standard.
3 min: spinner.
4 min: unstable.
6 min: boulder.
7 min: armored.

Size multiplier based on area of the shape: a gravel asteroid double the default area will cost 2 instead of 1. Cell number should scale with size to maintain the same grain size independently on the size multiplier.

At wave 1 I expect around 10/15 asteroids of varying sizes: suppose 7 gravel and 5 standards with an average size multiplier of 1. That's a budget of 25. 

At wave 10 (around 4 minutes in): 20/25 asteroids spawn, suppose: 10 gravel, 7 standard, 4 spinners, 3 unstable, with an average size of 1.5. That's a budget of around 75.

+5 to the budget at each wave should work fine for a start.


### To do

No more tuner: everything synced with game_config file. Lots of more hardcoded parameters need to be there.

Make the map bigger? 

Health system, new weapons, new skills.