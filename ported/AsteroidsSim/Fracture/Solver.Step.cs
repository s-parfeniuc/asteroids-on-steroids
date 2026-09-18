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

        // OPEN A FRESH CACHE EPOCH BEFORE ANYTHING READS GEOMETRY.
        // The skinned polygons and the cell-local vertex transforms are cached per substep, and the
        // trig cache per rotation epoch. Both were stamped during the previous tick's last substep —
        // and AFTER that, the end-of-tick work ran: splits re-centre a body's cells, a plastic
        // rebake moves rest offsets and rotations, and the pose integrates. The manifold build below
        // then ran at the same substep number and read all of it back out of the cache, so the first
        // contacts of every tick following a topology change were built from where the geometry used
        // to be. It was found by a bound that could not be violated being violated: a cell whose
        // rest offset was (0,0) and whose polygon spans 18 px produced a vertex 214 px away, still
        // carrying the offset it had had inside the body it was cut out of.
        _s.Substep++;
        BumpRotEpoch();

        BuildPairs();
        Mark(SolverPhase.BuildPairs);
        BuildManifold();                           // once per tick, with a speculative margin
        Mark(SolverPhase.BuildContacts);

        for (int s = 0; s < sub; s++)
        {
            _s.Substep++;
            BumpRotEpoch();
            // Refresh while the manifold still describes the scene; re-derive it when it does not.
            // In a settled pile this rebuilds once a tick, which is the whole saving; under a fast
            // impactor it rebuilds every substep, which is what correctness there costs.
            if (ManifoldDrift() > _mfDriftLimit)
            {
                BuildManifold();
                C.ManifoldRebuilds++;
                Mark(SolverPhase.BuildContacts);
            }
            else
            {
                RefreshManifold();
                Mark(SolverPhase.RefreshContacts);
            }
            ApplyInertialLoads(h);                 // before the solve: these are loads
            Mark(SolverPhase.Inertial);
            BondForces(h);
            Mark(SolverPhase.BondForces);
            for (int i = 0; i < _contactCount; i++) SolveContact(ref _contacts[i], h);
            ApplyCarving(h);                   // geometry changes AFTER every contact is solved
            Mark(SolverPhase.Contacts);
            BondIntegrate(h);
            Mark(SolverPhase.BondIntegrate);
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
            Mark(SolverPhase.Decompose);

            float rf = SimMath.Max(0f, 1f - _tune.Relax * h);
            for (int b = 0; b < _s.BodyCount; b++)
            {
                _s.BodyCpxAcc[b] = 0f; _s.BodyCpyAcc[b] = 0f; _s.BodyClAcc[b] = 0f;
            }

            if (!Par(16)) RealizeDeformation(h, rf, 0, _s.BodyCount, ref C);
            else RunOverBodies((chunk, lo, hi) => RealizeDeformation(h, rf, lo, hi, ref _chunkC[chunk]));

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
            BumpRotEpoch();                        // bodies just rotated

            Mark(SolverPhase.Realize);

            UpdateDamage(h);                       // marks affected bodies dirty
            Mark(SolverPhase.Damage);
        }

        // ── TOPOLOGY IS SETTLED ONCE PER TICK, NOT ONCE PER SUBSTEP ──────────
        // Breaking a bond stops it transmitting force immediately, which is the part that matters
        // physically; re-partitioning the body into fragments is bookkeeping and can wait for the
        // end of the tick. Doing it per substep meant up to nine component passes, nine membership
        // and adjacency rebuilds and nine broadphase rebuilds per tick, to reach a state that only
        // needed computing once. Separation velocity is not lost in the meantime: it lives in the
        // deviation field, and each fragment claims its share through DecomposeMotion below.
        RebuildBodies();
        DecomposeMotion();
        Mark(SolverPhase.Split);

        // NO INERTIA RECOMPUTE. It existed because realized displacement changed a body's shape
        // within a tick, so I drifted and angular momentum rather than omega had to be conserved —
        // and that coupling was itself a free spin-up loop until it was paid for. With deformation
        // gone the shape is fixed between topology events, so I is set once by RecomputeBody and
        // nothing here needs to touch it.

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
            if (_s.Dead(a) || _s.Dead(b)) continue;
            int ba = _s.CellBody[a], bb = _s.CellBody[b];
            if (ba < 0 || bb < 0 || ba >= _s.BodyCount || bb >= _s.BodyCount || ba == bb) continue;
            // Rubble only, both sides. Generalising this to all bodies was tried and reverted: it
            // is body-level positional response, which moves a body rather than letting the contact
            // resolve through the material, and that defeats the interpenetrating contact,
            // emergent participating mass and wave-speed load transfer the model exists for.
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

        Mark(SolverPhase.Settle);

        // The deep-overlap census that used to run here is gone with the criterion that read it.
        // It recorded each single-cell body's deepest contact partner, for a comminution trigger
        // keyed on penetration DEPTH sustained over several ticks. Pressure-gated comminution needs
        // neither: it selects on load rather than geometry, and it pays out to every contact by
        // weight rather than to one deepest partner, so a full sweep over contacts and cells per
        // tick disappears with it.
        if (ConvertDust())
        {
            RebuildBodies();                         // gated on mechanical decoupling
            DecomposeMotion();
        }
        Mark(SolverPhase.Dust);

        // Close the cache epoch as well as opening one. Everything above this line — the split, the
        // rebake, the dust conversion, the final pose — moved geometry after the last substep
        // stamped the skin and trig caches, so a reader that asks for a collider polygon between
        // now and the next tick would be handed the shape from before it all. That reader is not
        // hypothetical: it is the renderer, and the interpolating draw call that follows Step.
        _s.Substep++;
        BumpRotEpoch();
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
                if (_s.Dead(c)) continue;
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
                if (_s.Dead(c)) continue;
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

    /// <summary>
    /// Realizes the deformation field into <c>u</c>, applies the cap and the damping, for a
    /// contiguous range of bodies.
    /// </summary>
    /// <remarks>
    /// Iterating per body rather than over the global cell array leaves every accumulation into a
    /// cell, and into that cell's body reaction accumulators, in the same order: <c>BodyCells</c>
    /// holds a body's cells in ascending global index, and a cell only ever contributes to its own
    /// body. Same argument as <see cref="BondForces"/>, and the same reason it is dispatchable.
    /// </remarks>
    private void RealizeDeformation(float h, float rf, int bodyLo, int bodyHi, ref SolverCounters ctr)
    {
        for (int b = bodyLo; b < bodyHi; b++)
        {
            int cellOff = _s.BodyCellOff[b], cellLen = _s.BodyCellLen[b];
            for (int ci = 0; ci < cellLen; ci++)
            {
                int c = _s.BodyCells[cellOff + ci];
                if (_s.Dead(c)) continue;

                ctr.RealizeCells++;
                _s.CellDvx[c] *= rf;
                _s.CellDvy[c] *= rf;
                _s.CellDw[c] *= rf;
            }
        }
    }
    /// <summary>
    /// The furthest any collider vertex sits from its own cell's centre, in units of that cell's
    /// circumscribed radius.
    /// </summary>
    /// <remarks>
    /// With deformation removed this should be at most 1 by construction — the polygon is the rest
    /// polygon and nothing displaces it. It is kept because it is cheap and because it is the check
    /// that caught a stale per-substep cache feeding the first contacts of every tick geometry from
    /// before the previous tick's splits: the polygons were internally consistent and every
    /// conservation invariant stayed green, and only "this vertex is 200 px from the cell it belongs
    /// to" gave it away. A transform reading the wrong body or a stale pose would show up the same
    /// way.
    ///
    /// <para>Its companion, the shared-vertex gap, is gone: two cells sharing a Voronoi corner now
    /// compute it from the same rest data, so agreement is an identity rather than a measurement.</para>
    /// </remarks>
    public float MaxSkinRadiusRatio()
    {
        // Refresh the cached centres first. They are rebuilt inside the substep loop, and the body
        // pose integrates once more after the last substep, so reading CellPx straight after Step
        // compares a current-pose vertex against a centre one substep behind — which reads as a
        // vertex 1.7x its cell radius out and is an artefact of the measurement, not the geometry.
        UpdateCenters();

        float worst = 0f;
        var bufX = new float[64];
        var bufY = new float[64];
        for (int c = 0; c < _s.CellCount; c++)
        {
            if (_s.Dead(c) || _s.CellRad[c] <= 0f) continue;
            int len = _s.PolyLen[c];
            if (bufX.Length < len) { bufX = new float[len]; bufY = new float[len]; }
            int n = CellLocalPolygon(c, bufX, bufY);

            int b = _s.CellBody[c];
            BodyTrig(b, out float si, out float co);
            for (int v = 0; v < n; v++)
            {
                float wx = _s.BodyX[b] + bufX[v] * co - bufY[v] * si;
                float wy = _s.BodyY[b] + bufX[v] * si + bufY[v] * co;
                float r = SimMath.Hypot(wx - _s.CellPx[c], wy - _s.CellPy[c]) / _s.CellRad[c];
                if (r > worst) worst = r;
            }
        }
        return worst;
    }

}
