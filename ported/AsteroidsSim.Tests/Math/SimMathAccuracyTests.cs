using System;
using AsteroidsSim.Math;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Math;

/// <summary>
/// Accuracy of <see cref="SimMath"/> against the platform libm, measured in ulp.
/// </summary>
/// <remarks>
/// These do NOT assert equality with <c>MathF</c> — the entire point of SimMath is that it must not
/// depend on the platform libm. They assert that we are *close enough* to it, which is what makes the
/// port a drop-in replacement. Bit-identity across platforms is a separate test (see the fingerprint
/// tool and the CI compare job).
/// </remarks>
public sealed class SimMathAccuracyTests
{
    private readonly ITestOutputHelper _out;
    public SimMathAccuracyTests(ITestOutputHelper output) => _out = output;

    // Tolerance in ulp. Kernels evaluate in double and narrow to float, so the
    // realistic error is 0-1 ulp; 2 leaves headroom without hiding a real defect.
    private const long Tol = 2;

    private void Report(string name, long worst, float at)
    {
        _out.WriteLine($"{name,-12} worst = {worst} ulp  at x = {at:R}");
        Assert.True(worst <= Tol, $"{name}: {worst} ulp at x={at:R} exceeds tolerance {Tol}");
    }

    [Fact]
    public void Sin_MatchesLibm_OverGameRange()
    {
        var (w, at) = Ulp.Sweep(SimMath.Sin, MathF.Sin, -SimMath.TwoPI * 4, SimMath.TwoPI * 4, 200_000);
        Report("Sin", w, at);
    }

    [Fact]
    public void Cos_MatchesLibm_OverGameRange()
    {
        var (w, at) = Ulp.Sweep(SimMath.Cos, MathF.Cos, -SimMath.TwoPI * 4, SimMath.TwoPI * 4, 200_000);
        Report("Cos", w, at);
    }

    [Fact]
    public void Sin_MatchesLibm_OverWideRange()
    {
        var (w, at) = Ulp.Sweep(SimMath.Sin, MathF.Sin, -100_000f, 100_000f, 200_000);
        Report("Sin(wide)", w, at);
    }

    [Fact]
    public void Tan_MatchesLibm_AwayFromPoles()
    {
        // Sample inside one branch; near a pole the result is huge and ulp
        // comparison stops being meaningful.
        var (w, at) = Ulp.Sweep(SimMath.Tan, MathF.Tan, -1.5f, 1.5f, 200_000);
        Report("Tan", w, at);
    }

    [Fact]
    public void Atan_MatchesLibm()
    {
        var (w, at) = Ulp.Sweep(SimMath.Atan, MathF.Atan, -50f, 50f, 200_000);
        Report("Atan", w, at);
    }

    [Fact]
    public void Exp_MatchesLibm()
    {
        var (w, at) = Ulp.Sweep(SimMath.Exp, MathF.Exp, -80f, 80f, 200_000);
        Report("Exp", w, at);
    }

    [Fact]
    public void Log_MatchesLibm()
    {
        long worst = 0; float at = 0;
        for (int i = 1; i <= 200_000; i++)
        {
            float x = i * 1e-3f;
            long d = Ulp.Distance(SimMath.Log(x), MathF.Log(x));
            if (d > worst) { worst = d; at = x; }
        }
        Report("Log", worst, at);
    }

    [Fact]
    public void Log_MatchesLibm_AcrossExponents()
    {
        long worst = 0; float at = 0;
        for (int e = -120; e <= 120; e++)
        {
            for (int m = 1; m <= 512; m++)
            {
                float x = MathF.ScaleB(1.0f + m / 512f, e);
                if (!float.IsFinite(x) || x == 0) continue;
                long d = Ulp.Distance(SimMath.Log(x), MathF.Log(x));
                if (d > worst) { worst = d; at = x; }
            }
        }
        Report("Log(exp)", worst, at);
    }

    [Fact]
    public void Atan2_MatchesLibm_AllQuadrants()
    {
        long worst = 0; float atX = 0, atY = 0;
        const int n = 600;
        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                float x = -10f + 20f * (i / (float)n);
                float y = -10f + 20f * (j / (float)n);
                if (x == 0 && y == 0) continue;
                long d = Ulp.Distance(SimMath.Atan2(y, x), MathF.Atan2(y, x));
                if (d > worst) { worst = d; atX = x; atY = y; }
            }
        }
        _out.WriteLine($"Atan2        worst = {worst} ulp  at (y={atY:R}, x={atX:R})");
        Assert.True(worst <= Tol, $"Atan2: {worst} ulp at y={atY:R} x={atX:R}");
    }

    [Fact]
    public void Pow_MatchesLibm_OverTypicalDomain()
    {
        long worst = 0; float atX = 0, atY = 0;
        const int n = 500;
        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                float x = 0.001f + 20f * (i / (float)n);
                float y = -8f + 16f * (j / (float)n);
                float a = SimMath.Pow(x, y), b = MathF.Pow(x, y);
                if (!float.IsFinite(a) || !float.IsFinite(b)) continue;
                long d = Ulp.Distance(a, b);
                if (d > worst) { worst = d; atX = x; atY = y; }
            }
        }
        _out.WriteLine($"Pow          worst = {worst} ulp  at ({atX:R}, {atY:R})");
        // Pow composes exp and log, so its error budget is naturally larger.
        Assert.True(worst <= 4, $"Pow: {worst} ulp at x={atX:R} y={atY:R}");
    }

    [Fact]
    public void Pow_MatchesLibm_OnTheFractureKernelDomain()
    {
        // FractureKernel.StepFront computes Pow(max(0, align), AlignExponent)
        // with align in [0,1] and the exponent around 1.6. This is the hot path,
        // so it gets its own tighter check.
        long worst = 0; float at = 0;
        for (int i = 0; i <= 100_000; i++)
        {
            float a = i / 100_000f;
            long d = Ulp.Distance(SimMath.Pow(a, 1.6f), MathF.Pow(a, 1.6f));
            if (d > worst) { worst = d; at = a; }
        }
        Report("Pow(x,1.6)", worst, at);
    }
}
