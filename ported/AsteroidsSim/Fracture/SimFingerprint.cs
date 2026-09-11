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
    public static Fnv128 Compute(SimState s)
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
            h.Add(s.CellDead[c]); h.Add(s.CellCracked[c]);
            h.Add(s.CellTouch[c]); h.Add(s.CellBorn[c]);
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

    /// <summary>The 32-bit form sent over the wire for periodic desync detection.</summary>
    public static uint Short(SimState s) => Compute(s).ToUInt32();
}
