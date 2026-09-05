using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{
    // ══════════════════════════════════════════════════════════════════════════
    //  splitting
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-partitions cells into bodies by connected components over the surviving bonds.
    /// </summary>
    /// <remarks>
    /// <para>Component labelling walks cells in index order with an explicit stack, and adjacency
    /// was built by scanning bonds in index order. Both are part of the determinism contract:
    /// component numbering decides body indices, which decides the order of every per-body loop
    /// afterwards.</para>
    /// <para>A fragment inherits the parent's rigid motion at its OWN centroid, which conserves
    /// linear momentum exactly, and its angular momentum follows from the parallel-axis theorem
    /// provided the inertia is recomputed from the same offsets. Re-centring uses the DEFORMED
    /// configuration so the collider stays centred on the pose.</para>
    /// </remarks>
    private void RebuildBodies()
    {
        int n = _s.CellCount;
        if (_comp.Length < n) { _comp = new int[n]; _stack = new int[n]; }
        for (int i = 0; i < n; i++) _comp[i] = -1;

        int comps = 0;
        for (int c = 0; c < n; c++)
        {
            if (_s.CellDead[c] || _comp[c] >= 0) continue;
            int sp = 0;
            _stack[sp++] = c;
            _comp[c] = comps;
            while (sp > 0)
            {
                int u = _stack[--sp];
                int off = _s.AdjOff[u], len = _s.AdjLen[u];
                for (int i = 0; i < len; i++)
                {
                    int k = _s.AdjBond[off + i];
                    if (_s.BondBroken[k]) continue;
                    int v = _s.BondA[k] == u ? _s.BondB[k] : _s.BondA[k];
                    if (_s.CellDead[v] || _comp[v] >= 0) continue;
                    _comp[v] = comps;
                    _stack[sp++] = v;
                }
            }
            comps++;
        }

        int live = _s.LiveCellCount();
        if (comps == _s.BodyCount && comps == _lastComp && live == _lastLive) return;
        _lastComp = comps;
        _lastLive = live;

        // snapshot the parents, then rebuild
        int oldCount = _s.BodyCount;
        var oldX = new float[oldCount]; var oldY = new float[oldCount]; var oldRot = new float[oldCount];
        var oldVx = new float[oldCount]; var oldVy = new float[oldCount]; var oldW = new float[oldCount];
        var oldRho = new float[oldCount]; var oldCpx = new float[oldCount]; var oldChi = new float[oldCount];
        var oldVCrit = new float[oldCount]; var oldDuct = new float[oldCount];
        var oldCell = new float[oldCount];
        for (int b = 0; b < oldCount; b++)
        {
            oldX[b] = _s.BodyX[b]; oldY[b] = _s.BodyY[b]; oldRot[b] = _s.BodyRot[b];
            oldVx[b] = _s.BodyVx[b]; oldVy[b] = _s.BodyVy[b]; oldW[b] = _s.BodyW[b];
            oldRho[b] = _s.BodyRho[b]; oldCpx[b] = _s.BodyCpx[b]; oldChi[b] = _s.BodyChi[b];
            oldVCrit[b] = _s.BodyVCrit[b]; oldDuct[b] = _s.BodyDuct[b]; oldCell[b] = _s.BodyCellSize[b];
        }

        _s.EnsureBodies(comps > 1 ? comps : 1);
        var src = new int[comps];
        for (int g = 0; g < comps; g++) src[g] = -1;

        for (int c = 0; c < n; c++)
        {
            if (_s.CellDead[c]) continue;
            int g = _comp[c];
            if (src[g] < 0) src[g] = _s.CellBody[c];
            _s.CellBody[c] = g;
        }
        _s.BodyCount = comps;
        BodyBuilder.RebuildMembership(_s);

        for (int g = 0; g < comps; g++)
        {
            int o = src[g];
            if (o < 0 || o >= oldCount) continue;

            float M = 0f, rx = 0f, ry = 0f;
            int off = _s.BodyCellOff[g], len = _s.BodyCellLen[g];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                M += _s.CellM[c];
                rx += (_s.CellRx[c] + _s.CellUx[c]) * _s.CellM[c];
                ry += (_s.CellRy[c] + _s.CellUy[c]) * _s.CellM[c];
            }
            if (M < 1e-9f) continue;
            rx /= M; ry /= M;

            SimMath.SinCos(oldRot[o], out float si, out float co);
            _s.BodyRot[g] = oldRot[o];
            _s.BodyX[g] = oldX[o] + rx * co - ry * si;
            _s.BodyY[g] = oldY[o] + rx * si + ry * co;
            _s.BodyVx[g] = oldVx[o] - oldW[o] * (rx * si + ry * co);
            _s.BodyVy[g] = oldVy[o] + oldW[o] * (rx * co - ry * si);
            _s.BodyW[g] = oldW[o];
            _s.BodyWPrev[g] = oldW[o];
            _s.BodyAlpha[g] = 0f;
            _s.BodyRho[g] = oldRho[o]; _s.BodyCpx[g] = oldCpx[o]; _s.BodyChi[g] = oldChi[o];
            _s.BodyVCrit[g] = oldVCrit[o]; _s.BodyDuct[g] = oldDuct[o];
            _s.BodyCellSize[g] = oldCell[o];
            _s.BodyPlast[g] = 0f; _s.BodyEulL[g] = 0f;

            float I = 0f;
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                _s.CellRx[c] -= rx;
                _s.CellRy[c] -= ry;
                I += _s.CellIc[c] + _s.CellM[c] * (_s.CellRx[c] * _s.CellRx[c] + _s.CellRy[c] * _s.CellRy[c]);
            }
            _s.BodyM[g] = M;
            _s.BodyI[g] = SimMath.Max(1f, I);

            // A lone pebble is normalised to zero rest offset. A nonzero offset on a BOND-LESS cell
            // turns the centrifugal load into a perpetual self-accelerator, because nothing opposes
            // it; the recentring above would otherwise leave rx = -ux.
            if (len == 1)
            {
                int c = _s.BodyCells[off];
                if (_s.CellBorn[c] == int.MinValue) _s.CellBorn[c] = _s.Tick;
                _s.CellRx[c] = 0f; _s.CellRy[c] = 0f;
                _s.CellUx[c] = 0f; _s.CellUy[c] = 0f;
                _s.CellPhi[c] += _s.CellUth[c];
                _s.CellUth[c] = 0f;
                _s.CellRotStamp[c] = -1;
                _s.BodyI[g] = SimMath.Max(1f, _s.CellIc[c]);
            }
        }

        // anchors and axes are body-frame, so re-derive them after re-centring
        for (int k = 0; k < _s.BondCount; k++)
        {
            if (_s.BondBroken[k]) continue;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.CellDead[a] || _s.CellDead[b]) continue;
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
        }

        _s.Reindex();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  rebake — plastic deformation becomes structure
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Folds a body's accumulated plastic set into its rest configuration, so a bend survives
    /// elastic recovery and becomes part of the collider, the rendering, the inertia and the bond
    /// network's own equilibrium.
    /// </summary>
    /// <remarks>
    /// <para>Only the PLASTIC part may become geometry, and getting that wrong is instructive both
    /// ways: folding all of <c>u</c> leaves the bonds still loaded against a shape that already
    /// moved, a ratchet that does work on every rebake; unloading the stretches to compensate makes
    /// deformation entirely permanent, i.e. a fluid, which flows without limit. The bond plastic
    /// offsets ARE the permanent set, and the stretches were already unloaded by exactly that
    /// amount when it flowed — so the only missing step is geometric: find the per-cell field whose
    /// relative displacements match the offsets, then move it from <c>u</c> into the rest shape.
    /// <c>rest + u</c> is unchanged, so geometry is continuous and no force changes.</para>
    /// <para>Angular momentum, not omega, is conserved through the shape change.</para>
    /// </remarks>
    private void Rebake(int b)
    {
        int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
        if (len == 0) return;
        int n = _s.CellCount;
        if (_wx.Length < n) { _wx = new float[n]; _wy = new float[n]; _wt = new float[n]; }

        for (int i = 0; i < len; i++)
        {
            int c = _s.BodyCells[off + i];
            _wx[c] = 0f; _wy[c] = 0f; _wt[c] = 0f;
        }

        int boff = _s.BodyBondOff[b], blen = _s.BodyBondLen[b];
        for (int it = 0; it < 8; it++)
        {
            for (int i = 0; i < blen; i++)
            {
                int k = _s.BodyBonds[boff + i];
                if (_s.BondBroken[k]) continue;
                int a = _s.BondA[k], bb = _s.BondB[k];
                if (_s.CellDead[a] || _s.CellDead[bb]) continue;

                float nx = _s.BondNx[k], ny = _s.BondNy[k];
                float tgx = _s.BondPn[k] * nx - _s.BondPt[k] * ny;
                float tgy = _s.BondPn[k] * ny + _s.BondPt[k] * nx;
                float rx = (_wx[bb] - _wx[a]) - tgx;
                float ry = (_wy[bb] - _wy[a]) - tgy;
                float ra = (_wt[bb] - _wt[a]) - _s.BondPa[k];

                _wx[a] += rx * 0.25f; _wy[a] += ry * 0.25f; _wt[a] += ra * 0.25f;
                _wx[bb] -= rx * 0.25f; _wy[bb] -= ry * 0.25f; _wt[bb] -= ra * 0.25f;
            }
        }

        float M = 0f, mx = 0f, my = 0f;
        for (int i = 0; i < len; i++)
        {
            int c = _s.BodyCells[off + i];

            // A cell cannot make permanent more displacement than actually occurred. Without this
            // the relaxation — which need not converge on an incompatible plastic field — could
            // drive u past its cap, and clamping u would then break rest+u invariance and teleport
            // the collider.
            float wm = SimMath.Hypot(_wx[c], _wy[c]);
            float um = SimMath.Hypot(_s.CellUx[c], _s.CellUy[c]);
            if (wm > um && wm > 1e-9f) { float sc = um / wm; _wx[c] *= sc; _wy[c] *= sc; }
            if (SimMath.Abs(_wt[c]) > SimMath.Abs(_s.CellUth[c])) _wt[c] = _s.CellUth[c];

            _s.CellRx[c] += _wx[c]; _s.CellRy[c] += _wy[c]; _s.CellPhi[c] += _wt[c];
            _s.CellUx[c] -= _wx[c]; _s.CellUy[c] -= _wy[c]; _s.CellUth[c] -= _wt[c];
            _s.CellRotStamp[c] = -1;                     // phi changed outside the substep clock

            M += _s.CellM[c];
            mx += _s.CellRx[c] * _s.CellM[c];
            my += _s.CellRy[c] * _s.CellM[c];
        }
        if (M < 1e-9f) return;
        mx /= M; my /= M;

        SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
        _s.BodyX[b] += mx * co - my * si;
        _s.BodyY[b] += mx * si + my * co;
        // No velocity change: DecomposeMotion keeps the field zero-mean, so the body's
        // centre-of-mass velocity IS BodyV; re-expressing the origin must not touch it.

        float I = 0f;
        for (int i = 0; i < len; i++)
        {
            int c = _s.BodyCells[off + i];
            _s.CellRx[c] -= mx; _s.CellRy[c] -= my;
            I += _s.CellIc[c] + _s.CellM[c] * (_s.CellRx[c] * _s.CellRx[c] + _s.CellRy[c] * _s.CellRy[c]);
        }
        I = SimMath.Max(1f, I);
        _s.BodyW[b] *= _s.BodyI[b] / I;                  // shape changed: conserve L, not omega
        _s.BodyI[b] = I;

        for (int i = 0; i < blen; i++)
        {
            int k = _s.BodyBonds[boff + i];
            if (_s.BondBroken[k]) continue;
            int a = _s.BondA[k], bb = _s.BondB[k];
            if (_s.CellDead[a] || _s.CellDead[bb]) continue;
            float ax = (_s.CellRx[a] + _s.CellRx[bb]) * 0.5f;
            float ay = (_s.CellRy[a] + _s.CellRy[bb]) * 0.5f;
            _s.BondRax[k] = ax - _s.CellRx[a];
            _s.BondRay[k] = ay - _s.CellRy[a];
            _s.BondRbx[k] = ax - _s.CellRx[bb];
            _s.BondRby[k] = ay - _s.CellRy[bb];
            float nx = _s.CellRx[bb] - _s.CellRx[a], ny = _s.CellRy[bb] - _s.CellRy[a];
            float L = SimMath.Hypot(nx, ny);
            if (L == 0f) L = 1f;
            _s.BondNx[k] = nx / L; _s.BondNy[k] = ny / L;

            // Stretches are NOT touched: plastic flow already removed the offsets from them when it
            // flowed, so what remains is the live elastic state and stays valid about the new rest.
            _s.BondPn[k] = 0f; _s.BondPt[k] = 0f; _s.BondPa[k] = 0f;
        }

        _s.BodyPlast[b] = 0f;
        Rebakes++;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  comminution as a classifier
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts fracture-created single-cell bodies to debris. Returns true if anything converted.
    /// </summary>
    /// <remarks>
    /// <para>This runs on fracture OUTPUT only, so it can never touch a bonded, load-carrying cell
    /// and therefore cannot short-circuit a stress wave — which is the structural reason
    /// comminution is a classifier here rather than a physics channel.</para>
    /// <para>Two paths. <b>Crushed:</b> sustained deep overlap means the rubble has nowhere to go,
    /// so it is consumed and its momentum handed to whatever presses on it as a perfectly
    /// inelastic, torque-free transfer at the partner's centroid — provably dissipative for any
    /// masses. <b>Free:</b> debris that has touched nothing for a few ticks has nothing to hand its
    /// momentum to, so it exports to the ledger. Gating on contact rather than on a timer is what
    /// stops debris being removed mid-impact while it is still doing mechanical work.</para>
    /// </remarks>
    private bool ConvertDust()
    {
        if (!_tune.Dust) return false;
        bool did = false;

        // deep-overlap census from the last substep's contacts
        for (int c = 0; c < _s.CellCount; c++) if (!_s.CellDead[c]) _s.CellDeepN[c] = _s.CellDeepN[c];

        Span<int> deepPartner = stackalloc int[0];
        _ = deepPartner;

        // walk contacts, recording the deepest partner for each single-cell body
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            int a = ct.A, b = ct.B;
            if (_s.CellDead[a] || _s.CellDead[b]) continue;
            RecordDeep(a, b, ct.Depth);
            RecordDeep(b, a, ct.Depth);
        }

        for (int bi = 0; bi < _s.BodyCount; bi++)
        {
            if (_s.BodyCellLen[bi] != 1) continue;
            int c = _s.BodyCells[_s.BodyCellOff[bi]];
            if (_s.CellDead[c] || _s.CellSolo[c]) continue;

            SimMath.SinCos(_s.BodyRot[bi], out float si, out float co);
            float vx = _s.BodyVx[bi] + _s.CellDvx[c] * co - _s.CellDvy[c] * si;
            float vy = _s.BodyVy[bi] + _s.CellDvx[c] * si + _s.CellDvy[c] * co;

            float deepThr = 0.22f * _s.BodyCellSize[bi];
            bool deep = _deepDepth[c] > deepThr && _deepOther[c] >= 0 && !_s.CellDead[_deepOther[c]];
            _s.CellDeepN[c] = deep ? _s.CellDeepN[c] + 1 : 0;

            if (_s.CellDeepN[c] >= DustDeepTicks && deep)
            {
                int pb = _s.CellBody[_deepOther[c]];
                if (pb >= 0 && pb < _s.BodyCount)
                {
                    float mu = _s.CellM[c] * _s.BodyM[pb] / (_s.CellM[c] + _s.BodyM[pb]);
                    float jx = mu * (vx - _s.BodyVx[pb]);
                    float jy = mu * (vy - _s.BodyVy[pb]);
                    float gain = jx * _s.BodyVx[pb] + jy * _s.BodyVy[pb]
                                 + (jx * jx + jy * jy) / (2f * _s.BodyM[pb]);
                    _s.BodyVx[pb] += jx / _s.BodyM[pb];
                    _s.BodyVy[pb] += jy / _s.BodyM[pb];

                    ExportedPx += _s.CellM[c] * vx - jx;
                    ExportedPy += _s.CellM[c] * vy - jy;
                    ExportedKe += 0.5f * _s.CellM[c] * (vx * vx + vy * vy)
                                  + 0.5f * _s.BodyI[bi] * _s.BodyW[bi] * _s.BodyW[bi] - gain;
                    Dust++; Crushed++; DustMass += _s.CellM[c];
                    _s.CellDead[c] = true;
                    did = true;
                    continue;
                }
            }

            if (_s.CellTouch[c] != int.MinValue && _s.Tick - _s.CellTouch[c] < DustFreeTicks) continue;
            if (_s.CellBorn[c] == int.MinValue || _s.Tick - _s.CellBorn[c] < DustFreeTicks) continue;

            ExportedPx += _s.CellM[c] * vx;
            ExportedPy += _s.CellM[c] * vy;
            ExportedKe += 0.5f * _s.CellM[c] * (vx * vx + vy * vy)
                          + 0.5f * _s.BodyI[bi] * _s.BodyW[bi] * _s.BodyW[bi];
            Dust++; DustMass += _s.CellM[c];
            _s.CellDead[c] = true;
            did = true;
        }
        return did;
    }

    private float[] _deepDepth = Array.Empty<float>();
    private int[] _deepOther = Array.Empty<int>();

    private void ResetDeep()
    {
        if (_deepDepth.Length < _s.CellCount)
        {
            _deepDepth = new float[_s.CellCount];
            _deepOther = new int[_s.CellCount];
        }
        for (int i = 0; i < _s.CellCount; i++) { _deepDepth[i] = 0f; _deepOther[i] = -1; }
    }

    private void RecordDeep(int c, int other, float depth)
    {
        int b = _s.CellBody[c];
        if (b < 0 || b >= _s.BodyCount) return;
        if (_s.BodyCellLen[b] != 1 || _s.CellSolo[c]) return;
        if (depth > _deepDepth[c]) { _deepDepth[c] = depth; _deepOther[c] = other; }
    }
}
