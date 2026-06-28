using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Effects;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Shared fracture/impact VFX — the impact flash, the dust burst, and the polygon
/// debris shed by a pulverised cell. Config-driven (VfxConfig) so the game and the
/// demo produce identical effects.
/// </summary>
public sealed class ParticleEffects
{
    private readonly World          _world;
    private readonly ParticleSystem _fx;
    private readonly VfxConfig      _vfx;
    private readonly Random         _rng;

    public ParticleEffects(World world, ParticleSystem fx, VfxConfig vfx, Random rng)
    { _world = world; _fx = fx; _vfx = vfx; _rng = rng; }

    public void EmitFlash(Vector2 point, float energy)
    {
        float e  = Math.Clamp(energy / 80_000f, 0.25f, 2.5f);
        float sz = _vfx.FlashSize * e;
        _fx.Emit(new Particle
        {
            Position = point, Velocity = Vector2.Zero, Drag = 0f,
            Life = _vfx.FlashTtl, MaxLife = _vfx.FlashTtl,
            Size0 = sz * 0.35f, Size1 = sz,
            Color0 = new Color(255, 245, 210, 235), Color1 = new Color(255, 165, 70, 0),
        });
    }

    public void EmitDustBurst(Vector2 centroid, Vector2 dirHint, Vector2 carrier, float area, BodyColor color)
    {
        int n = Math.Clamp((int)(area / 200f), 2, (int)_vfx.DustCount);
        Vector2 vdir = dirHint.LengthSquared() > 1e-4f
            ? Vector2.Normalize(dirHint)
            : new Vector2(MathF.Cos((float)_rng.NextDouble() * MathF.Tau),
                          MathF.Sin((float)_rng.NextDouble() * MathF.Tau));
        float baseSz = _vfx.DustSize * MathF.Sqrt(MathF.Max(area, 1f) / 1400f);
        for (int i = 0; i < n; i++)
        {
            float ang = ((float)_rng.NextDouble() * 2f - 1f) * MathF.PI * _vfx.DustSpread;
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            Vector2 dir  = new(vdir.X * ca - vdir.Y * sa, vdir.X * sa + vdir.Y * ca);
            float   spd  = _vfx.DustSpeed * (0.4f + (float)_rng.NextDouble());
            float   ttl  = _vfx.DustTtl   * (0.6f + 0.6f * (float)_rng.NextDouble());
            float   sz   = baseSz * (0.7f + 0.6f * (float)_rng.NextDouble());
            Vector2 jit  = new((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f);
            _fx.Emit(new Particle
            {
                Position = centroid + jit * baseSz,
                Velocity = dir * spd + carrier,
                Drag = 2.2f, Life = ttl, MaxLife = ttl,
                Size0 = sz, Size1 = sz * 0.1f,
                Color0 = color.Outline.WithAlpha(220), Color1 = color.Outline.WithAlpha(0),
            });
        }
    }

    public void SpawnDebris(in CellPulverizedEvent ev, BodyColor color)
    {
        var verts = ev.WorldVerts;
        if (verts is null || verts.Length < 3) return;
        var pieces = new List<List<Vector2>> { new(verts) };
        int cuts = _rng.Next(1, 3);
        for (int c = 0; c < cuts; c++)
        {
            float ang = (float)(_rng.NextDouble() * Math.PI);
            Vector2 cutDir = new(MathF.Cos(ang), MathF.Sin(ang));
            var next = new List<List<Vector2>>();
            foreach (var poly in pieces)
            {
                var left = new List<Vector2>(); var right = new List<Vector2>();
                SplitByLine(poly, ev.WorldCentroid, cutDir, left, right);
                if (left.Count  >= 3) next.Add(left);
                if (right.Count >= 3) next.Add(right);
            }
            if (next.Count > 0) pieces = next;
        }
        float ttl = _vfx.DebrisTtl;
        foreach (var poly in pieces)
        {
            Vector2 cen = Vector2.Zero;
            foreach (var v in poly) cen += v;
            cen /= poly.Count;
            var local = poly.Select(v => v - cen).ToArray();
            Vector2 outward = (cen - ev.WorldCentroid) is var o && o.LengthSquared() > 1e-4f
                ? Vector2.Normalize(o)
                : new Vector2(MathF.Cos((float)(_rng.NextDouble() * MathF.Tau)),
                              MathF.Sin((float)(_rng.NextDouble() * MathF.Tau)));
            Vector2 vel  = ev.CellVelocity + outward * (_vfx.DebrisScatter * (0.5f + (float)_rng.NextDouble()));
            float   spin = ev.BodyAngular + (float)(_rng.NextDouble() * 2 - 1) * 4f;
            var de = _world.CreateEntity();
            _world.AddComponent(de, new Transform { Position = cen, PreviousPosition = cen });
            _world.AddComponent(de, new Velocity { Linear = vel, Angular = spin });
            _world.AddComponent(de, new TimeToLive { Remaining = ttl });
            _world.AddComponent(de, new DebrisPiece { Local = local, Color = color.Fill, MaxTtl = ttl });
        }
    }

    private static void SplitByLine(List<Vector2> poly, Vector2 P, Vector2 dir,
                                    List<Vector2> left, List<Vector2> right)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 cur = poly[i], nxt = poly[(i + 1) % n];
            float sc = dir.X * (cur.Y - P.Y) - dir.Y * (cur.X - P.X);
            float sn = dir.X * (nxt.Y - P.Y) - dir.Y * (nxt.X - P.X);
            if (sc >= 0f) left.Add(cur); else right.Add(cur);
            if ((sc > 0f) != (sn > 0f) && sc != sn)
            {
                float t = sc / (sc - sn);
                Vector2 ip = cur + (nxt - cur) * t;
                left.Add(ip); right.Add(ip);
            }
        }
    }
}
