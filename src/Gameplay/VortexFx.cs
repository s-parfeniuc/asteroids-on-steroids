using System.Numerics;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// The vortex made visible: sporadic gust motes advected along the real force field
/// (<see cref="AsteroidsGame.Gameplay.VortexSystem.FieldVelocity"/>) and drawn as fading streaks —
/// slow and sparse at the calm eye, fast and dense further out — plus an optional screen-space swirl
/// warp centred on the eye. Self-contained: owned and ticked by the shell, not an ECS system (the
/// engine's ParticleSystem can't advect along an external field or draw streaks).
/// </summary>
public sealed class VortexFx
{
    private struct Mote { public Vector2 Pos, Prev, Vel; public float Life, MaxLife; }

    private readonly Random _rng = new();
    private Mote[] _motes = new Mote[256];
    private int _count;
    private float _gustTimer;

    public void Reset() { _count = 0; _gustTimer = 0f; }

    /// <param name="viewMin">/<param name="viewMax">Camera world-space view rect, for spawn/cull culling.</param>
    public void Update(float dt, Vector2 centre, Func<Vector2, Vector2> field, in VortexFxConfig cfg,
                       Vector2 viewMin, Vector2 viewMax)
    {
        if (!cfg.Enabled) { _count = 0; return; }
        if (_motes.Length < cfg.MaxMotes) System.Array.Resize(ref _motes, cfg.MaxMotes);

        // ── advect + age ────────────────────────────────────────────────────────
        float margin = 300f;
        for (int i = 0; i < _count; )
        {
            ref var m = ref _motes[i];
            m.Life -= dt;
            m.Prev = m.Pos;
            m.Vel  = field(m.Pos) * cfg.SpeedScale;
            m.Pos += m.Vel * dt;
            bool gone = m.Life <= 0f
                     || (m.Pos - centre).LengthSquared() < 30f * 30f          // swallowed by the eye
                     || m.Pos.X < viewMin.X - margin || m.Pos.X > viewMax.X + margin
                     || m.Pos.Y < viewMin.Y - margin || m.Pos.Y > viewMax.Y + margin;
            if (gone) _motes[i] = _motes[--_count];
            else i++;
        }

        // ── sporadic gust bursts ─────────────────────────────────────────────────
        _gustTimer -= dt;
        if (_gustTimer <= 0f)
        {
            float jitter = 1f + ((float)_rng.NextDouble() * 2f - 1f) * cfg.GustJitter;
            _gustTimer = MathF.Max(0.05f, cfg.GustInterval * jitter);
            for (int k = 0; k < cfg.MotesPerGust && _count < cfg.MaxMotes; k++)
            {
                // Area-uniform spawn in the disc around the eye → naturally denser further out
                // (there is more area there), matching the field getting stronger with radius.
                float ang = (float)(_rng.NextDouble() * MathF.Tau);
                float r   = cfg.MaxRadius * MathF.Sqrt((float)_rng.NextDouble());
                Vector2 pos = centre + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r;
                if (pos.X < viewMin.X - margin || pos.X > viewMax.X + margin ||
                    pos.Y < viewMin.Y - margin || pos.Y > viewMax.Y + margin) continue;   // off-screen: skip
                float ttl = MathF.Max(0.2f, cfg.Ttl * (1f + ((float)_rng.NextDouble() * 2f - 1f) * cfg.TtlJitter));
                _motes[_count++] = new Mote { Pos = pos, Prev = pos, MaxLife = ttl, Life = ttl };
            }
        }
    }

    /// <summary>Draw the gust streaks + the bright eye core. Call inside the camera transform (world space).</summary>
    public void DrawStreaks(IRenderer r, Vector2 centre, in VortexFxConfig cfg)
    {
        if (!cfg.Enabled) return;
        byte cr = (byte)Clamp255(cfg.Color.Length > 0 ? cfg.Color[0] : 150f);
        byte cg = (byte)Clamp255(cfg.Color.Length > 1 ? cfg.Color[1] : 130f);
        byte cb = (byte)Clamp255(cfg.Color.Length > 2 ? cfg.Color[2] : 240f);
        for (int i = 0; i < _count; i++)
        {
            ref readonly var m = ref _motes[i];
            float k = m.MaxLife > 0f ? m.Life / m.MaxLife : 0f;   // 1 fresh → 0 dead
            float fade = k * (1f - k) * 4f;                       // ease in and out (0 at both ends)
            byte a = (byte)Clamp255(cfg.StreakAlpha * fade);
            if (a < 4) continue;
            Vector2 tail = m.Pos - m.Vel * cfg.StreakSeconds;
            r.DrawLine(tail, m.Pos, new Color(cr, cg, cb, a), MathF.Max(0.5f, cfg.StreakWidth));
        }
        // Bright eye core the streaks spiral into — the fixed landmark.
        byte coreA = (byte)Clamp255(cfg.StreakAlpha * 1.6f);
        r.FillCircle(centre, 6f, new Color(cr, cg, cb, coreA));
        r.DrawCircle(centre, 13f, new Color(cr, cg, cb, (byte)(coreA * 0.6f)), 2f);
    }

    private static float Clamp255(float v) => v < 0f ? 0f : v > 255f ? 255f : v;
}
