using System.Collections.Generic;
using System.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using AsteroidsSim.Config;
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
    /// <summary>The configuration every mode runs on: <c>Assets/sim.json</c>.</summary>
    private static readonly SimConfig Cfg = SimConfigFile.Load();

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
        if (Array.IndexOf(args, "--report") >= 0) { Report(); return; }
        if (Array.IndexOf(args, "--micro") >= 0) { Micro(); return; }
        if (Array.IndexOf(args, "--detail") >= 0) { Detail(args); return; }
        if (Array.IndexOf(args, "--sensitivity") >= 0) { Sensitivity(args); return; }
        if (Array.IndexOf(args, "--fingerprint") >= 0) { Fingerprint(); return; }
        if (Array.IndexOf(args, "--parbase") >= 0) { ParallelBaseline(args); return; }
        if (Array.IndexOf(args, "--sidecensus") >= 0) { SideCensus(); return; }
        if (Array.IndexOf(args, "--audit") >= 0) { SideAuditRun(); return; }
        if (Array.IndexOf(args, "--cell") >= 0) { TraceOneCell(args); return; }
        if (Array.IndexOf(args, "--pair") >= 0) { TracePair(args); return; }
        if (Array.IndexOf(args, "--census") >= 0) { CarveCensus(args); return; }
        if (Array.IndexOf(args, "--momflow") >= 0) { MomentumFlow(args); return; }
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
        RunCase(Cfg.Material("rock"), 3, 3, 900f, 40, false, quiet: true);

        Console.WriteLine("── scaling: field of asteroids, grain 900 (~30 px cells) ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 4), (8, 6), (12, 9), (16, 12), (20, 16), (26, 20),
                                             (36, 28), (46, 36), (56, 44) })
            RunCase(Cfg.Material("rock"), cols, rows, 900f, ticks, phases);

        Console.WriteLine();
        Console.WriteLine("── same cell counts, fewer+finer bodies (grain 225, ~15 px cells) ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 3), (6, 5), (8, 7), (10, 9) })
            RunCase(Cfg.Material("rock"), cols, rows, 225f, ticks, phases, radius: 60f, spacing: 150f);

        Console.WriteLine();
        Console.WriteLine("── dense: bodies just touching (spacing = diameter), grain 900 ──");
        Header();
        foreach (var (cols, rows) in new[] { (8, 6), (12, 9), (16, 12), (26, 20), (36, 28), (46, 36) })
            RunCase(Cfg.Material("rock"), cols, rows, 900f, ticks, phases, spacing: 128f);

        Console.WriteLine();
        Console.WriteLine("── few LARGE bodies (r=200, ~140 cells each) — the shape a real field has ──");
        Header();
        foreach (var (cols, rows) in new[] { (4, 3), (7, 5), (10, 7), (13, 10), (16, 12) })
            RunCase(Cfg.Material("rock"), cols, rows, 900f, ticks, phases, radius: 200f, spacing: 430f);

        Console.WriteLine();
        Console.WriteLine("── same, bodies just touching ──");
        Header();
        foreach (var (cols, rows) in new[] { (7, 5), (10, 7), (13, 10), (16, 12) })
            RunCase(Cfg.Material("rock"), cols, rows, 900f, ticks, phases, radius: 200f, spacing: 400f);

        Console.WriteLine();
        Console.WriteLine("── worst case: bodies start half-interpenetrating, grain 900 ──");
        Header();
        foreach (var (cols, rows) in new[] { (8, 6), (12, 9), (16, 12) })
            RunCase(Cfg.Material("rock"), cols, rows, 900f, ticks, phases, spacing: 118f);
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

        var tune = Cfg.Tuning;
        var r = Scenarios.Projectile(tune, Cfg.Material("rock"), speed, mass, grain);
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
        var tune = Cfg.Tuning;
        var r = Scenarios.Field(tune, Cfg.Material("rock"), cols, rows, 60f, 128f, 60f, 900f);
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
               + phaseMs[(int)SolverPhase.Damping]
               + phaseMs[(int)SolverPhase.Damage]) / ticks;
        jobs?.Dispose();
    }

    /// <summary>Census of side labels: how many real, crack, sealed and bonded, over time.</summary>
    private static void SideCensus()
    {
        var t = Cfg.Tuning;
        foreach (var (name, r) in new[]
        {
            ("collide", Scenarios.Collide(t, Cfg.Material("rock"), speed: 600f)),
            ("glass", Scenarios.Projectile(t, Cfg.Material("glass"))),
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
        var t = Cfg.Tuning;
        var t170 = Cfg.Tuning; t170.ToughnessScale = 1.1f; t170.CarveContinuity = 0.75f; t170.CrushConfine = 0.10f;
        var t255 = Cfg.Tuning; t255.ToughnessScale = 1.7f;
        var hardSteel = new Material("steel", 7850f, 5900f, 0.020f, 50f, 2.0e6f, 0.01875f, 0.50f, 2.5f);
        var scenes = new List<(string, Scenarios.Result)>();
        foreach (string n in Scenarios.ReferenceNames) scenes.Add((n, Scenarios.Reference(n, Cfg)));
        scenes.Add(("repro: collide/rock g170", Scenarios.Collide(t170, Cfg.Material("rock"), 600f, 170f)));
        scenes.Add(("repro: steel/steel g255 2250", Scenarios.Projectile(t255, hardSteel, 2250f, 5.5f, 255f, impactor: hardSteel)));
        scenes.Add(("repro: steel/rock g900 900", Scenarios.Projectile(t, Cfg.Material("rock"), 900f, 3f, 900f, impactor: Cfg.Material("steel"))));
        foreach (var (name, r) in scenes)
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

    private static void TraceOneCell(string[] args)
    {
        int cell = ArgInt(args, "--cell", 562);
        int from = ArgInt(args, "--from", 30), to = ArgInt(args, "--to", 38);
        var r = Scenarios.Collide(Cfg.Tuning, Cfg.Material("rock"), 600f, 170f);
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
            var tune = Cfg.Tuning;
            tune.CarveContinuity = cont;
            var r = Scenarios.Collide(tune, Cfg.Material("rock"), 600f, 170f);
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
            var tune = Cfg.Tuning;
            tune.CarveMinArea = minArea;
            if (loopCfg) { tune.ToughnessScale = 1.1f; tune.CarveContinuity = 0.75f; tune.CrushConfine = 0.10f; }
            var r = Scenarios.Collide(tune, Cfg.Material("rock"), 600f, 170f);
            var cen = new Solver.CarveCensus();
            r.Solver.Census = cen;
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

    private static float LedgerPct(Solver solver)
    {
        solver.TotalMomentum(out float px, out float py);
        float live = SimMath.Hypot(px, py), led = SimMath.Hypot(solver.ExportedPx, solver.ExportedPy);
        return live + led < 1f ? 0f : 100f * led / (live + led);
    }

    /// <summary>Where the momentum goes in a collide: body speeds, live contacts, comminution routing.</summary>
    private static void MomentumFlow(string[] args)
    {
        bool tough = Array.IndexOf(args, "--toughglass") >= 0;
        foreach (var m in tough ? new[] { Cfg.Material("glass") } : new[] { Cfg.Material("glass"), Cfg.Material("rock") })
        {
            var tn = Cfg.Tuning; if (tough) { tn.StrainScale = 3f; }
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
            var r = WithJobs(Scene(scene, Cfg.Tuning));
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
                var t = Cfg.Tuning;
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
        "collide" => Scenarios.Collide(t, Cfg.Material("rock"), speed: 600f * mul),
        "projectile" => Scenarios.Projectile(t, Cfg.Material("rock"), speed: 900f * mul),
        "steel" => Scenarios.Projectile(t, Cfg.Material("steel"), speed: 1500f * mul, massMul: 8f),
        "glass" => Scenarios.Projectile(t, Cfg.Material("glass"), speed: 1500f * mul, massMul: 8f),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Scenarios.Result Scene(string name, SimTuning t) => name switch
    {
        "collide" => Scenarios.Collide(t, Cfg.Material("rock"), speed: 600f),
        "projectile" => Scenarios.Projectile(t, Cfg.Material("rock")),
        "spin" => Scenarios.Spin(t, Cfg.Material("rock")),
        "steel" => Scenarios.Projectile(t, Cfg.Material("steel"), speed: 1500f, massMul: 8f),
        "glass" => Scenarios.Projectile(t, Cfg.Material("glass"), speed: 1500f, massMul: 8f),
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

        var tune = Cfg.Tuning;
        if (DriftOverride > 0f) tune.ManifoldDrift = DriftOverride;
        var r = WithJobs(Scenarios.Field(tune, Cfg.Material("rock"), cols, rows, 60f, spacing, 60f, 900f));
        int cells0 = r.State.CellCount, bonds0 = r.State.BondCount, bodies0 = r.State.BodyCount;

        double[] phaseMs = new double[(int)SolverPhase.Count];
        var phaseSw = new Stopwatch();
        r.Solver.PhaseMark = p => { phaseMs[(int)p] += phaseSw.Elapsed.TotalMilliseconds; phaseSw.Restart(); };

        for (int i = 0; i < 20; i++) { phaseSw.Restart(); r.Solver.Step(); }   // warm
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
        Row("cell world polygons", c.SkinComputed + c.SkinCacheHit, c.SkinCacheHit, "served from the substep cache");
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
        Phase(SolverPhase.Damping, tune.Substeps, c.DampCells, "cell");
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
        var tune = Cfg.Tuning;
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

    /// <summary>
    /// Tracking metrics for the reference scenes, at the same floor grain the correctness tests use.
    /// Nothing here is asserted: peak overlap is a promise the model aims to keep, and this is where
    /// it is watched from one change to the next.
    /// </summary>
    private static void Report()
    {
        var t = Cfg.Tuning;
        Console.WriteLine("TUNING " + DumpTuning(t));
        foreach (string name in Scenarios.ReferenceNames)
        {
            var r = Scenarios.Reference(name, Cfg);
            float cell = r.State.BodyCellSize[0];
            var m = SceneRunner.Run(r, 400, cell);
            Console.WriteLine($"{name,-17} grain {cell * cell,5:F0} ({cell:F1} px): {m}  "
                + $"peak overlap {m.PeakOverlap / cell:P0} of a cell, backstop {r.Solver.BackstopContacts} contacts / "
                + $"{r.Solver.BackstopComminuted} comminuted");
        }
    }

    private static string DumpTuning(in SimTuning t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in typeof(SimTuning).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(t), CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString();
    }

}
