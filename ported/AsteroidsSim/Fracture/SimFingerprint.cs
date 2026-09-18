using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Hashes the live simulation state.
/// </summary>
/// <remarks>
/// <para>Two jobs. Under lockstep this is the periodic desync check — a few bytes on the wire every
/// thirty-two ticks, against a state far too large to send. In development it is the regression
/// detector: two runs of the same build must agree, three platforms must agree in CI, and once the
/// parallel job system lands, running with one chunk and with N chunks must agree too.</para>
///
/// <para><b>Only live state is hashed.</b> Baked fields — masses, rest polygon geometry, bond
/// stiffness, material constants — are a pure function of the build seed and the content tables, so
/// they cannot diverge without the build itself having diverged, and including them would only slow
/// the hash. Scratch fields (world positions, skinning caches, the trig caches) are excluded for the
/// same reason in reverse: they are recomputed from live state every substep, so hashing them would
/// report a difference that does not exist.</para>
///
/// <para>Iteration is strictly by index, so the hash is order-stable by construction.</para>
/// </remarks>
public static class SimFingerprint
{
    public static Fnv128 Compute(SimState s, bool withRecords = true)
    {
        var h = Fnv128.Create();

        h.Add(s.BodyCount);
        h.Add(s.CellCount);
        h.Add(s.BondCount);

        for (int b = 0; b < s.BodyCount; b++)
        {
            h.AddFloat(s.BodyX[b]); h.AddFloat(s.BodyY[b]); h.AddFloat(s.BodyRot[b]);
            h.AddFloat(s.BodyVx[b]); h.AddFloat(s.BodyVy[b]); h.AddFloat(s.BodyW[b]);
            h.AddFloat(s.BodyWPrev[b]); h.AddFloat(s.BodyAlpha[b]);
            h.AddFloat(s.BodyM[b]); h.AddFloat(s.BodyI[b]);
        }

        for (int c = 0; c < s.CellCount; c++)
        {
            h.AddFloat(s.CellRx[c]); h.AddFloat(s.CellRy[c]);
            h.AddFloat(s.CellCrush[c]);
            h.AddFloat(s.CellDvx[c]); h.AddFloat(s.CellDvy[c]); h.AddFloat(s.CellDw[c]);
            h.Add(s.CellBody[c]);
            // One value covers Dead/Solo/Surf/Cracked and any bit added later, and being a single
            // field it cannot carry struct padding into the hash.
            h.Add((int)s.CellFlags[c]); h.Add(s.CellMat[c]); h.AddFloat(s.CellArea0[c]);
            h.AddFloat(s.CellCarvePend[c]);
            h.Add(s.CellTouch[c]); h.Add(s.CellBorn[c]);

            // Geometry is LIVE now: carving clips these in place, so the polygon is part of the
            // state a desync can differ in. Only the used prefix is hashed — the slack slots past
            // PolyLen hold whatever a previous, longer polygon left there and are not state.
            h.Add(s.PolyLen[c]);
            int off = s.PolyOff[c];
            for (int v = 0; v < s.PolyLen[c]; v++)
            {
                h.AddFloat(s.PolyX[off + v]); h.AddFloat(s.PolyY[off + v]);
                h.Add(s.PolyBond[off + v]);
            }
            h.AddFloat(s.CellSeedX[c]); h.AddFloat(s.CellSeedY[c]);
            h.AddFloat(s.CellArea[c]); h.AddFloat(s.CellPerim[c]); h.AddFloat(s.CellRad[c]);
            h.AddFloat(s.CellM[c]); h.AddFloat(s.CellIc[c]);
        }

        // Touch records are state: ClassifySide reads them, so carving and the crack rule do, so
        // the physics does. They were missing here — a desync in a span would not have moved the
        // hash until it had moved a polygon.
        if (withRecords)
        {
        h.Add(s.TouchCount);
        for (int r = 0; r < s.TouchCount; r++)
        {
            h.Add(s.TouchA[r]); h.Add(s.TouchB[r]); h.Add(s.TouchBond[r]);
            h.AddFloat(s.TouchT0[r]); h.AddFloat(s.TouchT1[r]);
            h.AddFloat(s.TouchS0[r]); h.AddFloat(s.TouchS1[r]);
            h.Add(s.TouchOpen[r]);
        }
        }

        for (int k = 0; k < s.BondCount; k++)
        {
            h.AddFloat(s.BondSn[k]); h.AddFloat(s.BondSt[k]); h.AddFloat(s.BondSa[k]);
            h.AddFloat(s.BondDmg[k]); h.AddFloat(s.BondLmax[k]);
            h.AddFloat(s.BondNx[k]); h.AddFloat(s.BondNy[k]);
            h.AddFloat(s.BondRax[k]); h.AddFloat(s.BondRay[k]);
            h.AddFloat(s.BondRbx[k]); h.AddFloat(s.BondRby[k]);
            h.Add(s.BondBroken[k]); h.Add(s.BondMode[k]);
        }

        return h;
    }

    public static string Hex(SimState s) => Compute(s).ToHex();

    /// <summary>The pre-v2 field set, for proving a change left the simulation itself untouched.</summary>
    public static string HexWithoutRecords(SimState s) => Compute(s, withRecords: false).ToHex();

    /// <summary>The 32-bit form sent over the wire for periodic desync detection.</summary>
    public static uint Short(SimState s) => Compute(s).ToUInt32();
}
