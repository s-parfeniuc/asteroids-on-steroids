using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{
    // ══════════════════════════════════════════════════════════════════════════
    //  contact — cell scale throughout
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Solves one contact. The impulse is both SIZED and APPLIED at cell scale — mismatched scales
    /// were an energy pump in an earlier version — and the constraint reads the cell's total
    /// velocity, body rigid motion plus deviation.
    /// </summary>
    /// <remarks>
    /// <para><b>Levers come from rest, geometry from the deformed shape.</b> The contact point and
    /// normal are produced by the skinned polygons, but the lever arms are measured from the rest
    /// cell centres, because <see cref="DecomposeMotion"/> promotes the deviation field with rest
    /// arms. Mismatched frames between injection and promotion pump energy under heavy vibration.
    /// </para>
    /// <para>The velocity solve kills approach plus a tiny capped separation drift that drains
    /// residual resting overlap. That is not the old penetration bias, which scaled with pen/h and
    /// ground fragment piles to dust; this is a constant crawl whose work is negligible by
    /// construction. Impact-scale overlap is real deformation and is consumed elsewhere.</para>
    /// </remarks>
    private void SolveContact(ref Contact ct, float h)
    {
        int a = ct.A, b = ct.B;
        if (_s.CellDead[a] || _s.CellDead[b]) return;
        int ba = _s.CellBody[a], bb = _s.CellBody[b];
        if (ba < 0 || bb < 0 || ba >= _s.BodyCount || bb >= _s.BodyCount) return;

        _s.CellTouch[a] = _s.Tick;
        _s.CellTouch[b] = _s.Tick;

        SimMath.SinCos(_s.BodyRot[ba], out float sA, out float cA);
        SimMath.SinCos(_s.BodyRot[bb], out float sB, out float cB);

        float wax = _s.BodyX[ba] + _s.CellRx[a] * cA - _s.CellRy[a] * sA;
        float way = _s.BodyY[ba] + _s.CellRx[a] * sA + _s.CellRy[a] * cA;
        float wbx = _s.BodyX[bb] + _s.CellRx[b] * cB - _s.CellRy[b] * sB;
        float wby = _s.BodyY[bb] + _s.CellRx[b] * sB + _s.CellRy[b] * cB;

        float rax = ct.Px - wax, ray = ct.Py - way;
        float rbx = ct.Px - wbx, rby = ct.Py - wby;
        float RAx = ct.Px - _s.BodyX[ba], RAy = ct.Py - _s.BodyY[ba];
        float RBx = ct.Px - _s.BodyX[bb], RBy = ct.Py - _s.BodyY[bb];

        float ima = _s.CellIm[a], imb = _s.CellIm[b];
        float iica = _s.CellIic[a], iicb = _s.CellIic[b];

        // normal
        TotalVelocity(a, b, ba, bb, cA, sA, cB, sB, rax, ray, rbx, rby, RAx, RAy, RBx, RBy,
            out float vax, out float vay, out float vbx2, out float vby2);
        float vn = (vbx2 - vax) * ct.Nx + (vby2 - vay) * ct.Ny;

        float rnAc = rax * ct.Ny - ray * ct.Nx;
        float rnBc = rbx * ct.Ny - rby * ct.Nx;
        float w = ima + imb + iica * rnAc * rnAc + iicb * rnBc * rnBc;
        if (w < 1e-12f) return;

        // Impenetrability is kinematic, so contact stiffness has a floor independent of material c:
        // a soft material must fracture, not interpenetrate. Two materials meet here, so take the
        // stiffer wave speed and the mean density.
        float cC = SimMath.Max(SimMath.Max(_s.BodyCpx[ba], _s.BodyCpx[bb]),
                               _tune.ContactCMin * _tune.PxPerMetre);
        float rho = 0.5f * (_s.BodyRho[ba] + _s.BodyRho[bb]);
        float lc = SimMath.Max(1f, SimMath.Min(_s.CellPerim[a], _s.CellPerim[b]) * 0.25f);
        float alpha = 1f / (rho * cC * cC * lc);
        float at = alpha / (h * h);

        float pen = SimMath.Max(0f, ct.Depth - 0.05f);
        float sep = SimMath.Min(pen, 2f) * 2f;

        float dl = ((sep - vn) * h - at * ct.Ln) / (w + at);
        float nl = SimMath.Max(0f, ct.Ln + dl);   // one-sided: contacts push, never pull
        dl = nl - ct.Ln;
        ct.Ln = nl;
        ApplyPair(a, b, cA, sA, cB, sB, rax, ray, rbx, rby, dl / h * ct.Nx, dl / h * ct.Ny);

        // Coulomb friction — same scale, solved after the normal so it sees corrected velocities.
        if (_tune.ContactMu > 0f && ct.Ln > 0f)
        {
            float tx = -ct.Ny, ty = ct.Nx;
            TotalVelocity(a, b, ba, bb, cA, sA, cB, sB, rax, ray, rbx, rby, RAx, RAy, RBx, RBy,
                out vax, out vay, out vbx2, out vby2);
            float vt = (vbx2 - vax) * tx + (vby2 - vay) * ty;
            float rtAc = rax * ty - ray * tx;
            float rtBc = rbx * ty - rby * tx;
            float wt = ima + imb + iica * rtAc * rtAc + iicb * rtBc * rtBc;
            if (wt > 1e-12f)
            {
                float dlt = -vt * h / wt;
                float cap = _tune.ContactMu * ct.Ln;
                float nlt = SimMath.Max(-cap, SimMath.Min(cap, ct.Lt + dlt));
                dlt = nlt - ct.Lt;
                ct.Lt = nlt;
                ApplyPair(a, b, cA, sA, cB, sB, rax, ray, rbx, rby, dlt / h * tx, dlt / h * ty);
            }
        }
    }

    private void TotalVelocity(
        int a, int b, int ba, int bb,
        float cA, float sA, float cB, float sB,
        float rax, float ray, float rbx, float rby,
        float RAx, float RAy, float RBx, float RBy,
        out float vax, out float vay, out float vbx, out float vby)
    {
        float adx = _s.CellDvx[a] * cA - _s.CellDvy[a] * sA;
        float ady = _s.CellDvx[a] * sA + _s.CellDvy[a] * cA;
        float bdx = _s.CellDvx[b] * cB - _s.CellDvy[b] * sB;
        float bdy = _s.CellDvx[b] * sB + _s.CellDvy[b] * cB;

        vax = _s.BodyVx[ba] - _s.BodyW[ba] * RAy + adx - _s.CellDw[a] * ray;
        vay = _s.BodyVy[ba] + _s.BodyW[ba] * RAx + ady + _s.CellDw[a] * rax;
        vbx = _s.BodyVx[bb] - _s.BodyW[bb] * RBy + bdx - _s.CellDw[b] * rby;
        vby = _s.BodyVy[bb] + _s.BodyW[bb] * RBx + bdy + _s.CellDw[b] * rbx;
    }

    private void ApplyPair(int a, int b, float cA, float sA, float cB, float sB,
        float rax, float ray, float rbx, float rby, float ix, float iy)
    {
        float aAx = -ix * _s.CellIm[a], aAy = -iy * _s.CellIm[a];
        float aBx = ix * _s.CellIm[b], aBy = iy * _s.CellIm[b];

        // world -> body-local at the boundary
        _s.CellDvx[a] += aAx * cA + aAy * sA;
        _s.CellDvy[a] += -aAx * sA + aAy * cA;
        _s.CellDvx[b] += aBx * cB + aBy * sB;
        _s.CellDvy[b] += -aBx * sB + aBy * cB;

        _s.CellDw[a] -= _s.CellIic[a] * (rax * iy - ray * ix);
        _s.CellDw[b] += _s.CellIic[b] * (rbx * iy - rby * ix);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  damage, plasticity, failure
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advances plastic flow and cohesive damage, and separates bonds that reach d = 1.
    /// Returns true if anything broke.
    /// </summary>
    /// <remarks>
    /// <para><b>Plastic flow.</b> Elastic stretch past the yield point flows into a permanent offset
    /// at a finite rate, capping the force near yield level and dissipating the work. Because flow
    /// takes time, fast loading outruns it and reaches the damage threshold instead: bend slowly,
    /// snap fast — the ductile/brittle transition is rate-dependent and emergent rather than
    /// switched.</para>
    /// <para><b>Cohesive damage.</b> Below the peak stretch the bond is elastic; past it, linear
    /// softening whose triangle area IS the fracture energy, so toughness is a local per-bond
    /// property. Damage is driven by the monotone history maximum, so unloading is elastic at
    /// reduced stiffness and the bond can never re-heal.</para>
    /// <para><b>Cracks must connect to a surface.</b> A crack is new free surface and cannot appear
    /// floating in the interior — separating there would have to push each half of the body apart
    /// through solid material. Interior bonds still accumulate damage (micro-cracking is real) but
    /// clamp just below separation until a crack front reaches them, at which point the stored
    /// history releases them at once. Cracks therefore nucleate at surfaces and run inward.</para>
    /// </remarks>
    private bool UpdateDamage(float h)
    {
        bool broke = false;
        float invSh = 1f / SimMath.Max(0.05f, _tune.ShearMul);
        float sm = SimMath.Max(0.05f, _tune.ShearMul);
        float fr = SimMath.Min(1f, _tune.FlowRate * h);

        for (int k = 0; k < _s.BondCount; k++)
        {
            if (_s.BondBroken[k]) continue;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.CellDead[a] || _s.CellDead[b]) { _s.BondBroken[k] = true; continue; }

            float sy = _s.BondSy0[k] * _tune.YieldScale;
            float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];
            float len = _s.BondLen[k];

            // Fast path for the idle majority: an L1 bound on the equivalent stretch, taken before
            // any transcendental or body lookup. Rate sensitivity only raises thresholds, and
            // compression below yield does nothing, so this cannot skip a bond that would act.
            if (_s.BondDmg[k] == 0f && _s.BondPTot[k] == 0f)
            {
                float ub = SimMath.Abs(sn) + SimMath.Abs(sa) * len * 0.5f + SimMath.Abs(st) * invSh;
                if (ub <= SimMath.Min(_s.BondS0[k], sy)) continue;
            }

            int body = _s.CellBody[a];

            if (sy < _s.BondS0[k] * 4f)
            {
                float ex = SimMath.Abs(sn) - sy;
                if (ex > 0f)
                {
                    float df = ex * fr * SimMath.Sign(sn);
                    sn -= df; _s.BondPn[k] += df;
                    float amt = SimMath.Abs(df);
                    _s.BondPTot[k] += amt;
                    PlasticWork += _s.BondK0[k] * sy * amt;
                    if (body >= 0 && body < _s.BodyCount) _s.BodyPlast[body] += amt;
                }
                float syt = sy * sm;
                ex = SimMath.Abs(st) - syt;
                if (ex > 0f)
                {
                    float df = ex * fr * SimMath.Sign(st);
                    st -= df; _s.BondPt[k] += df;
                    float amt = SimMath.Abs(df);
                    _s.BondPTot[k] += amt;
                    PlasticWork += _s.BondK0[k] * syt * amt;
                    if (body >= 0 && body < _s.BodyCount) _s.BodyPlast[body] += amt;
                }
                float sya = 2f * sy / SimMath.Max(1f, len);
                ex = SimMath.Abs(sa) - sya;
                if (ex > 0f)
                {
                    float df = ex * fr * SimMath.Sign(sa);
                    sa -= df; _s.BondPa[k] += df;
                    float amt = SimMath.Abs(df) * len * 0.5f;
                    _s.BondPTot[k] += amt;
                    PlasticWork += _s.BondKa0[k] * sya * SimMath.Abs(df);
                    if (body >= 0 && body < _s.BodyCount) _s.BodyPlast[body] += amt;
                }
                _s.BondSn[k] = sn; _s.BondSt[k] = st; _s.BondSa[k] = sa;
            }

            // Equivalent opening stretch: opening plus the outer-fibre stretch of bending drive
            // Mode I; shear, normalised so its own threshold maps to s0, drives Mode II.
            // Compression never damages a bond — unconfined compressive failure arrives through
            // shear and bending, which is how real uniaxial splitting works.
            float op = SimMath.Max(0f, sn) + SimMath.Abs(sa) * len * 0.5f;
            float sh = SimMath.Abs(st) * invSh;
            float lam = SimMath.Hypot(op, sh);
            if (lam > _s.BondLmax[k]) _s.BondLmax[k] = lam;

            float rateMul = 1f;
            if (_tune.RateSens > 0f)
                rateMul = 1f + _tune.RateSens * SimMath.Log(1f + _s.BondRate[k] / _tune.RateRef);

            float s0 = _s.BondS0[k] * rateMul;
            float chi = SimMath.Max(1.001f, body >= 0 && body < _s.BodyCount ? _s.BodyChi[body] : 1.8f);
            float duct = body >= 0 && body < _s.BodyCount ? _s.BodyDuct[body] : 0.02f;
            float cellSize = body >= 0 && body < _s.BodyCount ? _s.BodyCellSize[body] : 30f;

            // Ductile exhaustion: a bond cannot flow forever. The capacity is the material's plastic
            // strain to failure, NOT a multiple of chi — plastic toughness and brittle softening are
            // different properties, and steel's toughness comes from flowing at no cost in elastic
            // stretchiness.
            bool exh = _s.BondPTot[k] > 0f
                       && _s.BondPTot[k] >= SimMath.Max(0.01f, duct) * cellSize;

            if (_s.BondLmax[k] <= s0 && !exh) continue;

            float lmax = _s.BondLmax[k];
            float d;
            if (exh) d = 1f;
            else if (chi <= 1.02f) d = 1f;                       // brittle limit: snaps at peak
            else
            {
                float sf = s0 * chi;
                d = SimMath.Min(1f, sf * (lmax - s0) / (lmax * (sf - s0)));
            }

            bool atSurface = _s.CellSurf[a] || _s.CellSurf[b] || _s.CellCracked[a] || _s.CellCracked[b];
            if (!atSurface) d = SimMath.Min(d, 0.97f);

            float dPrev = _s.BondDmg[k];
            if (d > _s.BondDmg[k]) _s.BondDmg[k] = d;

            if (_s.BondDmg[k] >= 1f)
            {
                _s.BondBroken[k] = true;
                _s.CellCracked[a] = true;                        // a break IS new surface
                _s.CellCracked[b] = true;
                _s.BondMode[k] = (byte)(sh > op ? 2 : 1);
                Broken++;

                ReleaseRecoil(k, a, b, dPrev, chi);

                _s.BondSn[k] = 0f; _s.BondSt[k] = 0f; _s.BondSa[k] = 0f;
                broke = true;
            }
        }
        return broke;
    }

    /// <summary>
    /// Releases a separating bond's residual elastic energy as recoil instead of deleting it.
    /// </summary>
    /// <remarks>
    /// An ungated cohesive bond separates at ~zero force, so zeroing its stretch discards nothing.
    /// But a bond released by the surface gate — and every bond in the brittle limit — snaps while
    /// still carrying stiffness on a possibly large stretch. Converting that to an equal and
    /// opposite impulse along the strained direction conserves momentum exactly and is also the
    /// spall fly-apart: crack faces snap away from a crack that opens under load. The impulse is
    /// solved for the energy ACTUALLY injected given the cells' current relative velocity, because
    /// the naive form rides on top of cells already flying apart and the cross term mints energy.
    /// </remarks>
    private void ReleaseRecoil(int k, int a, int b, float dPrev, float chi)
    {
        float soft = 1f - dPrev;
        float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];

        // Capped at the full softening-triangle energy — the most a bond can physically hold.
        // Without the cap, a stretch that jumps far past separation inside one substep releases
        // energy that was never charged to the field.
        float en = SimMath.Min(
            0.5f * soft * (_s.BondK0[k] * (sn * sn + st * st) + _s.BondKa0[k] * sa * sa),
            0.5f * _s.BondK0[k] * _s.BondS0[k] * _s.BondS0[k] * chi);
        if (en <= 1e-9f) return;

        float nx = _s.BondNx[k], ny = _s.BondNy[k];
        float dx = SimMath.Max(0f, sn) * nx - st * ny;
        float dy = SimMath.Max(0f, sn) * ny + st * nx;
        float ld = SimMath.Hypot(dx, dy);
        if (ld <= 1e-9f) return;
        dx /= ld; dy /= ld;

        float mu = 1f / (_s.CellIm[a] + _s.CellIm[b]);
        float vrel = (_s.CellDvx[b] - _s.CellDvx[a]) * dx + (_s.CellDvy[b] - _s.CellDvy[a]) * dy;
        float disc = vrel * vrel + 2f * en / mu;
        float j = mu * (SimMath.Sqrt(SimMath.Max(0f, disc)) - vrel);
        if (j <= 0f) return;

        _s.CellDvx[a] -= dx * j * _s.CellIm[a];
        _s.CellDvy[a] -= dy * j * _s.CellIm[a];
        _s.CellDvx[b] += dx * j * _s.CellIm[b];
        _s.CellDvy[b] += dy * j * _s.CellIm[b];
        RecoilEnergy += en;
    }
}
