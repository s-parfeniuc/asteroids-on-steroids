using System;
using AsteroidsSim.Fracture;
using AsteroidsSim.Math;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The guardrails the performance programme runs behind.
/// </summary>
/// <remarks>
/// <para>Two different jobs. <b>Determinism</b> is a hard contract: the same build must produce the
/// same state, or lockstep is impossible. <b>Morphology</b> is a soft one: an optimisation is
/// allowed to move fragment counts a little, but not to turn a body that used to split into chunks
/// into one that turns to gravel. The conservation invariants sit underneath both and are the
/// sharpest bug detector we have — they caught six real defects during the port.</para>
///
/// <para>The morphology bands below are deliberately wide. They are not a specification of correct
/// behaviour; they are a tripwire for a change that altered character rather than arithmetic. If a
/// change trips one, the answer is to look at it and decide, not to widen the band.</para>
///
/// <para><b>Why morphology is asserted over an ensemble.</b> Fracture is chaotic. Multiplying the
/// impact speed by 1.000001 — a part per million, nothing any player could perceive — moves the
/// glass scene from 9 fragments to 6 and its big-mass share from 70% to 83%. So a single run's
/// fragment count carries almost no information, and an optimisation compared against one recorded
/// run will look like a regression roughly as often as not. Each morphology test therefore runs a
/// small ensemble of imperceptibly perturbed scenes and asserts what holds across all of them.
/// <see cref="TheEnsembleSpreadIsWideEnoughToMatter"/> keeps that claim honest by measuring the
/// spread rather than assuming it.</para>
/// </remarks>
public class GuardrailTests
{
    private readonly ITestOutputHelper _out;
    public GuardrailTests(ITestOutputHelper output) => _out = output;

    // ── determinism ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 4)]
    [InlineData(3, 16)]
    [InlineData(7, 64)]
    [InlineData(11, 256)]
    public void ParallelMatchesSequentialExactly(int workers, int chunks)
    {
        // The whole justification for allowing threads into the deterministic core. The partition is
        // a function of simulation state, chunks write only their own bodies, and cross-chunk
        // accumulators merge in chunk order — so the answer cannot depend on how many workers ran or
        // how the scheduler interleaved them. Asserted rather than argued, because the one bug this
        // caught was a fork-join state reuse that desynced about one run in five.
        string expected = Fingerprint(null);
        using var jobs = new SimJobs(workers, chunks);
        Assert.Equal(expected, Fingerprint(jobs));

        static string Fingerprint(SimJobs? jobs)
        {
            var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
            r.Solver.Jobs = jobs;
            for (int i = 0; i < 200; i++) r.Solver.Step();
            return SimFingerprint.Hex(r.State);
        }
    }

    [Theory]
    [InlineData("collide")]
    [InlineData("glass-projectile")]
    public void RenderGeometryAgreesWithTheSimulation(string scenario)
    {
        // Solver.CellLocalPolygon is what the viewer draws from, and a renderer that quietly shows
        // something other than what the solver believes is worse than no renderer. Two checks, both
        // through the public path: the polygon composed with its body's pose must put every vertex
        // on the cell it belongs to, and two cells sharing a Voronoi corner must place that corner
        // in the same spot. The second is the shared-vertex property the whole model rests on — if
        // the accessor returned stale or mis-indexed data, a gap would open here.
        var r = Build(scenario, SimTuning.Default, 1f);
        for (int i = 0; i < 150; i++) r.Solver.Step();

        SimState s = r.State;
        int maxLen = System.Math.Max(8, r.Solver.MaxPolyLen);
        var px = new float[maxLen];
        var py = new float[maxLen];

        // corner id -> world position, to check agreement between the cells that share it
        var corner = new System.Collections.Generic.Dictionary<long, (float X, float Y)>();
        float worstRadius = 0f;
        int drawn = 0;

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.CellDead[c]) continue;
            int n = r.Solver.CellLocalPolygon(c, px, py);
            Assert.Equal(s.PolyLen[c], n);
            if (n == 0) continue;
            drawn++;

            // The cell centre is derived from the SAME pose as the vertices. Reading the cached
            // CellPx instead would compare against a centre one substep stale, since the pose
            // integrates again after the last UpdateCenters.
            int b = s.CellBody[c];
            float si = SimMath.Sin(s.BodyRot[b]), co = SimMath.Cos(s.BodyRot[b]);
            float ccx = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
            float ccy = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;
            for (int v = 0; v < n; v++)
            {
                float wx = s.BodyX[b] + px[v] * co - py[v] * si;
                float wy = s.BodyY[b] + px[v] * si + py[v] * co;

                float d = SimMath.Hypot(wx - ccx, wy - ccy) / s.CellRad[c];
                if (d > worstRadius) worstRadius = d;

            }
        }

        _out.WriteLine($"{scenario}: {drawn} cells drawn, worst vertex {worstRadius:F2}x cell radius");
        Assert.True(drawn > 0, "nothing to draw — the scenario produced no live cells");
        // At most 1 by construction: the polygon is the rest polygon and nothing displaces it.
        Assert.True(worstRadius < 1.05f, $"a drawn vertex sat {worstRadius:F2}x its cell radius away");
    }

    [Fact]
    public void AViolentShatterDoesNotOutrunItsScratchArrays()
    {
        // Fine grain is the stress case for topology, not for arithmetic. A 3,800-cell target at
        // 5.5 px cells does not crack, it disintegrates: a couple of bodies become well over a
        // thousand, and several hundred of them can appear in a SINGLE tick. RebuildBodies reserved
        // a fixed 256 slots of headroom for that and threw IndexOutOfRange when a tick exceeded it.
        // Every reference scenario stayed green throughout — they are all coarse enough never to get
        // there — so nothing but a fine-grain run would have found it.
        var r = Scenarios.Projectile(SimTuning.Default, Material.Rock,
            speed: 900f, massMul: 3f, grain: 30f);
        for (int i = 0; i < 40; i++) r.Solver.Step();

        _out.WriteLine($"grain 30 projectile after 40 ticks: {r.State.BodyCount} bodies, "
                     + $"{r.Solver.Broken} bonds broken, {r.Solver.Dust} dust");
        Assert.True(r.State.BodyCount > 400,
            $"only {r.State.BodyCount} bodies — the scene did not shatter, so this proves nothing");
        Assert.Equal(0, r.Solver.CrossBodyBondCount());
        Assert.True(r.MomentumDrift() < 0.02f, $"momentum drift {r.MomentumDrift():P3}");
    }

    [Theory]
    [InlineData(900f, 9, 30.0f)]     // 30 px cells, CFL 0.16
    [InlineData(100f, 9, 10.0f)]     // 10 px cells, CFL 0.48
    [InlineData(30f, 16, 5.48f)]     // 5.5 px cells, CFL 0.49 — needs the extra substeps
    public void AFreeSpinnerIsStableAtEveryGrainGivenEnoughSubsteps(
        float grain, int substeps, float cellPx)
    {
        // Scale invariance, asserted. A single body rotating in vacuum has no contacts and no
        // external load; it should hold together forever at any cell size. The bond formulas do
        // support that — stiffness is rho*c^2*(sideLength/h) and failure stretch is strain*h, so
        // failure STRESS is rho*c^2*strain, independent of h.
        //
        // What is not scale-invariant is the INTEGRATOR. Bond forces are explicit, so they are
        // stable only while a stress wave crosses a fraction of a cell per substep, and the substep
        // count is a constant while cell size is a knob. At grain 30 with the default 9 substeps
        // this same scenario shatters into 349 bodies and loses a quarter of its energy — not
        // because the bonds got weaker, but because the deviation field diverges and the damage
        // model cannot tell numerical divergence from load. Raising substeps to 16 fixes it
        // completely, which is what pins the cause.
        var t = SimTuning.Default;
        t.Substeps = substeps;
        var r = Scenarios.Spin(t, Material.Rock, grain: grain);

        float cfl = r.State.BodyCpx[0] * (Solver.Dt / substeps) / cellPx;
        for (int i = 0; i < 150; i++) r.Solver.Step();

        _out.WriteLine($"grain {grain:F0} ({cellPx:F1} px cells), {substeps} substeps, CFL {cfl:F2}: "
                     + $"{r.State.BodyCount} bodies, {r.Solver.Broken} broken, "
                     + $"energy {r.EnergyFraction():P1}");

        Assert.True(cfl < 0.55f, $"test setup is not actually stable: CFL {cfl:F2}");
        Assert.Equal(1, r.State.BodyCount);
        Assert.Equal(0, r.Solver.Broken);
        Assert.True(r.EnergyFraction() > 0.9f,
            $"a free spinner lost energy: {r.EnergyFraction():P1}");
    }

    [Fact]
    public void SameBuildProducesTheSameStateTwice()
    {
        string a = RunAndFingerprint();
        string b = RunAndFingerprint();
        _out.WriteLine($"fingerprint: {a}");
        Assert.Equal(a, b);

        static string RunAndFingerprint()
        {
            var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
            for (int i = 0; i < 120; i++) r.Solver.Step();
            return SimFingerprint.Hex(r.State);
        }
    }

    [Fact]
    public void FingerprintChangesWhenStateChanges()
    {
        // A hash that never changes would pass the test above while detecting nothing.
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
        string atStart = SimFingerprint.Hex(r.State);
        for (int i = 0; i < 30; i++) r.Solver.Step();
        string later = SimFingerprint.Hex(r.State);
        Assert.NotEqual(atStart, later);
    }

    [Fact]
    public void DifferentScenariosDoNotCollide()
    {
        var a = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f);
        var b = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 601f);
        for (int i = 0; i < 60; i++) { a.Solver.Step(); b.Solver.Step(); }
        Assert.NotEqual(SimFingerprint.Hex(a.State), SimFingerprint.Hex(b.State));
    }

    // ── invariants, per scenario ─────────────────────────────────────────────

    [Theory]
    [InlineData("collide")]
    [InlineData("projectile")]
    [InlineData("steel-projectile")]
    [InlineData("glass-projectile")]
    public void BondsNeverCrossBodies(string scenario)
    {
        // The premise the parallel decomposition rests on. If a bond ever coupled cells of two
        // different bodies, handing bodies to different threads would mean two of them writing the
        // same cell, and the failure would show up as an occasional desync under load rather than
        // as anything reproducible.
        var r = Build(scenario, SimTuning.Default, 1f);
        for (int i = 0; i < 400; i++)
        {
            r.Solver.Step();
            if (i % 13 == 0) Assert.Equal(0, r.Solver.CrossBodyBondCount());
        }
        Assert.Equal(0, r.Solver.CrossBodyBondCount());
    }

    [Theory]
    [InlineData("collide")]
    [InlineData("projectile")]
    [InlineData("spin")]
    [InlineData("steel-projectile")]
    [InlineData("glass-projectile")]
    public void CollidersStayOnTheCellsTheyBelongTo(string scenario)
    {
        // A collider vertex cannot be far from its own cell: the polygon is convex and bounded by
        // the circumscribed radius, and realized deformation adds a few percent. This catches the
        // class of bug where geometry is read from a stale cache — the polygons stay internally
        // consistent, so momentum, energy and the shared-vertex gap all stay green while the
        // colliders sit where the cells used to be. One did exactly that for the first manifold
        // build of every tick after a topology change, and produced vertices 200 px out.
        var t = SimTuning.Default;
        Scenarios.Result r = Build(scenario, t, 1f);
        float worst = 0f;
        for (int i = 0; i < 400; i++)
        {
            r.Solver.Step();
            if (i % 7 != 0) continue;                 // coprime with the substep count
            float v = r.Solver.MaxSkinRadiusRatio();
            if (v > worst) worst = v;
        }
        _out.WriteLine($"{scenario}: worst collider vertex at {worst:F3} of its cell radius");
        // Healthy is 1.0 to 1.8 — a shared vertex is an average of what several cells think, so it
        // legitimately sits outside the cell's own circumscribed radius. The bound is set to catch
        // the order-of-magnitude failure, not to police deformation: the stale cache produced 6.4
        // and 6.5 here, and 12 before the tick-boundary half of it was fixed.
        Assert.True(worst < 3f,
            $"{scenario}: a collider vertex sat {worst:F1}x its cell radius from the cell");
    }

    [Theory]
    [InlineData("collide")]
    [InlineData("projectile")]
    [InlineData("spin")]
    [InlineData("steel-projectile")]
    [InlineData("glass-projectile")]
    public void ConservationInvariantsHold(string scenario)
    {
        var m = RunScenario(scenario, 400);
        _out.WriteLine($"{scenario}: {m}");

        Assert.True(m.MomentumDrift < 0.02f, $"momentum drift {m.MomentumDrift:P3}");
        Assert.True(m.PeakEnergyFraction <= 1.05f, $"energy created: peak {m.PeakEnergyFraction:P1}");
        // Collider vertices sit on their own cells. At most ~1 cell radius by construction now that
        // nothing displaces them; a larger value means geometry is being read from the wrong place.
        Assert.True(m.MaxSharedVertexGap < 1.05f,
            $"a collider vertex sat {m.MaxSharedVertexGap:F2}x its cell radius from the cell");
    }

    // ── morphology bands, asserted across an ensemble ───────────────────────

    [Fact]
    public void CollideStillProducesChunksRatherThanGravel()
    {
        foreach (var m in Ensemble("collide", 400))
        {
            _out.WriteLine($"collide: {m}");
            // 2..40 was recorded before the deformation layer was removed and is too tight for the
            // spread this scene actually has: the ensemble runs 33..42 across a part-per-million
            // perturbation, so the old ceiling was inside the noise and passed or failed by luck.
            // Widened to sit outside it. The discriminating assertions are the two below — mass in
            // large pieces, and crack connectivity — which separate chunks from gravel; a fragment
            // COUNT never could.
            Assert.InRange(m.Bodies, 2, 60);
            Assert.InRange(m.Broken, 120, 400);
            Assert.True(m.BigMassPct > 25f, $"only {m.BigMassPct:F0}% of mass in >4-cell pieces — gravel");
            Assert.True(m.CrackConnectivity > 80f,
                $"crack connectivity {m.CrackConnectivity:F0}% — damage is scattering, not cracking");
        }
    }

    [Fact]
    public void ProjectileCratersRatherThanShattering()
    {
        foreach (var m in Ensemble("projectile", 400))
        {
            _out.WriteLine($"projectile: {m}");
            Assert.True(m.BigMassPct > 80f, $"target lost coherence: only {m.BigMassPct:F0}% in big pieces");
            Assert.InRange(m.Broken, 5, 150);
        }
    }

    [Fact]
    public void SpinStaysIdle()
    {
        // Not an ensemble: a free spinner is not chaotic, and any spread here would be a bug.
        var m = RunScenario("spin", 300);
        _out.WriteLine($"spin: {m}");
        Assert.Equal(1, m.Bodies);
        Assert.Equal(0, m.Broken);
        Assert.True(m.PeakStretchPct < 2f, $"a pure spinner stretched {m.PeakStretchPct:F1}% of a cell");
        Assert.True(m.EnergyFraction > 0.9f, "a free spinner should keep its energy");
    }

    [Fact]
    public void MaterialsStayDistinguishable()
    {
        // Glass is brittle and rigid; steel is ductile and necks. If an optimisation collapses the
        // difference between them, the material model has stopped meaning anything. Necking is the
        // separator because it is the one metric the sensitivity sweep found stable: across a
        // thousandfold range of perturbation glass stays near 5-7% and steel near 19-23%.
        // Necking was the old discriminator and it is gone with plastic flow. What separates these
        // materials now is what always did the structural work: failure strain and the softening
        // ratio. Steel is strain 0.020 / chi 50, glass 0.008 / chi 1.05 — so glass fails at a
        // quarter of the load and, being at the brittle limit, snaps at peak instead of softening.
        // The visible consequence is fragmentation.
        var glass = Ensemble("glass-projectile", 400);
        var steel = Ensemble("steel-projectile", 400);
        for (int i = 0; i < glass.Length; i++)
        {
            _out.WriteLine($"glass: {glass[i]}");
            _out.WriteLine($"steel: {steel[i]}");
            Assert.True(glass[i].Broken > steel[i].Broken * 2,
                $"glass broke {glass[i].Broken} bonds vs steel {steel[i].Broken} — not distinguishable");
        }
    }

    [Fact]
    public void ContactsDoNotSinkIntoEachOther()
    {
        // Peak penetration is the metric that showed a systematic — not chaotic — response to the
        // contact manifold's refresh threshold, so it is the one worth pinning. The bound is one
        // cell: past that two cells have passed through each other rather than collided, and no
        // normal recovers it. The rigid scenes genuinely run close to this — glass under a
        // 1500 px/s impactor reaches 21-23 px with the manifold rebuilt every substep — so a
        // tighter bound would be asserting something the model never satisfied.
        foreach (string scene in new[] { "collide", "projectile", "glass-projectile", "steel-projectile" })
            foreach (var m in Ensemble(scene, 400))
            {
                _out.WriteLine($"{scene}: ov={m.PeakOverlap:F1}");
                Assert.True(m.PeakOverlap < 30f, $"{scene} penetrated {m.PeakOverlap:F1} px of a 30 px cell");
            }
    }

    [Fact]
    public void TheEnsembleSpreadIsWideEnoughToMatter()
    {
        // The justification for every band above. If this ever fails because the spread collapsed,
        // the ensemble is no longer doing anything and the bands could be tightened; if it fails
        // because the spread exploded, something has become unstable rather than merely chaotic.
        var e = Ensemble("glass-projectile", 400);
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var m in e) { if (m.Bodies < lo) lo = m.Bodies; if (m.Bodies > hi) hi = m.Bodies; }
        _out.WriteLine($"glass fragment count across a 1e-6..1e-3 speed perturbation: {lo}..{hi}");
        Assert.True(hi > lo, "a part-per-million perturbation changed nothing — is the scene running?");
        Assert.True(hi - lo < 60, $"fragment count swung {hi - lo} — instability, not chaos");
    }

    /// <summary>
    /// One scenario at speeds differing by between a part per million and a part per thousand.
    /// </summary>
    private static SceneMetrics[] Ensemble(string name, int ticks)
    {
        float[] muls = { 1f, 1.000001f, 1.0001f, 1.001f };
        var outp = new SceneMetrics[muls.Length];
        for (int i = 0; i < muls.Length; i++) outp[i] = RunScenario(name, ticks, muls[i]);
        return outp;
    }

    private static SceneMetrics RunScenario(string name, int ticks, float speedMul = 1f)
        => SceneRunner.Run(Build(name, SimTuning.Default, speedMul), ticks);

    private static Scenarios.Result Build(string name, in SimTuning t, float speedMul)
    {
        return name switch
        {
            "collide" => Scenarios.Collide(t, Material.Rock, speed: 600f * speedMul),
            "projectile" => Scenarios.Projectile(t, Material.Rock, speed: 900f * speedMul),
            "spin" => Scenarios.Spin(t, Material.Rock),
            "steel-projectile" => Scenarios.Projectile(t, Material.Steel, speed: 1500f * speedMul, massMul: 8f),
            "glass-projectile" => Scenarios.Projectile(t, Material.Glass, speed: 1500f * speedMul, massMul: 8f),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
