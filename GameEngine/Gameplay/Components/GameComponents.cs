using System.Numerics;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.Components;

public struct TimeToLive    { public float Remaining; }
public struct ShootCooldown { public float Remaining; }
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
public struct PiercingRoundTag { public Vector2 Direction; public float LateralClamp; public float PlayerGrace; }

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
    public float  Drag;        // 1/s velocity damping; 0 = none
    public Entity Owner;       // firing entity (ignored while OwnerGrace > 0)
    public float  OwnerGrace;  // seconds during which owner-hits are skipped
}

/// <summary>Tags all active mothership fragment entities for shared tracking and win condition.</summary>
public struct MothershpId { public int Id; public int InitialCockpitCount; }

/// <summary>Accumulates time on a cockpit-bearing mothership fragment to trigger alien spawns.</summary>
public struct SpawnerAccumulator { public float Value; }

/// <summary>Marker on black hole projectile entities. Carries attraction parameters set at spawn.</summary>
public struct BlackHoleTag { public float Radius; public float Strength; public float CrushRadius; }
