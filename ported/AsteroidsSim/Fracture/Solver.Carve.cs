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

    private float[] _clipX = new float[32];
    private float[] _clipY = new float[32];

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
    internal float CarveCell(int c, float px, float py, float nx, float ny)
    {
        int off = _s.PolyOff[c], len = _s.PolyLen[c];
        if (len < 3) return 0f;

        // Reach test first: if every vertex is strictly kept, there is nothing to do.
        float worst = float.NegativeInfinity;
        for (int v = 0; v < len; v++)
        {
            float d = (_s.PolyX[off + v] - px) * nx + (_s.PolyY[off + v] - py) * ny;
            if (d > worst) worst = d;
        }
        if (worst <= 0f) return 0f;

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

            if (dc <= 0f) { _clipX[n] = cx; _clipY[n] = cy; n++; }
            if ((dc <= 0f) != (dn <= 0f))
            {
                float t = dc / (dc - dn);
                _clipX[n] = cx + t * (ax - cx);
                _clipY[n] = cy + t * (ay - cy);
                n++;
            }
        }

        // Clipped away entirely, or down to a degenerate sliver. The caller's shed-limit test will
        // remove the cell; report the whole area as gone and leave the polygon alone so nothing
        // downstream ever sees a sub-triangle.
        if (n < 3) return area0;

        // ── fit the budget ───────────────────────────────────────────────────
        while (n > _s.PolyCap[c]) { DropLeastSignificantVertex(ref n); CarveSimplifications++; }

        Array.Copy(_clipX, 0, _s.PolyX, off, n);
        Array.Copy(_clipY, 0, _s.PolyY, off, n);
        _s.PolyLen[c] = n;
        CarveClips++;

        RefreshCellShape(c);
        return SimMath.Max(0f, area0 - _s.CellArea[c]);
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
    private void DropLeastSignificantVertex(ref int n)
    {
        int best = 0;
        float bestCost = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int p = i == 0 ? n - 1 : i - 1;
            int q = i + 1 == n ? 0 : i + 1;
            float ux = _clipX[i] - _clipX[p], uy = _clipY[i] - _clipY[p];
            float vx = _clipX[q] - _clipX[i], vy = _clipY[q] - _clipY[i];
            float cost = SimMath.Abs(ux * vy - uy * vx);   // twice the triangle area
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        for (int i = best; i + 1 < n; i++) { _clipX[i] = _clipX[i + 1]; _clipY[i] = _clipY[i + 1]; }
        n--;
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

        _s.CellRx[c] += cx;
        _s.CellRy[c] += cy;
        _s.CellArea[c] = area;
        _s.CellPerim[c] = perim;
        _s.CellRad[c] = SimMath.Sqrt(rad2);

        // Inertia follows the shape at FIXED mass: the mass a cell still has is distributed over
        // the area it still has. Recomputing it as rho * I_poly instead would silently re-derive the
        // mass too, and mass only ever changes through the shed path.
        float ipoly = PolyInertia(off, len);
        float ic = SimMath.Max(1e-6f, _s.CellM[c] * ipoly / area);
        _s.CellIc[c] = ic;
        _s.CellIic[c] = 1f / ic;
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
    }
}
