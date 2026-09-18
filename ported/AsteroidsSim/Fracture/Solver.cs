using System;
using System.Collections.Generic;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>A cell-vs-cell contact. Scratch: rebuilt every substep, never snapshotted.</summary>
public struct Contact
{
    public int A;
    public int B;
    public float Nx;
    public float Ny;
    public float Depth;
    public float Px;
    public float Py;
    public float Ln;   // normal impulse for THIS substep (XPBD lambda; reset each substep)
    public float Lt;   // accumulated tangential impulse

    /// <summary>Work this contact did against the approach this substep — what carving is paid from.</summary>
    public float Work;

    // Reference state captured when the manifold was built, so a substep can refresh the contact
    // from rigid motion instead of re-running the narrow phase. Depth0 is signed: negative means
    // the pair was speculative — tracked but not yet touching.
    public float Depth0;
    public float Ax0;
    public float Ay0;
    public float Bx0;
    public float By0;
}

/// <summary>
/// The destruction solver: one fixed tick of the bonded-particle model.
/// </summary>
/// <remarks>
/// <para>A transcription of <c>step()</c> in <c>prototypes/stress-fracture-v9.html</c>. The order of
/// operations is the specification, not an implementation detail — the bond solve and the contact
/// solve are both Gauss-Seidel, so reordering them changes the result. Contacts are solved in the
/// order they are built, which is derived from cell index order; bonds are visited in index order;
/// bodies in index order.</para>
///
/// <para><b>Representation.</b> Cells never move relative to their body. Deformation is bookkept per
/// bond as a stretch (normal, shear, bending) and carried dynamically by a per-cell deviation
/// velocity field. What the collider and the renderer see is <c>rest + u</c>, where <c>u</c>
/// integrates that field — but the solver itself always works in rest space. The two are reconciled
/// by shared-vertex skinning, which places every copy of a shared polygon vertex at the average of
/// where its sharing cells put it, so a bonded pair cannot open a gap.</para>
///
/// <para><b>No positional solver.</b> Overlap during an impact is deformation, and is consumed by
/// <c>u</c> elastically and by plastic denting through the rebake. The only positional nudge is for
/// bond-less rubble, which has no deformation outlet at all.</para>
/// </remarks>
public sealed partial class Solver
{
    public const float Dt = 1f / 60f;

    private readonly SimState _s;
    private SimTuning _tune;

    // scratch — reused, never allocated in the steady state
    private Contact[] _contacts = new Contact[256];
    private int _contactCount;
    private int[] _pairs = new int[1024];
    private int _pairCount;
    private readonly Dictionary<int, List<int>> _grid = new();
    private readonly List<List<int>> _bucketPool = new();
    private int _bucketsUsed;

    private float[] _skinX = Array.Empty<float>();   // skinned polygon vertices, world space
    private float[] _skinY = Array.Empty<float>();
    private int[] _skinStamp = Array.Empty<int>();

    /// <summary>
    /// Contact pressure summed over one cell's contacts, within one substep. Scratch, not state:
    /// filled by <c>SolveContact</c> and drained to zero by <c>AccumulateCrushDose</c> in the same
    /// substep, so it never survives into a snapshot and never reaches the fingerprint.
    /// </summary>
    private float[] _cellPress = Array.Empty<float>();
    private float[] _skinMinX = Array.Empty<float>(), _skinMaxX = Array.Empty<float>();
    private float[] _skinMinY = Array.Empty<float>(), _skinMaxY = Array.Empty<float>();

    // Cell-local transformed vertices, cached per substep. Skinning averages each shared vertex
    // over the cells that own it, so without this the same neighbour vertex is transformed once
    // for every cell in its group — nine transforms where three suffice for a typical Voronoi
    // vertex. This was the dominant cost in a packed scene.

    // Body rotation trig, cached per epoch. SimMath is a software libm — every Sin/Cos is a
    // Cody-Waite reduction plus a polynomial — and the naive code called it once per CELL in the
    // skinning and twice per CONTACT in the solve, which came to roughly forty thousand software
    // trig calls per tick in a packed scene. A body's rotation is constant between integrations,
    // so one call per body per epoch is all that is needed.
    private float[] _bodyCa = Array.Empty<float>(), _bodySa = Array.Empty<float>();
    private int[] _bodyRotStamp = Array.Empty<int>();
    private int _rotEpoch = 1;

    /// <summary>Invalidate the body trig cache. Called wherever a body's rotation can change.</summary>
    private void BumpRotEpoch() => _rotEpoch++;

    private void BodyTrig(int b, out float si, out float co)
    {
        if (_bodyRotStamp.Length <= b)
        {
            int n = b + 1 < 16 ? 16 : (b + 1) * 2;
            Array.Resize(ref _bodyCa, n);
            Array.Resize(ref _bodySa, n);
            var st = new int[n];
            Array.Copy(_bodyRotStamp, st, _bodyRotStamp.Length);
            _bodyRotStamp = st;
        }
        if (_bodyRotStamp[b] != _rotEpoch)
        {
            _bodyRotStamp[b] = _rotEpoch;
            SimMath.SinCos(_s.BodyRot[b], out float s2, out float c2);
            _bodySa[b] = s2; _bodyCa[b] = c2;
        }
        si = _bodySa[b]; co = _bodyCa[b];
    }

    /// <summary>Candidate pairs and surviving contacts last substep. Diagnostics.</summary>
    public int PairCount => _pairCount / 2;
    public int ContactCount => _contactCount;

    /// <summary>Per-tick work counters. Diagnostics only; never read by the simulation.</summary>
    public SolverCounters C;

    // Contact coherence measurement. Diagnostics only: how much of the contact set survives from
    // one substep to the next, and how far a surviving contact's depth moves. Together these say
    // whether the narrow phase could run less often than every substep.
    private readonly Dictionary<long, float> _prevContacts = new();
    private readonly Dictionary<long, float> _thisContacts = new();
    public bool MeasureCoherence;
    public double DepthDeltaSum;
    public double DepthDeltaMax;
    public long DepthDeltaCount;

    private float _specMargin = 4f;   // speculative admission margin, set per tick by BuildPairs

    // Pose of each body when the manifold was last built, plus a radius to turn rotation into a
    // rim displacement. Sized to BodyCount, which cannot change inside a tick.
    private float[] _mfX0 = Array.Empty<float>();
    private float[] _mfY0 = Array.Empty<float>();
    private float[] _mfRot0 = Array.Empty<float>();
    private float[] _mfRad = Array.Empty<float>();
    private int _mfBodies;
    private float _mfDriftLimit = 7.5f;

    /// <summary>Per-body broadphase reach: how far this body alone can travel in one tick.</summary>
    private float[] _bodyMargin = Array.Empty<float>();

    /// <summary>
    /// Worker pool for the passes that decompose per body. Null runs everything on this thread,
    /// which is the reference path — the two must produce identical fingerprints.
    /// </summary>
    public SimJobs? Jobs;

    /// <summary>Which passes are dispatched, by bit. Diagnostic, for bisecting a bad decomposition.</summary>
    public int ParallelMask = 0x3F;

    private bool Par(int bit) => Jobs != null && (ParallelMask & bit) != 0;

    // Per-chunk counter accumulators, merged in chunk order after each parallel pass so the census
    // is as reproducible as the simulation.
    private SolverCounters[] _chunkC = Array.Empty<SolverCounters>();

    // Per-chunk float accumulator for the recoil-energy tally, merged in chunk order. A float sum
    // is where chunk count WOULD start changing results, so it is the one thing here that must be
    // merged deterministically rather than just safely.
    private double[] _chunkRecoil = Array.Empty<double>();

    /// <summary>
    /// The four fields every broadphase and narrow-phase gate reads, interleaved.
    /// </summary>
    /// <remarks>
    /// Sixteen bytes, so four cells share a cache line. The gates were costing 255 ns for about ten
    /// arithmetic operations, because reading position, radius and body for two cells meant ten
    /// scattered loads across ten separate arrays — at 18.5k cells each of those arrays is 74 KB,
    /// none of them stay in L1, and the access pattern is random by construction. Packing turns ten
    /// cache misses into two. The simulation still reads the SoA arrays; this is a redundant copy
    /// rebuilt once per broadphase pass, which costs one sequential sweep.
    /// </remarks>
    private struct CellBroad
    {
        public float Px, Py, Rad;
        public int Body;              // negative for a dead cell, so one test covers both
    }

    private CellBroad[] _broad = Array.Empty<CellBroad>();

    /// <summary>Refreshes the packed copy. Must run after <see cref="UpdateCenters"/>.</summary>
    private void PackBroad()
    {
        int n = _s.CellCount;
        if (_broad.Length < n)
        {
            int cap = _broad.Length == 0 ? 256 : _broad.Length;
            while (cap < n) cap <<= 1;
            _broad = new CellBroad[cap];
        }
        for (int c = 0; c < n; c++)
        {
            ref CellBroad d = ref _broad[c];
            d.Px = _s.CellPx[c]; d.Py = _s.CellPy[c]; d.Rad = _s.CellRad[c];
            d.Body = _s.Dead(c) ? -1 : _s.CellBody[c];
        }
    }

    /// <summary>Body-range boundaries that give each chunk a similar number of bonds.</summary>
    private int[] _bondChunkBounds = Array.Empty<int>();
    private int _boundsTick = -1;
    private int _boundsBodies = -1;

    /// <summary>
    /// Splits the body array so each chunk carries about the same bond count, not the same body
    /// count. Recomputed once a tick — topology only changes at tick boundaries — and cached, so
    /// all eighteen dispatches in a tick reuse one split.
    /// </summary>
    /// <summary>
    /// Dispatches one pass over body ranges and folds the per-chunk counters back in chunk order.
    /// </summary>
    private void RunOverBodies(Action<int, int, int> pass)
    {
        int nc = Jobs!.Chunks;
        if (_chunkC.Length != nc) { _chunkC = new SolverCounters[nc]; _chunkRecoil = new double[nc]; }
        Array.Clear(_chunkRecoil);
        Jobs.For(BondChunkBounds(), pass);
        for (int i = 0; i < nc; i++)
        {
            C.Add(_chunkC[i]); _chunkC[i].Reset();
            RecoilEnergy += (float)_chunkRecoil[i];
        }
    }

    private int[] BondChunkBounds()
    {
        int nc = Jobs!.Chunks;
        if (_bondChunkBounds.Length != nc + 1) { _bondChunkBounds = new int[nc + 1]; _boundsTick = -1; }
        // Cached for the tick — eighteen dispatches share one split — but the cache has to be keyed
        // on the body count as well, because RebuildBodies runs INSIDE the tick and the decompose
        // that follows it must reach the fragments it just created. Keyed on the tick alone, the
        // stale bounds still ended at the old body count and the new bodies were never visited.
        if (_boundsTick == _s.Tick && _boundsBodies == _s.BodyCount) return _bondChunkBounds;
        _boundsTick = _s.Tick;
        _boundsBodies = _s.BodyCount;

        long total = 0;
        for (int b = 0; b < _s.BodyCount; b++) total += _s.BodyBondLen[b];

        _bondChunkBounds[0] = 0;
        int chunk = 1;
        long acc = 0;
        for (int b = 0; b < _s.BodyCount && chunk < nc; b++)
        {
            acc += _s.BodyBondLen[b];
            // Close the chunk once it holds its share. Integer comparison throughout, so the split
            // is identical on every machine.
            while (chunk < nc && acc * nc >= total * chunk)
                _bondChunkBounds[chunk++] = b + 1;
        }
        while (chunk <= nc) _bondChunkBounds[chunk++] = _s.BodyCount;
        return _bondChunkBounds;
    }


    private float[] _polyAx = new float[64];
    private float[] _polyAy = new float[64];
    private float[] _polyBx = new float[64];
    private float[] _polyBy = new float[64];

    // split scratch
    private int[] _comp = Array.Empty<int>();
    private int[] _stack = Array.Empty<int>();
    private int[] _touched = new int[16];        // bodies whose aggregates must be refreshed
    private int[] _touchedStamp = Array.Empty<int>();
    private int _touchedCount;
    private int _touchEpoch = 1;
    private int[] _parentOf = Array.Empty<int>();

    /// <summary>Marks a body as having lost a bond or a cell, so the topology pass will visit it.</summary>
    /// <summary>
    /// Flags a body for the topology rebuild.
    /// </summary>
    /// <remarks>
    /// This kept a running count alongside the flags, which was a shared write and therefore a race
    /// once damage ran across bodies in parallel: increments were lost, the rebuild was skipped, and
    /// the result depended on how the chunks happened to interleave. The flag itself is per body and
    /// safe; the count is now derived by scanning them, which costs one pass over bodies once a tick
    /// against a value that could not be trusted.
    /// </remarks>
    private void MarkDirty(int b)
    {
        if (b < 0 || b >= _s.BodyCount) return;
        _s.BodyDirty[b] = true;
    }

    /// <summary>
    /// Bonds listed under a body whose cells are not both in it — must always be zero.
    /// </summary>
    /// <remarks>
    /// The premise the whole parallel decomposition rests on: a bond couples two cells of one body,
    /// so bodies can be handed to different threads without any of them touching the same cell. If
    /// this were ever non-zero, the bond passes would be writing across chunks and the fingerprint
    /// would start drifting under load — the hardest kind of bug to catch after the fact, so it is
    /// asserted directly instead.
    /// </remarks>
    public int CrossBodyBondCount()
    {
        int n = 0;
        for (int b = 0; b < _s.BodyCount; b++)
        {
            int off = _s.BodyBondOff[b], len = _s.BodyBondLen[b];
            for (int i = 0; i < len; i++)
            {
                int k = _s.BodyBonds[off + i];
                if (_s.BondBroken[k]) continue;
                if (_s.CellBody[_s.BondA[k]] != b || _s.CellBody[_s.BondB[k]] != b) n++;
            }
        }
        return n;
    }

    private bool AnyDirty()
    {
        for (int b = 0; b < _s.BodyCount; b++) if (_s.BodyDirty[b]) return true;
        return false;
    }

    // rebake scratch
    private float[] _wx = Array.Empty<float>();
    private float[] _wy = Array.Empty<float>();
    private float[] _wt = Array.Empty<float>();

    public Solver(SimState state, in SimTuning tuning)
    {
        _s = state;
        _tune = tuning;
    }

    public ref SimTuning Tuning => ref _tune;

    /// <summary>Diagnostics, mirroring the prototype's <c>stats</c>. Not part of the sim state.</summary>
    // ── contact-stress instrumentation (off by default, for the comminution study) ────────────
    //
    // Reads the same per-cell pressure the criterion runs on, so a sweep over CrushThreshold answers
    // "what would this material have done" without re-running the sim per candidate value.
    public bool MeasureStress;
    public float CrushThreshold;
    public readonly long[] StressHist = new long[40];   // log2 buckets, bucket i = [2^i, 2^(i+1))
    public float PeakStress;
    /// <summary>Peak of the impulse (braking) term alone, for reading the two apart.</summary>
    public float PeakDyn;
    /// <summary>Peak of the confining (penetration) term alone.</summary>
    public float PeakConf;
    /// <summary>Peak per-contact carve work in a substep. Diagnostics.</summary>
    public float PeakWork;
    public long DbgCalls, DbgDead, DbgGate, DbgWc, DbgWant, DbgArea, DbgRemoved, DbgCarved;
    public int DbgCapHit; public double DbgCapExcess;

    // ── carve log: every clip, with the direction it used. Diagnostics only. ──
    /// <summary>Diagnostics: follow one cell through the contact solve and the carve.</summary>
    public int TraceCell = -1;
    public System.Action<string>? TraceSink;

    public bool CarveLogging;
    public int[] CarveLogCell = new int[4096];
    public float[] CarveLogNx = new float[4096];
    public float[] CarveLogNy = new float[4096];
    public float[] CarveLogArea = new float[4096];
    public int CarveLogCount;
    public float[] CrushDose = Array.Empty<float>();

    public int Broken;
    public int Dust;
    public int Crushed;

    /// <summary>Cell-areas' worth of material carved away, as a fraction sum. Diagnostics.</summary>
    public float ShedArea;
    public int Rebakes;
    public float MaxOverlap;
    public float PlasticWork;
    public float RecoilEnergy;
    public float DustMass;
    public float ExportedPx;
    public float ExportedPy;
    public float ExportedKe;

    private const float RebakeThreshold = 1.0f;
    private const int DustFreeTicks = 3;
    private const int DustDeepTicks = 4;

    /// <summary>
    /// Called once as each phase of the tick completes, for profiling. Null by default, and the
    /// call sites are per phase rather than per element, so the cost when unset is a null check a
    /// dozen times per substep.
    /// </summary>
    /// <remarks>
    /// The timer itself cannot live in this assembly: <c>Stopwatch</c> and <c>DateTime</c> are
    /// banned here because a wall clock inside the deterministic path is a desync waiting to
    /// happen. The host supplies the clock; the simulation only says where the boundaries are.
    /// </remarks>
    public Action<SolverPhase>? PhaseMark;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Mark(SolverPhase p) => PhaseMark?.Invoke(p);

    // ══════════════════════════════════════════════════════════════════════════
    //  transforms
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cell <paramref name="c"/>'s collider polygon in world space, cached for the substep. Returns
    /// the offset into <see cref="_skinX"/>/<see cref="_skinY"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>There is no skinning any more.</b> With realized displacement gone a cell sits
    /// exactly at its rest offset, so its body-local polygon is simply <c>CellR + q</c> — constant
    /// until a split re-centres the body — and two cells sharing a Voronoi corner place it at the
    /// same point <i>by construction</i>. The shared-vertex averaging that used to be needed to make
    /// that true, walking every vertex's group and transforming each member's polygon, is gone with
    /// it. What remains is one rotation per body applied to a fixed local polygon.</para>
    ///
    /// <para><b>Why still cached.</b> A cell in a crowded pile takes part in many candidate pairs and
    /// presents the same polygon to all of them. The cache is keyed on the substep, so it holds
    /// exactly as long as the body pose does.</para>
    /// </remarks>
    private int SkinCell(int c)
    {
        int off = _s.PolyOff[c];
        if (_skinStamp[c] == _s.Substep) { C.SkinCacheHit++; return off; }
        _skinStamp[c] = _s.Substep;
        C.SkinComputed++;

        int body = _s.CellBody[c];
        BodyTrig(body, out float si, out float co);
        float bx = _s.BodyX[body], by = _s.BodyY[body];
        float rx = _s.CellRx[c], ry = _s.CellRy[c];
        int len = _s.PolyLen[c];

        float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f;
        for (int v = 0; v < len; v++)
        {
            float lx = rx + _s.PolyX[off + v];
            float ly = ry + _s.PolyY[off + v];
            float wx = bx + lx * co - ly * si;
            float wy = by + lx * si + ly * co;
            _skinX[off + v] = wx;
            _skinY[off + v] = wy;
            if (v == 0) { minX = maxX = wx; minY = maxY = wy; }
            else
            {
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
            }
        }
        _skinMinX[c] = minX; _skinMaxX[c] = maxX;
        _skinMinY[c] = minY; _skinMaxY[c] = maxY;
        return off;
    }

    private void EnsureSkin()
    {
        if (_skinX.Length >= _s.PolyCount && _skinStamp.Length >= _s.CellCount
            && _skinMinX.Length >= _s.CellCount && _cellPress.Length >= _s.CellCount) return;
        int np = System.Math.Max(1, _s.PolyCount);
        _skinX = new float[np];
        _skinY = new float[np];
        int nc = System.Math.Max(1, _s.CellCount);
        _skinMinX = new float[nc]; _skinMaxX = new float[nc];
        _skinMinY = new float[nc]; _skinMaxY = new float[nc];
        _cellPress = new float[nc];
        var st = new int[nc];
        for (int i = 0; i < nc; i++) st[i] = -1;
        _skinStamp = st;
    }

    private void UpdateCenters()
    {
        C.UpdateCentersCalls++;
        for (int b = 0; b < _s.BodyCount; b++)
        {
            BodyTrig(b, out float si, out float co);
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.Dead(c)) continue;
                float x = _s.CellRx[c], y = _s.CellRy[c];
                _s.CellPx[c] = _s.BodyX[b] + x * co - y * si;
                _s.CellPy[c] = _s.BodyY[b] + x * si + y * co;
                C.UpdateCentersCells++;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  broad phase
    // ══════════════════════════════════════════════════════════════════════════

    private List<int> RentBucket()
    {
        if (_bucketsUsed < _bucketPool.Count)
        {
            var l = _bucketPool[_bucketsUsed++];
            l.Clear();
            return l;
        }
        var n = new List<int>();
        _bucketPool.Add(n);
        _bucketsUsed++;
        return n;
    }

    /// <summary>
    /// Candidate pairs, rebuilt once per tick with a speed-dependent margin. The narrow phase
    /// re-tests them every substep with fresh positions, because the position solver is gone and
    /// contact depths must be current.
    /// </summary>
    private void BuildPairs()
    {
        _pairCount = 0;
        C.PairBuilds++;
        UpdateCenters();
        PackBroad();

        float vmax = 0f, cellMax = 30f, cellMin = 0f;
        if (_bodyMargin.Length < _s.BodyCount) _bodyMargin = new float[_s.BodyCount * 2];
        for (int b = 0; b < _s.BodyCount; b++)
        {
            float sp = SimMath.Hypot(_s.BodyVx[b], _s.BodyVy[b]) + SimMath.Abs(_s.BodyW[b]) * 150f;
            if (sp > vmax) vmax = sp;

            // Each body carries its OWN reach. The grid still has to be sized for the fastest body
            // in the scene, but the pair test does not: sharing one global margin let a single fast
            // body inflate every test, and measured at 18.5k cells that was admitting 53% more
            // pairs than could ever touch — each of which then paid for two cell skins to be
            // rejected by a bounding box.
            _bodyMargin[b] = SimMath.Min(50f, 2f + sp * Dt);

            float cz = _s.BodyCellSize[b] * 2f;
            if (cz > cellMax) cellMax = cz;
            if (cz > 0f && (cellMin == 0f || cz < cellMin)) cellMin = cz;
        }
        if (cellMin == 0f) cellMin = 30f;

        // How far a body may travel before the manifold's held normals and cell pairings stop
        // describing the scene. A quarter of the smallest cell: below that the refresh is accurate,
        // above it the narrow phase has to run again.
        _mfDriftLimit = _tune.ManifoldDrift * cellMin;
        float margin = SimMath.Min(100f, 4f + vmax * Dt);
        float cs = cellMax + margin;

        // The manifold is built once a tick, so a pair must be admitted speculatively if it could
        // close before the next build. Same bound as the broadphase margin: anything nearer than
        // the furthest a pair can travel in a tick is tracked now rather than discovered late.
        _specMargin = margin;

        _grid.Clear();
        _bucketsUsed = 0;

        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.Dead(c)) continue;
            int key = GridKey((int)SimMath.Floor(_s.CellPx[c] / cs), (int)SimMath.Floor(_s.CellPy[c] / cs));
            if (!_grid.TryGetValue(key, out var bucket)) { bucket = RentBucket(); _grid[key] = bucket; }
            bucket.Add(c);
            C.PairGridInserts++;
        }

        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.Dead(c)) continue;
            ref CellBroad dc = ref _broad[c];
            int gx = (int)SimMath.Floor(dc.Px / cs);
            int gy = (int)SimMath.Floor(dc.Py / cs);
            int bc = dc.Body;
            float mc = _bodyMargin[bc];

            // HALF NEIGHBOURHOOD. Scanning all nine buckets visits every pair from both ends and
            // throws one away — measured at 18.5k cells, 293,000 of 567,000 bucket visits per tick
            // were doing nothing but that. Visiting the cell's own bucket plus the four buckets
            // "ahead" of it reaches every neighbouring pair exactly once: if two cells share a
            // bucket the index test still separates them, and if they are in adjacent buckets then
            // exactly one of the two sees the other as ahead.
            for (int nb = 0; nb < 5; nb++)
            {
                int dx = HalfNbrX[nb], dy = HalfNbrY[nb];
                if (!_grid.TryGetValue(GridKey(gx + dx, gy + dy), out var bucket)) continue;
                bool own = nb == 0;
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int o = bucket[i];
                        C.PairBucketVisits++;
                        // Only the shared bucket needs the index test; the other four cannot
                        // produce a duplicate at all.
                        if (own && o <= c) { C.PairRejectOrder++; continue; }
                        ref CellBroad d = ref _broad[o];
                        int bo = d.Body;
                        if (bo == bc) { C.PairRejectSameBody++; continue; }
                        float ddx = d.Px - dc.Px, ddy = d.Py - dc.Py;
                        float rr = dc.Rad + d.Rad + mc + _bodyMargin[bo];
                        if (ddx * ddx + ddy * ddy > rr * rr) { C.PairRejectRadius++; continue; }
                        C.PairEmitted++;
                        if (_pairCount + 2 > _pairs.Length) Array.Resize(ref _pairs, _pairs.Length * 2);
                        // A is the lower index whichever side found the pair, so the contact
                        // normal keeps pointing the same way it always did.
                        _pairs[_pairCount++] = c < o ? c : o;
                        _pairs[_pairCount++] = c < o ? o : c;
                    }
                }
            }
        }
    }

    /// <summary>Own bucket, then the four that no other cell will scan back toward.</summary>
    private static readonly int[] HalfNbrX = { 0, 1, 1, 1, 0 };
    private static readonly int[] HalfNbrY = { 0, -1, 0, 1, 1 };

    /// <summary>
    /// The reference's grid hash, reproduced exactly. Only ever probed, never iterated — a hash
    /// container's enumeration order is not defined and must not reach the simulation.
    /// </summary>
    private static int GridKey(int gx, int gy)
        => unchecked((gx * 73856093) ^ (gy * 19349663));

    /// <summary>
    /// Builds the contact manifold. Called once per tick, not once per substep.
    /// </summary>
    /// <remarks>
    /// <para>Measured before this change: 99% of contacts also existed in the previous substep and
    /// the mean depth change across one was 0.008 px, so rebuilding the set nine times a tick was
    /// re-deriving an almost identical answer. The nine substeps exist for the bond CFL condition,
    /// not for contacts.</para>
    /// <para>Pairs are admitted with a <b>speculative margin</b>: a pair separated by less than the
    /// margin is kept in the manifold with a negative depth, so a contact that closes during the
    /// tick is already tracked when it arrives rather than being missed until the next build.</para>
    /// </remarks>
    private void BuildManifold()
    {
        _contactCount = 0;
        C.NarrowCalls++;
        CaptureManifoldPose();
        if (_pairCount == 0) return;
        EnsureSkin();
        UpdateCenters();
        PackBroad();

        for (int p = 0; p < _pairCount; p += 2)
        {
            int c = _pairs[p], o = _pairs[p + 1];
            C.NarrowExamined++;
            ref CellBroad dc = ref _broad[c];
            ref CellBroad db = ref _broad[o];
            if (dc.Body < 0 || db.Body < 0) { C.NarrowRejectDead++; continue; }
            if (dc.Body == db.Body) { C.NarrowRejectSameBody++; continue; }
            float ddx = db.Px - dc.Px, ddy = db.Py - dc.Py;
            float rr = dc.Rad + db.Rad;
            if (ddx * ddx + ddy * ddy > rr * rr) { C.NarrowRejectRadius++; continue; }

            int oa = SkinCell(c), ob = SkinCell(o);

            // BIT-SAFE REJECTION. Two polygons whose axis-aligned boxes are disjoint cannot
            // overlap, so SAT would have returned no contact. Four comparisons replace roughly two
            // hundred operations, and in a packed pile the circumscribed-radius test above rejects
            // very little because the cells genuinely ARE close.
            if (_skinMaxX[c] < _skinMinX[o] || _skinMinX[c] > _skinMaxX[o] ||
                _skinMaxY[c] < _skinMinY[o] || _skinMinY[c] > _skinMaxY[o])
            { C.NarrowRejectAabb++; continue; }

            C.SatCalls++;
            int lc = _s.PolyLen[c], lo = _s.PolyLen[o];
            if (!Sat(_skinX.AsSpan(oa, lc), _skinY.AsSpan(oa, lc),
                     _skinX.AsSpan(ob, lo), _skinY.AsSpan(ob, lo), _specMargin,
                     out float nx, out float ny, out float depth, out float px, out float py,
                     out int axes, out int projections))
            { C.SatSeparated++; C.SatAxesTested += axes; C.SatProjections += projections; continue; }
            C.SatContact++; C.SatAxesTested += axes; C.SatProjections += projections;

            // DEEP-OVERLAP NORMAL GUARD. SAT returns the axis of minimum penetration; once two
            // cells are more than about half a cell deep that axis flips to the far side and the
            // contact pushes the impactor THROUGH. Past that depth the centre-to-centre direction
            // is the only trustworthy normal.
            float lim = 0.5f * SimMath.Min(_s.CellRad[c], _s.CellRad[o]);
            if (depth > lim)
            {
                float L = SimMath.Hypot(ddx, ddy);
                if (L > 1e-6f) { nx = ddx / L; ny = ddy / L; }
            }

            if (depth > MaxOverlap) MaxOverlap = depth;

            if (MeasureCoherence)
            {
                long key = ((long)c << 32) | (uint)o;
                _thisContacts[key] = depth;
                if (_prevContacts.TryGetValue(key, out float prevDepth))
                {
                    C.ContactRepeatPair++;
                    double d = System.Math.Abs(depth - prevDepth);
                    DepthDeltaSum += d;
                    if (d > DepthDeltaMax) DepthDeltaMax = d;
                    DepthDeltaCount++;
                }
            }

            if (_contactCount >= _contacts.Length) Array.Resize(ref _contacts, _contacts.Length * 2);
            _contacts[_contactCount++] = new Contact
            {
                A = c, B = o, Nx = nx, Ny = ny, Depth = depth, Px = px, Py = py, Ln = 0f, Lt = 0f,
                Depth0 = depth,
                Ax0 = _s.CellPx[c], Ay0 = _s.CellPy[c],
                Bx0 = _s.CellPx[o], By0 = _s.CellPy[o],
            };
        }

        if (MeasureCoherence)
        {
            _prevContacts.Clear();
            foreach (var kv in _thisContacts) _prevContacts[kv.Key] = kv.Value;
            _thisContacts.Clear();
        }
    }

    /// <summary>Records the pose the manifold was built against.</summary>
    private void CaptureManifoldPose()
    {
        int n = _s.BodyCount;
        if (_mfX0.Length < n)
        {
            int cap = _mfX0.Length == 0 ? 64 : _mfX0.Length;
            while (cap < n) cap <<= 1;
            _mfX0 = new float[cap]; _mfY0 = new float[cap];
            _mfRot0 = new float[cap]; _mfRad = new float[cap];
        }
        for (int b = 0; b < n; b++)
        {
            _mfX0[b] = _s.BodyX[b]; _mfY0[b] = _s.BodyY[b]; _mfRot0[b] = _s.BodyRot[b];
            float m = _s.BodyM[b];
            // Radius of gyration doubled: for a disc the rim sits at sqrt(2) times it, so this
            // over-estimates slightly, which is the safe direction for a staleness bound.
            _mfRad[b] = m > 1e-9f ? 2f * SimMath.Sqrt(_s.BodyI[b] / m) : 0f;
        }
        _mfBodies = n;
    }

    /// <summary>
    /// The furthest any body has moved since the manifold was built, rotation included as the
    /// displacement it produces at the rim.
    /// </summary>
    /// <remarks>
    /// This is what decides whether the cheap refresh is still telling the truth. The refresh holds
    /// the contact normal and the cell pairing fixed, which is accurate while bodies creep and wrong
    /// once they travel: a 1500 px/s impactor crosses a whole cell in one tick, so the manifold goes
    /// on naming the cell it has already passed. Costs one pass over bodies, of which there are
    /// three orders of magnitude fewer than cells.
    /// </remarks>
    private float ManifoldDrift()
    {
        float worst = 0f;
        int n = _s.BodyCount < _mfBodies ? _s.BodyCount : _mfBodies;
        for (int b = 0; b < n; b++)
        {
            float dx = _s.BodyX[b] - _mfX0[b], dy = _s.BodyY[b] - _mfY0[b];
            float d = SimMath.Hypot(dx, dy)
                    + SimMath.Abs(_s.BodyRot[b] - _mfRot0[b]) * _mfRad[b];
            if (d > worst) worst = d;
        }
        return worst;
    }

    /// <summary>
    /// Refreshes each contact's depth and point from how far its two cells have moved since the
    /// manifold was built, projected on the contact normal.
    /// </summary>
    /// <remarks>
    /// Exact for translation and a good approximation under the small rotations a substep produces;
    /// the measured drift the full narrow phase reported was 0.008 px per substep. The normal is
    /// held fixed for the tick. This costs a handful of operations per contact against roughly
    /// 490 ns for a SAT call.
    /// </remarks>
    private void RefreshManifold()
    {
        UpdateCenters();
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            int a = ct.A, b = ct.B;
            if (_s.Dead(a) || _s.Dead(b)) { ct.Depth = -1f; continue; }

            float dax = _s.CellPx[a] - ct.Ax0, day = _s.CellPy[a] - ct.Ay0;
            float dbx = _s.CellPx[b] - ct.Bx0, dby = _s.CellPy[b] - ct.By0;

            // The normal points from A to B, so the pair separates as (pB - pA) grows along it.
            ct.Depth = ct.Depth0 - ((dbx - dax) * ct.Nx + (dby - day) * ct.Ny);
            ct.Px += (dax + dbx) * 0.5f;
            ct.Py += (day + dby) * 0.5f;
            ct.Ax0 += dax; ct.Ay0 += day;
            ct.Bx0 += dbx; ct.By0 += dby;
            ct.Depth0 = ct.Depth;

            // The impulse accumulator resets every substep. This is XPBD, where lambda is the
            // multiplier for ONE timestep and the compliance term -alpha*lambda/h^2 represents how
            // much of the constraint this step has already answered. Carrying it into the next
            // substep makes that term suppress new impulse instead of stiffening the contact —
            // measured, it drove peak overlap from 8.8 px to 20.4 px. Warm starting is a
            // sequential-impulse idea and does not transfer to this formulation unchanged.
            ct.Ln = 0f;
            ct.Lt = 0f;

            if (ct.Depth > MaxOverlap) MaxOverlap = ct.Depth;
        }
    }

    private void EnsurePolyBuf(int n)
    {
        if (_polyAx.Length >= n) return;
        int cap = _polyAx.Length;
        while (cap < n) cap <<= 1;
        _polyAx = new float[cap]; _polyAy = new float[cap];
        _polyBx = new float[cap]; _polyBy = new float[cap];
    }

    /// <summary>Separating-axis test for two convex polygons, transcribed from the reference.</summary>
    /// <remarks>
    /// Takes spans rather than array-plus-offset so the JIT can prove every index is in range and
    /// drop the bounds checks. The inner loops make roughly three hundred element reads per call
    /// and this is the busiest routine in a crowded scene, so the checks were a measurable share of
    /// the frame rather than a micro-optimisation.
    /// </remarks>
    private static bool Sat(
        ReadOnlySpan<float> ax, ReadOnlySpan<float> ay,
        ReadOnlySpan<float> bx, ReadOnlySpan<float> by, float margin,
        out float nx, out float ny, out float depth, out float px, out float py,
        out int axes, out int projections)
    {
        float best = float.PositiveInfinity;
        float bnx = 0f, bny = 0f;
        bool have = false, flip = false;
        axes = 0; projections = 0;

        for (int side = 0; side < 2; side++)
        {
            ReadOnlySpan<float> pxs = side == 0 ? ax : bx, pys = side == 0 ? ay : by;
            ReadOnlySpan<float> qxs = side == 0 ? bx : ax, qys = side == 0 ? by : ay;
            int pn = pxs.Length, qn = qxs.Length;

            for (int i = 0; i < pn; i++)
            {
                // Wrap by comparison rather than modulo: `% pn` is an integer division, and at a
                // dozen per call over ~12k calls a tick it cost more than the projections it guards.
                int j = i + 1 == pn ? 0 : i + 1;
                float x0 = pxs[i], y0 = pys[i];
                float ex = pys[j] - y0, ey = -(pxs[j] - x0);
                float L = SimMath.Hypot(ex, ey);
                if (L < 1e-9f) continue;
                ex /= L; ey /= L;
                axes++;
                projections += pn + qn;

                float mp = float.NegativeInfinity;
                for (int v = 0; v < pn; v++)
                {
                    float d = pxs[v] * ex + pys[v] * ey;
                    if (d > mp) mp = d;
                }
                float mq = float.PositiveInfinity;
                for (int v = 0; v < qn; v++)
                {
                    float d = qxs[v] * ex + qys[v] * ey;
                    if (d < mq) mq = d;
                }
                float ov = mp - mq;
                // Separated by more than the speculative margin: no contact, and no point testing
                // the remaining axes. Inside the margin the pair is kept with a negative depth.
                if (ov <= -margin)
                {
                    nx = ny = depth = px = py = 0f;
                    return false;
                }
                if (ov < best) { best = ov; bnx = ex; bny = ey; flip = side == 1; have = true; }
            }
        }

        if (!have) { nx = ny = depth = px = py = 0f; return false; }

        nx = flip ? -bnx : bnx;
        ny = flip ? -bny : bny;
        depth = best;

        float dp = float.PositiveInfinity;
        px = 0f; py = 0f;
        for (int v = 0; v < bx.Length; v++)
        {
            float d = bx[v] * nx + by[v] * ny;
            if (d < dp) { dp = d; px = bx[v]; py = by[v]; }
        }
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  bonds
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pass one: forces from the current stretch into the cell deviation velocities.
    /// Cohesive damage enters as <c>(1 - dmg)</c> on the tangent, but COMPRESSION ALWAYS CARRIES
    /// FULL STIFFNESS — a closed crack still pushes. Because damage comes from a monotone history
    /// variable the force can never exceed the peak, so the softening branch cannot destabilise the
    /// explicit step.
    /// </summary>
    private void BondForces(float h)
    {
        if (!Par(1)) { BondForces(h, 0, _s.BodyCount, ref C); return; }
        RunOverBodies((chunk, lo, hi) => BondForces(h, lo, hi, ref _chunkC[chunk]));
    }

    /// <summary>Bond forces for a contiguous range of bodies.</summary>
    /// <remarks>
    /// <para>Iterating per body rather than over the global bond array is what makes this
    /// parallelisable: a bond only ever couples two cells of one body, so two bodies can never
    /// write to the same cell.</para>
    /// <para>It is also bit-identical to the global loop, which is not obvious and is the reason
    /// this is safe. <c>Reindex</c> fills <c>BodyBonds</c> by scanning bonds in index order, so a
    /// body's bonds appear in ascending global index; and every accumulation into a cell comes from
    /// a bond of that cell's own body. Each cell therefore receives exactly the same additions in
    /// exactly the same order as before, and float addition's non-associativity cannot bite.</para>
    /// </remarks>
    private void BondForces(float h, int bodyLo, int bodyHi, ref SolverCounters c)
    {
        for (int bi = bodyLo; bi < bodyHi; bi++)
        {
        int bondOff = _s.BodyBondOff[bi], bondLen = _s.BodyBondLen[bi];
        for (int bx = 0; bx < bondLen; bx++)
        {
            int k = _s.BodyBonds[bondOff + bx];
            bool broken = _s.BondBroken[k];
            if (broken && !_tune.CrackPush) { c.BondForceSkipped++; continue; }
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.Dead(a) || _s.Dead(b)) { c.BondForceSkipped++; continue; }
            c.BondForceVisits++;

            float kk = _s.BondK0[k], ka = _s.BondKa0[k];
            float soft = 1f - _s.BondDmg[k];
            float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];

            float fn, ft, fa;
            if (broken)
            {
                // A crack cannot pull, but it can push: full compressive stiffness while the faces
                // are pressed together, no tension, no shear. (sn is held <= 0 for a broken bond by
                // BondIntegrate, so this is the closing part only.)
                if (sn >= 0f) { c.BondForceSkipped++; continue; }
                fn = -kk * sn; ft = 0f; fa = 0f;
            }
            else
            {
                fn = -(sn > 0f ? kk * soft : kk) * sn;
                ft = -kk * soft * st;
                fa = -ka * soft * sa;
            }

            float nx = _s.BondNx[k], ny = _s.BondNy[k];
            float tx = -ny, ty = nx;
            float rax = _s.BondRax[k], ray = _s.BondRay[k];
            float rbx = _s.BondRbx[k], rby = _s.BondRby[k];

            float ix = (fn * nx + ft * tx) * h, iy = (fn * ny + ft * ty) * h;
            _s.CellDvx[a] -= ix * _s.CellIm[a];
            _s.CellDvy[a] -= iy * _s.CellIm[a];
            _s.CellDw[a] -= _s.CellIic[a] * (rax * iy - ray * ix);
            _s.CellDvx[b] += ix * _s.CellIm[b];
            _s.CellDvy[b] += iy * _s.CellIm[b];
            _s.CellDw[b] += _s.CellIic[b] * (rbx * iy - rby * ix);

            float ia = fa * h;
            _s.CellDw[a] -= ia * _s.CellIic[a];
            _s.CellDw[b] += ia * _s.CellIic[b];
        }
        }
    }

    /// <summary>
    /// Pass two: integrate the stretch from the updated deviation velocities. Two passes rather
    /// than one loop because every bond touching a cell must contribute to its velocity before any
    /// stretch is integrated from it — which is also what makes propagation at the material's wave
    /// speed emergent rather than imposed.
    /// </summary>
    private void BondIntegrate(float h)
    {
        if (!Par(2)) { BondIntegrate(h, 0, _s.BodyCount, ref C); return; }
        RunOverBodies((chunk, lo, hi) => BondIntegrate(h, lo, hi, ref _chunkC[chunk]));
    }

    /// <summary>Stretch integration for a contiguous range of bodies. See <see cref="BondForces"/>
    /// for why per-body iteration is bit-identical to the global loop.</summary>
    private void BondIntegrate(float h, int bodyLo, int bodyHi, ref SolverCounters c)
    {
        bool rateSens = _tune.RateSens > 0f;
        for (int bi = bodyLo; bi < bodyHi; bi++)
        {
        int bondOff = _s.BodyBondOff[bi], bondLen = _s.BodyBondLen[bi];
        for (int bx = 0; bx < bondLen; bx++)
        {
            int k = _s.BodyBonds[bondOff + bx];
            bool broken = _s.BondBroken[k];
            if (broken && !_tune.CrackPush) continue;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.Dead(a) || _s.Dead(b)) continue;

            float nx = _s.BondNx[k], ny = _s.BondNy[k];
            float tx = -ny, ty = nx;
            float rax = _s.BondRax[k], ray = _s.BondRay[k];
            float rbx = _s.BondRbx[k], rby = _s.BondRby[k];

            c.BondIntegrateVisits++;
            float vax = _s.CellDvx[a] - _s.CellDw[a] * ray;
            float vay = _s.CellDvy[a] + _s.CellDw[a] * rax;
            float vbx = _s.CellDvx[b] - _s.CellDw[b] * rby;
            float vby = _s.CellDvy[b] + _s.CellDw[b] * rbx;
            float rvx = vbx - vax, rvy = vby - vay, rva = _s.CellDw[b] - _s.CellDw[a];

            if (rateSens)
                _s.BondRate[k] = SimMath.Hypot(rvx, rvy) / SimMath.Max(1f, _s.BondLen[k]);

            if (broken)
            {
                // Across a crack only closing is remembered: the faces part freely, and meet again
                // from zero. Tension and shear across a crack do not exist.
                _s.BondSn[k] = SimMath.Min(0f, _s.BondSn[k] + (rvx * nx + rvy * ny) * h);
                continue;
            }
            _s.BondSn[k] += (rvx * nx + rvy * ny) * h;
            _s.BondSt[k] += (rvx * tx + rvy * ty) * h;
            _s.BondSa[k] += rva * h;
        }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  inertial loads
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Centrifugal, Euler and Coriolis loads in the rotating body frame, applied before the solve.
    /// </summary>
    /// <remarks>
    /// <para>Two corrections live here, both of which were free energy sources before they were
    /// found. <b>Centrifugal work is paid for out of rotational energy</b> — a body that expands
    /// under its own rotation slows down — because with omega treated as a free parameter the load
    /// was unbounded. <b>The Euler torque is fictitious.</b> Its field is exactly a rigid rotational
    /// acceleration, so it injects angular momentum <c>-alpha*h*Ip</c>, which
    /// <see cref="DecomposeMotion"/> then promotes into the body's own spin — and alpha is computed
    /// from that spin's change, closing a loop with gain about -1 that sits on the stability
    /// boundary and grows geometrically. The load still reaches the bonds in full (it is what shears
    /// a neck under spin-up); only its rigid component is cancelled after promotion.</para>
    /// </remarks>
    private void ApplyInertialLoads(float h)
    {
        if (!Par(4)) { ApplyInertialLoads(h, 0, _s.BodyCount, ref C); return; }
        RunOverBodies((chunk, lo, hi) => ApplyInertialLoads(h, lo, hi, ref _chunkC[chunk]));
    }

    /// <summary>Inertial loads for a contiguous range of bodies; each body is independent.</summary>
    private void ApplyInertialLoads(float h, int bodyLo, int bodyHi, ref SolverCounters ctr)
    {
        for (int b = bodyLo; b < bodyHi; b++)
        {
            float w = _s.BodyW[b], a = _s.BodyAlpha[b];
            float w2 = w * w;
            _s.BodyEulL[b] = 0f;
            ctr.InertialBodies++;
            if (w2 < 1e-12f && SimMath.Abs(a) < 1e-12f) { ctr.InertialSkipped++; continue; }

            float work = 0f, ip = 0f;
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.Dead(c)) continue;
                float vx = _s.CellDvx[c], vy = _s.CellDvy[c];
                float rx = _s.CellRx[c], ry = _s.CellRy[c];
                float ax = 0f, ay = 0f;

                if (_tune.Centrifugal)
                {
                    ax += w2 * rx; ay += w2 * ry;
                    work += _s.CellM[c] * (w2 * rx * vx + w2 * ry * vy) * h;
                }
                if (_tune.Euler)
                {
                    ax += a * ry; ay -= a * rx;
                    ip += _s.CellM[c] * (rx * rx + ry * ry);
                }
                if (_tune.Coriolis) { ax += 2f * w * vy; ay -= 2f * w * vx; }

                _s.CellDvx[c] += ax * h;
                _s.CellDvy[c] += ay * h;
                ctr.InertialCells++;
            }

            _s.BodyEulL[b] = ip != 0f ? -a * h * ip : 0f;

            if (work != 0f && _s.BodyI[b] > 0f)
            {
                float w2n = w * w - 2f * work / _s.BodyI[b];
                _s.BodyW[b] = w2n > 0f ? SimMath.Sign(w) * SimMath.Sqrt(w2n) : 0f;
            }
        }
    }

    /// <summary>
    /// Promotes the mass-weighted mean of the deviation field to rigid body motion and subtracts
    /// it. Exact: after subtraction the residual field carries zero net momentum by construction,
    /// which is what lets the contact impulse be applied at cell scale while momentum stays exact,
    /// and what makes the participating mass a contact meets rise on its own as bond support
    /// arrives at the wave speed.
    /// </summary>
    private void DecomposeMotion()
    {
        if (!Par(8)) { DecomposeMotion(0, _s.BodyCount, ref C); return; }
        RunOverBodies((chunk, lo, hi) => DecomposeMotion(lo, hi, ref _chunkC[chunk]));
    }

    /// <summary>Rigid decomposition for a contiguous range of bodies; each body is independent.</summary>
    private void DecomposeMotion(int bodyLo, int bodyHi, ref SolverCounters ctr)
    {
        for (int b = bodyLo; b < bodyHi; b++)
        {
            float px = 0f, py = 0f, L = 0f, M = 0f;
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.Dead(c)) continue;
                px += _s.CellM[c] * _s.CellDvx[c];
                py += _s.CellM[c] * _s.CellDvy[c];
                M += _s.CellM[c];
                L += _s.CellM[c] * (_s.CellRx[c] * _s.CellDvy[c] - _s.CellRy[c] * _s.CellDvx[c])
                     + _s.CellIc[c] * _s.CellDw[c];
            }
            ctr.DecomposeBodies++;
            ctr.DecomposeCells += len;
            if (M < 1e-9f) continue;
            float vx = px / M, vy = py / M, w = L / SimMath.Max(1f, _s.BodyI[b]);
            if (vx * vx + vy * vy < 1e-24f && SimMath.Abs(w) < 1e-12f) { ctr.DecomposeNoOp++; continue; }

            SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
            _s.BodyVx[b] += vx * co - vy * si;
            _s.BodyVy[b] += vx * si + vy * co;
            _s.BodyW[b] += w;

            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.Dead(c)) continue;
                _s.CellDvx[c] -= vx - w * _s.CellRy[c];
                _s.CellDvy[c] -= vy + w * _s.CellRx[c];
                _s.CellDw[c] -= w;
            }
        }
    }
}
