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
        // WORK, which is what carving is actually rate-limited by. Force times approach speed times
        // the substep: the energy this contact took out of the approach. A pressure-time integral
        // would let destruction outrun momentum transfer — carve rate would RISE with pressure, so
        // the more violent the impact the faster the load path is cut. Work cannot do that, because
        // the contact can only do work if the impactor decelerates.
        ct.Work = lnBrake * SimMath.Max(0f, -vn) / h;
        if (MeasureStress && ct.Work > PeakWork) PeakWork = ct.Work;
        if (TraceCell >= 0 && (a == TraceCell || b == TraceCell))
        {
            bool isA = a == TraceCell;
            TraceSink?.Invoke($"  sub{_s.Substep % 100,2} contact with {(isA ? b : a)}: "
                + $"depth {ct.Depth,6:F2} n=({ct.Nx,5:F2},{ct.Ny,5:F2}) "
                + $"dyn {dyn,10:E2} conf {conf,10:E2} drive {(isA ? driveA : driveB),10:E2} "
                + $"-> press {(isA ? pressA : pressB),10:E2}");
        }

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
    /// <summary>
    /// Turns the work each contact did into carved area, once per substep, after every contact has
    /// been solved.
    /// </summary>
    /// <remarks>
    /// <para><b>After the loop, not inside it.</b> Carving mutates collider geometry — vertices,
    /// area, <c>CellRad</c>, the centroid — and contacts solved later in the same substep read all
    /// of that. Carving mid-loop would leave the manifold describing geometry that no longer exists.
    /// </para>
    ///
    /// <para><b>Per contact, not per cell.</b> A cell in three contacts is carved three times, from
    /// three directions, which is what a cell caught in a pile should look like. Accumulating a
    /// single averaged direction per cell would flatten a corner being ground from two sides into one
    /// meaningless bevel.</para>
    ///
    /// <para>Pressure still decides WHETHER — it is compared against the cell's own material
    /// threshold — and the share of the work that carves ramps with how far over that threshold the
    /// cell is, so the onset is smooth rather than a switch.</para>
    /// </remarks>
    private void ApplyCarving(float h)
    {
        // The GATE reads the cell's TOTAL pressure this substep, not this contact's share. A cell
        // squeezed from both sides at 60% of threshold is under 120% of it and must yield; asking
        // each contact on its own reads 60%, twice, and concludes nothing is happening. Confinement
        // is the sum of what presses on a cell, so the sum is what the threshold is for.
        //
        // The RATE reads this contact's work, because the energy is this contact's to spend.
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];

            // NO BRAKING GATE. This used to skip any contact with ct.Work <= 0, left over from the
            // rate being paid out of braking work. The rate moved to pressure; the gate did not, so
            // carving still only ran on substeps where something was being decelerated — the
            // approach transient — and a cell held in deep sustained overlap carved on roughly one
            // substep in nine. Traced on cell 562 at grain 170: pressure sat at 3.5e5 against a
            // 2.5e5 threshold for eight substeps with no carve attempted at all, while penetration
            // grew from 2.3 to 5.3 px.
            float dA = CarveSide(ct.A, ct.B, _cellPress[ct.A], h, ct.Nx, ct.Ny, ct.Px, ct.Py);
            float dB = CarveSide(ct.B, ct.A, _cellPress[ct.B], h, -ct.Nx, -ct.Ny, ct.Px, ct.Py);

            // CLOSE THE FEEDBACK LOOP. Depth comes from the manifold, which is only re-derived when
            // ManifoldDrift trips — so without this the pressure keeps reading the overlap that was
            // just carved away, and erosion runs at full rate against material already gone.
            // Subtracting the realized depth is O(1); Depth0 goes with it so the refresh path stays
            // consistent with the rebuild path.
            float relief = dA + dB;
            if (relief > 0f) { ct.Depth -= relief; ct.Depth0 -= relief; }
        }

        // Drained by walking contacts rather than by clearing the whole array: only contact
        // endpoints are ever written, so this is O(contacts) instead of O(cells) — and it cannot
        // touch scratch that a contact-free tick never caused to be allocated.
        for (int i = 0; i < _contactCount; i++)
        {
            ref Contact ct = ref _contacts[i];
            ct.Work = 0f;
            _cellPress[ct.A] = 0f;
            _cellPress[ct.B] = 0f;
        }
    }

    /// <summary>
    /// Carves one cell of one contact, and hands the shed mass to the partner.
    /// </summary>
    /// <param name="nx">World-space direction from this cell TOWARD the partner: the load direction.</param>
    private float CarveSide(int c, int other, float press, float h, float nx, float ny, float wpx, float wpy)
    {
        DbgCalls++;
        if (_s.Dead(c) || _s.Dead(other)) { DbgDead++; return 0f; }
        int bi = _s.CellBody[c];
        if (bi < 0 || bi >= _s.BodyCount) return 0f;

        ref readonly Material m = ref _s.Mat(c);
        float excess = press - m.Crush;
        if (c == TraceCell)
            TraceSink?.Invoke($"     carve gate: press {press,10:E2} vs threshold {m.Crush,10:E2}"
                + $" -> {(excess > 0f ? "OPEN" : "shut")}");
        if (excess <= 0f) { DbgGate++; return 0f; }

        // ── RATE: VISCOPLASTIC YIELD, NOT BRAKING WORK ───────────────────────
        // The rate used to be paid out of the work the contact did against the approach. That is a
        // TRANSIENT: the solve kills the approach within a substep or two — that is its whole job —
        // so work collapses to nothing and a SUSTAINED overlap carves nothing at all. Measured, 10
        // of 12 carve attempts removed zero area while pressure sat well over threshold and the peak
        // work reading looked healthy; the peak was the first substep of contact and nothing after.
        //
        // Work-gating existed to stop destruction outrunning momentum transfer. ShedMass already
        // prevents that, continuously and by construction — the ledger share fell from 88.8% to
        // 2.8% — so the rate is free to follow pressure. A brake with nothing left to protect is
        // just a brake.
        //
        // It needs NO new free parameter. In 2D, energy-per-area and force-per-length are the same
        // units (M/T^2), so excess pressure over the acoustic impedance rho*c is a VELOCITY: the
        // speed the surface recedes at. Dimensionally forced, and it separates materials before any
        // tuning, since steel's impedance is 3.4x glass's.
        // SAME floored stiffness the contact itself uses. ContactCMin floors contact stiffness so a
        // soft material's contacts are no softer than a hard one's — impenetrability is kinematic —
        // which means ice and sandstone FEEL rock-like pressure. Dividing that by their unfloored
        // impedance made them erode 10x and 4x faster than rock: pressure floored in one place and
        // not the other. The floor has to appear on both sides or it is not a floor, it is a bias.
        float cPx = SimMath.Max(m.C * _tune.PxPerMetre, _tune.ContactCMin * _tune.PxPerMetre);
        float imp = SimMath.Max(1e-3f, (m.Rho / 1000f) * cPx);
        float depth = m.CrushRate * (excess / imp) * h;

        // A cell may not vanish inside one substep however violent the contact: the shed has to be
        // spread over enough substeps for its momentum to leave through the contact with it.
        if (depth <= 1e-5f) { DbgWant++; return 0f; }

        // ── v2: recede the surface around the contact ────────────────────────
        // Cells with a surface chord are dented through their records; the direction clip below
        // is kept only for cells with no chord to move (lone rubble, single-neighbour hangers).
        {
            float gotV2 = DentAt(c, other, depth, wpx, wpy);
            if (gotV2 >= 0f)
            {
                if (c == TraceCell)
                    TraceSink?.Invoke($"     dent: depth {depth,7:F3} removed {gotV2,7:F2}  area {_s.CellArea[c],7:F1}/{_s.CellArea0[c],7:F1} "
                        + $"shed {100f * (1f - _s.CellArea[c] / SimMath.Max(1f, _s.CellArea0[c])),5:F1}%");
                if (gotV2 <= 0f) { DbgRemoved++; return 0f; }
                DbgCarved++;
                return gotV2 / SimMath.Max(1f, _s.CellPerim[c] * 0.25f);
            }
        }

        // The yield speed gives how far the surface recedes; the area that corresponds to is the
        // recession times the contact length it happens over.
        // v1 only: the depth cap that kept a cell from vanishing in one substep. v2 guards on area.
        if (depth > _s.CellRad[c] * 0.25f) { DbgCapHit++; DbgCapExcess += depth / (_s.CellRad[c] * 0.25f); }
        depth = SimMath.Min(depth, _s.CellRad[c] * 0.25f);

        float lcw = SimMath.Max(1f, _s.CellPerim[c] * 0.25f);
        float wantArea = depth * lcw;

        // The load direction is world; the polygon is body-local.
        BodyTrig(bi, out float si, out float co);
        float lx = nx * co + ny * si;
        float ly = -nx * si + ny * co;

        float areaBefore = _s.CellArea[c];
        if (areaBefore <= 1e-6f) { DbgArea++; return 0f; }

        // ── A CLIP HAS TO BE WORTH MAKING ────────────────────────────────────
        // The rate law gives a recession per substep, which is hundredths of a pixel. Carving that
        // away immediately is not "continuous erosion", it is churn: a cut shallower than the
        // polygon's own feature size cannot shave a corner, so it deletes whichever side it runs
        // parallel to and relays a new one just behind, re-labelling the cell's edges every substep
        // for no visible change. Demand below the floor is HELD, not dropped, so the erosion rate is
        // untouched and only the grain of the geometry changes.
        float pending = _s.CellCarvePend[c] + wantArea;
        if (pending < _tune.CarveMinArea * areaBefore) { _s.CellCarvePend[c] = pending; DbgWant++; return 0f; }
        _s.CellCarvePend[c] = 0f;
        wantArea = pending;

        float removed = CarveCellByArea(c, lx, ly, wantArea);
        if (c == TraceCell)
            TraceSink?.Invoke($"     carve: depth {depth,7:F3} want {wantArea,7:F2} "
                + $"removed {removed,7:F2}  area {_s.CellArea[c],7:F1}/{_s.CellArea0[c],7:F1} "
                + $"shed {100f * (1f - _s.CellArea[c] / SimMath.Max(1f, _s.CellArea0[c])),5:F1}%");
        if (removed <= 0f) { DbgRemoved++; return 0f; }
        DbgCarved++;

        if (CarveLogging && CarveLogCount < CarveLogCell.Length)
        {
            CarveLogCell[CarveLogCount] = c;
            CarveLogNx[CarveLogCount] = nx; CarveLogNy[CarveLogCount] = ny;
            CarveLogArea[CarveLogCount] = removed;
            CarveLogCount++;
        }

        ShedMass(c, other, removed / areaBefore);

        // Realized depth, from the area actually taken — the request may have been clamped by the
        // bonded-edge guard, and only what really went may be credited against the overlap.
        float lc = SimMath.Max(1f, _s.CellPerim[c] * 0.25f);
        return removed / lc;
    }

    /// <summary>
    /// Removes the fraction <paramref name="f"/> of a cell's mass and hands its momentum to the
    /// partner cell, continuously.
    /// </summary>
    /// <remarks>
    /// <para><b>Density is held constant and mass leaves.</b> That is what makes carving spalling
    /// rather than compaction, and it is the same for every material — a receding surface is a dent
    /// whether the lost area became shed mass or higher density, so the eye cannot tell them apart
    /// and only one of them is cheap.</para>
    ///
    /// <para><b>The transfer is an inelastic collision between the shed mass and the partner
    /// CELL</b>, not its body. Handing it to the body would deliver it instantaneously to material
    /// arbitrarily far from the contact; writing into the partner's deviation field lets the bonds
    /// carry it outward at wave speed, which is the premise the model is built on. The inelastic
    /// form is what guarantees the exchange is dissipative: a straight handoff of <c>dm*v</c> would
    /// CREATE energy whenever the partner is already co-moving with the shedding cell.</para>
    ///
    /// <para>What the partner cannot take works out to <c>dm * v_partner</c> — precisely the
    /// momentum the departed mass would have had if it were moving with the material it left behind,
    /// which legitimately goes with it. That is the ledger's actual job.</para>
    /// </remarks>
    private void ShedMass(int c, int other, float f)
    {
        if (f <= 0f) return;
        if (f > 1f) f = 1f;

        float dm = _s.CellM[c] * f;
        if (dm <= 1e-9f) return;

        int bi = _s.CellBody[c], bo = _s.CellBody[other];
        if (bo < 0 || bo >= _s.BodyCount) return;

        CellVelocity(c, bi, out float vcx, out float vcy);
        CellVelocity(other, bo, out float vox, out float voy);

        float mo = _s.CellM[other];
        float mu = mo > 0f ? dm * mo / (dm + mo) : 0f;
        float jx = mu * (vcx - vox), jy = mu * (vcy - voy);

        // World -> the partner's body-local frame, exactly as ApplyPair does.
        BodyTrig(bo, out float so, out float coo);
        float ax = jx / mo, ay = jy / mo;
        _s.CellDvx[other] += ax * coo + ay * so;
        _s.CellDvy[other] += -ax * so + ay * coo;

        // Mass leaves at the cell's own velocity, so the cell's velocity does not change. Inertia
        // follows mass at fixed shape.
        float mNew = _s.CellM[c] - dm;
        if (mNew <= 1e-6f) mNew = 1e-6f;
        float scale = mNew / _s.CellM[c];
        dm = _s.CellM[c] - mNew;                  // what actually left, after the floor
        _s.CellM[c] = mNew;

        // THE BODY LOSES IT TOO, immediately. BodyM is otherwise only re-derived from live cells at
        // a topology boundary, so between now and then BodyKineticEnergy would still be counting
        // mass that has gone — while the ledger counts it as well. That double count is not subtle:
        // it read as energy CREATION, 113% of the initial, which the guardrail caught at once.
        // Inertia follows mass at fixed shape; RecomputeBody sets both absolutely later, so this
        // cannot compound with it.
        if (_s.BodyM[bi] > dm)
        {
            float bScale = (_s.BodyM[bi] - dm) / _s.BodyM[bi];
            _s.BodyM[bi] -= dm;
            _s.BodyI[bi] = SimMath.Max(1f, _s.BodyI[bi] * bScale);
        }
        _s.CellIm[c] = 1f / mNew;
        _s.CellIc[c] *= scale;
        _s.CellIic[c] = 1f / SimMath.Max(1e-9f, _s.CellIc[c]);

        // ── THE DUST COMPACTS INTO THE DENT ──────────────────────────────────
        // What the partner did not take, dm·v_c − j, used to go to the ledger: the dust left the
        // simulation at the partner's speed, so the partner was slowed by the dust it made and the
        // body that lost the material barely felt it — 35% of momentum in the ledger at 900 px/s,
        // 90% at 4000. The displaced material does not leave; it is pressed into the crater. So the
        // remainder is delivered to the shedding cell, which is exactly conservative in-sim at any
        // shed size — the reason the old depth cap is no longer needed. The energy the dust had at
        // v_c and does not have at the cell's speed is dissipation, which is what crushing is.
        float rx = dm * vcx - jx, ry = dm * vcy - jy;

        // The crater floor cannot be pushed faster than the impactor is closing on it: an inelastic
        // push ends at co-motion, not beyond. Without this, a cell shedding nearly all of itself in
        // one substep (mNew at its floor) would take the whole remainder as an absurd velocity —
        // measured as a NaN at 4000 px/s. What it cannot absorb is dust that was flung, and leaves.
        float dvMax = SimMath.Hypot(vox - vcx, voy - vcy);
        float dvAsk = SimMath.Hypot(rx, ry) / mNew;
        if (dvAsk > dvMax)
        {
            float keep = dvMax / dvAsk;
            ExportedPx += rx * (1f - keep); ExportedPy += ry * (1f - keep);
            rx *= keep; ry *= keep;
        }

        BodyTrig(bi, out float sc, out float cc);
        float bx = rx / mNew, by = ry / mNew;
        _s.CellDvx[c] += bx * cc + by * sc;
        _s.CellDvy[c] += -bx * sc + by * cc;
        ExportedKe += 0.5f * dm * (vcx * vcx + vcy * vcy)
                      - (jx * vox + jy * voy + (jx * jx + jy * jy) / (2f * mo))
                      - (rx * vcx + ry * vcy + (rx * rx + ry * ry) / (2f * mNew));   // what the cell gained
        DustMass += dm;
        ShedArea += f;
        MarkDirty(bi);
    }

    /// <summary>A cell's full world velocity: body translation, w x r, and its own deviation.</summary>
    private void CellVelocity(int c, int b, out float vx, out float vy)
    {
        BodyTrig(b, out float si, out float co);
        float rx = _s.CellRx[c] * co - _s.CellRy[c] * si;
        float ry = _s.CellRx[c] * si + _s.CellRy[c] * co;
        float w = _s.BodyW[b];
        vx = _s.BodyVx[b] - w * ry + _s.CellDvx[c] * co - _s.CellDvy[c] * si;
        vy = _s.BodyVy[b] + w * rx + _s.CellDvx[c] * si + _s.CellDvy[c] * co;
    }

    /// <summary>
    /// True when breaking bond <paramref name="k"/> would leave cell <paramref name="c"/> with no
    /// unbroken bond while it is still buried — surrounded by live cells of its own body.
    /// </summary>
    private bool WouldIsolateBuried(int c, int k)
    {
        int aoff = _s.AdjOff[c], alen = _s.AdjLen[c];
        for (int j = 0; j < alen; j++)
        {
            int o = _s.AdjBond[aoff + j];
            if (o == k || _s.BondBroken[o]) continue;
            return false;                       // another bond survives; nothing to worry about
        }
        return !HasFreeEdge(c);                 // last bond, and no side of it faces open space
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

            // ── A CRACK ADVANCES ALONG SIDES, NOT THROUGH CELLS ──────────────
            // Separation is allowed only where the side this bond occupies touches a side that is
            // already open. A crack tip is a vertex; the next side it can take is one sharing that
            // vertex.
            //
            // Testing whether the CELL is at a surface — which is what CellSurf/CellCracked did —
            // is far too weak. It licenses every bond of that cell, including ones buried on the far
            // side, so a crack tunnels inward and interior cells fall out of bodies that still
            // enclose them. And it cannot express the thing that matters: WHICH side is exposed.
            bool atSurface = SideTouchesSurface(a, k) || SideTouchesSurface(b, k);
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
                OpenBondSides(k);
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
