using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>Shared weapon spawn effects (single shots, stratified cones, grenade shrapnel),
/// used by the player, the aliens, and the fracture handlers so weapon behaviour is identical.</summary>
public static class WeaponEffects
{
    /// <summary>Seconds a freshly-fired bullet ignores hits on its owner (to clear the hull).</summary>
    public const float BulletGrace = 0.1f;

    /// <summary>Spawns one raycast bullet, carrying its per-bullet drag and the owner-grace
    /// window (so it ignores hits on the entity that fired it until it clears the hull).</summary>
    public static Entity SpawnBullet(World world, Vector2 pos, Vector2 vel, string weaponKey,
        float ttl, Color color, Entity owner, float ownerGrace, float drag, bool alien)
    {
        var b = world.CreateEntity();
        world.AddComponent(b, new Transform { Position = pos, PreviousPosition = pos });
        world.AddComponent(b, new Velocity { Linear = vel });
        world.AddComponent(b, new BulletTag());
        if (alien) world.AddComponent(b, new AlienBulletTag());
        world.AddComponent(b, new BulletVisual { Color = color });
        world.AddComponent(b, new BulletData
            { WeaponKey = weaponKey, Drag = drag, Owner = owner, OwnerGrace = ownerGrace });
        world.AddComponent(b, new TimeToLive { Remaining = ttl });
        return b;
    }

    /// <summary>Emits a stratified cone — evenly slotted rays with per-ray angular/speed/ttl
    /// jitter (structured, not gridded, not pure-random) — or a full ring when coneAngleDeg ≥
    /// 360. Variance + drag come from the WeaponConfig.</summary>
    public static void SpawnCone(World world, Vector2 origin, Vector2 baseDir, int rays,
        float coneAngleDeg, float speed, float ttl, string weaponKey,
        Color color, Entity owner, float ownerGrace, WeaponConfig wc, bool alien, Random rng)
    {
        if (rays < 1) rays = 1;
        bool ring = coneAngleDeg >= 359.9f;
        float baseA, step;
        if (ring)
        {
            baseA = (float)(rng.NextDouble() * MathF.Tau);
            step  = MathF.Tau / rays;
        }
        else
        {
            float half = coneAngleDeg * 0.5f * MathF.PI / 180f;
            baseA = MathF.Atan2(baseDir.Y, baseDir.X) - half;
            step  = rays > 1 ? half * 2f / (rays - 1) : 0f;
        }

        for (int i = 0; i < rays; i++)
        {
            float jitter = step * ((float)rng.NextDouble() - 0.5f) * wc.SpreadJitter;
            float ang = baseA + step * i + jitter;
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            float spd = speed * (1f + ((float)rng.NextDouble() * 2f - 1f) * wc.SpeedJitter);
            float t   = ttl   * (1f + ((float)rng.NextDouble() * 2f - 1f) * wc.TtlJitter);
            SpawnBullet(world, origin, dir * MathF.Max(1f, spd), weaponKey,
                        MathF.Max(0.05f, t), color, owner, ownerGrace, wc.Drag, alien);
        }
    }

    /// <summary>Detonates a grenade: removes its fuse, destroys it, and sprays a ring of
    /// shrapnel. Returns the total shrapnel energy (for the caller's flash VFX); 0 if invalid.</summary>
    public static float SpawnShrapnel(World world, GameConfig gc, Entity grenade,
        Vector2 worldPos, string weaponKey, Random rng)
    {
        if (!world.IsAlive(grenade)) return 0f;
        if (!world.HasComponent<GrenadeFuse>(grenade)) return 0f;   // already detonated this frame
        world.RemoveComponentImmediate<GrenadeFuse>(grenade);       // prevent double-detonation
        world.DestroyEntity(grenade);
        if (!gc.Weapons.TryGetValue(weaponKey, out var wcfg)) return 0f;

        int   count  = wcfg.ShrapnelCount ?? 12;
        float spread = wcfg.ShrapnelSpread ?? 360f;
        float speed  = wcfg.ShrapnelSpeed  ?? wcfg.ProjectileSpeed;

        SpawnCone(world, worldPos, Vector2.UnitX, count, spread, speed, wcfg.TimeToLive,
                  weaponKey, new Color(255, 160, 50), owner: default, ownerGrace: 0f, wcfg, alien: false, rng);
        return count * speed * 0.5f;   // flash magnitude (VFX only)
    }
}
