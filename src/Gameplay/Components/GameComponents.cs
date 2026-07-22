using System.Numerics;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.Components;

public struct TimeToLive    { public float Remaining; }
public struct ShootCooldown { public float Remaining; }
/// <summary>Per-alien skill cooldown tracker (e.g. the bruiser dash). DashCd counts down to 0 = ready.</summary>
public struct AlienSkillState { public float DashCd; }
public struct AimComponent  { public Vector2 Dir; }

/// <summary>Fill and outline colours for a fracturable body.</summary>
public struct BodyColor { public Color Fill, Outline; }

/// <summary>Active weapon key (indexes GameConfig.Weapons).</summary>
public struct ActiveWeapon { public string Key; }

/// <summary>Per-weapon fire cooldown timers (remaining seconds per weapon type).</summary>
public struct WeaponCooldowns
{
    public float Cannon;
    public float Shotgun;
    public float Piercing;
    public float Grenade;
}

/// <summary>Grenade projectile — detonates on impact or when Remaining reaches 0.</summary>
public struct GrenadeFuse { public float Remaining; public string WeaponKey; }

/// <summary>Marker on piercing round bodies. Carries aim direction for lateral clamp, and a
/// brief PlayerGrace countdown (seconds) during which the round ignores the player layer so
/// it can clear the firing ship's shape before colliding with it normally.</summary>
/// <summary>A wave body spawned in the off-screen ring OUTSIDE the playable field. Exempts it from
/// the border hazard (damp/push/clamp/erosion) until it crosses into the field — without this the
/// hazard would teleport it to the edge on its first frame. Removed on entry.</summary>
public struct InboundSpawn { }

/// <summary>
/// A piercing body's terminal-ballistics state. <see cref="Power"/> is its remaining penetration
/// budget, set once at spawn from the body's own kinetic energy (<see cref="PowerPerKE"/> is the
/// weapon's KE→power exchange rate, fixed at fire so shards recompute theirs from their own mass
/// and fling speed) and spent per cell crossed; <see cref="Power0"/> is the spawn value, kept so
/// speed can fade as the budget drains. <see cref="LastTarget"/>/<see cref="LastCell"/> remember
/// the cell the round is currently sitting inside, so a sensor contact that re-fires every frame
/// doesn't re-charge for the same cell.
/// </summary>
public struct PiercingRoundTag
{
    public Vector2 Direction;
    public float   LateralClamp;
    public float   PlayerGrace;
    public float   Power;
    public float   Power0;
    public float   PowerPerKE;
    public Entity  LastTarget;
    public int     LastCell;
}

/// <summary>Cooldown state for all three player skills.</summary>
public struct SkillState
{
    public float DashCooldown;
    public float TurboCooldown;
    public float SlowMoCooldown;
    /// <summary>Remaining active duration (>0 while skill is active).</summary>
    public float DashActive;
    public float TurboActive;
    public float SlowMoActive;
}

/// <summary>Fresh fracture fragment: no collision for a brief grace window so siblings
/// can separate without fighting each other.</summary>
public struct FractureGhost { public float Remaining; public bool Done; }

/// <summary>Cached body-local edge pairs for rendering (silhouette + crack lines).
/// Updated once at spawn; live fracture overrides this with a per-frame compute.</summary>
public struct RenderOutline { public Vector2[] Outline; public Vector2[] Cracks; }

/// <summary>Collider-less polygon chunk shed when a cell vaporises. Local verts are
/// centroid-relative; renderer fills it faded by remaining TTL.</summary>
public struct DebrisPiece { public Vector2[] Local; public Color Color; public float MaxTtl; }

/// <summary>Visual colour for a bullet tracer.</summary>
public struct BulletVisual { public Color Color; }

/// <summary>Per-bullet data: weapon key + impact energy, optional velocity drag (raycast
/// bullets bypass PhysicsSystem), and the firing entity + a grace window during which the
/// bullet ignores hits on its owner so it can escape the hull that spawned it.</summary>
public struct BulletData
{
    public string WeaponKey;
    public float  Drag;         // 1/s velocity damping; 0 = none
    public Entity Owner;        // firing entity (ignored while OwnerGrace > 0)
    public float  OwnerGrace;   // seconds during which owner-hits are skipped
    public float? MassOverride; // impact mass, trumping the weapon config's (e.g. boss barrage rays)
}

/// <summary>Tags all active mothership fragment entities for shared tracking and win condition.</summary>
public struct MothershpId { public int Id; public int InitialCockpitCount; }

/// <summary>Accumulates time on a cockpit-bearing mothership fragment to trigger alien spawns.</summary>
public struct SpawnerAccumulator { public float Value; }

/// <summary>Marker on black hole projectile entities. Carries attraction parameters set at spawn.</summary>
public struct BlackHoleTag { public float Radius; public float Strength; public float CrushRadius; }

/// <summary>A transient expanding-ring visual (e.g. the boss shockwave). Uses TimeToLive for lifetime;
/// the ring radius grows from 0 to MaxRadius over MaxAge.</summary>
public struct ShockwaveRing { public float MaxAge; public float MaxRadius; }

/// <summary>Per-cockpit-fragment boss controller state. Each cockpit-bearing mothership fragment is an
/// independent boss driven by BossSystem, acting on its own attached cells. Skill cooldowns weaken
/// (lengthen) as its "skill" cells are pulverised; a fragment that loses its cockpit becomes inert.</summary>
public struct BossBrain
{
    public float ShockwaveCd, BlackHoleCd, RamCd, BarrageCd, SpawnCd;
    public float RamActive;          // seconds of ram lunge remaining
    public int   MaxSkillCells;      // "skill"-role cells at creation, for cooldown weakening
    public int   MaxSpawnerCells;    // "spawner"-role cells at creation, for spawn-rate weakening
}
