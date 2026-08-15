using System;
using AsteroidsSim.Math;
using Xunit;

namespace AsteroidsSim.Tests.Math;

/// <summary>
/// Special-case behaviour. These follow C99 / IEEE 754, and they matter more than accuracy does:
/// a wrong NaN or a wrong zero sign propagates through the simulation and desyncs a match, whereas a
/// last-ulp difference in <c>Sin</c> would not.
/// </summary>
public sealed class SimMathEdgeCaseTests
{
    private static void AssertSameBits(float expected, float actual, string what)
    {
        Assert.True(
            BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            $"{what}: expected {expected:R} (0x{BitConverter.SingleToInt32Bits(expected):X8}), " +
            $"got {actual:R} (0x{BitConverter.SingleToInt32Bits(actual):X8})");
    }

    // ── Pow: the C99 table, case by case ─────────────────────────────────────

    [Theory]
    [InlineData(2f)] [InlineData(-2f)] [InlineData(0f)]
    [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void Pow_AnythingToZero_IsOne(float x)
    {
        Assert.Equal(1f, SimMath.Pow(x, 0f));
        Assert.Equal(1f, SimMath.Pow(x, -0f));
        Assert.Equal(1f, SimMath.Pow(-x, 0f));
    }

    [Theory]
    [InlineData(2f)] [InlineData(-2f)] [InlineData(0f)]
    [InlineData(float.PositiveInfinity)] [InlineData(float.NaN)]
    public void Pow_OneToAnything_IsOne(float y) => Assert.Equal(1f, SimMath.Pow(1f, y));

    [Fact]
    public void Pow_NegativeBase_NonIntegerExponent_IsNaN()
    {
        Assert.True(float.IsNaN(SimMath.Pow(-2f, 0.5f)));
        Assert.True(float.IsNaN(SimMath.Pow(-2f, 1.5f)));
        Assert.True(float.IsNaN(SimMath.Pow(-0.5f, 2.5f)));
    }

    [Fact]
    public void Pow_NegativeBase_IntegerExponent_KeepsParitySign()
    {
        Assert.Equal(-8f, SimMath.Pow(-2f, 3f));
        Assert.Equal(16f, SimMath.Pow(-2f, 4f));
        Assert.Equal(-0.125f, SimMath.Pow(-2f, -3f));
    }

    [Fact]
    public void Pow_Zero_FollowsC99()
    {
        AssertSameBits(0f, SimMath.Pow(0f, 3f), "pow(+0, 3)");
        AssertSameBits(-0f, SimMath.Pow(-0f, 3f), "pow(-0, 3) is -0 (odd exponent)");
        AssertSameBits(0f, SimMath.Pow(-0f, 4f), "pow(-0, 4) is +0 (even exponent)");
        Assert.Equal(float.PositiveInfinity, SimMath.Pow(0f, -3f));
        Assert.Equal(float.NegativeInfinity, SimMath.Pow(-0f, -3f));
        Assert.Equal(float.PositiveInfinity, SimMath.Pow(-0f, -4f));
    }

    [Fact]
    public void Pow_Infinities_FollowC99()
    {
        Assert.Equal(1f, SimMath.Pow(-1f, float.PositiveInfinity));
        Assert.Equal(1f, SimMath.Pow(-1f, float.NegativeInfinity));

        Assert.Equal(0f, SimMath.Pow(0.5f, float.PositiveInfinity));
        Assert.Equal(float.PositiveInfinity, SimMath.Pow(0.5f, float.NegativeInfinity));
        Assert.Equal(float.PositiveInfinity, SimMath.Pow(2f, float.PositiveInfinity));
        Assert.Equal(0f, SimMath.Pow(2f, float.NegativeInfinity));

        Assert.Equal(float.PositiveInfinity, SimMath.Pow(float.PositiveInfinity, 2f));
        Assert.Equal(0f, SimMath.Pow(float.PositiveInfinity, -2f));
        Assert.Equal(float.NegativeInfinity, SimMath.Pow(float.NegativeInfinity, 3f));
        Assert.Equal(float.PositiveInfinity, SimMath.Pow(float.NegativeInfinity, 4f));
        AssertSameBits(-0f, SimMath.Pow(float.NegativeInfinity, -3f), "pow(-inf, -3)");
        AssertSameBits(0f, SimMath.Pow(float.NegativeInfinity, -4f), "pow(-inf, -4)");
    }

    [Fact]
    public void Pow_NaN_Propagates()
    {
        Assert.True(float.IsNaN(SimMath.Pow(float.NaN, 2f)));
        Assert.True(float.IsNaN(SimMath.Pow(2f, float.NaN)));
    }

    // ── Atan2: quadrants and signed zeros ────────────────────────────────────

    [Fact]
    public void Atan2_SignedZeros_FollowC99()
    {
        AssertSameBits(0f, SimMath.Atan2(0f, 1f), "atan2(+0, +1)");
        AssertSameBits(-0f, SimMath.Atan2(-0f, 1f), "atan2(-0, +1)");
        Assert.Equal(SimMath.PI, SimMath.Atan2(0f, -1f), 5);
        Assert.Equal(-SimMath.PI, SimMath.Atan2(-0f, -1f), 5);
        AssertSameBits(0f, SimMath.Atan2(0f, 0f), "atan2(+0, +0)");
        AssertSameBits(-0f, SimMath.Atan2(-0f, 0f), "atan2(-0, +0)");
    }

    [Fact]
    public void Atan2_Infinities_FollowC99()
    {
        Assert.Equal(SimMath.PI / 4, SimMath.Atan2(float.PositiveInfinity, float.PositiveInfinity), 5);
        Assert.Equal(3 * SimMath.PI / 4, SimMath.Atan2(float.PositiveInfinity, float.NegativeInfinity), 5);
        Assert.Equal(-SimMath.PI / 4, SimMath.Atan2(float.NegativeInfinity, float.PositiveInfinity), 5);
        Assert.Equal(SimMath.PI / 2, SimMath.Atan2(float.PositiveInfinity, 1f), 5);
        Assert.Equal(SimMath.PI, SimMath.Atan2(1f, float.NegativeInfinity), 5);
        AssertSameBits(0f, SimMath.Atan2(1f, float.PositiveInfinity), "atan2(1, +inf)");
    }

    [Fact]
    public void Atan2_NaN_Propagates()
    {
        Assert.True(float.IsNaN(SimMath.Atan2(float.NaN, 1f)));
        Assert.True(float.IsNaN(SimMath.Atan2(1f, float.NaN)));
    }

    // ── Log / Exp ────────────────────────────────────────────────────────────

    [Fact]
    public void Log_EdgeCases()
    {
        Assert.Equal(float.NegativeInfinity, SimMath.Log(0f));
        Assert.True(float.IsNaN(SimMath.Log(-1f)));
        Assert.True(float.IsNaN(SimMath.Log(float.NaN)));
        Assert.Equal(float.PositiveInfinity, SimMath.Log(float.PositiveInfinity));
        AssertSameBits(0f, SimMath.Log(1f), "log(1)");
    }

    [Fact]
    public void Log_Subnormals_AreHandled()
    {
        float sub = BitConverter.Int32BitsToSingle(1); // smallest positive subnormal
        Assert.True(Ulp.Distance(SimMath.Log(sub), MathF.Log(sub)) <= 2);
    }

    [Fact]
    public void Exp_EdgeCases()
    {
        Assert.Equal(1f, SimMath.Exp(0f));
        Assert.Equal(float.PositiveInfinity, SimMath.Exp(float.PositiveInfinity));
        AssertSameBits(0f, SimMath.Exp(float.NegativeInfinity), "exp(-inf)");
        Assert.True(float.IsNaN(SimMath.Exp(float.NaN)));
        Assert.Equal(float.PositiveInfinity, SimMath.Exp(1000f));
        Assert.Equal(0f, SimMath.Exp(-1000f));
    }

    // ── Trig ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Trig_NonFiniteInput_IsNaN()
    {
        Assert.True(float.IsNaN(SimMath.Sin(float.NaN)));
        Assert.True(float.IsNaN(SimMath.Sin(float.PositiveInfinity)));
        Assert.True(float.IsNaN(SimMath.Cos(float.NegativeInfinity)));
        Assert.True(float.IsNaN(SimMath.Tan(float.NaN)));
    }

    /// <summary>
    /// Regression: huge arguments must produce a *defined* result on every architecture.
    /// </summary>
    /// <remarks>
    /// The determinism gate caught this on its first CI run — Cos and Tan diverged on
    /// macos-arm64 while Sin did not. Root cause: <c>ReducePio2</c> cast an out-of-range
    /// double to int, which C# leaves unspecified and the architectures answer differently
    /// (x86-64 `cvttsd2si` → int.MinValue; ARM64 `fcvtzs` saturates → int.MaxValue), so
    /// `n &amp; 3` selected quadrant 0 on one and quadrant 3 on the other.
    ///
    /// Above |x| = 2^26 a float's ulp exceeds 2*pi, so the input carries no phase
    /// information and any fixed answer is as defensible as another. What matters is that
    /// it is the *same* answer everywhere.
    /// </remarks>
    [Theory]
    [InlineData(67108864f)]        // exactly 2^26
    [InlineData(1e10f)]
    [InlineData(3.4e38f)]          // near float.MaxValue
    [InlineData(-67108864f)]
    [InlineData(-3.4e38f)]
    public void Trig_HugeArguments_AreDefined(float x)
    {
        AssertSameBits(0f, SimMath.Sin(x), $"Sin({x:R})");
        AssertSameBits(1f, SimMath.Cos(x), $"Cos({x:R})");
        AssertSameBits(0f, SimMath.Tan(x), $"Tan({x:R})");
    }

    /// <summary>The guard must not swallow the range where we do claim accuracy (|x| &lt; 2^20).</summary>
    [Fact]
    public void Trig_BelowExactReductionLimit_IsAccurate()
    {
        foreach (float x in new[] { 1048575f, -1048575f, 1000000f, 65536.5f })
        {
            Assert.True(Ulp.Distance(SimMath.Sin(x), MathF.Sin(x)) <= 2, $"Sin({x:R})");
            Assert.True(Ulp.Distance(SimMath.Cos(x), MathF.Cos(x)) <= 2, $"Cos({x:R})");
        }
    }

    /// <summary>
    /// Between 2^20 and 2^26 the two-stage Cody–Waite reduction loses accuracy, which the
    /// class documents. It must still return an in-range, finite, deterministic value —
    /// accuracy is not promised there, definedness is.
    /// </summary>
    [Fact]
    public void Trig_BetweenLimits_StaysInRangeAndFinite()
    {
        foreach (float x in new[] { 2097152f, 16777216f, 67108863f, -33554432f })
        {
            float s = SimMath.Sin(x), c = SimMath.Cos(x);
            Assert.True(float.IsFinite(s) && s >= -1f && s <= 1f, $"Sin({x:R}) = {s:R}");
            Assert.True(float.IsFinite(c) && c >= -1f && c <= 1f, $"Cos({x:R}) = {c:R}");
            // And it must be reproducible within this process, at minimum.
            AssertSameBits(s, SimMath.Sin(x), $"Sin({x:R}) repeatability");
        }
    }

    [Fact]
    public void Sin_Zero_PreservesSign()
    {
        AssertSameBits(0f, SimMath.Sin(0f), "sin(+0)");
        AssertSameBits(-0f, SimMath.Sin(-0f), "sin(-0)");
    }

    [Fact]
    public void SinCos_AgreesWithSeparateCalls()
    {
        for (int i = 0; i <= 10_000; i++)
        {
            float x = -20f + 40f * (i / 10_000f);
            SimMath.SinCos(x, out float s, out float c);
            AssertSameBits(SimMath.Sin(x), s, $"SinCos sin at {x:R}");
            AssertSameBits(SimMath.Cos(x), c, $"SinCos cos at {x:R}");
        }
    }

    [Fact]
    public void PythagoreanIdentity_HoldsTightly()
    {
        for (int i = 0; i <= 100_000; i++)
        {
            float x = -50f + 100f * (i / 100_000f);
            SimMath.SinCos(x, out float s, out float c);
            float err = MathF.Abs(s * s + c * c - 1f);
            Assert.True(err < 3e-7f, $"sin²+cos² off by {err:R} at x={x:R}");
        }
    }
}
