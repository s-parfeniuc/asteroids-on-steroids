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
        // The world polygons are cached per substep and the trig cache per rotation epoch. Both were
        // stamped during the previous tick's last substep, and the end-of-tick work that followed —
        // splits re-centring a body's cells, dust removal, the final pose — moved geometry after
        // that. Without a new epoch the manifold build below would read the stale shapes back.
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
            if (!Par(16)) DampDeviation(rf, 0, _s.BodyCount, ref C);
            else RunOverBodies((chunk, lo, hi) => DampDeviation(rf, lo, hi, ref _chunkC[chunk]));

            for (int b = 0; b < _s.BodyCount; b++)
            {
                _s.BodyX[b] += _s.BodyVx[b] * h;
                _s.BodyY[b] += _s.BodyVy[b] * h;
                _s.BodyRot[b] += _s.BodyW[b] * h;
                _s.BodyAlpha[b] = (_s.BodyW[b] - _s.BodyWPrev[b]) / h;
                _s.BodyWPrev[b] = _s.BodyW[b];
            }
            BumpRotEpoch();                        // bodies just rotated

            Mark(SolverPhase.Damping);

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

        // ── RUBBLE SEPARATION ─────────────────────────────────────────────────
        // Two bond-less single cells in contact are nudged apart by position, translation only. This
        // is the only positional correction in the model: it never touches a bonded body, whose
        // overlap is left to the contact bias and to carving.
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
            float pen = ct.Depth - K.ContactSlop;
            if (pen <= 0f) continue;
            float push = SimMath.Min(pen, K.RubblePushMax) * K.RubblePushFraction;
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
        // dust conversion, the final pose — moved geometry after the last substep stamped the
        // polygon and trig caches, and the renderer reads collider polygons between ticks.
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
    /// Rayleigh damping of the deviation field (<see cref="SimTuning.Relax"/>), for a contiguous
    /// range of bodies. Each cell is scaled independently, so the pass is dispatchable per body.
    /// </summary>
    private void DampDeviation(float rf, int bodyLo, int bodyHi, ref SolverCounters ctr)
    {
        for (int b = bodyLo; b < bodyHi; b++)
        {
            int cellOff = _s.BodyCellOff[b], cellLen = _s.BodyCellLen[b];
            for (int ci = 0; ci < cellLen; ci++)
            {
                int c = _s.BodyCells[cellOff + ci];
                if (_s.Dead(c)) continue;

                ctr.DampCells++;
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
    /// At most 1 by construction: the polygon is the rest polygon and nothing displaces it. A larger
    /// value means geometry is being read from a stale cache or the wrong pose — a failure that
    /// leaves every conservation invariant green, which is why this is checked separately.
    /// </remarks>
    public float MaxVertexRadiusRatio()
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
