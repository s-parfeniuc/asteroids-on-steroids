using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{
    /// <summary>
    /// Advances the simulation by one fixed tick.
    /// </summary>
    /// <remarks>
    /// The order below is the specification. Both the bond solve and the contact solve are
    /// Gauss-Seidel, so every reordering is a behaviour change; the substep loop, the position of
    /// <see cref="DecomposeMotion"/> before damping, and the placement of the topology work at the
    /// end of the tick are all load-bearing.
    /// </remarks>
    public void Step()
    {
        int sub = _tune.Substeps > 1 ? _tune.Substeps : 1;
        float h = Dt / sub;
        MaxOverlap = 0f;
        _s.Tick++;

        BuildPairs();

        for (int s = 0; s < sub; s++)
        {
            _s.Substep++;
            BuildContacts();
            ApplyInertialLoads(h);                 // before the solve: these are loads
            BondForces(h);
            for (int i = 0; i < _contactCount; i++) SolveContact(ref _contacts[i], h);
            BondIntegrate(h);
            DecomposeMotion();                     // before damping, so damping can't eat momentum

            // Undo the promotion of the Euler load's fictitious torque. The bond stretch it
            // produced has already been integrated, so the internal loading survives; only the
            // self-driving part of the spin is removed.
            for (int b = 0; b < _s.BodyCount; b++)
            {
                if (_s.BodyEulL[b] == 0f) continue;
                _s.BodyW[b] -= _s.BodyEulL[b] / _s.BodyI[b];
                _s.BodyEulL[b] = 0f;
            }

            float rf = SimMath.Max(0f, 1f - _tune.Relax * h);
            for (int b = 0; b < _s.BodyCount; b++)
            {
                _s.BodyCpxAcc[b] = 0f; _s.BodyCpyAcc[b] = 0f; _s.BodyClAcc[b] = 0f;
            }

            for (int c = 0; c < _s.CellCount; c++)
            {
                if (_s.CellDead[c]) continue;
                int b = _s.CellBody[c];

                // Realize the deformation. DecomposeMotion has just left the field zero-mean, so u
                // carries no rigid part and the collider stays centred on the pose.
                _s.CellUx[c] += _s.CellDvx[c] * h;
                _s.CellUy[c] += _s.CellDvy[c] * h;
                _s.CellUth[c] += _s.CellDw[c] * h;

                // Legitimate elastic displacement is about 1% of a cell, so this allowance is not a
                // physical limit but a guard on artifacts.
                float ucap = (b >= 0 && b < _s.BodyCount ? _s.BodyCellSize[b] : 30f) * 0.15f;

                // THE CAP IS A CONSTRAINT, NOT A CLAMP. Scaling u back while leaving dv alone lets
                // the field keep accelerating into a wall forever — free kinetic energy. At the
                // limit the outward velocity must go too, which is what a material that has run out
                // of deformation does: it stops, and breaks instead. The removed velocity is HANDED
                // TO THE BODY, not deleted, making the limit an internal inelastic collision:
                // momentum exact, energy strictly down.
                float um = SimMath.Hypot(_s.CellUx[c], _s.CellUy[c]);
                if (um > ucap)
                {
                    float nx = _s.CellUx[c] / um, ny = _s.CellUy[c] / um;
                    _s.CellUx[c] = nx * ucap;
                    _s.CellUy[c] = ny * ucap;
                    float vn = _s.CellDvx[c] * nx + _s.CellDvy[c] * ny;
                    if (vn > 0f)
                    {
                        float dx = vn * nx, dy = vn * ny;
                        _s.CellDvx[c] -= dx;
                        _s.CellDvy[c] -= dy;
                        if (b >= 0 && b < _s.BodyCount)
                        {
                            _s.BodyCpxAcc[b] += _s.CellM[c] * dx;
                            _s.BodyCpyAcc[b] += _s.CellM[c] * dy;
                            _s.BodyClAcc[b] += _s.CellM[c] * (_s.CellRx[c] * dy - _s.CellRy[c] * dx);
                        }
                    }
                }

                // With skinning, inter-cell rotation shears the polygons instead of opening a gap,
                // so this needs nothing like the range it once had. Same constraint rule.
                if (_s.CellUth[c] > 0.1f || _s.CellUth[c] < -0.1f)
                {
                    float lim = _s.CellUth[c] > 0f ? 0.1f : -0.1f;
                    if (_s.CellDw[c] * lim > 0f)
                    {
                        if (b >= 0 && b < _s.BodyCount)
                            _s.BodyClAcc[b] += _s.CellIc[c] * _s.CellDw[c];
                        _s.CellDw[c] = 0f;
                    }
                    _s.CellUth[c] = lim;
                }

                _s.CellDvx[c] *= rf;
                _s.CellDvy[c] *= rf;
                _s.CellDw[c] *= rf;
            }

            for (int b = 0; b < _s.BodyCount; b++)
            {
                if (_s.BodyCpxAcc[b] == 0f && _s.BodyCpyAcc[b] == 0f && _s.BodyClAcc[b] == 0f) continue;
                SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
                _s.BodyVx[b] += (_s.BodyCpxAcc[b] * co - _s.BodyCpyAcc[b] * si) / _s.BodyM[b];
                _s.BodyVy[b] += (_s.BodyCpxAcc[b] * si + _s.BodyCpyAcc[b] * co) / _s.BodyM[b];
                _s.BodyW[b] += _s.BodyClAcc[b] / _s.BodyI[b];
            }

            for (int b = 0; b < _s.BodyCount; b++)
            {
                _s.BodyX[b] += _s.BodyVx[b] * h;
                _s.BodyY[b] += _s.BodyVy[b] * h;
                _s.BodyRot[b] += _s.BodyW[b] * h;
                _s.BodyAlpha[b] = (_s.BodyW[b] - _s.BodyWPrev[b]) / h;
                _s.BodyWPrev[b] = _s.BodyW[b];
            }

            if (UpdateDamage(h))
            {
                RebuildBodies();
                BuildPairs();                      // fresh fragments need contacts THIS tick
                DecomposeMotion();                 // fragments claim their share of the field
            }
        }

        // ── INERTIA FOLLOWS THE DEFORMED SHAPE, AND L IS THE INVARIANT ────────
        // Material that moves outward increases I, and angular momentum — not omega — is what is
        // conserved. With I frozen at the rest configuration there is a free spin-up loop:
        // centrifugal drives radial deviation, Coriolis turns it tangential, DecomposeMotion
        // promotes that to body spin, which strengthens centrifugal. Paying for the shape change
        // makes it self-limiting, exactly as it is in reality. Rigid rotation keeps u identically
        // zero, so a pure spinner never enters this at all.
        for (int b = 0; b < _s.BodyCount; b++)
        {
            float I = 0f;
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                float x = _s.CellRx[c] + _s.CellUx[c];
                float y = _s.CellRy[c] + _s.CellUy[c];
                I += _s.CellIc[c] + _s.CellM[c] * (x * x + y * y);
            }
            I = SimMath.Max(1f, I);
            if (SimMath.Abs(I - _s.BodyI[b]) > 1e-9f * _s.BodyI[b])
            {
                _s.BodyW[b] *= _s.BodyI[b] / I;
                _s.BodyI[b] = I;
            }
        }

        // ── RUBBLE IS RIGID, SO ITS IMPENETRABILITY IS KINEMATIC ──────────────
        // A bond-less single cell has no bond network and therefore no deformation outlet at all:
        // u stays zero and overlap has nothing to convert into. Everything else resolves overlap by
        // deforming, so this is the ONE case that still needs a positional nudge. Translation-only,
        // and only when BOTH sides are rubble, so it can never touch a bonded body and the twitch it
        // used to cause on large bodies cannot return.
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            int a = ct.A, b = ct.B;
            if (_s.CellDead[a] || _s.CellDead[b]) continue;
            int ba = _s.CellBody[a], bb = _s.CellBody[b];
            if (ba < 0 || bb < 0 || ba >= _s.BodyCount || bb >= _s.BodyCount || ba == bb) continue;
            if (_s.BodyCellLen[ba] > 1 || _s.BodyCellLen[bb] > 1) continue;
            float pen = ct.Depth - 0.05f;
            if (pen <= 0f) continue;
            float push = SimMath.Min(pen, 0.5f) * 0.5f;
            float wA = 1f / _s.BodyM[ba], wB = 1f / _s.BodyM[bb];
            float tot = wA + wB;
            if (tot < 1e-12f) continue;
            _s.BodyX[ba] -= ct.Nx * push * wA / tot;
            _s.BodyY[ba] -= ct.Ny * push * wA / tot;
            _s.BodyX[bb] += ct.Nx * push * wB / tot;
            _s.BodyY[bb] += ct.Ny * push * wB / tot;
        }

        for (int b = 0; b < _s.BodyCount; b++)
            if (_s.BodyPlast[b] > RebakeThreshold) Rebake(b);   // bends become structure

        ResetDeep();
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            if (_s.CellDead[ct.A] || _s.CellDead[ct.B]) continue;
            RecordDeep(ct.A, ct.B, ct.Depth);
            RecordDeep(ct.B, ct.A, ct.Depth);
        }
        if (ConvertDust()) RebuildBodies();          // per tick, gated on mechanical decoupling
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  conservation metrics — diagnostics, not simulation state
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Rigid kinetic energy of the live bodies.</summary>
    public float BodyKineticEnergy()
    {
        float k = 0f;
        for (int b = 0; b < _s.BodyCount; b++)
            k += 0.5f * _s.BodyM[b] * (_s.BodyVx[b] * _s.BodyVx[b] + _s.BodyVy[b] * _s.BodyVy[b])
                 + 0.5f * _s.BodyI[b] * _s.BodyW[b] * _s.BodyW[b];
        return k;
    }

    /// <summary>
    /// Total linear momentum, bodies AND the deviation field. Counting only bodies mis-reads
    /// momentum still in flight as a leak, which is exactly how an earlier cell-contact experiment
    /// was wrongly convicted.
    /// </summary>
    public void TotalMomentum(out float px, out float py)
    {
        px = 0f; py = 0f;
        for (int b = 0; b < _s.BodyCount; b++)
        {
            px += _s.BodyM[b] * _s.BodyVx[b];
            py += _s.BodyM[b] * _s.BodyVy[b];
            SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                px += _s.CellM[c] * (_s.CellDvx[c] * co - _s.CellDvy[c] * si);
                py += _s.CellM[c] * (_s.CellDvx[c] * si + _s.CellDvy[c] * co);
            }
        }
    }

    /// <summary>Fastest material point in the scene, the normaliser for the drift metric.</summary>
    public float MaxMaterialSpeed()
    {
        float m = 0f;
        for (int b = 0; b < _s.BodyCount; b++)
        {
            SimMath.SinCos(_s.BodyRot[b], out float si, out float co);
            int off = _s.BodyCellOff[b], len = _s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = _s.BodyCells[off + i];
                if (_s.CellDead[c]) continue;
                float rx = _s.CellRx[c] * co - _s.CellRy[c] * si;
                float ry = _s.CellRx[c] * si + _s.CellRy[c] * co;
                float sp = SimMath.Hypot(_s.BodyVx[b] - _s.BodyW[b] * ry,
                                         _s.BodyVy[b] + _s.BodyW[b] * rx);
                if (sp > m) m = sp;
            }
        }
        return m;
    }

    /// <summary>
    /// Largest distance between two copies of the same shared vertex inside one body. A bond is a
    /// shared side, so this must stay at zero: it is the direct assertion that skinning holds.
    /// </summary>
    public float MaxSharedVertexGap()
    {
        float worst = 0f;
        var bufX = new float[64];
        var bufY = new float[64];
        for (int g = 0; g < _s.GrpCount; g++)
        {
            int off = _s.GrpOff[g], len = _s.GrpLen[g];
            for (int i = 0; i < len; i++)
            {
                int ci = _s.GrpCell[off + i];
                if (_s.CellDead[ci]) continue;
                if (bufX.Length < _s.PolyLen[ci]) { bufX = new float[_s.PolyLen[ci]]; bufY = new float[_s.PolyLen[ci]]; }
                CellPoly(ci, bufX, bufY);
                float x0 = bufX[_s.GrpVert[off + i]], y0 = bufY[_s.GrpVert[off + i]];

                for (int j = i + 1; j < len; j++)
                {
                    int cj = _s.GrpCell[off + j];
                    if (_s.CellDead[cj] || _s.CellBody[cj] != _s.CellBody[ci]) continue;
                    if (bufX.Length < _s.PolyLen[cj]) { bufX = new float[_s.PolyLen[cj]]; bufY = new float[_s.PolyLen[cj]]; }
                    CellPoly(cj, bufX, bufY);
                    float dx = bufX[_s.GrpVert[off + j]] - x0;
                    float dy = bufY[_s.GrpVert[off + j]] - y0;
                    float d = SimMath.Hypot(dx, dy);
                    if (d > worst) worst = d;
                }
            }
        }
        return worst;
    }
}
