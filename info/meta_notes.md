These are all conceptual questions — I'll answer directly in chat since they're clarifications rather than new topics.

## Collision resolution and chain reactions

There are two separate things that happen when a collision is detected:

Physical resolution (pushing objects apart, bouncing velocities) is handled by CollisionSystem itself, looking at RigidBody components. If an entity has a RigidBody, the system computes a velocity impulse from the contact normal and the two masses. If an entity has no RigidBody (a wall, a trigger zone), it's treated as infinite mass — immovable. This is purely mechanical and lives inside the engine.

Semantic resolution (asteroid splits, player takes damage, score increases) is handled by game systems subscribed to CollisionEvent. Those systems check the entity tags/components on the two colliding entities and decide what to do. CombatSystem reduces health; AsteroidSplitSystem spawns children. These are game concerns and live in Game/.

Chain reactions: if asteroid A splits into B and C at frame N, the new entities B and C are created via deferred spawn (not yet in the world). Their first collision check happens at frame N+1. This one-frame delay is imperceptible at 60fps. If you ever need same-frame chains (rare — pinball physics, dominoes), you run multiple collision passes in one frame, but that's a niche extension.

## Can the game developer replace physics and collision?

Yes, completely — this is one of the main benefits of ECS. Systems are just registered functions. If you don't register PhysicsSystem, it doesn't run. You can register SandPhysicsSystem instead, or both.

For a sand simulator with wind:

Write SandPhysicsSystem: reads SandGrain component, applies gravity + wind force, handles grain-grain compression (grains pile up, not bounce). Register it instead of PhysicsSystem.
Write SandCollisionSystem: different response rules — instead of elastic bounce, grains settle. Or use the existing CollisionSystem and subscribe a SandSettlingSystem to CollisionEvent that zeroes out upward velocity on contact.
The spatial index, the shape hierarchy, the contact manifold — all reusable as-is. Only the response logic changes.
The ISpatialIndex interface also lets you swap the broad phase entirely if the default grid doesn't suit a particle simulation (you might want a denser grid with smaller cells).

## Game States

The mental model: a state is the mode of the whole game, not the state of an object.

Ask "if this changes, does the meaning of Update and Draw change entirely?" If yes, it's a state. If no, it's component data or session data.

Is a state	Is NOT a state
MainMenu — no world, show menu	Player is alive vs. dead (Health component)
Playing — world runs, player has control	Asteroid is large/medium/small (AsteroidComponent.Size)
Paused — world frozen, overlay menu	Current wave number (session data)
GameOver — show score, wait for restart	Player is currently firing (ShipComponent.FireCooldown)
Loading — show progress bar, load assets	An enemy is patrolling vs. chasing (AI sub-state)
Why only the top state Updates: The stack models suspension, not multi-tasking. When you push PauseState, the world below is meant to be frozen — if PlayingState kept updating, physics would still run, bullets would still move, asteroids would drift. The pause would be fake. So Update only reaches the top.

Why Draw renders bottom-to-top: You want to see the frozen world behind the pause menu. PauseState draws a semi-transparent dark overlay and a menu on top of whatever PlayingState drew. Bottom-to-top draw order makes the stack behave like transparent layers, which is exactly the visual you want.

There's one wrinkle: some states are "pass-through" — a floating notification banner that appears without pausing the game. For this, add an UpdatesBelow flag to IGameState. If true, the stack also updates the state below. PlayingState's UpdatesBelow is false (it's the world). A BannerOverlayState's UpdatesBelow would be true. The stack checks the flag when deciding how deep to propagate Update.

## Prefabs — yes, they're exactly factories

A prefab is just a static method that creates an entity and attaches the right components. Nothing more.

```
static class AsteroidPrefab
{
    public static Entity Create(World world, Vector2 position, AsteroidSize size)
    {
        var e = world.CreateEntity();
        world.AddComponent(e, new Transform { Position = position });
        world.AddComponent(e, new Velocity  { Linear = RandomDirection() * SpeedFor(size) });
        world.AddComponent(e, new Collider  { Shape = CircleFor(size), Layer = Layers.Asteroid, Mask = Layers.Player | Layers.Bullet });
        world.AddComponent(e, new Sprite    { ImageId = SpriteFor(size), Layer = 1 });
        world.AddComponent(e, new Health    { Current = HpFor(size), Max = HpFor(size) });
        world.AddComponent(e, new AsteroidComponent { Size = size });
        return e;
    }
}
```

The value is that this is the single source of truth for what an asteroid needs. When a bullet hits a large asteroid and splits it, AsteroidSplitSystem calls AsteroidPrefab.Create(world, position, AsteroidSize.Medium) twice — it doesn't need to know what components a medium asteroid requires.

In data-driven engines (Unity, Godot), prefabs are serialized asset files you configure in an editor. In a code-first engine like ours, they're just code with a naming convention. Same concept, different medium.

## My Game - Asteroids on Steroids

Simple asteroids-like game with some progression.
Physics based destruction of asteroids and alien spaceships. Score system and personal leaderboard. 

The map is about x10 bigger than the screen. There are waves that become progressively harder (more/bigger asteroids with more health, stronger enemy spaceships). 

The focus will be on the physics based destruction of the asteroids, the idea is to make the asteroids of composed simple shapes stuck together, when they're hit by a bullet some pieces are destroyed while the rest of the asteroid splits in smaller composed pieces based on the place and force of the impact.

I want to design and plan thoroughly the game, analyze doability and potential problems of performance and logic, extensions to the engine that are needed, etc.


### TODO

Tune the vortex. Tune the player control (implement from scratch, maybe current approach is wrong). 
Conclusion: dampening border, vortex as before. To avoid asteroids clamping in the center might make sense to change the force values periodically to spice things up.


Implement wave spawn function: takes a list of asteroids (procedural descriptions) and their respective size multiplier velocity and spin, spawns them outside of camera field of view.
Implement choose asteroids function: takes a budget, a map of available asteroid types with their costs, a bias vector, current cell cap and asteroid_count_cap.
Sync wave system to the game editor. 












### Unrelated

#### Shopping list

Adapter Schuko
Prenotare locker
Vedere shuttle GMM
Tappi orecchie
Marsupi
Preordine birra 40$

#### To do before

GMM system with 100$ each
Scaricare app GMM

Ognuno:

Haine:
1 prostire
5 t-shirt
3 pantaloni 
1 giacca freddo
2 scarponi + scarpe normal
5 calzini + 2 mutande
1 pajama
1 borraccia

Hygiene:
Spazzolino + dentifricio
Farmaci psico (sertra + litio)
Farmaci extra

#### To do in Brussels

Comprare snacks/barrette
See public transport tickets


#### Transport

Biglietti aereo
Vedere shuttle GMM
Biglietti treno airbnb-airport

#### TimeLine

16 -> arrival at airport
16-17 -> airbnb
17 12:30 -> shuttle to graspop (28$ x 2)
17-22 -> festitent
22 -> shuttle to brussels
22-23 -> airbnb
23 -> departure at airport