using System;

namespace AsteroidsSim.Fracture;

/// <summary>
/// The JavaScript prototype's generator (mulberry32), reproduced bit-exactly.
/// </summary>
/// <remarks>
/// <para><b>Why this exists alongside <see cref="Math.DetRng"/>.</b> Body construction consumes
/// random numbers in a fixed order — two draws per tessellation grid point (including the points
/// that are rejected), the outline ring, then one Weibull draw per bond. The shapes that come out
/// are therefore a pure function of the generator. To compare the port's fragment counts and
/// conservation metrics against <c>prototypes/test-v9.js</c>, both sides must build the <i>same
/// bodies</i>, which means the port must consume the same stream. <c>DetRng</c> (PCG32) is the
/// project's generator for everything the prototype does not define — waves, loot, AI.</para>
///
/// <para>The algorithm is integer-only — 32-bit wrapping add, xor, shift and multiply — so it is
/// exactly reproducible in any language. JavaScript's <c>Math.imul</c> is a 32-bit wrapping
/// multiply, which is what unchecked <see cref="uint"/> multiplication is here; JavaScript's
/// <c>&gt;&gt;&gt;</c> is an unsigned shift, likewise. The final division by 2^32 is exact in
/// <see cref="double"/>, so the mantissa carries no rounding of its own.</para>
///
/// <para>It is a plain mutable struct with a single field, so snapshotting it is a copy.</para>
/// </remarks>
public struct ProtoRng : IEquatable<ProtoRng>
{
    private uint _state;

    /// <summary>The prototype's default seed (<c>cfg.seed</c>).</summary>
    public const int DefaultSeed = 12345;

    public ProtoRng(int seed) => _state = unchecked((uint)seed);

    /// <summary>Raw state, for snapshot and equality. Not meaningful on its own.</summary>
    public uint State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>
    /// The prototype's <c>srand()</c>: the next value in [0, 1), as a double.
    /// </summary>
    public double NextDouble()
    {
        unchecked
        {
            _state += 0x6D2B79F5u;
            uint s = _state;
            uint t = (s ^ (s >> 15)) * (1u | s);
            t = ((t + ((t ^ (t >> 7)) * (61u | t))) ^ t);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>The prototype's <c>rand(a, b)</c>: uniform in [a, b).</summary>
    public double Range(double a, double b) => a + NextDouble() * (b - a);

    public bool Equals(ProtoRng other) => _state == other._state;
    public override bool Equals(object? obj) => obj is ProtoRng o && Equals(o);
    public override int GetHashCode() => unchecked((int)_state);
    public static bool operator ==(ProtoRng a, ProtoRng b) => a.Equals(b);
    public static bool operator !=(ProtoRng a, ProtoRng b) => !a.Equals(b);
}
