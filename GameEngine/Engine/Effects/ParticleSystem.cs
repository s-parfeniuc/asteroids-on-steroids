using System.Numerics;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Effects;

/// <summary>
/// One simulated particle. Rendered as a circle whose size and colour interpolate
/// from (Size0, Color0) at birth to (Size1, Color1) at death. Velocity decays by Drag.
/// </summary>
public struct Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float   Drag;            // per-second exponential velocity decay
    public float   Life;            // remaining seconds
    public float   MaxLife;
    public float   Size0, Size1;    // radius at birth / death
    public Color   Color0, Color1;  // colour (with alpha) at birth / death
}

/// <summary>
/// A fixed-capacity pool of kinematic particles (dust, sparks, flashes). Game-agnostic
/// presentation utility: the game emits particles seeded from physical quantities;
/// Update integrates them and Draw renders via the PAL IRenderer. Swap-remove keeps it
/// allocation-free; emits past capacity are dropped.
/// </summary>
public sealed class ParticleSystem
{
    private readonly Particle[] _p;
    private int _count;

    public ParticleSystem(int capacity = 4096) => _p = new Particle[capacity];

    public int Count    => _count;
    public int Capacity => _p.Length;

    public void Emit(in Particle particle)
    {
        if (_count < _p.Length) _p[_count++] = particle;
    }

    public void Update(float dt)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _p[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _p[i] = _p[--_count];   // swap-remove the dead one
                i--;
                continue;
            }
            if (p.Drag > 0f) p.Velocity *= MathF.Exp(-p.Drag * dt);
            p.Position += p.Velocity * dt;
        }
    }

    /// <param name="offset">World-to-screen offset: screenPos = worldPos + offset.
    /// Pass Vector2.Zero when drawing directly in screen space.</param>
    public void Draw(IRenderer r, Vector2 offset = default)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _p[i];
            float t    = p.MaxLife > 0f ? 1f - p.Life / p.MaxLife : 1f;   // 0 birth → 1 death
            float size = p.Size0 + (p.Size1 - p.Size0) * t;
            if (size <= 0f) continue;
            r.FillCircle(p.Position + offset, size, Color.Lerp(p.Color0, p.Color1, t));
        }
    }

    public void Clear() => _count = 0;
}
