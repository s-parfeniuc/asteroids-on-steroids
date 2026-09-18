using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{
    // ══════════════════════════════════════════════════════════════════════════
    //  carving — the geometric half of comminution
    // ══════════════════════════════════════════════════════════════════════════
    //
    // A cell under enough contact pressure is clipped back on the loaded side rather than being
    // deleted whole. That is the entire mechanism, and it is the same operation the tessellator uses
    // to build cells in the first place: intersect with a half-plane.
    //
    // Three properties come free from choosing clipping over vertex displacement, and all three had
    // to be guarded explicitly in every alternative considered:
    //
    //   CONVEXITY   clipping a convex polygon by a half-plane is convex, unconditionally. The SAT
    //               narrow phase requires convex, CCW polygons and fails silently on either — a
    //               concave vertex reports separation for overlapping shapes, a winding flip
    //               inverts every normal. Nothing here can produce either.
    //   MONOTONE    area only ever decreases. Carving can never invent material, which is what
    //               keeps the shed-mass accounting honest.
    //   LOCAL       only the carved cell changes. Neighbours carved by different amounts leave a
    //               step in the surface, which is what differential erosion looks like, and a step
    //               is not a hole: no interior void can open, because nothing gains area.

    /// <summary>Cells whose vertex budget forced an approximation. Diagnostics only.</summary>
    public long CarveSimplifications;

    /// <summary>Clips applied. Diagnostics only.</summary>
    public long CarveClips;

    /// <summary>Carves abandoned because the budget could not be met without losing a record.</summary>
    public long CarveRefused;

    internal bool _diagNoGuard;
    public long CcLen, CcReach, CcDegen, CcOk, CcZeroArea, CcNoSurface;
    public float CcLastDepth, CcLastRemoved;
    private float[] _clipX = new float[32];
    private float[] _clipY = new float[32];
    private short[] _clipB = new short[32];
    private short[] _clipS = new short[32];
    private readonly short[] _carveRecs = new short[32];

    /// <summary>
    /// Clips cell <paramref name="c"/> by the half-plane keeping points with
    /// <c>(v - p)·n &lt;= 0</c>, in body-local coordinates, and refreshes everything derived from
    /// its shape. Returns the area removed, which is what the caller charges as shed mass.
    /// </summary>
    /// <remarks>
    /// <para>The normal points OUT of the material being kept, so the caller passes the contact
    /// normal as seen from this cell and an offset point at the carve depth.</para>
    ///
    /// <para>Returns 0 without touching anything when the plane does not reach the polygon. That
    /// early-out is bit-exact rather than approximate — a clip whose half-plane misses every vertex
    /// reproduces the input vertex for vertex — so skipping it cannot perturb the simulation.</para>
    /// </remarks>
    /// <summary>
    /// Carves until <paramref name="targetArea"/> has been removed from the side facing
    /// <c>(nx, ny)</c>. Returns the area actually removed.
    /// </summary>
    /// <remarks>
    /// <para><b>Area, not depth — and the difference is not cosmetic.</b> Positioning the plane a
    /// fixed depth below the cell's extreme vertex slices a wedge off a CORNER, and a corner's area
    /// goes as depth SQUARED, not depth times contact length. Measured, a 0.53 px cut removed
    /// 0.0000 area: the carve rate and the material actually removed were not related quantities at
    /// all, which is why no amount of moving the thresholds changed the behaviour.</para>
    ///
    /// <para>The plane offset is found by bisection instead. Area beyond a plane is monotone in the
    /// offset for a convex polygon, so eight halvings put it within 0.4% of the request — and the
    /// result is that asking for twice the area removes twice the material, which is what makes the
    /// knobs mean anything.</para>
    ///
    /// <para>The search is bounded below by the bonded-edge guard, so it can converge on removing
    /// less than asked but never on cutting into a neighbour's shared edge.</para>
    /// </remarks>
    internal float CarveCellByArea(int c, float nx, float ny, float targetArea)
    {
        if (targetArea <= 0f) return 0f;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 3) { CcLen++; return 0f; }

        // ── EROSION HAPPENS AT A SURFACE ─────────────────────────────────────
        // The clip removes the cap of the cell furthest along the load direction, so that direction
        // has to actually reach a surface. Under deep overlap it can stop doing so: once the
        // partner's centre passes this cell's midpoint, "toward the partner" points INTO this
        // cell's own body, the furthest cap sits against a bonded neighbour, and the cut carves a
        // void along an internal boundary. That is what produced the cuts at ticks 36 and 69 —
        // record 74's cut ran along cell 38's boundary with cell 24, a bonded same-body neighbour.
        //
        // Snapping rather than refusing: the pressure was real and the energy was spent, so the
        // erosion should happen — just somewhere it can. Every surface side's outward normal is a
        // direction that provably removes surface material, so take the one closest to what was
        // asked for. A slightly wrong direction moves a little; a fully inverted one turns around.
        if (Census != null) Census.Calls++;
        float inx = nx, iny = ny;
        if (!SteerToOpenSurface(c, ref nx, ref ny, out float turnCos))
        {
            CcNoSurface++; if (Census != null) Census.NoSurface++;
            if (c == TraceCell) TraceSink?.Invoke($"     clip REFUSED: no bare surface faces n=({inx:F3},{iny:F3})");
            return 0f;
        }
        if (c == TraceCell)
            TraceSink?.Invoke($"     clip dir: asked ({inx:F3},{iny:F3}) -> using ({nx:F3},{ny:F3}) "
                            + $"{(turnCos >= 1f ? "KEPT" : $"STEERED cos={turnCos:F3}")}; ray hit side {TrHit} "
                            + $"({TrHitKind}, t={TrHitT:F3}, covered [{TrHitF0:F3},{TrHitF1:F3}])"
                            + (turnCos < 1f ? $"; steered toward side {TrPickSide} t={TrPickT:F3}" : ""));
        if (Census != null)
        {
            if (turnCos >= 1f) Census.Unturned++; else Bin(Census.Turn, turnCos);
            Census.LastTurned = turnCos < 1f;
        }

        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (int v = 0; v < len; v++)
        {
            float pr = _s.PolyX[off + v] * nx + _s.PolyY[off + v] * ny;
            if (pr < lo) lo = pr;
            if (pr > hi) hi = pr;
        }

        if (lo >= hi) { CcReach++; return 0f; }
        float peak = hi;                     // the bisection below consumes hi as its bracket

        // Bisect: area beyond the plane falls monotonically as the offset rises.
        for (int it = 0; it < 8; it++)
        {
            float mid = 0.5f * (lo + hi);
            if (AreaBeyond(off, len, nx, ny, mid) < targetArea) hi = mid; else lo = mid;
        }
        float d = 0.5f * (lo + hi);

        // Pull the plane toward passing through the points where neighbours' erosion already cut the
        // sides we share with them, so the two surfaces meet instead of stepping.
        if (_tune.CarveContinuity > 0f && ContinuityOffset(c, nx, ny, out float want))
        {
            d += (want - d) * SimMath.Min(1f, _tune.CarveContinuity);
            d = SimMath.Max(lo, SimMath.Min(hi, d));
        }

        // ── EROSION STOPS AT A LIVE INTERFACE ────────────────────────────────
        // Steering guarantees the cap STARTS in open material; it does not bound where the cap
        // ends. A plane lying just behind a covered side removes that side whole, and the material
        // it takes is material the neighbour is still occupying — the cut has reached past the
        // interface into a cell that is physically in the way. So the cap is held in front of every
        // covered side: a cut may shorten one, never swallow it.
        //
        // This does not restore the old "never cut a shared edge past half its length" guard, which
        // forbade the outcome. Erosion through a neck still happens, by the cut being oblique to the
        // shared side and shortening it a little at a time until its span runs out — which is the
        // adjacency ending because the material joining the cells is gone, rather than because one
        // clip reached around behind it.
        float shield = float.NegativeInfinity;
        for (int v = 0; v < len; v++)
        {
            if (!_sideOk[v] || _sideOpen[v]) continue;
            int w = v + 1 == len ? 0 : v + 1;
            float q0 = _s.PolyX[off + v] * nx + _s.PolyY[off + v] * ny;
            float q1 = _s.PolyX[off + w] * nx + _s.PolyY[off + w] * ny;
            float near = SimMath.Min(q0, q1);
            if (near > shield) shield = near;
        }
        float dBefore = d;
        if (CarveShield && shield > d) d = shield;
        bool clamped = d != dBefore;
        if (Census != null && clamped) { Census.ShieldClamped++; Census.ClampedWant += targetArea; }
        if (c == TraceCell)
            TraceSink?.Invoke($"     clip plane: target {targetArea:F3} peak {peak:F3} d {dBefore:F3}"
                            + $"{(d != dBefore ? $" -> shield {d:F3}" : "")} depth {peak - d:F3}");
        if (d >= peak) { CcShielded++; if (Census != null) Census.ShieldRefused++; if (c == TraceCell) TraceSink?.Invoke("     clip SHIELDED out"); return 0f; }
        if (c == TraceCell)
        {
            var sb = new System.Text.StringBuilder("     clip sides vs plane (proj-d, +=removed):");
            for (int v = 0; v < len; v++)
            {
                int w = v + 1 == len ? 0 : v + 1;
                float q0 = _s.PolyX[off + v] * nx + _s.PolyY[off + v] * ny - d;
                float q1 = _s.PolyX[off + w] * nx + _s.PolyY[off + w] * ny - d;
                float dx = _s.PolyX[off + w] - _s.PolyX[off + v], dy = _s.PolyY[off + w] - _s.PolyY[off + v];
                float dl = SimMath.Hypot(dx, dy);
                float par = dl > 1e-6f ? SimMath.Abs((dy * nx - dx * ny) / dl) : 0f;   // |n·outward|
                sb.Append($" [{v}: {q0:+0.000;-0.000}/{q1:+0.000;-0.000} |n·out|={par:F2}]");
            }
            TraceSink?.Invoke(sb.ToString());
        }

        CcLastDepth = peak - d;
        float a0 = _s.CellArea[c];
        float got = CarveCell(c, nx * d, ny * d, nx, ny);
        if (Census != null)
        {
            Census.TotalRemoved += got;
            if (clamped) Census.ClampedArea += got;
            Bin(Census.AreaFrac, a0 > 0f ? got / a0 * 10f : 0f);   // 0-1%, 1-2%, ... 10%+
            Bin(Census.DepthPx, (peak - d) * 2f);   // 0-0.05px .. 0.5px+                    // 0-0.005px .. 0.05px+
        }
        return got;
    }

    /// <summary>
    /// Turns the load direction by the smallest angle that makes it erode open surface, and leaves
    /// it alone when it already does. False when the cell has no open side at all.
    /// </summary>
    /// <remarks>
    /// <para>A clip removes the cap of the cell furthest along the direction, so the question is what
    /// that cap is made of. The furthest point is the support vertex, and it is open material exactly
    /// when one of the two sides meeting there is open. Under deep overlap the contact normal stops
    /// satisfying that — once the partner's centre passes this cell's midpoint, "toward the partner"
    /// points into this cell's own body and the cap sits against a bonded neighbour.</para>
    ///
    /// <para>This used to be answered by replacing the direction with an open side's outward normal.
    /// That was wrong twice over. It made every cut parallel to a side of the cell, so erosion could
    /// only ever produce facets aligned with the existing Voronoi edges — the cuts looked machined.
    /// And a cut parallel to a side cannot shorten that side, it deletes it and lays down a copy a
    /// fraction of a pixel behind; when the side it happened to be parallel to was a bonded one, the
    /// adjacency was destroyed outright. Measured over a 170-grain collide, that accounted for 82%
    /// of every adjacency carving broke.</para>
    ///
    /// <para>So the direction is steered, not replaced. Which vertex is the support point changes
    /// only at the side normals, so the directions supported by a given vertex form the cone between
    /// the outward normals of its two sides. The valid set is the union of those cones over vertices
    /// that touch open material; a direction already inside it is kept <b>exactly</b>, and one
    /// outside is rotated to the nearest cone. Most cuts are already valid and keep their true
    /// contact normal, which is what stops the surface from faceting.</para>
    /// </remarks>
    /// <summary>
    /// Turns the load direction by the smallest angle that makes it erode exposed surface, and
    /// leaves it alone when it already does. False when nothing exposed faces the load.
    /// </summary>
    /// <remarks>
    /// <para>The question a clip has to answer is what the cap it removes is MADE OF, so the test
    /// follows the direction and asks which surface it reaches. The cell's vertices are stored
    /// relative to its centroid, so a vertex's position is also its direction from the centroid, and
    /// the directions leaving through side <c>i</c> are exactly the cone between <c>dir(v_i)</c> and
    /// <c>dir(v_i+1)</c>. Those cones tile the circle, so the side a direction reaches is unique and
    /// costs one scan to find.</para>
    ///
    /// <para>Reaching a side is not enough — it has to reach BARE material. A side may be partly
    /// eroded through, bare at one end and still covered by its neighbour at the other, and such a
    /// side is a perfectly good place to erode <i>where it is bare</i>. So the ray's exit point is
    /// located along the side and tested against the covered interval <c>ClassifySide</c> reports.
    /// Erosion is admitted through the bare stretch and refused through the covered one.</para>
    ///
    /// <para>The rule this replaces asked whether the furthest VERTEX along the direction happened
    /// to touch an open side. That is incidence, not direction: when the plane runs parallel to a
    /// shared side, both ends of that side are jointly furthest, and each is also an endpoint of
    /// some unrelated open side, so the test passed while the cap removed was a wedge shaved along
    /// a bonded interface. It also threw away the covered interval entirely, so a side 90% bare
    /// counted as closed.</para>
    ///
    /// <para>Nothing here needs an epsilon. A direction landing exactly on a cone boundary leaves
    /// through a vertex of that side, which is still its material, so the valid set is closed and
    /// its nearest point is attainable — which is why the old <c>ConeStepIn</c> nudge is gone.</para>
    /// </remarks>
    private int TrHit; private float TrHitT, TrHitF0, TrHitF1; private SideKind TrHitKind;
    private int TrPickSide = -1; private float TrPickT;

    private bool SteerToOpenSurface(int c, ref float nx, ref float ny, out float turnCos)
    {
        TrHit = -1; TrHitT = 0f; TrHitF0 = 0f; TrHitF1 = 1f; TrHitKind = SideKind.Interior;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        turnCos = 1f;
        if (len < 3) return false;
        EnsureSideScratch(len);

        bool anyBare = false;
        for (int v = 0; v < len; v++)
        {
            int w = v + 1 == len ? 0 : v + 1;
            float dx = _s.PolyX[off + w] - _s.PolyX[off + v];
            float dy = _s.PolyY[off + w] - _s.PolyY[off + v];
            _sideOk[v] = SimMath.Hypot(dx, dy) >= 1e-6f;
            if (!_sideOk[v]) { _sideOpen[v] = false; _sideF0[v] = 0f; _sideF1[v] = 1f; continue; }

            // f0..f1 is the COVERED stretch, as a fraction from vertex v to v+1. A fully open side
            // reports no cover at all; a fully buried one reports all of it.
            _sideOpen[v] = ClassifySide(c, v, out float f0, out float f1) == SideKind.RealSurface;
            if (_sideOpen[v]) { _sideF0[v] = 1f; _sideF1[v] = 1f; }   // empty covered interval
            else { _sideF0[v] = f0; _sideF1[v] = f1; }
            anyBare |= _sideOpen[v] || _sideF0[v] > 0f || _sideF1[v] < 1f;
        }
        if (!anyBare) return false;

        // ── does the direction already reach bare material? ───────────────────
        int hit = -1; float hitT = 0f;
        for (int v = 0; v < len && hit < 0; v++)
        {
            if (!_sideOk[v]) continue;
            int w = v + 1 == len ? 0 : v + 1;
            float ax = _s.PolyX[off + v], ay = _s.PolyY[off + v];
            float bx = _s.PolyX[off + w], by = _s.PolyY[off + w];
            if (ax * ny - ay * nx > 0f) continue;            // n is not CCW-after dir(v)
            if (nx * by - ny * bx > 0f) continue;            // n is not CCW-before dir(v+1)
            float den = (bx - ax) * ny - (by - ay) * nx;
            if (SimMath.Abs(den) < 1e-9f) continue;
            hit = v; hitT = SimMath.Max(0f, SimMath.Min(1f, -(ax * ny - ay * nx) / den));
        }
        if (hit >= 0)
        {
            TrHit = hit; TrHitT = hitT; TrHitF0 = _sideF0[hit]; TrHitF1 = _sideF1[hit];
            TrHitKind = _sideOpen[hit] ? SideKind.RealSurface : ClassifySide(c, hit, out _, out _);
        }
        if (hit >= 0 && (_sideOpen[hit] || hitT < _sideF0[hit] || hitT > _sideF1[hit])) return true;

        // ── otherwise rotate to the nearest bare stretch ──────────────────────
        // Every bare stretch is an interval of the side, and its two ends are directions. n is in
        // none of them, so the least rotation lands on whichever of those ends it is closest to.
        float bestDot = float.NegativeInfinity, rx = 0f, ry = 0f;
        for (int v = 0; v < len; v++)
        {
            if (!_sideOk[v]) continue;
            int w = v + 1 == len ? 0 : v + 1;
            float ax = _s.PolyX[off + v], ay = _s.PolyY[off + v];
            float bx = _s.PolyX[off + w], by = _s.PolyY[off + w];

            // Bare stretches: all of it when open, otherwise what lies outside the covered interval.
            for (int part = 0; part < 2; part++)
            {
                float ta, tb;
                if (_sideOpen[v]) { if (part == 1) break; ta = 0f; tb = 1f; }
                else if (part == 0) { ta = 0f; tb = _sideF0[v]; }
                else { ta = _sideF1[v]; tb = 1f; }
                if (tb <= ta) continue;

                for (int end = 0; end < 2; end++)
                {
                    float tt = end == 0 ? ta : tb;
                    float px = ax + (bx - ax) * tt, py = ay + (by - ay) * tt;
                    float pl = SimMath.Hypot(px, py);
                    if (pl < 1e-6f) continue;
                    float ux = px / pl, uy = py / pl;
                    float dot = nx * ux + ny * uy;
                    if (dot > bestDot) { bestDot = dot; rx = ux; ry = uy; TrPickSide = v; TrPickT = tt; }
                }
            }
        }

        if (bestDot <= 0f) return false;        // nothing bare faces the load at all
        if (Census != null)
        {
            // The picked endpoint is a polygon vertex when t is 0 or 1; the OTHER side at that vertex
            // tells whether the plane can run along covered material.
            int ps = TrPickSide;
            int adj = TrPickT <= 0f ? (ps == 0 ? len - 1 : ps - 1) : TrPickT >= 1f ? (ps + 1 == len ? 0 : ps + 1) : -1;
            if (adj >= 0 && !_sideOpen[adj]) Census.SteerToCoveredCorner++; else Census.SteerToOpenCorner++;
        }
        turnCos = bestDot;
        nx = rx; ny = ry;
        CcSteered++;
        return true;
    }

    private bool[] _sideOpen = Array.Empty<bool>(), _sideOk = Array.Empty<bool>();
    private float[] _sideF0 = Array.Empty<float>(), _sideF1 = Array.Empty<float>();

    private void EnsureSideScratch(int n)
    {
        if (_sideOpen.Length >= n) return;
        _sideOpen = new bool[n * 2]; _sideOk = new bool[n * 2];
        _sideF0 = new float[n * 2]; _sideF1 = new float[n * 2];
    }

    private bool ContinuityOffset(int c, float nx, float ny, out float offset)
    {
        offset = 0f;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        float sum = 0f; int n = 0;

        for (int v = 0; v < len; v++)
        {
            short rec = _s.SideTouch[off + v];
            if (rec < 0 || rec >= _s.TouchCount || _s.TouchA[rec] < 0) continue;
            if (!RecordBisector(rec, out float ex, out float ey, out float mx, out float my)) continue;

            int w = v + 1 == len ? 0 : v + 1;
            float u0 = (_s.CellRx[c] + _s.PolyX[off + v] - mx) * ex
                     + (_s.CellRy[c] + _s.PolyY[off + v] - my) * ey;
            float u1 = (_s.CellRx[c] + _s.PolyX[off + w] - mx) * ex
                     + (_s.CellRy[c] + _s.PolyY[off + w] - my) * ey;
            float lo = SimMath.Min(u0, u1), hi = SimMath.Max(u0, u1);
            float eps = SimMath.Max(0.05f, _s.CellRad[c] * 5e-3f);

            for (int e = 0; e < 2; e++)
            {
                float tt = e == 0 ? _s.TouchT0[rec] : _s.TouchT1[rec];
                if (tt <= lo + eps || tt >= hi - eps) continue;      // not cut inside this side

                // The cut point, in this cell's local frame, projected onto the carve direction.
                float px = mx + tt * ex - _s.CellRx[c];
                float py = my + tt * ey - _s.CellRy[c];
                sum += px * nx + py * ny;
                n++;
            }
        }

        if (n == 0) return false;
        offset = sum / n;
        return true;
    }

    /// <summary>Area of the part of a convex polygon lying beyond the plane at offset d along n.</summary>
    private float AreaBeyond(int off, int len, float nx, float ny, float d)
    {
        EnsureClipScratch(len + 2);
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            float cx = _s.PolyX[off + i], cy = _s.PolyY[off + i];
            float ax = _s.PolyX[off + j], ay = _s.PolyY[off + j];
            float dc = cx * nx + cy * ny - d;
            float dn = ax * nx + ay * ny - d;
            if (dc >= 0f) { _clipX[n] = cx; _clipY[n] = cy; n++; }
            if ((dc >= 0f) != (dn >= 0f))
            {
                float tt = dc / (dc - dn);
                _clipX[n] = cx + tt * (ax - cx);
                _clipY[n] = cy + tt * (ay - cy);
                n++;
            }
        }
        if (n < 3) return 0f;
        float a2 = 0f;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            a2 += _clipX[i] * _clipY[j] - _clipX[j] * _clipY[i];
        }
        return 0.5f * SimMath.Abs(a2);
    }

    internal float CarveCell(int c, float px, float py, float nx, float ny)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 3) return 0f;

        // ── THE PLANE MAY NOT REACH A BONDED EDGE ────────────────────────────
        // Carving a shared edge shortens THIS cell's copy of it while the neighbour keeps its own,
        // which opens a notch between two cells that are still bonded together. A contact should
        // never form on a buried edge in the first place, but relying on that is relying on the
        // overlap staying below one cell layer — which it currently does not. So the clamp makes it
        // impossible rather than unlikely, and in doing so removes a rule the design used to need:
        // a carve can no longer sever a bond by eating its edge, so bonds are severed only by the
        // damage model or by comminution.
        float d = px * nx + py * ny;                         // plane offset along n, cell-local
        float guard = _diagNoGuard ? float.NegativeInfinity : BondedGuard(c, nx, ny);
        if (guard > d)
        {
            d = guard; px = nx * d; py = ny * d;
            if (Census != null) Census.GuardClamped++;
            if (c == TraceCell) TraceSink?.Invoke($"     clip: BondedGuard moved the plane to {d:F3}");
        }

        // Reach test: if every vertex is strictly kept, there is nothing to do. Bit-exact, because a
        // clip whose half-plane misses every vertex reproduces its input vertex for vertex.
        float worst = float.NegativeInfinity;
        for (int v = 0; v < len; v++)
        {
            float dv = (_s.PolyX[off + v] - px) * nx + (_s.PolyY[off + v] - py) * ny;
            if (dv > worst) worst = dv;
        }
        if (worst <= 0f) { CcReach++; return 0f; }

        // Records this cell links BEFORE the clip. A clip can remove a side outright rather than
        // shortening it, in which case the interval never collapses to nothing and the record would
        // survive with only the neighbour still pointing at it. Comparing before against after is
        // the only way to see a side that simply stopped existing.
        int nBefore = 0;
        for (int v = 0; v < len && nBefore < _carveRecs.Length; v++)
        {
            short rr = _s.SideTouch[off + v];
            if (rr < 0) continue;
            bool dup = false;
            for (int q = 0; q < nBefore; q++) if (_carveRecs[q] == rr) { dup = true; break; }
            if (!dup) _carveRecs[nBefore++] = rr;
        }

        float area0 = PolyArea(off, len);

        // ── clip into scratch ────────────────────────────────────────────────
        // Sutton-Hodgman against one plane: keep vertices on the inside, and emit a crossing vertex
        // wherever an edge changes side. Cutting off exactly one corner drops 1 vertex and adds 2,
        // which is the only way this grows and the reason the budget below exists.
        EnsureClipScratch(len + 2);
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            float cx = _s.PolyX[off + i], cy = _s.PolyY[off + i];
            float ax = _s.PolyX[off + j], ay = _s.PolyY[off + j];
            float dc = (cx - px) * nx + (cy - py) * ny;
            float dn = (ax - px) * nx + (ay - py) * ny;

            // Carry the edge labels through: a kept vertex starts the same side it always did; a
            // crossing starts the NEW face the carve just cut, which is surface by definition.
            // Without this the labels index vertices that no longer exist.
            if (dc <= 0f)
            {
                _clipX[n] = cx; _clipY[n] = cy;
                _clipB[n] = _s.PolyBond[off + i]; _clipS[n] = _s.SideTouch[off + i];
                n++;
            }
            if ((dc <= 0f) != (dn <= 0f))
            {
                float t = dc / (dc - dn);
                _clipX[n] = cx + t * (ax - cx);
                _clipY[n] = cy + t * (ay - cy);
                _clipB[n] = dc <= 0f ? SimState.SideReal : _s.PolyBond[off + i];
                _clipS[n] = dc <= 0f ? (short)-1 : _s.SideTouch[off + i];
                n++;
            }
        }

        // Clipped away entirely, or down to a degenerate sliver. The caller's shed-limit test will
        // remove the cell; report the whole area as gone and leave the polygon alone so nothing
        // downstream ever sees a sub-triangle.
        // Clipped to nothing. Zero the area so the shed-limit test fires this tick rather than
        // leaving a cell with a stale area and a polygon nothing downstream can use.
        if (n < 3) { CcDegen++; _s.CellArea[c] = 0f; return area0; }

        // ── DROP SIDES THE CLIP LEFT WITH NO LENGTH ──────────────────────────
        // A crossing vertex landing within rounding of a kept one leaves a side of zero length. It
        // is not harmless: it carries a label like any other side, so a carve face reduced to a
        // point still reports itself REAL — surface with no extent, attached to nothing. It also
        // eats the vertex budget and can push a real side out through simplification.
        //
        // Only sides carrying no adjacency are collapsed, so this can never discard a touch record.
        for (int i = 0; i < n && n > 3; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            float ex = _clipX[j] - _clipX[i], ey = _clipY[j] - _clipY[i];
            if (_clipS[i] >= 0 || SimMath.Hypot(ex, ey) > 0.05f) continue;

            _clipB[i] = _clipB[j]; _clipS[i] = _clipS[j];       // side i inherits what side j was
            for (int q = j; q + 1 < n; q++)
            {
                _clipX[q] = _clipX[q + 1]; _clipY[q] = _clipY[q + 1];
                _clipB[q] = _clipB[q + 1]; _clipS[q] = _clipS[q + 1];
            }
            n--;
            i--;                                                // the new side i may be degenerate too
            CcCollapsed++;
            if (c == TraceCell) TraceSink?.Invoke("     clip: COLLAPSED a zero-length side");
        }

        // ── fit the budget ───────────────────────────────────────────────────
        while (n > _s.PolyCap[c])
        {
            if (!DropLeastSignificantVertex(ref n)) { CarveRefused++; return 0f; }
            CarveSimplifications++;
            if (c == TraceCell) TraceSink?.Invoke("     clip: DROPPED a vertex to fit the budget");
        }

        Array.Copy(_clipX, 0, _s.PolyX, off, n);
        Array.Copy(_clipY, 0, _s.PolyY, off, n);
        Array.Copy(_clipB, 0, _s.PolyBond, off, n);
        Array.Copy(_clipS, 0, _s.SideTouch, off, n);
        _s.PolyLen[c] = n;

        int relinkBefore = CcRelinked;
        RelinkCoincidentSides(c, nBefore, worst);
        CarveClips++;
        if (c == TraceCell)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"     clip result: {len} -> {n} verts, relinked {CcRelinked - relinkBefore}, sides:");
            for (int v = 0; v < n; v++)
            {
                int w = v + 1 == n ? 0 : v + 1;
                short pb = _s.PolyBond[off + v], st = _s.SideTouch[off + v];
                string lab = pb == SimState.SideReal ? "REAL" : pb == SimState.SideCrack ? "CRACK"
                           : pb == SimState.SideSealed ? "SEALED" : $"b{pb}";
                sb.Append($" [{v}:{lab}/r{st} {SimMath.Hypot(_s.PolyX[off + w] - _s.PolyX[off + v], _s.PolyY[off + w] - _s.PolyY[off + v]):F2}]");
            }
            TraceSink?.Invoke(sb.ToString());
        }

        RefreshCellShape(c);

        // Any adjacency whose side the clip removed outright has been eroded through.
        //
        // This is only sound because the cut direction is now the outward normal of a fully OPEN
        // side: a cut can no longer run parallel to a bonded interface, so a side that disappears
        // did so by being consumed, not by being replaced with a copy of itself a hair behind.
        for (int q = 0; q < nBefore; q++)
        {
            short rr = _carveRecs[q];
            if (rr < 0 || rr >= _s.TouchCount || _s.TouchA[rr] < 0) continue;
            bool still = false;
            for (int v = 0; v < _s.PolyLen[c] && !still; v++) still = _s.SideTouch[off + v] == rr;
            if (still) continue;
            CcVanishTotal++;
            if (Census != null)
            {
                Census.Vanished++;
                if (Census.LastTurned) Census.VanishAfterTurn++;
                if (RecordBisector(rr, out float bex, out float bey, out _, out _))
                    Bin(Census.VanishCos, SimMath.Abs(nx * -bey + ny * bex));
                Bin(Census.VanishSpan, (_s.TouchT1[rr] - _s.TouchT0[rr]) * 0.2f);
            }
            RetireTouch(rr, $"side-vanished(c={c})");
        }

        float rem = SimMath.Max(0f, area0 - _s.CellArea[c]);
        CcLastRemoved = rem;
        if (rem <= 0f) CcZeroArea++; else CcOk++;
        return rem;
    }

    /// <summary>
    /// Removes the vertex whose absence changes the polygon least, to fit the storage budget.
    /// </summary>
    /// <remarks>
    /// <para>The cost of dropping vertex <c>v</c> is the area of the triangle
    /// <c>(prev, v, next)</c> — the sliver the chord across it would cut off. Taking the minimum is
    /// Visvalingam-Whyatt simplification, and it is the right rule here for three reasons beyond
    /// minimising error: the chord of a convex polygon stays inside it, so <b>convexity survives</b>;
    /// the chord removes area rather than adding it, so simplification stays <b>monotone</b> like
    /// the carve itself and can never invent material; and a small triangle means either a nearly
    /// flat corner or two nearly coincident vertices, both of which are exactly what should go.</para>
    ///
    /// <para>Known artifact: the least significant vertex may sit on an UNCARVED edge shared with a
    /// bonded neighbour, so this can recede a boundary that had no physical reason to move, opening
    /// a gap bounded by that triangle. <see cref="CarveSimplifications"/> exists to say how often it
    /// actually happens — the expectation is almost never, because re-carving in the same direction
    /// is vertex-neutral and only a new direction can grow a cell.</para>
    /// </remarks>
    private bool DropLeastSignificantVertex(ref int n)
    {
        int best = -1;
        float bestCost = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int p = i == 0 ? n - 1 : i - 1;
            int q = i + 1 == n ? 0 : i + 1;

            // ── ONLY MERGE SIDES THAT MEAN THE SAME THING ────────────────────
            // Dropping vertex i fuses the sides p->i and i->q into one, and one label has to go.
            // If those sides carry different records, the survivor silently swallows the other —
            // and that loss does not stop at the picture. The record is then missing from this
            // cell, so it is retired as "the adjacency ended", which BREAKS THE BOND and separates
            // material inside a body that still encloses it. A hole, from a rounding decision about
            // a vertex. It is intermittent because it needs the vertex budget to be exceeded first,
            // which depends on how many distinct directions a cell has been carved from.
            //
            // So only a vertex whose two sides agree may be dropped. If none does, the polygon is
            // left over budget and the caller abandons the carve — losing a fraction of a pixel of
            // erosion is nothing beside losing a bond.
            if (_clipS[p] != _clipS[i]) continue;

            float ux = _clipX[i] - _clipX[p], uy = _clipY[i] - _clipY[p];
            float vx = _clipX[q] - _clipX[i], vy = _clipY[q] - _clipY[i];
            float cost = SimMath.Abs(ux * vy - uy * vx);   // twice the triangle area
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        if (best < 0) return false;

        for (int i = best; i + 1 < n; i++)
        {
            _clipX[i] = _clipX[i + 1]; _clipY[i] = _clipY[i + 1];
            _clipB[i] = _clipB[i + 1]; _clipS[i] = _clipS[i + 1];
        }
        n--;
        return true;
    }

    /// <summary>
    /// Re-derives everything that follows from a cell's shape, and re-centres the polygon on its own
    /// centroid.
    /// </summary>
    /// <remarks>
    /// <para><b>The re-centring is not optional.</b> <c>CellR</c> is the polygon centroid by
    /// contract: it is the lever-arm origin in <c>SolveContact</c> and the mass point in
    /// <c>RecomputeBody</c>. Clipping asymmetrically moves the centroid, so the vertices are shifted
    /// back onto it and the shift is pushed into <c>CellR</c>. Every vertex's body-frame position
    /// <c>CellR + Poly[v]</c> is unchanged by that pair, so it is a pure re-parameterisation and no
    /// geometry moves.</para>
    ///
    /// <para><b>CellRad is the dangerous one.</b> It gates the broadphase, the manifold radius
    /// reject and the deep-overlap normal guard; a stale value lets a cell escape its own bounding
    /// circle and pairs are dropped with no error anywhere. <c>CellPerim</c> feeds the contact
    /// compliance length. Mass is NOT recomputed here — carving sheds it explicitly, through the
    /// caller, so that momentum leaves with it.</para>
    /// </remarks>
    private void RefreshCellShape(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];

        float a2 = 0f, cx = 0f, cy = 0f;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            float x0 = _s.PolyX[off + i], y0 = _s.PolyY[off + i];
            float x1 = _s.PolyX[off + j], y1 = _s.PolyY[off + j];
            float cr = x0 * y1 - x1 * y0;
            a2 += cr;
            cx += (x0 + x1) * cr;
            cy += (y0 + y1) * cr;
        }
        float area = 0.5f * a2;
        if (area <= 1e-6f) { _s.CellArea[c] = SimMath.Max(0f, area); return; }

        cx /= 3f * a2;
        cy /= 3f * a2;

        // Shift onto the new centroid first; perimeter and radius are then taken from the settled
        // vertices rather than from a mix of shifted and unshifted ones.
        float rad2 = 0f;
        for (int i = 0; i < len; i++)
        {
            float x = _s.PolyX[off + i] - cx, y = _s.PolyY[off + i] - cy;
            _s.PolyX[off + i] = x; _s.PolyY[off + i] = y;
            float r2 = x * x + y * y;
            if (r2 > rad2) rad2 = r2;
        }

        float perim = 0f;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            perim += SimMath.Hypot(_s.PolyX[off + j] - _s.PolyX[off + i],
                                   _s.PolyY[off + j] - _s.PolyY[off + i]);
        }

        _s.CellSeedX[c] -= cx;
        _s.CellSeedY[c] -= cy;

        // A BOND-LESS cell must keep a zero rest offset. RecomputeBody normalises it for exactly one
        // reason, stated there: "a nonzero offset on a bond-less cell turns the centrifugal load into
        // a perpetual self-accelerator, because nothing opposes it." Carving re-centres the polygon,
        // so folding that shift into CellR the usual way re-breaks the invariant on every carve —
        // and the cell spins up for the rest of the tick, every tick. Push it into the POSE instead:
        // the cell stays where it is in the world, and the offset stays zero.
        int lb = _s.CellBody[c];
        if (lb >= 0 && lb < _s.BodyCount && _s.BodyCellLen[lb] == 1)
        {
            BodyTrig(lb, out float lsi, out float lco);
            _s.BodyX[lb] += cx * lco - cy * lsi;
            _s.BodyY[lb] += cx * lsi + cy * lco;
        }
        else
        {
            _s.CellRx[c] += cx;
            _s.CellRy[c] += cy;
        }
        _s.CellArea[c] = area;
        _s.CellPerim[c] = perim;
        _s.CellRad[c] = SimMath.Sqrt(rad2);

        // Inertia follows the shape at FIXED mass: the mass a cell still has is distributed over
        // the area it still has. Recomputing it as rho * I_poly instead would silently re-derive the
        // mass too, and mass only ever changes through the shed path.
        // FLOOR RELATIVE TO THE CELL, not an absolute epsilon. The old 1e-6 sat nine orders of
        // magnitude below a typical cell inertia of ~2.7e4, so a cell carved thin got an enormous
        // CellIic — which appears BOTH in sizing the contact impulse (through w) and in applying it
        // (in ApplyPair), so any impulse spun it violently. A uniform disc is 0.5*m*r^2 and a Voronoi
        // cell around 0.3, so 0.05 is well clear of anything legitimate.
        float ipoly = PolyInertia(off, len);
        float icFloor = 0.05f * _s.CellM[c] * _s.CellRad[c] * _s.CellRad[c];
        float ic = SimMath.Max(SimMath.Max(1e-6f, icFloor), _s.CellM[c] * ipoly / area);
        _s.CellIc[c] = ic;
        _s.CellIic[c] = 1f / ic;

        NarrowTouchRecords(c);

        ReanchorCarvedCell(c);
    }

    /// <summary>
    /// A bond has stopped holding: the side it occupied on each of its two cells becomes surface.
    /// </summary>
    /// <remarks>
    /// This is the ONLY way an interior side becomes exposed, and it is why the labelling is
    /// maintained rather than re-derived. It must be called wherever a bond stops carrying load —
    /// cohesive failure, comminution severing, anywhere — or a newly opened crack face stays
    /// invisible to both the crack rule and to carving.
    /// </remarks>
    internal void OpenBondSides(int k)
    {
        ClearSide(_s.BondA[k], k);
        ClearSide(_s.BondB[k], k);
    }

    /// <summary>
    /// The material behind this side has gone — comminuted, or carried off into another body — so
    /// the side is now genuinely the outside.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="OpenBondSides"/>, which marks crack surface: a crack is a face
    /// inside intact material and a crack arriving at one from both ends would ring material off,
    /// which the separation rule refuses. Once the other side is EMPTY none of that applies — this
    /// is the body's boundary, fresh cracks may start from it, and it should read as boundary.
    /// Without this promotion a fragment keeps drawing its whole new outline as crack surface and
    /// can never crack again from the face it was cut along.
    /// </remarks>
    internal void ExposeSide(int c, int k)
    {
        if (c < 0 || c >= _s.CellCount) return;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++)
            if (_s.PolyBond[off + v] == k) _s.PolyBond[off + v] = SimState.SideReal;
    }

    /// <summary>Promotes every side of a cell whose neighbour is now dead or in another body.</summary>
    internal void ExposeSeparatedSides(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        int body = _s.CellBody[c];
        for (int v = 0; v < len; v++)
        {
            short k = _s.PolyBond[off + v];
            if (k < 0) continue;
            int o = _s.BondA[k] == c ? _s.BondB[k] : _s.BondA[k];
            if (o < 0 || o >= _s.CellCount) continue;
            if (_s.Dead(o) || _s.CellBody[o] != body) _s.PolyBond[off + v] = SimState.SideReal;
        }
    }

    private void ClearSide(int c, int k)
    {
        if (c < 0 || c >= _s.CellCount) return;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++)
            if (_s.PolyBond[off + v] == k) _s.PolyBond[off + v] = SimState.SideCrack;
    }

    /// <summary>Edge index in cell <paramref name="c"/> carrying bond <paramref name="k"/>, or −1.</summary>
    internal int EdgeOfBond(int c, int k)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++) if (_s.PolyBond[off + v] == k) return v;
        return -1;
    }

    /// <summary>
    /// True when the side carrying bond <paramref name="k"/> in cell <paramref name="c"/> touches a
    /// side that is already surface — the condition for a crack to advance along it.
    /// </summary>
    /// <remarks>
    /// A crack tip is a VERTEX, and it advances to the side sharing that vertex. Testing whether the
    /// CELL is "at a surface" is far too weak: it licenses any bond of that cell, including ones
    /// buried on the far side, so cracks tunnel and interior cells fall out of bodies that still
    /// enclose them. Edges are cyclic, so the sides sharing a vertex with edge e are e-1 and e+1.
    /// </remarks>
    internal bool SideTouchesSurface(int c, int k)
    {
        int e = EdgeOfBond(c, k);
        if (e < 0) return true;                 // already open on this cell
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 3) return true;
        int prev = e == 0 ? len - 1 : e - 1;
        int next = e + 1 == len ? 0 : e + 1;

        short bp = _s.PolyBond[off + prev], bn = _s.PolyBond[off + next];
        bool openPrev = bp == SimState.SideReal || bp == SimState.SideCrack;
        bool openNext = bn == SimState.SideReal || bn == SimState.SideCrack;

        // A crack advances from an end that is already open.
        if (!openPrev && !openNext) return false;

        // ── AND IT MAY NOT CLOSE A LOOP ──────────────────────────────────────
        // Advancing "next to an open side" is not enough on its own: a crack can curl round and
        // arrive at a side from BOTH ends, and breaking that side rings the material inside it off
        // from the rest of the body. The cells inside are then detached while still buried, which is
        // the failure this whole rule exists to prevent — just reached the long way round.
        //
        // Real surface is exempt, because an end lying on the silhouette is not a crack arriving; it
        // is where a crack legitimately starts. So only the case where BOTH ends are crack surface
        // is refused.
        if (openPrev && openNext && bp == SimState.SideCrack && bn == SimState.SideCrack)
            return false;

        return true;
    }

    /// <summary>What a side is. Derived from the touch records — never stored, so never stale.</summary>
    public enum SideKind
    {
        /// <summary>Facing open space: the body's outline, or a face erosion cut.</summary>
        RealSurface,
        /// <summary>Material across it, held by a live bond.</summary>
        Interior,
        /// <summary>Material across it, but no bond — the shared side was too short to build one.</summary>
        Sealed,
        /// <summary>Material across it, but the bond has broken: a crack face inside the body.</summary>
        Crack,
    }

    /// <summary>
    /// Classifies one side, and reports which fraction of it is actually covered.
    /// </summary>
    /// <remarks>
    /// <para><b>Derived, not stored.</b> Every earlier version kept this as flags that something had
    /// to remember to update — on bond break, on split, on comminution, on carve — and every bug in
    /// this area was a place that forgot. Here it is a pure function of the touch record and the
    /// bond, so there is nothing to forget and nothing to go stale.</para>
    ///
    /// <para><paramref name="f0"/>..<paramref name="f1"/> is the covered span as a fraction along
    /// the side from vertex <c>v</c> to <c>v+1</c>. A side can be PARTLY covered: when a cell erodes,
    /// the neighbour keeps its full side while only part of it still faces material, and the rest
    /// has become real surface. That is representable here because the record stores an interval
    /// rather than a length.</para>
    /// </remarks>
    public SideKind ClassifySide(int c, int v, out float f0, out float f1)
    {
        f0 = 0f; f1 = 0f;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (v < 0 || v >= len) return SideKind.RealSurface;

        short rec = _s.SideTouch[off + v];
        if (rec < 0 || rec >= _s.TouchCount) return SideKind.RealSurface;
        int a = _s.TouchA[rec];
        if (a < 0) return SideKind.RealSurface;                       // retired
        int o = _s.TouchOther(rec, c);
        if (o < 0 || o >= _s.CellCount || _s.Dead(o)) return SideKind.RealSurface;
        if (_s.CellBody[o] != _s.CellBody[c]) return SideKind.RealSurface;
        if (!RecordBisector(rec, out float ex, out float ey, out float mx, out float my))
            return SideKind.RealSurface;

        int w = v + 1 == len ? 0 : v + 1;
        float u0 = (_s.CellRx[c] + _s.PolyX[off + v] - mx) * ex + (_s.CellRy[c] + _s.PolyY[off + v] - my) * ey;
        float u1 = (_s.CellRx[c] + _s.PolyX[off + w] - mx) * ex + (_s.CellRy[c] + _s.PolyY[off + w] - my) * ey;
        if (SimMath.Abs(u1 - u0) < 1e-6f) return SideKind.RealSurface;

        float c0 = SimMath.Max(SimMath.Min(u0, u1), _s.TouchT0[rec]);
        float c1 = SimMath.Min(SimMath.Max(u0, u1), _s.TouchT1[rec]);
        if (c1 <= c0) return SideKind.RealSurface;                    // eroded clear of the overlap

        f0 = (c0 - u0) / (u1 - u0);
        f1 = (c1 - u0) / (u1 - u0);
        if (f0 > f1) (f0, f1) = (f1, f0);
        f0 = SimMath.Max(0f, f0); f1 = SimMath.Min(1f, f1);

        // SNAP SUB-PIXEL EXPOSURE AWAY. The record's interval is the intersection of the two cells'
        // extents, so two copies of a side that agree to float rounding still leave a sliver of a
        // few thousandths of a pixel uncovered at each end. Reported literally, every interior side
        // in the body carries two specks of "real surface" — which is what showed up as bold dots
        // scattered through solid material. Real erosion moves a surface by a fifth of a pixel per
        // substep, two orders of magnitude above this, so nothing genuine is hidden.
        float sideLen = SimMath.Abs(u1 - u0);
        float snap = sideLen > 1e-6f ? 0.05f / sideLen : 0f;
        if (f0 < snap) f0 = 0f;
        if (f1 > 1f - snap) f1 = 1f;

        short k = _s.TouchBond[rec];
        if (k < 0 || k >= _s.BondCount) return SideKind.Sealed;
        return _s.BondBroken[k] ? SideKind.Crack : SideKind.Interior;
    }

    /// <summary>
    /// Bitmask of this cell's <b>surface sides</b> — sides facing open space. Bit <c>i</c> is the
    /// edge from vertex <c>i</c> to <c>i+1</c>.
    /// </summary>
    /// <remarks>
    /// Reads the maintained labels rather than re-deriving them from geometry. It used to test each
    /// edge against each neighbour's seed bisector, which is correct only while nothing has moved:
    /// once carving replaced one endpoint of a shared edge with a vertex on the carve plane, the
    /// "both endpoints on the bisector" test failed and the edge was reported FREE — so interior
    /// sides were shed and gaps opened inside solid bodies. The renderer and the crack rule now read
    /// the same labels, so the picture cannot disagree with the physics.
    /// </remarks>
    public int FreeEdgeMask(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 3 || len > 30) return 0;
        int mask = 0;
        for (int v = 0; v < len; v++)
        {
            short k = _s.PolyBond[off + v];
            if (k == SimState.SideReal || k == SimState.SideCrack) mask |= 1 << v;
            else if (k >= 0 && _s.BondBroken[k]) mask |= 1 << v;
        }
        return mask;
    }

    /// <summary>True when the cell has at least one free side — it is on the body's boundary.</summary>
    public bool HasFreeEdge(int c) => FreeEdgeMask(c) != 0;

    /// <summary>
    /// The smallest plane offset along <c>n</c> that removes no vertex shared with a bonded
    /// neighbour. <c>float.NegativeInfinity</c> when the cell has no live bonds.
    /// </summary>
    /// <remarks>
    /// <para>A vertex is <i>bonded</i> when it lies on the plane of one of this cell's unbroken
    /// bonds — the shared edge is exactly the polygon's intersection with that plane. Protecting
    /// vertices protects whole edges: the region a half-plane removes from a convex polygon is one
    /// contiguous arc, and a straight segment with both endpoints kept cannot have its middle cut,
    /// so an edge survives whenever its two ends do.</para>
    ///
    /// <para>O(vertices x bonds), which is roughly 50 dot products and only runs when a cell is
    /// actually under enough load to carve.</para>
    /// </remarks>
    private float BondedGuard(int c, float nx, float ny)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        float guard = float.NegativeInfinity;

        // Read straight off the labels: a side with a bond on it may not be cut back past half the
        // length that bond was built with, so the bond can never be severed by erosion. A surface
        // side has no such limit — cutting surface back IS erosion.
        for (int v = 0; v < len; v++)
        {
            short k = _s.PolyBond[off + v];
            if (k == SimState.SideReal || k == SimState.SideCrack) continue;

            int w = v + 1 == len ? 0 : v + 1;
            float x0 = _s.PolyX[off + v], y0 = _s.PolyY[off + v];
            float x1 = _s.PolyX[off + w], y1 = _s.PolyY[off + w];
            float p0 = x0 * nx + y0 * ny, p1 = x1 * nx + y1 * ny;
            float loP = SimMath.Min(p0, p1), hiP = SimMath.Max(p0, p1);

            // A sealed side has no bond to measure against, so it is protected outright: there is
            // material behind it and nothing there may be cut.
            if (k < 0 || _s.BondBroken[k]) { if (hiP > guard) guard = hiP; continue; }

            float cur = SimMath.Hypot(x1 - x0, y1 - y0);
            float keep = 0.5f * _s.BondLen[k];
            float frac = cur > 1e-6f ? SimMath.Min(1f, keep / cur) : 1f;
            float proj = loP + frac * (hiP - loP);
            if (proj > guard) guard = proj;
        }
        return guard;
    }

    /// <summary>
    /// Re-derives the bonds of a cell whose centroid has moved, and rotates their stored stretch into
    /// the new axis.
    /// </summary>
    /// <remarks>
    /// <para>Bond anchors and the bond axis are expressed relative to cell centres, so carving — which
    /// moves a centroid — makes every bond on that cell stale. Left alone, the lever arms drift and
    /// the bond applies its force at the wrong point.</para>
    ///
    /// <para><b>The stretch has to rotate with the axis.</b> <c>BondSn</c>/<c>BondSt</c> are
    /// components in the bond frame; re-deriving the axis without rotating them leaves the stored
    /// elastic force pointing somewhere it was never pointing, a kick proportional to the angle. The
    /// old <c>Rebake</c> had exactly this bug and waved it through as "stretches are NOT touched".
    /// The rotation comes from the dot and cross of the old and new unit axes, so it costs no
    /// transcendental.</para>
    /// </remarks>
    private void ReanchorCarvedCell(int c)
    {
        int aoff = _s.AdjOff[c], alen = _s.AdjLen[c];
        for (int j = 0; j < alen; j++)
        {
            int k = _s.AdjBond[aoff + j];
            if (_s.BondBroken[k]) continue;
            float ox = _s.BondNx[k], oy = _s.BondNy[k];
            ReanchorBond(k);
            float co = ox * _s.BondNx[k] + oy * _s.BondNy[k];
            float si = ox * _s.BondNy[k] - oy * _s.BondNx[k];
            float sn = _s.BondSn[k], st = _s.BondSt[k];
            _s.BondSn[k] = sn * co + st * si;
            _s.BondSt[k] = -sn * si + st * co;
        }
    }

    /// <summary>
    /// Narrows every touch record this cell links, to the extent its side still covers.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole point of the representation. Carving removed material from ONE cell,
    /// and the shared segment shrank for BOTH — because there is one segment, not a copy per cell.
    /// One write here and the neighbour sees it; nothing has to be pushed across, and the two cells
    /// cannot end up disagreeing because there is nothing to disagree with.</para>
    ///
    /// <para>Run after the polygon has been re-centred: the shift moves CellR and the vertices by
    /// equal and opposite amounts, so body-frame positions — and therefore the bisector and the
    /// interval measured along it — are unchanged by it.</para>
    /// </remarks>
    /// <summary>
    /// Re-attaches carve faces that are geometrically indistinguishable from an adjacency the cell
    /// still has.
    /// </summary>
    /// <remarks>
    /// <para>A clip that skims along a shared boundary removes a slab of almost no thickness, and
    /// every vertex it emits there is marked as new carve face. The stretch then has no record, so
    /// the narrowing that follows shortens the adjacency by its whole length — destroying a contact
    /// that still physically exists, and leaving both cells drawing surface with the neighbour's
    /// material pressed against it.</para>
    ///
    /// <para>Measured on collide at grain 170, tick 69: record 74 between cells 24 and 38 lost
    /// exactly 9.21 of span, and cell 38's two new recordless sides measured 5.67 + 3.54 = 9.21. At
    /// a probe of two hundredths of a pixel the neighbour's material was still there.</para>
    ///
    /// <para>So the rule is geometric rather than procedural: a side lying on a live adjacency's
    /// bisector, inside its span, IS that adjacency — however it came to be emitted. Run before
    /// narrowing, because narrowing reads these labels.</para>
    /// </remarks>
    /// <param name="cut">
    /// How far the clip just reached past the deepest vertex — the most material it can have taken
    /// off anywhere, and so the most any surviving face can have moved.
    /// </param>
    /// <remarks>
    /// <para>A clip that runs at a shallow angle to a shared side shaves a wedge along it instead of
    /// trimming its end. The side comes back as two collinear pieces, and only the piece the clip
    /// kept intact still carries the record — the shaved piece returns as a fresh carve face. Left
    /// alone that face reports itself as surface while the neighbour is still flush against it, and
    /// the record narrows to the labelled half, so the NEIGHBOUR's copy reads half exposed too. That
    /// is a false surface straight down the middle of a bonded interface, and it is what let carving
    /// start on interior cells.</para>
    ///
    /// <para>The tolerance is taken from the cut rather than from the cell's size. A face can only
    /// have moved off the bisector by as much as this clip removed, so that distance is the exact
    /// reach needed and nothing wider is justified. The old fixed <c>CellRad * 5e-3</c> was 0.04 px
    /// against a 0.08 px shave, so it missed by a factor of two and the interface tore.</para>
    /// </remarks>
    private void RelinkCoincidentSides(int c, int nBefore, float cut)
    {
        if (nBefore <= 0) return;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];

        for (int v = 0; v < len; v++)
        {
            if (_s.SideTouch[off + v] >= 0) continue;
            int w = v + 1 == len ? 0 : v + 1;
            float x0 = _s.PolyX[off + v], y0 = _s.PolyY[off + v];
            float x1 = _s.PolyX[off + w], y1 = _s.PolyY[off + w];
            float tol = SimMath.Max(SimMath.Max(1e-2f, _s.CellRad[c] * 5e-3f), cut);

            for (int q = 0; q < nBefore; q++)
            {
                short rec = _carveRecs[q];
                if (rec < 0 || rec >= _s.TouchCount || _s.TouchA[rec] < 0) continue;
                if (!RecordBisector(rec, out float ex, out float ey, out float mx, out float my)) continue;

                float nx = -ey, ny = ex;
                float bx0 = _s.CellRx[c] + x0 - mx, by0 = _s.CellRy[c] + y0 - my;
                float bx1 = _s.CellRx[c] + x1 - mx, by1 = _s.CellRy[c] + y1 - my;
                if (c == TraceCell)
                    TraceSink?.Invoke($"     relink? side {v} vs record {rec} (with {_s.TouchOther(rec, c)}): "
                        + $"endpoint gaps {SimMath.Abs(bx0 * nx + by0 * ny):F4} / {SimMath.Abs(bx1 * nx + by1 * ny):F4}, tol {tol:F4}");
                if (SimMath.Abs(bx0 * nx + by0 * ny) > tol) continue;
                if (SimMath.Abs(bx1 * nx + by1 * ny) > tol) continue;

                float u0 = bx0 * ex + by0 * ey, u1 = bx1 * ex + by1 * ey;
                float lo = SimMath.Min(u0, u1), hi = SimMath.Max(u0, u1);
                if (SimMath.Min(hi, _s.TouchT1[rec]) - SimMath.Max(lo, _s.TouchT0[rec]) <= 1e-3f) continue;

                // BOTH labels, or the side contradicts itself: SideTouch naming a live adjacency
                // while PolyBond still says REAL is exactly the state the audit reports as
                // OpenSideIsCovered and BondHasNoSide, and the renderer draws as bare surface.
                short bk = _s.TouchBond[rec];
                _s.SideTouch[off + v] = rec;
                _s.PolyBond[off + v] = bk >= 0 && bk < _s.BondCount && !_s.BondBroken[bk]
                                     ? bk : SimState.SideSealed;
                CcRelinked++;
                break;
            }
        }
    }

    private void NarrowTouchRecords(int c)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++)
        {
            short rec = _s.SideTouch[off + v];
            if (rec < 0 || rec >= _s.TouchCount || _s.TouchA[rec] < 0) continue;

            bool seen = false;                       // already handled on an earlier side
            for (int q = 0; q < v && !seen; q++) seen = _s.SideTouch[off + q] == rec;
            if (seen) continue;

            int o = _s.TouchOther(rec, c);
            if (o < 0 || o >= _s.CellCount) continue;
            if (!RecordBisector(rec, out float ex, out float ey, out float mx, out float my)) continue;

            // UNION across every side carrying this record, THEN intersect. One shared boundary can
            // span several collinear sides, and intersecting with each in turn would narrow the
            // record down to whichever fragment happened to come last.
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int q = 0; q < len; q++)
            {
                if (_s.SideTouch[off + q] != rec) continue;
                int w = q + 1 == len ? 0 : q + 1;
                float p0 = (_s.CellRx[c] + _s.PolyX[off + q] - mx) * ex
                         + (_s.CellRy[c] + _s.PolyY[off + q] - my) * ey;
                float p1 = (_s.CellRx[c] + _s.PolyX[off + w] - mx) * ex
                         + (_s.CellRy[c] + _s.PolyY[off + w] - my) * ey;
                lo = SimMath.Min(lo, SimMath.Min(p0, p1));
                hi = SimMath.Max(hi, SimMath.Max(p0, p1));
            }
            if (lo > hi) continue;

            if (lo > _s.TouchT0[rec]) _s.TouchT0[rec] = lo;
            if (hi < _s.TouchT1[rec]) _s.TouchT1[rec] = hi;

            if (_s.TouchT1[rec] - _s.TouchT0[rec] <= 1e-3f) RetireTouch(rec, $"span-collapsed(c={c}, lo={lo:F3}, hi={hi:F3})");
        }
    }

    /// <summary>
    /// The adjacency has ended — carving ate through the last of the shared side.
    /// </summary>
    /// <remarks>
    /// The natural end of erosion, not an error: the material joining two cells has been removed, so
    /// they stop touching and any bond along the adjacency stops existing. This is what replaces the
    /// arbitrary "never cut a shared side past half its build length" guard — instead of forbidding
    /// the cut, the model lets it happen and gives it its consequence.
    /// </remarks>
    internal int CcVanishTotal, CcSteered, CcShielded, CcCollapsed, CcRelinked;
    internal System.Action<int, int, int, string>? RetireSink;

    /// <summary>Diagnostic tally of what carving actually does. Null (and free) unless a tool asks.</summary>
    internal sealed class CarveCensus
    {
        public int Calls, NoSurface;
        public int Unturned;                            // direction already eroded open material
        public readonly int[] Turn = new int[11];       // cos of the rotation applied to the rest
        public readonly int[] AreaFrac = new int[11];   // removed area / cell area, 0..1% .. 10%+
        public readonly int[] DepthPx = new int[11];    // cut depth in px, 0.01 .. 10+
        public int Vanished, VanishAfterTurn;
        // Steer targets: an endpoint shared with a covered side vs. a genuinely open corner.
        public int SteerToCoveredCorner, SteerToOpenCorner;
        // Shield outcomes.
        public int ShieldClamped, ShieldRefused; public double ClampedArea, ClampedWant, TotalRemoved;
        public int GuardClamped;   // BondedGuard inside CarveCell moved the plane
        public readonly int[] VanishSpan = new int[11];   // remaining span of the retired adjacency, px
        public bool LastTurned;
        public readonly int[] VanishCos = new int[11];  // |cos| of clip normal vs vanished bisector
    }

    internal CarveCensus? Census;
    internal bool CarveShield = true;

    private static void Bin(int[] b, float v01) =>
        b[(int)SimMath.Max(0f, SimMath.Min(10f, v01 * 10f))]++;

    private void RetireTouch(int rec, string why = "?")
    {
        int a = _s.TouchA[rec], b = _s.TouchB[rec];
        RetireSink?.Invoke(rec, a, b, why);
        ExposeRecordEnds(rec);                    // its endpoints are surface now
        short k = _s.TouchBond[rec];
        if (k >= 0 && k < _s.BondCount && !_s.BondBroken[k])
        {
            _s.BondBroken[k] = true;
            _s.BondSn[k] = 0f; _s.BondSt[k] = 0f; _s.BondSa[k] = 0f;
            if (a >= 0) MarkDirty(_s.CellBody[a]);
        }
        UnlinkSide(a, rec);
        UnlinkSide(b, rec);
        _s.TouchA[rec] = -1; _s.TouchB[rec] = -1; _s.TouchBond[rec] = -1;
    }

    /// <summary>Retires every adjacency whose two cells no longer share a body, or one of which died.</summary>
    internal void RetireSeparatedTouches()
    {
        for (int r = 0; r < _s.TouchCount; r++)
        {
            int a = _s.TouchA[r], b = _s.TouchB[r];
            if (a < 0 || b < 0) continue;
            if (_s.Dead(a) || _s.Dead(b) || _s.CellBody[a] != _s.CellBody[b]) RetireTouch(r, $"separated(dead {_s.Dead(a)}/{_s.Dead(b)}, bodies {_s.CellBody[a]}/{_s.CellBody[b]})");
        }
    }

    /// <summary>Drops a retired record from a cell's sides, and promotes those sides to surface.</summary>
    /// <remarks>
    /// The promotion is the point. A side's two labels answer different questions — <c>SideTouch</c>
    /// names the adjacency, <c>PolyBond</c> says what the side IS — and retiring a record only ever
    /// answered the first. A bonded side got the second answered anyway, because breaking its bond
    /// runs <see cref="OpenBondSides"/>. A SEALED side has no bond to break, so nothing promoted it:
    /// it went on calling itself sealed — "material across me" — after the material had eroded away.
    /// The side then read as real surface to <see cref="ClassifySide"/> (its record was gone) and as
    /// sealed to the renderer, so it drew as neither surface nor crack: a stub of boundary inside
    /// solid material, attached to nothing.
    /// </remarks>
    private void UnlinkSide(int c, int rec)
    {
        if (c < 0 || c >= _s.CellCount) return;
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        for (int v = 0; v < len; v++)
        {
            if (_s.SideTouch[off + v] != rec) continue;
            _s.SideTouch[off + v] = -1;

            // Nothing is across it any more, so it has to stop claiming otherwise — whatever kind of
            // cover it claimed. A side that was bonded leaves crack surface, because the bond it
            // names has just been broken; a sealed side was joined to nothing, so it is simply the
            // outside now. Leaving either alone gives a side that ClassifySide reads as surface (its
            // record is gone) while PolyBond still reads as covered, which is the state that draws
            // as neither surface nor crack.
            short pb = _s.PolyBond[off + v];
            if (pb == SimState.SideSealed) _s.PolyBond[off + v] = SimState.SideReal;
            else if (pb >= 0) _s.PolyBond[off + v] = SimState.SideCrack;
        }

        // A side that has just become surface may have no length — a build-time sealed sliver whose
        // neighbour has gone. As surface it would be a degenerate corner candidate, so it is merged
        // here, where it became surface, not only in the dent rebuild the cell may never enter.
        MergeSpentSides(c);
    }

    /// <summary>
    /// The bisector of a RECORD, always oriented from its own A to its own B.
    /// </summary>
    /// <remarks>
    /// The orientation has to come from the record, never from whichever cell happens to be asking.
    /// Deriving it from (querying cell -> other cell) flips the axis whenever the query comes from
    /// TouchB, and the stored interval was measured along the unflipped axis — so the span keeps the
    /// right LENGTH but lands in the wrong place. Measured: a side 17.87 long, a record spanning
    /// exactly 17.87, and a reported coverage of [0.348, 1.000]. Half the interior sides of every
    /// body claimed to be a third exposed, at build, before anything had moved.
    /// </remarks>
    internal bool RecordBisector(int rec, out float ex, out float ey, out float mx, out float my)
    {
        ex = 1f; ey = 0f; mx = 0f; my = 0f;
        int a = _s.TouchA[rec], b = _s.TouchB[rec];
        if (a < 0 || b < 0) return false;
        return TouchBisector(a, b, out ex, out ey, out mx, out my);
    }

    /// <summary>The perpendicular bisector of two cells' seeds, in body coordinates.</summary>
    internal bool TouchBisector(int a, int b, out float ex, out float ey, out float mx, out float my)
    {
        float sax = _s.CellRx[a] + _s.CellSeedX[a], say = _s.CellRy[a] + _s.CellSeedY[a];
        float sbx = _s.CellRx[b] + _s.CellSeedX[b], sby = _s.CellRy[b] + _s.CellSeedY[b];
        float dx = sbx - sax, dy = sby - say;
        float d = SimMath.Hypot(dx, dy);
        mx = 0.5f * (sax + sbx); my = 0.5f * (say + sby);
        if (d < 1e-6f) { ex = 1f; ey = 0f; return false; }
        ex = -dy / d; ey = dx / d;
        return true;
    }

    private float PolyArea(int off, int len)
    {
        float a2 = 0f;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            a2 += _s.PolyX[off + i] * _s.PolyY[off + j] - _s.PolyX[off + j] * _s.PolyY[off + i];
        }
        return 0.5f * SimMath.Abs(a2);
    }

    /// <summary>
    /// Second moment of area about the origin, which the caller has made the centroid. Same form as
    /// <c>Geometry2D.PolyInertia</c>, which bakes it at build — the two must agree or a carved cell
    /// would step discontinuously the first time it is clipped.
    /// </summary>
    private float PolyInertia(int off, int len)
    {
        float num = 0f;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            float x0 = _s.PolyX[off + i], y0 = _s.PolyY[off + i];
            float x1 = _s.PolyX[off + j], y1 = _s.PolyY[off + j];
            float cr = SimMath.Abs(x0 * y1 - x1 * y0);
            num += cr * (x0 * x0 + x0 * x1 + x1 * x1 + y0 * y0 + y0 * y1 + y1 * y1);
        }
        return num / 12f;
    }

    private void EnsureClipScratch(int n)
    {
        if (_clipX.Length >= n) return;
        _clipX = new float[n * 2];
        _clipY = new float[n * 2];
        _clipB = new short[n * 2];
        _clipS = new short[n * 2];
    }
}
