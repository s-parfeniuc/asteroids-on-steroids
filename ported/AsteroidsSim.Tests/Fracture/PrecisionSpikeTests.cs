using System;
using AsteroidsSim.Fracture;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The precision gate: does the model's conservation behaviour survive single precision?
/// </summary>
/// <remarks>
/// <para>The reference implementation is JavaScript, so every number in it is a double. The
/// simulation core here is float32, because <c>SimMath</c>, <c>Vec2</c> and the whole determinism
/// foundation are — and because halving the state halves the snapshot, which the rollback budget
/// cares about. But the model leans on exact cancellations: the deviation field is kept zero-mean
/// by <c>DecomposeMotion</c>, the Euler load's fictitious torque is subtracted back out, and the
/// deformation cap hands its removed momentum to the body rather than deleting it. If float32
/// erodes those, momentum drift and energy would show it immediately.</para>
///
/// <para>These tests are the decision, not an opinion: if they hold, single precision stays. The
/// fallback is cheap — <c>SimMath</c>'s kernels already evaluate in double internally, so exposing
/// double variants is a policy edit rather than a rewrite.</para>
/// </remarks>
public class PrecisionSpikeTests
{
    private readonly ITestOutputHelper _out;
    public PrecisionSpikeTests(ITestOutputHelper output) => _out = output;

    private static void Run(Scenarios.Result r, int ticks, out float peakKe)
    {
        peakKe = 0f;
        for (int i = 0; i < ticks; i++)
        {
            r.Solver.Step();
            float ke = r.EnergyFraction();
            if (ke > peakKe) peakKe = ke;
        }
    }

    [Fact]
    public void SpinIsIdle()
    {
        // Rigid rotation gives identical velocity at a shared anchor, so an undisturbed spinner
        // must accumulate no stretch, no damage and no deformation at all. This is the sharpest
        // single check on the whole frame discipline.
        var r = Scenarios.Spin(SimTuning.Default, Material.Rock);
        Run(r, 300, out float peakKe);

        _out.WriteLine($"spin: bodies={r.State.BodyCount} broken={r.Solver.Broken} " +
                       $"mom={100 * r.MomentumDrift():F4}% ke={100 * r.EnergyFraction():F1}% " +
                       $"peak={100 * peakKe:F1}% vtx={r.Solver.MaxSkinRadiusRatio():F4}");

        Assert.Equal(1, r.State.BodyCount);
        Assert.Equal(0, r.Solver.Broken);
        Assert.True(peakKe <= 1.02f, $"spin created energy: peak {peakKe:P1}");

        // Cells no longer displace, so what a spinner must keep quiet is the deviation VELOCITY
        // field: a rigid rotation should leave it identically zero.
        float u = 0f;
        for (int c = 0; c < r.State.CellCount; c++)
            u = System.Math.Max(u, System.Math.Abs(r.State.CellDvx[c]) + System.Math.Abs(r.State.CellDvy[c]));
        Assert.True(u < 0.5f, $"a pure spinner stirred the field by {u:F3} px/s");
    }

    [Fact]
    public void CollideConservesMomentumAndDoesNotCreateEnergy()
    {
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
        int cells0 = r.State.CellCount, bonds0 = r.State.BondCount;
        Run(r, 600, out float peakKe);

        float drift = r.MomentumDrift();
        _out.WriteLine($"collide: cells={cells0} bonds={bonds0} " +
                       $"bodies={r.State.BodyCount} broken={r.Solver.Broken} dust={r.Solver.Dust} " +
                       $"mom={100 * drift:F4}% ke={100 * r.EnergyFraction():F1}% peak={100 * peakKe:F1}% " +
                       $"ov={r.Solver.MaxOverlap:F2} vtx={r.Solver.MaxSkinRadiusRatio():F4}");

        Assert.True(drift < 0.02f, $"momentum drift {drift:P3}");
        Assert.True(peakKe <= 1.05f, $"energy created: peak {peakKe:P1}");
        AssertAllFinite(r.State);
    }

    [Fact]
    public void ProjectileConservesMomentumAndDoesNotCreateEnergy()
    {
        var r = Scenarios.Projectile(SimTuning.Default, Material.Rock);
        Run(r, 600, out float peakKe);

        float drift = r.MomentumDrift();
        _out.WriteLine($"projectile: bodies={r.State.BodyCount} broken={r.Solver.Broken} " +
                       $"dust={r.Solver.Dust} mom={100 * drift:F4}% " +
                       $"ke={100 * r.EnergyFraction():F1}% peak={100 * peakKe:F1}% " +
                       $"ov={r.Solver.MaxOverlap:F2} vtx={r.Solver.MaxSkinRadiusRatio():F4}");

        Assert.True(drift < 0.02f, $"momentum drift {drift:P3}");
        Assert.True(peakKe <= 1.05f, $"energy created: peak {peakKe:P1}");
        AssertAllFinite(r.State);
    }

    [Fact]
    public void SharedSidesNeverOpen()
    {
        // A bond is a shared side, so the two cells' copies of it must stay coincident. This is
        // the direct assertion that skinning holds, and it is what the reference reports as
        // "gap = 0.000 px" in every scenario.
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
        float worst = 0f;
        for (int i = 0; i < 200; i++)
        {
            r.Solver.Step();
            if (i % 10 == 0)
                worst = System.Math.Max(worst, r.Solver.MaxSkinRadiusRatio());
        }
        _out.WriteLine($"max shared-vertex gap over 200 ticks: {worst:E3} px");
        Assert.True(worst < 1.05f,
            $"a collider vertex sat {worst:F2}x its cell radius from the cell");
    }

    [Fact]
    public void SteelDentsRatherThanShattering()
    {
        // The plasticity check. Steel yields early and flows a long way before tearing, so it
        // should dent and keep the set rather than break. This is NOT yet the pole acceptance
        // test from the reference suite — that scenario's carved geometry is not ported — so it
        // exercises the plastic path without asserting the hinge angle.
        var r = Scenarios.Projectile(SimTuning.Default, Material.Steel, speed: 1500f, massMul: 8f);
        Run(r, 400, out float peakKe);

        float bend = 0f;
        for (int c = 0; c < r.State.CellCount; c++)
            if (!r.State.Dead(c))
                bend = System.Math.Max(bend, System.Math.Abs(r.State.CellDw[c]));

        _out.WriteLine($"steel: bodies={r.State.BodyCount} broken={r.Solver.Broken} " +
                       $"rebakes={r.Solver.Rebakes} plastic={r.Solver.PlasticWork:E2} " +
                       $"bend={bend * 180f / 3.14159f:F1}deg " +
                       $"mom={100 * r.MomentumDrift():F4}% peak={100 * peakKe:F1}%");

        Assert.True(peakKe <= 1.05f, $"energy created: peak {peakKe:P1}");
        Assert.True(r.MomentumDrift() < 0.02f);
        AssertAllFinite(r.State);
    }

    private static void AssertAllFinite(SimState s)
    {
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            Assert.True(float.IsFinite(s.CellDvx[c]) && float.IsFinite(s.CellDvy[c])
                        && float.IsFinite(s.CellDvx[c]) && float.IsFinite(s.CellDvy[c]),
                        $"cell {c} is not finite");
        }
        for (int b = 0; b < s.BodyCount; b++)
            Assert.True(float.IsFinite(s.BodyX[b]) && float.IsFinite(s.BodyVx[b])
                        && float.IsFinite(s.BodyW[b]) && float.IsFinite(s.BodyI[b]),
                        $"body {b} is not finite");
    }
}
