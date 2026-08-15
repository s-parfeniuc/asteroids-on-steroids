using System;

namespace AsteroidsSim.Tests.Math;

/// <summary>Helpers for comparing floats by ulp distance rather than by epsilon.</summary>
internal static class Ulp
{
    /// <summary>
    /// Distance in representable floats between <paramref name="a"/> and <paramref name="b"/>.
    /// Returns <see cref="long.MaxValue"/> if either is NaN, or if they differ in finiteness.
    /// </summary>
    public static long Distance(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return float.IsNaN(a) && float.IsNaN(b) ? 0 : long.MaxValue;
        if (a == b) return 0;
        if (float.IsInfinity(a) || float.IsInfinity(b)) return long.MaxValue;

        int ia = BitConverter.SingleToInt32Bits(a);
        int ib = BitConverter.SingleToInt32Bits(b);

        // Map the sign-magnitude layout onto a monotonic ordering.
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;

        return System.Math.Abs((long)ia - ib);
    }

    /// <summary>Runs <paramref name="f"/> over a sampled domain and returns the worst ulp error.</summary>
    public static (long worst, float at) Sweep(
        Func<float, float> actual, Func<float, float> expected, float lo, float hi, int samples)
    {
        long worst = 0;
        float at = lo;
        for (int i = 0; i <= samples; i++)
        {
            float x = lo + (hi - lo) * (i / (float)samples);
            long d = Distance(actual(x), expected(x));
            if (d > worst) { worst = d; at = x; }
        }
        return (worst, at);
    }
}
