using System.Numerics;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsGame.Gameplay;

/// <summary>A bullet raycast struck a fracturable body. Published by RaycastBulletSystem,
/// consumed by FractureResponseSystem.</summary>
public readonly struct BulletHitEvent
{
    public readonly Entity  Target, Bullet;
    public readonly int     StruckCell;
    public readonly Vector2 Point, ShotDir;
    public BulletHitEvent(Entity target, Entity bullet, int cell, Vector2 point, Vector2 shotDir)
    { Target = target; Bullet = bullet; StruckCell = cell; Point = point; ShotDir = shotDir; }
}

/// <summary>A grenade reached its fuse end or hit something and should detonate.</summary>
public readonly struct GrenadeDetonateEvent
{
    public readonly Entity  Grenade;
    public readonly Vector2 WorldPos;
    public readonly string  WeaponKey;
    public GrenadeDetonateEvent(Entity g, Vector2 pos, string key)
    { Grenade = g; WorldPos = pos; WeaponKey = key; }
}
