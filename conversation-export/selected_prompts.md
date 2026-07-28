# Selected prompts — technical depth & design decisions

Total of more than 400 prompts, I selected the most meaningful ones and divided them in 2 categories: "Technical design & physics decisions", "System & content design decisions"

---

## I. Technical design & physics decisions

### 1 — Engine/game responsibility split 

> Subtracting the kinetic energy that fragments fly away with is definitely something we should look into.
>
> Now the model feels exactly like I envisioned. Let's think about what's missing before writing a spec file of the new features for the engine, the new model and the new logic of the fracturing. Some features are still missing in the engine (fixed-time frames and others), make a list of these so I can confirm and we can advance to writing the spec. An important aspect to keep in mind is that the engine should support different UIs, it should definitely support WinForms as a future extension.
>
> Let's plan this out carefully. My 2D game engine's character is specialization in destruction physics; even so, maybe not everything we discussed belongs directly in the engine, some parts may be game-dependent. How would organize these features and responsibilites?

### 2 — Fracturing model refinement

> What would happen to the energy still in the cells/bonds of a chunk that detaches? Is it discarded or transformed into velocity?
>
> Why not spawn new entities at each fragment propagation iteration with their own fracture_front built with the energy still in their cells? That way we wouldn't have the problem of different behaviour if the chunk had stayed stayed attached.
>
> Is the problem just the high cost of rebuilding/remapping the fracture state at each iteration? Explain this to me and give a honest opinion on the whole thing: does it make sense to put effort into this feature, what would the cost be in terms of project structure/complexity, future extendability and whatever you feel like deserves my attention.

### 3 — Multi-frame crack propagation design

> Now it works as I wanted. Let's move on to the last pre-game phase: multi-frame crack propagation. Let's do some planning before implementing.
>
> I was thinking that the speed at which the fragmentation happens, frame-wise, should be configurable: how many steps every iteration and the number of frames between iteration - I want to test cracks propagating slower than one step each frame.
>
> It should be a different system/component pair, I think we should keep the current implementations in case I'll decide to go back to this approach.
>
> I'm wondering about what should decide the speed of the propagation and who should own the parameters influencing it.
>
> I also wonder about how this multi-frame should handle hits happening during the propagation process (what if a hit from the opposite direction with respect to the first hit happens some frames later).

### 4 — Engine tweaks

> Alright. Let's plan the last tweaks on this system before moving on to the game design. Here's the last things. Give me some honest feedback on them and lets plan this carefully before implementing.
> Game engine last tweaks:
> 0. Directionality based on the spin and direction of impact on asteroid-on-asteroid fracture events (new tunable parameter).
> 1. Cell separation in their own subgraphs when they absorb a lot of energy but not enough to complitely vanish. The absorbed force should then be propagated inside the subgraph from the same direction as the initial hit.
> 2. Proper debris: when a cell vanishes, it should be split into a number of convex polygons (with simple cuts): these will be the debris particles. They do not have a collider and have TimeToLive component. They progressively fade.

### 5 — One realistic energy model for every collision type (02)
*Insists the model capture angular velocity and generalise across bullet/asteroid/ship collisions; questions whether the threshold cap is still needed.*

> Agreed on all points.
>
> The only thing I'm still not sure about is the energy model. I want it to capture the angular velocity, take speed and mass into account. Is it possible to have a physics realistic energy model that could adapt to both the bullet-asteroids collisions and the asteroids-asteroids / ship-asteroids collisions?
> The threshold cap was there just to not trigger a fracture at every collision, especially if its effect would be negligible. Maybe we don't need it anymore.

### 6 — Procedural asteroid generation design

> Let's continue this plan, I have other things I want to change.
>
> Rework the procedural asteroid builder. The seedClusterCenter parameter shouldn't be removed, the seeds should always be distributed uniformly in the asteroid and the number should be based on the material. Instead introduce clusterCount, clusterStrength and clusterCentrality parameters. Here's the explanation: clusters are chosen seeds that have a higher density and bond multiplier, that propagates to the neighbors.
> 1. clusterStrength is the budget to distribute bond and density multipliers with a bf visit of the graph starting from a seed. It spreads based on the distance of the cells.
> 2. clusterCentrality governs how close to the center the cluster seeds are chosen.
> What do you think of this design?
>
> A rework of the shotgun and the grenade: These kinds of bullets should have some variations: different ttls, different speeds, the radius shouldn't be evenly covered by the bullets (not completely random either), there should also be some drag on these bullets so that they really do more damage in close range. What do you think?

### 7 — Asteroid procedural generation refinement + bug 

> Yes, the current procedural clusters need better parameters to describe them. The bondGain, DensityGain and BlastGain need to be separate parameters as well as clusterSpread, which should be a range of percentages of the radius of the asteroid and not absolute. I don't get what you mean by "Armor blast-resistance (B2): I folded blastResist into the cluster spread so surface clusters resist vaporizing too — tell me if you'd rather drop blastResist entirely or keep it as a flat per-material value."
> The drag should be a responsibility of the RaycastBulletSystem, since it conceptually belongs there and isn't really a separate concern, so no new system needed.
> There is also a bug when the bullets shot by an alien instantly collide with their body, it needs a grace period just like the piercing round to guarantee it escapes the body that spawned it.
> I would also appreciate an explanation of all the parameters that influence the fracturing/vaporization process (blastResistance, bondStrength, etc.), how they are computed, how they change, what's stored in the cells, how the energy traverses the graph and why. You can write this in a separate file in the info directory.

### 8 — Restating the model as a verification technique

> Ok, let's see if I got the fracture model correctly, please correct me where I'm wrong and be precise.
> At impact, 3 things are computed:
> Impulse J, which decides how much the two objects get pushed in the opposite direction of the impact.
> energy E, the budget of the fracture front that traverses the graphs.
> stress that decides whether a crack starts. It depends on the impulse relative to the area of the impacted *cell*, not the whole body, and on the concentration.
>
> The budget of the crack depends purely on the SurfaceFraction and the Energy.
>
> Propagation is the trickiest part. There's CrackSteps each CrackFrames frames (global fracture config), and each step only one cell (the highest-energy) propagates its energy to its neighbors. If the amount of energy passed to the neighbors is greater than the bondStrength (with the spin-prestress included) the bond breaks and the bondStrength is subtracted to the passed energy. The energy isn't distributed among the neighbors, each neighbor gets the full amount.
>
> Vaporisation, during the fracture step, if the energy delivered to a cell is greater than its threshold (which depends on its BlastResist and BlastFraction of the crack, set by the weapon) it gets vaporised.

### 9 — Piercing round design

> The piercing just isn't working as intended, it sometimes doesn't even trigger a fracture event when hitting something with its pointed head, while it can deal massive damage if it hits something sideways, with more surface colliding. What does surfaceEnergyPerLen do?
>
> Before choosing one of the 2 fixes, I would like to review some things:
> 1. Per cell stress/damage accumulation should definitely be reintroduced, alongside the bond accumulation.
> 2. In your comparison you take the whole asteroids mass and compare it to the strength of a single bond, considering there are ~100/300 bonds in a 30-cell rock they should have the same scale, am I wrong? If not, the bond strength should just be increased to be on the same scale as the density.
> 3. The BlastFraction should be taken from the local energy per-cell, not from the total budget. We introduced this to fix the edgecase of brittleness = 1. In the old model, the outward energy portion was a lerp(0.1,0.96,brittleness). That should work, what do you think?
> 4. When something vaporises, shouldn't there be some energy loss (apart from the energy needed to vaporise the cell itself)? Right now, setting the BlastFraction to 1.0 makes the fracturing completely ignore the material brittleness, although I think this would be fixed by implementing the previous point i made.
>
> What do you think of this? Do you agree, would you do it differently? How does real physics work in this case?

### 10 — relativeVelocity bug-solving + piercing round

> I did some tuning on the material and weapons parameters, however the tuning tasks shouldn't be considered done yet. Here are some other things to adjust/add to the plan.
> There is a problem with the computed energy from collision among fracturable bodies and it's almost certainly caused by the relativeVelocity computation. I noticed that the piercing round, even while traveling at a very high speed (2500 in the game_config) and having a respectable mass, sometimes generates very little energy (~100 or less) and bounces off of the impacted asteroid.
>
> Does the system run before the CollisionSystem changes velocities? Does the relative velocity take into account the angular velocities of the bodies and the lever length?
>
> Regardless of this bug, this is what I would do:
>
> Refactor of Fracture OnCollision, BeginFracture and ComputeEnergy
> The clean fix is to change all three together:
>
> OnCollision computes the true contact relative velocity and normal speed.
> BeginFracture accepts the normal impact speed (or directly the impact energy) instead of an impactor velocity.
> ComputeEnergy uses that speed without reconstructing velocities.
>
> That produces a much simpler and less error-prone design, but it requires coordinated changes across all three functions.
>
> The fact that OnCollision method of the FractureGameplay handles 3 unrelated behaviours (grenades, asteroid-on-asteroid, piercing round) isn't really SOLID. I'd split it like this:
>
> GrenadeSystem — detonate grenades on contact.
> ProjectileSystem — handle piercing-round impacts, projectile-specific fracture, ricochet, penetration, velocity clamping, etc.
> FractureCollisionSystem (your current OnCollision) — handle collisions between ordinary fracturable rigid bodies and compute impact energy.
>
> That separation follows the single-responsibility principle and makes each system much easier to reason about and extend.
>
> There are also some visual bugs:
> 1. On fracturing, sometimes cracks appear and disappear some frames later, this shouldn't be possible since cracks are rendered only when a bond is broken, and that's irreversible.
> 2. The player shape HUD in the game, the cells should gradually become orange then red based on the damage absorbed by the cells, but this never happens.

### 11 — Piercing refinement sprint
> New sprint:
>
> Piercing round refinement:
> the fact it can pierce through even if its force isn't enough to fracture the asteroid, sometimes the fracture starts inside the asteroid, not on the surface which remains intact. Ideas?
>
> Cells don't inherit stress on split
>
> Visual indicators have to be reworked, border should "eat" projectiles instead of pushing them back.
> The vortex visual indicator shouldn't be a static spiral moving with its center, I was considering these ideas:
> 1. Some sporadic gusts/wind-like particles around the center, less frequent and weaker the further.
> 2. Visual distorsion in the surrounding area, gets stronger toward the center.
> Other ideas?
>
> fracturing frontiers must be revisited, the problem is evident when multiple shots hit the same slow-fracturing asteroid: the new hits don't feel impactful because the fracture moves along the main path.

### 12 — Crack speed + piercing redesign

> Yes, rewrite task 3 and keep the real bug in. I would also like to add some new tasks to the sprint:
> 1. Crack speed: I'm thinking it should depend on both the velocity and the material somehow, so fast collisions fracture faster than slow but heavy ones, even if the energy is the same.
> 2. the piercing round should impact all the cells it passes through (so potentially more than one cell per frame), processed in order from the first impact to the last one, I'm not sure if this would change something but I see that the piercing round sometimes passes through asteroids and doesn't seem to even start fractures for some periods of time, leaving large pieces on the line of penetration intact. This could also be caused by the bug in task 3.
> 3. Visual: all widgets need to be bigger (cooldowns, etc.) each one should have a name and the key it's binded to. The timer and score should also be bigger.
> 4. Add spawn pattern to all waves: burst, direction, maybe some other parameters that could be useful.
> 5. Remove the red erosion border visual effect, instead it could be indicated by some kind of warp (or something else, but more subtle and thematic than the current visual effect), and the main indicator should be once the player enters the zone (red-tint filter that darkens the further you go).

### 13 — Optimisation pass 

> Much better, the fps only drops to around 40 on grenade detonation. The warp should be removed, it takes 50% of the budget per frame whenever the vortex is on screen.
>
> I want to optimize it further however, by using the same grid that the collisionsystem uses. No code yet, what would be the tradeoff of this approach? I'm also thinking that it might be worth it to implement the adaptive grid creation now that the raycastSystem also uses the grid. What do you think?

---

## II. System & content design decisions

### 14 — Detailed design refinement

> Here's my feedback:
> 1. The world needs to be bigger: at least 16x the window size.
> 2. The soft repulsion field should work fine, could you expand on the alternative you mentioned?
> 3. The player ship being a compound is a very cool idea. I'm thinking every cell needs to have a specific function: 2 cells are the propellers, 2 cannons, the cockpit, maybe even a cell for each skill and weapon. When a cell breaks the function it provided is not available anymore. This implies there needs to be a way to repair the ship.
> 4. Cannon is good. Shotgun doesn't have to be slow pellets, but they should definitely be better at short range. The main purpose of the shotgun is to destroy multiple debris cells that would otherwise require a bullet each. The piercing round should collide normally with other entities, but it should have enough force/mass to withstand hard hits and continue on as long as the impacted asteroids aren't tough enough to stop its momentum. The grenade is cool, maybe a shrapnel based (shoot lots of weak but very fast bullets in a radius) could be worth considering. The ammo system needs to be reviewed.
> 5. The dash is cool and the short invicibility window is a cool addition, maybe even trigger fractures in the asteroids it hits. The turbo mode is cool. The slow-mo should slow down everything in the engine but the player controls should be more responsive and the player should have finer control on the ship's movements; it isn't just a skill to let the player have more time to react, it should give the player a clear edge and let him do things he couldn't do otherwise.
> 6. The asteroid variants sound good, the wave system needs to be better specified: how many asteroids spawn every wave? Are the asteroid variants of each wave predetermined? There should also be a cap on live entities for performance gameplay reasons. I'm thinking the asteroids must spawn all at once, the wavesystem has a budget that it uses to spawn different asteroids with some biases that change during the run. This is very important and should be defined better.
> 7. The 2 alien types are good. While the bruiser can use a simple ai that goes straight to the player ignoring obstacles in the way, the ai of the second one should be a little more complex, or else it would just collide with the asteroids in the way and eventually die.
> 8. The environmental forces & anomalies sounds good and since this will be one of the last features we'll add to the game the refinement can be postponed.
> 9. The damage system is perfect, we'll need to better define the player ship structure but it's good.
> 10. The main thing I'm worried about is giving a sense of progression and a clear objective to the player, accumulating points is good but there needs to be something else.
> 11. You captured the vibe perfectly, nothing to add here.
>
> To respond to your open questions:
> 1. Mouse-aim,
> 2. Slow-mo should scale real world-time while giving the player a temporary movement boost (control-wise, nothing crazy just finer control over the ship)
> 3. The player ship should be fracturable, no respawns: you die once the run is over.
> 4. Anomaly spawning can be postponed.
> 5. There should be a main menu, some basic settings and some run-specific settings, not a lot more.
>
> Give me some honest feedback on this and feel free to give suggestions.

### 15 — Setting up the editor + general design

> Ok, some problems I see right now and some quick questions:
> 1. Does the game load all the parameters from the GameConfig? For example the player movement stats, the weapon stats etc. If so, I would like to be able to build and modify assets in the Game Editor (demo), where I can get a feel and tune the parameters. The game editor should let me import, build assets and save them as files in a shared folder that the real game then loads. I'm talking every single parameter should be tunable, and every single asset should be directly testable in the game editor. This is a big cost but it will speed up greatly the workflow for the development, since most of the work will consist of creating content, tuning the parameters and playtesting. What do you think about this? Feel free to give me feedback and suggestions on this.
> 2. The waves should feel organic, not a pause and full reset once the timer is out. The asteroids that remained alive persist to the next wave, and the next wave spawns taking into account the current live asteroids so as to not hit the entity cap. This means that if the player is unable to destroy the asteroids they accumulate and ultimately kill him. That's the incentive. The wave system should be carefully planned.
> 3. The world needs to be (3*1920 x 3*1920), with a force field at the border.

### 16 — Game Design

> Let's go back to the game design: I put some thought into how I want the game to work and these are some new directions. Give me honest feedback, suggestions, complications if there's any and doability analysis.
>
> ## New notes that may change other parts
>
> A typical run lasts around 12 minutes, at 10 minutes, independently on how many waves the player cleared, the mothership spawns. Special waves (explicit waves) spawn at fixed points during the game. After the boss fight endless mode starts, with fast scaling of waves.
>
> ### World
>
> The world is 5760x3240. All asteroids "orbit" around the center and are pulled laterally and back into the center exponentially more the greater their distance from the world center. This would give movement to asteroids and make them concentrate in the center of the map.
> This would be just a system that applies forces to all bodies in the game based on their distance from the center of the map.
> The border of the map has a flat bounce as fallback for when objects reach it, shouldn't happen normally though.
>
> ### Waves
>
> Max live cells: as the game progresses, the cap on the number of live cells raises, so the later waves are far more chaotic than the beginning. Start with 300 max live cells, increment at the end of each wave by around 30. No more than 2000.
>
> Wave budget: asteroid types have a cost, multiplied by their "size_multiplier". Incremented at the end of each wave.
>
> WaveSystem: has internal timer. every 5 seconds or so, check the number of current live cells -> if <= 30% current_max_cells: spawn_new wave and reset timer.
> if 30 seconds passed from last wave spawn: spawn new wave and reset timer.
>
> After every spawn raise wave_budget and max_live_cells
>
> SpawnWave: the most difficult and flexible part.
> 1. Choosing the asteroids: based on how much time has passed since the beginning of the run, the pool of available asteroids/aliens is increased.
> 2. Each asteroid/alien type has a cost, paid with the wave budget. For asteroids there is a "size_multiplier" (or something like that) that multiplies its area and number of vertices and cells. It increases its cost accordingly.
> 3. Bias when choosing: the wave manager must maximize the wave cost, the count of new asteroids and new cells. Constraints: wave_budget, current_max_cells, wave_count_max. Need to also guarantee variety in some way.
> 4. Spawn position: For each asteroid select a position: close to the border (so the orbit system gives him movement), strictly outside of the camera view, no other body collides with it.
> 5. Special waves: explicit waves are based purely based on how much time has passed from the beginning of the run:
> Something like: around 3 minutes in the first horde wave spawns, 5 minutes in a large group of heavily spinning small asteroids, at 7 minutes a big group of alien ships spawn all together, at 10 minutes the mothership spawns.
>
> ### Upgrade system
>
> We can stub this one for now.
> At the end of each wave (beginning of a new one) the game pauses and the user can choose an upgrade chosen at random from a pool (faster fire rate, more energy, new weapons, ammo for some weapon etc.). This will give a feeling of progression to the player and enables various "builds".

### 17 — One of the final planning phase before release

> Let's plan these last tasks so I can ship a complete game.
>
> Last features/refinements:
> 1. The vortex warp is too big, it needs to be just around the center but distort much more than it does now. More vortex particles (and wider but less opaque) so the vortex currents are clearer.
> 2. When in the "Hunter Zone" and the "Erosion Zone" (and only then) the border should be visible so the player knows in which direction to move to get out of danger.
> 3. Bug fix: the timer when in Hunter Zone isn't slowing down when using slow-mo.
> 4. A minimap with different-sized dots for asteroids based on their area, with different colors for different materials (same color as the material).
>
> Installation for different OS:
> We basically need 3 versions of the game: Windows, Linux and MacOS.
> The installation of the dependencies should be very easy and automated. Windows should use Winforms.
>
> Performance:
> Winforms backend should use SKIA, not GDI+.
> Heavy profiling at different depths: first system level: measure which are the most expensive systems/ modules of the engine, then we work on each of them separately searching for optimization strategies.
>
> Cleanup of the project:
> Move editor, game versions outside of gameEngine directory/project. Lots of UI apis are in the engine but only used in the editor, maybe it would make sense to move them in the editor and keep the engine clean.
>
> Did I miss something?
> Give me honest feedback on these and suggest a detailed plan so we can finish this.
