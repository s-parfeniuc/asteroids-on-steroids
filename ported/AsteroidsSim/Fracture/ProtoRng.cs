using System;

namespace AsteroidsSim.Fracture;

/// <summary>
/// The body-construction generator (mulberry32).
/// </summary>
/// <remarks>
/// <para>Body construction consumes random numbers in a fixed order — the outline ring, two draws
/// per tessellation grid point (including the points that are rejected), then one Weibull draw per
/// bond — so the shapes that come out are a pure function of this stream. <see cref="Math.DetRng"/>
/// (PCG32) is the generator for everything else.</para>
///
/// <para>The algorithm is integer-only — 32-bit wrapping add, xor, shift and multiply — so it is
/// exactly reproducible on every platform. The final division by 2^32 is exact in
/// <see cref="double"/>, so the mantissa carries no rounding of its own.</para>
///
/// <para>It is a plain mutable struct with a single field, so snapshotting it is a copy.</para>
/// </remarks>
public struct ProtoRng : IEquatable<ProtoRng>
{
    private uint _state;

    /// <summary>The seed every scenario uses unless told otherwise.</summary>
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
