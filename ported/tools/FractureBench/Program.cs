using System.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using AsteroidsSim.Fracture;
using AsteroidsSim.Math;

namespace FractureBench;

/// <summary>
/// Scaling harness for the destruction solver: how many live cells hold 60 Hz, and which phase
/// runs out of budget first.
/// </summary>
/// <remarks>
/// The clock lives here rather than in the simulation. <c>Stopwatch</c> is banned inside
/// <c>AsteroidsSim</c> because a wall clock in the deterministic path desyncs a match; the sim
/// only reports where its phase boundaries are, through <see cref="Solver.PhaseMark"/>.
/// </remarks>
internal static class Program
{
    private const double Budget60Hz = 16.667;
    private const double PhysicsShare = 8.0;   // the budget the port plan reserves for physics

    /// <summary>Set by --drift, to sweep the manifold threshold against the scaling scenes.</summary>
    private static float DriftOverride;

    /// <summary>Set by --jobs: worker threads, or -1 for the sequential reference path.</summary>
    private static int JobsWorkers = -1;
    private static int JobsChunks = SimJobs.DefaultChunks;
    private static int JobsMask = 0x3F;
    private static SimJobs? _jobs;

    /// <summary>Attaches the worker pool, if one was asked for, to a freshly built scene.</summary>
    private static Scenarios.Result WithJobs(Scenarios.Result r)
    {
        if (JobsWorkers >= 0)
        {
            _jobs ??= new SimJobs(JobsWorkers, JobsChunks);
            r.Solver.Jobs = _jobs;
            r.Solver.ParallelMask = JobsMask;
        }
        return r;
    }

    private static void Main(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        bool phases = Array.IndexOf(args, "--phases") >= 0;
        JobsWorkers = ArgInt(args, "--jobs", -1);
        JobsChunks = ArgInt(args, "--chunks", SimJobs.DefaultChunks);
        if (Array.IndexOf(args, "--micro") >= 0) { Micro(); return; }
        if (Array.IndexOf(args, "--detail") >= 0) { Detail(args); return; }
        if (Array.IndexOf(args, "--manifold") >= 0) { ManifoldSweep(args); return; }
        if (Array.IndexOf(args, "--sensitivity") >= 0) { Sensitivity(args); return; }
        if (Array.IndexOf(args, "--fingerprint") >= 0) { Fingerprint(); return; }
        if (Array.IndexOf(args, "--parbase") >= 0) { ParallelBaseline(args); return; }
        if (Array.IndexOf(args, "--biassweep") >= 0) { BiasSweep(args); return; }
        if (Array.IndexOf(args, "--stress") >= 0) { StressStudy(args); return; }
        if (Array.IndexOf(args, "--crush") >= 0) { CrushSweep(args); return; }
        if (Array.IndexOf(args, "--crushdiag") >= 0) { CrushDiag(args); return; }
        if (Array.IndexOf(args, "--polycensus") >= 0) { PolyCensus(); return; }
        if (Array.IndexOf(args, "--sidecensus") >= 0) { SideCensus(); return; }
        if (Array.IndexOf(args, "--audit") >= 0) { SideAuditRun(); return; }
        if (Array.IndexOf(args, "--carvetrace") >= 0) { CarveTrace(); return; }
        if (Array.IndexOf(args, "--tick69") >= 0) { Tick69(args); return; }
        if (Array.IndexOf(args, "--normals") >= 0) { NormalSpread(args); return; }
        if (Array.IndexOf(args, "--cell") >= 0) { TraceOneCell(args); return; }
        if (Array.IndexOf(args, "--pair") >= 0) { TracePair(args); return; }
        if (Array.IndexOf(args, "--census") >= 0) { CarveCensus(args); return; }
        if (Array.IndexOf(args, "--steel") >= 0) { SteelRepro(args); return; }
        if (Array.IndexOf(args, "--loop") >= 0) { LoopRepro(args); return; }
        if (Array.IndexOf(args, "--records") >= 0) { RecordCensus(); return; }
        if (Array.IndexOf(args, "--build0") >= 0) { BuildFaults(args); return; }
        if (Array.IndexOf(args, "--osic") >= 0) { OpenCoveredSample(args); return; }
        if (Array.IndexOf(args, "--dent") >= 0) { DentProfile(args); return; }
        if (Array.IndexOf(args, "--tri") >= 0) { TriRepro(args); return; }
        if (Array.IndexOf(args, "--lendis") >= 0) { LengthDisagreements(args); return; }
        if (Array.IndexOf(args, "--five") >= 0) { FiveRepro(args); return; }
        if (Array.IndexOf(args, "--zls") >= 0) { ZeroLengthSample(args); return; }
        if (Array.IndexOf(args, "--cell36") >= 0) { Cell36(args); return; }
        if (Array.IndexOf(args, "--vkinds") >= 0) { VertexKinds(args); return; }
        if (Array.IndexOf(args, "--bias") >= 0) { BiasSweepV2(args); return; }
        if (Array.IndexOf(args, "--momflow") >= 0) { MomentumFlow(args); return; }
        if (Array.IndexOf(args, "--mats") >= 0) { MaterialCensus(args); return; }
        if (Array.IndexOf(args, "--stretch") >= 0) { StretchProbe(args); return; }
        if (Array.IndexOf(args, "--crushcap") >= 0) { CrushCapSweep(args); return; }
        if (Array.IndexOf(args, "--spall") >= 0) { SpallSweep(args); return; }
        if (Array.IndexOf(args, "--jobsweep") >= 0) { JobSweep(args); return; }
        if (Array.IndexOf(args, "--ticktrace") >= 0) { TickTrace(args); return; }
        int ticks = ArgInt(args, "--ticks", 180);
        DriftOverride = ArgFloat(args, "--drift", 0f);
        JobsWorkers = ArgInt(args, "--jobs", -1);

        Console.WriteLine("FractureBench — destruction solver scaling");
        Console.WriteLine($"  budget {Budget60Hz.ToString("F2", ci)} ms/tick at 60 Hz, " +
                          $"physics share ~{PhysicsShare.ToString("F0", ci)} ms");
        Console.WriteLine($"  ticks per measurement: {ticks}");
        Console.WriteLine();

        // Warm the JIT on a small case so the first real row is not measuring compilation.
        RunCase(Material.Rock, 3, 3, 900f, 40, false, quiet: true);

        Console.WriteLine("── scaling: field of asteroids, grain 900 (~30 px cells) ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 4), (8, 6), (12, 9), (16, 12), (20, 16), (26, 20),
                                             (36, 28), (46, 36), (56, 44) })
            RunCase(Material.Rock, cols, rows, 900f, ticks, phases);

        Console.WriteLine();
        Console.WriteLine("── same cell counts, fewer+finer bodies (grain 225, ~15 px cells) ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 3), (6, 5), (8, 7), (10, 9) })
            RunCase(Material.Rock, cols, rows, 225f, ticks, phases, radius: 60f, spacing: 150f);

        Console.WriteLine();
        Console.WriteLine("── dense: bodies just touching (spacing = diameter), grain 900 ──");
        Header();
        foreach (var (cols, rows) in new[] { (8, 6), (12, 9), (16, 12), (26, 20), (36, 28), (46, 36) })
            RunCase(Material.Rock, cols, rows, 900f, ticks, phases, spacing: 128f);

        Console.WriteLine();
        Console.WriteLine("── few LARGE bodies (r=200, ~140 cells each) — the shape a real field has ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 3), (7, 5), (10, 7), (13, 10), (16, 12) })
            RunCase(Material.Rock, cols, rows, 900f, ticks, phases, radius: 200f, spacing: 430f);

        Console.WriteLine();
        Console.WriteLine("── same, bodies just touching ──");
        Header();
        foreach (var (cols, rows) in new[] { (7, 5), (10, 7), (13, 10), (16, 12) })
            RunCase(Material.Rock, cols, rows, 900f, ticks, phases, radius: 200f, spacing: 400f);

        Console.WriteLine();
        Console.WriteLine("── worst case: bodies start half-interpenetrating, grain 900 ──");
        Header();
        foreach (var (cols, rows) in new[] { (8, 6), (12, 9), (16, 12) })
            RunCase(Material.Rock, cols, rows, 900f, ticks, phases, spacing: 118f);
    }

    /// <summary>
    /// What the persistent manifold costs in fidelity and buys in time, across the drift threshold.
    /// </summary>
    /// <remarks>
    /// <c>ManifoldDrift = 0</c> re-derives the manifold every substep, which is the behaviour that
    /// preceded the change — so the first row of each block is the reference the rest are read
    /// against, measured in the same process rather than recalled from a previous build.
    /// </remarks>
    private static void ManifoldSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        int ticks = ArgInt(args, "--ticks", 400);
        float[] drifts = { 0f, 0.02f, 0.05f, 0.10f, 0.15f, 0.25f, 1e9f };
        float[] muls = { 1f, 1.000001f, 1.0001f, 1.001f };

        Console.WriteLine("FractureBench — persistent contact manifold: fidelity and cost");
        Console.WriteLine("  drift = how far a body may move, in cells, before the narrow phase reruns");
        Console.WriteLine("  drift 0 = rebuild every substep = the behaviour before the change");
        Console.WriteLine("  every cell is the range over 4 runs whose speeds differ by <=0.1%, so the");
        Console.WriteLine("  drift-0 row shows what spread the scene produces on its own");
        Console.WriteLine();

        foreach (string scene in new[] { "collide", "projectile", "glass", "steel" })
        {
            Console.WriteLine($"── {scene} ──");
            Console.WriteLine("  drift    ms/tick   sat/tick      bodies      broken       big%        ov");
            foreach (float d in drifts)
            {
                double ms = 0, sat = 0;
                int bLo = int.MaxValue, bHi = int.MinValue, kLo = int.MaxValue, kHi = int.MinValue;
                float gLo = float.MaxValue, gHi = float.MinValue, oLo = float.MaxValue, oHi = float.MinValue;

                foreach (float mul in muls)
                {
                    var t = SimTuning.Default;
                    t.ManifoldDrift = d;
                    var r = ScenePerturbed(scene, t, mul);
                    var sw = Stopwatch.StartNew();
                    var m = SceneRunner.Run(r, ticks);
                    sw.Stop();
                    ms += sw.Elapsed.TotalMilliseconds / ticks;
                    sat += (double)r.Solver.C.SatCalls / ticks;
                    if (m.Bodies < bLo) bLo = m.Bodies; if (m.Bodies > bHi) bHi = m.Bodies;
                    if (m.Broken < kLo) kLo = m.Broken; if (m.Broken > kHi) kHi = m.Broken;
                    if (m.BigMassPct < gLo) gLo = m.BigMassPct; if (m.BigMassPct > gHi) gHi = m.BigMassPct;
                    if (m.PeakOverlap < oLo) oLo = m.PeakOverlap; if (m.PeakOverlap > oHi) oHi = m.PeakOverlap;
                }

                ms /= muls.Length; sat /= muls.Length;
                string label = d >= 1e8f ? "never" : d.ToString("F2", ci);
                Console.WriteLine(
                    $"  {label,-7} {ms.ToString("F3", ci),8} {sat.ToString("F0", ci),10}   "
                  + $"{$"{bLo}-{bHi}",9} {$"{kLo}-{kHi}",11} "
                  + $"{$"{gLo.ToString("F0", ci)}-{gHi.ToString("F0", ci)}",10} "
                  + $"{$"{oLo.ToString("F1", ci)}-{oHi.ToString("F1", ci)}",11}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Per-tick cost for one scenario, to find a tick that misbehaves rather than a scene that is
    /// merely large.
    /// </summary>
    private static void TickTrace(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        int ticks = ArgInt(args, "--ticks", 60);
        float grain = ArgFloat(args, "--grain", 900f);
        float speed = ArgFloat(args, "--speed", 900f);
        float mass = ArgFloat(args, "--mass", 3f);

        var tune = SimTuning.Default;
        var r = Scenarios.Projectile(tune, Material.Rock, speed, mass, grain);
        Console.WriteLine($"projectile grain {grain.ToString("F0", ci)} "
                        + $"({System.Math.Sqrt(grain).ToString("F1", ci)} px cells): "
                        + $"{r.State.CellCount} cells, {r.State.BondCount} bonds");
        Console.WriteLine();
        Console.WriteLine("  tick       ms   bodies   broken     dust   contacts   satcalls");

        var sw = new Stopwatch();
        for (int i = 0; i < ticks; i++)
        {
            r.Solver.C.Reset();
            sw.Restart();
            r.Solver.Step();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 5 || i % 5 == 0 || i >= ticks - 3)
                Console.WriteLine($"  {i,4} {ms.ToString("F2", ci),8} {r.State.BodyCount,8} "
                                + $"{r.Solver.Broken,8} {r.Solver.Dust,8} {r.Solver.ContactCount,10} "
                                + $"{r.Solver.C.SatCalls,10}");
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Every combination of worker count, chunk count and scene size, on the real solver.
    /// </summary>
    /// <remarks>
    /// The first attempt at this concluded parallelism did not pay here, from a synthetic benchmark
    /// that was compute-bound — a tight FMA loop, which is exactly the workload a power-limited
    /// laptop handles worst. The bond passes are not that: 19 ns for roughly thirty flops means they
    /// are waiting on memory, and a core stalled on a cache miss is not spending the power budget.
    /// So the earlier conclusion was drawn from the wrong test, and this measures the real one.
    ///
    /// <para>Two numbers per configuration. The <b>parallel phases</b> column is bond forces plus
    /// bond integration, which is all that is dispatched today, and is the honest measure of whether
    /// the decomposition works. The <b>whole tick</b> column is what the frame actually costs, and
    /// is bounded by the 70% that is still sequential.</para>
    /// </remarks>
    private static void JobSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        int ticks = ArgInt(args, "--ticks", 12);
        int warm = ArgInt(args, "--warm", 6);
        int[] workerSet = ArgInt(args, "--maxw", 0) > 0
            ? new[] { 2, 4, 6, 8, 11 } : new[] { 1, 2, 3, 4, 6, 8, 11 };
        int[] chunkSet = ArgInt(args, "--maxw", 0) > 0
            ? new[] { 8, 16, 32, 64, 128 }
            : new[] { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 };
        var scenes = ArgInt(args, "--maxw", 0) > 0
            ? new[] { (46, 36), (56, 44) }
            : new[] { (16, 12), (36, 28), (46, 36), (56, 44) };

        Console.WriteLine("FractureBench — parallel sweep: workers x chunks x scene size");
        Console.WriteLine($"  dense field (spacing 128), {warm} warm + {ticks} measured ticks per cell");
        Console.WriteLine("  'parallel phases' = inertial + bond forces + integrate + decompose +");
        Console.WriteLine("                     realize + damage, i.e. everything dispatched");
        Console.WriteLine();

        foreach (var (cols, rows) in scenes)
        {
            double seqTotal = 0, seqPar = 0;
            int cells = 0, bodies = 0;
            Measure(cols, rows, -1, 0, warm, ticks, ref seqTotal, ref seqPar, ref cells, ref bodies);

            Console.WriteLine($"══ {bodies} bodies, {cells} cells — sequential {seqTotal.ToString("F2", ci)} ms/tick, "
                            + $"of which parallel phases {seqPar.ToString("F2", ci)} ms");
            Console.WriteLine();
            Console.WriteLine("  parallel phases only (ms, and speedup vs sequential)");
            Console.Write("  chunks ");
            foreach (int w in workerSet) Console.Write($"{$"w={w}",14}");
            Console.WriteLine();

            var bestTotal = new double[chunkSet.Length, workerSet.Length];
            for (int ci2 = 0; ci2 < chunkSet.Length; ci2++)
            {
                Console.Write($"  {chunkSet[ci2],6} ");
                for (int wi = 0; wi < workerSet.Length; wi++)
                {
                    double tot = 0, par = 0; int c2 = 0, b2 = 0;
                    Measure(cols, rows, workerSet[wi], chunkSet[ci2], warm, ticks, ref tot, ref par, ref c2, ref b2);
                    bestTotal[ci2, wi] = tot;
                    Console.Write($"{$"{par.ToString("F2", ci)} ({(seqPar / par).ToString("F1", ci)}x)",14}");
                }
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.WriteLine("  whole tick (ms)");
            Console.Write("  chunks ");
            foreach (int w in workerSet) Console.Write($"{$"w={w}",14}");
            Console.WriteLine();
            for (int ci2 = 0; ci2 < chunkSet.Length; ci2++)
            {
                Console.Write($"  {chunkSet[ci2],6} ");
                for (int wi = 0; wi < workerSet.Length; wi++)
                    Console.Write($"{bestTotal[ci2, wi].ToString("F2", ci),14}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }
    }

    private static void Measure(int cols, int rows, int workers, int chunks, int warm, int ticks,
        ref double msTick, ref double msPar, ref int cells, ref int bodies)
    {
        var tune = SimTuning.Default;
        var r = Scenarios.Field(tune, Material.Rock, cols, rows, 60f, 128f, 60f, 900f);
        cells = r.State.CellCount; bodies = r.State.BodyCount;

        SimJobs? jobs = null;
        if (workers >= 0) { jobs = new SimJobs(workers, chunks); r.Solver.Jobs = jobs; }

        double[] phaseMs = new double[(int)SolverPhase.Count];
        var phaseSw = new Stopwatch();
        r.Solver.PhaseMark = p => { phaseMs[(int)p] += phaseSw.Elapsed.TotalMilliseconds; phaseSw.Restart(); };

        for (int i = 0; i < warm; i++) { phaseSw.Restart(); r.Solver.Step(); }
        Array.Clear(phaseMs);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++) { phaseSw.Restart(); r.Solver.Step(); }
        sw.Stop();

        msTick = sw.Elapsed.TotalMilliseconds / ticks;
        msPar = (phaseMs[(int)SolverPhase.Inertial]
               + phaseMs[(int)SolverPhase.BondForces]
               + phaseMs[(int)SolverPhase.BondIntegrate]
               + phaseMs[(int)SolverPhase.Decompose]
               + phaseMs[(int)SolverPhase.Realize]
               + phaseMs[(int)SolverPhase.Damage]) / ticks;
        jobs?.Dispose();
    }

    /// <summary>
    /// What the crush criterion actually responds to: overlap, closing speed, and contact duration.
    /// </summary>
    private static void CrushDiag(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        var t = SimTuning.Default;
        var mpRock = new MaterialProps(Material.Rock, t);
        float thrRock = mpRock.CrushStress;
        Console.WriteLine($"rock crush threshold = {thrRock.ToString("F0", ci)}   "
                        + $"ContactMaxBias = {t.ContactMaxBias.ToString("F1", ci)} px   "
                        + $"ContactBias = {t.ContactBias.ToString("F2", ci)}");

        // ── the material table, since these are authored now ─────────────────
        Console.WriteLine();
        Console.WriteLine("MATERIALS  (threshold and capacity authored; tensile = rho c^2 eps for scale)");
        Console.WriteLine("   material      tensile      crush thr   thr/tensile      capacity");
        foreach (var m in new[] { Material.Ice, Material.Sandstone, Material.Rock,
                                  Material.Glass, Material.Steel })
        {
            var mp = new MaterialProps(m, t);
            float tensile = mp.Rho * mp.Cpx * mp.Cpx * mp.Eps;
            Console.WriteLine($"   {m.Name,-10} {tensile.ToString("E2", ci),12} "
                + $"{mp.CrushStress.ToString("E2", ci),14} {(mp.CrushStress / tensile).ToString("F1", ci),13}x "
                + $"{mp.CrushRate.ToString("E2", ci),13}");
        }

        // ── A. pressure vs static overlap ────────────────────────────────────
        // Two bodies placed at a chosen overlap with zero velocity, stepped one tick. Nothing is
        // approaching, so the impulse term has almost nothing to brake and this reads the confining
        // term nearly on its own — which is the whole point of having one.
        Console.WriteLine();
        Console.WriteLine("A. PRESSURE vs STATIC OVERLAP  (at rest — the confining term on its own)");
        Console.WriteLine("   overlap px    peak dyn     peak conf    peak total   over thr?    peak work   carved");
        foreach (float push in new[] { 0.5f, 1f, 2f, 4f, 8f, 16f, 32f })
        {
            var r = Scenarios.Collide(t, Material.Rock, speed: 0f);
            // The blobs are ~130 px radius at x=250 and x=650, so 140 px of clear air. Close all of
            // it, then overlap by exactly `push`.
            r.State.BodyX[1] -= 140f + push;
            r.State.BodyVx[0] = 0f; r.State.BodyVx[1] = 0f;
            r.State.BodyW[0] = 0f; r.State.BodyW[1] = 0f;
            r.Solver.MeasureStress = true;
            r.Solver.Step();
            Console.WriteLine($"   {push,10} {r.Solver.PeakDyn.ToString("E2", ci),11} "
                + $"{r.Solver.PeakConf.ToString("E2", ci),13} {r.Solver.PeakStress.ToString("E2", ci),13}   "
                + $"{(r.Solver.PeakStress > thrRock ? "YES" : "no"),5} "
                + $"{r.Solver.PeakWork.ToString("E2", ci),12} {r.Solver.ShedArea.ToString("F3", ci),8}"
                + $"  [calls {r.Solver.DbgCalls} gate {r.Solver.DbgGate} depth {r.Solver.DbgWant} rem {r.Solver.DbgRemoved} ok {r.Solver.DbgCarved}]"
                + $" cc[len {r.Solver.CcLen} reach {r.Solver.CcReach} degen {r.Solver.CcDegen} zeroA {r.Solver.CcZeroArea} ok {r.Solver.CcOk} lastD {r.Solver.CcLastDepth:F4} lastR {r.Solver.CcLastRemoved:F4}]");
        }

        // ── B. pressure vs closing speed ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("B. PRESSURE vs CLOSING SPEED  (projectile, 60 ticks)");
        Console.WriteLine("   speed     peak dyn     peak conf    peak shed   carved  crushed");
        foreach (float v in new[] { 150f, 300f, 600f, 1200f, 2400f, 4800f })
        {
            var r = Scenarios.Projectile(t, Material.Rock, v, 3f);
            r.Solver.MeasureStress = true;
            for (int i = 0; i < 60; i++) r.Solver.Step();
            Console.WriteLine($"   {v,7} {r.Solver.PeakDyn.ToString("E2", ci),12} "
                + $"{r.Solver.PeakConf.ToString("E2", ci),13} "
                + $"{(100f * MaxShed(r.State)).ToString("F1", ci),11}% "
                + $"{r.Solver.ShedArea.ToString("F2", ci),8} {r.Solver.Crushed,9}");
        }

        // ── C. dose vs sustained contact ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("C. SHED vs SUSTAINED CONTACT  (slow closing press, worst cell, % of its limit)");
        Console.WriteLine("   ticks      60 px/s     150 px/s     400 px/s");
        foreach (int n in new[] { 20, 50, 100, 200, 400 })
        {
            Console.Write($"   {n,6}");
            foreach (float v in new[] { 60f, 150f, 400f })
            {
                var r = Scenarios.Collide(t, Material.Rock, speed: v);
                for (int i = 0; i < n; i++) r.Solver.Step();
                Console.Write($"{(100f * MaxShed(r.State)).ToString("F1", ci),12}%");
            }
            Console.WriteLine();
        }

        // ── D. does a settled pile erode? ────────────────────────────────────
        // The regression the deleted CrushFloor existed to prevent. Contact stiffness has a floor
        // independent of material, so a soft material's contacts are no softer than a hard one's; if
        // the crush threshold sits below what resting contact generates, a pile quietly evaporates.
        // With the threshold authored per material this has to be checked rather than assumed, and
        // it is checked on the SOFT materials because they are where it bites.
        Console.WriteLine();
        // ── E. where does a crushed cell's momentum GO? ──────────────────────
        Console.WriteLine();
        Console.WriteLine("E. FATE OF CRUSHED MOMENTUM  (projectile, 200 ticks)");
        Console.WriteLine("   The ledger is a SINK, not a transfer. Momentum booked there is counted");
        Console.WriteLine("   by the conservation test but has left the physics: it never reaches the");
        Console.WriteLine("   body that was pressing on the cell.");
        Console.WriteLine("   speed   crushed  transfers   p live      p ledger   ledger%    broken");
        foreach (float v in new[] { 300f, 600f, 900f, 1500f, 3000f })
        {
            var r = Scenarios.Projectile(t, Material.Rock, v, 3f);
            for (int i = 0; i < 200; i++) r.Solver.Step();
            r.Solver.TotalMomentum(out float px, out float py);
            float live = SimMath.Hypot(px, py);
            float led = SimMath.Hypot(r.Solver.ExportedPx, r.Solver.ExportedPy);
            float tot = live + led;
            Console.WriteLine($"   {v,6} {r.Solver.Crushed,9} {r.Solver.C.DustTransfers,10} {live.ToString("E2", ci),10} "
                + $"{led.ToString("E2", ci),13} {(100f * led / (tot < 1f ? 1f : tot)).ToString("F1", ci),8}% "
                + $"{r.Solver.Broken,9}");
        }

        Console.WriteLine();
        Console.WriteLine("D. RESTING EROSION  (5x5 field, 600 ticks)");
        Console.WriteLine("   'resting' starts already in light contact and never moves; 'drifting'");
        Console.WriteLine("   closes at 20 px/s, so its crushing is collision and may be legitimate.");
        Console.WriteLine("   material         thr    resting  peak press    drifting  peak press");
        foreach (var m in new[] { Material.Ice, Material.Sandstone, Material.Rock,
                                  Material.Glass, Material.Steel })
        {
            float thr = new MaterialProps(m, t).CrushStress;
            Console.Write($"   {m.Name,-10} {thr.ToString("E2", ci),11}");
            // resting: spacing below 2x radius so neighbours start touching, and no drift at all
            foreach (float drift in new[] { 0f, 20f })
            {
                var r = Scenarios.Field(t, m, 5, 5, 60f, drift == 0f ? 125f : 150f, drift);
                r.Solver.MeasureStress = true;
                int cells0 = r.State.CellCount;
                for (int i = 0; i < 600; i++) r.Solver.Step();
                Console.Write($" {r.Solver.Crushed,5} ({(100f * r.Solver.Crushed / cells0).ToString("F1", ci)}%)"
                    + $" {r.Solver.PeakStress.ToString("E2", ci),11}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>How the comminution dose limit trades absorption against over-fragmentation.</summary>
    /// <summary>
    /// Capacity is the TIME CONSTANT of comminution, and this is the sweep that shows why it
    /// matters more than the threshold. A stress wave needs a few ticks to cross a body; if cells
    /// at the contact powder faster than that, the impact is absorbed at the surface and the
    /// interior is never loaded, so nothing fractures however violent the hit.
    /// </summary>
    private static void CrushCapSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        var t = SimTuning.Default;
        Console.WriteLine("capacity sweep — rock projectile, 200 ticks");
        Console.WriteLine("  transit time of a 190 px body at c=2600 px/s is about 4 ticks");
        Console.WriteLine();
        // Lowering the threshold boosts the SLOW press far more than the fast impact. Dose rate is
        // (press - thr): under an impact press >> thr so the rate barely notices, while under a
        // sustained squeeze press is only just above thr and the rate is almost all threshold.
        // That is what lets one pair of numbers serve both regimes.
        Console.WriteLine("  thr      cap        rest  press/cap   p900 brk  p1500 brk  p3000 brk/crush");
        foreach (float thr in new[] { 1.0e6f, 5.0e5f, 2.5e5f, 1.0e5f })
        {
            foreach (float cap in new[] { 6.0e4f, 2.0e5f, 6.0e5f })
            {
                var m = Retune(Material.Rock, thr, cap);

                var rest = Scenarios.Field(t, m, 5, 5, 60f, 125f, 0f);
                for (int i = 0; i < 600; i++) rest.Solver.Step();

                var press = Scenarios.Collide(t, m, speed: 150f);
                for (int i = 0; i < 400; i++) press.Solver.Step();
                float dose = MaxShed(press.State);

                Console.Write($"  {thr.ToString("E1", ci),-8} {cap.ToString("E1", ci),-10} "
                    + $"{rest.Solver.Crushed,4} {dose.ToString("F2", ci),10}");
                foreach (float v in new[] { 900f, 1500f, 3000f })
                {
                    var r = Scenarios.Projectile(t, m, v, 3f);
                    for (int i = 0; i < 200; i++) r.Solver.Step();
                    Console.Write($"  {r.Solver.Broken,9}/{r.Solver.Crushed}");
                }
                Console.WriteLine();
            }
            Console.WriteLine();
        }
    }

    private delegate void TuneMut(ref SimTuning t);

    /// <summary>The candidate sources of fragment ejection speed, one row each.</summary>
    private static (string, TuneMut)[] Knobs() => new (string, TuneMut)[]
    {
        ("base",    (ref SimTuning t) => { }),
        ("nodust",  (ref SimTuning t) => t.Dust = false),
        ("spall.25",(ref SimTuning t) => t.SpallFraction = 0.25f),
        ("spall0",  (ref SimTuning t) => t.SpallFraction = 0f),
        ("bias.05", (ref SimTuning t) => t.ContactBias = 0.05f),
        ("bias.01", (ref SimTuning t) => t.ContactBias = 0.01f),
        ("cap0.5",  (ref SimTuning t) => t.ContactMaxBias = 0.5f),
        ("relax8",  (ref SimTuning t) => t.Relax = 8f),
        ("relax20", (ref SimTuning t) => t.Relax = 20f),
    };

    /// <summary>
    /// What the spall fraction costs and buys. Momentum drift and peak energy are in the table
    /// because the claim that this knob cannot break either is worth checking rather than asserting.
    /// </summary>
    private static void SpallSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine("spall fraction — share of a snapping bond's energy that becomes fragment velocity");
        Console.WriteLine("  speed should go as sqrt(fraction): 0.25 -> half, 0.0625 -> a quarter");
        Console.WriteLine();
        Console.WriteLine("  knob    scene        bodies  broken   mean eject   max eject   mom drift   peak KE");
        foreach (var (label, mut) in Knobs())
        {
            foreach (string scene in new[] { "collide", "projectile", "glass" })
            {
                var t = SimTuning.Default;
                mut(ref t);
                float f = 0f; _ = f;
                var r = Scene(scene, t);
                for (int i = 0; i < 400; i++) r.Solver.Step();

                // Ejection speed of FRAGMENTS: small bodies, measured against the mass-weighted mean
                // velocity of the scene, so a scene that is simply travelling does not read as fast.
                float mtot = 0f, mvx = 0f, mvy = 0f;
                for (int b = 0; b < r.State.BodyCount; b++)
                {
                    mtot += r.State.BodyM[b];
                    mvx += r.State.BodyM[b] * r.State.BodyVx[b];
                    mvy += r.State.BodyM[b] * r.State.BodyVy[b];
                }
                if (mtot > 0f) { mvx /= mtot; mvy /= mtot; }

                float sum = 0f, max = 0f; int n = 0;
                for (int b = 0; b < r.State.BodyCount; b++)
                {
                    if (r.State.BodyCellLen[b] > 4) continue;          // fragments only
                    float sp = SimMath.Hypot(r.State.BodyVx[b] - mvx, r.State.BodyVy[b] - mvy);
                    sum += sp; n++;
                    if (sp > max) max = sp;
                }
                float mean = n > 0 ? sum / n : 0f;

                Console.WriteLine($"  {label,-7} {scene,-12} {r.State.BodyCount,6} "
                    + $"{r.Solver.Broken,7} {mean.ToString("F0", ci),12} {max.ToString("F0", ci),11} "
                    + $"{(100f * r.MomentumDrift()).ToString("F3", ci),11}% "
                    + $"{(100f * r.EnergyFraction()).ToString("F1", ci),8}%");
            }
            Console.WriteLine();
        }
    }

    /// <summary>Vertex-count census: what fixed stride the polygon storage needs.</summary>
    private static void PolyCensus()
    {
        var t = SimTuning.Default;
        int globalMax = 0;
        foreach (var (name, grain) in new[] { ("collide", 900f), ("collide-fine", 100f),
                                              ("collide-vfine", 30f), ("projectile", 900f),
                                              ("field", 900f) })
        {
            var r = name.StartsWith("collide")
                ? Scenarios.Collide(t, Material.Rock, 600f, grain)
                : name == "projectile" ? Scenarios.Projectile(t, Material.Rock, 900f, 3f, grain)
                : Scenarios.Field(t, Material.Rock, 5, 5, 60f, 150f, 60f, grain);

            var hist = new int[40];
            int max = 0, cells = 0;
            for (int c = 0; c < r.State.CellCount; c++)
            {
                int n = r.State.PolyLen[c];
                if (n <= 0) continue;
                cells++;
                if (n < hist.Length) hist[n]++;
                if (n > max) max = n;
            }
            if (max > globalMax) globalMax = max;
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < hist.Length; i++)
                if (hist[i] > 0) parts.Append($" {i}:{hist[i]}");
            Console.WriteLine($"{name,-14} grain {grain,5} cells {cells,6} max {max,3} |{parts}");
        }
        Console.WriteLine();
        Console.WriteLine($"GLOBAL MAX VERTICES PER CELL AT BUILD: {globalMax}");
    }

    /// <summary>Worst shed fraction in the scene, as a share of that cell's material limit.</summary>
    private static float MaxShed(SimState s)
    {
        float worst = 0f;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c) || s.CellArea0[c] <= 0f) continue;
            float lim = SimMath.Max(0.01f, s.Mat(c).ShedLimit);
            float f = (1f - s.CellArea[c] / s.CellArea0[c]) / lim;
            if (f > worst) worst = f;
        }
        return worst;
    }

    /// <summary>Census of side labels: how many real, crack, sealed and bonded, over time.</summary>
    private static void SideCensus()
    {
        var t = SimTuning.Default;
        foreach (var (name, r) in new[]
        {
            ("collide", Scenarios.Collide(t, Material.Rock, speed: 600f)),
            ("glass", Scenarios.Projectile(t, Material.Glass)),
        })
        {
            Console.WriteLine($"── {name} ──");
            Console.WriteLine("   tick    real   crack  sealed  bonded   bodies");
            for (int i = 0; i <= 200; i++)
            {
                if (i % 50 == 0)
                {
                    var s = r.State;
                    int real = 0, crack = 0, seal = 0, bond = 0;
                    for (int c = 0; c < s.CellCount; c++)
                    {
                        if (s.Dead(c)) continue;
                        int off = s.PolyOff[c];
                        for (int v = 0; v < s.PolyLen[c]; v++)
                        {
                            short k = s.PolyBond[off + v];
                            if (k == SimState.SideReal) real++;
                            else if (k == SimState.SideCrack) crack++;
                            else if (k == SimState.SideSealed) seal++;
                            else bond++;
                        }
                    }
                    Console.WriteLine($"   {i,4} {real,7} {crack,7} {seal,7} {bond,7} {s.BodyCount,8}");
                }
                r.Solver.Step();
            }
        }
    }

    /// <summary>Runs the side-classification audit per tick and reports where it first breaks.</summary>
    private static void SideAuditRun()
    {
        var t = SimTuning.Default;
        var t170 = SimTuning.Default; t170.ToughnessScale = 1.1f; t170.CarveContinuity = 0.75f; t170.CrushConfine = 0.10f;
        var t255 = SimTuning.Default; t255.ToughnessScale = 1.7f;
        var hardSteel = new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 2.0e6f, 0.01875f, 0.50f, 2.5f);
        foreach (var (name, r) in new[]
        {
            ("collide/rock", Scenarios.Collide(t, Material.Rock, speed: 600f)),
            ("collide/glass", Scenarios.Collide(t, Material.Glass, speed: 600f)),
            ("projectile", Scenarios.Projectile(t, Material.Rock)),
            ("collide/rock g170", Scenarios.Collide(t170, Material.Rock, 600f, 170f)),
            ("steel/steel g255 2250", Scenarios.Projectile(t255, hardSteel, 2250f, 5.5f, 255f, impactor: hardSteel)),
            ("steel/rock g900 900", Scenarios.Projectile(t, Material.Rock, 900f, 3f, 900f, impactor: Material.Steel)),
        })
        {
            var rep = new SideAudit.Report();
            SideAudit.Audit(r.State, rep, 0);
            SideAudit.AuditGeometry(r.State, rep, 0);
            {
                var s2 = r.State; int nReal = 0, nPart = 0, nOther = 0;
                for (int c = 0; c < s2.CellCount; c++)
                {
                    if (s2.Dead(c)) continue;
                    for (int v = 0; v < s2.PolyLen[c]; v++)
                    {
                        var kk = r.Solver.ClassifySide(c, v, out float g0, out float g1);
                        if (kk == Solver.SideKind.RealSurface) nReal++;
                        else { nOther++; if (g0 > 1e-3f || g1 < 1f - 1e-3f) nPart++; }
                    }
                }
                Console.WriteLine($"   sides: {nReal} real, {nOther} covered, {nPart} partly exposed");
                int shown = 0;
                for (int c = 0; c < s2.CellCount && shown < 4; c++)
                {
                    if (s2.Dead(c)) continue;
                    int off = s2.PolyOff[c];
                    for (int v = 0; v < s2.PolyLen[c] && shown < 4; v++)
                    {
                        var kk = r.Solver.ClassifySide(c, v, out float g0, out float g1);
                        if (kk == Solver.SideKind.RealSurface) continue;
                        if (g0 <= 1e-3f && g1 >= 1f - 1e-3f) continue;
                        short rec = s2.SideTouch[off + v];
                        int o = s2.TouchOther(rec, c);
                        int w = v + 1 == s2.PolyLen[c] ? 0 : v + 1;
                        float sl = SimMath.Hypot(s2.PolyX[off + w] - s2.PolyX[off + v],
                                                 s2.PolyY[off + w] - s2.PolyY[off + v]);
                        Console.WriteLine($"      cell {c} side {v} (len {sl:F2}) rec {rec} with {o}: "
                            + $"covered [{g0:F3},{g1:F3}]  rec span {s2.TouchT1[rec] - s2.TouchT0[rec]:F2}");
                        shown++;
                    }
                }
            }
            Console.WriteLine($"── {name} — at BUILD: {rep}");

            var run = new SideAudit.Report();
            for (int i = 1; i <= 200; i++)
            {
                r.Solver.Step();
                SideAudit.Audit(r.State, run, i);
                if (i % 100 == 0) Console.WriteLine($"   tick {i,3}: {FalseSurface(r, out int part)} false, {part} partial"
                    + $", clips {r.Solver.CarveClips}, simplifications {r.Solver.CarveSimplifications}"
                    + $", refused {r.Solver.CarveRefused}");
            }
            Console.WriteLine($"   over 200 ticks: {run}");
            if (run.FirstUnlinked != null) Console.WriteLine($"   first unlinked: {run.FirstUnlinked}");
        }
    }

    /// <summary>
    /// Counts what the VIEW would draw as real surface with material actually across it — exactly
    /// the classification the renderer uses, probed geometrically.
    /// </summary>
    private static int FalseSurface(Scenarios.Result r, out int partial)
    {
        SimState s = r.State;
        int bad = 0; partial = 0;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            if (len < 3) continue;
            float probe = SimMath.Max(0.05f, s.CellRad[c] * 0.03f);

            for (int v = 0; v < len; v++)
            {
                var kind = r.Solver.ClassifySide(c, v, out float f0, out float f1);
                bool anyExposed = kind == Solver.SideKind.RealSurface
                                  || f0 > 1e-3f || f1 < 1f - 1e-3f;
                if (!anyExposed) continue;
                if (kind != Solver.SideKind.RealSurface) partial++;

                // Probe the midpoint of an exposed span.
                float a0 = kind == Solver.SideKind.RealSurface ? 0f : (f0 > 1e-3f ? 0f : f1);
                float a1 = kind == Solver.SideKind.RealSurface ? 1f : (f0 > 1e-3f ? f0 : 1f);
                float mid = 0.5f * (a0 + a1);

                int w = v + 1 == len ? 0 : v + 1;
                float x0 = s.PolyX[off + v], y0 = s.PolyY[off + v];
                float x1 = s.PolyX[off + w], y1 = s.PolyY[off + w];
                float dx = x1 - x0, dy = y1 - y0;
                float dl = SimMath.Hypot(dx, dy);
                if (dl < 1e-4f) continue;
                float px = s.CellRx[c] + x0 + dx * mid + (dy / dl) * probe;
                float py = s.CellRy[c] + y0 + dy * mid - (dx / dl) * probe;

                if (Covered(s, px, py, s.CellBody[c], c)) bad++;
            }
        }
        return bad;
    }

    private static bool Covered(SimState s, float px, float py, int body, int skip)
    {
        for (int o = 0; o < s.CellCount; o++)
        {
            if (o == skip || s.Dead(o) || s.CellBody[o] != body) continue;
            int off = s.PolyOff[o], len = s.PolyLen[o];
            if (len < 3) continue;
            float qx = px - s.CellRx[o], qy = py - s.CellRy[o];
            if (SimMath.Hypot(qx, qy) > s.CellRad[o]) continue;
            bool inside = true;
            for (int v = 0; v < len && inside; v++)
            {
                int w = v + 1 == len ? 0 : v + 1;
                float ax = s.PolyX[off + v], ay = s.PolyY[off + v];
                float bx = s.PolyX[off + w], by = s.PolyY[off + w];
                inside = (bx - ax) * (qy - ay) - (by - ay) * (qx - ax) >= -1e-3f;
            }
            if (inside) return true;
        }
        return false;
    }

    /// <summary>One controlled carve, with every side's classification before and after.</summary>
    private static void CarveTrace()
    {
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 0f);
        SimState s = r.State;

        // An interior-ish cell with several bonded sides.
        int target = -1;
        for (int c = 0; c < s.CellCount && target < 0; c++)
        {
            int covered = 0;
            for (int v = 0; v < s.PolyLen[c]; v++)
                if (r.Solver.ClassifySide(c, v, out _, out _) != Solver.SideKind.RealSurface) covered++;
            if (covered >= 4) target = c;
        }
        Console.WriteLine($"cell {target}, radius {s.CellRad[target]:F2}, {s.PolyLen[target]} sides");

        void Dump(string when)
        {
            Console.WriteLine($"  ── {when} ──");
            int off = s.PolyOff[target], len = s.PolyLen[target];
            for (int v = 0; v < len; v++)
            {
                int w = v + 1 == len ? 0 : v + 1;
                float sl = SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v],
                                         s.PolyY[off + w] - s.PolyY[off + v]);
                var k = r.Solver.ClassifySide(target, v, out float f0, out float f1);
                short rec = s.SideTouch[off + v];
                string span = rec >= 0 && rec < s.TouchCount && s.TouchA[rec] >= 0
                    ? $"rec {rec} [{s.TouchT0[rec]:F2},{s.TouchT1[rec]:F2}]" : "no record";
                Console.WriteLine($"    side {v}: len {sl,6:F2}  {k,-12} covered [{f0:F3},{f1:F3}]  {span}");
            }
        }

        Dump("before");
        // Carve along +x, taking a slab off that side.
        float removed = r.Solver.CarveCellByArea(target, 1f, 0f, 30f);
        Console.WriteLine($"  carved {removed:F2} area along +x");
        Dump("after");

        // The neighbour across record 0 — its side is untouched, but part of it now faces the
        // carve face and should read as exposed. THIS is the copy the carved cell cannot see.
        int nb = s.TouchA[0] == target ? s.TouchB[0] : s.TouchA[0];
        Console.WriteLine($"  ── neighbour {nb} across record 0 ──");
        int noff = s.PolyOff[nb], nlen = s.PolyLen[nb];
        for (int v = 0; v < nlen; v++)
        {
            if (s.SideTouch[noff + v] != 0) continue;
            int w = v + 1 == nlen ? 0 : v + 1;
            float sl = SimMath.Hypot(s.PolyX[noff + w] - s.PolyX[noff + v],
                                     s.PolyY[noff + w] - s.PolyY[noff + v]);
            var k = r.Solver.ClassifySide(nb, v, out float f0, out float f1);
            Console.WriteLine($"    side {v}: len {sl,6:F2}  {k,-12} covered [{f0:F3},{f1:F3}]"
                + $"  -> covered {sl * (f1 - f0),6:F2}, exposed {sl * (1f - (f1 - f0)),6:F2}");
        }
    }

    /// <summary>Finds the tick a side first goes falsely-surface, and dumps its state either side.</summary>
    private static void Tick69(string[] args)
    {
        int stop = ArgInt(args, "--at", 69);
        ProbeDist = ArgFloat(args, "--probe", 0.03f);
        Console.WriteLine($"probe = {ProbeDist:F4} x cell radius");
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 170f);
        SimState s = r.State;

        var wasFalse = new System.Collections.Generic.HashSet<long>();
        float[] t0 = new float[4096], t1 = new float[4096];
        int[] ta = new int[4096], tb = new int[4096];
        short[] tbond = new short[4096];

        for (int tick = 1; tick <= stop; tick++)
        {
            for (int i = 0; i < s.TouchCount && i < 4096; i++)
            { t0[i] = s.TouchT0[i]; t1[i] = s.TouchT1[i]; ta[i] = s.TouchA[i]; tb[i] = s.TouchB[i]; tbond[i] = s.TouchBond[i]; }

            r.Solver.Step();

            var now = FalseSides(r);
            foreach (long key in now)
            {
                if (!wasFalse.Add(key)) continue;
                int c = (int)(key >> 20), v = (int)(key & 0xFFFFF);
                if (tick < stop) continue;
                int off = s.PolyOff[c], len = s.PolyLen[c];
                short rec = s.SideTouch[off + v];
                int w = v + 1 == len ? 0 : v + 1;
                float sl = SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v],
                                         s.PolyY[off + w] - s.PolyY[off + v]);
                var kind = r.Solver.ClassifySide(c, v, out float f0, out float f1);
                Console.WriteLine($"tick {tick}: cell {c} (body {s.CellBody[c]}, {len} sides) side {v} "
                    + $"len {sl:F2} -> {kind} covered [{f0:F3},{f1:F3}]");
                if (rec >= 0 && rec < s.TouchCount)
                    Console.WriteLine($"           rec {rec}: A={s.TouchA[rec]} B={s.TouchB[rec]} "
                        + $"bond={s.TouchBond[rec]} span [{s.TouchT0[rec]:F2},{s.TouchT1[rec]:F2}]  "
                        + $"BEFORE A={ta[rec]} B={tb[rec]} bond={tbond[rec]} span [{t0[rec]:F2},{t1[rec]:F2}]");
                else
                    Console.WriteLine($"           SideTouch = {rec} (no record)");

            }
        }
        Console.WriteLine("no new false surface found");
    }

    private static System.Collections.Generic.HashSet<long> FalseSides(Scenarios.Result r)
    {
        SimState s = r.State;
        var set = new System.Collections.Generic.HashSet<long>();
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            if (len < 3) continue;
            float probe = ProbeDist * SimMath.Max(1f, s.CellRad[c]);
            for (int v = 0; v < len; v++)
            {
                var kind = r.Solver.ClassifySide(c, v, out float f0, out float f1);
                if (kind != Solver.SideKind.RealSurface && f0 <= 1e-3f && f1 >= 1f - 1e-3f) continue;
                int w = v + 1 == len ? 0 : v + 1;
                float x0 = s.PolyX[off + v], y0 = s.PolyY[off + v];
                float dx = s.PolyX[off + w] - x0, dy = s.PolyY[off + w] - y0;
                float dl = SimMath.Hypot(dx, dy);
                if (dl < 1e-4f) continue;
                float mid = kind == Solver.SideKind.RealSurface ? 0.5f
                          : (f0 > 1e-3f ? 0.5f * f0 : 0.5f * (1f + f1));
                float px = s.CellRx[c] + x0 + dx * mid + (dy / dl) * probe;
                float py = s.CellRy[c] + y0 + dy * mid - (dx / dl) * probe;
                if (Covered(s, px, py, s.CellBody[c], c)) set.Add(((long)c << 20) | (uint)v);
            }
        }
        return set;
    }

    /// <summary>Per-tick carve census: how much each cell lost, and how scattered the directions were.</summary>
    private static void NormalSpread(string[] args)
    {
        int at = ArgInt(args, "--at", 36);
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 170f);
        SimState s = r.State;
        r.Solver.CarveLogging = true;

        for (int tick = 1; tick <= at; tick++)
        {
            r.Solver.CarveLogCount = 0;
            r.Solver.Step();
            if (tick < at - 1) continue;

            int n = r.Solver.CarveLogCount;
            Console.WriteLine($"── tick {tick}: {n} clips ──");

            // group by cell
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int c = r.Solver.CarveLogCell[i];
                if (!seen.Add(c)) continue;
                float area = 0f, sx = 0f, sy = 0f, mag = 0f;
                int cuts = 0;
                for (int j = 0; j < n; j++)
                {
                    if (r.Solver.CarveLogCell[j] != c) continue;
                    float a = r.Solver.CarveLogArea[j];
                    area += a; cuts++;
                    sx += r.Solver.CarveLogNx[j] * a; sy += r.Solver.CarveLogNy[j] * a; mag += a;
                }
                float coh = mag > 1e-9f ? SimMath.Hypot(sx, sy) / mag : 0f;
                float shed = s.CellArea0[c] > 0f ? 1f - s.CellArea[c] / s.CellArea0[c] : 0f;
                if (area < 0.5f) continue;
                Console.WriteLine($"   cell {c,4}: {cuts,2} cuts, area {area,7:F2} "
                    + $"({100f * area / SimMath.Max(1f, s.CellArea0[c]),5:F1}% of build), "
                    + $"direction coherence {coh:F2}, total shed {100f * shed,5:F1}%");
            }
        }
    }

    private static void TraceOneCell(string[] args)
    {
        int cell = ArgInt(args, "--cell", 562);
        int from = ArgInt(args, "--from", 30), to = ArgInt(args, "--to", 38);
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 170f);
        SimState s = r.State;
        r.Solver.TraceSink = Console.WriteLine;

        for (int tick = 1; tick <= to; tick++)
        {
            r.Solver.TraceCell = tick >= from ? cell : -1;
            if (tick >= from)
                Console.WriteLine($"── tick {tick}  body {s.CellBody[cell]}  "
                    + $"area {s.CellArea[cell]:F1}/{s.CellArea0[cell]:F1}  "
                    + $"shed {100f * (1f - s.CellArea[cell] / SimMath.Max(1f, s.CellArea0[cell])):F1}%  "
                    + $"sides {s.PolyLen[cell]}  dead {s.Dead(cell)}");
            r.Solver.Step();
        }
    }

    /// <summary>State of one cell pair at a given tick, swept over the continuity knob.</summary>
    private static void TracePair(string[] args)
    {
        int ca = ArgInt(args, "--a", 455), cb = ArgInt(args, "--b", 474);
        int at = ArgInt(args, "--at", 42);
        foreach (float cont in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            var tune = SimTuning.Default;
            tune.CarveContinuity = cont;
            var r = Scenarios.Collide(tune, Material.Rock, 600f, 170f);
            SimState s = r.State;
            for (int i = 0; i < at; i++) r.Solver.Step();

            int bond = -1;
            for (int k = 0; k < s.BondCount; k++)
                if ((s.BondA[k] == ca && s.BondB[k] == cb) || (s.BondA[k] == cb && s.BondB[k] == ca))
                { bond = k; break; }

            string state = bond < 0 ? "no bond exists"
                : s.BondBroken[bond] ? $"bond {bond} BROKEN" : $"bond {bond} intact";
            string bodies = $"bodies {s.CellBody[ca]}/{s.CellBody[cb]}";
            string shed = $"shed {100f * (1f - s.CellArea[ca] / SimMath.Max(1f, s.CellArea0[ca])):F1}%"
                        + $"/{100f * (1f - s.CellArea[cb] / SimMath.Max(1f, s.CellArea0[cb])):F1}%";
            Console.WriteLine($"continuity {cont:F2}: {state}, {bodies}, {shed}, "
                + $"dead {s.Dead(ca)}/{s.Dead(cb)} | vanished sides {r.Solver.CcVanishTotal},"
                + $" steered {r.Solver.CcSteered}, shielded {r.Solver.CcShielded}");
        }
    }

    private static void CarveCensus(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 200);
        bool loopCfg = Array.IndexOf(args, "--loopcfg") >= 0;
        foreach (float minArea in loopCfg ? new[] { 0.005f } : new[] { 0f, 0.0025f, 0.005f, 0.01f, 0.02f })
        {
            var tune = SimTuning.Default;
            tune.CarveMinArea = minArea;
            if (loopCfg) { tune.ToughnessScale = 1.1f; tune.CarveContinuity = 0.75f; tune.CrushConfine = 0.10f; }
            var r = Scenarios.Collide(tune, Material.Rock, 600f, 170f);
            var cen = new Solver.CarveCensus();
            r.Solver.Census = cen;
            if (loopCfg) r.Solver.DentProbe = msg => Console.WriteLine("   probe: " + msg);
            float peakOv = 0f;
            for (int i = 0; i < ticks; i++)
            {
                r.Solver.Step();
                if (r.Solver.MaxOverlap > peakOv) peakOv = r.Solver.MaxOverlap;
            }
            SimState st = r.State;
            float shed = 0f, area0 = 0f;
            for (int c = 0; c < st.CellCount; c++)
            { area0 += st.CellArea0[c]; shed += st.CellArea0[c] - (st.Dead(c) ? 0f : st.CellArea[c]); }

            Console.WriteLine($"── CarveMinArea {minArea:P2}: clips {r.Solver.CarveClips}, "
                            + $"vanished {cen.Vanished}, peak overlap {peakOv:F1}px, "
                            + $"total shed {100f * shed / area0:F1}% of body area");
            Console.WriteLine($"   carve calls {cen.Calls}, no-surface {cen.NoSurface}");
            Console.WriteLine($"   direction kept exactly {cen.Unturned}, steered {cen.Calls - cen.NoSurface - cen.Unturned}"
                            + $" (to a corner shared with a covered side: {cen.SteerToCoveredCorner}, to an open corner: {cen.SteerToOpenCorner})");
            Console.WriteLine($"   v2 dents: calls {r.Solver.DentCalls}, record ends {r.Solver.DentVertices}, corners {r.Solver.DentCorners}, reclips {r.Solver.DentReclips}, "
                            + $"records spent {r.Solver.DentSpent}, v1 fallbacks {r.Solver.DentFallback}; v1 clips {r.Solver.CarveClips}");
            Console.WriteLine($"   dent calls that slid nothing: no exposed end at all {r.Solver.DentNoOpenEnd}, all out of reach {r.Solver.DentOutOfReach}");
            Console.WriteLine($"   propagation: record ends exposed {r.Solver.DentExposed}, vertex splits {r.Solver.DentSplits}, budget refusals {r.Solver.DentBudgetRefused}");
            Console.WriteLine($"   area removed per unit depth (in CellRad): {Hist(r.Solver.DentWidth, "0", "2R+")}");
            Console.WriteLine($"   removed / target A*                     : {Hist(r.Solver.DentHit, "0", "2+")}   (guard bound {r.Solver.DentGuarded}, insensitive {r.Solver.DentInsensitive})");
            Console.WriteLine($"   total removed {r.Solver.DentSumRemoved:F0} vs v1's target depth·perim/4 {r.Solver.DentSumDepthLcw:F0} (ratio {r.Solver.DentSumRemoved / System.Math.Max(1e-9, r.Solver.DentSumDepthLcw):F2})");
            Console.WriteLine($"   CellRad/4 cap bound on {r.Solver.DbgCapHit} of {r.Solver.DbgCalls} carve calls; when it bound, uncapped depth averaged {r.Solver.DbgCapExcess / System.Math.Max(1, r.Solver.DbgCapHit):F1}x the cap");
            Console.WriteLine($"   nearest exposed end (in R)    : {Hist(r.Solver.DentNearest, "0", "2R+")}");
            Console.WriteLine($"   BondedGuard (half-BondLen) moved the plane on {cen.GuardClamped} clips");
            Console.WriteLine($"   shield: clamped {cen.ShieldClamped} clips, refused {cen.ShieldRefused}; clamped clips wanted "
                            + $"{cen.ClampedWant:F0} area and removed {cen.ClampedArea:F0} ({100.0 * cen.ClampedArea / System.Math.Max(1e-9, cen.TotalRemoved):F1}% of all removed)");
            Console.WriteLine($"   cos of rotation when steered  : {Hist(cen.Turn, "0", "1")}");
            Console.WriteLine($"   removed area / cell area (%)  : {Hist(cen.AreaFrac, "0%", "10%+")}");
            Console.WriteLine($"   cut depth (px)                : {Hist(cen.DepthPx, "0", "0.5px+")}");
            Console.WriteLine($"   vanished sides {cen.Vanished}, of which the direction had been steered: {cen.VanishAfterTurn}");
            Console.WriteLine($"   |cos| clip vs vanished bisect : {Hist(cen.VanishCos, "0", "1")}");
            Console.WriteLine($"   span left on retired adjacency: {Hist(cen.VanishSpan, "0", "5px+")}");
            Console.WriteLine();
        }
    }

    private static string Hist(int[] b, string lo, string hi)
    {
        int tot = 0; foreach (int v in b) tot += v;
        if (tot == 0) return "(none)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < b.Length; i++) sb.Append($"{100.0 * b[i] / tot,5:F1}");
        return sb + $"   [{lo} .. {hi}]  n={tot}";
    }

    private static void StretchProbe(string[] args)
    {
        foreach (bool shield in new[] { false, true })
        foreach (float minArea in new[] { 0f, 0.005f })
        {
            var tune = SimTuning.Default;
            tune.CarveMinArea = minArea;
            foreach (var (name, mk) in new (string, Func<SimTuning, Scenarios.Result>)[]
            {
                ("projectile", tt => Scenarios.Projectile(tt, Material.Rock)),
                ("collide",    tt => Scenarios.Collide(tt, Material.Rock, 600f, 170f)),
            })
            {
                var r = mk(tune);
                r.Solver.CarveShield = shield;
                float peakStretch = 0f, peakOv = 0f;
                for (int i = 0; i < 200; i++)
                {
                    r.Solver.Step();
                    SimState s = r.State;
                    for (int k = 0; k < s.BondCount; k++)
                    {
                        if (s.BondBroken[k]) continue;
                        float st = SimMath.Abs(s.BondSn[k]) / SimMath.Max(1e-3f, s.BondLen[k]);
                        if (st > peakStretch) peakStretch = st;
                    }
                    if (r.Solver.MaxOverlap > peakOv) peakOv = r.Solver.MaxOverlap;
                }
                Console.WriteLine($"shield={shield,-5} minArea={minArea:P2} {name,-10}: "
                    + $"peak stretch {100f * peakStretch,9:F1}%  peak overlap {peakOv,5:F1}px  "
                    + $"shielded {r.Solver.CcShielded}");
            }
        }
    }

    /// <summary>Carving-created internal surface: collide rock, grain 170, tough 1.1, cont 0.75, confine 0.10.</summary>
    /// <summary>Every audit fault present at tick 0, with the geometry behind it.</summary>
    private static void BuildFaults(string[] args)
    {
        foreach (string scene in new[] { "collide", "projectile" })
        {
            var r = Scene(scene, SimTuning.Default);
            SimState s = r.State;
            var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
            SideAudit.Audit(s, rep, 0);
            Console.WriteLine($"── {scene}: {rep.Total} faults at build");
            foreach (string line in rep.All!) Console.WriteLine("   " + line);
            foreach (string line in rep.All!)
            {
                // pull "cell N side V" and "record R"
                var m = System.Text.RegularExpressions.Regex.Match(line, @"cell (\d+) side (\d+).*record (\d+)");
                var m2 = System.Text.RegularExpressions.Regex.Match(line, @"record (\d+) \((\d+) body \d+, (\d+)");
                int rec = m.Success ? int.Parse(m.Groups[3].Value) : m2.Success ? int.Parse(m2.Groups[1].Value) : -1;
                if (rec < 0) continue;
                int a = s.TouchA[rec], b = s.TouchB[rec];
                Console.WriteLine($"   record {rec}: ({a},{b}) bond {s.TouchBond[rec]} span [{s.TouchT0[rec]:F3},{s.TouchT1[rec]:F3}] open {s.TouchOpen[rec]}");
                foreach (int c in new[] { a, b })
                {
                    int off = s.PolyOff[c], len = s.PolyLen[c];
                    for (int v = 0; v < len; v++)
                    {
                        int w = v + 1 == len ? 0 : v + 1;
                        float dx = s.PolyX[off + w] - s.PolyX[off + v], dy = s.PolyY[off + w] - s.PolyY[off + v];
                        var kind = r.Solver.ClassifySide(c, v, out float f0, out float f1);
                        Console.WriteLine($"      cell {c} side {v}: bond {s.PolyBond[off + v],4} rec {s.SideTouch[off + v],4} {kind,-11} "
                            + $"cover [{f0:F3},{f1:F3}] len {SimMath.Hypot(dx, dy):F3}");
                    }
                }
            }
        }
    }

    /// <summary>A sample of OpenSideIsCovered faults on collide/rock, with the cells behind them.</summary>
    private static void OpenCoveredSample(string[] args)
    {
        int at = ArgInt(args, "--at", 100), show = ArgInt(args, "--show", 4);
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, 600f);
        SimState s = r.State;
        for (int i = 0; i < at; i++) r.Solver.Step();
        var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
        SideAudit.Audit(s, rep, at);
        int shown = 0, total = 0, lone = 0, small = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (string line in rep.All!)
        {
            if (!line.StartsWith("OpenSideIsCovered")) continue;
            total++;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"cell (\d+) side (\d+) is open but cell (\d+)");
            int c = int.Parse(m.Groups[1].Value), v = int.Parse(m.Groups[2].Value), o = int.Parse(m.Groups[3].Value);
            int b = s.CellBody[c];
            if (s.BodyCellLen[b] <= 2) lone++;
            int off = s.PolyOff[c], w = v + 1 == s.PolyLen[c] ? 0 : v + 1;
            if (SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v], s.PolyY[off + w] - s.PolyY[off + v]) < 0.5f) small++;
            if (shown < show && seen.Add(c))
            {
                shown++;
                Console.WriteLine($"── {line}   (body {b} has {s.BodyCellLen[b]} cells)");
                DumpCell(r, c); DumpCell(r, o);
            }
        }
        Console.WriteLine($"OpenSideIsCovered at tick {at}: {total}; in bodies of ≤2 cells: {lone}; on sides under 0.5px: {small}");
    }

    /// <summary>Steel on steel below the fracture threshold: does the surface recede as a dent?</summary>
    private static void DentProfile(string[] args)
    {
        float speed = ArgInt(args, "--speed", 900);
        int ticks = ArgInt(args, "--ticks", 60);
        foreach (float dent in new[] { 1.0f, 2.5f, 4.0f })
        {
            var steel = new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 1.0e6f, 0.3f, 0.50f, dent);
            var r = Scenarios.Projectile(SimTuning.Default, steel, speed, 3f, 180f, impactor: steel);
            SimState s = r.State;
            int cells0 = s.CellCount;
            for (int i = 0; i < ticks; i++) r.Solver.Step();

            int broken = 0; for (int k = 0; k < s.BondCount; k++) if (s.BondBroken[k]) broken++;
            int dead = 0; for (int c = 0; c < s.CellCount; c++) if (s.Dead(c)) dead++;

            // Shed fraction of the target's cells, binned by distance from the most-eroded cell.
            int worst = -1; float worstShed = 0f;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c) || s.CellBody[c] != 0) continue;
                float sh = 1f - s.CellArea[c] / s.CellArea0[c];
                if (sh > worstShed) { worstShed = sh; worst = c; }
            }
            var bins = new float[8]; var cnt = new int[8];
            if (worst >= 0)
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c) || s.CellBody[c] != 0) continue;
                    float d = SimMath.Hypot(s.CellRx[c] - s.CellRx[worst], s.CellRy[c] - s.CellRy[worst]) / s.CellRad[worst];
                    int b = (int)SimMath.Min(7f, d);
                    bins[b] += 1f - s.CellArea[c] / s.CellArea0[c]; cnt[b]++;
                }
            var sb = new System.Text.StringBuilder();
            for (int b = 0; b < 8; b++) sb.Append(cnt[b] > 0 ? $"{100f * bins[b] / cnt[b],5:F1}" : "    -");
            Console.WriteLine($"dent {dent:F1}: bodies {s.BodyCount}, bonds broken {broken}, cells dead {dead}, "
                + $"ledger {LedgerPct(r.Solver):F1}%; mean shed % by distance (in cell radii) from the deepest point: {sb}");
            Console.WriteLine($"          cap bound on {r.Solver.DbgCapHit} of {r.Solver.DbgCalls} calls, uncapped depth avg {r.Solver.DbgCapExcess / System.Math.Max(1, r.Solver.DbgCapHit):F1}x cap; "
                + $"area/depth (R): {Hist(r.Solver.DentWidth, "0", "2R+")}");
        }
    }

    private static float LedgerPct(Solver solver)
    {
        solver.TotalMomentum(out float px, out float py);
        float live = SimMath.Hypot(px, py), led = SimMath.Hypot(solver.ExportedPx, solver.ExportedPy);
        return live + led < 1f ? 0f : 100f * led / (live + led);
    }

    /// <summary>Projectile steel-vs-rock, defaults: the 62/49/35 vertex between ticks 15 and 16.</summary>
    private static void TriRepro(string[] args)
    {
        int[] cells = { ArgInt(args, "--a", 62), ArgInt(args, "--b", 49), ArgInt(args, "--c", 35) };
        int from = ArgInt(args, "--from", 15), to = ArgInt(args, "--to", 16);
        var r = Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 3f, 900f, impactor: Material.Steel);
        SimState s = r.State;
        bool Watched(int c) => Array.IndexOf(cells, c) >= 0;

        int tk = 0;
        r.Solver.RetireSink = (rec, a, b, why) => { if (Watched(a) || Watched(b)) Console.WriteLine($"   tick {tk}: record {rec} ({a},{b}) retired: {why}"); };
        for (int tick = 0; tick <= to; tick++)
        {
            tk = tick;
            if (tick >= from)
            {
                Console.WriteLine($"── start of tick {tick}");
                foreach (int c in cells) DumpCell(r, c);
                for (int k = 0; k < s.TouchCount; k++)
                    if (s.TouchA[k] >= 0 && Watched(s.TouchA[k]) && Watched(s.TouchB[k]))
                        Console.WriteLine($"   record {k}: ({s.TouchA[k]},{s.TouchB[k]}) bond {s.TouchBond[k]} span [{s.TouchT0[k]:F3},{s.TouchT1[k]:F3}] built [{s.TouchS0[k]:F3},{s.TouchS1[k]:F3}] open {s.TouchOpen[k]}");
                r.Solver.TraceCell = cells[0];
                r.Solver.TraceSink = m => { if (m.Contains("dent")) Console.WriteLine(m); };
            }
            r.Solver.Step();
            r.Solver.TraceCell = -1; r.Solver.TraceSink = null;
        }
        Console.WriteLine($"── end of tick {to}");
        foreach (int c in cells) DumpCell(r, c);
        for (int k = 0; k < s.TouchCount; k++)
            if (s.TouchA[k] >= 0 && Watched(s.TouchA[k]) && Watched(s.TouchB[k]))
                Console.WriteLine($"   record {k}: ({s.TouchA[k]},{s.TouchB[k]}) bond {s.TouchBond[k]} span [{s.TouchT0[k]:F3},{s.TouchT1[k]:F3}] built [{s.TouchS0[k]:F3},{s.TouchS1[k]:F3}] open {s.TouchOpen[k]}");
    }

    /// <summary>Every LengthDisagreement over a run, with what kind of cells are involved.</summary>
    private static void LengthDisagreements(string[] args)
    {
        foreach (string scene in new[] { "glass", "projectile" })
        {
            var r = Scene(scene, SimTuning.Default);
            SimState s = r.State;
            int total = 0, v1cells = 0, shown = 0;
            for (int tick = 1; tick <= 200; tick++)
            {
                r.Solver.Step();
                var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
                SideAudit.Audit(s, rep, tick);
                foreach (string line in rep.All!)
                {
                    if (!line.StartsWith("LengthDisagreement")) continue;
                    total++;
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"bond (\d+) \((\d+),(\d+)\) is ([0-9.]+) long in A but ([0-9.]+)");
                    int k = int.Parse(m.Groups[1].Value), a = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
                    bool aV1 = CountRecords(s, a) < 2, bV1 = CountRecords(s, b) < 2;
                    if (aV1 || bV1) v1cells++;
                    int rec = -1; for (int q = 0; q < s.TouchCount; q++) if (s.TouchBond[q] == k) { rec = q; break; }
                    if (shown < 3) { shown++; Console.WriteLine($"── {scene} tick {tick}: {line}   [records: {a}→{CountRecords(s, a)}, {b}→{CountRecords(s, b)}; rec {rec} span {(rec >= 0 ? s.TouchLen(rec) : -1f):F3} open {(rec >= 0 ? s.TouchOpen[rec] : 0)}]"); }
                }
            }
            Console.WriteLine($"{scene}: {total} LengthDisagreement over 200 ticks; {v1cells} involve a cell on the v1 path (<2 records)");
        }
    }

    private static int CountRecords(SimState s, int c)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c], n = 0;
        for (int v = 0; v < len; v++)
        {
            short r = s.SideTouch[off + v];
            if (r < 0 || s.TouchA[r] < 0) continue;
            bool dup = false; for (int q = 0; q < v && !dup; q++) dup = s.SideTouch[off + q] == r;
            if (!dup) n++;
        }
        return n;
    }

    /// <summary>Grain 255, steel-steel, 2250 px/s, mass 5.5, toughness 1.7, crush thr x2, crush energy x0.0625.</summary>
    private static void FiveRepro(string[] args)
    {
        int[] cells = { 291, 292, 267, 268, 243 };
        int from = ArgInt(args, "--from", 7), to = ArgInt(args, "--to", 8);
        var tune = SimTuning.Default;
        tune.ToughnessScale = 1.7f;
        var steel = new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 1.0e6f * 2.0f, 0.3f * 0.0625f, 0.50f, 2.5f);
        var r = Scenarios.Projectile(tune, steel, 2250f, 5.5f, 255f, impactor: steel);
        SimState s = r.State;
        bool Watched(int c) => Array.IndexOf(cells, c) >= 0;

        int tk = 0;
        r.Solver.RetireSink = (rec, a, b, why) => { if (Watched(a) || Watched(b)) Console.WriteLine($"   tick {tk}: record {rec} ({a},{b}) retired: {why}"); };
        void Records()
        {
            for (int k = 0; k < s.TouchCount; k++)
                if (s.TouchA[k] >= 0 && Watched(s.TouchA[k]) && Watched(s.TouchB[k]))
                    Console.WriteLine($"   record {k}: ({s.TouchA[k]},{s.TouchB[k]}) bond {s.TouchBond[k]} span [{s.TouchT0[k]:F3},{s.TouchT1[k]:F3}] built [{s.TouchS0[k]:F3},{s.TouchS1[k]:F3}] open {s.TouchOpen[k]}");
        }
        int[] watchRecs = { 716, 717, 784, 786, 788, 854 };
        var lastOpen = new byte[s.TouchCount];
        for (int k = 0; k < s.TouchCount; k++) lastOpen[k] = s.TouchOpen[k];
        Console.WriteLine("── at build:"); Records();
        for (int tick = 0; tick <= to; tick++)
        {
            tk = tick;
            for (int k = 0; k < s.TouchCount; k++)
                if (Array.IndexOf(watchRecs, k) >= 0 && s.TouchOpen[k] != lastOpen[k])
                { Console.WriteLine($"   tick {tick} start: record {k} ({s.TouchA[k]},{s.TouchB[k]}) open {lastOpen[k]} -> {s.TouchOpen[k]}"); lastOpen[k] = s.TouchOpen[k]; }
            if (tick >= from)
            {
                Console.WriteLine($"── start of tick {tick}");
                foreach (int c in cells) DumpCell(r, c);
                Records();
                r.Solver.TraceCell = cells[0];
                r.Solver.TraceSink = m => { if (m.Contains("dent")) Console.WriteLine(m); };
            }
            r.Solver.Step();
            r.Solver.TraceCell = -1; r.Solver.TraceSink = null;
        }
        Console.WriteLine($"── end of tick {to}");
        foreach (int c in cells) DumpCell(r, c);
        Records();
        var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
        SideAudit.Audit(s, rep, to);
        foreach (string line in rep.All!) if (cells.Any(c => line.Contains($" {c} ") || line.Contains($"({c},") || line.Contains($",{c})"))) Console.WriteLine("   FAULT " + line);
    }

    private static void ZeroLengthSample(string[] args)
    {
        var r = Array.IndexOf(args, "--steelrock") >= 0
            ? Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 3f, 900f, impactor: Material.Steel)
            : Scenarios.Collide(new SimTuning { }, Material.Rock, 600f, 170f);
        if (Array.IndexOf(args, "--steelrock") < 0) { var t170 = SimTuning.Default; t170.ToughnessScale = 1.1f; t170.CarveContinuity = 0.75f; t170.CrushConfine = 0.10f; r = Scenarios.Collide(t170, Material.Rock, 600f, 170f); }
        SimState s = r.State;
        int shown = 0;
        for (int tick = 1; tick <= 200 && shown < 3; tick++)
        {
            r.Solver.Step();
            var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
            SideAudit.Audit(s, rep, tick);
            foreach (string line in rep.All!)
            {
                if (!line.StartsWith("ZeroLengthSurfaceSide")) continue;
                int c = int.Parse(System.Text.RegularExpressions.Regex.Match(line, @"cell (\d+)").Groups[1].Value);
                Console.WriteLine($"── tick {tick}: {line}   (records {CountRecords(s, c)}, body {s.CellBody[c]} has {s.BodyCellLen[s.CellBody[c]]} cells)");
                DumpCell(r, c);
                if (++shown >= 3) break;
            }
        }
    }

    private static void Cell36(string[] args)
    {
        var r = Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 3f, 900f, impactor: Material.Steel);
        SimState s = r.State; int c = 36;
        r.Solver.RetireSink = (rec, a, b, why) => { if (a == c || b == c) Console.WriteLine($"      record {rec} ({a},{b}) retired: {why}"); };
        for (int tick = 0; tick <= 21; tick++)
        {
            int off = s.PolyOff[c], len = s.PolyLen[c];
            var sb = new System.Text.StringBuilder($"tick {tick,2}: len {len} area {s.CellArea[c]:F1} sides:");
            for (int v = 0; v < len; v++)
            {
                int w = v + 1 == len ? 0 : v + 1;
                float l = SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v], s.PolyY[off + w] - s.PolyY[off + v]);
                short pb = s.PolyBond[off + v], rec = s.SideTouch[off + v];
                string lab = pb == SimState.SideReal ? "REAL" : pb == SimState.SideCrack ? "CRACK" : pb == SimState.SideSealed ? "SEALED" : $"b{pb}";
                sb.Append($" [{lab}/r{rec} {l:F2}]");
            }
            Console.WriteLine(sb.ToString());
            r.Solver.Step();
        }
    }

    private static void VertexKinds(string[] args)
    {
        var t170 = SimTuning.Default; t170.ToughnessScale = 1.1f; t170.CarveContinuity = 0.75f; t170.CrushConfine = 0.10f;
        var r = Scenarios.Collide(t170, Material.Rock, 600f, 170f);
        SimState s = r.State;
        foreach (int at in new[] { 0, 50, 100 })
        {
            while (s.Tick < at) r.Solver.Step();
            int[] n = new int[4]; int openEnds = 0;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) continue;
                for (int v = 0; v < s.PolyLen[c]; v++) n[(int)r.Solver.ClassifyVertex(c, v)]++;
            }
            for (int k = 0; k < s.TouchCount; k++)
                if (s.TouchA[k] >= 0) openEnds += (s.TouchOpen[k] & 1) + ((s.TouchOpen[k] >> 1) & 1);
            Console.WriteLine($"tick {at,3}: interior {n[0]}, exposed ends {n[1]} (records report {openEnds} open ends → ≤ {2 * openEnds} vertex marks), corners {n[2]}, fixed corners {n[3]}");
        }
    }

    /// <summary>Where the momentum goes in a collide: body speeds, live contacts, comminution routing.</summary>
    private static void MomentumFlow(string[] args)
    {
        bool tough = Array.IndexOf(args, "--toughglass") >= 0;
        foreach (var m in tough ? new[] { Material.Glass } : new[] { Material.Glass, Material.Rock })
        {
            var tn = SimTuning.Default; if (tough) { tn.StrainScale = 3f; }
            if (Array.IndexOf(args, "--crackpush") >= 0) tn.CrackPush = true;
            var r = Scenarios.Collide(tn, m, 600f, 170f);
            SimState s = r.State;
            float v0 = SimMath.Hypot(s.BodyVx[0], s.BodyVy[0]);
            float m0 = s.BodyM[0];
            Console.WriteLine($"── {m.Name}: body 0 starts at {v0:F0} px/s, mass {m0:F0}");
            Console.WriteLine("   tick  body0 v   mass%   bodies  contacts  dead  dust→nbrs  dust→contacts  dust→ledger  ledger%   |Dv| front / back   stretched bonds front / back");
            float ux0 = s.BodyVx[0] / SimMath.Max(1e-3f, v0), uy0 = s.BodyVy[0] / SimMath.Max(1e-3f, v0);
            int lastDead = 0;
            for (int tick = 1; tick <= 60; tick++)
            {
                r.Solver.Step();
                if (tick % 5 != 0) continue;
                int dead = 0; for (int c = 0; c < s.CellCount; c++) if (s.Dead(c)) dead++;
                // Stress reaching the back: deviation speed and stretched bonds, front half vs back half of body 0
                // along its own direction of travel (front = ahead of the body centre).
                float dvF = 0f, dvB = 0f; int nF = 0, nB = 0, sbF = 0, sbB = 0;
                SimMath.SinCos(s.BodyRot[0], out float sn, out float cs);
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c) || s.CellBody[c] != 0) continue;
                    float wx = s.CellRx[c] * cs - s.CellRy[c] * sn, wy = s.CellRx[c] * sn + s.CellRy[c] * cs;
                    bool front = wx * ux0 + wy * uy0 > 0f;
                    float dv = SimMath.Hypot(s.CellDvx[c], s.CellDvy[c]);
                    if (front) { dvF += dv; nF++; } else { dvB += dv; nB++; }
                }
                for (int k = 0; k < s.BondCount; k++)
                {
                    if (s.BondBroken[k] || s.CellBody[s.BondA[k]] != 0) continue;
                    if (SimMath.Abs(s.BondSn[k]) < 0.25f * s.BondS0[k] * SimMath.Max(1f, s.BondLen[k])) continue;   // carrying real load
                    int c = s.BondA[k];
                    float wx = s.CellRx[c] * cs - s.CellRy[c] * sn, wy = s.CellRx[c] * sn + s.CellRy[c] * cs;
                    if (wx * ux0 + wy * uy0 > 0f) sbF++; else sbB++;
                }
                Console.WriteLine($"   {tick,4}  {SimMath.Hypot(s.BodyVx[0], s.BodyVy[0]),7:F0}  {100f * s.BodyM[0] / m0,5:F0}%  {s.BodyCount,6}  {r.Solver.ContactCount,8}  {dead,4}  {r.Solver.DustToNeighbours,9}  {r.Solver.DustToContacts,13}  {r.Solver.DustNoNeighbour,11}  {LedgerPct(r.Solver),6:F1}%   {(nF > 0 ? dvF / nF : 0f),6:F1} / {(nB > 0 ? dvB / nB : 0f),-6:F1}   {sbF,5} / {sbB}");
                lastDead = dead;
            }
            Console.WriteLine($"   comminution momentum: to neighbours (impact share) {r.Solver.DustDevMom:F0}, left with mass (rigid share) {r.Solver.DustRigidMom:F0}, "
                            + $"into contact partners {r.Solver.DustContactMom:F0}, exported (pressing on nothing) {r.Solver.DustLostMom:F0}; body 0 initial momentum {m0 * v0:F0}");
        }
    }

    private static string RunSummary(Scenarios.Result r, int ticks)
    {
        SimState s = r.State;
        float peakOv = 0f;
        for (int i = 0; i < ticks; i++) { r.Solver.Step(); if (r.Solver.MaxOverlap > peakOv) peakOv = r.Solver.MaxOverlap; }
        int broken = 0; for (int k = 0; k < s.BondCount; k++) if (s.BondBroken[k]) broken++;
        int dead = 0, live = 0; float shed = 0f, a0 = 0f;
        for (int c = 0; c < s.CellCount; c++)
        {
            a0 += s.CellArea0[c];
            if (s.Dead(c)) { dead++; shed += s.CellArea0[c]; } else { live++; shed += s.CellArea0[c] - s.CellArea[c]; }
        }
        // fastest body that is not one of the two biggest — a fragment's ejection speed
        int big1 = -1, big2 = -1;
        for (int b = 0; b < s.BodyCount; b++)
        {
            if (big1 < 0 || s.BodyM[b] > s.BodyM[big1]) { big2 = big1; big1 = b; }
            else if (big2 < 0 || s.BodyM[b] > s.BodyM[big2]) big2 = b;
        }
        float vmax = 0f;
        for (int b = 0; b < s.BodyCount; b++)
            if (b != big1 && b != big2) vmax = SimMath.Max(vmax, SimMath.Hypot(s.BodyVx[b], s.BodyVy[b]));
        return $"bodies {s.BodyCount,3}  broken {broken,4}  dead cells {dead,3} ({100f * dead / (dead + live),4:F1}%)  "
             + $"shed {100f * shed / a0,5:F1}%  peak overlap {peakOv,5:F1}px  fastest fragment {vmax,6:F0} px/s  ledger {LedgerPct(r.Solver),5:F1}%";
    }

    private static void BiasSweepV2(string[] args)
    {
        foreach (float bias in new[] { 0.2f, 0.1f, 0.05f, 0.02f })
        foreach (string scene in new[] { "collide g170 600", "projectile g900 900" })
        {
            var t = SimTuning.Default; t.ContactBias = bias;
            var r = scene.StartsWith("collide") ? Scenarios.Collide(t, Material.Rock, 600f, 170f)
                                                : Scenarios.Projectile(t, Material.Rock, 900f, 3f, 900f);
            Console.WriteLine($"bias {bias:F2}  {scene,-20}: {RunSummary(r, 200)}");
        }
    }

    private static void MaterialCensus(string[] args)
    {
        if (Array.IndexOf(args, "--glass") >= 0)
        {
            // What the v1→v2 change did to the effective rate, and what restores glass's intent.
            foreach (var (label, m) in new (string, Material)[]
            {
                ("glass as is (rate 2.0, crush 4e5)",     Material.Glass),
                ("rate x0.4 (v1 effective)",              new Material("glass", 2500f, 5500f, 0.008f, 1.05f, 9f, 0f, 4.0e5f, 0.8f, 0.15f, 0.8f)),
                ("rate x0.4, crush x2",                   new Material("glass", 2500f, 5500f, 0.008f, 1.05f, 9f, 0f, 8.0e5f, 0.8f, 0.15f, 0.8f)),
                ("rate x0.4, crush x3",                   new Material("glass", 2500f, 5500f, 0.008f, 1.05f, 9f, 0f, 1.2e6f, 0.8f, 0.15f, 0.8f)),
                ("rock, rate x0.4 (v1 effective)",        new Material("rock", 3000f, 5000f, 0.010f, 90f, 0.95f, 0.02f, 2.5e5f, 0.4f, 0.35f, 1.5f)),
            })
            {
                var r = Scenarios.Collide(SimTuning.Default, m, 600f, 170f);
                Console.WriteLine($"{label,-34}: {RunSummary(r, 200)}");
            }
            return;
        }
        foreach (var m in new[] { Material.Rock, Material.Ice, Material.Sandstone, Material.Glass, Material.Steel })
        {
            var r = Scenarios.Collide(SimTuning.Default, m, 600f, 170f);
            Console.WriteLine($"{m.Name,-9} crush {m.Crush:E1} rate {m.CrushRate:F2} shed {m.ShedLimit:F2} dent {m.Dent:F1}: {RunSummary(r, 200)}");
        }
    }

    private static void RecordCensus()
    {
        foreach (string scene in new[] { "collide", "projectile", "glass", "five" })
        {
            var r = scene == "five"
                ? Scenarios.Projectile(SimTuning.Default, new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 2.0e6f, 0.01875f, 0.50f, 2.5f), 2250f, 5.5f, 255f,
                                       impactor: new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 2.0e6f, 0.01875f, 0.50f, 2.5f))
                : Scene(scene, SimTuning.Default);
            SimState s = r.State;
            int n0 = 0, n1 = 0, n2 = 0, bonded = 0, sealedN = 0, outlineVerts = 0;
            for (int k = 0; k < s.TouchCount; k++)
            {
                int o = (s.TouchOpen[k] & 1) + ((s.TouchOpen[k] >> 1) & 1);
                if (o == 0) n0++; else if (o == 1) n1++; else n2++;
                if (s.TouchBond[k] >= 0) bonded++; else sealedN++;
            }
            // outline vertices where a free side meets a non-free side, counted per cell
            for (int c = 0; c < s.CellCount; c++)
            {
                int off = s.PolyOff[c], len = s.PolyLen[c];
                for (int v = 0; v < len; v++)
                {
                    int pv = v == 0 ? len - 1 : v - 1;
                    bool f = s.PolyBond[off + v] == SimState.SideReal, fp = s.PolyBond[off + pv] == SimState.SideReal;
                    if (f != fp) outlineVerts++;
                }
            }
            int sliv = 0, slivLinked = 0; float shortest = float.MaxValue;
            for (int c = 0; c < s.CellCount; c++)
            {
                int off = s.PolyOff[c], len = s.PolyLen[c];
                for (int v = 0; v < len; v++)
                {
                    int w = v + 1 == len ? 0 : v + 1;
                    float l = SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v], s.PolyY[off + w] - s.PolyY[off + v]);
                    if (l < shortest) shortest = l;
                    if (l < 0.25f) { sliv++; if (s.SideTouch[off + v] >= 0) slivLinked++; }
                }
            }
            Console.WriteLine($"{scene,-11}: sides under 0.25px at build: {sliv} ({slivLinked} carrying a record); shortest side {shortest:F4}px");
            int realWithRec = 0, bondNoRec = 0, sealedNoRec = 0, wrongBond = 0;
            for (int c = 0; c < s.CellCount; c++)
            {
                int off = s.PolyOff[c], len = s.PolyLen[c];
                for (int v = 0; v < len; v++)
                {
                    short pb = s.PolyBond[off + v], rec = s.SideTouch[off + v];
                    bool hasRec = rec >= 0 && s.TouchA[rec] >= 0;
                    if (pb == SimState.SideReal && hasRec) realWithRec++;
                    else if (pb >= 0 && !hasRec) bondNoRec++;
                    else if (pb == SimState.SideSealed && !hasRec) sealedNoRec++;
                    else if (pb >= 0 && hasRec && s.TouchBond[rec] != pb) wrongBond++;
                }
            }
            Console.WriteLine($"{scene,-11}: label/record contradictions at build: REAL-with-record {realWithRec}, bond-without-record {bondNoRec}, sealed-without-record {sealedNoRec}, record names a different bond {wrongBond}");
            Console.WriteLine($"{scene,-11}: {s.TouchCount} records ({bonded} bonded, {sealedN} sealed); "
                + $"open ends: none {n0}, one {n1}, both {n2}; free/non-free vertex transitions per cell {outlineVerts} "
                + $"(≈ 2 per exposed record end → {2 * (n1 + 2 * n2)})");
        }
    }

    private static void LoopRepro(string[] args)
    {
        int watch = ArgInt(args, "--cell", 245), other = ArgInt(args, "--other", 224);
        int from = ArgInt(args, "--from", 42), to = ArgInt(args, "--to", 42);

        var tune = SimTuning.Default;
        tune.ToughnessScale = 1.1f;
        tune.CarveContinuity = 0.75f;
        tune.CrushConfine = 0.10f;
        var r = Scenarios.Collide(tune, Material.Rock, 600f, 170f);
        SimState s = r.State;

        int third = ArgInt(args, "--third", 244);
        int tk = 0;
        r.Solver.RetireSink = (rec, a, b, why) =>
        {
            if (a == watch || b == watch || a == third || b == third)
                Console.WriteLine($"   tick {tk}: record {rec} ({a},{b}) retired: {why}");
        };
        Console.WriteLine("── build-time records of the watched cell:");
        for (int k = 0; k < s.TouchCount; k++)
            if (s.TouchA[k] == watch || s.TouchB[k] == watch)
                Console.WriteLine($"   record {k}: ({s.TouchA[k]},{s.TouchB[k]}) bond {s.TouchBond[k]}");

        for (int tick = 0; tick <= to; tick++)
        {
            tk = tick;
            bool on = tick >= from;
            if (on)
            {
                Console.WriteLine($"── start of tick {tick}");
                DumpCell(r, watch);
                DumpCell(r, third);
                r.Solver.TraceCell = watch;
                r.Solver.TraceSink = msg => Console.WriteLine(msg);
            }
            r.Solver.Step();
            if (on)
            {
                r.Solver.TraceCell = -1; r.Solver.TraceSink = null;
                Console.WriteLine($"── end of tick {tick}");
                DumpCell(r, watch);
                DumpCell(r, other);
            }
        }
    }

    private static void SteelRepro(string[] args)
    {
        int ca = ArgInt(args, "--a", 218), cb = ArgInt(args, "--b", 248);
        int from = ArgInt(args, "--from", 5), to = ArgInt(args, "--to", 6);

        var tune = SimTuning.Default;
        tune.StrainScale = 2.0f;
        tune.ToughnessScale = 1.8f;
        tune.CarveMinArea = 0.005f;
        tune.CrushConfine = 0.04f;                 // the user's value: 0.04, not 0.4
        var r = Scenarios.Projectile(tune, Material.Steel, 4000f, 8f, 180f, impactor: Material.Steel);
        SimState s = r.State;

        for (int tick = 0; tick <= to; tick++)
        {
            if (tick >= from)
            {
                Console.WriteLine($"── tick {tick}");
                foreach (int c in new[] { ca, cb }) DumpCell(r, c);
                for (int k = 0; k < s.TouchCount; k++)
                {
                    int a = s.TouchA[k], b = s.TouchB[k];
                    if (a != ca && a != cb && b != ca && b != cb) continue;
                    Console.WriteLine($"   record {k}: A={a} B={b} bond={s.TouchBond[k]} "
                        + $"span [{s.TouchT0[k]:F3},{s.TouchT1[k]:F3}]");
                }
                var rep = new SideAudit.Report { All = new System.Collections.Generic.List<string>() };
                SideAudit.Audit(s, rep, tick);
                foreach (string line in rep.All!)
                    if (line.Contains($" {ca} ") || line.Contains($" {cb} ")
                        || line.Contains($"({ca},") || line.Contains($"({cb},")
                        || line.Contains($",{ca})") || line.Contains($",{cb})"))
                        Console.WriteLine($"   FAULT {line}");
                Console.WriteLine();
            }
            r.Solver.Step();
        }
    }

    private static void DumpCell(Scenarios.Result r, int c)
    {
        SimState s = r.State;
        int off = s.PolyOff[c], len = s.PolyLen[c];
        Console.WriteLine($"   cell {c}: body {s.CellBody[c]} dead {s.Dead(c)} len {len} "
            + $"area {s.CellArea[c]:F1}/{s.CellArea0[c]:F1}");
        for (int v = 0; v < len; v++)
        {
            var kind = r.Solver.ClassifySide(c, v, out float f0, out float f1);
            short k = s.PolyBond[off + v], rec = s.SideTouch[off + v];
            string bond = k == SimState.SideReal ? "REAL" : k == SimState.SideCrack ? "CRACK"
                        : k == SimState.SideSealed ? "SEALED" : $"bond{k}";
            int w = v + 1 == len ? 0 : v + 1;
            float dx = s.PolyX[off + w] - s.PolyX[off + v], dy = s.PolyY[off + w] - s.PolyY[off + v];
            World(s, c, off + v, out float x0, out float y0);
            World(s, c, off + w, out float x1, out float y1);
            Console.WriteLine($"      side {v}: {bond,-7} rec {rec,4} {kind,-11} "
                + $"cover [{f0:F3},{f1:F3}] len {SimMath.Hypot(dx, dy):F2}  "
                + $"({x0,7:F2},{y0,7:F2})->({x1,7:F2},{y1,7:F2})");
        }
    }

    private static void World(SimState s, int c, int i, out float wx, out float wy)
    {
        int b = s.CellBody[c];
        float co = SimMath.Cos(s.BodyRot[b]), si = SimMath.Sin(s.BodyRot[b]);
        float lx = s.CellRx[c] + s.PolyX[i], ly = s.CellRy[c] + s.PolyY[i];
        wx = s.BodyX[b] + lx * co - ly * si;
        wy = s.BodyY[b] + lx * si + ly * co;
    }

    private static float ProbeDist = 0.03f;

    private static Material Retune(in Material m, float crush, float energy)
        => new(m.Name, m.Rho, m.C, m.Strain, m.Chi, m.Yield, m.Duct, crush, energy, m.ShedLimit);

    /// <summary>
    /// Calibrates the comminution pair against BEHAVIOUR in four regimes at once, rather than
    /// against a peak-stress number.
    /// </summary>
    /// <remarks>
    /// <para>A threshold has to satisfy two opposing constraints, and neither can be read off a
    /// single scenario. Too low and a pile erodes while nothing is happening to it — the failure the
    /// deleted <c>CrushFloor</c> existed to patch. Too high and nothing ever comminutes, which is
    /// where this started. The window between them is what this prints.</para>
    ///
    /// <para><b>rest</b> must be 0: cells lost by a field that starts in light contact and is never
    /// driven. <b>drift</b>, <b>press</b> and <b>proj</b> are the phenomena that should happen —
    /// gentle collision, sustained slow squeeze, and impact. A usable row has rest at 0 and press
    /// above 0; a material whose rows never manage both needs its capacity moved, not its
    /// threshold, because capacity is what filters brief transients out of a sustained load.</para>
    /// </remarks>
    private static void CrushSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        var t = SimTuning.Default;
        Console.WriteLine("comminution calibration: threshold vs behaviour in four regimes");
        Console.WriteLine("  rest  = 5x5 field starting in light contact, never driven — MUST be 0");
        Console.WriteLine("  drift = same field closing at 20 px/s (collision; some crushing is fine)");
        Console.WriteLine("  press = worst-cell dose after a 400-tick 150 px/s squeeze, over capacity");
        Console.WriteLine("  proj  = cells crushed by a 900 px/s projectile");
        Console.WriteLine();

        foreach (var baseM in new[] { Material.Ice, Material.Sandstone, Material.Rock,
                                      Material.Glass, Material.Steel })
        {
            Console.WriteLine($"── {baseM.Name}   (authored thr {baseM.Crush.ToString("E1", ci)}, "
                            + $"capacity {baseM.CrushRate.ToString("E1", ci)}) ──");
            Console.WriteLine("     threshold    rest   drift    press/cap    proj");
            foreach (float mul in new[] { 0.25f, 0.5f, 1f, 2f, 4f, 8f })
            {
                var m = Retune(baseM, baseM.Crush * mul, baseM.CrushRate);

                var rest = Scenarios.Field(t, m, 5, 5, 60f, 125f, 0f);
                for (int i = 0; i < 600; i++) rest.Solver.Step();

                var drift = Scenarios.Field(t, m, 5, 5, 60f, 150f, 20f);
                for (int i = 0; i < 600; i++) drift.Solver.Step();

                var press = Scenarios.Collide(t, m, speed: 150f);
                for (int i = 0; i < 400; i++) press.Solver.Step();
                float dose = MaxShed(press.State);

                var proj = Scenarios.Projectile(t, m, 900f, 3f);
                for (int i = 0; i < 200; i++) proj.Solver.Step();

                Console.WriteLine($"   {(m.Crush).ToString("E2", ci),11} {rest.Solver.Crushed,7} "
                    + $"{drift.Solver.Crushed,7} {dose.ToString("F2", ci),12} "
                    + $"{proj.Solver.Crushed,7}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Contact stress across the full scenario span, to decide whether a single crush threshold can
    /// serve all of them.
    /// </summary>
    /// <remarks>
    /// The question this answers: comminution used to key on penetration DEPTH, which is geometry
    /// and says nothing about how hard something is being pressed — so no single value could cover a
    /// slow press and a hypervelocity impact, and it behaved as all-or-nothing. Stress is a material
    /// property and should be comparable across scenarios. Whether it actually is, is a measurement.
    ///
    /// <para>Two columns matter. <b>Peak stress</b> is the instantaneous maximum. <b>Dose</b> is
    /// max(0, stress - threshold) integrated over time on the worst cell — what a rate law would
    /// accumulate, and the measure that responds to participating mass: a lone cell is pushed away
    /// within about a substep, a supported one keeps being pressed.</para>
    /// </remarks>
    private static void StressStudy(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        int ticks = ArgInt(args, "--ticks", 200);

        var scenes = new (string Name, string Kind, Func<SimTuning, Scenarios.Result> Make)[]
        {
            ("settled field",   "quiet", t => Scenarios.Field(t, Material.Rock, 8, 6, 60f, 150f, 10f)),
            ("touching field",  "quiet", t => Scenarios.Field(t, Material.Rock, 8, 6, 60f, 128f, 10f)),
            ("slow press 60",   "quiet", t => Scenarios.Collide(t, Material.Rock, speed: 60f)),
            ("collide 600",     "mid",   t => Scenarios.Collide(t, Material.Rock, speed: 600f)),
            ("projectile 900",  "mid",   t => Scenarios.Projectile(t, Material.Rock, 900f, 3f)),
            ("projectile 1500", "crush", t => Scenarios.Projectile(t, Material.Rock, 1500f, 8f)),
            ("glass 1500",      "crush", t => Scenarios.Projectile(t, Material.Glass, 1500f, 8f)),
            ("projectile 3000", "crush", t => Scenarios.Projectile(t, Material.Rock, 3000f, 12f)),
        };

        Console.WriteLine("contact stress across the scenario span   (stress = Ln / (h * contactLength))");
        Console.WriteLine();
        Console.WriteLine("  scenario           want    peak stress    p99      p90      p50");
        var peaks = new double[scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
        {
            var t = SimTuning.Default;
            var r = scenes[i].Make(t);
            r.Solver.MeasureStress = true;
            r.Solver.CrushDose = new float[r.State.CellCount];
            for (int k = 0; k < ticks; k++) r.Solver.Step();

            long total = 0;
            foreach (long v in r.Solver.StressHist) total += v;
            peaks[i] = r.Solver.PeakStress;
            Console.WriteLine($"  {scenes[i].Name,-18} {scenes[i].Kind,-6} "
                + $"{r.Solver.PeakStress.ToString("F0", ci),11}"
                + $"{Pct(r.Solver.StressHist, total, 0.99).ToString("F0", ci),9}"
                + $"{Pct(r.Solver.StressHist, total, 0.90).ToString("F0", ci),9}"
                + $"{Pct(r.Solver.StressHist, total, 0.50).ToString("F0", ci),9}");
        }

        Console.WriteLine();
        Console.WriteLine("  worst-cell crush dose (stress-seconds above threshold) — the rate-law integral");
        Console.Write($"  {"scenario",-18} {"want",-6}");
        float[] thresholds = { 7e3f, 1e4f, 1.5e4f, 2e4f, 2.5e4f, 3e4f };
        foreach (float th in thresholds) Console.Write($"{("T=" + th.ToString("0.0e0", ci)),14}");
        Console.WriteLine();

        for (int i = 0; i < scenes.Length; i++)
        {
            Console.Write($"  {scenes[i].Name,-18} {scenes[i].Kind,-6}");
            foreach (float th in thresholds)
            {
                var t = SimTuning.Default;
                var r = scenes[i].Make(t);
                r.Solver.MeasureStress = true;
                r.Solver.CrushThreshold = th;
                r.Solver.CrushDose = new float[r.State.CellCount];
                for (int k = 0; k < ticks; k++) r.Solver.Step();
                // Count dead cells too: a cell that accumulated dose and was then destroyed by the
                // existing dust path is precisely the one this criterion is meant to select, and
                // skipping it would hide the very signal being looked for.
                float worst = 0f;
                int hit = 0, n = r.State.CellCount;
                for (int c = 0; c < n; c++)
                {
                    if (r.Solver.CrushDose[c] > 0f) hit++;
                    if (r.Solver.CrushDose[c] > worst) worst = r.Solver.CrushDose[c];
                }
                // "dose (percent of cells touched)" — an all-or-nothing criterion shows up here as
                // a jump straight from 0% to ~100%, never as a graded zone.
                Console.Write($"{worst.ToString("0.0e0", ci) + "/" + (n == 0 ? 0 : 100 * hit / n) + "%",14}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>Percentile from the log2 histogram, returned as the bucket's lower bound.</summary>
    private static double Pct(long[] hist, long total, double q)
    {
        if (total == 0) return 0;
        long want = (long)(total * q), seen = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            seen += hist[i];
            if (seen >= want) return System.Math.Pow(2, i);
        }
        return System.Math.Pow(2, hist.Length);
    }

    /// <summary>
    /// How hard the contact positional bias has to push now that deformation is not absorbing
    /// overlap, and what that costs in energy.
    /// </summary>
    private static void BiasSweep(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine("contact bias sweep — the only thing resolving overlap now");
        Console.WriteLine();
        Console.WriteLine("  bias    scene          bodies  broken   peak ov   peak KE   mom drift");
        foreach (float bias in new[] { 0.05f, 0.1f, 0.2f, 0.4f, 0.8f })
        {
            foreach (string scene in new[] { "collide", "projectile", "glass" })
            {
                var t = SimTuning.Default;
                t.ContactBias = bias;
                var r = Scene(scene, t);
                var m = SceneRunner.Run(r, 400);
                Console.WriteLine($"  {bias.ToString("F2", ci),-7} {scene,-12} {m.Bodies,7} {m.Broken,7} "
                    + $"{m.PeakOverlap.ToString("F1", ci),9} "
                    + $"{(100 * m.PeakEnergyFraction).ToString("F1", ci),9} "
                    + $"{(100 * m.MomentumDrift).ToString("F3", ci),11}");
            }
        }
    }

    /// <summary>
    /// The achievable parallel speedup on this machine, with no pool in the way.
    /// </summary>
    /// <remarks>
    /// <para>Raw threads over a <b>shared atomic index claimed in fixed-size batches</b>. That is the
    /// minimum synchronisation uneven work can be run with, so it is the ceiling everything else is
    /// quoted against.</para>
    ///
    /// <para><b>This is deliberately not what <c>SimJobs</c> does.</b> <c>SimJobs</c> cuts the work
    /// into a fixed 32 chunks, so a chunk is N/32 items and granularity <i>scales with N</i>: at
    /// 65,536 items that is 2,048 per claim and 32 claims across a dozen threads, under three each,
    /// so one slow chunk sets the critical path. Fixed-batch claiming gives N/batch claims regardless
    /// of N. The current scheme therefore gets more unbalanced as scenes grow, which is backwards,
    /// and the batch sweep below is what decides whether to change it.</para>
    ///
    /// <para>Sized to the real target: 65,536 items is about the bond count of a 20,000-cell scene
    /// (a power of two so the wrap is a mask rather than a division). Two access patterns, because
    /// the passes this exists to inform are memory-bound rather than compute-bound — bond forces is
    /// ~19 ns for roughly thirty flops, which is a gather stalling, not arithmetic.</para>
    /// </remarks>
    private static void ParallelBaseline(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        const int N = 65536;                       // ~bonds in a 20k-cell scene
        const int Mask = N - 1;

        int[] flopSet = { 1, 2, 4, 8, 16, 32, 64, 128 };
        int[] batchSet = { 1, 8, 32, 128, 512, 2048 };
        int[] threadSet = ThreadSet(Environment.ProcessorCount);
        int reportBatch = ArgInt(args, "--batch", 128);

        var src = new float[N];
        var dst = new float[N];
        var idx = new int[N];

        // Per-item cost multiplier. Uniform work cannot show the imbalance this exists to measure,
        // and the real distribution is not uniform OR random: a body's cells are CONTIGUOUS in the
        // index tables, so one large body is a solid block of expensive items. Here the first tenth
        // of the range costs 9x and the rest 1x, so half the work sits in a tenth of the indices —
        // about the shape of a projectile scene, where one body holds ~85% of the cells.
        var weight = new int[N];
        for (int i = 0; i < N; i++) weight[i] = i < N / 10 ? 9 : 1;
        for (int i = 0; i < N; i++) { src[i] = 1f + i * 1e-6f; idx[i] = i; }
        uint seed = 20260908;
        for (int i = N - 1; i > 0; i--)            // reproducible shuffle; a plain LCG is enough here
        {
            seed = seed * 1664525u + 1013904223u;
            int j = (int)(seed % (uint)(i + 1));
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }

        Console.WriteLine("parallel baseline — raw threads, shared atomic index, fixed-size batches");
        Console.WriteLine($"  {N} items, {Environment.ProcessorCount} hardware threads");
        Console.WriteLine("  NOT SimJobs: this claims a fixed batch, SimJobs claims 1/32 of the work");
        Console.WriteLine();

        foreach (bool gather in new[] { false, true })
        {
            Console.WriteLine($"── {(gather ? "gather (shuffled index — the BondA/BondB pattern)" : "sequential")}"
                            + $", batch {reportBatch} ──");
            Console.Write("  flops ");
            foreach (int t in threadSet) Console.Write($"{$"t={t}",9}");
            Console.WriteLine("      ns/item");

            foreach (int flops in flopSet)
            {
                double one = 0;
                Console.Write($"  {flops,5} ");
                foreach (int t in threadSet)
                {
                    double ns = Measure(src, dst, idx, null, gather, flops, t, reportBatch, N, Mask);
                    if (t == threadSet[0]) one = ns;
                    Console.Write($"{(one / ns).ToString("F2", ci) + "x",9}");
                }
                Console.WriteLine($"{one.ToString("F2", ci),13}");
            }
            Console.WriteLine();
        }

        int tMax = threadSet[threadSet.Length - 1];
        Console.WriteLine($"── batch sensitivity at {tMax} threads (speedup vs 1 thread, same batch) ──");
        Console.Write("  flops ");
        foreach (int b in batchSet) Console.Write($"{$"b={b}",9}");
        Console.WriteLine();
        foreach (var (label, w) in new[] { ("uniform", (int[]?)null), ("skewed (one big body)", weight) })
        {
            Console.WriteLine($"  {label}:");
            foreach (int flops in new[] { 4, 16, 64 })
            {
                Console.Write($"  {flops,5} ");
                foreach (int b in batchSet)
                {
                    double one = Measure(src, dst, idx, w, false, flops, 1, b, N, Mask);
                    double many = Measure(src, dst, idx, w, false, flops, tMax, b, N, Mask);
                    Console.Write($"{(one / many).ToString("F2", ci) + "x",9}");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  SimJobs equivalent: 32 chunks over {N} items = {N / 32} per claim");
        GC.KeepAlive(dst);
    }

    private static int[] ThreadSet(int cores)
    {
        var list = new System.Collections.Generic.List<int>();
        for (int t = 1; t <= cores; t = t < 4 ? t + 1 : t + 2) list.Add(t);
        if (list[list.Count - 1] != cores) list.Add(cores);
        return list.ToArray();
    }

    /// <summary>
    /// One timed run, returning <b>nanoseconds per item</b>. Threads are created and parked first, so
    /// the clock covers the work and the claiming, never thread creation.
    /// </summary>
    private static double Measure(float[] src, float[] dst, int[] idx, int[]? weight, bool gather,
        int flops, int threads, int batch, int n, int mask, int reps = 3)
    {
        // Median of a few runs: this machine throttles hard under all-core load and a single sample
        // is worth little.
        var runs = new double[reps];
        for (int r = 0; r < reps; r++)
            runs[r] = MeasureOnce(src, dst, idx, weight, gather, flops, threads, batch, n, mask);
        System.Array.Sort(runs);
        return runs[reps / 2];
    }

    private static double MeasureOnce(float[] src, float[] dst, int[] idx, int[]? weight, bool gather,
        int flops, int threads, int batch, int n, int mask)
    {
        // Enough passes over the array to make a run tens of milliseconds. A dependent FMA chain
        // costs roughly 1.3 ns per flop on this class of core, so size from that and round to whole
        // passes; otherwise a one-flop run finishes before the threads have all started.
        double nsPerItem = 0.5 + flops * 1.3;
        long passes = System.Math.Clamp((long)(40e6 / (nsPerItem * n)), 1, 2000);
        long total = passes * n;

        long index = 0;
        using var go = new System.Threading.ManualResetEventSlim(false);
        var workers = new System.Threading.Thread[threads - 1];

        for (int t = 0; t < threads - 1; t++)
        {
            var th = new System.Threading.Thread(() => { go.Wait(); Claim(); }, 256 * 1024)
            { IsBackground = true };
            workers[t] = th;
            th.Start();
        }
        System.Threading.Thread.Sleep(1);          // let the workers reach the gate

        var sw = Stopwatch.StartNew();
        go.Set();
        Claim();
        foreach (var w in workers) w.Join();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1e6 / total;

        void Claim()
        {
            while (true)
            {
                long start = System.Threading.Interlocked.Add(ref index, batch) - batch;
                if (start >= total) return;
                long end = System.Math.Min(start + batch, total);
                for (long i = start; i < end; i++)
                {
                    int j = (int)(i & mask);
                    float v = src[gather ? idx[j] : j];
                    int work = weight == null ? flops : flops * weight[j];
                    // A dependent chain, which is what the solver's arithmetic actually looks like.
                    for (int f = 0; f < work; f++) v = v * 1.0000001f + 0.5f;
                    dst[j] = v;
                }
            }
        }
    }

    /// <summary>
    /// Runs a fixed battery of scenarios and prints the hash of the state each one ends in.
    /// </summary>
    /// <remarks>
    /// <para>The cross-platform determinism gate for the simulation itself. The existing
    /// <c>MathFingerprint</c> gate covers <c>SimMath</c>, which is the layer where a stray
    /// <c>MathF.Sin</c> would show up; this covers everything built on top of it — the solve order,
    /// the topology renumbering, the accumulation order inside every reduction. A build that agrees
    /// on the maths and disagrees here has a bug in the solver, not in the library.</para>
    /// <para>It is also the gate for the parallel work that has not landed yet: running with one
    /// chunk and with N chunks must print the same line, or the decomposition is not deterministic.
    /// Scenario list and tick counts are part of the contract — changing either changes every hash,
    /// so change them only deliberately.</para>
    /// </remarks>
    private static void Fingerprint()
    {
        var scenes = new[] { "collide", "projectile", "spin", "glass", "steel" };
        var combined = Fnv128.Create();

        foreach (string scene in scenes)
        {
            var r = WithJobs(Scene(scene, SimTuning.Default));
            for (int i = 0; i < 400; i++) r.Solver.Step();
            string hex = SimFingerprint.Hex(r.State);
            Console.Error.WriteLine($"{scene,-12} {hex}   (without records: {SimFingerprint.HexWithoutRecords(r.State)})");
            foreach (char ch in hex) combined.Add(ch);
        }

        Console.WriteLine(combined.ToHex());
    }

    /// <summary>
    /// How far apart two runs land when the input is perturbed by an amount nobody would call a
    /// behaviour change.
    /// </summary>
    /// <remarks>
    /// This calibrates every other comparison in the programme. Fracture is chaotic: a bond that
    /// breaks one substep earlier redirects a crack, and the outcome diverges from there. Without
    /// knowing the spread a 0.001% change of impact speed already produces, there is no way to read
    /// a metric shift after an optimisation — it could be a regression, or it could be the same
    /// physics landing in a different one of its natural outcomes.
    /// </remarks>
    private static void Sensitivity(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        int ticks = ArgInt(args, "--ticks", 400);

        Console.WriteLine("FractureBench — sensitivity of the outcome to a negligible input change");
        Console.WriteLine("  every row rebuilds the manifold every substep (drift 0), so the ONLY");
        Console.WriteLine("  difference between rows is the impact speed in the last decimal place");
        Console.WriteLine();
        Console.WriteLine("  scene        speed        bodies  broken  dust   big%   conn%   neck%   ov");

        foreach (string scene in new[] { "collide", "projectile", "glass", "steel" })
        {
            foreach (float mul in new[] { 1f, 1.000001f, 1.00001f, 1.0001f, 1.001f })
            {
                var t = SimTuning.Default;
                t.ManifoldDrift = 0f;
                var r = ScenePerturbed(scene, t, mul);
                var m = SceneRunner.Run(r, ticks);
                Console.WriteLine(
                    $"  {scene,-11} {(mul * 100f).ToString("F4", ci),9}%  "
                  + $"{m.Bodies,7} {m.Broken,7} {m.Dust,5} {m.BigMassPct.ToString("F0", ci),6} "
                  + $"{m.CrackConnectivity.ToString("F0", ci),7} "
                  + $"{m.PeakOverlap.ToString("F1", ci),5}");
            }
            Console.WriteLine();
        }
    }

    private static Scenarios.Result ScenePerturbed(string name, SimTuning t, float mul) => name switch
    {
        "collide" => Scenarios.Collide(t, Material.Rock, speed: 600f * mul),
        "projectile" => Scenarios.Projectile(t, Material.Rock, speed: 900f * mul),
        "steel" => Scenarios.Projectile(t, Material.Steel, speed: 1500f * mul, massMul: 8f),
        "glass" => Scenarios.Projectile(t, Material.Glass, speed: 1500f * mul, massMul: 8f),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Scenarios.Result Scene(string name, SimTuning t) => name switch
    {
        "collide" => Scenarios.Collide(t, Material.Rock, speed: 600f),
        "projectile" => Scenarios.Projectile(t, Material.Rock),
        "spin" => Scenarios.Spin(t, Material.Rock),
        "steel" => Scenarios.Projectile(t, Material.Steel, speed: 1500f, massMul: 8f),
        "glass" => Scenarios.Projectile(t, Material.Glass, speed: 1500f, massMul: 8f),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// Full work census for one scene: how often each decision is reached, how much of it is spent
    /// discovering that nothing happened, and what each phase costs per call and per element.
    /// </summary>
    private static void Detail(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 120);
        string which = ArgStr(args, "--scene", "dense");
        int cols = ArgInt(args, "--cols", 16);
        int rows = ArgInt(args, "--rows", 12);
        float spacing = which switch
        {
            "sparse" => 150f,
            "worst" => 118f,
            _ => 128f,
        };

        var tune = SimTuning.Default;
        if (DriftOverride > 0f) tune.ManifoldDrift = DriftOverride;
        var r = WithJobs(Scenarios.Field(tune, Material.Rock, cols, rows, 60f, spacing, 60f, 900f));
        int cells0 = r.State.CellCount, bonds0 = r.State.BondCount, bodies0 = r.State.BodyCount;

        double[] phaseMs = new double[(int)SolverPhase.Count];
        var phaseSw = new Stopwatch();
        r.Solver.PhaseMark = p => { phaseMs[(int)p] += phaseSw.Elapsed.TotalMilliseconds; phaseSw.Restart(); };

        for (int i = 0; i < 20; i++) { phaseSw.Restart(); r.Solver.Step(); }   // warm
        r.Solver.MeasureCoherence = true;
        r.Solver.C.Reset();
        r.State.ReindexCalls = 0; r.State.ReindexBondScans = 0;
        Array.Clear(phaseMs);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++) { phaseSw.Restart(); r.Solver.Step(); }
        sw.Stop();
        double msTick = sw.Elapsed.TotalMilliseconds / ticks;

        ref SolverCounters c = ref r.Solver.C;
        double T = ticks;
        Console.WriteLine($"scene '{which}': {bodies0} bodies, {cells0} cells, {bonds0} bonds, " +
                          $"{tune.Substeps} substeps — {msTick:F2} ms/tick");
        Console.WriteLine();

        Console.WriteLine("── work per tick, and how much of it finds nothing ──");
        Console.WriteLine($"{"stage",-30}{"calls/tick",12}{"wasted",12}{"waste%",9}  what 'wasted' means");
        Row("broadphase bucket visits", c.PairBucketVisits, c.PairRejectOrder + c.PairRejectSameBody + c.PairRejectRadius, "not a pair");
        Row("  of which same-body", c.PairRejectSameBody, c.PairRejectSameBody, "own body, can never touch");
        Row("  of which out of range", c.PairRejectRadius, c.PairRejectRadius, "too far apart");
        Row("narrow-phase pair tests", c.NarrowExamined, c.NarrowRejectDead + c.NarrowRejectSameBody + c.NarrowRejectRadius + c.NarrowRejectAabb, "rejected before SAT");
        Row("  rejected: dead cell", c.NarrowRejectDead, c.NarrowRejectDead, "one cell is gone");
        Row("  rejected: same body", c.NarrowRejectSameBody, c.NarrowRejectSameBody, "merged since pairing");
        Row("  rejected: radius", c.NarrowRejectRadius, c.NarrowRejectRadius, "circles do not touch");
        Row("  rejected: AABB", c.NarrowRejectAabb, c.NarrowRejectAabb, "boxes do not touch");
        Row("SAT calls", c.SatCalls, c.SatSeparated, "SAT ran and found no contact");
        Row("cell skinning", c.SkinComputed + c.SkinCacheHit, c.SkinCacheHit, "served from the substep cache");
        Row("cell local transforms", c.LocalComputed + c.LocalCacheHit, c.LocalCacheHit, "served from the substep cache");
        Row("bond force visits", c.BondForceVisits + c.BondForceSkipped, c.BondForceSkipped, "bond already broken");
        Row("damage visits", c.DamageVisits, c.DamageEarlyOut, "below threshold, early out");
        Row("  reached cohesive law", c.DamageEvaluated, c.DamageEvaluated - c.DamageBroke, "damaged but did not break");
        Row("component walks", c.SplitCalls, c.SplitNoChange, "walked, nothing split");
        Row("  cells walked", c.SplitCellsWalked, 0, "");
        Row("  bonds re-anchored", c.SplitBondsReanchored, 0, "");
        Row("reindex bond scans", r.State.ReindexBondScans, 0, "");
        Row("inertial body passes", c.InertialBodies, c.InertialSkipped, "body not rotating");
        Row("decompose bodies", c.DecomposeBodies, c.DecomposeNoOp, "field already zero-mean");
        Row("dust singles seen", c.DustSinglesSeen, c.DustSinglesSeen - c.DustConverted, "not eligible yet");
        Console.WriteLine();

        Console.WriteLine("── cost per phase ──");
        Console.WriteLine($"{"phase",-16}{"ms/tick",10}{"share",8}{"calls/tick",12}{"ns/call",10}   per-element");
        Phase(SolverPhase.BuildPairs, c.PairBuilds, c.PairBucketVisits, "bucket visit");
        Phase(SolverPhase.BuildContacts, c.NarrowCalls, c.NarrowExamined, "pair test");
        Phase(SolverPhase.RefreshContacts, tune.Substeps, c.ContactSolves, "contact");
        Phase(SolverPhase.Inertial, c.InertialBodies, c.InertialCells, "cell");
        Phase(SolverPhase.BondForces, tune.Substeps, c.BondForceVisits, "bond");
        Phase(SolverPhase.Contacts, c.ContactSolves, c.ContactSolves, "contact");
        Phase(SolverPhase.BondIntegrate, tune.Substeps, c.BondIntegrateVisits, "bond");
        Phase(SolverPhase.Decompose, c.DecomposeBodies, c.DecomposeCells, "cell");
        Phase(SolverPhase.Realize, tune.Substeps, c.RealizeCells, "cell");
        Phase(SolverPhase.Damage, tune.Substeps, c.DamageVisits, "bond");
        Phase(SolverPhase.Split, c.SplitCalls, c.SplitCellsWalked, "cell walked");
        Phase(SolverPhase.Settle, 1, r.State.BodyCount, "body");
        Phase(SolverPhase.Dust, c.DustScans, c.DustSinglesSeen, "single");
        Console.WriteLine();

        // How much of the scene is doing nothing? This is the ceiling on what sleeping could save.
        int idleCells = 0, movingCells = 0, idleBodies = 0, touchedBodies = 0;
        for (int b = 0; b < r.State.BodyCount; b++)
        {
            bool bodyIdle = true, touched = false;
            int off = r.State.BodyCellOff[b], len = r.State.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int cc = r.State.BodyCells[off + i];
                if (r.State.Dead(cc)) continue;
                float dv = System.Math.Abs(r.State.CellDvx[cc]) + System.Math.Abs(r.State.CellDvy[cc]);
                if (dv < 0.3f) idleCells++; else { movingCells++; bodyIdle = false; }
                if (r.State.CellTouch[cc] >= r.State.Tick - 2) touched = true;
            }
            if (touched) touchedBodies++;
            else if (bodyIdle) idleBodies++;
        }
        Console.WriteLine("── how much of the scene is doing nothing ──");
        Console.WriteLine($"  cells with a quiet deformation field     {idleCells,8} of {idleCells + movingCells} ({100.0 * idleCells / System.Math.Max(1, idleCells + movingCells):F0}%)");
        Console.WriteLine($"  bodies quiet AND untouched for 3 ticks   {idleBodies,8} of {r.State.BodyCount} ({100.0 * idleBodies / System.Math.Max(1, r.State.BodyCount):F0}%)");
        Console.WriteLine($"  bodies in contact recently               {touchedBodies,8} of {r.State.BodyCount}");
        Console.WriteLine();

        Console.WriteLine("── contact coherence between substeps ──");
        double repeatPct = c.SatContact > 0 ? 100.0 * c.ContactRepeatPair / c.SatContact : 0;
        Console.WriteLine($"  contacts that also existed last substep  {c.ContactRepeatPair / T,8:F0}/tick ({repeatPct:F0}%)");
        Console.WriteLine($"  mean |depth change| per substep          {(r.Solver.DepthDeltaCount > 0 ? r.Solver.DepthDeltaSum / r.Solver.DepthDeltaCount : 0),8:F4} px");
        Console.WriteLine($"  worst |depth change| per substep         {r.Solver.DepthDeltaMax,8:F4} px");
        Console.WriteLine();

        Console.WriteLine("── SAT internals ──");
        double satMs = phaseMs[(int)SolverPhase.BuildContacts] / T;
        Console.WriteLine($"  axes tested        {c.SatAxesTested / T,10:F0}/tick   ({(double)c.SatAxesTested / System.Math.Max(1, c.SatCalls),4:F1} per call)");
        Console.WriteLine($"  vertex projections {c.SatProjections / T,10:F0}/tick   ({(double)c.SatProjections / System.Math.Max(1, c.SatCalls),4:F1} per call)");
        Console.WriteLine($"  contacts produced  {c.SatContact / T,10:F0}/tick   ({100.0 * c.SatContact / System.Math.Max(1, c.SatCalls),4:F0}% hit rate)");
        Console.WriteLine();

        void Row(string name, long total, long wasted, string meaning)
        {
            double pct = total > 0 ? 100.0 * wasted / total : 0;
            Console.WriteLine($"{name,-30}{total / T,12:F0}{wasted / T,12:F0}{pct,8:F0}%  {meaning}");
        }
        void Phase(SolverPhase p, long calls, long elems, string unit)
        {
            double ms = phaseMs[(int)p] / T;
            double share = 100.0 * ms / System.Math.Max(1e-9, msTick);
            double nsCall = calls > 0 ? ms * 1e6 / (calls / T) : 0;
            double nsElem = elems > 0 ? ms * 1e6 / (elems / T) : 0;
            Console.WriteLine($"{p,-16}{ms,10:F2}{share,7:F0}%{calls / T,12:F0}{nsCall,10:F0}   " +
                              (elems > 0 ? $"{nsElem,6:F0} ns/{unit} ({elems / T,8:F0}/tick)" : ""));
        }
    }

    private static string ArgStr(string[] args, string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    /// <summary>Cost of the primitives the hot loops actually call.</summary>
    private static void Micro()
    {
        const int N = 2_000_000;
        var xs = new float[1024];
        for (int i = 0; i < xs.Length; i++) xs[i] = (i - 512) * 0.0002f;   // the uth range, +-0.1 rad

        float acc = 0f;
        var sw = new Stopwatch();

        sw.Restart();
        for (int i = 0; i < N; i++) { AsteroidsSim.Math.SimMath.SinCos(xs[i & 1023], out float s1, out float c1); acc += s1 + c1; }
        sw.Stop();
        Console.WriteLine($"SimMath.SinCos      {sw.Elapsed.TotalMilliseconds * 1e6 / N,7:F1} ns/call");

        sw.Restart();
        for (int i = 0; i < N; i++) { acc += AsteroidsSim.Math.SimMath.Sin(xs[i & 1023]); }
        sw.Stop();
        Console.WriteLine($"SimMath.Sin         {sw.Elapsed.TotalMilliseconds * 1e6 / N,7:F1} ns/call");

        sw.Restart();
        for (int i = 0; i < N; i++) { acc += MathF.Sin(xs[i & 1023]); }
        sw.Stop();
        Console.WriteLine($"MathF.Sin (banned)  {sw.Elapsed.TotalMilliseconds * 1e6 / N,7:F1} ns/call");

        sw.Restart();
        for (int i = 0; i < N; i++) { acc += AsteroidsSim.Math.SimMath.Sqrt(xs[i & 1023] * xs[i & 1023] + 1f); }
        sw.Stop();
        Console.WriteLine($"SimMath.Sqrt        {sw.Elapsed.TotalMilliseconds * 1e6 / N,7:F1} ns/call");

        Console.WriteLine($"(checksum {acc:E3})");
    }

    private static void Header()
        => Console.WriteLine(
            "  bodies   cells   bonds |  build ms |  ms/tick    p50    p99   max | cells@60Hz | broke dust");

    private static void RunCase(
        Material mat, int cols, int rows, float grain, int ticks, bool phases,
        float radius = 60f, float spacing = 150f, bool quiet = false)
    {
        var tune = SimTuning.Default;
        if (DriftOverride > 0f) tune.ManifoldDrift = DriftOverride;

        var buildSw = Stopwatch.StartNew();
        var r = WithJobs(Scenarios.Field(tune, mat, cols, rows, radius, spacing, 60f, grain));
        buildSw.Stop();

        int cells0 = r.State.CellCount, bonds0 = r.State.BondCount, bodies0 = r.State.BodyCount;

        double[] phaseMs = new double[(int)SolverPhase.Count];
        var phaseSw = new Stopwatch();
        if (phases)
        {
            r.Solver.PhaseMark = p =>
            {
                phaseMs[(int)p] += phaseSw.Elapsed.TotalMilliseconds;
                phaseSw.Restart();
            };
        }

        var samples = new double[ticks];
        var sw = new Stopwatch();
        for (int i = 0; i < ticks; i++)
        {
            if (phases) phaseSw.Restart();
            sw.Restart();
            r.Solver.Step();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        if (quiet) return;

        Array.Sort(samples);
        double p50 = samples[samples.Length / 2];
        double p99 = samples[(int)(samples.Length * 0.99)];
        double max = samples[samples.Length - 1];
        double mean = 0; foreach (double v in samples) mean += v;
        mean /= samples.Length;

        int live = 0;
        for (int c = 0; c < r.State.CellCount; c++) if (!r.State.Dead(c)) live++;

        // Linear extrapolation from the median: how many cells fit the physics share.
        double cellsAtBudget = live > 0 ? live * (PhysicsShare / p50) : 0;

        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine(
            $"  {bodies0,6} {cells0,7} {bonds0,7} | {buildSw.Elapsed.TotalMilliseconds,9:F0} | " +
            $"{mean,8:F2} {p50,6:F2} {p99,6:F2} {max,5:F2} | {cellsAtBudget,10:F0} | " +
            $"{r.Solver.Broken,5} {r.Solver.Dust,4}");
        long satTotal = r.Solver.C.SatCalls + r.Solver.C.NarrowRejectAabb;
        Console.WriteLine(
            $"        pairs={r.Solver.PairCount}/substep  contacts={r.Solver.ContactCount}  " +
            $"sat={r.Solver.C.SatCalls / System.Math.Max(1, ticks)}/tick  " +
            $"boxRejected={100.0 * r.Solver.C.NarrowRejectAabb / System.Math.Max(1L, satTotal):F0}%");

        if (phases)
        {
            double total = 0; foreach (double v in phaseMs) total += v;
            if (total <= 0) return;
            Console.Write("        ");
            for (int p = 0; p < (int)SolverPhase.Count; p++)
            {
                if (phaseMs[p] / total < 0.02) continue;
                Console.Write($"{(SolverPhase)p}={(100 * phaseMs[p] / total).ToString("F0", ci)}% " +
                              $"({(phaseMs[p] / ticks).ToString("F2", ci)}ms)  ");
            }
            Console.WriteLine();
        }
    }

    private static float ArgFloat(string[] args, string name, float fallback)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return fallback;
        return float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : fallback;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return fallback;
        return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;
    }
}
