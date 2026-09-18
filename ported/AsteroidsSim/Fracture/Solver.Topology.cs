using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{

    // preallocated split scratch — see RebuildBodies
    private float[] _oldX = Array.Empty<float>(), _oldY = Array.Empty<float>();
    private float[] _oldRot = Array.Empty<float>(), _oldVx = Array.Empty<float>();
    private float[] _oldVy = Array.Empty<float>(), _oldW = Array.Empty<float>();
    private float[] _oldRho = Array.Empty<float>(), _oldCpx = Array.Empty<float>();
    private float[] _oldChi = Array.Empty<float>(), _oldVCrit = Array.Empty<float>();
    private float[] _oldDuct = Array.Empty<float>(), _oldCell = Array.Empty<float>();
    private int[] _srcBody = Array.Empty<int>();

    private void EnsureSplitScratch(int oldCount, int comps)
    {
        if (_oldX.Length < oldCount)
        {
            int n = oldCount < 16 ? 16 : oldCount * 2;
            _oldX = new float[n]; _oldY = new float[n]; _oldRot = new float[n];
            _oldVx = new float[n]; _oldVy = new float[n]; _oldW = new float[n];
            _oldRho = new float[n]; _oldCpx = new float[n]; _oldChi = new float[n];
            _oldVCrit = new float[n]; _oldDuct = new float[n]; _oldCell = new float[n];
        }
        if (_srcBody.Length < comps) _srcBody = new int[comps < 16 ? 16 : comps * 2];
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  splitting — incremental, driven by per-body dirty flags
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-partitions the cells of <b>dirty</b> bodies into connected components over the surviving
    /// bonds, and refreshes their aggregates.
    /// </summary>
    /// <remarks>
    /// <para><b>Why dirty flags.</b> A broken bond can only ever disconnect the body it belongs to,
    /// and a vaporised cell can only change the aggregates of its own body — so a global walk over
    /// every cell after every break is re-deriving an answer it already has. Measured before this
    /// change: five walks per tick, each over all 2,135 cells, and roughly half of them concluded
    /// that nothing had split. A body is marked dirty when it loses a bond or a cell and is cleared
    /// once processed.</para>
    ///
    /// <para><b>Numbering is part of the contract.</b> Dirty bodies are processed in index order.
    /// Within one, the component containing the lowest-indexed cell keeps the parent's index and
    /// the rest are appended in discovery order. That rule is what makes body indices — and hence
    /// the order of every per-body loop after this — a deterministic function of cell indices.
    /// It differs from the old global renumbering, which is a behaviour change, not a bug.</para>
    ///
    /// <para>A fragment inherits the parent's rigid motion AT ITS OWN CENTROID, which conserves
    /// linear momentum exactly, and its angular momentum follows from the parallel-axis theorem
    /// provided inertia is recomputed from the same offsets. Re-centring uses the DEFORMED
    /// configuration so the collider stays centred on the pose.</para>
    /// </remarks>
    private void RebuildBodies()
    {
        C.SplitCalls++;
        if (!AnyDirty()) { C.SplitNoChange++; return; }
        _boundsTick = -1;                        // body set is about to change under the chunk split
        _touchEpoch++;

        int n = _s.CellCount;
        if (_comp.Length < n) { _comp = new int[n]; _stack = new int[n]; }
        if (_parentOf.Length < _s.BodyCount + 64)
        {
            var np = new int[_s.BodyCount + 256];
            for (int i = 0; i < np.Length; i++) np[i] = -1;
            Array.Copy(_parentOf, np, _parentOf.Length);
            _parentOf = np;
        }

        bool anyStructuralChange = false;
        int dirtyProcessed = 0;

        // Snapshot every dirty body's rigid state before touching it: fragments read the PARENT's
        // pose and velocity, and the parent's own record is about to be re-centred.
        int bodiesAtEntry = _s.BodyCount;
        for (int b = 0; b < bodiesAtEntry; b++)
        {
            if (!_s.BodyDirty[b]) continue;
            dirtyProcessed++;
            EnsureSplitScratch(bodiesAtEntry, bodiesAtEntry);
            _oldX[b] = _s.BodyX[b]; _oldY[b] = _s.BodyY[b]; _oldRot[b] = _s.BodyRot[b];
            _oldVx[b] = _s.BodyVx[b]; _oldVy[b] = _s.BodyVy[b]; _oldW[b] = _s.BodyW[b];
            _oldRho[b] = _s.BodyRho[b]; _oldCpx[b] = _s.BodyCpx[b]; _oldChi[b] = _s.BodyChi[b];
            _oldVCrit[b] = _s.BodyVCrit[b]; _oldDuct[b] = _s.BodyDuct[b];
            _oldCell[b] = _s.BodyCellSize[b];
        }
        if (dirtyProcessed == 0) { C.SplitNoChange++; return; }

        // Walk components inside each dirty body only.
        _touchedCount = 0;
        for (int b = 0; b < bodiesAtEntry; b++)
        {
            if (!_s.BodyDirty[b]) continue;

            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++) _comp[_s.BodyCells[off + i]] = -1;

            int local = 0;
            for (int i = 0; i < len; i++)
            {
                int seed = _s.BodyCells[off + i];
                if (_s.Dead(seed) || _comp[seed] >= 0) continue;

                // The component containing the lowest-indexed cell keeps the parent's index;
                // later ones are appended.
                int target = local == 0 ? b : _s.BodyCount;
                if (local > 0)
                {
                    _s.EnsureBodies(_s.BodyCount + 1);
                    InheritBody(_s.BodyCount, b);
                    _s.BodyCount++;
                    anyStructuralChange = true;
                }

                int sp = 0;
                _stack[sp++] = seed;
                _comp[seed] = target;
                while (sp > 0)
                {
                    int u = _stack[--sp];
                    C.SplitCellsWalked++;
                    _s.CellBody[u] = target;
                    int aoff = _s.AdjOff[u], alen = _s.AdjLen[u];
                    for (int j = 0; j < alen; j++)
                    {
                        int k = _s.AdjBond[aoff + j];
                        if (_s.BondBroken[k]) continue;
                        int v = _s.BondA[k] == u ? _s.BondB[k] : _s.BondA[k];
                        if (_s.Dead(v) || _comp[v] >= 0) continue;
                        _comp[v] = target;
                        _stack[sp++] = v;
                    }
                }
                MarkTouched(target);
                local++;
            }

            _s.BodyDirty[b] = false;
            if (local > 1) anyStructuralChange = true;
            MarkTouched(b);
        }

        if (!anyStructuralChange) C.SplitNoChange++; else C.SplitPerformed++;

        // Membership and adjacency are flat offset tables, so they are rebuilt whole; both are a
        // single linear pass and did not show up in the profile.
        BodyBuilder.RebuildMembership(_s);

        for (int i = 0; i < _touchedCount; i++) RecomputeBody(_touched[i]);

        // A fragment's cut faces are its outline now: the material across them left with another
        // body. Until they are promoted they stay crack surface, so the new boundary neither draws
        // as boundary nor lets a fresh crack start from it.
        for (int i = 0; i < _touchedCount; i++)
        {
            int tb = _touched[i];
            int coff = _s.BodyCellOff[tb], clen = _s.BodyCellLen[tb];
            for (int j = 0; j < clen; j++) ExposeSeparatedSides(_s.BodyCells[coff + j]);
        }

        // Two cells that ended up in different bodies are no longer adjacent — whatever held them
        // together has gone, and the sides facing each other are now each fragment's own outline.
        RetireSeparatedTouches();

        // Re-anchor only the bonds of touched bodies. This used to scan every bond in the world.
        for (int i = 0; i < _touchedCount; i++)
        {
            int b = _touched[i];
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int j = 0; j < len; j++)
            {
                int c = _s.BodyCells[off + j];
                int aoff = _s.AdjOff[c], alen = _s.AdjLen[c];
                for (int q = 0; q < alen; q++) ReanchorBond(_s.AdjBond[aoff + q]);
            }
        }

        // Dust always empties a single-cell body, and the old global rebuild simply never emitted
        // empty ones. Incrementally they linger, and an empty body has zero mass, which divides.
        CompactBodies();

        _s.Reindex();
        BumpRotEpoch();          // body indices changed; the trig cache is keyed on them
    }

    /// <summary>
    /// Removes bodies that have no live cells, preserving the relative order of the rest.
    /// </summary>
    /// <remarks>
    /// Order-preserving rather than swap-remove: body index order drives every per-body loop, and a
    /// stable compaction keeps that order a monotone function of creation order, which is far easier
    /// to reason about when reading a divergence.
    /// </remarks>
    private void CompactBodies()
    {
        int w = 0;
        bool moved = false;
        for (int r = 0; r < _s.BodyCount; r++)
        {
            if (_s.BodyCellLen[r] == 0) { moved = true; continue; }
            if (w != r)
            {
                moved = true;
                _s.BodyX[w] = _s.BodyX[r]; _s.BodyY[w] = _s.BodyY[r]; _s.BodyRot[w] = _s.BodyRot[r];
                _s.BodyVx[w] = _s.BodyVx[r]; _s.BodyVy[w] = _s.BodyVy[r]; _s.BodyW[w] = _s.BodyW[r];
                _s.BodyWPrev[w] = _s.BodyWPrev[r]; _s.BodyAlpha[w] = _s.BodyAlpha[r];
                _s.BodyM[w] = _s.BodyM[r]; _s.BodyI[w] = _s.BodyI[r];
                _s.BodyEulL[w] = _s.BodyEulL[r];
                _s.BodyRho[w] = _s.BodyRho[r]; _s.BodyCpx[w] = _s.BodyCpx[r];
                _s.BodyChi[w] = _s.BodyChi[r]; _s.BodyVCrit[w] = _s.BodyVCrit[r];
                _s.BodyDuct[w] = _s.BodyDuct[r]; _s.BodyCellSize[w] = _s.BodyCellSize[r];
                _s.BodyDirty[w] = _s.BodyDirty[r];

                int off = _s.BodyCellOff[r], len = _s.BodyCellLen[r];
                for (int i = 0; i < len; i++) _s.CellBody[_s.BodyCells[off + i]] = w;
            }
            w++;
        }
        if (!moved) return;
        _s.BodyCount = w;
        BodyBuilder.RebuildMembership(_s);
    }

    /// <summary>Copies a parent's material and frame onto a freshly created fragment.</summary>
    /// <remarks>
    /// Grows <c>_parentOf</c> here rather than trusting the headroom reserved at the top of
    /// <see cref="RebuildBodies"/>. That reservation was a fixed 256 slots taken from the body count
    /// on entry, which is a bet on how much a tick can shatter — and a fine-grain body loses that
    /// bet: a 3,800-cell target at 5.5 px cells went from 332 bodies to several hundred more in one
    /// tick and ran off the end of the array. Every other per-body scratch array in this file grows
    /// on demand; this one was the exception.
    /// </remarks>
    private void InheritBody(int dst, int parent)
    {
        if (_parentOf.Length <= dst)
        {
            var np = new int[(dst + 1) * 2];
            for (int i = 0; i < np.Length; i++) np[i] = -1;
            Array.Copy(_parentOf, np, _parentOf.Length);
            _parentOf = np;
        }
        _s.BodyRot[dst] = _s.BodyRot[parent];
        _s.BodyW[dst] = _s.BodyW[parent];
        _s.BodyWPrev[dst] = _s.BodyW[parent];
        _s.BodyAlpha[dst] = 0f;
        _s.BodyRho[dst] = _oldRho[parent]; _s.BodyCpx[dst] = _oldCpx[parent];
        _s.BodyChi[dst] = _oldChi[parent]; _s.BodyVCrit[dst] = _oldVCrit[parent];
        _s.BodyDuct[dst] = _oldDuct[parent]; _s.BodyCellSize[dst] = _oldCell[parent];
        _s.BodyEulL[dst] = 0f;
        _s.BodyDirty[dst] = false;
        _parentOf[dst] = parent;
    }

    private void MarkTouched(int b)
    {
        if (_touchedStamp.Length <= b)
        {
            int nn = b + 1 < 16 ? 16 : (b + 1) * 2;
            var st = new int[nn];
            Array.Copy(_touchedStamp, st, _touchedStamp.Length);
            _touchedStamp = st;
        }
        if (_touchedStamp[b] == _touchEpoch) return;
        _touchedStamp[b] = _touchEpoch;
        if (_touchedCount >= _touched.Length) Array.Resize(ref _touched, System.Math.Max(16, _touched.Length * 2));
        _touched[_touchedCount++] = b;
    }

    /// <summary>
    /// Recomputes one body's centroid, pose, velocity and inertia from its current cells.
    /// </summary>
    private void RecomputeBody(int g)
    {
        int parent = _parentOf.Length > g && _parentOf[g] >= 0 ? _parentOf[g] : g;
        _parentOf[g] = -1;

        float M = 0f, rx = 0f, ry = 0f;
        int off = _s.BodyCellOff[g], len = _s.BodyCellLen[g];
        for (int i = 0; i < len; i++)
        {
            int c = _s.BodyCells[off + i];
            M += _s.CellM[c];
            rx += _s.CellRx[c] * _s.CellM[c];
            ry += _s.CellRy[c] * _s.CellM[c];
        }
        if (M < 1e-9f)
        {
            // No live cells left. CompactBodies will drop the record; until then keep the mass and
            // inertia at one so that any stray division is finite rather than infinite.
            _s.BodyM[g] = 1f; _s.BodyI[g] = 1f;
            _s.BodyVx[g] = 0f; _s.BodyVy[g] = 0f; _s.BodyW[g] = 0f;
            return;
        }
        rx /= M; ry /= M;

        SimMath.SinCos(_oldRot[parent], out float si, out float co);
        _s.BodyRot[g] = _oldRot[parent];
        _s.BodyX[g] = _oldX[parent] + rx * co - ry * si;
        _s.BodyY[g] = _oldY[parent] + rx * si + ry * co;
        _s.BodyVx[g] = _oldVx[parent] - _oldW[parent] * (rx * si + ry * co);
        _s.BodyVy[g] = _oldVy[parent] + _oldW[parent] * (rx * co - ry * si);
        _s.BodyW[g] = _oldW[parent];
        _s.BodyWPrev[g] = _oldW[parent];
        _s.BodyAlpha[g] = 0f;
        _s.BodyEulL[g] = 0f;

        float I = 0f;
        for (int i = 0; i < len; i++)
        {
            int c = _s.BodyCells[off + i];
            _s.CellRx[c] -= rx; _s.CellRy[c] -= ry;
            I += _s.CellIc[c] + _s.CellM[c] * (_s.CellRx[c] * _s.CellRx[c] + _s.CellRy[c] * _s.CellRy[c]);
        }
        _s.BodyM[g] = M;
        _s.BodyI[g] = SimMath.Max(1f, I);

        // A lone pebble is normalised to zero rest offset. A nonzero offset on a BOND-LESS cell
        // turns the centrifugal load into a perpetual self-accelerator, because nothing opposes it.
        if (len == 1)
        {
            int c = _s.BodyCells[off];
            if (_s.CellBorn[c] == int.MinValue) _s.CellBorn[c] = _s.Tick;
            _s.CellRx[c] = 0f; _s.CellRy[c] = 0f;
            _s.BodyI[g] = SimMath.Max(1f, _s.CellIc[c]);
        }
    }

    /// <summary>Anchors and axis are body-frame, so they are re-derived after any re-centring.</summary>
    private void ReanchorBond(int k)
    {
        if (_s.BondBroken[k]) return;
        int a = _s.BondA[k], b = _s.BondB[k];
        if (_s.Dead(a) || _s.Dead(b)) return;
        float mx = (_s.CellRx[a] + _s.CellRx[b]) * 0.5f;
        float my = (_s.CellRy[a] + _s.CellRy[b]) * 0.5f;
        _s.BondRax[k] = mx - _s.CellRx[a];
        _s.BondRay[k] = my - _s.CellRy[a];
        _s.BondRbx[k] = mx - _s.CellRx[b];
        _s.BondRby[k] = my - _s.CellRy[b];
        float nx = _s.CellRx[b] - _s.CellRx[a], ny = _s.CellRy[b] - _s.CellRy[a];
        float L = SimMath.Hypot(nx, ny);
        if (L == 0f) L = 1f;
        _s.BondNx[k] = nx / L;
        _s.BondNy[k] = ny / L;
        C.SplitBondsReanchored++;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  rebake — plastic deformation becomes structure
    // ══════════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════════
    //  comminution as a classifier
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Comminutes cells that have absorbed their material's crush capacity, handing their momentum
    /// to whatever was pressing on them. Returns true if anything converted.
    /// </summary>
    /// <remarks>
    /// <para><b>Comminution is a transfer, not a deletion.</b> This is the part that matters, and
    /// getting it wrong is silent: an earlier version booked a crushed cell's momentum straight to
    /// the export ledger. That balanced the conservation guardrail perfectly — <c>MomentumDrift</c>
    /// adds the ledger back in — while the material physically vanished mid-collision. Measured on
    /// the 900 px/s projectile, 88.8% of the scene's momentum ended up in the ledger from FOUR
    /// crushed cells, because those four cells were the impactor: it was deleted in flight and the
    /// target was never struck, breaking exactly zero bonds. A momentum-conservation test cannot see
    /// that, because the ledger is what makes it balance.</para>
    ///
    /// <para><b>Who receives it.</b> Every contact the cell had this tick, weighted by the pressure
    /// that contact contributed — the same weights that decided the cell crushes decide where it
    /// goes. Each share is handed over as a perfectly inelastic collision between that share of the
    /// cell's mass and the partner body, which is what keeps the operation dissipative for any mass
    /// ratio. The remainder that a share cannot deliver works out to <c>m*V_partner</c>: precisely
    /// the momentum the partner would have gained by absorbing the cell's MASS, which it cannot,
    /// since <c>BodyM</c> is rebuilt from live cells. So that part legitimately leaves, and for the
    /// case that was broken it is zero — a stationary target means <c>V = 0</c> and the impactor's
    /// momentum transfers in full.</para>
    ///
    /// <para><b>Torque-free</b>, applied at the partner's centroid. Applying it at the contact point
    /// would let a crushed cell spin up whatever crushed it, which is a free-energy loop waiting to
    /// happen. The cell's own spin goes to the ledger.</para>
    ///
    /// <para>Only cells the narrow phase actually paired can reach this at all: pressure is written
    /// exclusively by <c>SolveContact</c>, so a cell with no contact has no dose. Comminution is
    /// therefore a surface phenomenon by construction, not by a rule — interior cells become
    /// eligible only once penetration is deep enough to pair them, which is the crushed zone.</para>
    /// </remarks>
    private bool ConvertDust()
    {
        if (!_tune.Dust) return false;
        C.DustScans++;
        bool did = false;
        EnsureCrushScratch();

        // ── COMMINUTION IS THE LIMIT OF CARVING ───────────────────────────────
        // Nothing is selected here on a dose or a timer any more. A cell is carved back continuously
        // by the contacts pressing on it, shedding mass to whatever is doing the pressing, and it
        // comminutes when it has lost its material's share of its original area. So the graceful low
        // end and the terminal case are one mechanism rather than two: a shallow contact shaves a
        // sliver, a sustained one runs the cell out.
        //
        // The momentum has already gone, continuously and locally, as the mass left. What remains
        // here is only what has to happen at a topology boundary: severing bonds and removing the
        // cell.
        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.Dead(c) || _s.Solo(c)) continue;
            float a0 = _s.CellArea0[c];
            if (a0 <= 0f) continue;
            float shed = 1f - _s.CellArea[c] / a0;
            if (shed < _s.Mat(c).ShedLimit) continue;
            if (_crushMark[c]) continue;
            _crushMark[c] = true;
            _crushList[_crushCount++] = c;
        }

        for (int i = 0; i < _crushCount; i++)
        {
            int c = _crushList[i];
            _crushMark[c] = false;
            if (_s.Dead(c)) continue;
            C.DustSinglesSeen++;
            int bi = _s.CellBody[c];
            if (bi < 0 || bi >= _s.BodyCount) continue;
            CellVelocity(c, bi, out float vx, out float vy);

            // Powder is not attached to anything: sever the cell before removing it, so the body
            // re-partitions around the hole rather than keeping bonds to material that is gone.
            int aoff = _s.AdjOff[c], alen = _s.AdjLen[c];
            for (int j = 0; j < alen; j++)
            {
                int k = _s.AdjBond[aoff + j];
                if (_s.BondBroken[k]) continue;
                _s.BondBroken[k] = true;
                _s.BondSn[k] = 0f; _s.BondSt[k] = 0f; _s.BondSa[k] = 0f;
                // The cell is about to vanish, so the neighbour is left facing empty space.
                OpenBondSides(k);
                ExposeSide(_s.BondA[k] == c ? _s.BondB[k] : _s.BondA[k], k);
                int other = _s.BondA[k] == c ? _s.BondB[k] : _s.BondA[k];
                if (other >= 0 && other < _s.CellCount) _s.SetFlag(other, CellFlag.Cracked, true);
            }

            // ── THE POWDER COMPACTS INTO THE CRATER ──────────────────────────
            // The cell's momentum has two shares. The rigid share — what it carried because it was
            // part of the body — leaves with its mass: mass leaving at the body's own velocity
            // changes nothing about the body, and keeping that momentum while dropping the mass
            // would speed the body up, creating energy of order m/M (serious for a small fragment).
            // The deviation share is what the impact put into it, and that is what the powder
            // presses into the crater floor: it goes to the cell's live neighbours, mass-weighted,
            // in the cell layer where DecomposeMotion already knows how to promote it.
            SimMath.SinCos(_s.BodyRot[bi], out float csn, out float ccs);
            float dvx = _s.CellDvx[c], dvy = _s.CellDvy[c];                  // deviation, body-local
            float rvx = vx - (dvx * ccs - dvy * csn), rvy = vy - (dvx * csn + dvy * ccs);   // rigid share, world
            float mc = _s.CellM[c];
            float mN = 0f;
            for (int j = 0; j < alen; j++)
            {
                int k = _s.AdjBond[aoff + j];
                int o = _s.BondA[k] == c ? _s.BondB[k] : _s.BondA[k];
                if (o >= 0 && o < _s.CellCount && !_s.Dead(o) && _s.CellBody[o] == bi) mN += _s.CellM[o];
            }
            if (mN > 0f)
            {
                DustToNeighbours++; DustDevMom += mc * SimMath.Hypot(dvx, dvy); DustRigidMom += mc * SimMath.Hypot(rvx, rvy);
                float fx = mc * dvx / mN, fy = mc * dvy / mN;               // per unit neighbour mass
                for (int j = 0; j < alen; j++)
                {
                    int k = _s.AdjBond[aoff + j];
                    int o = _s.BondA[k] == c ? _s.BondB[k] : _s.BondA[k];
                    if (o < 0 || o >= _s.CellCount || _s.Dead(o) || _s.CellBody[o] != bi) continue;
                    _s.CellDvx[o] += fx; _s.CellDvy[o] += fy;
                }
                ExportedPx += mc * rvx; ExportedPy += mc * rvy;
            }
            else
            {
                // ── NO SAME-BODY NEIGHBOUR: PRESS INTO WHAT IT IS TOUCHING ────
                // A cell that comminutes inside a fragment — glass cracks first and crushes
                // second, so this is most of glass — has its old neighbours in other bodies,
                // geometrically right there and interpenetrating. Routing only to the same body
                // exported 414 of 504 glass comminutions whole: half the body's momentum left the
                // simulation and the back rows never felt the impact. So the powder presses into
                // its CONTACT partners, whatever body they are in. Into a different body the whole
                // momentum can go — that is an inelastic transfer into another mass, not momentum
                // kept while its own mass is dropped, so no energy is created.
                float mP = 0f, pvx = 0f, pvy = 0f;          // partners' mass and momentum
                for (int ci = 0; ci < _contactCount; ci++)
                {
                    ref Contact ct = ref _contacts[ci];
                    int o = ct.A == c ? ct.B : ct.B == c ? ct.A : -1;
                    if (o < 0 || o >= _s.CellCount || _s.Dead(o)) continue;
                    int bo = _s.CellBody[o];
                    if (bo < 0 || bo >= _s.BodyCount) continue;
                    CellVelocity(o, bo, out float ovx, out float ovy);
                    mP += _s.CellM[o]; pvx += _s.CellM[o] * ovx; pvy += _s.CellM[o] * ovy;
                }
                if (mP > 0f)
                {
                    // Inelastic, by the same rule ShedMass uses: a reduced-mass impulse toward the
                    // partners' mean velocity. Giving them the WHOLE momentum instead created energy
                    // when they were lighter than the dying cell — 116.6% on the glass projectile.
                    // What the impulse does not carry leaves with the mass, at the speed the powder
                    // ends up moving: co-moving with what it was pressed into.
                    // Perfectly inelastic: the dying cell and everything it presses on end at ONE
                    // velocity, and each partner is moved to it — m_o·(V − v_o). The total impulse is
                    // the same reduced-mass J as before; the distribution is not. Splitting J by
                    // partner mass pushed a partner that was already moving with the cell FORWARD
                    // (the row behind, in a collide) instead of decelerating the one moving against
                    // it, which is where the shock has to go.
                    float V_x = (mc * vx + pvx) / (mc + mP), V_y = (mc * vy + pvy) / (mc + mP);
                    float jx = mc * (vx - V_x), jy = mc * (vy - V_y);
                    DustToContacts++; DustContactMom += SimMath.Hypot(jx, jy);
                    for (int ci = 0; ci < _contactCount; ci++)
                    {
                        ref Contact ct = ref _contacts[ci];
                        int o = ct.A == c ? ct.B : ct.B == c ? ct.A : -1;
                        if (o < 0 || o >= _s.CellCount || _s.Dead(o)) continue;
                        int bo = _s.CellBody[o];
                        if (bo < 0 || bo >= _s.BodyCount) continue;
                        CellVelocity(o, bo, out float ovx, out float ovy);
                        SimMath.SinCos(_s.BodyRot[bo], out float so, out float coo);
                        float ax = V_x - ovx, ay = V_y - ovy;               // Σ m_o·(V − v_o) = J
                        _s.CellDvx[o] += ax * coo + ay * so;
                        _s.CellDvy[o] += -ax * so + ay * coo;
                    }
                    ExportedPx += mc * vx - jx; ExportedPy += mc * vy - jy;
                }
                else
                {
                    // Pressing on nothing: free spall, and the ledger is the honest place for it.
                    DustNoNeighbour++; DustLostMom += mc * SimMath.Hypot(vx, vy);
                    ExportedPx += mc * vx; ExportedPy += mc * vy;
                }
            }
            // Translational energy at the cell's own velocity, plus the spin it carries away with
            // it. Its share of the body's rotational energy is already in the w x r above. What the
            // neighbours gained is second order in the share they received and is left as
            // dissipation.
            float cw = _s.BodyW[bi] + _s.CellDw[c];
            ExportedKe += 0.5f * _s.CellM[c] * (vx * vx + vy * vy)
                          + 0.5f * _s.CellIc[c] * cw * cw;
            Dust++; Crushed++; C.DustConverted++; DustMass += _s.CellM[c];
            _s.SetFlag(c, CellFlag.Dead, true);
            MarkDirty(bi);
            did = true;
        }
        _crushCount = 0;

        // ── free debris ───────────────────────────────────────────────────────
        // Off by default now. A fragment vanishing because nothing has touched it for a few ticks
        // was a performance measure wearing physics clothing: rubble is real material and belongs in
        // the world. The price is that body count grows, which is what makes rubble tiering and
        // sleeping load-bearing rather than optional.
        if (!_tune.ExportFreeDebris) return did;

        for (int bi = 0; bi < _s.BodyCount; bi++)
        {
            if (_s.BodyCellLen[bi] != 1) continue;
            int c = _s.BodyCells[_s.BodyCellOff[bi]];
            if (_s.Dead(c) || _s.Solo(c)) continue;

            SimMath.SinCos(_s.BodyRot[bi], out float si, out float co);
            float vx = _s.BodyVx[bi] + _s.CellDvx[c] * co - _s.CellDvy[c] * si;
            float vy = _s.BodyVy[bi] + _s.CellDvx[c] * si + _s.CellDvy[c] * co;

            if (_s.CellTouch[c] != int.MinValue && _s.Tick - _s.CellTouch[c] < DustFreeTicks) continue;
            if (_s.CellBorn[c] == int.MinValue || _s.Tick - _s.CellBorn[c] < DustFreeTicks) continue;

            ExportedPx += _s.CellM[c] * vx;
            ExportedPy += _s.CellM[c] * vy;
            ExportedKe += 0.5f * _s.CellM[c] * (vx * vx + vy * vy)
                          + 0.5f * _s.BodyI[bi] * _s.BodyW[bi] * _s.BodyW[bi];
            Dust++; C.DustConverted++; DustMass += _s.CellM[c];
            _s.SetFlag(c, CellFlag.Dead, true);
            MarkDirty(bi);
            did = true;
        }
        return did;
    }

    // Comminution transfer scratch, indexed by cell and valid only within one ConvertDust call.
    private bool[] _crushMark = Array.Empty<bool>();
    private bool[] _crushNew = Array.Empty<bool>();
    private int _crushCount;
    private int[] _crushList = Array.Empty<int>();
    private float[] _crushVx = Array.Empty<float>();
    private float[] _crushVy = Array.Empty<float>();
    private float[] _crushW = Array.Empty<float>();
    private float[] _crushTot = Array.Empty<float>();
    private float[] _crushJx = Array.Empty<float>();
    private float[] _crushJy = Array.Empty<float>();
    private float[] _crushGain = Array.Empty<float>();

    private void EnsureCrushScratch()
    {
        if (_crushMark.Length >= _s.CellCount) return;
        int n = System.Math.Max(1, _s.CellCount);
        _crushMark = new bool[n];
        _crushNew = new bool[n];
        _crushList = new int[n];
        _crushVx = new float[n]; _crushVy = new float[n]; _crushW = new float[n];
        _crushTot = new float[n];
        _crushJx = new float[n]; _crushJy = new float[n]; _crushGain = new float[n];
    }

    /// <summary>
    /// Hands one share of a comminuting cell's momentum to one partner, as a perfectly inelastic
    /// collision at the partner body's centroid.
    /// </summary>
    /// <remarks>
    /// <para>The share is <c>weight / total</c> of the cell's mass, where the weights are the
    /// pressures the contacts contributed. Treating each share as its own inelastic collision — and
    /// so using the reduced mass of the SHARE against the partner, not of the whole cell — is what
    /// keeps every individual transfer dissipative, which makes the sum dissipative too.</para>
    ///
    /// <para>The accounting is exact rather than approximate. The partner gains <c>j</c> and the
    /// energy that goes with it; the cell's books record <c>j</c> as delivered and its own kinetic
    /// energy less that gain as exported. Summing the two gives back precisely what the cell had, so
    /// neither momentum nor energy is created or lost by the transfer itself — only by the material
    /// leaving, which is what the ledger is for.</para>
    /// </remarks>
    private void TransferShare(int cell, int partner, float weight)
    {
        float total = _crushTot[cell];
        if (total <= 0f || weight <= 0f) return;
        int pb = _s.CellBody[partner];
        if (pb < 0 || pb >= _s.BodyCount) return;
        float mp = _s.BodyM[pb], mc = _s.CellM[partner];
        if (mp <= 0f || mc <= 0f) return;

        float ms = _s.CellM[cell] * (weight / total);
        if (ms <= 0f) return;

        // ── LOCAL, LIKE EVERY OTHER IMPULSE IN THIS MODEL ─────────────────────
        // The recipient is the partner CELL's deviation field, not its body's centroid, and the
        // resisting mass is the partner CELL's. Handing it to the body was body-level response by
        // another name: it de-localises the momentum with no physical basis, delivers it
        // instantaneously to material arbitrarily far from the contact, and contradicts the premise
        // the whole model is built on — that an impact travels through the material at wave speed
        // and the participating mass GROWS as it does. Writing into dv lets exactly that happen:
        // the bonds carry it outward and DecomposeMotion promotes the rigid part when there is one.
        //
        // So the reduced mass is cell-against-cell. That transfers less per share than a body-mass
        // reduced mass would, and correctly so: the body behind the partner participates through
        // the bond network over the following substeps, not in this one.
        float mu = ms * mc / (ms + mc);
        BodyTrig(pb, out float sp, out float cp);
        float rx = _s.CellRx[partner] * cp - _s.CellRy[partner] * sp;
        float ry = _s.CellRx[partner] * sp + _s.CellRy[partner] * cp;
        float pw = _s.BodyW[pb];
        float pvx = _s.BodyVx[pb] - pw * ry + _s.CellDvx[partner] * cp - _s.CellDvy[partner] * sp;
        float pvy = _s.BodyVy[pb] + pw * rx + _s.CellDvx[partner] * sp + _s.CellDvy[partner] * cp;

        float jx = mu * (_crushVx[cell] - pvx);
        float jy = mu * (_crushVy[cell] - pvy);

        // Energy is booked against the BODY mass even though the momentum enters one cell, because
        // that is where it ends up: DecomposeMotion promotes the field's mean, so the eventual
        // counted gain is j.V + j^2/(2M) for the body. BodyKineticEnergy does not count the
        // deviation field, so crediting the cell here would credit energy the ledger cannot see.
        float gain = jx * _s.BodyVx[pb] + jy * _s.BodyVy[pb] + (jx * jx + jy * jy) / (2f * mp);

        // World -> body-local at the boundary, exactly as ApplyPair does.
        float ax = jx / mc, ay = jy / mc;
        _s.CellDvx[partner] += ax * cp + ay * sp;
        _s.CellDvy[partner] += -ax * sp + ay * cp;

        _crushJx[cell] += jx;
        _crushJy[cell] += jy;
        _crushGain[cell] += gain;
        C.DustTransfers++;
    }

}
