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
    public float Ln;   // accumulated normal impulse
    public float Lt;   // accumulated tangential impulse
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

    private float[] _polyAx = new float[64];
    private float[] _polyAy = new float[64];
    private float[] _polyBx = new float[64];
    private float[] _polyBy = new float[64];

    // split scratch
    private int[] _comp = Array.Empty<int>();
    private int[] _stack = Array.Empty<int>();
    private int _lastComp = -1;
    private int _lastLive = -1;

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
    public int Broken;
    public int Dust;
    public int Crushed;
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

    // ══════════════════════════════════════════════════════════════════════════
    //  transforms
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-cell rest rotation, cached for the substep. One trig call per cell rather than one per
    /// shared vertex. A rebake changes <c>phi</c> outside the substep clock and invalidates by
    /// clearing the stamp.
    /// </summary>
    private void CellRot(int c)
    {
        if (_s.CellRotStamp[c] == _s.Substep) return;
        _s.CellRotStamp[c] = _s.Substep;
        SimMath.SinCos(_s.CellPhi[c] + _s.CellUth[c], out float sa, out float ca);
        _s.CellCa[c] = ca;
        _s.CellSa[c] = sa;
    }

    private void UpdateCenters()
    {
        for (int b = 0; b < _s.BodyCount; b++)
        {
            SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                float x = _s.CellRx[c] + _s.CellUx[c];
                float y = _s.CellRy[c] + _s.CellUy[c];
                _s.CellPx[c] = _s.BodyX[b] + x * co - y * si;
                _s.CellPy[c] = _s.BodyY[b] + x * si + y * co;
            }
        }
    }

    /// <summary>
    /// Writes cell <paramref name="c"/>'s world polygon into the supplied buffers using
    /// shared-vertex skinning, and returns the vertex count.
    /// </summary>
    private int CellPoly(int c, float[] outX, float[] outY)
    {
        int body = _s.CellBody[c];
        SimMath.SinCos(_s.BodyRot[body], out float si, out float co);
        int off = _s.PolyOff[c], len = _s.PolyLen[c];

        for (int v = 0; v < len; v++)
        {
            float ax = 0f, ay = 0f;
            int n = 0;
            int g = _s.PolyGroup[off + v];
            if (g >= 0)
            {
                int goff = _s.GrpOff[g], glen = _s.GrpLen[g];
                for (int k = 0; k < glen; k++)
                {
                    int cm = _s.GrpCell[goff + k];
                    if (_s.CellDead[cm] || _s.CellBody[cm] != body) continue;
                    CellRot(cm);
                    int mv = _s.GrpVert[goff + k];
                    float qx = _s.PolyX[_s.PolyOff[cm] + mv];
                    float qy = _s.PolyY[_s.PolyOff[cm] + mv];
                    ax += _s.CellRx[cm] + _s.CellUx[cm] + qx * _s.CellCa[cm] - qy * _s.CellSa[cm];
                    ay += _s.CellRy[cm] + _s.CellUy[cm] + qx * _s.CellSa[cm] + qy * _s.CellCa[cm];
                    n++;
                }
            }
            if (n == 0)
            {
                CellRot(c);
                float qx = _s.PolyX[off + v], qy = _s.PolyY[off + v];
                ax = _s.CellRx[c] + _s.CellUx[c] + qx * _s.CellCa[c] - qy * _s.CellSa[c];
                ay = _s.CellRy[c] + _s.CellUy[c] + qx * _s.CellSa[c] + qy * _s.CellCa[c];
                n = 1;
            }
            ax /= n; ay /= n;
            outX[v] = _s.BodyX[body] + ax * co - ay * si;
            outY[v] = _s.BodyY[body] + ax * si + ay * co;
        }
        return len;
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
        UpdateCenters();

        float vmax = 0f, cellMax = 30f;
        for (int b = 0; b < _s.BodyCount; b++)
        {
            float sp = SimMath.Hypot(_s.BodyVx[b], _s.BodyVy[b]) + SimMath.Abs(_s.BodyW[b]) * 150f;
            if (sp > vmax) vmax = sp;
            if (_s.BodyCellSize[b] * 2f > cellMax) cellMax = _s.BodyCellSize[b] * 2f;
        }
        float margin = SimMath.Min(100f, 4f + vmax * Dt);
        float cs = cellMax + margin;

        _grid.Clear();
        _bucketsUsed = 0;

        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.CellDead[c]) continue;
            int key = GridKey((int)SimMath.Floor(_s.CellPx[c] / cs), (int)SimMath.Floor(_s.CellPy[c] / cs));
            if (!_grid.TryGetValue(key, out var bucket)) { bucket = RentBucket(); _grid[key] = bucket; }
            bucket.Add(c);
        }

        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.CellDead[c]) continue;
            int gx = (int)SimMath.Floor(_s.CellPx[c] / cs);
            int gy = (int)SimMath.Floor(_s.CellPy[c] / cs);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!_grid.TryGetValue(GridKey(gx + dx, gy + dy), out var bucket)) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int o = bucket[i];
                        // index order dedupes each symmetric pair
                        if (_s.CellBody[o] == _s.CellBody[c] || o <= c) continue;
                        float ddx = _s.CellPx[o] - _s.CellPx[c], ddy = _s.CellPy[o] - _s.CellPy[c];
                        float rr = _s.CellRad[c] + _s.CellRad[o] + margin;
                        if (ddx * ddx + ddy * ddy > rr * rr) continue;
                        if (_pairCount + 2 > _pairs.Length) Array.Resize(ref _pairs, _pairs.Length * 2);
                        _pairs[_pairCount++] = c;
                        _pairs[_pairCount++] = o;
                    }
                }
        }
    }

    /// <summary>
    /// The reference's grid hash, reproduced exactly. Only ever probed, never iterated — a hash
    /// container's enumeration order is not defined and must not reach the simulation.
    /// </summary>
    private static int GridKey(int gx, int gy)
        => unchecked((gx * 73856093) ^ (gy * 19349663));

    private void BuildContacts()
    {
        _contactCount = 0;
        if (_pairCount == 0) return;
        UpdateCenters();

        for (int p = 0; p < _pairCount; p += 2)
        {
            int c = _pairs[p], o = _pairs[p + 1];
            if (_s.CellDead[c] || _s.CellDead[o]) continue;
            if (_s.CellBody[c] == _s.CellBody[o]) continue;
            float ddx = _s.CellPx[o] - _s.CellPx[c], ddy = _s.CellPy[o] - _s.CellPy[c];
            float rr = _s.CellRad[c] + _s.CellRad[o];
            if (ddx * ddx + ddy * ddy > rr * rr) continue;

            EnsurePolyBuf(_s.PolyLen[c] > _s.PolyLen[o] ? _s.PolyLen[c] : _s.PolyLen[o]);
            int na = CellPoly(c, _polyAx, _polyAy);
            int nb = CellPoly(o, _polyBx, _polyBy);

            if (!Sat(_polyAx, _polyAy, na, _polyBx, _polyBy, nb,
                     out float nx, out float ny, out float depth, out float px, out float py))
                continue;

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

            if (_contactCount >= _contacts.Length) Array.Resize(ref _contacts, _contacts.Length * 2);
            _contacts[_contactCount++] = new Contact
            {
                A = c, B = o, Nx = nx, Ny = ny, Depth = depth, Px = px, Py = py, Ln = 0f, Lt = 0f,
            };
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
    private static bool Sat(
        float[] ax, float[] ay, int na, float[] bx, float[] by, int nb,
        out float nx, out float ny, out float depth, out float px, out float py)
    {
        float best = float.PositiveInfinity;
        float bnx = 0f, bny = 0f;
        bool have = false, flip = false;

        for (int side = 0; side < 2; side++)
        {
            float[] px0 = side == 0 ? ax : bx, py0 = side == 0 ? ay : by;
            float[] qx0 = side == 0 ? bx : ax, qy0 = side == 0 ? by : ay;
            int pn = side == 0 ? na : nb, qn = side == 0 ? nb : na;

            for (int i = 0; i < pn; i++)
            {
                float x0 = px0[i], y0 = py0[i];
                float x1 = px0[(i + 1) % pn], y1 = py0[(i + 1) % pn];
                float ex = y1 - y0, ey = -(x1 - x0);
                float L = SimMath.Hypot(ex, ey);
                if (L < 1e-9f) continue;
                ex /= L; ey /= L;

                float mp = float.NegativeInfinity;
                for (int v = 0; v < pn; v++)
                {
                    float d = px0[v] * ex + py0[v] * ey;
                    if (d > mp) mp = d;
                }
                float mq = float.PositiveInfinity;
                for (int v = 0; v < qn; v++)
                {
                    float d = qx0[v] * ex + qy0[v] * ey;
                    if (d < mq) mq = d;
                }
                float ov = mp - mq;
                if (ov <= 0f)
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
        for (int v = 0; v < nb; v++)
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
        for (int k = 0; k < _s.BondCount; k++)
        {
            if (_s.BondBroken[k]) continue;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.CellDead[a] || _s.CellDead[b]) continue;

            float kk = _s.BondK0[k], ka = _s.BondKa0[k];
            float soft = 1f - _s.BondDmg[k];
            float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];

            float fn = -(sn > 0f ? kk * soft : kk) * sn;
            float ft = -kk * soft * st;
            float fa = -ka * soft * sa;

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

    /// <summary>
    /// Pass two: integrate the stretch from the updated deviation velocities. Two passes rather
    /// than one loop because every bond touching a cell must contribute to its velocity before any
    /// stretch is integrated from it — which is also what makes propagation at the material's wave
    /// speed emergent rather than imposed.
    /// </summary>
    private void BondIntegrate(float h)
    {
        bool rateSens = _tune.RateSens > 0f;
        for (int k = 0; k < _s.BondCount; k++)
        {
            if (_s.BondBroken[k]) continue;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.CellDead[a] || _s.CellDead[b]) continue;

            float nx = _s.BondNx[k], ny = _s.BondNy[k];
            float tx = -ny, ty = nx;
            float rax = _s.BondRax[k], ray = _s.BondRay[k];
            float rbx = _s.BondRbx[k], rby = _s.BondRby[k];

            float vax = _s.CellDvx[a] - _s.CellDw[a] * ray;
            float vay = _s.CellDvy[a] + _s.CellDw[a] * rax;
            float vbx = _s.CellDvx[b] - _s.CellDw[b] * rby;
            float vby = _s.CellDvy[b] + _s.CellDw[b] * rbx;
            float rvx = vbx - vax, rvy = vby - vay, rva = _s.CellDw[b] - _s.CellDw[a];

            if (rateSens)
                _s.BondRate[k] = SimMath.Hypot(rvx, rvy) / SimMath.Max(1f, _s.BondLen[k]);

            _s.BondSn[k] += (rvx * nx + rvy * ny) * h;
            _s.BondSt[k] += (rvx * tx + rvy * ty) * h;
            _s.BondSa[k] += rva * h;
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
        for (int b = 0; b < _s.BodyCount; b++)
        {
            float w = _s.BodyW[b], a = _s.BodyAlpha[b];
            float w2 = w * w;
            _s.BodyEulL[b] = 0f;
            if (w2 < 1e-12f && SimMath.Abs(a) < 1e-12f) continue;

            float work = 0f, ip = 0f;
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
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
        for (int b = 0; b < _s.BodyCount; b++)
        {
            float px = 0f, py = 0f, L = 0f, M = 0f;
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                px += _s.CellM[c] * _s.CellDvx[c];
                py += _s.CellM[c] * _s.CellDvy[c];
                M += _s.CellM[c];
                L += _s.CellM[c] * (_s.CellRx[c] * _s.CellDvy[c] - _s.CellRy[c] * _s.CellDvx[c])
                     + _s.CellIc[c] * _s.CellDw[c];
            }
            if (M < 1e-9f) continue;
            float vx = px / M, vy = py / M, w = L / SimMath.Max(1f, _s.BodyI[b]);
            if (vx * vx + vy * vy < 1e-24f && SimMath.Abs(w) < 1e-12f) continue;

            SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
            _s.BodyVx[b] += vx * co - vy * si;
            _s.BodyVy[b] += vx * si + vy * co;
            _s.BodyW[b] += w;

            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                _s.CellDvx[c] -= vx - w * _s.CellRy[c];
                _s.CellDvy[c] -= vy + w * _s.CellRx[c];
                _s.CellDw[c] -= w;
            }
        }
    }
}
