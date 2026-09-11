using System;
using System.Globalization;

namespace AsteroidsSim.Math;

/// <summary>
/// 128-bit FNV-1a, integer-only.
/// </summary>
/// <remarks>
/// <para>Used to fingerprint simulation state for desync detection and for the cross-platform CI
/// gate. Integer-only by construction, so the hash can never itself be a source of divergence —
/// the same reasoning as the existing <c>tools/MathFingerprint</c>, which carries a private copy of
/// this algorithm and must keep it (its recorded baseline hash is a published constant).</para>
///
/// <para>Floats enter through <see cref="AddFloat"/>, which canonicalises NaN. IEEE 754 leaves NaN
/// sign and payload unspecified and x86-64 and ARM64 genuinely differ, so a raw bit copy would
/// report a false desync. Collapsing every NaN to one value keeps the gate honest: a NaN where
/// another machine produced a number still differs.</para>
/// </remarks>
public struct Fnv128
{
    private ulong _hi;
    private ulong _lo;

    public static Fnv128 Create() => new() { _hi = 0x6C62272E07BB0142UL, _lo = 0x62B821756295C58DUL };

    /// <summary>Folds in four octets, little-endian, one at a time.</summary>
    public void Add(int value)
    {
        for (int b = 0; b < 4; b++)
        {
            _lo ^= (byte)(value >> (b * 8));
            Mul();
        }
    }

    public void Add(long value) { Add((int)value); Add((int)(value >> 32)); }

    /// <summary>Folds in a float by its bits, with every NaN collapsed to one canonical value.</summary>
    public void AddFloat(float v)
        => Add(float.IsNaN(v) ? unchecked((int)0x7FC00000) : BitConverter.SingleToInt32Bits(v));

    public void Add(bool v) => Add(v ? 1 : 0);

    private void Mul()
    {
        // 128-bit FNV prime is 2^88 + 0x13B, applied in 64-bit halves.
        const ulong p = 0x13B;
        ulong loLo = _lo & 0xFFFFFFFFUL;
        ulong loHi = _lo >> 32;
        ulong r0 = loLo * p;
        ulong r1 = loHi * p + (r0 >> 32);
        ulong newLo = (r1 << 32) | (r0 & 0xFFFFFFFFUL);
        ulong newHi = _hi * p + (r1 >> 32) + (_lo << 24);
        _lo = newLo;
        _hi = newHi;
    }

    public readonly string ToHex()
        => _hi.ToString("x16", CultureInfo.InvariantCulture)
         + _lo.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Low 32 bits, for the periodic desync check that goes over the wire.</summary>
    public readonly uint ToUInt32() => (uint)_lo;
}
