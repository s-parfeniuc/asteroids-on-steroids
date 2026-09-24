using System.Collections.Generic;
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
        if (Array.IndexOf(args, "--audit") >= 0) { SideAuditRun(Array.IndexOf(args, "--v1lone") < 0); return; }
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
        if (Array.IndexOf(args, "--shell") >= 0) { ShellRun(args); return; }
        if (Array.IndexOf(args, "--impact") >= 0) { ImpactTypes(args); return; }
        if (Array.IndexOf(args, "--structure") >= 0) { StructureRun(args); return; }
        if (Array.IndexOf(args, "--crashhunt") >= 0) { CrashHunt(args); return; }
        if (Array.IndexOf(args, "--bondlen") >= 0) { BondLenRun(args); return; }
        if (Array.IndexOf(args, "--spawnin") >= 0) { SpawnInside(args); return; }
        if (Array.IndexOf(args, "--rodshape") >= 0) { RodShape(args); return; }
        if (Array.IndexOf(args, "--backstop") >= 0) { BackstopRun(args); return; }
        if (Array.IndexOf(args, "--overlapgate") >= 0) { OverlapGateRun(args); return; }
        if (Array.IndexOf(args, "--subablate") >= 0) { SubstepAblationRun(args); return; }
        if (Array.IndexOf(args, "--ablate") >= 0) { InertialAblationRun(args); return; }
        if (Array.IndexOf(args, "--drained") >= 0) { DrainedCellRun(args); return; }
        if (Array.IndexOf(args, "--bondomega") >= 0) { BondOmegaRun(args); return; }
        if (Array.IndexOf(args, "--energystage") >= 0) { EnergyStageRun(args); return; }
        if (Array.IndexOf(args, "--diverge") >= 0) { DivergeRun(args); return; }
        if (Array.IndexOf(args, "--nanstage") >= 0) { NanStageRun(args); return; }
        if (Array.IndexOf(args, "--nantrap") >= 0) { NanTrapRun(args); return; }
        if (Array.IndexOf(args, "--overlap") >= 0) { OverlapCensusRun(args); return; }
        if (Array.IndexOf(args, "--nansweep") >= 0) { NanSweepRun(args); return; }
        if (Array.IndexOf(args, "--pairwatch") >= 0) { PairWatchRun(args); return; }
        if (Array.IndexOf(args, "--guardspread") >= 0) { GuardrailSpreadRun(args); return; }
        if (Array.IndexOf(args, "--smallround") >= 0) { SmallRoundRun(args); return; }
        if (Array.IndexOf(args, "--fusecheck") >= 0) { FuseCheckRun(args); return; }
        if (Array.IndexOf(args, "--bondiface") >= 0) { BondInterfaceRun(args); return; }
        if (Array.IndexOf(args, "--partb") >= 0) { PartBRun(args); return; }
        if (Array.IndexOf(args, "--dentcap") >= 0) { DentCapacityRun(args); return; }
        if (Array.IndexOf(args, "--dentbudget") >= 0) { DentBudgetRun(args); return; }
        if (Array.IndexOf(args, "--dentpick") >= 0) { DentPickRun(args); return; }
        if (Array.IndexOf(args, "--dentscale") >= 0) { DentScaleRun(args); return; }
        if (Array.IndexOf(args, "--tiptrace") >= 0) { TipTraceRun(args); return; }
        if (Array.IndexOf(args, "--carveangle") >= 0) { CarveAngleRun(args); return; }
        if (Array.IndexOf(args, "--viewerrod") >= 0) { ViewerRodRun(args); return; }
        if (Array.IndexOf(args, "--tip") >= 0) { TipRun(args); return; }
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
    private static void SideAuditRun(bool loneCells = true)
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
            ("steel shell / rock core", Scenarios.Shell(t, Material.Steel, Material.Rock)),
        })
        {
            r.Solver.DentLoneCells = loneCells;
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
            int ts = ArgInt(args, "--tscale", -1); if (ts >= 0) tn.ToughnessScale = ts / 10f;
            if (Array.IndexOf(args, "--crackpush") >= 0) tn.CrackPush = true;
            if (Array.IndexOf(args, "--splitopen") >= 0) tn.SplitOnOpen = true;
            var r = Scenarios.Collide(tn, m, 600f, 170f);
            SimState s = r.State;
            float v0 = SimMath.Hypot(s.BodyVx[0], s.BodyVy[0]);
            float m0 = s.BodyM[0];
            Console.WriteLine($"── {m.Name}: body 0 starts at {v0:F0} px/s, mass {m0:F0}");
            Console.WriteLine("   tick  body0 v   mass%   bodies  contacts  dead  dust→nbrs  dust→contacts  ledger%   |Dv| front / back   loaded bonds front / back");
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
                Console.WriteLine($"   {tick,4}  {SimMath.Hypot(s.BodyVx[0], s.BodyVy[0]),7:F0}  {100f * s.BodyM[0] / m0,5:F0}%  {s.BodyCount,6}  {r.Solver.ContactCount,8}  {dead,4}  {r.Solver.DustToNeighbours,9}  {r.Solver.DustToContacts,13}  {LedgerPct(r.Solver),6:F1}%   {(nF > 0 ? dvF / nF : 0f),6:F1} / {(nB > 0 ? dvB / nB : 0f),-6:F1}   {sbF,5} / {sbB}");
                lastDead = dead;
            }
            Console.WriteLine($"   comminution momentum: to neighbours (impact share) {r.Solver.DustDevMom:F0}, left with mass (rigid share) {r.Solver.DustRigidMom:F0}, "
                            + $"into contact partners {r.Solver.DustContactMom:F0}, exported (pressing on nothing) {r.Solver.DustLostMom:F0}; body 0 initial momentum {m0 * v0:F0}");
        }
    }

    /// <summary>The rod's tip: what the driver sees there, and what it does about it.</summary>
    /// Builds the rod exactly as the viewer's left-click does and reports whether carving v2 even
    /// runs on it, or whether it falls back to v1 for want of a live touch record.
    /// Steps a scene tick by tick and reports the first NaN, and where it appeared.
    private static bool FirstNaN(Scenarios.Result r, int ticks, string label)
    {
        SimState s = r.State;
        for (int i = 0; i < ticks; i++)
        {
            r.Solver.Step();
            for (int b = 0; b < s.BodyCount; b++)
            {
                if (!float.IsNaN(s.BodyX[b]) && !float.IsNaN(s.BodyY[b])
                    && !float.IsNaN(s.BodyVx[b]) && !float.IsNaN(s.BodyVy[b])
                    && !float.IsNaN(s.BodyRot[b])) continue;
                Console.WriteLine($"  {label}: NaN at tick {i} in body {b} "
                    + $"(x {s.BodyX[b]}, y {s.BodyY[b]}, vx {s.BodyVx[b]}, vy {s.BodyVy[b]}, rot {s.BodyRot[b]})");
                return true;
            }
            int bad = 0;
            for (int c = 0; c < s.CellCount; c++)
                if (float.IsNaN(s.CellRx[c]) || float.IsNaN(s.CellRy[c])
                    || float.IsNaN(s.CellDvx[c]) || float.IsNaN(s.CellDvy[c])) bad++;
            if (bad > 0)
            {
                Console.WriteLine($"  {label}: NaN at tick {i} in {bad} cell(s) (rx/ry/dvx/dvy), bodies still finite");
                return true;
            }
        }
        Console.WriteLine($"  {label}: clean through {ticks} ticks");
        return false;
    }

    private static void CarveAngleRun(string[] args)
    {
        {
            var tune0 = SimTuning.Default;
            void Probe(string label, Scenarios.Result cs0)
            {
                SimState st0 = cs0.State;
                int b0 = st0.BodyCount;
                int firstSplit = -1, firstNan = -1;
                for (int i = 0; i < 30 && firstNan < 0; i++)
                {
                    cs0.Solver.Step();
                    if (firstSplit < 0 && st0.BodyCount > b0) firstSplit = i;
                    for (int b = 0; b < st0.BodyCount; b++)
                        if (float.IsNaN(st0.BodyX[b])) { firstNan = i; break; }
                }
                Console.WriteLine($"  {label,-34} start {b0,3} bodies -> first split at tick "
                    + $"{(firstSplit < 0 ? "never" : firstSplit.ToString()),5}, first NaN at tick "
                    + $"{(firstNan < 0 ? "never" : firstNan.ToString()),5}, now {st0.BodyCount} bodies");
            }
            void Angles(string label, Scenarios.Result cs, int ticks)
            {
                cs.Solver.MeasureCarveAngles = true;
                for (int i = 0; i < ticks; i++) cs.Solver.Step();
                int tot = 0; foreach (int v in cs.Solver.CarveNormalAngle) tot += v;
                if (tot == 0) { Console.WriteLine($"  {label}: no carves"); return; }
                var sb = new System.Text.StringBuilder();
                for (int b = 0; b < 9; b++) sb.Append($"{100.0 * cs.Solver.CarveNormalAngle[b] / tot,5:F1}");
                Console.WriteLine($"  {label,-30} {sb}   ({tot} carves)");
            }
            Console.WriteLine("  carve-normal vs approach direction, % of carves per 10 deg bucket");
            Console.WriteLine($"  {"",-30}  0-10 10-20 20-30 30-40 40-50 50-60 60-70 70-80 80-90");
            Angles("Collide(rock 600 g900)", Scenarios.Collide(tune0, Material.Rock, 600f, 900f), 120);
            Angles("Collide(rock 600 g255)", Scenarios.Collide(tune0, Material.Rock, 600f, 255f), 120);
            Angles("Projectile(rock 900 g900)", Scenarios.Projectile(tune0, Material.Rock, 900f, 8f, 900f), 120);
            {
                var rr = Scenarios.Collide(tune0, Material.Rock, 0f, 900f);
                float tx2 = rr.State.BodyX[0], ty2 = rr.State.BodyY[0];
                rr = Scenarios.FireAt(rr, tune0, Material.Penetrator, tx2 - 420f, ty2, tx2, ty2,
                    speed: 1800f, radius: 15f, grain: 900f, seed: 1, radiusX: 52.5f, radiusY: 4.3f);
                Angles("viewer ROD (1 cell)", rr, 120);
            }
            Console.WriteLine();
            Probe("Collide(rock, 600, g900)", Scenarios.Collide(tune0, Material.Rock, 600f, 900f));
            Probe("Collide(rock, 0, g900)", Scenarios.Collide(tune0, Material.Rock, 0f, 900f));
            Probe("Collide(steel, 600, g900)", Scenarios.Collide(tune0, Material.Steel, 600f, 900f));
            Probe("Projectile(rock, 900, g900)", Scenarios.Projectile(tune0, Material.Rock, 900f, 8f, 900f));
            Probe("Spin(rock, g900)", Scenarios.Spin(tune0, Material.Rock, grain: 900f));
            Probe("Field(rock, 3x3, g900)", Scenarios.Field(tune0, Material.Rock, 3, 3, 60f, 150f, 60f, 900f));
            Console.WriteLine();
        }
        float grain = ArgFloat(args, "--grain", 900f), speed = ArgFloat(args, "--speed", 1800f);
        float mass = ArgFloat(args, "--mass", 1f);
        float radius = 15f * MathF.Sqrt(mass);
        var tune = SimTuning.Default;
        Console.WriteLine($"grain {grain}, speed {speed}, mass {mass} (radius {radius:F1}), CrackPush {tune.CrackPush}, CrackPushCap {tune.CrackPushCap}");

        foreach (float sp in new[] { 0f, 1f, 100f, 600f })
        {
            var cs = Scenarios.Collide(tune, Material.Rock, sp, grain);
            SimState st = cs.State;
            int badAtBuild = 0;
            for (int b = 0; b < st.BodyCount; b++)
                if (float.IsNaN(st.BodyX[b]) || float.IsNaN(st.BodyY[b]) || float.IsNaN(st.BodyVx[b])
                    || float.IsNaN(st.BodyVy[b]) || float.IsNaN(st.BodyRot[b]) || float.IsNaN(st.BodyW[b])) badAtBuild++;
            Console.WriteLine($"  Collide(speed {sp}): {badAtBuild} of {st.BodyCount} bodies NaN AT BUILD "
                + $"(b0 v {st.BodyVx[0]},{st.BodyVy[0]} rot {st.BodyRot[0]} omega {st.BodyW[0]})");
            FirstNaN(cs, 40, $"    Collide(speed {sp}) stepped");
        }

        foreach (var (name, mat, rx, ry) in new (string, Material, float, float)[]
        {
            ("round bullet, Rock", Material.Rock, radius, radius),
            ("round bullet, Penetrator", Material.Penetrator, radius, radius),
            ("ROD, Rock", Material.Rock, radius * 3.5f, radius / 3.5f),
            ("ROD, Penetrator", Material.Penetrator, radius * 3.5f, radius / 3.5f),
        })
        {
            var r = Scenarios.Collide(tune, Material.Rock, 0f, grain);
            float tx = r.State.BodyX[0], ty = r.State.BodyY[0];
            r = Scenarios.FireAt(r, tune, mat, tx - 420f, ty, tx, ty,
                speed: speed, radius: radius, grain: grain, seed: 1, radiusX: rx, radiusY: ry);
            int n0 = r.State.CellCount;
            int rodCells = 0; int rb = r.State.CellBody[n0 - 1];
            for (int c = 0; c < n0; c++) if (r.State.CellBody[c] == rb) rodCells++;
            FirstNaN(r, 40, $"{name} ({rodCells} cell rod)");
        }
    }

    /// The viewer's piercing round at its real defaults (grain 900, speed 900, mass 3), with the
    /// material knobs the report used to exaggerate erosion: min crush threshold (x1/64), max
    /// erosion rate (x64), max shed limit. Traces every dent on the rod's front cell.
    /// Which per-cell length scale lets ONE global Dent constant resolve a cell's own vertices?
    /// For each cell: the typical spacing between neighbouring vertices is the feature the kernel
    /// must tell apart. A scale is good if scale/spacing is the SAME for every cell shape, because
    /// then a single multiplier works everywhere. Reported as coefficient of variation (lower better).
    /// Sweeps Material.Dent on a normal collide and reports what the population of cells does,
    /// so the retune is chosen on more than the one rod cell that exposed the bug.
    /// A/B for the iterated budget solve: how much of the area the crush law asks for is actually
    /// removed, with the iteration on and off, at the kernel width the model now ships.
    /// Controlled test of "more vertices = more capacity": take each cell's real outline, measure
    /// its one-step recession capacity, then insert a midpoint on a surface edge and measure again.
    /// Capacity = sum over free corners of (area per unit slide) x (slide before the corner crosses
    /// the chord joining its neighbours) — the two quantities GatherCornersOf already computes.
    /// State of part B: how much carving still goes through the v1 fallback, how far its clip
    /// direction sits from the actual approach, and whether the rebuild ever retires a vertex.
    /// Part D: does a bond's strength still describe the interface that is actually left?
    /// Checks three things the carved-interface rescale claims: that stiffness telescopes exactly
    /// with length, that nothing holds two cells together after its interface is gone, and what a
    /// two-cell body does when its single interface is eroded away.
    /// C3: does the solver report a projectile's first contact on the tick it actually strikes?
    /// Cross-checked against two independent signals: the round's speed dropping, and its world
    /// AABB first overlapping the target's.
    /// C2: can a round be made much smaller and still do the same damage, and by which lever?
    /// Baseline is today's round. Then the same round shrunk, with mass restored by raising the
    /// material's density (a dense slug), and shrunk with mass left to fall and speed raised to
    /// hold kinetic energy. Damage is what the target actually loses.
    /// E: the morphology guardrails' ensembles, printed rather than asserted, so a band can be
    /// re-derived from the spread the scene actually has instead of from one run.
    /// Point 3's repro: collide rock/rock at defaults, watch a named pair overlap and see when
    /// (and whether) carving responds. Reports the contact between them every tick with the gate
    /// decision, so "deep overlap but nothing recedes" can be attributed to a specific test.
    /// Point 2: sweep viewer-like configurations looking for a state that goes non-finite.
    /// Point 1: how deep does overlap actually get, as a fraction of the cells involved?
    /// Point 2, narrowing: run the smallest failing configuration and name the FIRST quantity that
    /// goes non-finite, so the blow-up can be attributed to a stage rather than guessed at.
    /// Point 2, pass 1: hook every solver stage and report the FIRST stage after which any LIVE
    /// quantity is non-finite — with every category that is bad at that moment, counted, so the
    /// order the checks run in cannot pass for causation.
    /// Point 2, pass 2: the non-finite value is the END of a divergence. Find where it STARTS:
    /// the first stage after which a live body spins or moves faster than anything in the scene
    /// could legitimately make it, and dump that body's rotational state.
    /// Point 2, ablation: which inertial load is necessary for the blow-up? Each crashing
    /// configuration is run with each load switched off, and live state checked for 60 ticks.
    /// Point 2: is it a stability limit? The same configurations at more substeps, with the
    /// acoustic CFL number each one runs at (wave speed x substep / cell size).
    /// Point 1: at each depth of penetration, how often does the carve gate open? Then the one
    /// existing knob that ties pressure to depth (CrushConfine), swept, with what it does to
    /// penetration and to morphology.
    private static float HalfExtent(SimState s, int c, float nx, float ny)
    {
        int len = s.PolyLen[c];
        if (len <= 0) return s.CellRad[c];
        int b = s.CellBody[c];
        float si = MathF.Sin(s.BodyRot[b]), co = MathF.Cos(s.BodyRot[b]);
        float lx = nx * co + ny * si, ly = -nx * si + ny * co;
        int off = s.PolyOff[c];
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (int v = 0; v < len; v++)
        {
            float pr = s.PolyX[off + v] * lx + s.PolyY[off + v] * ly;
            lo = MathF.Min(lo, pr); hi = MathF.Max(hi, pr);
        }
        return 0.5f * (hi - lo);
    }

    /// Point 1, after the change: penetration against the backstop's own yardstick (the thinner
    /// cell's half-extent along the normal), how often the backstop fires, and what it costs.
    private static void BackstopRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 200);
        var variants = new (string, float, float)[]
        {
            ("old defaults (0.05, no backstop)", 0.05f, 0f),
            ("CrushConfine 1.0, no backstop", 1.0f, 0f),
            ("CrushConfine 1.0, backstop 0.3", 1.0f, 0.3f),
            ("CrushConfine 1.0, backstop 0.2", 1.0f, 0.2f),
        };
        foreach (var (sname, make) in new (string, Func<SimTuning, Scenarios.Result>)[]
        {
            ("collide rock g900", t2 => Scenarios.Collide(t2, Material.Rock, 600f, 900f)),
            ("collide rock g255", t2 => Scenarios.Collide(t2, Material.Rock, 600f, 255f)),
            ("collide glass g255", t2 => Scenarios.Collide(t2, Material.Glass, 600f, 255f)),
            ("projectile rock", t2 => Scenarios.Projectile(t2, Material.Rock, 900f, 8f, 900f)),
            ("projectile steel->rock 2500", t2 => Scenarios.Projectile(t2, Material.Rock, 2500f, 8f, 900f, impactor: Material.Steel)),
        })
        {
            Console.WriteLine($"\n── {sname} ──");
            Console.WriteLine("  variant                           | pen/halfext p50  p90    p99    max   | backstop contact-substeps | bodies | area kept");
            foreach (var (vname, cc, bs) in variants)
            {
                var tune = SimTuning.Default; tune.CrushConfine = cc; tune.OverlapBackstop = bs;
                var r = make(tune);
                SimState s = r.State;
                float area0 = 0f; for (int c = 0; c < s.CellCount; c++) area0 += s.CellArea[c];
                var frac = new List<double>();
                bool fresh = true;
                // The tick's own narrow phase runs once, before the substeps, from current geometry:
                // sampling there reads the TRUE penetration at every tick boundary, not the solver's
                // per-substep bookkeeping of it.
                r.Solver.PhaseMark = ph =>
                {
                    if (ph == SolverPhase.Inertial) fresh = false;
                    if (ph != SolverPhase.BuildContacts || !fresh) return;
                    for (int q = 0; q < r.Solver.ContactCount; q++)
                    {
                        Contact ct = r.Solver.ContactAt(q);
                        if (ct.A >= s.CellCount || ct.B >= s.CellCount || s.Dead(ct.A) || s.Dead(ct.B)) continue;
                        if (ct.Depth <= 0f) continue;
                        float he = MathF.Min(HalfExtent(s, ct.A, ct.Nx, ct.Ny), HalfExtent(s, ct.B, ct.Nx, ct.Ny));
                        if (he > 1e-3f) frac.Add(ct.Depth / he);
                    }
                };
                for (int i = 0; i < ticks; i++) { fresh = true; r.Solver.Step(); }
                frac.Sort();
                double Q(double f) => frac.Count == 0 ? 0 : frac[(int)(f * (frac.Count - 1))];
                float area1 = 0f; for (int c = 0; c < s.CellCount; c++) if (!s.Dead(c)) area1 += s.CellArea[c];
                Console.WriteLine($"  {vname,-33} | {Q(0.5),6:F3} {Q(0.9),6:F3} {Q(0.99),6:F3} {(frac.Count > 0 ? frac[^1] : 0),6:F3} | "
                    + $"{r.Solver.BackstopContacts,10}                | {s.BodyCount,6} | {100f * area1 / area0,7:F1}%");
                if (r.Solver.BackstopContacts > 0)
                    Console.WriteLine($"      forced: asked {r.Solver.BackstopAsked:F1} px, got {r.Solver.BackstopGot:F1} px; "
                        + $"removed nothing on {r.Solver.BackstopNothing} of {r.Solver.BackstopContacts}; "
                        + $"a side stuck {r.Solver.BackstopOutOfReach}, "
                        + $"v1 {r.Solver.BackstopV1}; overrode the rate law {r.Solver.BackstopRecessions}x; "
                        + $"COMMINUTED {r.Solver.BackstopComminuted}, solo exempt {r.Solver.BackstopSoloExempt}"
                        + $"\n      right after the carve: both sides receded -> worst {r.Solver.BackstopWorstAfterBoth:F3}, still over {r.Solver.BackstopStillOverBoth}; "
                        + $"a side could not -> worst {r.Solver.BackstopWorstAfterStuck:F3}, still over {r.Solver.BackstopStillOverStuck}");
            }
        }
    }

    private static void OverlapGateRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 200);
        string[] bins = { "<0.1", "0.1-0.25", "0.25-0.5", "0.5-1", ">1" };
        foreach (float cc in new[] { 0.05f, 0.2f, 0.5f, 1.0f, 2.0f })
        {
            Console.WriteLine($"\n══ CrushConfine {cc} ══");
            foreach (var (name, make) in new (string, Func<SimTuning, Scenarios.Result>)[]
            {
                ("collide rock g900", t2 => Scenarios.Collide(t2, Material.Rock, 600f, 900f)),
                ("collide rock g255", t2 => Scenarios.Collide(t2, Material.Rock, 600f, 255f)),
                ("projectile rock", t2 => Scenarios.Projectile(t2, Material.Rock, 900f, 8f, 900f)),
            })
            {
                var tune = SimTuning.Default; tune.CrushConfine = cc;
                var r = make(tune);
                r.Solver.MeasureCarveAngles = true;
                SimState s = r.State;
                float area0 = 0f; for (int c = 0; c < s.CellCount; c++) area0 += s.CellArea[c];
                var frac = new List<double>();
                for (int i = 0; i < ticks; i++)
                {
                    r.Solver.Step();
                    for (int q = 0; q < r.Solver.ContactCount; q++)
                    {
                        Contact ct = r.Solver.ContactAt(q);
                        if (ct.Depth <= 0f || ct.A >= s.CellCount || ct.B >= s.CellCount) continue;
                        float rad = MathF.Min(s.CellRad[ct.A], s.CellRad[ct.B]);
                        if (rad > 1e-3f) frac.Add(ct.Depth / rad);
                    }
                }
                frac.Sort();
                double Q(double f) => frac.Count == 0 ? 0 : frac[(int)(f * (frac.Count - 1))];
                float area1 = 0f; int live = 0;
                for (int c = 0; c < s.CellCount; c++) if (!s.Dead(c)) { area1 += s.CellArea[c]; live++; }
                var sb = new System.Text.StringBuilder();
                for (int b = 0; b < 5; b++)
                {
                    long o = r.Solver.GateOpenByPen[b], sh = r.Solver.GateShutByPen[b];
                    sb.Append(o + sh == 0 ? $" {bins[b]}: -" : $" {bins[b]}: {100.0 * o / (o + sh):F0}% of {o + sh}");
                    sb.Append(" |");
                }
                Console.WriteLine($"  {name,-18} pen p50 {Q(0.5):F3} p90 {Q(0.9):F3} p99 {Q(0.99):F3} max {(frac.Count > 0 ? frac[^1] : 0):F3}"
                    + $" | bodies {s.BodyCount,3}, area kept {100f * area1 / area0:F1}%");
                Console.WriteLine($"     gate open by penetration:{sb}");
            }
        }
    }

    private static void SubstepAblationRun(string[] args)
    {
        var cfgs = new (string, Material, float, float, float)[]
        {
            ("rock g30 v2500 m24", Material.Rock, 30f, 2500f, 24f),
            ("sandstone g60 v4000 m8", Material.Sandstone, 60f, 4000f, 8f),
            ("steel g30 v4000 m24", Material.Steel, 30f, 4000f, 24f),
            ("steel g30 v1500 m1", Material.Steel, 30f, 1500f, 1f),
            ("steel g30 v2500 m8", Material.Steel, 30f, 2500f, 8f),
        };
        int[] subs = { 9, 12, 16, 24 };
        Console.Write("  config                    ");
        foreach (int n in subs) Console.Write($"| {n,2} substeps (CFL)   ");
        Console.WriteLine();
        foreach (var (name, mat, grain, speed, mass) in cfgs)
        {
            Console.Write($"  {name,-25} ");
            foreach (int n in subs)
            {
                var tune = SimTuning.Default;
                tune.Substeps = n;
                var r = Scenarios.Projectile(tune, mat, speed, mass, grain);
                SimState s = r.State;
                // The grain actually built (bodies are floored at BodyBuilder.MinGrain), and the CFL
                // the suite defines: wave speed x (Solver.Dt / substeps) / cell size.
                float built = MathF.Max(grain, BodyBuilder.MinGrain(mat, tune));
                float cellPx = SimMath.Sqrt(built);
                float cpx = mat.C * tune.PxPerMetre;
                float cfl = cpx * (Solver.Dt / n) / cellPx;
                string res = "ok";
                for (int i = 0; i < 60 && res == "ok"; i++)
                {
                    r.Solver.Step();
                    for (int b = 0; b < s.BodyCount; b++)
                    {
                        if (s.BodyCellLen[b] <= 0) continue;
                        if (!float.IsFinite(s.BodyW[b]) || !float.IsFinite(s.BodyVx[b])) { res = $"NaN t{i}"; break; }
                    }
                }
                Console.Write($"| {res,-8} ({cfl,5:F2})   ");
            }
            Console.WriteLine();
        }
    }

    private static void InertialAblationRun(string[] args)
    {
        var cfgs = new (string, Material, float, float, float)[]
        {
            ("rock g30 v2500 m24", Material.Rock, 30f, 2500f, 24f),
            ("sandstone g60 v4000 m8", Material.Sandstone, 60f, 4000f, 8f),
            ("steel g30 v4000 m24", Material.Steel, 30f, 4000f, 24f),
            ("steel g30 v1500 m1", Material.Steel, 30f, 1500f, 1f),
            ("ice g30 v4000 m24", Material.Ice, 30f, 4000f, 24f),
        };
        Console.WriteLine("  config                    | all loads | no centrifugal | no Euler | no Coriolis");
        foreach (var (name, mat, grain, speed, mass) in cfgs)
        {
            string Run(bool cen, bool eul, bool cor)
            {
                var tune = SimTuning.Default;
                tune.Centrifugal = cen; tune.Euler = eul; tune.Coriolis = cor;
                var r = Scenarios.Projectile(tune, mat, speed, mass, grain);
                SimState s = r.State;
                for (int i = 0; i < 60; i++)
                {
                    r.Solver.Step();
                    for (int b = 0; b < s.BodyCount; b++)
                    {
                        if (s.BodyCellLen[b] <= 0) continue;
                        if (!float.IsFinite(s.BodyW[b]) || !float.IsFinite(s.BodyVx[b]) || !float.IsFinite(s.BodyVy[b]))
                            return $"NaN t{i}";
                    }
                }
                return "ok";
            }
            Console.WriteLine($"  {name,-25} | {Run(true, true, true),9} | {Run(false, true, true),14} | "
                + $"{Run(true, false, true),8} | {Run(true, true, false),11}");
        }
    }

    /// Point 2, the head of the divergence: total kinetic energy (rigid + deviation) after every
    /// stage, and the stages that raise it most. Carving, contacts and dust should only lose energy
    /// or trade it with the bonds; a stage that creates a large multiple of it is the source.
    /// Point 2: each live bond's explicit-stability number w*h = sqrt(K0*(1/mA+1/mB)) * h, over
    /// time, against how much mass its cells have lost. The grain cap bounds this at build; the
    /// question is whether carving drives it up afterwards.
    /// Point 2: is the drained-but-bonded cell something the new carving settings produce? The
    /// crashing configuration on each combination of CrushConfine and backstop, counting — inside
    /// the tick, after every carve — live bonds whose cell is already past its ShedLimit.
    private static void DrainedCellRun(string[] args)
    {
        Console.WriteLine("rock grain 30 (built 93), 2500 px/s, mass 24, 60 ticks\n");
        Console.WriteLine("  CrushConfine  backstop | result   | substeps with a live bond on a cell past ShedLimit | worst w*h on one");
        foreach (var (cc, bs) in new (float, float)[] { (0.05f, 0f), (1.0f, 0f), (0.05f, 0.3f), (1.0f, 0.3f) })
        {
            var tune = SimTuning.Default; tune.CrushConfine = cc; tune.OverlapBackstop = bs;
            var r = Scenarios.Projectile(tune, Material.Rock, 2500f, 24f, 30f);
            SimState s = r.State;
            float h = Solver.Dt / tune.Substeps;
            long drainedSubsteps = 0; float worst = 0f; string res = "ok";
            r.Solver.PhaseMark = ph =>
            {
                if (ph != SolverPhase.Contacts) return;          // right after this substep's carving
                bool any = false;
                for (int k = 0; k < s.BondCount; k++)
                {
                    if (s.BondBroken[k]) continue;
                    int a = s.BondA[k], b = s.BondB[k];
                    if (s.Dead(a) || s.Dead(b)) continue;
                    bool pa = s.CellArea0[a] > 0f && 1f - s.CellArea[a] / s.CellArea0[a] >= s.Mat(a).ShedLimit;
                    bool pb = s.CellArea0[b] > 0f && 1f - s.CellArea[b] / s.CellArea0[b] >= s.Mat(b).ShedLimit;
                    if (!pa && !pb) continue;
                    any = true;
                    float wh = MathF.Sqrt(MathF.Max(0f, s.BondK0[k] * (1f / s.CellM[a] + 1f / s.CellM[b]))) * h;
                    if (wh > worst) worst = wh;
                }
                if (any) drainedSubsteps++;
            };
            for (int i = 0; i < 60 && res == "ok"; i++)
            {
                r.Solver.Step();
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c)) continue;
                    int b = s.CellBody[c];
                    if (!float.IsFinite(s.CellDvx[c]) || (b >= 0 && b < s.BodyCount && !float.IsFinite(s.BodyVx[b]))) { res = $"NaN t{i}"; break; }
                }
            }
            Console.WriteLine($"  {cc,12:F2}  {bs,8:F2} | {res,-8} | {drainedSubsteps,10}                                       | {worst,10:G4}");
        }
    }

    private static void BondOmegaRun(string[] args)
    {
        string mn = ArgStr(args, "--mat", "rock");
        float grain = ArgFloat(args, "--grain", 30f), speed = ArgFloat(args, "--speed", 2500f), massMul = ArgFloat(args, "--mass", 24f);
        Material mat = mn switch { "steel" => Material.Steel, "sandstone" => Material.Sandstone, "glass" => Material.Glass, _ => Material.Rock };
        var tune = SimTuning.Default;
        var r = Scenarios.Projectile(tune, mat, speed, massMul, grain);
        SimState s = r.State;
        float h = Solver.Dt / tune.Substeps;
        var m0 = new float[s.CellCount];
        for (int c = 0; c < s.CellCount; c++) m0[c] = s.CellM[c];
        Console.WriteLine($"{mn} grain {grain} speed {speed} mass {massMul}; h {h:E3}");
        Console.WriteLine("  tick | bonds | w*h max   p99     p50  | >1   >2   | worst bond: cells' mass left (of build)");
        for (int tick = 0; tick < 9; tick++)
        {
            var wh = new List<float>(); float worst = 0f; int wk = -1; int over1 = 0, over2 = 0;
            for (int k = 0; k < s.BondCount; k++)
            {
                if (s.BondBroken[k]) continue;
                int a = s.BondA[k], b = s.BondB[k];
                if (a >= m0.Length || b >= m0.Length || s.Dead(a) || s.Dead(b)) continue;
                float inv = 1f / s.CellM[a] + 1f / s.CellM[b];
                float v = MathF.Sqrt(MathF.Max(0f, s.BondK0[k] * inv)) * h;
                wh.Add(v);
                if (v > 1f) over1++;
                if (v > 2f) over2++;
                if (v > worst) { worst = v; wk = k; }
            }
            wh.Sort();
            string wdesc = wk < 0 ? "" : $"{100f * s.CellM[s.BondA[wk]] / m0[s.BondA[wk]],5:F1}% and {100f * s.CellM[s.BondB[wk]] / m0[s.BondB[wk]],5:F1}%";
            Console.WriteLine($"  {tick,4} | {wh.Count,5} | {(wh.Count > 0 ? wh[^1] : 0),7:F3} {(wh.Count > 0 ? wh[(int)(0.99 * (wh.Count - 1))] : 0),7:F3} {(wh.Count > 0 ? wh[wh.Count / 2] : 0),7:F3} | {over1,4} {over2,4} | {wdesc}");
            r.Solver.Step();
        }
    }

    private static void EnergyStageRun(string[] args)
    {
        string mn = ArgStr(args, "--mat", "rock");
        float grain = ArgFloat(args, "--grain", 30f), speed = ArgFloat(args, "--speed", 2500f), massMul = ArgFloat(args, "--mass", 24f);
        int from = ArgInt(args, "--from", 0), to = ArgInt(args, "--to", 12);
        Material mat = mn switch { "steel" => Material.Steel, "sandstone" => Material.Sandstone, "glass" => Material.Glass, _ => Material.Rock };
        var r = Scenarios.Projectile(SimTuning.Default, mat, speed, massMul, grain);
        SimState s = r.State;
        double KE()
        {
            double e = 0;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) continue;
                int b = s.CellBody[c];
                if (b < 0 || b >= s.BodyCount) continue;
                float si = MathF.Sin(s.BodyRot[b]), co = MathF.Cos(s.BodyRot[b]);
                float rx = s.CellRx[c] * co - s.CellRy[c] * si, ry = s.CellRx[c] * si + s.CellRy[c] * co;
                float dvx = s.CellDvx[c] * co - s.CellDvy[c] * si, dvy = s.CellDvx[c] * si + s.CellDvy[c] * co;
                double vx = s.BodyVx[b] - s.BodyW[b] * ry + dvx, vy = s.BodyVy[b] + s.BodyW[b] * rx + dvy;
                double w = s.BodyW[b] + s.CellDw[c];
                e += 0.5 * s.CellM[c] * (vx * vx + vy * vy) + 0.5 * s.CellIc[c] * w * w;
            }
            return e;
        }
        double e0 = KE(), last = e0; int tick = 0, sub = -1; bool dumped = false;
        Console.WriteLine($"{mn} grain {grain} (built {MathF.Max(grain, BodyBuilder.MinGrain(mat, SimTuning.Default)):F0}) speed {speed} mass {massMul}; initial KE {e0:E3}\n");
        var gain = new Dictionary<SolverPhase, double>();
        r.Solver.PhaseMark = ph =>
        {
            if (ph == SolverPhase.Inertial) sub++;
            double e = KE();
            double d = e - last;
            last = e;
            if (tick < from) return;
            if (d > 0) gain[ph] = (gain.TryGetValue(ph, out double g) ? g : 0) + d;
            if (d > 0.05 * e0 || !double.IsFinite(e))
                Console.WriteLine($"  t{tick} s{sub} after {ph,-14} KE {e,11:E3} ({e / e0,8:F2}x initial)   +{d,11:E3}");
            if (ph == SolverPhase.BondForces && d > 5.0 * e0 && !dumped)
            {
                dumped = true;
                float h2 = Solver.Dt / SimTuning.Default.Substeps;
                int top = -1; float topv = 0f;
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c)) continue;
                    float dv = SimMath.Hypot(s.CellDvx[c], s.CellDvy[c]);
                    if (dv > topv) { topv = dv; top = c; }
                }
                Console.WriteLine($"     hardest-kicked cell {top}: |dv| {topv:E3}, mass {s.CellM[top]:F1}, area {s.CellArea[top]:F1}, bonds:");
                for (int k = 0; k < s.BondCount; k++)
                {
                    if (s.BondA[k] != top && s.BondB[k] != top) continue;
                    int o = s.BondA[k] == top ? s.BondB[k] : s.BondA[k];
                    float wh = MathF.Sqrt(MathF.Max(0f, s.BondK0[k] * (1f / s.CellM[top] + 1f / s.CellM[o]))) * h2;
                    Console.WriteLine($"       bond {k,5} to {o,5}: broken {s.BondBroken[k],-5} w*h {wh:F3}  sn {s.BondSn[k],10:E2} st {s.BondSt[k],10:E2} "
                        + $"sa {s.BondSa[k],10:E2}  S0 {s.BondS0[k]:E2}  dmg {s.BondDmg[k]:F2}  len {s.BondLen[k]:F1}");
                }
            }
        };
        for (tick = 0; tick <= to && double.IsFinite(last); tick++) { sub = -1; r.Solver.Step(); }
        Console.WriteLine("\n  energy CREATED per stage over the window (sum of increases):");
        foreach (var kv in gain.OrderByDescending(x => x.Value))
            Console.WriteLine($"    {kv.Key,-15} {kv.Value,12:E3}  ({kv.Value / e0,8:F2}x initial KE)");
    }

    private static void DivergeRun(string[] args)
    {
        string mn = ArgStr(args, "--mat", "sandstone");
        float grain = ArgFloat(args, "--grain", 60f), speed = ArgFloat(args, "--speed", 4000f);
        float massMul = ArgFloat(args, "--mass", 8f);
        float wBound = ArgFloat(args, "--wbound", 300f);
        Material mat = mn switch
        {
            "glass" => Material.Glass, "rock" => Material.Rock, "ice" => Material.Ice,
            "steel" => Material.Steel, _ => Material.Sandstone,
        };
        var r = Scenarios.Projectile(SimTuning.Default, mat, speed, massMul, grain);
        SimState s = r.State;
        float vBound = 5f * speed;
        Console.WriteLine($"{mn} grain {grain:F0} speed {speed:F0} mass {massMul:F0}; tripwire |w| > {wBound} rad/s or |v| > {vBound} px/s\n");

        int tick = 0, sub = -1, watched = -1, lines = 0;
        int bodiesAtTickStart = s.BodyCount;
        var createdTick = new Dictionary<int, int>();
        string Rot(int b) => $"w {s.BodyW[b],12:G5} wPrev {s.BodyWPrev[b],12:G5} alpha {s.BodyAlpha[b],12:G5} "
            + $"|v| {SimMath.Hypot(s.BodyVx[b], s.BodyVy[b]),10:G4} I {s.BodyI[b],10:G5} M {s.BodyM[b],9:G5} cells {s.BodyCellLen[b]}";
        float lastW = 0f, lastM = 0f, lastV = 0f;
        r.Solver.PhaseMark = ph =>
        {
            if (ph == SolverPhase.Inertial) sub++;
            if (watched < 0)
            {
                if (ph != SolverPhase.Realize) return;               // a settled point in each substep
                for (int b = 0; b < s.BodyCount; b++)
                {
                    if (s.BodyCellLen[b] <= 0 || s.BodyM[b] < 1e-3f) continue;   // retired / placeholder
                    float v = SimMath.Hypot(s.BodyVx[b], s.BodyVy[b]);
                    float gyr = SimMath.Sqrt(SimMath.Max(0f, s.BodyI[b] / s.BodyM[b]));   // radius of gyration
                    float rim = SimMath.Abs(s.BodyW[b]) * gyr;
                    if (float.IsFinite(rim) && float.IsFinite(v) && rim <= vBound && v <= vBound) continue;
                    watched = b;
                    int born = createdTick.TryGetValue(b, out int bt) ? bt : -1;
                    Console.WriteLine($"tick {tick} sub {sub}: body {b} tripped (rim speed {rim:G4}, |v| {v:G4}, gyration radius {gyr:F2} px)");
                    Console.WriteLine($"   created at tick {(born < 0 ? "build" : born.ToString())}");
                    Console.WriteLine($"   {Rot(b)}");
                    lastW = s.BodyW[b]; lastM = s.BodyM[b]; lastV = v;
                    break;
                }
                return;
            }
            if (ph == SolverPhase.BondIntegrate && lines < 60)          // the state Decompose is about to read
            {
                int bb = watched; float M = 0f, mrx = 0f, mry = 0f, px = 0f, py = 0f, L = 0f;
                int o2 = s.BodyCellOff[bb], l2 = s.BodyCellLen[bb];
                for (int i2 = 0; i2 < l2; i2++)
                {
                    int c = s.BodyCells[o2 + i2];
                    if (s.Dead(c)) continue;
                    float mc = s.CellM[c];
                    M += mc; mrx += mc * s.CellRx[c]; mry += mc * s.CellRy[c];
                    px += mc * s.CellDvx[c]; py += mc * s.CellDvy[c];
                    L += mc * (s.CellRx[c] * s.CellDvy[c] - s.CellRy[c] * s.CellDvx[c]) + s.CellIc[c] * s.CellDw[c];
                }
                if (M > 0f)
                {
                    float rcx = mrx / M, rcy = mry / M;
                    float spur = rcx * py - rcy * px;                     // r_com x P
                    float Icom = s.BodyI[bb] - M * (rcx * rcx + rcy * rcy);
                    Console.WriteLine($"        before Decompose: COM off origin by {SimMath.Hypot(rcx, rcy),7:F2} px, |P| {SimMath.Hypot(px, py),10:G4}, "
                        + $"L {L,11:G4}, of which r_com x P {spur,11:G4}  ->  w += {L / MathF.Max(1f, s.BodyI[bb]),10:G4} "
                        + $"(about the COM it would be {(L - spur) / MathF.Max(1f, Icom),10:G4})");
                }
            }
            if (lines >= 60) return;
            float w = s.BodyW[watched], m = s.BodyM[watched], vv = SimMath.Hypot(s.BodyVx[watched], s.BodyVy[watched]);
            bool changed = SimMath.Abs(w - lastW) > 0.02f * SimMath.Max(1f, SimMath.Abs(lastW))
                        || SimMath.Abs(m - lastM) > 1e-4f * SimMath.Max(1f, lastM)
                        || SimMath.Abs(vv - lastV) > 0.02f * SimMath.Max(1f, lastV)
                        || !float.IsFinite(w) || !float.IsFinite(vv);
            if (!changed) return;
            lines++;
            Console.WriteLine($"   t{tick} s{sub} after {ph,-14} {Rot(watched)}");
            lastW = w; lastM = m; lastV = vv;
            if (!float.IsFinite(w) || !float.IsFinite(vv)) lines = 60;
        };
        for (tick = 0; tick < 60 && lines < 60; tick++)
        {
            bodiesAtTickStart = s.BodyCount;
            sub = -1;
            r.Solver.Step();
            for (int b = bodiesAtTickStart; b < s.BodyCount; b++) if (!createdTick.ContainsKey(b)) createdTick[b] = tick;
        }
        if (watched < 0) Console.WriteLine("nothing tripped in 60 ticks");
    }

    private static void NanStageRun(string[] args)
    {
        string mn = ArgStr(args, "--mat", "steel");
        float grain = ArgFloat(args, "--grain", 30f), speed = ArgFloat(args, "--speed", 4000f);
        float massMul = ArgFloat(args, "--mass", 24f);
        Material mat = mn switch
        {
            "glass" => Material.Glass, "rock" => Material.Rock, "ice" => Material.Ice,
            "sandstone" => Material.Sandstone, _ => Material.Steel,
        };
        var r = Scenarios.Projectile(SimTuning.Default, mat, speed, massMul, grain);
        SimState s = r.State;
        Console.WriteLine($"{mn} grain {grain:F0} speed {speed:F0} mass {massMul:F0}: {s.CellCount} cells\n");

        int tick = 0, sub = -1; bool done = false;
        var liveBody = new bool[1];
        string? Check()
        {
            if (liveBody.Length < s.BodyCount) liveBody = new bool[s.BodyCount * 2];
            System.Array.Clear(liveBody, 0, liveBody.Length);
            for (int c = 0; c < s.CellCount; c++)
                if (!s.Dead(c) && s.CellBody[c] >= 0 && s.CellBody[c] < liveBody.Length) liveBody[s.CellBody[c]] = true;
            int bV = 0, bP = 0, bMI = 0, cDv = 0, cDw = 0, cR = 0, cA = 0, kS = 0;
            string firstEnt = "";
            for (int b = 0; b < s.BodyCount; b++)
            {
                if (!liveBody[b]) continue;
                if (!float.IsFinite(s.BodyVx[b]) || !float.IsFinite(s.BodyVy[b]) || !float.IsFinite(s.BodyW[b]))
                { bV++; if (firstEnt == "") firstEnt = $"body {b} v({s.BodyVx[b]},{s.BodyVy[b]}) w {s.BodyW[b]} M {s.BodyM[b]} I {s.BodyI[b]} cells {s.BodyCellLen[b]}"; }
                if (!float.IsFinite(s.BodyX[b]) || !float.IsFinite(s.BodyY[b]) || !float.IsFinite(s.BodyRot[b])) bP++;
                if (!float.IsFinite(s.BodyM[b]) || !float.IsFinite(s.BodyI[b]) || s.BodyM[b] <= 0f || s.BodyI[b] <= 0f)
                { bMI++; if (firstEnt == "") firstEnt = $"body {b} M {s.BodyM[b]} I {s.BodyI[b]} cells {s.BodyCellLen[b]}"; }
            }
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) continue;
                if (!float.IsFinite(s.CellDvx[c]) || !float.IsFinite(s.CellDvy[c]))
                { cDv++; if (firstEnt == "") firstEnt = $"cell {c} body {s.CellBody[c]} dv({s.CellDvx[c]},{s.CellDvy[c]}) area {s.CellArea[c]}"; }
                if (!float.IsFinite(s.CellDw[c])) { cDw++; if (firstEnt == "") firstEnt = $"cell {c} dw {s.CellDw[c]}"; }
                if (!float.IsFinite(s.CellRx[c]) || !float.IsFinite(s.CellRy[c])) cR++;
                if (!float.IsFinite(s.CellArea[c]) || !float.IsFinite(s.CellRad[c])) cA++;
            }
            for (int k = 0; k < s.BondCount; k++)
            {
                if (s.BondBroken[k]) continue;
                if (s.Dead(s.BondA[k]) || s.Dead(s.BondB[k])) continue;
                if (!float.IsFinite(s.BondSn[k]) || !float.IsFinite(s.BondSt[k]) || !float.IsFinite(s.BondSa[k]))
                { kS++; if (firstEnt == "") firstEnt = $"bond {k} cells {s.BondA[k]},{s.BondB[k]} sn {s.BondSn[k]} st {s.BondSt[k]} sa {s.BondSa[k]}"; }
            }
            if (bV + bP + bMI + cDv + cDw + cR + cA + kS == 0) return null;
            return $"bodyV {bV}, bodyPose {bP}, bodyM/I {bMI}, cellDv {cDv}, cellDw {cDw}, cellR {cR}, cellArea {cA}, bondStretch {kS}"
                 + $"\n      e.g. {firstEnt}";
        }

        r.Solver.PhaseMark = ph =>
        {
            if (done) return;
            if (ph == SolverPhase.Inertial) sub++;
            string? bad = Check();
            if (bad == null) return;
            done = true;
            Console.WriteLine($"tick {tick}, substep {sub}: live state first non-finite after stage {ph}");
            Console.WriteLine($"   {bad}");
        };
        for (tick = 0; tick < 60 && !done; tick++) { sub = -1; r.Solver.Step(); }
        if (!done) Console.WriteLine("live state stayed finite through 60 ticks");
    }

    private static void NanTrapRun(string[] args)
    {
        string mn = ArgStr(args, "--mat", "steel");
        float grain = ArgFloat(args, "--grain", 170f);
        float speed = ArgFloat(args, "--speed", 4000f);
        float massMul = ArgFloat(args, "--mass", 24f);
        Material mat = mn switch
        {
            "glass" => Material.Glass, "rock" => Material.Rock, "ice" => Material.Ice,
            "sandstone" => Material.Sandstone, _ => Material.Steel,
        };
        int poison = ArgInt(args, "--poison", 0);
        for (int q = 0; q < poison; q++)
        {
            // Run the configurations that precede this one in the sweep, in the same process, to
            // see whether a scene is affected by what ran before it.
            var pr = Scenarios.Projectile(SimTuning.Default, Material.Steel, 4000f, 24f, 30f);
            for (int i = 0; i < 40; i++) pr.Solver.Step();
        }
        var r = Scenarios.Projectile(SimTuning.Default, mat, speed, massMul, grain);
        SimState s = r.State;
        Console.WriteLine($"{mn} grain {grain:F0} speed {speed:F0} mass {massMul:F0}: {s.CellCount} cells, {s.BondCount} bonds"
            + $" (after {poison} poison run(s))\n");

        bool reportedAny = false;
        for (int i = 0; i < 200; i++)
        {
            r.Solver.Step();
            string? first = null; string detail = "";
            for (int k = 0; k < s.BondCount && first == null; k++)
            {
                if (s.BondBroken[k]) continue;
                if (!float.IsFinite(s.BondSn[k])) { first = "BondSn"; detail = $"bond {k} (cells {s.BondA[k]},{s.BondB[k]}) sn {s.BondSn[k]}"; }
                else if (!float.IsFinite(s.BondSt[k])) { first = "BondSt"; detail = $"bond {k} st {s.BondSt[k]}"; }
                else if (!float.IsFinite(s.BondSa[k])) { first = "BondSa"; detail = $"bond {k} sa {s.BondSa[k]}"; }
            }
            for (int c = 0; c < s.CellCount && first == null; c++)
            {
                string live = s.Dead(c) ? "DEAD" : "live";
                if (!float.IsFinite(s.CellDvx[c]) || !float.IsFinite(s.CellDvy[c]))
                { first = "CellDv"; detail = $"{live} cell {c} dv ({s.CellDvx[c]},{s.CellDvy[c]}) body {s.CellBody[c]}"; }
                else if (!float.IsFinite(s.CellRx[c]) || !float.IsFinite(s.CellRy[c]))
                { first = "CellR"; detail = $"{live} cell {c} r ({s.CellRx[c]},{s.CellRy[c]}) body {s.CellBody[c]}"; }
                else if (!float.IsFinite(s.CellArea[c]) || !float.IsFinite(s.CellRad[c]))
                { first = "CellArea/Rad"; detail = $"{live} cell {c} area {s.CellArea[c]} rad {s.CellRad[c]}"; }
            }
            for (int b = 0; b < s.BodyCount && first == null; b++)
            {
                int nlive = 0;
                for (int c = 0; c < s.CellCount; c++) if (s.CellBody[c] == b && !s.Dead(c)) nlive++;
                if (!float.IsFinite(s.BodyVx[b]) || !float.IsFinite(s.BodyVy[b]) || !float.IsFinite(s.BodyW[b]))
                { first = "BodyV"; detail = $"body {b} ({nlive} live cells) v ({s.BodyVx[b]},{s.BodyVy[b]}) w {s.BodyW[b]} m {s.BodyM[b]} I {s.BodyI[b]}"; }
                else if (!float.IsFinite(s.BodyX[b]) || !float.IsFinite(s.BodyY[b]) || !float.IsFinite(s.BodyRot[b]))
                { first = "BodyPose"; detail = $"body {b} ({nlive} live cells) pose ({s.BodyX[b]},{s.BodyY[b]}) rot {s.BodyRot[b]}"; }
                else if (!float.IsFinite(s.BodyM[b]) || !float.IsFinite(s.BodyI[b]) || s.BodyM[b] <= 0f || s.BodyI[b] <= 0f)
                { first = "BodyM/I"; detail = $"body {b} m {s.BodyM[b]} I {s.BodyI[b]} cells {s.BodyCellLen[b]}"; }
            }
            if (first != null && !reportedAny)
            {
                reportedAny = true;
                Console.WriteLine($"tick {i}: first non-finite ANYWHERE is {first} -> {detail}");
            }
            // Does it ever reach state the tick actually uses?
            string? liveBad = null; string liveDetail = "";
            for (int c = 0; c < s.CellCount && liveBad == null; c++)
            {
                if (s.Dead(c)) continue;
                if (!float.IsFinite(s.CellDvx[c]) || !float.IsFinite(s.CellDvy[c]))
                { liveBad = "CellDv"; liveDetail = $"live cell {c} body {s.CellBody[c]}"; }
                else if (!float.IsFinite(s.CellRx[c]) || !float.IsFinite(s.CellRy[c]) || !float.IsFinite(s.CellArea[c]))
                { liveBad = "CellR/Area"; liveDetail = $"live cell {c} body {s.CellBody[c]}"; }
            }
            for (int b = 0; b < s.BodyCount && liveBad == null; b++)
            {
                int nl = 0;
                for (int c = 0; c < s.CellCount; c++) if (s.CellBody[c] == b && !s.Dead(c)) nl++;
                if (nl == 0) continue;
                if (!float.IsFinite(s.BodyVx[b]) || !float.IsFinite(s.BodyVy[b]) || !float.IsFinite(s.BodyW[b])
                    || !float.IsFinite(s.BodyX[b]) || !float.IsFinite(s.BodyY[b]) || !float.IsFinite(s.BodyRot[b]))
                { liveBad = "Body"; liveDetail = $"body {b} with {nl} live cells: v ({s.BodyVx[b]},{s.BodyVy[b]}) pos ({s.BodyX[b]},{s.BodyY[b]})"; }
            }
            if (liveBad == null) continue;
            Console.WriteLine($"tick {i}: LIVE state went non-finite: {liveBad} -> {liveDetail}");
            Console.WriteLine($"   scene: {s.BodyCount} bodies, {s.CellCount} cells, "
                + $"broken {r.Solver.Broken}, dust {r.Solver.Dust}, maxOverlap {r.Solver.MaxOverlap:F2}");
            return;
        }
        Console.WriteLine("LIVE state stayed finite through 200 ticks");
    }

    private static void OverlapCensusRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 200);
        Console.WriteLine($"contact penetration over {ticks} ticks, as a fraction of the smaller cell's radius\n");
        Console.WriteLine("  scene                | contacts | median | p90    | p99    | max    | max px | worst cell rad");
        foreach (var (name, make) in new (string, Func<Scenarios.Result>)[]
        {
            ("collide rock g900", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f)),
            ("collide rock g255", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 255f)),
            ("collide glass g255", () => Scenarios.Collide(SimTuning.Default, Material.Glass, 600f, 255f)),
            ("projectile rock", () => Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 8f, 900f)),
            ("projectile rock fast", () => Scenarios.Projectile(SimTuning.Default, Material.Rock, 2500f, 8f, 900f)),
        })
        {
            var r = make();
            SimState s = r.State;
            var frac = new List<double>();
            float maxPx = 0f, atRad = 0f;
            for (int i = 0; i < ticks; i++)
            {
                r.Solver.Step();
                for (int q = 0; q < r.Solver.ContactCount; q++)
                {
                    Contact ct = r.Solver.ContactAt(q);
                    if (ct.Depth <= 0f) continue;
                    if (ct.A < 0 || ct.B < 0 || ct.A >= s.CellCount || ct.B >= s.CellCount) continue;
                    float rad = MathF.Min(s.CellRad[ct.A], s.CellRad[ct.B]);
                    if (rad < 1e-3f) continue;
                    frac.Add(ct.Depth / rad);
                    if (ct.Depth > maxPx) { maxPx = ct.Depth; atRad = rad; }
                }
            }
            frac.Sort();
            if (frac.Count == 0) { Console.WriteLine($"  {name,-20} | no penetrating contacts sampled"); continue; }
            double Q(double f) => frac[(int)(f * (frac.Count - 1))];
            Console.WriteLine($"  {name,-20} | {frac.Count,8} | {Q(0.5),6:F3} | {Q(0.9),6:F3} | {Q(0.99),6:F3} | "
                + $"{frac[frac.Count - 1],6:F3} | {maxPx,6:F1} | {atRad,6:F1}");
        }
    }

    private static void NanSweepRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 150);
        int found = 0, tried = 0;
        string only = ArgStr(args, "--only", "");
        var allMats = new (string, Material)[]
        {
            ("rock", Material.Rock), ("glass", Material.Glass), ("steel", Material.Steel),
            ("ice", Material.Ice), ("sandstone", Material.Sandstone),
        };
        var mats = only.Length > 0 ? allMats.Where(x => x.Item1 == only).ToArray() : allMats;
        float onlyG = ArgFloat(args, "--g", 0f), onlyV = ArgFloat(args, "--v", 0f), onlyM = ArgFloat(args, "--m", 0f);
        foreach (var (mn, mat) in mats)
        foreach (float grain in onlyG > 0f ? new[] { onlyG } : new[] { 30f, 60f, 170f, 255f, 900f, 1800f })
        foreach (float speed in onlyV > 0f ? new[] { onlyV } : new[] { 600f, 1500f, 2500f, 4000f })
        foreach (float massMul in onlyM > 0f ? new[] { onlyM } : new[] { 1f, 8f, 24f })
        {
            tried++;
            Scenarios.Result r;
            try { r = Scenarios.Projectile(SimTuning.Default, mat, speed, massMul, grain); }
            catch (Exception e) { Console.WriteLine($"  BUILD THREW {mn} g{grain} v{speed} m{massMul}: {e.GetType().Name}"); found++; continue; }
            SimState s = r.State;
            try
            {
                for (int i = 0; i < ticks; i++)
                {
                    r.Solver.Step();
                    // LIVE state only: a retired cell may hold anything, and counting it produced a
                    // false positive the first time this sweep ran.
                    bool bad = false;
                    for (int c = 0; c < s.CellCount && !bad; c++)
                    {
                        if (s.Dead(c)) continue;
                        int b = s.CellBody[c];
                        bad = !float.IsFinite(s.CellRx[c]) || !float.IsFinite(s.CellRy[c])
                           || !float.IsFinite(s.CellDvx[c]) || !float.IsFinite(s.CellDvy[c])
                           || (b >= 0 && b < s.BodyCount && (!float.IsFinite(s.BodyX[b]) || !float.IsFinite(s.BodyY[b])
                               || !float.IsFinite(s.BodyVx[b]) || !float.IsFinite(s.BodyVy[b])
                               || !float.IsFinite(s.BodyRot[b]) || !float.IsFinite(s.BodyW[b])));
                    }
                    if (!bad) continue;
                    Console.WriteLine($"  NON-FINITE  {mn,-9} grain {grain,6:F0} speed {speed,6:F0} mass {massMul,4:F0}"
                        + $"  at tick {i}  ({s.BodyCount} bodies, {s.CellCount} cells)");
                    found++;
                    break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"  THREW       {mn,-9} grain {grain,6:F0} speed {speed,6:F0} mass {massMul,4:F0}"
                    + $"  : {e.GetType().Name}: {e.Message}");
                found++;
            }
        }
        Console.WriteLine($"\n{tried} configurations, {found} bad");
    }

    private static void PairWatchRun(string[] args)
    {
        int ca = ArgInt(args, "--a", 60), cb = ArgInt(args, "--b", 6);
        int from = ArgInt(args, "--from", 20), to = ArgInt(args, "--to", 45);
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f);
        SimState s = r.State;
        Console.WriteLine($"collide rock/rock, speed 600, grain 900, SimTuning.Default");
        Console.WriteLine($"watching cells {ca} and {cb}; crush threshold {Material.Rock.Crush:E2}\n");
        float a0 = s.CellArea[ca], b0 = s.CellArea[cb];

        Console.WriteLine("  tick | contact? depth |  peak press   threshold | gate open/tries | dents | area a          area b");
        for (int i = 0; i < to; i++)
        {
            int da0 = r.Solver.DentCalls;
            float pressSeen = 0f; int gateOpen = 0, gateShut = 0;
            r.Solver.TraceCell = ca;
            int tk = i;
            r.Solver.DentTrace = Array.IndexOf(args, "--dents") >= 0 && i >= from && i <= from + 12
                ? msg => Console.WriteLine($"t{tk,3} {msg}") : null;
            r.Solver.TraceSink = msg =>
            {
                if (!msg.Contains("carve gate")) return;
                if (msg.Contains("OPEN")) gateOpen++; else gateShut++;
                int ix = msg.IndexOf("press");
                if (ix >= 0 && float.TryParse(msg.Substring(ix + 5, 12).Trim(),
                        System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float pv))
                    pressSeen = MathF.Max(pressSeen, pv);
            };
            r.Solver.Step();
            r.Solver.TraceSink = null;
            if (i < from) continue;

            float depth = float.NaN; bool found = false;
            for (int q = 0; q < r.Solver.ContactCount; q++)
            {
                Contact ct = r.Solver.ContactAt(q);
                if ((ct.A == ca && ct.B == cb) || (ct.A == cb && ct.B == ca)) { depth = ct.Depth; found = true; break; }
            }
            // geometric overlap along the centre line, independent of the manifold
            float dx = (s.BodyX[s.CellBody[ca]] + s.CellRx[ca]) - (s.BodyX[s.CellBody[cb]] + s.CellRx[cb]);
            float dy = (s.BodyY[s.CellBody[ca]] + s.CellRy[ca]) - (s.BodyY[s.CellBody[cb]] + s.CellRy[cb]);
            float sep = SimMath.Hypot(dx, dy);
            float gap = sep - (s.CellRad[ca] + s.CellRad[cb]);

            Console.WriteLine($"  {i,4} | {(found ? $"yes {depth,6:F2}" : $"no  {gap,6:F2}")} | "
                + $"{pressSeen,10:E2} {Material.Rock.Crush,10:E2} | {gateOpen,3}/{gateOpen + gateShut,-3} | "
                + $"{r.Solver.DentCalls - da0,5} | {s.CellArea[ca],8:F1} ({100f * s.CellArea[ca] / a0,5:F1}%) "
                + $"{s.CellArea[cb],8:F1} ({100f * s.CellArea[cb] / b0,5:F1}%)");
        }
    }

    private static void GuardrailSpreadRun(string[] args)
    {
        float[] muls = { 1f, 1.000001f, 1.0001f, 1.001f };
        foreach (var (name, ticks) in new (string, int)[] { ("collide", 400), ("projectile", 400) })
        {
            Console.WriteLine($"\n── {name}, {ticks} ticks, 4 imperceptibly perturbed runs ──");
            Console.WriteLine("  speed x   | bodies | broken | dust | conn% | big% ");
            int bmin = int.MaxValue, bmax = int.MinValue;
            foreach (float mul in muls)
            {
                var r = name == "collide"
                    ? Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 600f * mul)
                    : Scenarios.Projectile(SimTuning.Default, Material.Rock, speed: 900f * mul);
                for (int i = 0; i < ticks; i++) r.Solver.Step();
                SimState s = r.State;
                int broken = (int)r.Solver.Broken;      // the same counter the guardrail asserts on
                // mass in pieces of more than four cells
                var cnt = new Dictionary<int, int>();
                var mass = new Dictionary<int, float>();
                float total = 0f;
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c)) continue;
                    int bb = s.CellBody[c];
                    cnt[bb] = cnt.TryGetValue(bb, out int q) ? q + 1 : 1;
                    float mm = s.CellArea[c];
                    mass[bb] = mass.TryGetValue(bb, out float w) ? w + mm : mm;
                    total += mm;
                }
                float big = 0f;
                foreach (var kv in cnt) if (kv.Value > 4) big += mass[kv.Key];
                bmin = System.Math.Min(bmin, broken); bmax = System.Math.Max(bmax, broken);
                Console.WriteLine($"  {mul,9:F6} | {s.BodyCount,6} | {broken,6} | {r.Solver.Dust,4} | "
                    + $"{"-",5} | {100f * big / MathF.Max(1e-6f, total),4:F0}%");
            }
            Console.WriteLine($"  broken across the ensemble: {bmin}..{bmax}");
        }

        // The shatter stress test needs the scene to actually shatter to prove anything.
        Console.WriteLine("\n── AViolentShatter stress case: bodies after 40 ticks (needs > 400) ──");
        foreach (var (lbl, grain, speed, massMul) in new (string, float, float, float)[]
        {
            ("grain 30, 900 px/s, mass 3 (as written)", 30f, 900f, 3f),
            ("grain 30, 2400 px/s, mass 3", 30f, 2400f, 3f),
            ("grain 30, 2400 px/s, mass 12", 30f, 2400f, 12f),
            ("grain 30, 4000 px/s, mass 24", 30f, 4000f, 24f),
            ("grain 20, 4000 px/s, mass 24", 20f, 4000f, 24f),
            ("grain 15, 4000 px/s, mass 24", 15f, 4000f, 24f),
            ("grain 20, 6000 px/s, mass 24", 20f, 6000f, 24f),
        })
        {
            var swx = Stopwatch.StartNew();
            var r = Scenarios.Projectile(SimTuning.Default, Material.Rock, speed, massMul, grain);
            for (int i = 0; i < 40; i++) r.Solver.Step();
            swx.Stop();
            Console.WriteLine($"  {lbl,-40} -> {r.State.BodyCount,5} bodies, {r.Solver.Broken,5} broken, "
                + $"{r.Solver.Dust,4} dust, {swx.Elapsed.TotalSeconds,5:F1}s");
        }
    }

    private static void SmallRoundRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 120);
        float baseR = ArgFloat(args, "--radius", 26f);
        float baseV = ArgFloat(args, "--speed", 900f);

        static (float lost, int bodies, int dead, float impMass, float ke) Shot(
            float radius, float rhoMul, float speed, int ticks2, float massWanted = 0f)
        {
            var tune = SimTuning.Default;
            var rk = Material.Rock;
            var round = new Material("round", rk.Rho * rhoMul, rk.C, rk.Strain, rk.Chi, rk.Yield,
                rk.Duct, rk.Crush, rk.CrushRate, rk.ShedLimit, rk.Dent);
            var r = Scenarios.Collide(tune, Material.Rock, 0f, 900f);
            SimState s = r.State;
            int before = s.CellCount;
            float area0 = 0f;
            for (int c = 0; c < before; c++) area0 += s.CellArea[c];
            float tx = s.BodyX[0], ty = s.BodyY[0];
            r = Scenarios.FireAt(r, tune, round, tx - 400f, ty, tx, ty,
                speed: speed, radius: radius, grain: 900f, seed: 1, roundMass: massWanted);
            s = r.State;
            int rb = s.CellBody[before];
            float m = s.BodyM[rb];
            for (int i = 0; i < ticks2; i++) r.Solver.Step();
            float area1 = 0f; int dead = 0;
            for (int c = 0; c < before; c++)                       // target cells only
            {
                if (s.Dead(c)) { dead++; continue; }
                area1 += s.CellArea[c];
            }
            return (100f * (area0 - area1) / area0, s.BodyCount, dead, m, 0.5f * m * speed * speed);
        }

        var (lost0, bodies0, dead0, m0, ke0) = Shot(baseR, 1f, baseV, ticks);
        Console.WriteLine($"target: rock g900. Baseline round radius {baseR:F0} px at {baseV:F0} px/s, mass {m0:F0}.\n");
        Console.WriteLine("  round                              | radius | mass      | KE        | target area lost | bodies | dead");
        Console.WriteLine($"  {"baseline",-34} | {baseR,6:F1} | {m0,9:F0} | {ke0,9:E2} | {lost0,15:F1}% | {bodies0,6} | {dead0,4}");
        foreach (var (name, radius, rhoMul, speed, wanted) in new (string, float, float, float, float)[]
        {
            ("half size, density x4", baseR / 2f, 4f, baseV, 0f),
            ("half size, mass set directly", baseR / 2f, 1f, baseV, m0),
            ("third size, mass set directly", baseR / 3f, 1f, baseV, m0),
            ("quarter size, mass set directly", baseR / 4f, 1f, baseV, m0),
            ("sixth size, mass set directly", baseR / 6f, 1f, baseV, m0),
            ("half size, nothing compensated", baseR / 2f, 1f, baseV, 0f),
        })
        {
            var (lost, bodies, dead, m, ke) = Shot(radius, rhoMul, speed, ticks, wanted);
            string err = wanted > 0f ? $"  (asked {wanted:F0}, err {100f * (m - wanted) / wanted:+0.00;-0.00}%)" : "";
            Console.WriteLine($"  {name,-34} | {radius,6:F1} | {m,9:F0} | {ke,9:E2} | {lost,15:F1}% | {bodies,6} | {dead,4}{err}");
        }
    }

    private static void FuseCheckRun(string[] args)
    {
        float speed = ArgFloat(args, "--speed", 900f);
        var tune = SimTuning.Default;
        var r = Scenarios.Collide(tune, Material.Rock, 0f, 900f);
        SimState s = r.State;
        int before = s.CellCount;
        float tx = s.BodyX[0], ty = s.BodyY[0];
        r = Scenarios.FireAt(r, tune, Material.Rock, tx - 500f, ty, tx, ty, speed: speed, radius: 20f, grain: 900f, seed: 1);
        s = r.State;
        int rb = s.CellBody[before];
        float v0 = SimMath.Hypot(s.BodyVx[rb], s.BodyVy[rb]);
        int touchTick = -1, slowTick = -1, overlapTick = -1;

        for (int i = 0; i < 200; i++)
        {
            r.Solver.Step();
            if (rb >= s.BodyCount) break;
            if (touchTick < 0 && r.Solver.BodyTouchedThisTick(rb)) touchTick = i;
            if (slowTick < 0 && SimMath.Hypot(s.BodyVx[rb], s.BodyVy[rb]) < 0.99f * v0) slowTick = i;
            if (overlapTick < 0)
            {
                float rmnx = float.MaxValue, rmxx = float.MinValue, rmny = float.MaxValue, rmxy = float.MinValue;
                float omnx = float.MaxValue, omxx = float.MinValue, omny = float.MaxValue, omxy = float.MinValue;
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.Dead(c)) continue;
                    int bb = s.CellBody[c];
                    float wx = s.BodyX[bb] + s.CellRx[c], wy = s.BodyY[bb] + s.CellRy[c];
                    float rr = s.CellRad[c];
                    if (bb == rb) { rmnx = MathF.Min(rmnx, wx - rr); rmxx = MathF.Max(rmxx, wx + rr); rmny = MathF.Min(rmny, wy - rr); rmxy = MathF.Max(rmxy, wy + rr); }
                    else { omnx = MathF.Min(omnx, wx - rr); omxx = MathF.Max(omxx, wx + rr); omny = MathF.Min(omny, wy - rr); omxy = MathF.Max(omxy, wy + rr); }
                }
                if (rmxx >= omnx && omxx >= rmnx && rmxy >= omny && omxy >= rmny) overlapTick = i;
            }
            if (touchTick >= 0 && slowTick >= 0 && overlapTick >= 0) break;
        }
        Console.WriteLine($"round fired at {speed:F0} px/s from 500 px away");
        Console.WriteLine($"   solver reported first contact at tick : {touchTick}");
        Console.WriteLine($"   round first lost 1% of its speed at   : {slowTick}");
        Console.WriteLine($"   AABBs first overlapped at             : {overlapTick}");
        Console.WriteLine(touchTick >= 0 && slowTick >= 0 && System.Math.Abs(touchTick - slowTick) <= 1
            ? "   -> contact report agrees with the round actually being slowed"
            : "   -> MISMATCH");
    }

    private static void BondInterfaceRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 200);
        foreach (var (label, make) in new (string, Func<Scenarios.Result>)[]
        {
            ("collide rock g900", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f)),
            ("collide rock g170", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 170f)),
            ("collide glass g255", () => Scenarios.Collide(SimTuning.Default, Material.Glass, 600f, 255f)),
            ("projectile rock", () => Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 8f, 900f)),
            ("viewer piercing rod + knobs", () =>
            {
                var pen = Material.Penetrator;
                var rod = new Material(pen.Name, pen.Rho, pen.C, pen.Strain, pen.Chi, pen.Yield, pen.Duct,
                    pen.Crush / 64f, pen.CrushRate * 64f, MathF.Min(0.95f, pen.ShedLimit * 64f), pen.Dent);
                var rr = Scenarios.Collide(SimTuning.Default, Material.Rock, 0f, 900f);
                float tx = rr.State.BodyX[0], ty = rr.State.BodyY[0];
                float rad = 15f * MathF.Sqrt(3f);
                return Scenarios.FireAt(rr, SimTuning.Default, rod, tx - 360f, ty, tx, ty,
                    speed: 900f, radius: rad, grain: 900f, seed: 1, radiusX: rad * 3.5f, radiusY: rad / 3.5f);
            }),
        })
        {
            var r = make();
            SimState s = r.State;
            int nb = s.BondCount;
            var len0 = new float[nb]; var k0 = new float[nb]; var s0 = new float[nb];
            for (int k = 0; k < nb; k++) { len0[k] = s.BondLen[k]; k0[k] = s.BondK0[k]; s0[k] = s.BondS0[k]; }

            int grew = 0;
            var prevLen = (float[])s.BondLen.Clone();
            for (int i = 0; i < ticks; i++)
            {
                r.Solver.Step();
                for (int k = 0; k < nb && k < s.BondCount; k++)
                {
                    if (!s.BondBroken[k] && s.BondLen[k] > prevLen[k] + 1e-4f) grew++;
                    prevLen[k] = s.BondLen[k];
                }
            }

            // 1. stiffness vs length, on bonds whose interface actually eroded
            double worst = 0; int eroded = 0; int s0Changed = 0;
            for (int k = 0; k < nb && k < s.BondCount; k++)
            {
                if (s.BondBroken[k] || len0[k] <= 1e-6f || k0[k] <= 1e-9f) continue;
                float lr = s.BondLen[k] / len0[k];
                if (lr > 0.999f) continue;
                eroded++;
                double kr = s.BondK0[k] / k0[k];
                worst = System.Math.Max(worst, System.Math.Abs(kr - lr));
                if (System.Math.Abs(s.BondS0[k] - s0[k]) > 1e-9f) s0Changed++;
            }

            // 2. anything still bonded with no interface left
            var hasRec = new bool[s.BondCount];
            for (int rr = 0; rr < s.TouchCount; rr++)
            {
                if (s.TouchA[rr] < 0) continue;
                short bk = s.TouchBond[rr];
                if (bk >= 0 && bk < s.BondCount) hasRec[bk] = true;
            }
            int glue = 0;
            for (int k = 0; k < s.BondCount; k++)
            {
                if (s.BondBroken[k] || hasRec[k]) continue;
                int a = s.BondA[k], b = s.BondB[k];
                if (a < 0 || b < 0 || s.Dead(a) || s.Dead(b)) continue;
                glue++;
            }

            Console.WriteLine($"\n── {label} after {ticks} ticks ──");
            Console.WriteLine($"   bonds whose interface eroded: {eroded}");
            Console.WriteLine($"   worst |k/k0 - len/len0| among them: {worst:E2}   (claim: telescopes exactly)");
            Console.WriteLine($"   of those, bonds whose failure stretch S0 moved: {s0Changed}   (claim: never)");
            Console.WriteLine($"   times a live bond's length INCREASED: {grew}   (claim: never; rescale only fires on shrink)");
            Console.WriteLine($"   live bonds joining two live cells with NO touch record: {glue}   (claim: none)");

            // The build sets Ka0 = K0 * L^2 / 12 (second moment of a line of springs over length L).
            // Does the carved-interface rescale keep that?
            double worstBend = 1.0; int bendChecked = 0; double atF = 1.0;
            for (int k = 0; k < nb && k < s.BondCount; k++)
            {
                if (s.BondBroken[k] || len0[k] <= 1e-6f) continue;
                float lr = s.BondLen[k] / len0[k];
                if (lr > 0.999f) continue;
                double want = s.BondK0[k] * (double)s.BondLen[k] * s.BondLen[k] / 12.0;
                if (want <= 1e-12) continue;
                double ratio = s.BondKa0[k] / want;
                bendChecked++;
                if (ratio > worstBend) { worstBend = ratio; atF = lr; }
            }
            if (bendChecked > 0)
                Console.WriteLine($"   Ka0 / (K0*L^2/12) on {bendChecked} eroded bonds: worst {worstBend:F2}x too stiff "
                    + $"at interface {atF * 100:F1}% (1/f^2 predicts {1.0 / (atF * atF):F2}x)");

            // How far down can a live bond's interface be eroded and still hold?
            var lenRatios = new List<double>();
            for (int k = 0; k < nb && k < s.BondCount; k++)
            {
                if (s.BondBroken[k] || len0[k] <= 1e-6f) continue;
                int a = s.BondA[k], b = s.BondB[k];
                if (a < 0 || b < 0 || s.Dead(a) || s.Dead(b)) continue;
                lenRatios.Add(s.BondLen[k] / len0[k]);
            }
            lenRatios.Sort();
            if (lenRatios.Count > 0)
                Console.WriteLine($"   live bond interface remaining: min {lenRatios[0] * 100:F1}%, "
                    + $"p10 {lenRatios[lenRatios.Count / 10] * 100:F1}%, median {lenRatios[lenRatios.Count / 2] * 100:F1}%");

            // Two-cell bodies: how many, and what holds them together.
            var cellsOf = new Dictionary<int, List<int>>();
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) continue;
                int bb = s.CellBody[c];
                if (!cellsOf.TryGetValue(bb, out var l)) cellsOf[bb] = l = new List<int>();
                l.Add(c);
            }
            int two = 0, twoNoBond = 0, twoNoRec = 0; double worstTwo = 1.0;
            foreach (var kv in cellsOf.OrderBy(x => x.Key))
            {
                if (kv.Value.Count != 2) continue;
                two++;
                int a = kv.Value[0], b = kv.Value[1];
                int found = -1;
                for (int k = 0; k < s.BondCount; k++)
                {
                    if (s.BondBroken[k]) continue;
                    if ((s.BondA[k] == a && s.BondB[k] == b) || (s.BondA[k] == b && s.BondB[k] == a)) { found = k; break; }
                }
                if (found < 0) { twoNoBond++; continue; }
                bool rec = false;
                for (int rr = 0; rr < s.TouchCount; rr++)
                    if (s.TouchA[rr] >= 0 && s.TouchBond[rr] == found) { rec = true; break; }
                if (!rec) twoNoRec++;
                if (found < nb && len0[found] > 1e-6f) worstTwo = System.Math.Min(worstTwo, s.BondLen[found] / len0[found]);
            }
            Console.WriteLine($"   two-cell bodies: {two}; of those {twoNoBond} have NO live bond between their cells, "
                + $"{twoNoRec} have a bond but no record; thinnest surviving interface {worstTwo * 100:F1}%");
        }
    }

    private static void PartBRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 120);
        Console.WriteLine($"after {ticks} ticks\n");
        Console.WriteLine("  scene                | dents | v1 fallback | carves >60 deg off approach | morphology");
        foreach (var (name, make) in new (string, Func<Scenarios.Result>)[]
        {
            ("collide rock g900", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f)),
            ("collide rock g255", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 255f)),
            ("collide steel g170", () => Scenarios.Collide(SimTuning.Default, Material.Steel, 600f, 170f)),
            ("projectile rock", () => Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 8f, 900f)),
            ("collide glass g255", () => Scenarios.Collide(SimTuning.Default, Material.Glass, 600f, 255f)),
        })
        {
            var r = make();
            r.Solver.MeasureCarveAngles = true;
            r.Solver.DentLoneCells = Array.IndexOf(args, "--v1lone") < 0;
            for (int i = 0; i < 10; i++) r.Solver.Step();          // warm
            var sw = Stopwatch.StartNew();
            for (int i = 10; i < ticks; i++) r.Solver.Step();
            sw.Stop();
            int live = 0, dead = 0; float areaLeft = 0f;
            for (int c = 0; c < r.State.CellCount; c++) { if (r.State.Dead(c)) dead++; else { live++; areaLeft += r.State.CellArea[c]; } }
            int tot = 0; foreach (int v in r.Solver.CarveNormalAngle) tot += v;
            int off60 = r.Solver.CarveNormalAngle[6] + r.Solver.CarveNormalAngle[7] + r.Solver.CarveNormalAngle[8];
            int calls = r.Solver.DentCalls, fb = r.Solver.DentFallback;
            Console.WriteLine($"  {name,-20} | {calls,5} | {fb,5} ({100.0 * fb / System.Math.Max(1, calls),4:F1}%) | "
                + $"{off60,6} of {tot,6} ({100.0 * off60 / System.Math.Max(1, tot),4:F1}%)  | "
                + $"{r.State.BodyCount,4} bodies {live,4} live {dead,4} dead | {sw.Elapsed.TotalMilliseconds / (ticks - 10),5:F2} ms/tick");
        }
    }

    private static void DentCapacityRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 60);

        static double Capacity(float[] xs, float[] ys, int[] touch, int n)
        {
            double cap = 0;
            double gx = 0, gy = 0;
            for (int v = 0; v < n; v++) { gx += xs[v]; gy += ys[v]; }
            gx /= n; gy /= n;
            for (int v = 0; v < n; v++)
            {
                int pv = v == 0 ? n - 1 : v - 1, nv = v + 1 == n ? 0 : v + 1;
                if (touch[pv] >= 0 || touch[v] >= 0) continue;
                double kx = xs[v] - gx, ky = ys[v] - gy;
                double kl = System.Math.Sqrt(kx * kx + ky * ky);
                if (kl < 1e-4) continue;
                double ux = -kx / kl, uy = -ky / kl;
                double p1x = xs[pv] - gx, p1y = ys[pv] - gy;
                double p2x = xs[nv] - gx, p2y = ys[nv] - gy;
                double cx = p2x - p1x, cy = p2y - p1y;
                double cross = ux * cy - uy * cx;
                double da = 0.5 * System.Math.Abs(cross);
                if (da <= 1e-6) continue;
                double toChord = -((kx - p1x) * cy - (ky - p1y) * cx) / cross;
                if (toChord <= 0) continue;
                cap += da * toChord;
            }
            return cap;
        }

        foreach (var (label, r) in new (string, Scenarios.Result)[]
        {
            ("collide rock g900", Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f)),
            ("collide rock g255", Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 255f)),
            ("collide steel g170", Scenarios.Collide(SimTuning.Default, Material.Steel, 600f, 170f)),
        })
        {
            SimState s = r.State;
            for (int i = 0; i < ticks; i++) r.Solver.Step();
            var ratios = new List<double>();
            int tested = 0, better = 0;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c) || s.CellArea[c] <= 1e-3f) continue;
                int off = s.PolyOff[c], n = s.PolyLen[c];
                if (n < 4) continue;
                var xs = new float[n + 1]; var ys = new float[n + 1]; var tc = new int[n + 1];
                for (int v = 0; v < n; v++) { xs[v] = s.PolyX[off + v]; ys[v] = s.PolyY[off + v]; tc[v] = s.SideTouch[off + v]; }
                double cap0 = Capacity(xs, ys, tc, n);
                if (cap0 <= 1e-6) continue;

                // Insert a midpoint on the longest SURFACE edge — the one a contact is most likely
                // to land in the middle of, and the only kind we may re-mesh (a record side is
                // shared geometry).
                int bestE = -1; float bestL = 0f;
                for (int v = 0; v < n; v++)
                {
                    if (tc[v] >= 0) continue;
                    int nv = v + 1 == n ? 0 : v + 1;
                    float L = SimMath.Hypot(xs[nv] - xs[v], ys[nv] - ys[v]);
                    if (L > bestL) { bestL = L; bestE = v; }
                }
                if (bestE < 0) continue;

                var x2 = new float[n + 1]; var y2 = new float[n + 1]; var t2 = new int[n + 1];
                int k2 = 0;
                for (int v = 0; v < n; v++)
                {
                    x2[k2] = xs[v]; y2[k2] = ys[v]; t2[k2] = tc[v]; k2++;
                    if (v != bestE) continue;
                    int nv = v + 1 == n ? 0 : v + 1;
                    x2[k2] = 0.5f * (xs[v] + xs[nv]); y2[k2] = 0.5f * (ys[v] + ys[nv]); t2[k2] = -1; k2++;
                }
                double cap1 = Capacity(x2, y2, t2, k2);
                tested++;
                if (cap1 > cap0) better++;
                ratios.Add(cap1 / cap0);
            }
            ratios.Sort();
            Console.WriteLine($"\n── {label}: {tested} cells with a free corner and a surface edge ──");
            if (tested == 0) continue;
            Console.WriteLine($"   capacity after inserting one midpoint, relative to before:"
                + $"  median {ratios[ratios.Count / 2]:F3}   p25 {ratios[ratios.Count / 4]:F3}   p75 {ratios[3 * ratios.Count / 4]:F3}");
            Console.WriteLine($"   cells where the insertion INCREASED capacity: {better} of {tested} ({100.0 * better / tested:F1}%)");
        }
    }

    private static void DentBudgetRun(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 120);
        Console.WriteLine($"realised / target area over {ticks} ticks\n");
        Console.WriteLine("  scene                      | single pass + clamp |  iterated (shipping) ");
        foreach (var (name, make) in new (string, Func<Scenarios.Result>)[]
        {
            ("collide rock g900", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 900f)),
            ("collide rock g255", () => Scenarios.Collide(SimTuning.Default, Material.Rock, 600f, 255f)),
            ("collide steel g170", () => Scenarios.Collide(SimTuning.Default, Material.Steel, 600f, 170f)),
            ("projectile rock", () => Scenarios.Projectile(SimTuning.Default, Material.Rock, 900f, 8f, 900f)),
        })
        {
            var outp = new double[2];
            string bound = "";
            for (int mode2 = 0; mode2 < 2; mode2++)
            {
                var r = make();
                r.Solver.MeasureDentBudget = true;
                r.Solver.DentSinglePass = mode2 == 0;
                for (int i = 0; i < ticks; i++) r.Solver.Step();
                outp[mode2] = 100.0 * r.Solver.DentRealisedSum / System.Math.Max(1e-9, r.Solver.DentTargetSum);
                if (mode2 == 1) bound = $"| capacity-bound on {r.Solver.DentCapacityBound} of {r.Solver.DentTotalSolved} dents "
                    + $"({100.0 * r.Solver.DentCapacityBound / System.Math.Max(1, r.Solver.DentTotalSolved):F1}%)";
            }
            Console.WriteLine($"  {name,-26} | {outp[0],18:F1}% | {outp[1],18:F1}%   {bound}");
        }
    }

    private static void DentPickRun(string[] args)
    {
        float grain = ArgFloat(args, "--grain", 900f);
        int ticks = ArgInt(args, "--ticks", 120);
        Console.WriteLine($"rock collide, grain {grain}, {ticks} ticks — sweeping Material.Dent on the meanEdge base\n");
        Console.WriteLine("  Dent | bodies | dead | live | median aspect | area kept | dents | clamped/solved");
        foreach (float d in new[] { 0.40f, 0.55f, 0.75f, 1.00f, 1.25f, 1.50f, 2.00f })
        {
            var rock = Material.Rock;
            var m = new Material(rock.Name, rock.Rho, rock.C, rock.Strain, rock.Chi, rock.Yield,
                rock.Duct, rock.Crush, rock.CrushRate, rock.ShedLimit, d);
            var r = Scenarios.Collide(SimTuning.Default, m, 600f, grain);
            SimState s = r.State;
            float area0 = 0f;
            for (int c = 0; c < s.CellCount; c++) area0 += s.CellArea[c];
            for (int i = 0; i < ticks; i++) r.Solver.Step();

            var aspects = new List<double>();
            int dead = 0, live = 0; float area1 = 0f;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) { dead++; continue; }
                live++; area1 += s.CellArea[c];
                int off = s.PolyOff[c], n = s.PolyLen[c];
                if (n < 4) continue;
                double diam = 0; int ai = 0, bi = 0;
                for (int v = 0; v < n; v++)
                    for (int w = v + 1; w < n; w++)
                    {
                        double dx = s.PolyX[off + v] - s.PolyX[off + w], dy = s.PolyY[off + v] - s.PolyY[off + w];
                        double dd = System.Math.Sqrt(dx * dx + dy * dy);
                        if (dd > diam) { diam = dd; ai = v; bi = w; }
                    }
                double axx = s.PolyX[off + bi] - s.PolyX[off + ai], axy = s.PolyY[off + bi] - s.PolyY[off + ai];
                double al = System.Math.Sqrt(axx * axx + axy * axy);
                double lo = double.MaxValue, hi = double.MinValue;
                for (int v = 0; v < n; v++)
                {
                    double pr = (s.PolyX[off + v] * -axy + s.PolyY[off + v] * axx) / System.Math.Max(1e-9, al);
                    lo = System.Math.Min(lo, pr); hi = System.Math.Max(hi, pr);
                }
                aspects.Add(diam / System.Math.Max(1e-6, hi - lo));
            }
            aspects.Sort();
            double medA = aspects.Count > 0 ? aspects[aspects.Count / 2] : double.NaN;
            int cl = r.Solver.DentClamped, un = r.Solver.DentUnclamped;
            Console.WriteLine($"  {d,4:F2} | {s.BodyCount,6} | {dead,4} | {live,4} | {medA,13:F2} | {100f * area1 / area0,8:F1}% | "
                + $"{r.Solver.DentCalls,5} | {cl,5}/{cl + un}");
        }
    }

    private static void DentScaleRun(string[] args)
    {
        var tune = SimTuning.Default;
        var scenes = new (string, Scenarios.Result)[]
        {
            ("collide rock g900", Scenarios.Collide(tune, Material.Rock, 600f, 900f)),
            ("collide rock g255", Scenarios.Collide(tune, Material.Rock, 600f, 255f)),
            ("collide steel g170", Scenarios.Collide(tune, Material.Steel, 600f, 170f)),
        };
        string[] names = { "CellRad (now)", "diameter", "meanEdge=perim/n", "sqrt(area)" };

        foreach (var (label, r) in scenes)
        {
            SimState s = r.State;
            for (int i = 0; i < 60; i++) r.Solver.Step();      // let carving make the shapes irregular
            var ratios = new List<double>[4];
            for (int j = 0; j < 4; j++) ratios[j] = new List<double>();
            var elong = new List<double>[4];
            for (int j = 0; j < 4; j++) elong[j] = new List<double>();
            int cells = 0, elongCells = 0;

            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) continue;
                int off = s.PolyOff[c], n = s.PolyLen[c];
                if (n < 4) continue;
                // typical spacing between neighbouring vertices
                var nn = new List<double>();
                for (int v = 0; v < n; v++)
                {
                    double best = double.MaxValue;
                    for (int w = 0; w < n; w++)
                    {
                        if (w == v) continue;
                        double dx = s.PolyX[off + v] - s.PolyX[off + w], dy = s.PolyY[off + v] - s.PolyY[off + w];
                        best = System.Math.Min(best, System.Math.Sqrt(dx * dx + dy * dy));
                    }
                    nn.Add(best);
                }
                nn.Sort();
                double spacing = nn[nn.Count / 2];
                if (spacing < 1e-3) continue;

                double diam = 0; int ai = 0, bi = 0;
                for (int v = 0; v < n; v++)
                    for (int w = v + 1; w < n; w++)
                    {
                        double dx = s.PolyX[off + v] - s.PolyX[off + w], dy = s.PolyY[off + v] - s.PolyY[off + w];
                        double d = System.Math.Sqrt(dx * dx + dy * dy);
                        if (d > diam) { diam = d; ai = v; bi = w; }
                    }
                // width = extent perpendicular to the diameter axis
                double axx = s.PolyX[off + bi] - s.PolyX[off + ai], axy = s.PolyY[off + bi] - s.PolyY[off + ai];
                double al = System.Math.Sqrt(axx * axx + axy * axy);
                double lo = double.MaxValue, hi = double.MinValue;
                for (int v = 0; v < n; v++)
                {
                    double pr = (s.PolyX[off + v] * -axy + s.PolyY[off + v] * axx) / System.Math.Max(1e-9, al);
                    lo = System.Math.Min(lo, pr); hi = System.Math.Max(hi, pr);
                }
                double aspect = diam / System.Math.Max(1e-6, hi - lo);

                double[] sc = { s.CellRad[c], diam, s.CellPerim[c] / n, System.Math.Sqrt(s.CellArea[c]) };
                cells++;
                bool isElong = aspect >= 2.0;
                if (isElong) elongCells++;
                for (int j = 0; j < 4; j++)
                {
                    ratios[j].Add(sc[j] / spacing);
                    if (isElong) elong[j].Add(sc[j] / spacing);
                }
            }

            Console.WriteLine($"\n── {label}: {cells} live cells ({elongCells} elongated, aspect >= 2) ──");
            Console.WriteLine("  scale               scale/spacing: MEDIAN  IQR/med   min     max  |  elongated-only med  IQR/med");
            for (int j = 0; j < 4; j++)
            {
                static (double med, double iqr) Robust(List<double> xs)
                {
                    var a = xs.OrderBy(x => x).ToArray();
                    double Q(double f) { double i = f * (a.Length - 1); int lo2 = (int)i; int hi2 = System.Math.Min(a.Length - 1, lo2 + 1); return a[lo2] + (i - lo2) * (a[hi2] - a[lo2]); }
                    return (Q(0.5), Q(0.75) - Q(0.25));
                }
                var (med, iqr) = Robust(ratios[j]);
                var (emed, eiqr) = elong[j].Count > 0 ? Robust(elong[j]) : (double.NaN, double.NaN);
                Console.WriteLine($"  {names[j],-20} {med,8:F2} {100 * iqr / med,6:F1}% {ratios[j].Min(),7:F2} {ratios[j].Max(),7:F2}"
                    + $"  |  {emed,10:F2} {100 * eiqr / emed,6:F1}%");
            }
        }
    }

    private static void TipTraceRun(string[] args)
    {
        float grain = ArgFloat(args, "--grain", 900f);
        float speed = ArgFloat(args, "--speed", 900f);
        float mass = ArgFloat(args, "--mass", 3f);
        int ticks = ArgInt(args, "--ticks", 30);
        bool plain = Array.IndexOf(args, "--plain") >= 0;

        var tune = SimTuning.Default;
        Material pen = Material.Penetrator;
        float dentOverride = ArgFloat(args, "--dentval", -1f);
        float dentUse = dentOverride > 0f ? dentOverride : pen.Dent;
        Material rod = plain
            ? new Material(pen.Name, pen.Rho, pen.C, pen.Strain, pen.Chi, pen.Yield, pen.Duct,
                pen.Crush, pen.CrushRate, pen.ShedLimit, dentUse)
            : new Material(pen.Name, pen.Rho, pen.C, pen.Strain, pen.Chi, pen.Yield,
                pen.Duct, pen.Crush / 64f, pen.CrushRate * 64f, MathF.Min(0.95f, pen.ShedLimit * 64f), dentUse);
        Console.WriteLine($"grain {grain}, speed {speed}, mass {mass}; rod material {(plain ? "Penetrator as-is" : "Penetrator + knobs")}:");
        Console.WriteLine($"   crush {rod.Crush:E2} (base {pen.Crush:E2}), rate {rod.CrushRate:F3} (base {pen.CrushRate:F3}), "
            + $"shedLimit {rod.ShedLimit:F2} (base {pen.ShedLimit:F2}), dent {rod.Dent:F2}");

        var r = Scenarios.Collide(tune, Material.Rock, 0f, grain);
        SimState s = r.State;
        int before = s.CellCount;
        float tx = s.BodyX[0], ty = s.BodyY[0];
        float radius = 15f * MathF.Sqrt(mass);
        r = Scenarios.FireAt(r, tune, rod, tx - 360f, ty, tx, ty,
            speed: speed, radius: radius, grain: grain, seed: 1,
            radiusX: radius * 3.5f, radiusY: radius / 3.5f);
        s = r.State;

        int rodBody = s.CellBody[before];
        int front = -1; float bestX = float.NegativeInfinity;
        Console.WriteLine($"\nrod is body {rodBody}, radius {radius:F1} -> rx {radius * 3.5f:F1} ry {radius / 3.5f:F1}");
        for (int c = before; c < s.CellCount; c++)
        {
            if (s.CellBody[c] != rodBody) continue;
            Console.WriteLine($"   cell {c}: {s.PolyLen[c]} verts, area {s.CellArea[c]:F0}, rad {s.CellRad[c]:F1}, local ({s.CellRx[c]:F1},{s.CellRy[c]:F1})");
            if (s.CellRx[c] > bestX) { bestX = s.CellRx[c]; front = c; }
        }
        Console.WriteLine($"front cell is {front}; its vertices (cell-local, centroid at origin):");
        int fo = s.PolyOff[front];
        for (int v = 0; v < s.PolyLen[front]; v++)
            Console.WriteLine($"   v{v}: ({s.PolyX[fo + v],7:F2},{s.PolyY[fo + v],7:F2})  {r.Solver.ClassifyVertex(front, v)}");

        r.Solver.TraceCell = front;
        r.Solver.MeasureCarveAngles = true;
        r.Solver.DentSinglePass = Array.IndexOf(args, "--singlepass") >= 0;
        r.Solver.MeasureDentBudget = true;
        int shown = 0;
        int tick = 0;
        int traceUntil = ArgInt(args, "--trace", 10);
        r.Solver.DentTrace = msg => { if (tick <= traceUntil && shown < 200) { Console.WriteLine($"t{tick,3} {msg}"); shown++; } };

        float a0 = s.CellArea[front];
        Console.WriteLine("\n  tick | area | verts | tip x | half-width | shape (local x,y per vertex)");
        for (tick = 0; tick < ticks; tick++)
        {
            int d0 = r.Solver.DentCalls;
            r.Solver.Step();
            if (s.Dead(front)) { Console.WriteLine($"t{tick,3} front cell {front} is DEAD"); break; }
            if (r.Solver.DentCalls == d0 && s.CellArea[front] >= a0 - 1e-3f) continue;
            int off = s.PolyOff[front], len = s.PolyLen[front];
            float mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue;
            var sb = new System.Text.StringBuilder();
            for (int v = 0; v < len; v++)
            {
                float vx3 = s.PolyX[off + v], vy3 = s.PolyY[off + v];
                mxx = MathF.Max(mxx, vx3); mny = MathF.Min(mny, vy3); mxy = MathF.Max(mxy, vy3);
                sb.Append($"({vx3,6:F1},{vy3,5:F1})");
            }
            if (false) { }
            Console.WriteLine($"  {tick,4} | {s.CellArea[front],5:F0} ({100f * s.CellArea[front] / a0,5:F1}%) | {len,2} | "
                + $"{mxx,6:F1} | {0.5f * (mxy - mny),6:F1} | {sb}");
        }

        double tot = 0, totS = 0;
        foreach (double v in r.Solver.DentAreaByU) tot += v;
        foreach (double v in r.Solver.DentSlideByU) totS += v;
        string[] band = { "u < 0.1  (at the contact)", "u 0.1-0.3", "u 0.3-0.6", "u > 0.6  (far side)" };
        Console.WriteLine($"\nwhere the front cell's dented area went (total {tot:F0} px2, total slide {totS:F1} px):");
        for (int b = 0; b < 4; b++)
            Console.WriteLine($"   {band[b],-26} area {r.Solver.DentAreaByU[b],8:F0} ({100.0 * r.Solver.DentAreaByU[b] / System.Math.Max(1e-9, tot),5:F1}%)"
                + $"   slide {r.Solver.DentSlideByU[b],7:F1} ({100.0 * r.Solver.DentSlideByU[b] / System.Math.Max(1e-9, totS),5:F1}%)");
        Console.WriteLine($"corner slides clamped by toChord: {r.Solver.DentClamped} of {r.Solver.DentClamped + r.Solver.DentUnclamped}");
        Console.WriteLine($"corners reached but already on/past their chord (capacity lost): "
            + $"{r.Solver.DentCornerAtChord} of {r.Solver.DentCornerAtChord + r.Solver.DentCornerUsable}");
        Console.WriteLine($"budget realised over the run: {100.0 * r.Solver.DentRealisedSum / System.Math.Max(1e-9, r.Solver.DentTargetSum):F1}%"
            + $" ({(r.Solver.DentSinglePass ? "single pass + clamp" : "iterated")})");
        double pd = r.Solver.PressDyn, pc = r.Solver.PressConf, pr = r.Solver.PressDrive;
        double pt = pd + pc + pr;
        Console.WriteLine($"\nfront cell's contact pressure, summed over {r.Solver.PressSamples} contact-substeps:");
        Console.WriteLine($"   dyn   (impulse that brakes the approach; velocity) {pd,12:E3}  ({100.0 * pd / System.Math.Max(1e-9, pt),5:F1}%)");
        Console.WriteLine($"   conf  (penetration strain x modulus; DEPTH)        {pc,12:E3}  ({100.0 * pc / System.Math.Max(1e-9, pt),5:F1}%)");
        Console.WriteLine($"   drive (own deviation velocity x impedance)         {pr,12:E3}  ({100.0 * pr / System.Math.Max(1e-9, pt),5:F1}%)");
        Console.WriteLine($"   contact-substeps with pen == 0 (no depth term at all): {r.Solver.PressZeroPen} of {r.Solver.PressSamples}");
    }

    private static void ViewerRodRun(string[] args)
    {
        float mass = ArgFloat(args, "--mass", 1f), grain = ArgFloat(args, "--grain", 900f);
        float speed = ArgFloat(args, "--speed", 1800f);
        var tune = SimTuning.Default;
        var r = Scenarios.Collide(tune, Material.Rock, 0f, grain);
        SimState s = r.State;
        int before = s.CellCount;

        float radius = 15f * MathF.Sqrt(mass);
        bool round = Array.IndexOf(args, "--round") >= 0;
        float rx = round ? radius : radius * 3.5f, ry = round ? radius : radius / 3.5f;
        Console.WriteLine($"viewer rod: mass {mass}, grain {grain}, speed {speed} -> radius {radius:F1}, rx {rx:F1}, ry {ry:F1}");

        // Aim at a real body, the way a click in the viewer does.
        Console.WriteLine($"scene has {s.BodyCount} bodies, {s.CellCount} cells");
        for (int b = 0; b < s.BodyCount; b++)
        {
            float mnx = float.MaxValue, mxx = float.MinValue, mny = float.MaxValue, mxy = float.MinValue; int nc = 0;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.CellBody[c] != b || s.Dead(c)) continue;
                nc++;
                float wx = s.BodyX[b] + s.CellRx[c], wy = s.BodyY[b] + s.CellRy[c];
                mnx = MathF.Min(mnx, wx); mxx = MathF.Max(mxx, wx);
                mny = MathF.Min(mny, wy); mxy = MathF.Max(mxy, wy);
            }
            Console.WriteLine($"  body {b}: origin ({s.BodyX[b]:F0},{s.BodyY[b]:F0}) {nc} cells, "
                + $"world x [{mnx:F0},{mxx:F0}] y [{mny:F0},{mxy:F0}]");
        }
        float tx = s.BodyX[0], ty = s.BodyY[0];
        Console.WriteLine($"target body 0 centre ({tx:F0},{ty:F0})");
        r = Scenarios.FireAt(r, tune, Material.Penetrator, tx - 420f, ty, tx, ty,
            speed: speed, radius: radius, grain: grain, seed: 1, radiusX: rx, radiusY: ry);
        s = r.State;
        int rodBody = s.CellBody[before];
        int rodCells = 0;
        for (int c = before; c < s.CellCount; c++) if (s.CellBody[c] == rodBody) rodCells++;
        Console.WriteLine($"rod is body {rodBody} with {rodCells} cell(s):");
        for (int c = before; c < s.CellCount; c++)
        {
            if (s.CellBody[c] != rodBody) continue;
            Console.WriteLine($"   cell {c}: {s.PolyLen[c]} verts, area {s.CellArea[c]:F0}, rad {s.CellRad[c]:F1}, "
                + $"local ({s.CellRx[c]:F1},{s.CellRy[c]:F1})");
        }

        var a0 = new float[s.CellCount];
        for (int c = 0; c < s.CellCount; c++) a0[c] = s.CellArea[c];
        int lastDent = 0, lastFb = 0;
        int rodCell = -1;
        for (int c = before; c < s.CellCount; c++) if (s.CellBody[c] == rodBody) { rodCell = c; break; }
        r.Solver.TraceCell = rodCell;
        int f0 = r.Solver.DentFallback, c0 = r.Solver.DentCalls;

        // The rod flies +x, so its axis is x. For every contact the rod is in, how far is the
        // carving normal from that axis? 0 deg = pushing on the tip; 90 deg = clipping the flanks.
        Console.WriteLine("\n  tick | rod contacts | normal angle to the rod axis (deg) | depth | rod area left");
        int axial = 0, lateral = 0;
        for (int i = 0; i < 90; i++)
        {
            r.Solver.Step();
            int n = 0; float angSum = 0f, dSum = 0f; float worst = 0f;
            for (int q = 0; q < r.Solver.ContactCount; q++)
            {
                Contact ct = r.Solver.ContactAt(q);
                bool aIn = ct.A >= 0 && ct.A < s.CellCount && s.CellBody[ct.A] == rodBody;
                bool bIn = ct.B >= 0 && ct.B < s.CellCount && s.CellBody[ct.B] == rodBody;
                if (!aIn && !bIn) continue;
                float ang = MathF.Abs(MathF.Atan2(MathF.Abs(ct.Ny), MathF.Abs(ct.Nx))) * 180f / MathF.PI;
                n++; angSum += ang; dSum += ct.Depth; if (ang > worst) worst = ang;
                if (ang > 45f) lateral++; else axial++;
            }
            float left = 0f, was = 0f;
            for (int c = 0; c < s.CellCount; c++)
            { if (s.CellBody[c] != rodBody) continue; was += a0[c]; if (!s.Dead(c)) left += s.CellArea[c]; }
            if (i % 5 != 0 && n == 0) continue;
            string angTxt = n > 0 ? $"mean {angSum / n,6:F1}  worst {worst,6:F1}" : "          (cleared)";
            Console.WriteLine($"  {i,4} | {n,12} | {angTxt}       | {(n > 0 ? dSum / n : 0f),5:F2} | {100f * left / MathF.Max(1f, was),5:F1}%"
                + $"  v1 along {r.Solver.CarveV1Along,6:F0} across {r.Solver.CarveV1Across,6:F0}"
                + $"  rod at ({s.BodyX[rodBody],7:F0},{s.BodyY[rodBody],6:F0}) v ({s.BodyVx[rodBody],7:F0},{s.BodyVy[rodBody],6:F0})"
                + $"  dents {r.Solver.DentCalls - lastDent,4} fallbacks {r.Solver.DentFallback - lastFb,3} pairs {r.Solver.C.PairEmitted,5} sat {r.Solver.C.SatCalls,5} satContact {r.Solver.C.SatContact,4}");
            lastDent = r.Solver.DentCalls; lastFb = r.Solver.DentFallback;
            // Independent overlap check: rod polygon vs body 0 cells, both in world space.
            {
                float bc = MathF.Cos(s.BodyRot[rodBody]), bs = MathF.Sin(s.BodyRot[rodBody]);
                int ro = s.PolyOff[rodCell], rl = s.PolyLen[rodCell];
                float rminx = float.MaxValue, rmaxx = float.MinValue, rminy = float.MaxValue, rmaxy = float.MinValue;
                for (int v = 0; v < rl; v++)
                {
                    float px2 = s.CellRx[rodCell] + s.PolyX[ro + v], py2 = s.CellRy[rodCell] + s.PolyY[ro + v];
                    float wx = s.BodyX[rodBody] + px2 * bc - py2 * bs, wy = s.BodyY[rodBody] + px2 * bs + py2 * bc;
                    rminx = MathF.Min(rminx, wx); rmaxx = MathF.Max(rmaxx, wx);
                    rminy = MathF.Min(rminy, wy); rmaxy = MathF.Max(rmaxy, wy);
                }
                int overlapping = 0;
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.CellBody[c] == rodBody || s.Dead(c)) continue;
                    int bb = s.CellBody[c];
                    float cc2 = MathF.Cos(s.BodyRot[bb]), ss2 = MathF.Sin(s.BodyRot[bb]);
                    int co2 = s.PolyOff[c], cl2 = s.PolyLen[c];
                    float mnx2 = float.MaxValue, mxx2 = float.MinValue, mny2 = float.MaxValue, mxy2 = float.MinValue;
                    for (int v = 0; v < cl2; v++)
                    {
                        float px2 = s.CellRx[c] + s.PolyX[co2 + v], py2 = s.CellRy[c] + s.PolyY[co2 + v];
                        float wx = s.BodyX[bb] + px2 * cc2 - py2 * ss2, wy = s.BodyY[bb] + px2 * ss2 + py2 * cc2;
                        mnx2 = MathF.Min(mnx2, wx); mxx2 = MathF.Max(mxx2, wx);
                        mny2 = MathF.Min(mny2, wy); mxy2 = MathF.Max(mxy2, wy);
                    }
                    if (rmaxx >= mnx2 && mxx2 >= rminx && rmaxy >= mny2 && mxy2 >= rminy) overlapping++;
                }
                int liveForeign = 0;
                float fmnx = float.MaxValue, fmxx = float.MinValue, fmny = float.MaxValue, fmxy = float.MinValue;
                for (int c = 0; c < s.CellCount; c++)
                {
                    if (s.CellBody[c] == rodBody || s.Dead(c)) continue;
                    liveForeign++;
                    int bb = s.CellBody[c];
                    float wx = s.BodyX[bb] + s.CellRx[c], wy = s.BodyY[bb] + s.CellRy[c];
                    fmnx = MathF.Min(fmnx, wx); fmxx = MathF.Max(fmxx, wx);
                    fmny = MathF.Min(fmny, wy); fmxy = MathF.Max(fmxy, wy);
                }
                Console.WriteLine($"         rod world aabb x [{rminx:F0},{rmaxx:F0}] y [{rminy:F0},{rmaxy:F0}]"
                    + $"  -> {overlapping} foreign cells overlap it; {liveForeign} live foreign cells span "
                    + $"x [{fmnx:F0},{fmxx:F0}] y [{fmny:F0},{fmxy:F0}]");
            }
        }
        Console.WriteLine($"\nrod contacts by normal: {axial} axial (<45 deg, pushing the tip), "
            + $"{lateral} lateral (>45 deg, clipping the flanks)");
        Console.WriteLine($"DentCalls {r.Solver.DentCalls - c0}, DentFallback (lone cell -> carving v1) {r.Solver.DentFallback - f0}");
        double al = r.Solver.CarveV1Along, ac = r.Solver.CarveV1Across;
        Console.WriteLine($"v1 clip area by direction in the cell's own frame: along the long axis {al:F0} "
            + $"({100.0 * al / System.Math.Max(1e-9, al + ac):F1}%), ACROSS it {ac:F0} "
            + $"({100.0 * ac / System.Math.Max(1e-9, al + ac):F1}%)");
        int alive = 0; for (int c = 0; c < s.CellCount; c++) if (s.CellBody[c] == rodBody && !s.Dead(c)) alive++;
        Console.WriteLine($"rod cells alive: {alive} of {rodCells}");
    }

    private static void TipRun(string[] args)
    {
        var tn = SimTuning.Default;
        var r = Scenarios.Pierce(tn, Material.Rock, ArgInt(args, "--speed", 1800), 3f, 900f);
        SimState s = r.State;

        // The rod is body 1, flying +x; its tip is the cell with the largest local x.
        int tip = -1; float best = float.NegativeInfinity;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.CellBody[c] != 1) continue;
            if (s.CellRx[c] > best) { best = s.CellRx[c]; tip = c; }
        }
        var a0 = new float[s.CellCount];
        for (int c = 0; c < s.CellCount; c++) a0[c] = s.CellArea[c];

        Console.WriteLine($"rod tip is cell {tip} ({s.PolyLen[tip]} verts, area {s.CellArea[tip]:F0}, rad {s.CellRad[tip]:F1})");
        for (int v = 0; v < s.PolyLen[tip]; v++)
            Console.WriteLine($"   vertex {v}: {r.Solver.ClassifyVertex(tip, v)}");

        // Which cells actually receive erosion, and were they in contact when they did?
        var everContact = new bool[s.CellCount];
        var eroded = new float[s.CellCount];
        Console.WriteLine("\n  tick | tip shed% | rod shed by position (front third / middle / back third) | contacts on tip");
        for (int i = 1; i <= 60; i++)
        {
            r.Solver.Step();
            for (int q = 0; q < r.Solver.ContactCount; q++)
            { var ct = r.Solver.ContactAt(q); if (ct.A < s.CellCount) everContact[ct.A] = true; if (ct.B < s.CellCount) everContact[ct.B] = true; }
            if (i % 6 != 0) continue;
            float f = 0f, m = 0f, b = 0f; float fa = 0f, ma = 0f, ba = 0f;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int c = 0; c < s.CellCount; c++) { if (s.CellBody[c] != 1 || s.Dead(c)) continue; lo = SimMath.Min(lo, s.CellRx[c]); hi = SimMath.Max(hi, s.CellRx[c]); }
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.CellBody[c] != 1 || s.Dead(c)) continue;
                float u = hi > lo ? (s.CellRx[c] - lo) / (hi - lo) : 0.5f;
                float lost = a0[c] - s.CellArea[c];
                if (u > 0.667f) { f += lost; fa += a0[c]; } else if (u > 0.333f) { m += lost; ma += a0[c]; } else { b += lost; ba += a0[c]; }
            }
            int tc = 0;
            for (int q = 0; q < r.Solver.ContactCount; q++) { var ct = r.Solver.ContactAt(q); if (ct.A == tip || ct.B == tip) tc++; }
            float tipShed = a0[tip] > 0 ? 100f * (a0[tip] - (s.Dead(tip) ? 0f : s.CellArea[tip])) / a0[tip] : 0f;
            Console.WriteLine($"  {i,4} | {tipShed,8:F1}% | {100f * f / SimMath.Max(1f, fa),10:F1}% {100f * m / SimMath.Max(1f, ma),8:F1}% {100f * b / SimMath.Max(1f, ba),8:F1}% | {tc,4}");
        }

        int erodedTouched = 0, erodedNever = 0; float areaTouched = 0f, areaNever = 0f;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.CellBody[c] != 1 && !(s.Dead(c) && a0[c] > 0f)) continue;
            float lost = a0[c] - (s.Dead(c) ? 0f : s.CellArea[c]);
            if (lost <= 0.01f * a0[c]) continue;
            if (everContact[c]) { erodedTouched++; areaTouched += lost; }
            else
            {
                erodedNever++; areaNever += lost;
                Console.WriteLine($"    cell {c,4} lost {100f * lost / a0[c],5:F1}% of its area, never contacted anything"
                    + $" (local x {s.CellRx[c],7:F1}, y {s.CellRy[c],7:F1})");
            }
        }
        Console.WriteLine($"\nrod cells that eroded: {erodedTouched} were in contact ({areaTouched:F0} area), "
            + $"{erodedNever} NEVER were ({areaNever:F0} area)");
    }

    /// <summary>What does an elongated MakeBlob actually tessellate into?</summary>
    private static void RodShape(string[] args)
    {
        float grain = ArgInt(args, "--grain", 170);
        foreach (var (label, rx, ry) in new (string, float, float)[]
        { ("round  r=73", 73f, 73f), ("rod 2:1", 103f, 52f), ("rod 3.5:1", 257f, 21f), ("rod 6:1", 440f, 12f) })
        {
            var s = new SimState();
            var rng = new ProtoRng(7);
            var outline = BodyBuilder.MakeBlob(ref rng, 400, 350, rx, ry, 0.10, 12);
            BodyBuilder.AddBody(s, ref rng, SimTuning.Default, outline, 0f, 0f, 0f, Material.Steel, grain);
            float maxRad = 0f, maxArea = 0f, sumArea = 0f; int worst = -1;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.CellRad[c] > maxRad) { maxRad = s.CellRad[c]; worst = c; }
                maxArea = SimMath.Max(maxArea, s.CellArea[c]);
                sumArea += s.CellArea[c];
            }
            float step = SimMath.Sqrt(grain);
            Console.WriteLine($"{label,-12} ({rx:F0}x{ry:F0}): {s.CellCount,3} cells, step {step:F0}px  |  "
                + $"max CellRad {maxRad,8:F1}px  max cell area {maxArea,9:F0}  total area {sumArea,9:F0} "
                + $"(outline area {SimMath.Abs((float)Geometry2D.Area(outline)),9:F0})");
            if (worst >= 0 && maxRad > 5f * step)
                Console.WriteLine($"             worst cell {worst}: {s.PolyLen[worst]} verts, rad {maxRad:F1}px = {maxRad / step:F1}x the seed step");
        }
    }

    /// <summary>Spawn a round INSIDE a body, as the viewer does on an unlucky click, and watch the cost.</summary>
    private static void SpawnInside(string[] args)
    {
        var tn = SimTuning.Default;
        float grain = ArgInt(args, "--grain", 900);
        if (Array.IndexOf(args, "--nocrackpush") >= 0) tn.CrackPush = false;
        if (ArgInt(args, "--cap", -1) >= 0) tn.CrackPushCap = ArgInt(args, "--cap", 400) / 100f;
        float mass = ArgInt(args, "--mass", 3);
        float rad = 15f * SimMath.Sqrt(mass);
        bool rod = Array.IndexOf(args, "--rod") >= 0;
        foreach (var (label, dx) in new (string, float)[]
        { ("outside (control)", -600f), ("INSIDE the target", 0f) })
        {
            var r = Scenarios.Collide(tn, Material.Rock, 0f, grain);
            SimState s = r.State;
            for (int i = 0; i < 5; i++) r.Solver.Step();

            float tx = 650f, ty = 350f;
            Console.WriteLine($"   [round r={rad:F0}px{(rod ? $", rod {rad * 3.5f:F0}x{rad / 3.5f:F0}" : "")}, grain {grain:F0}, mass x{mass:F0}]");
            r = Scenarios.FireAt(r, tn, rod ? Material.Penetrator : Material.Steel, tx + dx, ty, tx, ty,
                                 speed: ArgInt(args, "--speed", 2500), radius: rad, grain: grain, seed: 7,
                                 radiusX: rod ? rad * 3.5f : 0f, radiusY: rod ? rad / 3.5f : 0f);

            Console.WriteLine($"── {label}");
            Console.WriteLine("   tick |  pairs   SAT calls  contacts | manifold rebuilds | ms/tick");
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 1; i <= 40; i++)
            {
                var before = r.Solver.C;
                sw.Restart(); r.Solver.Step(); sw.Stop();
                if (i % 5 != 0 && i > 3) continue;
                var c = r.Solver.C;
                Console.WriteLine($"   {i,4} | {r.Solver.PairCount,6}  {c.SatCalls - before.SatCalls,9}  {r.Solver.ContactCount,8} | "
                    + $"{c.ManifoldRebuilds - before.ManifoldRebuilds,3} | {sw.Elapsed.TotalMilliseconds,6:F1} | "
                    + $"examined {c.NarrowExamined - before.NarrowExamined,7}  same-body {c.NarrowRejectSameBody - before.NarrowRejectSameBody,7}  "
                    + $"radius {c.NarrowRejectRadius - before.NarrowRejectRadius,6}  aabb {c.NarrowRejectAabb - before.NarrowRejectAabb,6}  "
                    + $"sat-sep {c.SatSeparated - before.SatSeparated,6}");
                float mr = 0f, ma = 0f; int worst = -1;
                for (int cc = 0; cc < s.CellCount; cc++)
                {
                    if (s.Dead(cc)) continue;
                    if (s.CellRad[cc] > mr) { mr = s.CellRad[cc]; worst = cc; }
                    ma = SimMath.Max(ma, s.CellArea[cc]);
                }
                float maxSn = 0f, maxV = 0f; int nan = 0, brokenCompressed = 0;
                for (int k = 0; k < s.BondCount; k++)
                {
                    float sn = s.BondSn[k];
                    if (float.IsNaN(sn) || float.IsInfinity(sn)) nan++;
                    else if (s.BondBroken[k] && sn < 0f) { brokenCompressed++; maxSn = SimMath.Max(maxSn, -sn); }
                }
                for (int b = 0; b < s.BodyCount; b++)
                {
                    float v = SimMath.Hypot(s.BodyVx[b], s.BodyVy[b]);
                    if (float.IsNaN(v) || float.IsInfinity(v)) nan++; else maxV = SimMath.Max(maxV, v);
                }
                Console.WriteLine($"        crack compression: {brokenCompressed} broken bonds pressed, deepest {maxSn,10:F1}px  "
                    + $"| max body speed {maxV,10:F0} px/s | NaN/Inf {nan}");
                Console.WriteLine($"        max CellRad {mr,10:F1}px (cell {worst}, {(worst >= 0 ? s.PolyLen[worst] : 0)} verts, "
                    + $"area {(worst >= 0 ? s.CellArea[worst] : 0f),10:F0}, body {(worst >= 0 ? s.CellBody[worst] : -1)})   max area {ma,10:F0}");
            }
        }
    }

    /// <summary>Does an eroded interface carry less force? Built length vs current, and the stiffness.</summary>
    private static void BondLenRun(string[] args)
    {
        var tn = SimTuning.Default;
        var r = Scenarios.Collide(tn, Material.Rock, 600f, 170f);
        SimState s = r.State;
        var len0 = new float[s.BondCount];
        for (int k = 0; k < s.BondCount; k++) len0[k] = s.BondLen[k];
        var k00 = new float[s.BondCount];
        for (int k = 0; k < s.BondCount; k++) k00[k] = s.BondK0[k];

        for (int i = 0; i < 200; i++) r.Solver.Step();

        int eroded = 0, badK = 0, badS = 0; float worst = 1f;
        for (int k = 0; k < s.BondCount; k++)
        {
            if (s.BondBroken[k] || len0[k] <= 0f) continue;
            float frac = s.BondLen[k] / len0[k];
            if (frac > 0.95f) continue;
            eroded++;
            worst = SimMath.Min(worst, frac);
            if (s.BondK0[k] == k00[k]) badK++;      // stiffness never followed the interface
            badS++;
        }
        Console.WriteLine($"live bonds whose interface shrank below 95% of built: {eroded}");
        Console.WriteLine($"  of those, stiffness still at its built value: {badK}   (narrowest interface now {100f * worst:F0}% of built)");

        // Two-cell fragments specifically: one bond holding the whole body.
        int twoCell = 0, twoCellEroded = 0; float twoWorst = 1f;
        for (int b = 0; b < s.BodyCount; b++)
        {
            if (s.BodyCellLen[b] != 2) continue;
            twoCell++;
            int off = s.BodyCellOff[b];
            int a = s.BodyCells[off], c = s.BodyCells[off + 1];
            for (int k = 0; k < s.BondCount; k++)
            {
                if (s.BondBroken[k]) continue;
                if ((s.BondA[k] != a || s.BondB[k] != c) && (s.BondA[k] != c || s.BondB[k] != a)) continue;
                float frac = len0[k] > 0f ? s.BondLen[k] / len0[k] : 1f;
                if (frac < 0.95f) { twoCellEroded++; twoWorst = SimMath.Min(twoWorst, frac); }
            }
        }
        Console.WriteLine($"two-cell bodies: {twoCell}, of which the holding bond has eroded: {twoCellEroded} (narrowest {100f * twoWorst:F0}%)");
    }

    /// <summary>Hunt the reported crash: every scenario, wide speed and grain range.</summary>
    private static void CrashHunt(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 150);
        var mats = new[] { Material.Rock, Material.Glass, Material.Steel, Material.Ice, Material.Sandstone };
        int runs = 0, fails = 0;
        foreach (float speed in new[] { 1500f, 3500f, 6000f })
        foreach (float grain in new[] { 170f, 900f })
        foreach (var m in mats)
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            string what = $"kind {kind} {m.Name} speed {speed:F0} grain {grain:F0}";
            try
            {
                var t = SimTuning.Default;
                Scenarios.Result r = kind switch
                {
                    0 => Scenarios.Projectile(t, m, speed, 3f, grain, impactor: Material.Steel),
                    1 => Scenarios.Pierce(t, m, speed, 3f, grain),
                    2 => Scenarios.Shell(t, Material.Steel, m, speed, 3f, grain),
                    _ => Scenarios.Blast(Scenarios.Collide(t, m, speed * 0.3f, grain), t, 370f, 350f, 6e5f, 300f),
                };
                runs++;
                for (int i = 0; i < ticks; i++) r.Solver.Step();
            }
            catch (Exception ex)
            {
                fails++;
                Console.WriteLine($"CRASH  {what}");
                Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
                var st = ex.StackTrace?.Split('\n');
                if (st != null) foreach (var line in st.Take(4)) Console.WriteLine("       " + line.Trim());
            }
        }
        Console.WriteLine($"── {runs} runs, {fails} crashes");
    }

    /// <summary>Does per-body structure reach the physics? Bond strength spread and crack direction.</summary>
    private static void StructureRun(string[] args)
    {
        foreach (var (label, wm, an, fl, lockg) in new (string, float, float, float, bool)[]
        {
            ("uniform          ", 0f,  0f,   0f,   false),
            ("weibull 8        ", 8f,  0f,   0f,   false),
            ("weibull 3 (wide) ", 3f,  0f,   0f,   false),
            ("aniso 0.6 locked ", 8f,  0.6f, 0f,   true),
            ("surface flaws 0.5", 8f,  0f,   0.5f, false),
        })
        {
            var tn = SimTuning.Default;
            tn.WeibullM = wm; tn.Aniso = an; tn.SurfFlaw = fl; tn.GrainLock = lockg; tn.GrainAngle = 0f;
            var r = Scenarios.Collide(tn, Material.Rock, 600f, 170f);
            SimState s = r.State;

            // Bond strength spread as built, then how the scene actually breaks.
            float lo = float.MaxValue, hi = 0f, mean = 0f;
            for (int k = 0; k < s.BondCount; k++) { lo = SimMath.Min(lo, s.BondStr[k]); hi = SimMath.Max(hi, s.BondStr[k]); mean += s.BondStr[k]; }
            mean /= SimMath.Max(1, s.BondCount);

            for (int i = 0; i < 200; i++) r.Solver.Step();

            // Direction of the breaks: |cos| of the bond normal against the grain axis, averaged.
            int broken = 0; float along = 0f;
            for (int k = 0; k < s.BondCount; k++)
            {
                if (!s.BondBroken[k]) continue;
                broken++;
                along += SimMath.Abs(s.BondNx[k]);        // grain angle 0 => grain along x
            }
            Console.WriteLine($"{label}: BondStr {lo:F2}-{hi:F2} (mean {mean:F2})  ->  broken {broken,4}, bodies {s.BodyCount,3}, "
                + $"mean |cos| of break normal to grain {(broken > 0 ? along / broken : 0f):F3}");
        }
    }

    /// <summary>The impact types against one another: what each does to the same target.</summary>
    private static void ImpactTypes(string[] args)
    {
        int ticks = ArgInt(args, "--ticks", 120);
        var t = SimTuning.Default;

        // A crater profile: how much of the target is lost, and how deep the damage reaches.
        static string Profile(Scenarios.Result r, int ticks, Func<int, Scenarios.Result>? perTick = null)
        {
            SimState s = r.State;
            float a0 = 0f;
            for (int c = 0; c < s.CellCount; c++) if (s.CellBody[c] == 0) a0 += s.CellArea0[c];
            for (int i = 0; i < ticks; i++) { r.Solver.Step(); if (perTick != null) r = perTick(i); }
            float shed = 0f; int dead = 0, broken = 0;
            // Depth INTO the target: damaged cells run from the struck face inward, so the deepest
            // is the largest local x (the impactor arrives from -x).
            float face = float.PositiveInfinity, deepest = float.NegativeInfinity;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.Dead(c)) dead++;
                if (s.CellBody[c] != 0) continue;
                float lost = s.Dead(c) ? s.CellArea0[c] : s.CellArea0[c] - s.CellArea[c];
                shed += lost;
                if (lost > 0.02f * s.CellArea0[c])
                {
                    face = SimMath.Min(face, s.CellRx[c]);
                    deepest = SimMath.Max(deepest, s.CellRx[c]);
                }
            }
            for (int k = 0; k < s.BondCount; k++) if (s.BondBroken[k]) broken++;
            string reach = deepest > face ? $"{face,5:F0} to {deepest,4:F0}px (depth {deepest - face,4:F0})" : "  none";
            return $"bodies {s.BodyCount,3}  broken {broken,4}  dead {dead,3}  target shed {100f * shed / SimMath.Max(1f, a0),5:F1}%  damaged span {reach}";
        }

        Console.WriteLine($"── same target (blob at 560,350 r190), {ticks} ticks");
        Console.WriteLine($"   bullet   1800px/s x3 : {Profile(Scenarios.Projectile(t, Material.Rock, 1800f, 3f, 900f, impactor: Material.Steel), ticks)}");
        Console.WriteLine($"   pierce   1800px/s x3 : {Profile(Scenarios.Pierce(t, Material.Rock, 1800f, 3f, 900f), ticks)}");
        foreach (float pres in new[] { 1e5f, 3e5f, 1e6f })
        {
            var bl = Scenarios.Collide(t, Material.Rock, 0f, 900f);
            var blasted = Scenarios.Blast(bl, t, 370f, 350f, pres, 260f);
            Console.WriteLine($"   blast    p={pres,7:E1} : {Profile(blasted, ticks)}");
        }

        // Explosive round: the bullet path, then a blast at wherever the round got to. Composition
        // by the caller — Blast stays a primitive that knows nothing about fuses.
        {
            int fuse = ArgInt(args, "--fuse", 6);
            var ex = Scenarios.Projectile(t, Material.Rock, 1800f, 3f, 900f, impactor: Material.Steel);
            int shot = ex.State.BodyCount - 1;
            Console.WriteLine($"   explosive fuse {fuse,2}   : {Profile(ex, ticks, i =>
            {
                if (i != fuse - 1) return ex;
                SimState es = ex.State;
                float bx = shot < es.BodyCount ? es.BodyX[shot] : 0f, by = shot < es.BodyCount ? es.BodyY[shot] : 0f;
                return ex = Scenarios.Blast(ex, t, bx, by, 3e5f, 260f);
            })}");
        }
    }

    /// <summary>Steel shell over a rock core: does the shell resist while the core crushes?</summary>
    private static void ShellRun(string[] args)
    {
        var r = Scenarios.Shell(SimTuning.Default, Material.Steel, Material.Rock,
                                speed: ArgInt(args, "--speed", 1500), massMul: 8f, grain: 900f);
        SimState s = r.State;
        byte steelId = 255;
        for (int i = 0; i < s.MatCount; i++) if (s.MatTable[i].Name == "steel") { steelId = (byte)i; break; }
        int nShell = 0, nCore = 0;
        for (int c = 0; c < s.CellCount; c++) { if (s.CellBody[c] != 0) continue; if (s.CellMat[c] == steelId) nShell++; else nCore++; }
        int tss = 0, tcc = 0, tsc = 0;
        for (int k = 0; k < s.BondCount; k++)
        {
            if (s.CellBody[s.BondA[k]] != 0) continue;
            bool a = s.CellMat[s.BondA[k]] == steelId, b = s.CellMat[s.BondB[k]] == steelId;
            if (a && b) tss++; else if (!a && !b) tcc++; else tsc++;
        }
        Console.WriteLine($"target: {nShell} shell cells, {nCore} core cells; bonds: {tss} shell-shell, {tcc} core-core, {tsc} seam");
        Console.WriteLine("  tick | shell dead  core dead | shell shed%  core shed% | broken: shell-shell core-core seam");
        for (int tick = 1; tick <= 60; tick++)
        {
            r.Solver.Step();
            if (tick % 10 != 0) continue;
            int ds = 0, dc = 0; float ss = 0f, sc = 0f, as0 = 0f, ac0 = 0f;
            for (int c = 0; c < s.CellCount; c++)
            {
                if (s.CellBody[c] < 0) continue;
                bool isShell = s.CellMat[c] == steelId;
                if (s.Dead(c)) { if (isShell) ds++; else dc++; }
                if (isShell) { as0 += s.CellArea0[c]; ss += s.CellArea0[c] - (s.Dead(c) ? 0f : s.CellArea[c]); }
                else { ac0 += s.CellArea0[c]; sc += s.CellArea0[c] - (s.Dead(c) ? 0f : s.CellArea[c]); }
            }
            int bss = 0, bcc = 0, bsc = 0;
            for (int k = 0; k < s.BondCount; k++)
            {
                if (!s.BondBroken[k]) continue;
                bool a = s.CellMat[s.BondA[k]] == steelId, b = s.CellMat[s.BondB[k]] == steelId;
                if (a && b) bss++; else if (!a && !b) bcc++; else bsc++;
            }
            Console.WriteLine($"  {tick,4} | {ds,10} {dc,10} | {100f * ss / SimMath.Max(1f, as0),10:F1} {100f * sc / SimMath.Max(1f, ac0),10:F1} | "
                + $"{100f * bss / SimMath.Max(1, tss),12:F0}% {100f * bcc / SimMath.Max(1, tcc),8:F0}% {100f * bsc / SimMath.Max(1, tsc),4:F0}%");
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
