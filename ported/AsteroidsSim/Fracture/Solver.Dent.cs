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

    internal int DentCalls, DentVertices, DentReclips, DentSpent, DentFallback, DentSplits, DentBudgetRefused, DentExposed, DentCollapsed, DentCorners;
    internal System.Action<string>? DentProbe; internal int DentProbeLeft = 12;
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
        if (LiveRecordCount(c) < 1) { DentFallback++; return -1f; }   // lone cells keep v1 until the feel is confirmed
        EnsureDentScratch();
        int stamp = ++_dentStamp;
        DentCalls++;

        int bi = _s.CellBody[c];
        BodyTrig(bi, out float si, out float co);
        float dx = wpx - _s.BodyX[bi], dy = wpy - _s.BodyY[bi];
        float lx = dx * co + dy * si, ly = -dx * si + dy * co;          // contact, body-local
        float radius = SimMath.Max(1e-3f, _s.Mat(c).Dent * _s.CellRad[c]);

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

        for (int i = 0; i < _candCount; i++)
        {
            float slide = SimMath.Min(lambda * _candK[i], _s.CellRad[c]);     // never more than a cell in one step
            if (slide <= 0f) continue;
            if (_candRec[i] >= 0) ApplyEnd(_candRec[i], _candEnd[i], slide, stamp, ref nCells);
            else ApplyCorner(_candCell[i], _candVert[i], SimMath.Min(slide, _candMax[i]), _candUx[i], _candUy[i], stamp, ref nCells);
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
            if (toChord <= 0f) continue;

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

    private void GrowCandidates()
    {
        int n = System.Math.Max(64, _candRec.Length * 2);
        Array.Resize(ref _candRec, n); Array.Resize(ref _candEnd, n);
        Array.Resize(ref _candK, n); Array.Resize(ref _candDa, n);
        Array.Resize(ref _candCell, n); Array.Resize(ref _candVert, n);
        Array.Resize(ref _candUx, n); Array.Resize(ref _candUy, n); Array.Resize(ref _candMax, n);
    }

    /// <summary>Slides one record end inward, keeps the bond's length honest, and retires a spent record.</summary>
    private void ApplyEnd(int r, int end, float slide, int stamp, ref int nCells)
    {
        if (r < 0 || r >= _s.TouchCount || _s.TouchA[r] < 0) return;
        if (!RecordBisector(r, out float ex, out float ey, out float mx, out float my)) return;

        float t0 = _s.TouchT0[r], t1 = _s.TouchT1[r];
        if (end == 0) _s.TouchT0[r] = SimMath.Min(t1, t0 + slide);
        else          _s.TouchT1[r] = SimMath.Max(t0, t1 - slide);

        // The bond along this interface is as long as the interface: its bending lever in the
        // damage model shortens with the material that carries it.
        short bk = _s.TouchBond[r];
        if (bk >= 0 && bk < _s.BondCount) _s.BondLen[bk] = SimMath.Max(1e-3f, _s.TouchT1[r] - _s.TouchT0[r]);

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
                n--; dropped = true; v--;
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
