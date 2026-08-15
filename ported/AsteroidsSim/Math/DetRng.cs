using System;

namespace AsteroidsSim.Math;

/// <summary>Named RNG substreams. See the class remarks on <see cref="DetRng"/>.</summary>
public enum RngStream
{
    /// <summary>Voronoi seeding, Lloyd relaxation, cluster placement.</summary>
    Tessellation = 0,
    /// <summary>Wave composition, spawn selection, spawn placement.</summary>
    Waves = 1,
    /// <summary>Per-body cluster fields (density / bond multipliers).</summary>
    Clusters = 2,
    /// <summary>AI decisions, wander, jitter.</summary>
    Ai = 3,
    /// <summary>Loot rolls: rarity, affixes.</summary>
    Loot = 4,
    /// <summary>Weapon spread, recoil jitter — anything gameplay-affecting on fire.</summary>
    Weapons = 5,
}

/// <summary>
/// Deterministic PCG32 with named, independent substreams.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>System.Random</c>.</b> Its algorithm is unspecified and has changed between .NET
/// versions; it is not reproducible across runtimes and is therefore unusable under lockstep.</para>
///
/// <para><b>Why substreams.</b> The current game shares one <c>Random</c> through <c>GameContext</c>, so
/// adding a particle effect silently shifts every subsequent wave roll — which makes tuning
/// irreproducible even in single-player. Each <see cref="RngStream"/> advances independently, so
/// presentation churn can never perturb simulation rolls.</para>
///
/// <para><b>Presentation must not use this.</b> VFX and audio jitter live outside <c>SimState</c> and
/// should use their own generator; anything drawn from here is part of the deterministic contract and
/// must be snapshotted.</para>
///
/// <para>PCG32 (O'Neill, 2014): a 64-bit LCG whose output is permuted by an xorshift and a rotation.
/// All arithmetic is integer, so it is bit-identical everywhere by construction.</para>
/// </remarks>
public struct DetRng : IEquatable<DetRng>
{
    private const ulong Multiplier = 6364136223846793005UL;

    // One (state, increment) pair per substream. Fixed size so the whole struct
    // is blittable and snapshot/restore is a plain copy.
    private const int StreamCount = 6;

    private ulong _s0, _s1, _s2, _s3, _s4, _s5;
    private ulong _i0, _i1, _i2, _i3, _i4, _i5;

    /// <summary>Seed every substream from one run seed. Streams are independent.</summary>
    public static DetRng FromSeed(ulong seed)
    {
        DetRng r = default;
        for (int i = 0; i < StreamCount; i++)
        {
            // SplitMix64 the (seed, streamIndex) pair so nearby seeds don't
            // produce correlated streams.
            ulong z = seed + 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;

            // The increment must be odd for full period.
            ulong inc = (z << 1) | 1UL;
            r.SetInc(i, inc);
            r.SetState(i, 0UL);
            r.NextUInt((RngStream)i);            // advance once past the seed
            r.SetState(i, r.GetState(i) + z);
            r.NextUInt((RngStream)i);
        }
        return r;
    }

    /// <summary>Uniform 32-bit value.</summary>
    public uint NextUInt(RngStream stream)
    {
        int i = (int)stream;
        ulong old = GetState(i);
        SetState(i, unchecked(old * Multiplier + GetInc(i)));

        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform in [0, bound). Rejection-sampled, so unbiased and deterministic.</summary>
    public uint NextUInt(RngStream stream, uint bound)
    {
        if (bound == 0) return 0;
        uint threshold = (uint)(-(int)bound) % bound;   // 2^32 % bound
        while (true)
        {
            uint r = NextUInt(stream);
            if (r >= threshold) return r % bound;
        }
    }

    /// <summary>Uniform in [0, bound).</summary>
    public int NextInt(RngStream stream, int bound)
        => bound <= 0 ? 0 : (int)NextUInt(stream, (uint)bound);

    /// <summary>Uniform in [min, max).</summary>
    public int NextInt(RngStream stream, int min, int max)
        => max <= min ? min : min + NextInt(stream, max - min);

    /// <summary>
    /// Uniform in [0, 1). Built from the top 24 bits so every result is exactly
    /// representable — no rounding, therefore no platform variance.
    /// </summary>
    public float NextFloat(RngStream stream)
        => (NextUInt(stream) >> 8) * (1.0f / 16777216.0f);

    /// <summary>Uniform in [min, max).</summary>
    public float NextFloat(RngStream stream, float min, float max)
        => min + (max - min) * NextFloat(stream);

    /// <summary>Uniform in [-1, 1).</summary>
    public float NextSigned(RngStream stream)
        => NextFloat(stream) * 2.0f - 1.0f;

    // ── fixed-slot accessors (no arrays, so the struct stays blittable) ───────

    private ulong GetState(int i) => i switch
    {
        0 => _s0, 1 => _s1, 2 => _s2, 3 => _s3, 4 => _s4, _ => _s5,
    };

    private void SetState(int i, ulong v)
    {
        switch (i)
        {
            case 0: _s0 = v; break;
            case 1: _s1 = v; break;
            case 2: _s2 = v; break;
            case 3: _s3 = v; break;
            case 4: _s4 = v; break;
            default: _s5 = v; break;
        }
    }

    private ulong GetInc(int i) => i switch
    {
        0 => _i0, 1 => _i1, 2 => _i2, 3 => _i3, 4 => _i4, _ => _i5,
    };

    private void SetInc(int i, ulong v)
    {
        switch (i)
        {
            case 0: _i0 = v; break;
            case 1: _i1 = v; break;
            case 2: _i2 = v; break;
            case 3: _i3 = v; break;
            case 4: _i4 = v; break;
            default: _i5 = v; break;
        }
    }

    public bool Equals(DetRng other) =>
        _s0 == other._s0 && _s1 == other._s1 && _s2 == other._s2 &&
        _s3 == other._s3 && _s4 == other._s4 && _s5 == other._s5 &&
        _i0 == other._i0 && _i1 == other._i1 && _i2 == other._i2 &&
        _i3 == other._i3 && _i4 == other._i4 && _i5 == other._i5;

    public override bool Equals(object? obj) => obj is DetRng o && Equals(o);

    public override int GetHashCode() => HashCode.Combine(_s0, _s1, _s2, _s3, _s4, _s5);

    public static bool operator ==(DetRng a, DetRng b) => a.Equals(b);
    public static bool operator !=(DetRng a, DetRng b) => !a.Equals(b);
}
