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
        if (_s.Dead(a) || _s.Dead(b)) return;
        // Speculative entries are tracked but inert until they actually close. Their stored
        // impulse is cleared so a pair that separates and closes again does not warm-start from a
        // stale value.
        if (ct.Depth <= 0f) { ct.Ln = 0f; ct.Lt = 0f; return; }
        int ba = _s.CellBody[a], bb = _s.CellBody[b];
        if (ba < 0 || bb < 0 || ba >= _s.BodyCount || bb >= _s.BodyCount) return;
        if (ba == bb) return;                      // the two cells merged into one body

        C.ContactSolves++;
        _s.CellTouch[a] = _s.Tick;
        _s.CellTouch[b] = _s.Tick;

        BodyTrig(ba, out float sA, out float cA);
        BodyTrig(bb, out float sB, out float cB);

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
        float alpha = _tune.ContactCompliance / (rho * cC * cC * lc);
        float at = alpha / (h * h);

        // POSITIONAL BIAS — now the ONLY thing resolving overlap.
        //
        // It used to be a token 4 px/s at most, because penetration was absorbed by deformation:
        // overlap WAS the deformation, consumed elastically by u and permanently by the rebake. With
        // that layer gone the bias does all the work, and at its old strength two bodies sank 36 px
        // into each other — deeper than a cell.
        //
        // Baumgarte form: removing `pen` within one substep needs a separation velocity of pen/h, so
        // the tuning value is the fraction of the error corrected per substep. Capped, because a deep
        // overlap on the first substep of an impact would otherwise fire material apart.
        float pen = SimMath.Max(0f, ct.Depth - 0.05f);
        float sep = SimMath.Min(pen, _tune.ContactMaxBias) * _tune.ContactBias / h;

        float dl = ((sep - vn) * h - at * ct.Ln) / (w + at);
        float nl = SimMath.Max(0f, ct.Ln + dl);   // one-sided: contacts push, never pull
        dl = nl - ct.Ln;
        ct.Ln = nl;
        ApplyPair(a, b, cA, sA, cB, sB, rax, ray, rbx, rby, dl / h * ct.Nx, dl / h * ct.Ny);

        // ── COMMINUTION PRESSURE ──────────────────────────────────────────────
        // Stress, not penetration depth. Depth is geometry and says nothing about how hard something
        // is being pressed, which is why one constant could never cover both a slow press and a
        // hypervelocity impact.
        //
        // TWO TERMS, because the impulse alone measures only half the load.
        //
        //   dynamic     what the solve spent to stop material APPROACHING. It reads an impact hard
        //               and a static squeeze as almost nothing.
        //
        //               Two corrections against the obvious `Ln/(h*lc)`. First, h SQUARED: in XPBD
        //               the multiplier has units of mass*length and the force it represents is
        //               lambda/h^2, so dividing by h once left a quantity proportional to the
        //               substep length — the criterion changed with the substep count, and since
        //               CFL ties substeps to grain, with cell size too.
        //
        //               Second, the BIAS SHARE IS REMOVED. Lambda pays for two things here: braking
        //               approach, and driving the positional correction apart. The second is
        //               bookkeeping, not load — and it dominated: measured at rest, a 0.5 px
        //               overlap generated 3.0e6 of "pressure" from the bias alone, more than an
        //               800 px/s impact, and it was flat across a 64x range of overlap because the
        //               bias is capped. Lambda resets every substep and the solve is a single pass,
        //               so Ln is exactly ((sep - vn)*h)/(w + at) and the two parts separate with no
        //               shadow state: the braking share is the same expression with sep dropped.
        //
        //   confining   penetration strain times the contact modulus. Uncapped, unlike the bias in
        //               the solve above: nothing here feeds back into motion, so depth can be read
        //               honestly. This is what registers a cell wedged between two neighbours that
        //               have already stopped moving — which the impulse term, by construction,
        //               cannot see at all.
        //
        //   drive       the cell's OWN deviation velocity, where it points INTO the contact. This
        //               is the body behind the cell refusing to let it escape: the contact pushes
        //               the cell back, the bonds push it forward, and the material in between is
        //               what gets crushed. Scaled by the acoustic impedance rho*c, which is the
        //               plane-wave relation stress = rho*c*v and so lands on the same scale as the
        //               other two with no free constant of its own.
        //
        //               This is the only per-cell term — a and b have different deviation fields —
        //               and it is why the two cells of one contact can be under different pressure.
        //               It also needs NO special case for a lone cell: DecomposeMotion zeroes the
        //               deviation field of a single-cell body by construction, since one cell's
        //               motion is entirely rigid, so drive is exactly zero there. That is the right
        //               answer rather than a missing one — a lone cell that the normal cannot crush
        //               simply gets pushed away by the solver instead.
        //
        // The sum is ACCUMULATED PER CELL and tested once per substep, not tested per contact. A
        // cell squeezed from both sides at 60% of threshold is under 120% of it and must crush;
        // testing each contact on its own reads 60%, twice, and concludes nothing is happening.
        // Confinement is the sum of what presses on a cell, so the sum is what the threshold is
        // for.
        float lnBrake = SimMath.Min(ct.Ln, SimMath.Max(0f, -vn) * h / (w + at));
        float dyn = lnBrake / (h * h * lc);
        float conf = _tune.CrushConfine * rho * cC * cC * (pen / lc);
        float shared = dyn + conf;

        // The normal points from a to b, so a is driven into the contact along +n and b along -n.
        float adx = _s.CellDvx[a] * cA - _s.CellDvy[a] * sA;
        float ady = _s.CellDvx[a] * sA + _s.CellDvy[a] * cA;
        float bdx = _s.CellDvx[b] * cB - _s.CellDvy[b] * sB;
        float bdy = _s.CellDvx[b] * sB + _s.CellDvy[b] * cB;
        float rc = rho * cC;
        float driveA = rc * SimMath.Max(0f, adx * ct.Nx + ady * ct.Ny);
        float driveB = rc * SimMath.Max(0f, -(bdx * ct.Nx + bdy * ct.Ny));

        float pressA = shared + driveA;
        float pressB = shared + driveB;
        _cellPress[a] += pressA;
        _cellPress[b] += pressB;
        ct.PressA = pressA;
        ct.PressB = pressB;
        float press = SimMath.Max(pressA, pressB);

        if (MeasureStress)
        {
            if (dyn > PeakDyn) PeakDyn = dyn;
            if (conf > PeakConf) PeakConf = conf;
            if (press > PeakStress) PeakStress = press;
            if (press > 1f)
            {
                int bucket = (int)SimMath.Log2(press);
                if (bucket >= StressHist.Length) bucket = StressHist.Length - 1;
                if (bucket >= 0) StressHist[bucket]++;
            }
        }

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
                C.ContactFriction++;
                ApplyPair(a, b, cA, sA, cB, sB, rax, ray, rbx, rby, dlt / h * tx, dlt / h * ty);
            }
        }
    }

    /// <summary>
    /// Turns the per-cell contact pressure accumulated over this substep into comminution dose.
    /// </summary>
    /// <remarks>
    /// <para>Dose rather than a switch, and that is what couples comminution to participating mass.
    /// A lone cell is pushed away within about a substep and accumulates almost nothing; a cell with
    /// a body behind it has its bonds pulling it back into the contact — <c>SolveContact</c> runs
    /// after <c>BondForces</c>, so that restoring force is already in <c>dv</c> — so it keeps being
    /// pressed and the dose climbs until it fails. Pressure decides <i>whether</i>, duration decides
    /// <i>how long until</i>.</para>
    ///
    /// <para><b>Each cell is charged against its own material.</b> The previous version took
    /// <c>min</c> of the two bodies' thresholds and applied it to both, so a steel plate struck by a
    /// glass shard was judged by glass — a cell's resistance to being crushed is a property of the
    /// cell, never of what happens to be touching it.</para>
    ///
    /// <para>Walks the contact list rather than every cell, because the cells under load are exactly
    /// the ones with contacts. Zeroing on visit is what makes that safe: a cell in six contacts is
    /// reached six times and charged once, with its complete sum.</para>
    /// </remarks>
    private void AccumulateCrushDose(float h)
    {
        EnsureCrushScratch();
        int before = _crushCount;

        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            ChargeCell(ct.A, h);
            ChargeCell(ct.B, h);
        }

        // ── THE MOMENTUM TRANSFER HAPPENS HERE, NOT AT END OF TICK ────────────
        // The cell is not removed until ConvertDust — topology cannot change mid-solve — but WHO it
        // hands its momentum to has to be decided now, in the substep that crossed capacity, while
        // the contacts that did the crushing are still live.
        //
        // Deferring the whole operation to end of tick did not work, and the measurement was
        // unambiguous: at 1500 px/s the projectile crushed five cells and performed ZERO transfers.
        // Under a fast impactor the manifold is re-derived every substep, so by the end of the tick
        // the contact list no longer holds the pair that did the crushing — and a cell whose dose
        // crossed three substeps ago may have separated entirely. The weights were gone before
        // anything could be paid out with them, and the whole momentum went to the ledger.
        //
        // Two passes over contacts, and only when something actually crossed: total the weights over
        // live partners, then distribute. Both are O(contacts) however many cells crushed.
        // `_crushNew` is what crossed THIS substep and has not been paid out; `_crushMark` is
        // everything awaiting removal. The two must be separate: a cell marked three substeps ago
        // has already handed over its momentum, and totalling it again would pay it out twice.
        //
        // A partner that is ITSELF being powdered is still a valid recipient, and excluding one was
        // measurably wrong. Under a fast impactor both sides of the contact cross capacity in the
        // same substep — that is what a violent impact IS — so excluding crushed partners meant
        // neither side had anyone to pay, and at 1500 px/s five cells crushed with zero transfers.
        // The recipient is the partner's BODY, which goes on existing minus one cell, so it can
        // absorb momentum perfectly well while the cell that delivered the contact is removed.
        if (_crushCount == before) return;

        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            int a = ct.A, b = ct.B;
            if (_crushNew[a] && !_s.Dead(b)) _crushTot[a] += ct.PressA;
            if (_crushNew[b] && !_s.Dead(a)) _crushTot[b] += ct.PressB;
        }
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            int a = ct.A, b = ct.B;
            if (_crushNew[a] && !_s.Dead(b)) TransferShare(a, b, ct.PressA);
            if (_crushNew[b] && !_s.Dead(a)) TransferShare(b, a, ct.PressB);
        }
        for (int i = before; i < _crushCount; i++) _crushNew[_crushList[i]] = false;
    }

    private void ChargeCell(int c, float h)
    {
        float press = _cellPress[c];
        if (press <= 0f) return;
        _cellPress[c] = 0f;

        if (MeasureStress && CrushDose.Length > c)
        {
            float exd = press - CrushThreshold;
            if (exd > 0f) CrushDose[c] += exd * h;
        }

        int bi = _s.CellBody[c];
        if (bi < 0 || bi >= _s.BodyCount) return;
        float excess = press - _s.BodyCrush[bi];
        if (excess <= 0f) return;
        _s.CellCrush[c] += excess * h;

        // Crossed its material's capacity: schedule it for removal and price it now, at this
        // substep's pose and velocity, so the transfer below pays out what it actually had.
        if (!_tune.Dust || _crushMark[c] || _s.Solo(c)) return;
        if (_s.CellCrush[c] < _s.BodyCrushCap[bi]) return;

        BodyTrig(bi, out float si, out float co);
        float rx = _s.CellRx[c] * co - _s.CellRy[c] * si;
        float ry = _s.CellRx[c] * si + _s.CellRy[c] * co;
        float bw = _s.BodyW[bi];
        _crushVx[c] = _s.BodyVx[bi] - bw * ry + _s.CellDvx[c] * co - _s.CellDvy[c] * si;
        _crushVy[c] = _s.BodyVy[bi] + bw * rx + _s.CellDvx[c] * si + _s.CellDvy[c] * co;
        _crushW[c] = bw + _s.CellDw[c];
        _crushTot[c] = 0f; _crushJx[c] = 0f; _crushJy[c] = 0f; _crushGain[c] = 0f;
        _crushMark[c] = true;
        _crushNew[c] = true;
        _crushList[_crushCount++] = c;
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
        if (!Par(32))
        {
            double rec = 0;
            bool r = UpdateDamage(h, 0, _s.BodyCount, ref C, ref rec);
            RecoilEnergy += (float)rec;
            return r;
        }

        long before = C.DamageBroke;
        RunOverBodies((chunk, lo, hi) =>
            UpdateDamage(h, lo, hi, ref _chunkC[chunk], ref _chunkRecoil[chunk]));
        long broke = C.DamageBroke - before;
        Broken += (int)broke;                       // tallied from the merge, never across threads
        return broke > 0;
    }

    /// <summary>Cohesive damage and plastic flow for a contiguous range of bodies.</summary>
    /// <remarks>
    /// Per-body iteration for the same reason as <see cref="BondForces"/>: a bond couples two cells
    /// of one body, everything it writes belongs to that body, and <c>BodyBonds</c> holds a body's
    /// bonds in ascending global index — so the sequence each bond sees is unchanged.
    /// </remarks>
    private bool UpdateDamage(float h, int bodyLo, int bodyHi, ref SolverCounters ctr,
        ref double recoil)
    {
        bool broke = false;
        float invSh = 1f / SimMath.Max(0.05f, _tune.ShearMul);
        float sm = SimMath.Max(0.05f, _tune.ShearMul);
        float fr = SimMath.Min(1f, _tune.FlowRate * h);

        for (int bi = bodyLo; bi < bodyHi; bi++)
        {
        int bondOff = _s.BodyBondOff[bi], bondLen = _s.BodyBondLen[bi];
        for (int bx = 0; bx < bondLen; bx++)
        {
            int k = _s.BodyBonds[bondOff + bx];
            if (_s.BondBroken[k]) continue;
            ctr.DamageVisits++;
            int a = _s.BondA[k], b = _s.BondB[k];
            if (_s.Dead(a) || _s.Dead(b))
            { _s.BondBroken[k] = true; MarkDirty(_s.CellBody[a]); continue; }

            float sy = _s.BondSy0[k] * _tune.YieldScale;
            float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];
            float len = _s.BondLen[k];

            // FAST PATH FOR THE IDLE MAJORITY — and it now applies to damaged bonds too.
            //
            // `ub` is an L1 upper bound on the equivalent stretch, taken before any transcendental
            // or body lookup. Damage advances only when the history maximum BondLmax grows, so if
            // the bound sits at or below that maximum nothing can change; below BondS0 no damage
            // starts at all. Either way the bond is inert this substep.
            //
            // The old gate was `BondDmg == 0`, which meant a bond that had EVER been damaged could
            // never take the fast path again — it ran the full cohesive law every substep forever,
            // at rest or not. That is why this pass climbed to second place in the tick exactly as a
            // scene became interesting: the early-out rate collapsed as damage spread. Bounding
            // against max(s0, Lmax) instead skips work that provably does nothing, so the result is
            // unchanged and a resting damaged bond costs what a resting pristine one costs.
            {
                float ub = SimMath.Abs(sn) + SimMath.Abs(sa) * len * 0.5f + SimMath.Abs(st) * invSh;
                if (ub <= SimMath.Max(_s.BondS0[k], _s.BondLmax[k])) { ctr.DamageEarlyOut++; continue; }
            }

            int body = _s.CellBody[a];

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
            // Ductile exhaustion is gone with plastic flow: a bond that cannot flow cannot exhaust
            // itself. Materials now separate on failure strain and on chi, the softening ratio,
            // rather than on how far they could yield before tearing.
            if (_s.BondLmax[k] <= s0) continue;
            ctr.DamageEvaluated++;

            float lmax = _s.BondLmax[k];
            float d;
            if (chi <= 1.02f) d = 1f;                            // brittle limit: snaps at peak
            else
            {
                float sf = s0 * chi;
                d = SimMath.Min(1f, sf * (lmax - s0) / (lmax * (sf - s0)));
            }

            bool atSurface = _s.AtSurface(a) || _s.AtSurface(b);
            if (!atSurface) d = SimMath.Min(d, 0.97f);

            float dPrev = _s.BondDmg[k];
            if (d > _s.BondDmg[k]) _s.BondDmg[k] = d;

            if (_s.BondDmg[k] >= 1f)
            {
                _s.BondBroken[k] = true;
                _s.SetFlag(a, CellFlag.Cracked, true);                        // a break IS new surface
                _s.SetFlag(b, CellFlag.Cracked, true);
                _s.BondMode[k] = (byte)(sh > op ? 2 : 1);
                if (Jobs == null) Broken++;
                ctr.DamageBroke++;
                MarkDirty(body);

                ReleaseRecoil(k, a, b, dPrev, chi, ref recoil);

                _s.BondSn[k] = 0f; _s.BondSt[k] = 0f; _s.BondSa[k] = 0f;
                broke = true;
            }
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
    private void ReleaseRecoil(int k, int a, int b, float dPrev, float chi, ref double recoil)
    {
        float soft = 1f - dPrev;
        float sn = _s.BondSn[k], st = _s.BondSt[k], sa = _s.BondSa[k];

        // Capped at the full softening-triangle energy — the most a bond can physically hold.
        // Without the cap, a stretch that jumps far past separation inside one substep releases
        // energy that was never charged to the field.
        float en = SimMath.Min(
            0.5f * soft * (_s.BondK0[k] * (sn * sn + st * st) + _s.BondKa0[k] * sa * sa),
            0.5f * _s.BondK0[k] * _s.BondS0[k] * _s.BondS0[k] * chi);
        en *= _tune.SpallFraction;      // the rest goes to new surface and to radiated waves
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
        recoil += en;
    }
}
