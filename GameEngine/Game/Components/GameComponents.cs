using System.Numerics;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.Components;

public struct TimeToLive    { public float Remaining; }
public struct ShootCooldown { public float Remaining; }
public struct AimComponent  { public Vector2 Dir; }

/// <summary>Fill and outline colours for a fracturable body.</summary>
public struct BodyColor { public Color Fill, Outline; }

/// <summary>Active weapon key (indexes GameConfig.Weapons).</summary>
public struct ActiveWeapon { public string Key; }

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

/// <summary>Weapon key and impact energy carried by this bullet entity.</summary>
public struct BulletData { public string WeaponKey; public float Energy; }
