using System;
using System.Runtime.CompilerServices;

namespace AsteroidsSim.Math;

/// <summary>
/// Deterministic floating-point math for the simulation core.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>MathF.Sin</c> and friends call the platform C runtime — glibc on
/// Linux, ucrtbase on Windows, Apple's libm on macOS. Those differ in the last ulp, and under lockstep
/// netcode a last-ulp difference desyncs the match. Every function here is implemented in terms of
/// operations that IEEE 754 specifies exactly, so results are bit-identical on every platform that
/// complies with it.</para>
///
/// <para><b>What is safe to use.</b> Only <c>+ - * /</c>, comparisons, and bit reinterpretation. These
/// are exactly specified by IEEE 754-2008 and produce identical results everywhere. Two .NET properties
/// make this hold: the JIT never contracts <c>a*b+c</c> into an FMA (only an explicit
/// <c>Math.FusedMultiplyAdd</c> does that), and .NET Core has no x87 excess precision on any supported
/// target. <c>Sqrt</c> is also exactly specified, so it is passed through to the hardware.</para>
///
/// <para><b>Accuracy.</b> These are ported from the FDLIBM / FreeBSD msun lineage (the same source musl,
/// OpenLibm and Rust's <c>libm</c> crate derive from). Kernels evaluate in <c>double</c> and return
/// <c>float</c>, so roughly 29 bits of headroom absorb the polynomial error: results are within 1 ulp of
/// correctly-rounded for the tested domains. They are <i>not</i> claimed to be correctly-rounded — the
/// contract is determinism first, accuracy second.</para>
///
/// <para><b>Domain note for <see cref="Sin"/>/<see cref="Cos"/>/<see cref="Tan"/>.</b> Argument reduction
/// uses two-stage Cody–Waite, which is exact for |x| &lt; 2^20 (≈ 1.05e6 radians) — far beyond any angle a
/// game produces. Above that, accuracy degrades gracefully; results stay bit-identical across platforms,
/// which is the property that matters here.</para>
///
/// <para>Derived from FDLIBM (Sun Microsystems, 1993) via FreeBSD msun. See THIRD_PARTY.md.</para>
/// </remarks>
public static class SimMath
{
    // ── Exactly-specified passthroughs ────────────────────────────────────────
    // IEEE 754 requires these to be correctly rounded, so every platform agrees.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float x) => MathF.Sqrt(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Abs(float x) => MathF.Abs(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b) => MathF.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b) => MathF.Max(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Floor(float x) => MathF.Floor(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Ceiling(float x) => MathF.Ceiling(x);

    /// <summary>Round half to even (banker's rounding) — exactly specified, so deterministic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Round(float x) => MathF.Round(x);

    /// <summary>
    /// Euclidean length of (x, y). Built from multiply and <see cref="Sqrt"/> only, so it is
    /// exactly specified and needs no kernel.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the scaled libm <c>hypot</c>. That algorithm exists to avoid overflow when
    /// x*x would exceed the exponent range, and it costs a branch plus a division on every call.
    /// The simulation's magnitudes are bounded (positions, velocities and stretches all live well
    /// inside 1e18, where the naive form cannot overflow), and every fracture hot path calls this,
    /// so the naive form is both faster and — because it uses only exactly-specified operations —
    /// trivially deterministic. Callers that genuinely need the full float range must not use this.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// <summary>
    /// Sign of <paramref name="x"/> as -1, 0 or +1. Returns 0 for both zeros and for NaN, so it
    /// never propagates a NaN into a magnitude.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sign(float x) => x > 0f ? 1f : x < 0f ? -1f : 0f;

    /// <summary>Clamp to [min, max]. Comparison-only, so exactly specified.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float x, float min, float max) => x < min ? min : x > max ? max : x;

    /// <summary>
    /// Linear interpolation, written as <c>a + (b - a) * t</c> to match the form used throughout
    /// the ported code. Not the fused <c>a*(1-t) + b*t</c> form — the two differ in the last ulp
    /// and only one of them can be the contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public const float PI = 3.14159265358979323846f;
    public const float TwoPI = 6.28318530717958647692f;
    public const float HalfPI = 1.57079632679489661923f;

    // ══════════════════════════════════════════════════════════════════════════
    //  Trigonometry
    // ══════════════════════════════════════════════════════════════════════════

    // Two-stage Cody–Waite split of pi/2. Each part has trailing zero mantissa
    // bits so that k * part is exact for the k we admit.
    private const double InvPio2 = 6.36619772367581382433e-01; // 2/pi
    private const double Pio2_1 = 1.57079632673412561417e+00;  // first 33 bits of pi/2
    private const double Pio2_1t = 6.07710050650619224932e-11; // pi/2 - Pio2_1
    private const double Pio2_2 = 6.07710050630396597660e-11;
    private const double Pio2_2t = 2.02226624879595063154e-21;

    /// <summary>Reduce <paramref name="x"/> to r in [-pi/4, pi/4] and return the quadrant.</summary>
    private static int ReducePio2(double x, out double r)
    {
        double ax = x < 0 ? -x : x;

        if (ax <= 0.785398163397448278999) // |x| <= pi/4: no reduction needed
        {
            r = x;
            return 0;
        }

        // Beyond |x| = 2^26 a float's ulp exceeds 2*pi, so consecutive representable
        // inputs differ by more than a full period and the argument carries no phase
        // information at all — every answer is equally defensible. Return a fixed one.
        //
        // This guard is NOT cosmetic. Without it the conversion below is an
        // out-of-range double->int cast, which C# leaves unspecified and which the
        // architectures genuinely disagree on: x86-64 `cvttsd2si` yields int.MinValue,
        // ARM64 `fcvtzs` SATURATES to int.MaxValue. `n & 3` is then 0 on x86 and 3 on
        // ARM, selecting a different quadrant — which is exactly how the determinism
        // gate first caught this (Cos and Tan diverged on macos-arm64, Sin did not).
        if (ax >= 67108864.0)   // 2^26
        {
            r = 0.0;
            return 0;           // => Sin -> 0, Cos -> 1, Tan -> 0
        }

        double fn = x * InvPio2;
        // Round half away from zero, written explicitly so it does not depend on
        // Math.Round's midpoint mode. |fn| < 2^26 * (2/pi) here, comfortably inside
        // int range, so the cast is well-defined on every architecture.
        int n = (int)(fn >= 0 ? fn + 0.5 : fn - 0.5);
        double dn = n;

        // Stage 1: subtract the leading 33 bits of n·(pi/2). Exact for |n| < 2^20.
        double t = x - dn * Pio2_1;

        // Stage 2: subtract the remainder, split again so the cancellation stays
        // exact. Pio2_1t ≈ Pio2_2 + Pio2_2t, so this refines what stage 1 left.
        // Always executed — when it isn't needed the correction is zero.
        double w = dn * Pio2_2;
        double r2 = t - w;
        double corr = dn * Pio2_2t - ((t - r2) - w);
        r = r2 - corr;

        return n & 3;
    }

    // sin(x) on [-pi/4, pi/4], FDLIBM __kernel_sin coefficients.
    private const double S1 = -1.66666666666666324348e-01;
    private const double S2 = 8.33333333332248946124e-03;
    private const double S3 = -1.98412698298579493134e-04;
    private const double S4 = 2.75573137070700676789e-06;
    private const double S5 = -2.50507602534068634195e-08;
    private const double S6 = 1.58969099521155010221e-10;

    private static double KernelSin(double x)
    {
        // |x| < 2^-27: sin(x) == x to within a rounding error, and taking this
        // path is what preserves the sign of zero. Without it, x + v*(S1 + ...)
        // turns -0.0 into +0.0, because S1 is negative.
        if (x > -7.4505805969238281e-09 && x < 7.4505805969238281e-09) return x;

        double z = x * x;
        double v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        return x + v * (S1 + z * r);
    }

    // cos(x) on [-pi/4, pi/4], FDLIBM __kernel_cos coefficients.
    private const double C1 = 4.16666666666666019037e-02;
    private const double C2 = -1.38888888888741095749e-03;
    private const double C3 = 2.48015872894767294178e-05;
    private const double C4 = -2.75573143513906633035e-07;
    private const double C5 = 2.08757232129817482790e-09;
    private const double C6 = -1.13596475577881948265e-11;

    private static double KernelCos(double x)
    {
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        double hz = 0.5 * z;
        double w = 1.0 - hz;
        return w + ((1.0 - w) - hz + z * r);
    }

    public static float Sin(float xf)
    {
        if (float.IsNaN(xf) || float.IsInfinity(xf)) return float.NaN;

        int q = ReducePio2(xf, out double r);
        return q switch
        {
            0 => (float)KernelSin(r),
            1 => (float)KernelCos(r),
            2 => (float)(-KernelSin(r)),
            _ => (float)(-KernelCos(r)),
        };
    }

    public static float Cos(float xf)
    {
        if (float.IsNaN(xf) || float.IsInfinity(xf)) return float.NaN;

        int q = ReducePio2(xf, out double r);
        return q switch
        {
            0 => (float)KernelCos(r),
            1 => (float)(-KernelSin(r)),
            2 => (float)(-KernelCos(r)),
            _ => (float)KernelSin(r),
        };
    }

    /// <summary>Sine and cosine of the same angle, sharing one argument reduction.</summary>
    public static void SinCos(float xf, out float sin, out float cos)
    {
        if (float.IsNaN(xf) || float.IsInfinity(xf)) { sin = float.NaN; cos = float.NaN; return; }

        int q = ReducePio2(xf, out double r);
        double s = KernelSin(r), c = KernelCos(r);
        switch (q)
        {
            case 0: sin = (float)s; cos = (float)c; break;
            case 1: sin = (float)c; cos = (float)(-s); break;
            case 2: sin = (float)(-s); cos = (float)(-c); break;
            default: sin = (float)(-c); cos = (float)s; break;
        }
    }

    public static float Tan(float xf)
    {
        if (float.IsNaN(xf) || float.IsInfinity(xf)) return float.NaN;

        int q = ReducePio2(xf, out double r);
        double s = KernelSin(r), c = KernelCos(r);
        // Odd quadrants swap the roles and negate.
        return (q & 1) == 0 ? (float)(s / c) : (float)(-c / s);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Arctangent  (FDLIBM __ieee754_atan)
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly double[] AtanHi =
    {
        4.63647609000806093515e-01, // atan(0.5)
        7.85398163397448278999e-01, // atan(1.0)
        9.82793723247329054082e-01, // atan(1.5)
        1.57079632679489655800e+00, // atan(inf)
    };

    private static readonly double[] AtanLo =
    {
        2.26987774529616870924e-17,
        3.06161699786838301793e-17,
        1.39033110312309984516e-17,
        6.12323399573676603587e-17,
    };

    private const double aT0 = 3.33333333333329318027e-01;
    private const double aT1 = -1.99999999998764832476e-01;
    private const double aT2 = 1.42857142725034663711e-01;
    private const double aT3 = -1.11111104054623557880e-01;
    private const double aT4 = 9.09088713343650656196e-02;
    private const double aT5 = -7.69187620504482999495e-02;
    private const double aT6 = 6.66107313738753120669e-02;
    private const double aT7 = -5.83357013379057348645e-02;
    private const double aT8 = 4.97687799461593236017e-02;
    private const double aT9 = -3.65315727442169155270e-02;
    private const double aT10 = 1.62858201153657823623e-02;

    private static double AtanD(double x)
    {
        double ax = x < 0 ? -x : x;
        bool neg = x < 0;

        if (ax >= 7.30e+18) // |x| >= 2^63: atan(x) -> +-pi/2
            return neg ? -(AtanHi[3] + AtanLo[3]) : AtanHi[3] + AtanLo[3];

        int id;
        if (ax < 0.4375)
        {
            if (ax < 3.7252902984e-09) return x; // |x| < 2^-28: atan(x) ~ x
            id = -1;
        }
        else if (ax < 1.1875)
        {
            if (ax < 0.6875) { id = 0; ax = (2.0 * ax - 1.0) / (2.0 + ax); }
            else { id = 1; ax = (ax - 1.0) / (ax + 1.0); }
        }
        else if (ax < 2.4375) { id = 2; ax = (ax - 1.5) / (1.0 + 1.5 * ax); }
        else { id = 3; ax = -1.0 / ax; }

        double z = ax * ax;
        double w = z * z;
        double s1 = z * (aT0 + w * (aT2 + w * (aT4 + w * (aT6 + w * (aT8 + w * aT10)))));
        double s2 = w * (aT1 + w * (aT3 + w * (aT5 + w * (aT7 + w * aT9))));

        if (id < 0) return x - x * (s1 + s2);

        double r = AtanHi[id] - ((ax * (s1 + s2) - AtanLo[id]) - ax);
        return neg ? -r : r;
    }

    public static float Atan(float x)
    {
        if (float.IsNaN(x)) return float.NaN;
        if (float.IsPositiveInfinity(x)) return (float)(AtanHi[3] + AtanLo[3]);
        if (float.IsNegativeInfinity(x)) return (float)(-(AtanHi[3] + AtanLo[3]));
        return (float)AtanD(x);
    }

    private const double PiD = 3.14159265358979311600e+00;
    private const double PiLoD = 1.2246467991473531772e-16;

    /// <summary>Four-quadrant arctangent. Special cases follow C99 / IEEE 754.</summary>
    public static float Atan2(float y, float x)
    {
        if (float.IsNaN(y) || float.IsNaN(x)) return float.NaN;

        // x == 1 is the common case; fall through to the general path anyway for
        // exactness rather than special-casing it.
        if (x == 0.0f && y == 0.0f)
        {
            // atan2(+-0, +0) = +-0 ; atan2(+-0, -0) = +-pi
            bool xNeg = IsNegative(x);
            if (!xNeg) return y;                       // preserves the sign of zero
            return IsNegative(y) ? -(float)PiD : (float)PiD;
        }

        if (float.IsInfinity(y))
        {
            if (float.IsInfinity(x))
            {
                double q = IsNegative(x) ? 3.0 * PiD / 4.0 : PiD / 4.0;
                return IsNegative(y) ? (float)(-q) : (float)q;
            }
            return IsNegative(y) ? -(float)(PiD / 2.0) : (float)(PiD / 2.0);
        }

        if (float.IsInfinity(x))
        {
            if (IsNegative(x))
                return IsNegative(y) ? -(float)PiD : (float)PiD;
            return IsNegative(y) ? -0.0f : 0.0f;
        }

        if (y == 0.0f)
            return IsNegative(x) ? (IsNegative(y) ? -(float)PiD : (float)PiD)
                                 : y;   // preserves the sign of zero

        if (x == 0.0f)
            return IsNegative(y) ? -(float)(PiD / 2.0) : (float)(PiD / 2.0);

        double a = AtanD((double)y / x);
        if (x > 0.0f) return (float)a;

        // x < 0: shift by +-pi, using the split constant so the result is accurate
        // near the discontinuity.
        return IsNegative(y) ? (float)(a - (PiD + PiLoD)) : (float)(a + (PiD + PiLoD));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNegative(float v) => BitConverter.SingleToInt32Bits(v) < 0;

    // ══════════════════════════════════════════════════════════════════════════
    //  Exponential and logarithm
    // ══════════════════════════════════════════════════════════════════════════

    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double InvLn2 = 1.44269504088896338700e+00;

    /// <summary>e^x in double. Only basic ops plus an exact power-of-two scale.</summary>
    private static double ExpD(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x > 709.782712893384) return double.PositiveInfinity;
        if (x < -745.133219101941) return 0.0;

        // k = round(x / ln2), written without Math.Round so the midpoint rule is explicit.
        double fk = x * InvLn2;
        int k = (int)(fk >= 0 ? fk + 0.5 : fk - 0.5);
        double dk = k;

        // r = x - k*ln2, in two parts to keep the cancellation exact.
        double r = (x - dk * Ln2Hi) - dk * Ln2Lo;

        // e^r on |r| <= ln2/2 ~ 0.3466. Degree 10 Taylor: the next term is
        // r^11/39916800 < 1.2e-12 relative — far inside double's headroom for a
        // float result.
        double e = 1.0 + r * (1.0
                 + r * (0.5
                 + r * (1.66666666666666666e-01
                 + r * (4.16666666666666666e-02
                 + r * (8.33333333333333333e-03
                 + r * (1.38888888888888889e-03
                 + r * (1.98412698412698413e-04
                 + r * (2.48015873015873016e-05
                 + r * (2.75573192239858907e-06
                 + r * 2.75573192239858907e-07)))))))));

        return Scale2(e, k);
    }

    /// <summary>Multiply by 2^k by constructing the scale factor exactly.</summary>
    private static double Scale2(double v, int k)
    {
        // Split large exponents so the intermediate never overflows or denormalises.
        if (k > 1023)
        {
            v *= BitConverter.Int64BitsToDouble((long)(1023 + 1023) << 52);
            k -= 1023;
            if (k > 1023)
            {
                v *= BitConverter.Int64BitsToDouble((long)(1023 + 1023) << 52);
                k -= 1023;
                if (k > 1023) k = 1023;
            }
        }
        else if (k < -1022)
        {
            v *= BitConverter.Int64BitsToDouble((long)(-1022 + 1023) << 52);
            k += 1022;
            if (k < -1022)
            {
                v *= BitConverter.Int64BitsToDouble((long)(-1022 + 1023) << 52);
                k += 1022;
                if (k < -1022) k = -1022;
            }
        }
        return v * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
    }

    public static float Exp(float x)
    {
        if (float.IsNaN(x)) return float.NaN;
        if (float.IsPositiveInfinity(x)) return float.PositiveInfinity;
        if (float.IsNegativeInfinity(x)) return 0.0f;
        return (float)ExpD(x);
    }

    // FDLIBM __ieee754_log coefficients.
    private const double Lg1 = 6.666666666666735130e-01;
    private const double Lg2 = 3.999999999940941908e-01;
    private const double Lg3 = 2.857142874366239149e-01;
    private const double Lg4 = 2.222219843214978396e-01;
    private const double Lg5 = 1.818357216161805012e-01;
    private const double Lg6 = 1.531383769920937332e-01;
    private const double Lg7 = 1.479819860511658591e-01;

    /// <summary>Natural log in double, via the FDLIBM decomposition x = 2^k * m.</summary>
    private static double LogD(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x < 0.0) return double.NaN;
        if (x == 0.0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return double.PositiveInfinity;

        long bits = BitConverter.DoubleToInt64Bits(x);
        int k = 0;

        if (bits < 0x0010_0000_0000_0000L) // subnormal: scale up by 2^54 first
        {
            k -= 54;
            x *= 1.80143985094819840000e+16;
            bits = BitConverter.DoubleToInt64Bits(x);
        }

        int hi = (int)(bits >> 32);
        k += (hi >> 20) - 1023;
        hi &= 0x000F_FFFF;

        // Normalise the mantissa into [sqrt(2)/2, sqrt(2)).
        int i = (hi + 0x9_5F64) & 0x10_0000;
        bits = ((long)(hi | (i ^ 0x3FF0_0000)) << 32) | (bits & 0xFFFF_FFFFL);
        x = BitConverter.Int64BitsToDouble(bits);
        k += i >> 20;

        double f = x - 1.0;

        // |f| small: use the short series to avoid cancellation.
        if ((0x000F_FFFF & (2 + hi)) < 3)
        {
            if (f == 0.0) return k == 0 ? 0.0 : k * Ln2Hi + k * Ln2Lo;
            double rr = f * f * (0.5 - 0.33333333333333333 * f);
            if (k == 0) return f - rr;
            return k * Ln2Hi - ((rr - k * Ln2Lo) - f);
        }

        double s = f / (2.0 + f);
        double dk = k;
        double z = s * s;
        double w = z * z;
        double t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        double t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        double r = t2 + t1;
        double hfsq = 0.5 * f * f;

        if (k == 0) return f - (hfsq - s * (hfsq + r));
        return dk * Ln2Hi - ((hfsq - (s * (hfsq + r) + dk * Ln2Lo)) - f);
    }

    public static float Log(float x)
    {
        if (float.IsNaN(x)) return float.NaN;
        return (float)LogD(x);
    }

    public static float Log2(float x)
    {
        if (float.IsNaN(x)) return float.NaN;
        return (float)(LogD(x) * InvLn2);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Power
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// x^y. Computed as exp(y·ln x) in double: the float result needs 24 bits and
    /// double carries 53, so the intermediate error is far below a float ulp across
    /// the whole non-overflowing range. Special cases follow C99 / IEEE 754.
    /// </summary>
    public static float Pow(float x, float y)
    {
        // pow(x, +-0) = 1 for every x, including NaN.
        if (y == 0.0f) return 1.0f;
        // pow(1, y) = 1 for every y, including NaN.
        if (x == 1.0f) return 1.0f;

        if (float.IsNaN(x) || float.IsNaN(y)) return float.NaN;

        bool yIsInt = IsInteger(y);
        bool yIsOddInt = yIsInt && IsOddInteger(y);
        bool xNeg = IsNegative(x);

        if (float.IsInfinity(y))
        {
            float ax0 = MathF.Abs(x);
            if (ax0 == 1.0f) return 1.0f;                       // pow(-1, +-inf) = 1
            bool big = ax0 > 1.0f;
            bool yPos = y > 0.0f;
            return (big == yPos) ? float.PositiveInfinity : 0.0f;
        }

        if (x == 0.0f)
        {
            if (y < 0.0f)
                return (xNeg && yIsOddInt) ? float.NegativeInfinity : float.PositiveInfinity;
            return (xNeg && yIsOddInt) ? -0.0f : 0.0f;
        }

        if (float.IsInfinity(x))
        {
            if (xNeg)
            {
                if (y < 0.0f) return yIsOddInt ? -0.0f : 0.0f;
                return yIsOddInt ? float.NegativeInfinity : float.PositiveInfinity;
            }
            return y < 0.0f ? 0.0f : float.PositiveInfinity;
        }

        // Negative base with a non-integer exponent is undefined.
        if (xNeg && !yIsInt) return float.NaN;

        double ax = xNeg ? -(double)x : x;
        double result = ExpD((double)y * LogD(ax));

        return (xNeg && yIsOddInt) ? (float)(-result) : (float)result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInteger(float v) => v == MathF.Truncate(v);

    private static bool IsOddInteger(float v)
    {
        // |v| >= 2^24 means every representable float is an even integer.
        float a = MathF.Abs(v);
        if (a >= 16777216.0f) return false;
        return (a * 0.5f) != MathF.Truncate(a * 0.5f);
    }
}
