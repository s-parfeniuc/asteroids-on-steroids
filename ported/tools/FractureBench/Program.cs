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
                + $"{mp.CrushCap.ToString("E2", ci),13}");
        }

        // ── A. pressure vs static overlap ────────────────────────────────────
        // Two bodies placed at a chosen overlap with zero velocity, stepped one tick. Nothing is
        // approaching, so the impulse term has almost nothing to brake and this reads the confining
        // term nearly on its own — which is the whole point of having one.
        Console.WriteLine();
        Console.WriteLine("A. PRESSURE vs STATIC OVERLAP  (at rest — the confining term on its own)");
        Console.WriteLine("   overlap px    peak dyn     peak conf    peak total   over thr?");
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
                + $"{(r.Solver.PeakStress > thrRock ? "YES" : "no"),5}");
        }

        // ── B. pressure vs closing speed ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("B. PRESSURE vs CLOSING SPEED  (projectile, 60 ticks)");
        Console.WriteLine("   speed     peak dyn     peak conf     max dose   crushed");
        foreach (float v in new[] { 150f, 300f, 600f, 1200f, 2400f, 4800f })
        {
            var r = Scenarios.Projectile(t, Material.Rock, v, 3f);
            r.Solver.MeasureStress = true;
            for (int i = 0; i < 60; i++) r.Solver.Step();
            float dose = 0f;
            for (int c = 0; c < r.State.CellCount; c++)
                if (r.State.CellCrush[c] > dose) dose = r.State.CellCrush[c];
            Console.WriteLine($"   {v,7} {r.Solver.PeakDyn.ToString("E2", ci),12} "
                + $"{r.Solver.PeakConf.ToString("E2", ci),13} {dose.ToString("E2", ci),12} "
                + $"{r.Solver.Crushed,9}");
        }

        // ── C. dose vs sustained contact ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("C. DOSE vs SUSTAINED CONTACT  (slow closing press, dose on the worst cell)");
        Console.WriteLine("   ticks      60 px/s     150 px/s     400 px/s");
        foreach (int n in new[] { 20, 50, 100, 200, 400 })
        {
            Console.Write($"   {n,6}");
            foreach (float v in new[] { 60f, 150f, 400f })
            {
                var r = Scenarios.Collide(t, Material.Rock, speed: v);
                for (int i = 0; i < n; i++) r.Solver.Step();
                float dose = 0f;
                for (int c = 0; c < r.State.CellCount; c++)
                    if (r.State.CellCrush[c] > dose) dose = r.State.CellCrush[c];
                Console.Write($"{dose.ToString("E2", ci),13}");
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
                float dose = 0f;
                for (int c = 0; c < press.State.CellCount; c++)
                    if (press.State.CellCrush[c] > dose) dose = press.State.CellCrush[c];

                Console.Write($"  {thr.ToString("E1", ci),-8} {cap.ToString("E1", ci),-10} "
                    + $"{rest.Solver.Crushed,4} {(dose / cap).ToString("F2", ci),10}");
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

    private static Material Retune(in Material m, float crush, float cap)
        => new(m.Name, m.Rho, m.C, m.Strain, m.Chi, m.Yield, m.Duct, crush, cap);

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
                            + $"capacity {baseM.CrushCap.ToString("E1", ci)}) ──");
            Console.WriteLine("     threshold    rest   drift    press/cap    proj");
            foreach (float mul in new[] { 0.25f, 0.5f, 1f, 2f, 4f, 8f })
            {
                var m = Retune(baseM, baseM.Crush * mul, baseM.CrushCap);

                var rest = Scenarios.Field(t, m, 5, 5, 60f, 125f, 0f);
                for (int i = 0; i < 600; i++) rest.Solver.Step();

                var drift = Scenarios.Field(t, m, 5, 5, 60f, 150f, 20f);
                for (int i = 0; i < 600; i++) drift.Solver.Step();

                var press = Scenarios.Collide(t, m, speed: 150f);
                for (int i = 0; i < 400; i++) press.Solver.Step();
                float dose = 0f;
                for (int c = 0; c < press.State.CellCount; c++)
                    if (press.State.CellCrush[c] > dose) dose = press.State.CellCrush[c];

                var proj = Scenarios.Projectile(t, m, 900f, 3f);
                for (int i = 0; i < 200; i++) proj.Solver.Step();

                Console.WriteLine($"   {(m.Crush).ToString("E2", ci),11} {rest.Solver.Crushed,7} "
                    + $"{drift.Solver.Crushed,7} {(dose / m.CrushCap).ToString("F2", ci),12} "
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
            Console.Error.WriteLine($"{scene,-12} {hex}");
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
