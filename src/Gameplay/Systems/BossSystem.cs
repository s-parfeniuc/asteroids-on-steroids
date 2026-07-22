using System;
using System.Collections.Generic;
using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Drives each cockpit-bearing mothership fragment as an independent boss, acting only on its own
/// attached cells: face the player + hold a standoff (or ram along its nose), and cast skills gated by
/// its alive "skill" cells with cooldowns that lengthen (weaken) as those cells are pulverised. A
/// fragment that loses its cockpit sheds its BossBrain and becomes inert tough debris.
/// Shared by the game and the demo.
/// </summary>
public sealed class BossSystem : ISystem
{
    private readonly GameContext     _ctx;
    private readonly ParticleEffects _fx;
    private readonly Camera          _camera;
    private readonly Random          _rng;
    private readonly Func<Entity>    _player;
    private readonly List<Entity>    _bosses  = new();
    private readonly List<Entity>    _toClear = new();

    public BossSystem(GameContext ctx, ParticleEffects fx, Camera camera, Random rng, Func<Entity> player)
    { _ctx = ctx; _fx = fx; _camera = camera; _rng = rng; _player = player; }

    public void Update(World world, double dtd)
    {
        if (!_ctx.Config.Entities.TryGetValue("mothership", out var ec) || ec.Boss is not { } bc) return;
        float dt = (float)dtd;

        Entity player   = _player();
        bool playerAlive = world.IsAlive(player) && world.HasComponent<Transform>(player);
        Vector2 playerPos = playerAlive ? world.GetComponent<Transform>(player).Position : Vector2.Zero;
        Vector2 playerVel = playerAlive && world.HasComponent<Velocity>(player)
            ? world.GetComponent<Velocity>(player).Linear : Vector2.Zero;

        // Collect first so the per-boss work (which spawns entities / runs its own ForEach) isn't nested.
        _bosses.Clear();
        world.ForEach<BossBrain>((Entity e, ref BossBrain _) => _bosses.Add(e));
        _toClear.Clear();

        foreach (var e in _bosses)
        {
            if (!world.IsAlive(e) || !world.HasComponent<FracturableBody>(e) || !world.HasComponent<Velocity>(e))
                continue;

            // Count this fragment's alive cells by role (release the fb ref before any spawning).
            int cockpit = 0, skill = 0, spawner = 0;
            {
                ref var fb = ref world.GetComponent<FracturableBody>(e);
                bool[]? pulv = world.HasComponent<FractureProcess>(e)
                    ? world.GetComponent<FractureProcess>(e).Pulverized : null;
                for (int i = 0; i < fb.Cells.Length; i++)
                {
                    if (pulv != null && pulv[i]) continue;
                    switch (fb.Cells[i].Role)
                    {
                        case "cockpit": cockpit++; break;
                        case "skill":   skill++;   break;
                        case "spawner": spawner++; break;
                    }
                }
            }
            if (cockpit == 0) { _toClear.Add(e); continue; }   // no cockpit → inert debris

            ref var brain = ref world.GetComponent<BossBrain>(e);   // BossBrain set never resized below

            // ── Movement (Transform/Velocity refs scoped; released before any spawning) ──
            Vector2 pos; float rot;
            {
                ref var t   = ref world.GetComponent<Transform>(e);
                ref var vel = ref world.GetComponent<Velocity>(e);
                pos = t.Position; rot = t.Rotation;

                Vector2 facing = new(MathF.Cos(rot - MathF.PI * 0.5f), MathF.Sin(rot - MathF.PI * 0.5f));
                if (playerAlive)
                {
                    Vector2 toP  = playerPos - pos;
                    float   dist = toP.Length();
                    Vector2 pdir = dist > 1f ? toP / dist : facing;

                    // Kinematic turn to face the player.
                    float targetAngle = MathF.Atan2(pdir.Y, pdir.X) + MathF.PI * 0.5f;
                    float diff        = NormalizeAngle(targetAngle - rot);
                    vel.Angular = Math.Clamp(diff * 8f, -2.5f, 2.5f);

                    if (brain.RamActive > 0f)
                    {
                        // Ram lunges along the nose (facing), moving only this fragment's cells.
                        brain.RamActive -= dt;
                        Vector2 dv = facing * bc.RamChargeSpeed - vel.Linear;
                        float dl = dv.Length(), step = bc.RamChargeAccel * dt;
                        if (dl > 0f) vel.Linear += dl <= step ? dv : dv / dl * step;
                    }
                    else
                    {
                        Vector2 tangent = new(-pdir.Y, pdir.X);
                        Vector2 desired = dist > bc.PreferredRange * 1.1f  ? pdir * bc.CruiseSpeed
                                        : dist < bc.PreferredRange * 0.85f ? -pdir * bc.CruiseSpeed * 0.6f
                                        :                                     tangent * bc.CruiseSpeed * 0.35f;
                        Vector2 dv = desired - vel.Linear;
                        float dl = dv.Length(), step = bc.Accel * dt;
                        if (dl > 0f) vel.Linear += dl <= step ? dv : dv / dl * step;
                    }
                }
                else vel.Angular *= MathF.Exp(-3f * dt);
            }
            Vector2 nose = new(MathF.Cos(rot - MathF.PI * 0.5f), MathF.Sin(rot - MathF.PI * 0.5f));

            // ── Skills: need alive skill cells; cooldowns weaken (×maxSkill/alive) as they die ──
            if (skill > 0 && playerAlive)
            {
                float w = brain.MaxSkillCells > 0 ? brain.MaxSkillCells / (float)skill : 1f;

                brain.ShockwaveCd -= dt;
                if (brain.ShockwaveCd <= 0f) { DoShockwave(world, pos, bc); brain.ShockwaveCd = bc.ShockwaveCooldown * w; }

                brain.BlackHoleCd -= dt;
                if (brain.BlackHoleCd <= 0f) { SpawnBlackHole(world, pos, playerPos, playerVel, bc); brain.BlackHoleCd = bc.BlackHoleCooldown * w; }

                brain.BarrageCd -= dt;
                if (brain.BarrageCd <= 0f) { FireBarrage(world, pos, bc); brain.BarrageCd = bc.BarrageCooldown * w; }

                brain.RamCd -= dt;
                if (brain.RamCd <= 0f)
                {
                    if ((playerPos - pos).Length() >= bc.RamChargeMinDist)
                    {
                        brain.RamActive = bc.RamChargeDuration;
                        brain.RamCd     = bc.RamChargeCooldown * w;
                    }
                    // Too close for a run-up: retry shortly instead of burning the whole cooldown on a
                    // no-op (which is why the boss "never rammed" when PreferredRange < RamChargeMinDist).
                    else brain.RamCd = 0.4f;
                }
            }

            // ── Spawn drones from spawner cells; rate weakens as spawner cells die ──
            if (spawner > 0)
            {
                float sw = brain.MaxSpawnerCells > 0 ? brain.MaxSpawnerCells / (float)spawner : 1f;
                brain.SpawnCd -= dt;
                if (brain.SpawnCd <= 0f)
                {
                    SpawnDroneFrom(world, e, pos, rot, bc);
                    brain.SpawnCd = bc.SpawnInterval * sw;
                }
            }
        }

        foreach (var e in _toClear)
            if (world.IsAlive(e) && world.HasComponent<BossBrain>(e))
                world.RemoveComponent<BossBrain>(e);   // becomes inert tough debris
    }

    private void DoShockwave(World world, Vector2 center, BossConfig bc)
    {
        float radSq = bc.ShockwaveRadius * bc.ShockwaveRadius;
        var impulses = new List<(Entity e, Vector2 dir, float kick)>();
        world.ForEach<Transform, RigidBody>((Entity e, ref Transform t, ref RigidBody _) =>
        {
            if (world.HasComponent<MothershpId>(e)) return;   // don't blow the boss around
            Vector2 delta = t.Position - center;
            float dSq = delta.LengthSquared();
            if (dSq < 1f || dSq > radSq) return;
            float dist = MathF.Sqrt(dSq);
            impulses.Add((e, delta / dist, bc.ShockwaveStrength / (dist + 1f)));
        });
        foreach (var (e, dir, kick) in impulses)
            if (world.IsAlive(e) && world.HasComponent<Velocity>(e))
                world.GetComponent<Velocity>(e).Linear += dir * kick;

        var ring = world.CreateEntity();
        world.AddComponent(ring, new Transform { Position = center, PreviousPosition = center });
        world.AddComponent(ring, new ShockwaveRing { MaxAge = 0.45f, MaxRadius = bc.ShockwaveRadius });
        world.AddComponent(ring, new TimeToLive { Remaining = 0.45f });
        _fx.EmitFlash(center, 60000f);
        _camera.AddTrauma(0.5f);
    }

    private void SpawnBlackHole(World world, Vector2 center, Vector2 playerPos, Vector2 playerVel, BossConfig bc)
    {
        float travel   = (playerPos - center).Length() / MathF.Max(1f, bc.BlackHoleSpeed);
        Vector2 aim     = playerPos + playerVel * (travel * bc.BlackHoleLead);
        Vector2 toAim   = aim - center;
        float   tlen    = toAim.Length();
        Vector2 dir      = tlen > 1f ? toAim / tlen : -Vector2.UnitY;
        var bh = world.CreateEntity();
        world.AddComponent(bh, new Transform { Position = center, PreviousPosition = center });
        world.AddComponent(bh, new Velocity  { Linear = dir * bc.BlackHoleSpeed });
        world.AddComponent(bh, new BlackHoleTag
            { Radius = bc.BlackHoleRadius, Strength = bc.BlackHoleStrength, CrushRadius = bc.BlackHoleCrushRadius });
        world.AddComponent(bh, new TimeToLive { Remaining = bc.BlackHoleDuration });
    }

    private void FireBarrage(World world, Vector2 center, BossConfig bc)
    {
        if (!_ctx.Config.Weapons.TryGetValue("cannon_alien", out var wcfg)) return;
        int   n     = Math.Max(3, bc.BarrageCount);
        float baseA = (float)_rng.NextDouble() * MathF.Tau;
        float step  = MathF.Tau / n;
        var   color = new Color(255, 120, 120);
        for (int i = 0; i < n; i++)
        {
            float jitter = step * ((float)_rng.NextDouble() - 0.5f) * bc.BarrageSpreadJitter;
            float ang    = baseA + step * i + jitter;
            Vector2 dir  = new(MathF.Cos(ang), MathF.Sin(ang));
            float spd    = bc.BarrageSpeed * (1f + ((float)_rng.NextDouble() * 2f - 1f) * bc.BarrageSpeedJitter);
            float ttl    = wcfg.TimeToLive * (1f + ((float)_rng.NextDouble() * 2f - 1f) * bc.BarrageTtlJitter);
            WeaponEffects.SpawnBullet(world, center + dir * bc.BarrageSpawnRadius, dir * MathF.Max(1f, spd),
                "cannon_alien", MathF.Max(0.05f, ttl), color, owner: default, ownerGrace: 0f, wcfg.Drag, alien: true,
                massOverride: bc.BarrageRayMass);
        }
        _camera.AddTrauma(0.35f);
        _fx.EmitFlash(center, 30000f);
    }

    // Spawn a drone from this fragment's first alive spawner cell, pushed out past the hull.
    private void SpawnDroneFrom(World world, Entity e, Vector2 fragCenter, float rot, BossConfig bc)
    {
        ref var fb = ref world.GetComponent<FracturableBody>(e);
        bool[]? pulv = world.HasComponent<FractureProcess>(e)
            ? world.GetComponent<FractureProcess>(e).Pulverized : null;
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 spawnPos = fragCenter;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            if (fb.Cells[i].Role != "spawner" || (pulv != null && pulv[i])) continue;
            Vector2 cen = fb.Cells[i].Centroid;
            Vector2 worldCen = new(cen.X * cos - cen.Y * sin + fragCenter.X,
                                   cen.X * sin + cen.Y * cos + fragCenter.Y);
            Vector2 outDir = worldCen - fragCenter;
            float olen = outDir.Length();
            outDir = olen < 1e-4f ? Vector2.UnitX : outDir / olen;
            spawnPos = worldCen + outDir * bc.SpawnSafetyMargin;
            break;
        }
        AlienPrefab.Spawn(world, _ctx, _rng, spawnPos, bc.SpawnType);
    }

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI)  a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
