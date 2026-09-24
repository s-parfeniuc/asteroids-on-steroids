using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Carving v2: erosion as surface recession on touch records.
/// </summary>
/// <remarks>
/// <para>A surface vertex is the exposed end of a touch record, and the record already stores its
/// span along the seed bisector. A dent is that end sliding inward — <c>T1</c> decreasing, or
/// <c>T0</c> increasing — which shortens the interface for BOTH cells at once because they read the
/// same number. Each cell's polygon is then re-derived: its copy of the interface ends exactly at the
/// record's end, and the surface side that met the old end now meets the new one. Two neighbours'
/// surfaces therefore meet at the shared point by construction; a cut can never run along an
/// interface, because the cut is the surface, moved; and interior cells cannot erode, because they
/// have no exposed end — they can only become surface when a record is spent.</para>
///
/// <para>No direction is used. The contact gives a location and, through the rate law, a depth; the
/// depth is spread over the surface vertices near the location with a smooth kernel whose radius
/// is the material's <see cref="Material.Dent"/>. That is what makes a dent rather than a per-cell
/// notch: one contact moves several vertices, less the further they are.</para>
///
/// <para>Cells with fewer than two live records — lone rubble, or a cell hanging from one
/// neighbour — have no surface chord to move and keep the v1 direction clip, which is sound there
/// because there is no interface for it to be inconsistent with.</para>
/// </remarks>
public partial class Solver
{
    private int[] _dentRecStamp = Array.Empty<int>();
    private int[] _dentCellStamp = Array.Empty<int>();
    private int[] _dentCells = Array.Empty<int>();
    private float[] _dentAreaBefore = Array.Empty<float>();
    private int _dentStamp, _dentWalkStamp;
    private int[] _dentWalkMark = Array.Empty<int>();
    private int[] _dentQueue = Array.Empty<int>();

    internal System.Action<string>? DentTrace;
    internal int DentCalls, DentVertices, DentReclips, DentSpent, DentFallback, DentSplits, DentBudgetRefused, DentExposed, DentCollapsed, DentCorners;
    internal System.Action<string>? DentProbe; internal int DentProbeLeft = 12;
    // DIAGNOSTIC: area removed by the v1 fallback clip, split by the clip direction in the cell's
    // OWN body frame -- along the body's long axis vs across it. Not part of the model.
    internal double CarveV1Along, CarveV1Across;
    // DIAGNOSTIC: how far the carving normal sits from the actual approach direction, in 10 deg
    // buckets (index 0 = 0-10 deg = aligned with the load, index 8 = 80-90 deg = across it).
    internal readonly int[] CarveNormalAngle = new int[9];
    /// DIAGNOSTIC: set to collect CarveNormalAngle and DentContactDepth; off in the hot path.
    internal bool MeasureCarveAngles;
    /// Contacts past the penetration ceiling, and carves the backstop drove (not diagnostic-gated:
    /// two increments, and how often the guarantee is exercised is worth always knowing).
    internal long BackstopContacts, BackstopRecessions, BackstopComminuted, BackstopSoloExempt;
    /// DIAGNOSTIC: what a forced recession asked for against what it got, and why when nothing.
    internal double BackstopAsked, BackstopGot;
    internal long BackstopNothing, BackstopNoCand, BackstopOutOfReach, BackstopInsensitive, BackstopV1;
    /// DIAGNOSTIC: depth / thinner half-extent right AFTER the backstop's carve (what it controls),
    /// worst case, split by whether both sides could recede; plus how much the bodies closed in.
    internal float BackstopWorstAfterBoth, BackstopWorstAfterStuck;
    internal long BackstopStillOverBoth, BackstopStillOverStuck;
    /// DIAGNOSTIC (overlap study): carve-gate outcomes binned by penetration / smaller cell radius,
    /// bins [0,.1) [.1,.25) [.25,.5) [.5,1) [1,inf). Filled only while MeasureCarveAngles is on.
    internal readonly long[] GateOpenByPen = new long[5], GateShutByPen = new long[5];
    // DIAGNOSTIC: first-order area removed by a dent on TraceCell, bucketed by how far the moving
    // point is from the contact, in units of the kernel radius. Answers "where does the area go".
    internal readonly double[] DentAreaByU = new double[4];
    internal readonly double[] DentSlideByU = new double[4];
    internal int DentClamped, DentUnclamped;
    /// DIAGNOSTIC: set to compare the iterated budget solve against a single pass plus clamping.
    internal bool MeasureDentBudget, DentSinglePass;
    /// <summary>
    /// Carve cells with no touch record — lone rubble — through v2, using their corners, instead of
    /// dropping them to the v1 half-plane clip. On by default; the flag exists so the bench can A/B it.
    /// </summary>
    /// <remarks>
    /// The v1 fallback clips along the SAT axis, which is the axis of LEAST overlap, not the load
    /// direction: once a cell is penetrated deeper than its own narrowest width the axis flips
    /// sideways and stays there. That was a minority path for rock (2-3% of dents) but the dominant
    /// one for brittle materials, because they shatter into single-cell rubble — measured at 55.6%
    /// of all dents on a glass collide. A lone cell has no record to slide, but every vertex is a
    /// corner between two surface sides, which is exactly what GatherCornersOf takes, so v2 needs
    /// nothing new to handle it. Measured: the fallback share fell to 0.0-4.5% (the residue is
    /// triangles, which have no corners to move and still take v1), the audit stayed clean on all
    /// seven scenes, and the glass collide got 29% FASTER because a dent is cheaper than a clip plus
    /// a polygon rebuild. Glass keeps slightly more of itself: 40 bodies against 31, 175 live cells
    /// against 166.
    /// </remarks>
    internal bool DentLoneCells = true;
    /// DIAGNOSTIC: corners the kernel reached but could not use because they already sit on (or
    /// past) the chord joining their neighbours -- recession capacity a cell has permanently lost.
    internal int DentCornerAtChord, DentCornerUsable;
    /// DIAGNOSTIC: dents where every candidate hit its cap, i.e. the cell could not shed what the
    /// crush law asked for without a vertex crossing its chord (leaving the cell non-convex).
    internal int DentCapacityBound, DentTotalSolved;
    /// DIAGNOSTIC: times the rebuild retired a surface vertex the outline had passed (B2).
    internal int DentVertexConsumed;
    internal double DentTargetSum, DentRealisedSum;
    /// DIAGNOSTIC: the geometric penetration of the contact currently being carved.
    internal float DentContactDepth;
    /// DIAGNOSTIC: the three terms of contact pressure, accumulated for TraceCell only.
    internal double PressDyn, PressConf, PressDrive;
    internal int PressSamples, PressZeroPen;
    // Why a call slid nothing: no exposed end among the candidates at all, or none within reach.
    internal int DentNoOpenEnd, DentOutOfReach, DentGuarded, DentInsensitive;
    internal int DustToNeighbours, DustNoNeighbour, DustToContacts; internal double DustDevMom, DustRigidMom, DustLostMom, DustContactMom;
    internal readonly int[] DentHit = new int[11];   // removed / target, 0 .. 2+
    // Area removed per unit depth, in cell radii — the "effective contact width" the geometry gave.
    internal readonly int[] DentWidth = new int[11];   // 0 .. 2 CellRad+, bins of 0.2
    internal double DentSumRemoved, DentSumDepthLcw;    // vs v1's target depth·(perim/4)
    internal readonly int[] DentNearest = new int[11];   // nearest exposed end, in units of R (0..2+)
    private int _dentOpenSeen; private float _dentNearest;

    private void EnsureDentScratch()
    {
        if (_dentRecStamp.Length < _s.TouchCount) _dentRecStamp = new int[_s.TouchCount * 2 + 16];
        if (_dentCellStamp.Length < _s.CellCount)
        {
            _dentCellStamp = new int[_s.CellCount * 2 + 16];
            _dentCells = new int[_s.CellCount * 2 + 16];
            _dentAreaBefore = new float[_s.CellCount * 2 + 16];
            _dentWalkMark = new int[_s.CellCount * 2 + 16];
            _dentQueue = new int[_s.CellCount * 2 + 16];
        }
    }

    /// <summary>Number of live records this cell carries.</summary>
    private int LiveRecordCount(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c], n = 0;
        for (int v = 0; v < len; v++)
        {
            short r = _s.SideTouch[off + v];
            if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) continue;
            bool dup = false;
            for (int q = 0; q < v && !dup; q++) dup = _s.SideTouch[off + q] == r;
            if (!dup) n++;
        }
        return n;
    }

    // Candidate exposed points gathered for one contact, then moved together under one budget.
    private int[] _candRec = Array.Empty<int>();          // record, or -1 for a corner
    private byte[] _candEnd = Array.Empty<byte>();
    private int[] _candCell = Array.Empty<int>(), _candVert = Array.Empty<int>();   // corner: cell and vertex
    private float[] _candUx = Array.Empty<float>(), _candUy = Array.Empty<float>(), _candMax = Array.Empty<float>();
    private float[] _candK = Array.Empty<float>(), _candDa = Array.Empty<float>();
    private float[] _candSlide = Array.Empty<float>();   // solved slide; -1 while still free
    private int _candCount;

    /// <summary>Largest share of a cell's area one contact may remove in one substep — a spike guard only.</summary>
    /// <remarks>
    /// Replaces the v1 <c>CellRad/4</c> depth cap, whose purpose was momentum accounting that the
    /// compaction rule makes unnecessary. This one never binds in normal play (the depth cap bound
    /// on 0.05–0.5% of calls); it turns a bad pressure sample into a bounded loss rather than a
    /// deletion. If it ever binds, that is a signal about the rate law, not something to hide.
    /// </remarks>
    private const float DentAreaGuard = 0.5f;

    /// <summary>
    /// Recedes the surface around a contact on cell <paramref name="c"/>, removing the area the
    /// rate law asks for, spread over the exposed points the material's dent radius reaches.
    /// </summary>
    /// <remarks>
    /// <para><b>Area-targeted.</b> The rate law's <paramref name="depth"/> is a recession distance;
    /// the area it stands for is <c>depth · perim/4</c>, v1's target, so <c>CrushRate</c> keeps its
    /// calibration. Moving an exposed point removes area at a rate that depends on the geometry
    /// around it — a face pivoting about its far end sweeps <c>½·L·sinθ</c> per unit slide — and
    /// measured before this, the same depth removed anywhere from under 0.2 to over 2 cell radii of
    /// area per pixel. So every candidate's area per unit motion is computed from the current
    /// geometry (first-order exact), and one scale <c>λ = A* / Σ dA/ds·k</c> sets the motions. The
    /// kernel decides <i>where</i>; the pressure decides <i>how much</i>.</para>
    /// </remarks>
    /// <returns>Area removed from <paramref name="c"/> itself (neighbours' losses are shed by this
    /// call too), or −1 when the cell has no surface chord and the caller should fall back.</returns>
    internal float DentAt(int c, int other, float depth, float wpx, float wpy)
    {
        // A lone cell has no record to slide, but every one of its vertices is a corner between two
        // surface sides, which is exactly what GatherCornersOf wants. A triangle still has none
        // (GatherCornersOf needs four), so it keeps v1.
        bool lone = LiveRecordCount(c) < 1;
        if (lone && (!DentLoneCells || _s.PolyLen[c] < 4)) { DentFallback++; return -1f; }
        EnsureDentScratch();
        int stamp = ++_dentStamp;
        DentCalls++;

        int bi = _s.CellBody[c];
        BodyTrig(bi, out float si, out float co);
        float dx = wpx - _s.BodyX[bi], dy = wpy - _s.BodyY[bi];
        float lx = dx * co + dy * si, ly = -dx * si + dy * co;          // contact, body-local
        float radius = SimMath.Max(1e-3f, _s.Mat(c).Dent * MeanEdge(c));

        // ── gather every exposed point the kernel reaches ─────────────────────
        // Walk the adjacency outward while cells are within reach of the contact point: bodies
        // interpenetrate one to two layers, so the surface is often two or three rings out.
        _candCount = 0;
        _dentOpenSeen = 0; _dentNearest = float.PositiveInfinity;
        int walkStamp = ++_dentWalkStamp;
        int head = 0, tail = 0;
        _dentQueue[tail++] = c; _dentWalkMark[c] = walkStamp;
        while (head < tail)
        {
            int cc = _dentQueue[head++];
            GatherEndsOf(cc, lx, ly, radius, stamp);
            GatherCornersOf(cc, lx, ly, radius);
            int coff = _s.PolyOff[cc], clen = _s.PolyLen[cc];
            for (int v = 0; v < clen; v++)
            {
                short r = _s.SideTouch[coff + v];
                if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) continue;
                int o = _s.TouchOther(r, cc);
                if (o < 0 || o >= _s.CellCount || _s.Dead(o) || _dentWalkMark[o] == walkStamp) continue;
                float ox = _s.CellRx[o] - lx, oy = _s.CellRy[o] - ly;
                if (SimMath.Hypot(ox, oy) > radius + _s.CellRad[o]) continue;
                _dentWalkMark[o] = walkStamp;
                if (tail < _dentQueue.Length) _dentQueue[tail++] = o;
            }
        }

        // Nothing the kernel could move: a lone cell has no second chance from a later record, so
        // hand it back to v1 rather than leave it uncarved.
        if (_candCount == 0 && lone) { DentFallback++; return -1f; }

        int nCells = 0;
        if (_candCount == 0)
        {
            if (_dentOpenSeen == 0) DentNoOpenEnd++; else DentOutOfReach++;
        }
        if (_dentOpenSeen > 0) DentNearest[(int)SimMath.Min(10f, _dentNearest * 5f)]++;

        // ── one budget, one scale ─────────────────────────────────────────────
        float target = depth * SimMath.Max(1f, _s.CellPerim[c] * 0.25f);
        float guard = DentAreaGuard * _s.CellArea[c];
        if (target > guard) { DentGuarded++; target = guard; }
        float sens = 0f;
        for (int i = 0; i < _candCount; i++) sens += _candDa[i] * _candK[i];
        float lambda = sens > 1e-6f ? target / sens : 0f;
        // No candidate removes area at first order (every reachable end is a corner of a cell
        // behind): nothing to scale against, so nothing moves this substep. The next contact, once
        // a face has receded, will find sensitivity.
        if (lambda <= 0f) { DentInsensitive++; return 0f; }

        // ── ONE BUDGET, SOLVED WITH THE CAPS INSIDE IT ───────────────────────
        // lambda scales every candidate's slide, but a corner may not pass the chord joining its
        // neighbours and nothing may cross a whole cell in one substep. Solving once and clamping
        // afterwards drops the clamped candidates' share of the budget on the floor: measured over
        // five consecutive dents on the rod's tip cell, only 47-62% of the area the crush law asked
        // for was ever removed, so the cell eroded at about half the rate its material specifies.
        //
        // So the solve is iterated. Whoever saturates is charged for the area it ACTUALLY removes,
        // and lambda is re-solved for the rest against what is left. That is also what makes this a
        // dent rather than a contraction: the vertex under the contact runs to its chord, and the
        // budget it cannot spend flows to its neighbours, which then recede together — the face
        // flattens where it was struck instead of the whole polygon shrinking.
        if (DentSinglePass)
        {
            for (int i = 0; i < _candCount; i++)
                _candSlide[i] = SimMath.Min(lambda * _candK[i], CandCap(i, c));
        }
        else SolveSlides(c, target);

        if (MeasureDentBudget)
        {
            float got = 0f;
            for (int i = 0; i < _candCount; i++) got += _candDa[i] * _candSlide[i];
            DentTargetSum += target; DentRealisedSum += got;
            DentTotalSolved++;
            if (got < 0.95f * target) DentCapacityBound++;
        }

        if (DentTrace != null && c == TraceCell)
        {
            float dent = _s.Mat(c).Dent, crad = _s.CellRad[c], pen = SimMath.Max(0f, DentContactDepth);
            // Candidate scales that do NOT depend on the contact solver's residual overlap.
            float rB = 0.5f * crad;                       // half the carved cell's radius
            float rC = 0.25f * crad;                      // a quarter of it
            float meanEdge = 0f;                          // the cell's own feature size
            {
                int eo = _s.PolyOff[c], el = _s.PolyLen[c];
                for (int v = 0; v < el; v++)
                {
                    int nv2 = v + 1 == el ? 0 : v + 1;
                    meanEdge += SimMath.Hypot(_s.PolyX[eo + nv2] - _s.PolyX[eo + v], _s.PolyY[eo + nv2] - _s.PolyY[eo + v]);
                }
                meanEdge = el > 0 ? meanEdge / el : crad;
            }
            float rD = SimMath.Max(1e-3f, meanEdge);
            float realised = 0f;
            for (int i = 0; i < _candCount; i++) realised += _candDa[i] * _candSlide[i];
            DentTrace($"  dent on {c}: recession {depth:F3} target {target:F2} realised {realised:F2}"
                + $" ({100f * realised / SimMath.Max(1e-6f, target):F1}%), {_candCount} candidates;"
                + $" kernel radius {radius:F1} (Dent {_s.Mat(c).Dent:F2} x meanEdge {MeanEdge(c):F1}),"
                + $" contact local ({lx:F1},{ly:F1}), cell centre ({_s.CellRx[c]:F1},{_s.CellRy[c]:F1})");
            for (int i = 0; i < _candCount; i++)
            {
                float vx2, vy2;
                string what;
                if (_candRec[i] >= 0)
                {
                    RecordBisector(_candRec[i], out float ex3, out float ey3, out float mx3, out float my3);
                    float t3 = _candEnd[i] == 0 ? _s.TouchT0[_candRec[i]] : _s.TouchT1[_candRec[i]];
                    vx2 = mx3 + t3 * ex3; vy2 = my3 + t3 * ey3;
                    what = $"end{_candEnd[i]} of rec {_candRec[i]} (with {_s.TouchOther(_candRec[i], _s.TouchA[_candRec[i]])})";
                }
                else
                {
                    int o3 = _s.PolyOff[_candCell[i]];
                    vx2 = _s.CellRx[_candCell[i]] + _s.PolyX[o3 + _candVert[i]];
                    vy2 = _s.CellRy[_candCell[i]] + _s.PolyY[o3 + _candVert[i]];
                    what = $"corner of cell {_candCell[i]} v{_candVert[i]}";
                }
                float dist = SimMath.Hypot(vx2 - lx, vy2 - ly);
                
                
                DentTrace($"    dist {dist,6:F2} u {dist / radius,5:F3} -> k {_candK[i]:F3}"
                    + $" | dA/ds {_candDa[i],7:F2} | cap {CandCap(i, c),7:F2}"
                    + $" | slide {_candSlide[i],6:F3} | {what}");
            }
        }
        if (c == TraceCell)
        {
            for (int i = 0; i < _candCount; i++)
            {
                // Recover u from k: k = 0.5(1+cos(pi u)) is monotone, so compare against the same
                // thresholds the kernel would give at u = 0.1, 0.3, 0.6.
                int bu = _candK[i] >= 0.9755f ? 0 : _candK[i] >= 0.7939f ? 1 : _candK[i] >= 0.3455f ? 2 : 3;
                DentAreaByU[bu] += (double)_candDa[i] * _candSlide[i];
                DentSlideByU[bu] += _candSlide[i];
            }
        }
        for (int i = 0; i < _candCount; i++)
        {
            float slide = _candSlide[i];
            if (slide <= 0f) continue;
            if (_candRec[i] >= 0) ApplyEnd(_candRec[i], _candEnd[i], slide, stamp, ref nCells);
            else ApplyCorner(_candCell[i], _candVert[i], slide, _candUx[i], _candUy[i], stamp, ref nCells);
        }

        // ── re-derive every cell whose records moved, and shed what they lost ─
        float removedSelf = 0f, removedAll = 0f;
        for (int i = 0; i < nCells; i++)
        {
            int cc = _dentCells[i];
            float before = _dentAreaBefore[i];
            ReclipToRecords(cc);
            DentReclips++;
            float lost = SimMath.Max(0f, before - _s.CellArea[cc]);
            if (lost <= 0f) continue;
            removedAll += lost;
            if (cc == c) removedSelf = lost;
            ShedMass(cc, other, lost / before);
        }
        if (nCells > 0 && depth > 0f)
        {
            DentSumRemoved += removedAll;
            DentSumDepthLcw += depth * SimMath.Max(1f, _s.CellPerim[c] * 0.25f);
            DentWidth[(int)SimMath.Min(10f, removedAll / depth / _s.CellRad[c] * 5f)]++;
            DentHit[(int)SimMath.Min(10f, removedAll / target * 5f)]++;
        }
        return removedSelf;
    }

    /// <summary>Records one cell's exposed record ends within the kernel, with their area sensitivity.</summary>
    private void GatherEndsOf(int c, float lx, float ly, float radius, int stamp)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++)
        {
            short r = _s.SideTouch[off + v];
            if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) continue;
            if (_dentRecStamp[r] == stamp) continue;              // one visit per record per contact
            _dentRecStamp[r] = stamp;

            byte open = _s.TouchOpen[r];
            if (open == 0) continue;
            if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) continue;

            for (int end = 0; end < 2; end++)
            {
                if ((open & (end == 0 ? SimState.TouchOpen0 : SimState.TouchOpen1)) == 0) continue;
                float t = end == 0 ? _s.TouchT0[r] : _s.TouchT1[r];
                float vx = mx + t * ex, vy = my + t * ey;                  // the end, body-local
                float u = SimMath.Hypot(vx - lx, vy - ly) / radius;
                _dentOpenSeen++; if (u < _dentNearest) _dentNearest = u;
                if (u >= 1f) continue;
                float k = 0.5f * (1f + SimMath.Cos(u * 3.14159265f));     // raised cosine
                if (k <= 0f) continue;

                // Inward along the record: from this end toward the other.
                float ux = end == 0 ? ex : -ex, uy = end == 0 ? ey : -ey;
                float da = EndAreaRate(r, vx, vy, ux, uy);

                if (_candCount >= _candRec.Length) GrowCandidates();
                _candRec[_candCount] = r; _candEnd[_candCount] = (byte)end; _candCell[_candCount] = -1;
                _candK[_candCount] = k; _candDa[_candCount] = da;
                _candCount++;
                DentVertices++;
            }
        }
    }

    /// <summary>
    /// Area swept per unit slide of a record end, summed over the cells for which that end is a
    /// surface vertex: half the cross product of the slide direction with the adjacent surface side.
    /// </summary>
    /// <remarks>
    /// For a cell whose sides at the end are both records — the cell behind a receding pair — the
    /// end contributes nothing at first order: it loses only the chord across its corner, which is
    /// quadratic in the slides. That is why such cells do not erode before their neighbours do.
    /// </remarks>
    private float EndAreaRate(int r, float vx, float vy, float ux, float uy)
    {
        float da = 0f;
        for (int side = 0; side < 2; side++)
        {
            int c = side == 0 ? _s.TouchA[r] : _s.TouchB[r];
            if (c < 0 || c >= _s.CellCount || _s.Dead(c)) continue;
            int off = _s.PolyOff[c], len = _s.PolyLen[c];
            for (int v = 0; v < len; v++)
            {
                if (_s.SideTouch[off + v] != r) continue;
                int w = v + 1 == len ? 0 : v + 1;
                // Which vertex of this side is the end? The nearer one.
                float d0 = SimMath.Hypot(_s.CellRx[c] + _s.PolyX[off + v] - vx, _s.CellRy[c] + _s.PolyY[off + v] - vy);
                float d1 = SimMath.Hypot(_s.CellRx[c] + _s.PolyX[off + w] - vx, _s.CellRy[c] + _s.PolyY[off + w] - vy);
                int adj, far;
                if (d0 <= d1) { adj = v == 0 ? len - 1 : v - 1; far = adj; }        // side before; its start vertex
                else          { adj = w; far = w + 1 == len ? 0 : w + 1; }           // side after; its end vertex
                if (_s.SideTouch[off + adj] >= 0) continue;                           // a record side: nothing swept here
                float px = _s.CellRx[c] + _s.PolyX[off + far] - vx, py = _s.CellRy[c] + _s.PolyY[off + far] - vy;
                da += 0.5f * SimMath.Abs(ux * py - uy * px);
            }
        }
        return da;
    }

    /// <summary>
    /// Records one cell's interface-less corners within the kernel: vertices between two surface
    /// sides, which recede toward the cell's centroid.
    /// </summary>
    /// <remarks>
    /// <para>A corner belongs to one cell — no record, so nothing to keep consistent with a
    /// neighbour — and carries no state beyond its position. It is any vertex between two surface
    /// sides: a build-time outline corner, the two vertices left where a comminuted neighbour's side
    /// was promoted, a detached fragment's cut face. It is what gives a one-record cell — a two-cell
    /// fragment — a movable surface at all, and it is why the v1 clip is no longer needed there.</para>
    ///
    /// <para><b>Moved, not cut.</b> The vertex itself moves toward the centroid. The two sides at it
    /// tilt inward; no vertex is added; convexity holds because the moved vertex stays inside the
    /// polygon and its neighbours' turns stay on the inner side of their edges — until it reaches
    /// the chord between its neighbours, where it is consumed. The area removed is
    /// <c>½·|u × (P₂ − P₁)|</c> per unit motion — linear, so a corner is just another exposed point
    /// in the same budget as record ends. A plane cut at the corner would remove <c>½·δ²·(tan a +
    /// tan b)</c> — quadratic, and two new vertices per cut.</para>
    ///
    /// <para>When two adjacent corners both move, the side between them translates inward: a face
    /// receding along its normal, which arrives here for free.</para>
    /// </remarks>
    /// <summary>DIAGNOSTIC: body-local position of candidate <paramref name="j"/>.</summary>
    private float CandX(int j)
    {
        if (_candRec[j] < 0) return _s.CellRx[_candCell[j]] + _s.PolyX[_s.PolyOff[_candCell[j]] + _candVert[j]];
        RecordBisector(_candRec[j], out float ex, out float _, out float mx, out float _2);
        return mx + (_candEnd[j] == 0 ? _s.TouchT0[_candRec[j]] : _s.TouchT1[_candRec[j]]) * ex;
    }

    private float CandY(int j)
    {
        if (_candRec[j] < 0) return _s.CellRy[_candCell[j]] + _s.PolyY[_s.PolyOff[_candCell[j]] + _candVert[j]];
        RecordBisector(_candRec[j], out float _, out float ey, out float _2, out float my);
        return my + (_candEnd[j] == 0 ? _s.TouchT0[_candRec[j]] : _s.TouchT1[_candRec[j]]) * ey;
    }

    private void GatherCornersOf(int c, float lx, float ly, float radius)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 4) return;                                        // a triangle's corners cannot recede
        for (int v = 0; v < len; v++)
        {
            int pv = v == 0 ? len - 1 : v - 1, nv = v + 1 == len ? 0 : v + 1;
            if (_s.SideTouch[off + pv] >= 0 || _s.SideTouch[off + v] >= 0) continue;   // a record end, not a corner

            float kx = _s.PolyX[off + v], ky = _s.PolyY[off + v];           // cell-local: centroid is the origin
            float u = SimMath.Hypot(_s.CellRx[c] + kx - lx, _s.CellRy[c] + ky - ly) / radius;
            if (u >= 1f) continue;
            float k = 0.5f * (1f + SimMath.Cos(u * 3.14159265f));
            if (k <= 0f) continue;

            float kl = SimMath.Hypot(kx, ky);
            if (kl < 1e-4f) continue;
            float ux = -kx / kl, uy = -ky / kl;                             // toward the centroid

            // Area per unit motion, and how far it may go before crossing the chord P1–P2.
            float p1x = _s.PolyX[off + pv], p1y = _s.PolyY[off + pv];
            float p2x = _s.PolyX[off + nv], p2y = _s.PolyY[off + nv];
            float cx = p2x - p1x, cy = p2y - p1y;
            float cross = ux * cy - uy * cx;
            float da = 0.5f * SimMath.Abs(cross);
            if (da <= 1e-6f) continue;                                      // moving along the chord: no area
            float toChord = -((kx - p1x) * cy - (ky - p1y) * cx) / cross;   // motion at which K lands on the chord
            if (toChord <= 0f) { DentCornerAtChord++; continue; }
            DentCornerUsable++;

            if (_candCount >= _candRec.Length) GrowCandidates();
            _candRec[_candCount] = -1; _candEnd[_candCount] = 0;
            _candCell[_candCount] = c; _candVert[_candCount] = v;
            _candUx[_candCount] = ux; _candUy[_candCount] = uy; _candMax[_candCount] = toChord;
            _candK[_candCount] = k; _candDa[_candCount] = da;
            _candCount++;
            DentCorners++;
        }
    }

    /// <summary>Moves a corner toward its centroid; the rebuild consumes it if it reaches the chord.</summary>
    private void ApplyCorner(int c, int v, float slide, float ux, float uy, int stamp, ref int nCells)
    {
        if (slide <= 0f || c < 0 || c >= _s.CellCount || _s.Dead(c) || v >= _s.PolyLen[c]) return;
        int off = _s.PolyOff[c];
        _s.PolyX[off + v] += ux * slide;
        _s.PolyY[off + v] += uy * slide;
        MarkDentCell(c, stamp, ref nCells);
    }

    /// <summary>What a polygon vertex is to carving v2. For the viewer.</summary>
    public enum VertexKind
    {
        /// <summary>Between two record sides, neither end exposed: cannot move.</summary>
        Interior,
        /// <summary>An exposed record end: slides along its interface under load.</summary>
        ExposedEnd,
        /// <summary>Between two surface sides: moves toward the centroid under load.</summary>
        Corner,
        /// <summary>A corner of a triangle: cannot recede (nothing to consume it into).</summary>
        FixedCorner,
    }

    /// <summary>Classifies vertex <paramref name="v"/> of cell <paramref name="c"/> exactly as the driver does.</summary>
    public VertexKind ClassifyVertex(int c, int v)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (v < 0 || v >= len) return VertexKind.Interior;
        int pv = v == 0 ? len - 1 : v - 1;
        short rp = _s.SideTouch[off + pv], rn = _s.SideTouch[off + v];
        bool prevRec = rp >= 0 && rp < _s.TouchCount && _s.TouchA[rp] >= 0;
        bool nextRec = rn >= 0 && rn < _s.TouchCount && _s.TouchA[rn] >= 0;
        if (!prevRec && !nextRec) return len < 4 ? VertexKind.FixedCorner : VertexKind.Corner;

        // A record end is exposed at this vertex if either record here is open at the end that
        // sits on it — the same nearest-parameter test the driver and the rebuild use.
        if (prevRec && EndOpenAt(c, pv, v, rp)) return VertexKind.ExposedEnd;
        if (nextRec && EndOpenAt(c, v, v, rn)) return VertexKind.ExposedEnd;
        return VertexKind.Interior;
    }

    private bool EndOpenAt(int c, int side, int vert, short r)
    {
        if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) return false;
        int off = _s.PolyOff[c];
        float u = (_s.CellRx[c] + _s.PolyX[off + vert] - mx) * ex + (_s.CellRy[c] + _s.PolyY[off + vert] - my) * ey;
        bool isT0 = SimMath.Abs(u - _s.TouchT0[r]) <= SimMath.Abs(u - _s.TouchT1[r]);
        return (_s.TouchOpen[r] & (isT0 ? SimState.TouchOpen0 : SimState.TouchOpen1)) != 0;
    }

    /// <summary>
    /// The length the dent kernel is built on: the cell's mean polygon edge.
    /// </summary>
    /// <remarks>
    /// <para>The kernel has to tell one vertex of a cell from the next, because that is what decides
    /// whether a face is planed off where it is pressed or the whole cell contracts toward its
    /// centroid. Sized off <c>CellRad</c> it could not: the circumradius runs ~1.75x the spacing
    /// between neighbouring vertices on a median cell, so with any multiplier above about 0.6 the
    /// whole polygon sat in the flat top of the raised cosine and every corner drew near-equal
    /// weight. Measured on the rod's tip cell the three front vertices came out at k 1.000, 0.952
    /// and 0.895 — no ordering at all — and the cell shrank self-similarly to nothing rather than
    /// flattening at the tip.</para>
    /// <para>The mean edge is the polygon's OWN feature size. It shrinks with the features as a cell
    /// erodes, and it separates shapes: 0.87x the circumradius for a regular heptagon, 0.51x for the
    /// rod's elongated tip cell, so a pointy cell gets a proportionally tighter kernel with no extra
    /// tuning. Measured over three scenes it is also the steadiest candidate against vertex spacing
    /// (IQR/median 58-70%, against 76-89% for the circumradius, the polygon diameter and
    /// sqrt(area)), which is what lets one constant per material work across cell shapes.</para>
    /// <para>The diameter was measured and rejected: at 1.85x the circumradius with that factor
    /// holding to ±5% across three scenes and both shape classes, it is a reparameterisation of
    /// <see cref="Material.Dent"/>, not an adaptation — and it would need a cached per-cell array
    /// kept in step with every polygon rewrite.</para>
    /// <para>Derived, never stored. <c>CellPerim</c> and <c>PolyLen</c> are both refreshed by
    /// <c>RefreshCellShape</c>, which every carving path already calls, so this is exactly as fresh
    /// as <c>CellRad</c> with no new state and no new invariant to audit.</para>
    /// </remarks>
    private float MeanEdge(int c)
    {
        int n = _s.PolyLen[c];
        return n > 0 ? _s.CellPerim[c] / n : _s.CellRad[c];
    }

    /// <summary>The furthest one candidate may slide: a corner stops at the chord joining its
    /// neighbours, and nothing crosses a whole cell in one substep.</summary>
    private float CandCap(int i, int c)
        => _candRec[i] >= 0 ? _s.CellRad[c] : SimMath.Min(_s.CellRad[c], _candMax[i]);

    /// <summary>
    /// Spends one area budget as slide across the candidates, respecting each one's cap and
    /// re-spending what a capped candidate could not take. See the call site for why.
    /// </summary>
    /// <remarks>Three passes then a final share-out: each pass either finishes or retires at least
    /// one candidate, so it terminates, and it walks the candidates in their build order, so it is
    /// deterministic.</remarks>
    private void SolveSlides(int c, float target)
    {
        for (int i = 0; i < _candCount; i++) _candSlide[i] = -1f;      // -1 = still free
        float remaining = target;
        int settled = 0;

        for (int pass = 0; pass < 3 && settled < _candCount; pass++)
        {
            float sens = 0f;
            for (int i = 0; i < _candCount; i++)
                if (_candSlide[i] < 0f) sens += _candDa[i] * _candK[i];
            if (sens <= 1e-6f || remaining <= 0f) break;

            float lam = remaining / sens;
            bool clamped = false;
            for (int i = 0; i < _candCount; i++)
            {
                if (_candSlide[i] >= 0f) continue;
                float cap = CandCap(i, c);
                if (lam * _candK[i] <= cap) continue;
                _candSlide[i] = cap;
                remaining -= _candDa[i] * cap;
                settled++;
                clamped = true;
                DentClamped++;
            }
            if (clamped) continue;

            for (int i = 0; i < _candCount; i++)                        // nobody saturates: done
                if (_candSlide[i] < 0f) { _candSlide[i] = lam * _candK[i]; settled++; DentUnclamped++; }
            break;
        }

        if (settled >= _candCount) return;
        float s2 = 0f;                                                  // whatever is left, shared out
        for (int i = 0; i < _candCount; i++)
            if (_candSlide[i] < 0f) s2 += _candDa[i] * _candK[i];
        float lam2 = s2 > 1e-6f && remaining > 0f ? remaining / s2 : 0f;
        for (int i = 0; i < _candCount; i++)
            if (_candSlide[i] < 0f) _candSlide[i] = SimMath.Min(lam2 * _candK[i], CandCap(i, c));
    }

    private void GrowCandidates()
    {
        int n = System.Math.Max(64, _candRec.Length * 2);
        Array.Resize(ref _candRec, n); Array.Resize(ref _candEnd, n);
        Array.Resize(ref _candK, n); Array.Resize(ref _candDa, n);
        Array.Resize(ref _candCell, n); Array.Resize(ref _candVert, n);
        Array.Resize(ref _candUx, n); Array.Resize(ref _candUy, n); Array.Resize(ref _candMax, n);
        Array.Resize(ref _candSlide, n);
    }

    /// <summary>Slides one record end inward, keeps the bond's length honest, and retires a spent record.</summary>
    private void ApplyEnd(int r, int end, float slide, int stamp, ref int nCells)
    {
        if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) return;
        if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) return;

        float t0 = _s.TouchT0[r], t1 = _s.TouchT1[r];
        if (end == 0) _s.TouchT0[r] = SimMath.Min(t1, t0 + slide);
        else          _s.TouchT1[r] = SimMath.Max(t0, t1 - slide);

        // ── THE BOND IS ONLY AS STRONG AS THE INTERFACE THAT IS LEFT ─────────
        // The bond's length is the interface's length, so erosion shortens its bending lever — but
        // its STIFFNESS has to follow too, or a bond whose interface has been eaten down to a third
        // still pulls with the force of a whole one. Measured on the grain-170 collide: eight live
        // bonds had eroded to as little as 63% of their built interface and every one of them still
        // carried its built stiffness.
        //
        // Stiffness scales with the bonded length; the failure STRETCH does not. A cohesive law has
        // traction = k·s with both k and the peak traction proportional to area, so s0 = peak/k is
        // area-independent — an eroded bond fails at the same opening, just carrying less force.
        // Scaled by the ratio rather than recomputed, so the product telescopes to len/len0 exactly.
        short bk = _s.TouchBond[r];
        if (bk >= 0 && bk < _s.BondCount)
        {
            float oldLen = _s.BondLen[bk];
            float newLen = SimMath.Max(1e-3f, _s.TouchT1[r] - _s.TouchT0[r]);
            _s.BondLen[bk] = newLen;
            if (oldLen > 1e-6f && newLen < oldLen)
            {
                float f = newLen / oldLen;
                _s.BondK0[bk] *= f;
                // The ANGULAR stiffness carries two more powers of the length. BodyBuilder sets
                // Ka0 = K0 * L^2 / 12, the second moment of a line of springs spread over the
                // interface, so holding that through erosion needs Ka0 *= f^3: K0 already takes one
                // f, and the lever arm takes the other two. Scaling it by f alone left an eroded
                // bond resisting bending by 1/f^2 too much — measured at 5.72x on an interface worn
                // to 41.8%, with the drift matching 1/f^2 to the digit on five scenes. Wrong in the
                // direction that matters: a deeply carved interface should go floppy, not stay
                // rigid. f^3 telescopes to (len/len0)^3 exactly, the same way f does for K0.
                _s.BondKa0[bk] *= f * f * f;
            }
        }

        MarkDentCell(_s.TouchA[r], stamp, ref nCells);
        MarkDentCell(_s.TouchB[r], stamp, ref nCells);

        // Slid to nothing: the material joining the two cells is gone and the adjacency ends. The
        // spent point is written into both cells' copies of the side FIRST: once the record is
        // unlinked the rebuild has nothing to place the side by, and a stub left behind is a notch
        // waiting to open. At zero length the rebuild merges it and the point is one vertex for all.
        if (_s.TouchT1[r] - _s.TouchT0[r] <= 1e-3f)
        {
            float tsp = _s.TouchT0[r];
            float wx = mx + tsp * ex, wy = my + tsp * ey;
            for (int side = 0; side < 2; side++)
            {
                int cc = side == 0 ? _s.TouchA[r] : _s.TouchB[r];
                if (cc < 0 || cc >= _s.CellCount || _s.Dead(cc)) continue;
                int coff = _s.PolyOff[cc], clen = _s.PolyLen[cc];
                for (int sv = 0; sv < clen; sv++)
                {
                    if (_s.SideTouch[coff + sv] != r) continue;
                    int sw = sv + 1 == clen ? 0 : sv + 1;
                    _s.PolyX[coff + sv] = wx - _s.CellRx[cc]; _s.PolyY[coff + sv] = wy - _s.CellRy[cc];
                    _s.PolyX[coff + sw] = wx - _s.CellRx[cc]; _s.PolyY[coff + sw] = wy - _s.CellRy[cc];
                }
            }
            DentSpent++;
            RetireTouch(r, "dent-spent");
        }
    }

    private void MarkDentCell(int c, int stamp, ref int nCells)
    {
        if (c < 0 || c >= _s.CellCount || _s.Dead(c)) return;
        if (_dentCellStamp[c] == stamp) return;
        _dentCellStamp[c] = stamp;
        _dentCells[nCells] = c;
        _dentAreaBefore[nCells] = _s.CellArea[c];
        nCells++;
    }

    /// <summary>
    /// Re-derives a cell's polygon from its records: every side that carries a record ends exactly
    /// at that record's current ends, and any surface vertex the new outline has passed is dropped.
    /// </summary>
    /// <remarks>
    /// <para>Not a clip. An exposed record end's new position is written directly from the record,
    /// so both cells of the record write the same body-frame point and their copies agree to
    /// rounding rather than to a tolerance. Interior ends are never written: an interior vertex is
    /// shared with another record's side, and the two records describe it with parameters that
    /// agree only to rounding.</para>
    ///
    /// <para><b>Splitting.</b> Once a record is spent, the vertex it ended at is surface, and the
    /// two records that also end there are open at that end. When they slide, they slide along
    /// different lines, so the one vertex has to become two, with a new surface side between them —
    /// the bare stretch the cell behind presents as its neighbours recede. The polygon is rebuilt
    /// from its sides so that this happens exactly where two open record ends stop coinciding.</para>
    ///
    /// <para>A surface vertex between two surface sides is left where it is unless the moved outline
    /// has passed inside it, in which case it is consumed.</para>
    /// </remarks>
    private void ReclipToRecords(int c)
    {
        int off = _s.PolyOff[c];
        MergeSpentSides(c);
        int len = _s.PolyLen[c];
        if (len < 3) return;
        EnsureClipScratch(len * 2 + 4);

        // Target position of each record side's start and end, when that end is exposed.
        int n = 0;
        for (int v = 0; v < len; v++)
        {
            int pv = v == 0 ? len - 1 : v - 1;
            bool haveA = SideEndTarget(c, pv, false, out float ax, out float ay);   // previous side's END
            bool haveB = SideEndTarget(c, v, true, out float bx, out float by);     // this side's START
            float x = _s.PolyX[off + v], y = _s.PolyY[off + v];

            // Two open ends at one vertex have genuinely parted only past the noise floor: two records
            // describe the same vertex with parameters that agree to rounding, and at 1e-4 the split
            // fired on a 0.04 px disagreement, leaving a bare sliver of pure noise on an uncarved cell.
            if (haveA && haveB && SimMath.Hypot(ax - bx, ay - by) > 0.05f)
            {
                // Two open ends that no longer coincide: the vertex has become two, with a bare side
                // between them. For the cell behind a receding pair this is the chord across its
                // corner — the third wall of the pit its neighbours' pivoting faces have opened.
                _clipX[n] = ax; _clipY[n] = ay; _clipB[n] = SimState.SideReal; _clipS[n] = -1; n++;
                _clipX[n] = bx; _clipY[n] = by; _clipB[n] = _s.PolyBond[off + v]; _clipS[n] = _s.SideTouch[off + v]; n++;
                DentSplits++;
                continue;
            }
            if (haveB) { x = bx; y = by; } else if (haveA) { x = ax; y = ay; }
            _clipX[n] = x; _clipY[n] = y; _clipB[n] = _s.PolyBond[off + v]; _clipS[n] = _s.SideTouch[off + v]; n++;
        }

        // Consume surface vertices the outline has passed: a vertex between two surface sides that
        // now turns the wrong way lies inside the chord its neighbours make.
        for (bool dropped = true; dropped && n > 3;)
        {
            dropped = false;
            for (int v = 0; v < n && n > 3; v++)
            {
                int pv = v == 0 ? n - 1 : v - 1, nv = v + 1 == n ? 0 : v + 1;
                if (_clipS[pv] >= 0 || _clipS[v] >= 0) continue;
                float ux = _clipX[v] - _clipX[pv], uy = _clipY[v] - _clipY[pv];
                float wx = _clipX[nv] - _clipX[v], wy = _clipY[nv] - _clipY[v];
                if (ux * wy - uy * wx >= 0f) continue;
                for (int q = v; q + 1 < n; q++)
                {
                    _clipX[q] = _clipX[q + 1]; _clipY[q] = _clipY[q + 1];
                    _clipB[q] = _clipB[q + 1]; _clipS[q] = _clipS[q + 1];
                }
                n--; dropped = true; v--; DentVertexConsumed++;
            }
        }

        // Fit the budget: a split adds a vertex. Drop the least significant SURFACE vertex until it
        // fits; record sides are never dropped, they are the geometry.
        while (n > _s.PolyCap[c])
        {
            int best = -1; float bestArea = float.PositiveInfinity;
            for (int v = 0; v < n; v++)
            {
                int pv = v == 0 ? n - 1 : v - 1, nv = v + 1 == n ? 0 : v + 1;
                if (_clipS[pv] >= 0 || _clipS[v] >= 0) continue;
                float a = SimMath.Abs((_clipX[v] - _clipX[pv]) * (_clipY[nv] - _clipY[pv]) - (_clipY[v] - _clipY[pv]) * (_clipX[nv] - _clipX[pv]));
                if (a < bestArea) { bestArea = a; best = v; }
            }
            if (best < 0) { DentBudgetRefused++; return; }
            for (int q = best; q + 1 < n; q++)
            {
                _clipX[q] = _clipX[q + 1]; _clipY[q] = _clipY[q + 1];
                _clipB[q] = _clipB[q + 1]; _clipS[q] = _clipS[q + 1];
            }
            n--;
        }

        Array.Copy(_clipX, 0, _s.PolyX, off, n);
        Array.Copy(_clipY, 0, _s.PolyY, off, n);
        Array.Copy(_clipB, 0, _s.PolyBond, off, n);
        Array.Copy(_clipS, 0, _s.SideTouch, off, n);
        _s.PolyLen[c] = n;

        RefreshCellShape(c);
    }

    /// <summary>
    /// A spent interface is a vertex, not a side: merges any zero-length surface side in place.
    /// </summary>
    /// <remarks>
    /// When a record's exposed end slides all the way to its other end, the side that carried it
    /// has both vertices at one point. Left as a zero-length side it is a notch waiting to open:
    /// the next slide along a neighbouring record moves one of its two vertices and the stub
    /// stretches along the old interface line, absorbing the recession that should have pivoted
    /// the cell's face — which is how 62 kept a 14 px bare side on the 49–62 line while 49 was cut
    /// beneath it. This runs on the stored polygon BEFORE any target is read, because in one contact
    /// pass a record can be spent and its neighbour's newly exposed end slid; merging afterwards
    /// would find the stub already stretched.
    /// </remarks>
    private void MergeSpentSides(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int i = 0; i < len && len > 3; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            if (_s.SideTouch[off + i] >= 0) continue;                        // a record side is geometry
            float ex = _s.PolyX[off + j] - _s.PolyX[off + i], ey = _s.PolyY[off + j] - _s.PolyY[off + i];
            if (ex * ex + ey * ey > 0.05f * 0.05f) continue;
            _s.PolyBond[off + i] = _s.PolyBond[off + j]; _s.SideTouch[off + i] = _s.SideTouch[off + j];
            for (int q = j; q + 1 < len; q++)
            {
                _s.PolyX[off + q] = _s.PolyX[off + q + 1]; _s.PolyY[off + q] = _s.PolyY[off + q + 1];
                _s.PolyBond[off + q] = _s.PolyBond[off + q + 1]; _s.SideTouch[off + q] = _s.SideTouch[off + q + 1];
            }
            len--; i--;
            DentCollapsed++;
        }
        _s.PolyLen[c] = len;
    }

    /// <summary>
    /// Cell-local position of one end of side <paramref name="v"/> as its record dictates, when that
    /// end is exposed. False for surface sides and for interior ends.
    /// </summary>
    private bool SideEndTarget(int c, int v, bool start, out float x, out float y)
    {
        x = 0f; y = 0f;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        short r = _s.SideTouch[off + v];
        if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) return false;
        byte open = _s.TouchOpen[r];
        if (open == 0) return false;
        if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) return false;

        // Which record end is this side's start: the nearer along the bisector.
        float u0 = (_s.CellRx[c] + _s.PolyX[off + v] - mx) * ex + (_s.CellRy[c] + _s.PolyY[off + v] - my) * ey;
        bool startIsT0 = SimMath.Abs(u0 - _s.TouchT0[r]) <= SimMath.Abs(u0 - _s.TouchT1[r]);
        bool wantT0 = start ? startIsT0 : !startIsT0;
        if ((open & (wantT0 ? SimState.TouchOpen0 : SimState.TouchOpen1)) == 0) return false;

        float tt = wantT0 ? _s.TouchT0[r] : _s.TouchT1[r];
        x = mx + tt * ex - _s.CellRx[c];
        y = my + tt * ey - _s.CellRy[c];
        return true;
    }

    /// <summary>
    /// A record has ended: the two points it ended at are surface now, so every other record of
    /// its cells that ends at one of them becomes exposed there.
    /// </summary>
    /// <remarks>
    /// <para>This is how the surface moves inward. One rule covers a span slid to nothing, a
    /// neighbour that comminuted, and a body that split — all of them retire the record. Nothing
    /// else ever sets an open bit after build.</para>
    ///
    /// <para>Found through the surviving cell's polygon, not through the retired record's geometry:
    /// the record's bisector is built from both cells' seeds, and once the other cell is dead or in
    /// another body those seeds are in a different frame and the positions mean nothing. The sides
    /// adjacent to the retired side in the polygon are the records that share its vertices, and
    /// their own bisectors are valid because their other cell is still a live neighbour.</para>
    /// </remarks>
    private void ExposeRecordEnds(int rec)
    {
        for (int side = 0; side < 2; side++)
        {
            int c = side == 0 ? _s.TouchA[rec] : _s.TouchB[rec];
            if (c < 0 || c >= _s.CellCount || _s.Dead(c)) continue;
            int off = _s.PolyOff[c], len = _s.PolyLen[c];
            if (len < 3) continue;
            int body = _s.CellBody[c];

            for (int v = 0; v < len; v++)
            {
                if (_s.SideTouch[off + v] != rec) continue;
                int pv = v == 0 ? len - 1 : v - 1, nv = v + 1 == len ? 0 : v + 1;
                OpenEndAtVertex(c, body, pv, v);        // the side before shares vertex v
                OpenEndAtVertex(c, body, nv, nv);       // the side after shares vertex v+1
            }
        }
    }

    /// <summary>Marks record side <paramref name="o"/>'s end at polygon vertex <paramref name="vert"/> exposed.</summary>
    private void OpenEndAtVertex(int c, int body, int o, int vert)
    {
        int off = _s.PolyOff[c];
        short r = _s.SideTouch[off + o];
        if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) return;
        int other = _s.TouchOther(r, c);
        if (other < 0 || other >= _s.CellCount || _s.Dead(other) || _s.CellBody[other] != body) return;
        if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) return;

        float u = (_s.CellRx[c] + _s.PolyX[off + vert] - mx) * ex + (_s.CellRy[c] + _s.PolyY[off + vert] - my) * ey;
        bool isT0 = SimMath.Abs(u - _s.TouchT0[r]) <= SimMath.Abs(u - _s.TouchT1[r]);
        byte bit = isT0 ? SimState.TouchOpen0 : SimState.TouchOpen1;
        if ((_s.TouchOpen[r] & bit) == 0) { _s.TouchOpen[r] |= bit; DentExposed++; }
    }
}
