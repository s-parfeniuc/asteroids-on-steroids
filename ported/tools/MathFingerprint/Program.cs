using System;
using System.Globalization;
using AsteroidsSim.Math;

namespace AsteroidsSim.Tools.MathFingerprint;

/// <summary>
/// Evaluates every <see cref="SimMath"/> function over a fixed input vector and prints a 128-bit hash
/// of the raw result bits.
/// </summary>
/// <remarks>
/// <para>This is the determinism gate. CI runs it on Linux x64, Windows x64 and macOS arm64 and fails
/// the build if the hashes differ. It is the only mechanism that will catch a stray <c>MathF.Sin</c>
/// slipping into the simulation two years from now.</para>
///
/// <para>The hash itself is FNV-1a over the exact bit patterns — integer-only, so the hashing step can
/// never be the thing that varies.</para>
///
/// <para>Usage: <c>MathFingerprint [--verbose]</c>. Exit code 0 always; the comparison is CI's job.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        bool verbose = Array.IndexOf(args, "--verbose") >= 0;

        var h = new Fnv128();

        HashUnary(ref h, "Sin", SimMath.Sin, verbose);
        HashUnary(ref h, "Cos", SimMath.Cos, verbose);
        HashUnary(ref h, "Tan", SimMath.Tan, verbose);
        HashUnary(ref h, "Atan", SimMath.Atan, verbose);
        HashUnary(ref h, "Exp", SimMath.Exp, verbose);
        HashUnary(ref h, "Log", SimMath.Log, verbose);
        HashUnary(ref h, "Log2", SimMath.Log2, verbose);
        HashUnary(ref h, "Sqrt", SimMath.Sqrt, verbose);
        HashBinary(ref h, "Atan2", SimMath.Atan2, verbose);
        HashBinary(ref h, "Pow", SimMath.Pow, verbose);
        HashRng(ref h, verbose);

        Console.WriteLine(h.ToHex());
        return 0;
    }

    // ── the fixed input vector ────────────────────────────────────────────────

    /// <summary>
    /// Inputs are generated arithmetically rather than read from a file, so the vector cannot drift and
    /// the tool has no I/O. Covers ordinary values, boundaries, subnormals and specials.
    /// </summary>
    private static float[] BuildInputs()
    {
        var list = new System.Collections.Generic.List<float>(400_000);

        // Dense sweep over the range a game actually produces.
        for (int i = 0; i <= 200_000; i++)
            list.Add(-100f + 200f * (i / 200_000f));

        // Wide sweep, to exercise argument reduction.
        for (int i = 0; i <= 50_000; i++)
            list.Add(-1e6f + 2e6f * (i / 50_000f));

        // Every binade, top and bottom, positive and negative.
        for (int e = -126; e <= 127; e++)
        {
            float b = MathF.ScaleB(1f, e);
            if (!float.IsFinite(b) || b == 0f) continue;
            list.Add(b);
            list.Add(-b);
            list.Add(b * 1.9999999f);
            list.Add(-b * 1.9999999f);
        }

        // Subnormals and the boundary around them.
        for (int i = 1; i <= 64; i++)
        {
            list.Add(BitConverter.Int32BitsToSingle(i));
            list.Add(BitConverter.Int32BitsToSingle(unchecked((int)(0x8000_0000u | (uint)i))));
        }
        list.Add(float.Epsilon);
        list.Add(BitConverter.Int32BitsToSingle(0x0080_0000)); // smallest normal

        // Specials.
        list.Add(0f);
        list.Add(-0f);
        list.Add(1f);
        list.Add(-1f);
        list.Add(float.MaxValue);
        list.Add(float.MinValue);
        list.Add(float.PositiveInfinity);
        list.Add(float.NegativeInfinity);
        list.Add(float.NaN);

        return list.ToArray();
    }

    private static readonly float[] Inputs = BuildInputs();

    /// <summary>Smaller cross-product grid for the two-argument functions.</summary>
    private static float[] BuildPairInputs()
    {
        var list = new System.Collections.Generic.List<float>(1200);
        for (int i = 0; i <= 600; i++) list.Add(-30f + 60f * (i / 600f));
        list.Add(0f); list.Add(-0f); list.Add(1f); list.Add(-1f);
        list.Add(0.5f); list.Add(-0.5f); list.Add(2f); list.Add(-2f);
        list.Add(3f); list.Add(-3f); list.Add(4f); list.Add(-4f);
        list.Add(float.PositiveInfinity); list.Add(float.NegativeInfinity); list.Add(float.NaN);
        list.Add(float.MaxValue); list.Add(float.MinValue);
        return list.ToArray();
    }

    private static readonly float[] PairInputs = BuildPairInputs();

    // ── hashing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bit pattern of a result, with NaN payloads canonicalised.
    /// </summary>
    /// <remarks>
    /// IEEE 754 leaves the sign and payload bits of a NaN unspecified, and the
    /// architectures genuinely differ: x86-64 and ARM64 produce different bit
    /// patterns for e.g. <c>sqrt(-1)</c>. Hashing those raw reports a divergence
    /// that has no semantic content. Collapsing every NaN to one value keeps the
    /// gate honest about what matters — a NaN where another platform produced a
    /// number still differs, which is the case we must catch.
    /// </remarks>
    private static int ResultBits(float v) =>
        float.IsNaN(v) ? unchecked((int)0x7FC00000) : BitConverter.SingleToInt32Bits(v);

    private static void HashUnary(ref Fnv128 h, string name, Func<float, float> f, bool verbose)
    {
        var local = new Fnv128();
        foreach (float x in Inputs)
            local.Add(ResultBits(f(x)));

        if (verbose) Console.Error.WriteLine($"{name,-8} {local.ToHex()}  ({Inputs.Length} inputs)");
        h.Add(local);
    }

    private static void HashBinary(ref Fnv128 h, string name, Func<float, float, float> f, bool verbose)
    {
        var local = new Fnv128();
        foreach (float a in PairInputs)
            foreach (float b in PairInputs)
                local.Add(ResultBits(f(a, b)));

        if (verbose)
            Console.Error.WriteLine(
                $"{name,-8} {local.ToHex()}  ({PairInputs.Length}² = {PairInputs.Length * PairInputs.Length} inputs)");
        h.Add(local);
    }

    private static void HashRng(ref Fnv128 h, bool verbose)
    {
        var local = new Fnv128();
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var r = DetRng.FromSeed(seed);
            for (int i = 0; i < 512; i++)
            {
                local.Add((int)r.NextUInt(RngStream.Tessellation));
                local.Add(ResultBits(r.NextFloat(RngStream.Waves)));
                local.Add(r.NextInt(RngStream.Loot, 1000));
                local.Add(ResultBits(r.NextFloat(RngStream.Ai, -5f, 5f)));
            }
        }

        if (verbose) Console.Error.WriteLine($"{"DetRng",-8} {local.ToHex()}  (64 seeds × 512 draws)");
        h.Add(local);
    }

    /// <summary>128-bit FNV-1a. Integer-only, so the hash can never be the source of divergence.</summary>
    private struct Fnv128
    {
        private ulong _lo = 0x62B821756295C58D;
        private ulong _hi = 0x6C62272E07BB0142;

        public Fnv128() { }

        public void Add(int value)
        {
            for (int b = 0; b < 4; b++)
            {
                byte octet = (byte)(value >> (b * 8));
                _lo ^= octet;
                // Multiply by the 128-bit FNV prime (2^88 + 0x13B), done in 64-bit halves.
                ulong lo = _lo, hi = _hi;
                ulong loHi = lo >> 32, loLo = lo & 0xFFFF_FFFF;
                ulong p = 0x13B;
                ulong r0 = loLo * p;
                ulong r1 = loHi * p + (r0 >> 32);
                ulong newLo = (r1 << 32) | (r0 & 0xFFFF_FFFF);
                ulong newHi = hi * p + (r1 >> 32) + (lo << 24);
                _lo = newLo;
                _hi = newHi;
            }
        }

        public void Add(Fnv128 other)
        {
            Add((int)(other._lo & 0xFFFF_FFFF));
            Add((int)(other._lo >> 32));
            Add((int)(other._hi & 0xFFFF_FFFF));
            Add((int)(other._hi >> 32));
        }

        public readonly string ToHex() =>
            _hi.ToString("x16", CultureInfo.InvariantCulture) +
            _lo.ToString("x16", CultureInfo.InvariantCulture);
    }
}
