using System;
using System.Collections.Generic;
using AsteroidsSim.Fracture;
using AsteroidsSim.Math;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// Correctness invariants of the simulation: conservation, topology, geometry and finiteness.
/// </summary>
/// <remarks>
/// Nothing here asserts how a scene looks — fragment counts, crater shapes and material character
/// are judged in the viewer. These are the properties that must hold for ANY tuning.
/// </remarks>
public class InvariantTests
{
    private readonly ITestOutputHelper _out;
    public InvariantTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Scenes()
    {
        foreach (string n in Scenarios.ReferenceNames) yield return new object[] { n };
    }

    // ── whole-scene invariants ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Scenes))]
    public void InvariantsHoldThroughTheScene(string scene)
    {
        var r = Scenarios.Reference(scene, TestConfig.Value);
        float peakKe = 0f, worstVertex = 0f;
        for (int i = 0; i < 400; i++)
        {
            r.Solver.Step();
            float ke = r.EnergyFraction();
            if (ke > peakKe) peakKe = ke;

            if (i % 7 != 0) continue;                    // coprime with the substep count
            // A bond coupling two bodies would let two parallel chunks write the same cell.
            Assert.True(r.Solver.CrossBodyBondCount() == 0, $"{scene}: a bond crosses bodies at tick {i + 1}");
            // A vertex off its own cell means geometry is being read from a stale pose or cache.
            worstVertex = SimMath.Max(worstVertex, r.Solver.MaxVertexRadiusRatio());
        }

        float drift = r.MomentumDrift();
        _out.WriteLine($"{scene}: cells {r.State.CellCount}, bodies {r.State.BodyCount}, "
                     + $"momentum drift {drift:P4}, peak energy {peakKe:P1}, worst vertex {worstVertex:F3} radii");

        Assert.Equal(0, r.Solver.CrossBodyBondCount());
        Assert.True(drift < 0.02f, $"{scene}: momentum drift {drift:P3}");
        Assert.True(peakKe <= 1.05f, $"{scene}: energy created, peak {peakKe:P1}");
        Assert.True(worstVertex < 1.05f, $"{scene}: a collider vertex sat {worstVertex:F2}x its cell radius from the cell");
        AssertAllFinite(r.State, scene);
    }

    [Theory]
    [InlineData("rock")]
    [InlineData("glass")]
    [InlineData("steel")]
    [InlineData("ice")]
    [InlineData("sandstone")]
    public void ASubcriticalSpinnerStaysIdle(string material)
    {
        // A body spinning in vacuum below its breakup rate carries only its own centrifugal load:
        // it must not fracture, must not gain energy, and its deviation field must settle.
        var t = TestConfig.Tuning;
        var m = TestConfig.Material(material);
        float grain = Scenarios.FloorGrain(t, m);
        float omega = 0.4f * BreakupOmega(t, m, Scenarios.Spin(t, m, omega: 0f, grain: grain).State);
        var r = Scenarios.Spin(t, m, omega, grain);

        float peakKe = 0f;
        for (int i = 0; i < 300; i++)
        {
            r.Solver.Step();
            float ke = r.EnergyFraction();
            if (ke > peakKe) peakKe = ke;
        }

        SimState s = r.State;
        float field = 0f;
        for (int c = 0; c < s.CellCount; c++)
            if (!s.Dead(c)) field = SimMath.Max(field, SimMath.Abs(s.CellDvx[c]) + SimMath.Abs(s.CellDvy[c]));

        _out.WriteLine($"{material} at grain {grain:F0}, omega {omega:F2}: bodies {s.BodyCount}, "
                     + $"broken {r.Solver.Broken}, energy {r.EnergyFraction():P1} (peak {peakKe:P1}), "
                     + $"field {field:F3} px/s");

        Assert.Equal(1, s.BodyCount);
        Assert.Equal(0, r.Solver.Broken);
        Assert.True(peakKe <= 1.02f, $"spinner created energy: peak {peakKe:P1}");
        Assert.True(r.EnergyFraction() > 0.9f, $"spinner lost energy: {r.EnergyFraction():P1}");
        Assert.True(field < 0.5f, $"the deviation field did not settle: {field:F3} px/s");
        AssertAllFinite(s, material);
    }

    /// <summary>
    /// Spin rate at which centrifugal stress alone reaches the material's failure strain, for a disc
    /// of the body's outer radius: <c>eps = (3 + nu)/8 * (w R / c)^2</c> with <c>nu = 1/4</c>.
    /// </summary>
    private static float BreakupOmega(in SimTuning t, in Material m, SimState s)
    {
        var mp = new MaterialProps(m, t);
        float radius = 0f;
        for (int c = 0; c < s.CellCount; c++)
            radius = SimMath.Max(radius, SimMath.Hypot(s.CellRx[c], s.CellRy[c]) + s.CellRad[c]);
        return mp.Cpx / radius * MathF.Sqrt(mp.Eps / 0.40625f);
    }

    [Fact]
    public void AViolentShatterDoesNotOutrunItsScratchArrays()
    {
        // Hundreds of fragments appearing in ONE tick is the stress case for the topology rebuild's
        // per-body scratch. Strength is scaled down so the spin load disintegrates the body at once.
        var t = TestConfig.Tuning;
        t.StrainScale = 0.01f;
        var rock = TestConfig.Material("rock");
        var r = Scenarios.Spin(t, rock, omega: 3f, grain: Scenarios.FloorGrain(t, rock));

        int largestJump = 0, prev = r.State.BodyCount;
        for (int i = 0; i < 40; i++)
        {
            r.Solver.Step();
            largestJump = System.Math.Max(largestJump, r.State.BodyCount - prev);
            prev = r.State.BodyCount;
        }

        _out.WriteLine($"{r.State.BodyCount} bodies, largest single-tick increase {largestJump}");
        Assert.True(largestJump > 256, $"only {largestJump} bodies in one tick — the stress case was not reached");
        Assert.Equal(0, r.Solver.CrossBodyBondCount());
        Assert.True(r.MomentumDrift() < 0.02f, $"momentum drift {r.MomentumDrift():P3}");
        AssertAllFinite(r.State, "shatter");
    }

    [Theory]
    [InlineData("collide")]
    [InlineData("glass-projectile")]
    public void RenderGeometryAgreesWithTheSimulation(string scene)
    {
        // CellLocalPolygon is what the viewer draws from. Composed with its body's pose, every
        // vertex it returns must sit on the cell it belongs to.
        var r = Scenarios.Reference(scene, TestConfig.Value);
        for (int i = 0; i < 150; i++) r.Solver.Step();

        SimState s = r.State;
        int maxLen = System.Math.Max(8, r.Solver.MaxPolyLen);
        var px = new float[maxLen];
        var py = new float[maxLen];
        float worst = 0f;
        int drawn = 0;

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int n = r.Solver.CellLocalPolygon(c, px, py);
            Assert.Equal(s.PolyLen[c], n);
            if (n == 0) continue;
            drawn++;

            // Centre from the same pose as the vertices, not the cached CellPx.
            int b = s.CellBody[c];
            float si = SimMath.Sin(s.BodyRot[b]), co = SimMath.Cos(s.BodyRot[b]);
            float ccx = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
            float ccy = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;
            for (int v = 0; v < n; v++)
            {
                float wx = s.BodyX[b] + px[v] * co - py[v] * si;
                float wy = s.BodyY[b] + px[v] * si + py[v] * co;
                worst = SimMath.Max(worst, SimMath.Hypot(wx - ccx, wy - ccy) / s.CellRad[c]);
            }
        }

        _out.WriteLine($"{scene}: {drawn} cells drawn, worst vertex {worst:F2}x cell radius");
        Assert.True(drawn > 0, "the scene produced no live cells");
        Assert.True(worst < 1.05f, $"a drawn vertex sat {worst:F2}x its cell radius away");
    }

    // ── build invariants ─────────────────────────────────────────────────────

    [Fact]
    public void EveryCellHasPositiveMassAndFiniteInertia()
    {
        SimState s = Scenarios.Reference("collide", TestConfig.Value).State;
        for (int c = 0; c < s.CellCount; c++)
        {
            Assert.True(s.CellM[c] > 0f, $"cell {c} mass");
            Assert.True(float.IsFinite(s.CellIc[c]) && s.CellIc[c] > 0f, $"cell {c} inertia");
            Assert.True(float.IsFinite(s.CellIm[c]), $"cell {c} inverse mass");
        }
    }

    [Fact]
    public void AdjacencyIsSymmetricAndComplete()
    {
        SimState s = Scenarios.Reference("collide", TestConfig.Value).State;
        int total = 0;
        for (int c = 0; c < s.CellCount; c++) total += s.AdjLen[c];
        Assert.Equal(2 * s.BondCount, total);

        for (int k = 0; k < s.BondCount; k++)
        {
            Assert.Contains(k, Slice(s, s.BondA[k]));
            Assert.Contains(k, Slice(s, s.BondB[k]));
        }

        static IEnumerable<int> Slice(SimState s, int cell)
        {
            for (int i = 0; i < s.AdjLen[cell]; i++) yield return s.AdjBond[s.AdjOff[cell] + i];
        }
    }

    private static void AssertAllFinite(SimState s, string where)
    {
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            Assert.True(float.IsFinite(s.CellDvx[c]) && float.IsFinite(s.CellDvy[c]) && float.IsFinite(s.CellDw[c]),
                $"{where}: cell {c} is not finite");
        }
        for (int b = 0; b < s.BodyCount; b++)
            Assert.True(float.IsFinite(s.BodyX[b]) && float.IsFinite(s.BodyVx[b])
                        && float.IsFinite(s.BodyW[b]) && float.IsFinite(s.BodyI[b]),
                $"{where}: body {b} is not finite");
    }
}
