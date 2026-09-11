using System;
using System.Collections.Generic;

namespace AsteroidsSim.Fracture;

/// <summary>
/// The destruction simulation's entire mutable state, as parallel flat arrays.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> Struct-of-arrays throughout, with no references between records — a cell
/// points at its body by index, a bond at its cells by index. This is deliberate and is what makes
/// snapshotting a block copy of a known set of arrays rather than a graph walk, which is the
/// property lockstep resync and rollback both need. It is not an ECS; these are hand-sized tables,
/// per PORT_PLAN §3.5.</para>
///
/// <para><b>Two classes of field.</b> <i>Baked</i> fields are written once at construction and never
/// again (masses, rest geometry, bond stiffness, material constants). <i>Live</i> fields change every
/// tick. Only the live set has to be restored to rewind the simulation; the baked set is a pure
/// function of the build seed and the content tables, so it can be rebuilt rather than shipped. The
/// split is marked per group below because it decides the snapshot bill.</para>
///
/// <para><b>Ordering is part of the contract.</b> Cells, bonds and bodies are visited in index order
/// everywhere, contacts are solved in the order they are built, and adjacency is built by scanning
/// bonds in index order. The solver is Gauss-Seidel, so all of that is load-bearing: change an
/// iteration order and you change the result. Nothing here may be iterated through a hash container.
/// </para>
/// </remarks>
public sealed class SimState
{
    // ── cells ────────────────────────────────────────────────────────────────
    // baked
    public float[] CellM = Array.Empty<float>();      // mass
    public float[] CellIm = Array.Empty<float>();     // 1/mass
    public float[] CellIc = Array.Empty<float>();     // own rotational inertia
    public float[] CellIic = Array.Empty<float>();    // 1/CellIc
    public float[] CellArea = Array.Empty<float>();
    public float[] CellPerim = Array.Empty<float>();
    public float[] CellRad = Array.Empty<float>();    // max vertex distance from the cell centre
    public bool[] CellSolo = Array.Empty<bool>();     // built as a single cell: a pebble, never dust
    public bool[] CellSurf = Array.Empty<bool>();     // free boundary at build: cracks may separate here

    // live
    public float[] CellRx = Array.Empty<float>();     // rest offset in the body frame
    public float[] CellRy = Array.Empty<float>();
    // NO realized displacement. Cells sit exactly at their rest offsets: deformation is bookkept on
    // the bonds as stretch and never becomes visible or collidable geometry. That is what makes a
    // cell's body-local polygon constant (CellR + q), and therefore the collider constant, which in
    // turn removes shared-vertex skinning entirely — adjacent cells agree on a shared corner by
    // construction rather than by averaging.
    /// <summary>Accumulated comminution dose: stress-seconds of contact above the crush threshold.</summary>
    public float[] CellCrush = Array.Empty<float>();

    public float[] CellDvx = Array.Empty<float>();    // deviation velocity field
    public float[] CellDvy = Array.Empty<float>();
    public float[] CellDw = Array.Empty<float>();
    public int[] CellBody = Array.Empty<int>();
    public bool[] CellDead = Array.Empty<bool>();
    public bool[] CellCracked = Array.Empty<bool>();  // a break has opened this cell to a surface
    public int[] CellTouch = Array.Empty<int>();      // last tick this cell was in a contact
    public int[] CellBorn = Array.Empty<int>();       // tick it became a lone single, or -1

    // scratch, rebuilt every substep — never snapshotted
    public float[] CellPx = Array.Empty<float>();
    public float[] CellPy = Array.Empty<float>();

    // cell polygons, cell-local and centroid-relative (baked)
    public int[] PolyOff = Array.Empty<int>();
    public int[] PolyLen = Array.Empty<int>();
    public float[] PolyX = Array.Empty<float>();
    public float[] PolyY = Array.Empty<float>();
    public int PolyCount;

    public int CellCount;

    // ── shared-vertex groups ─────────────────────────────────────────────────
    // A Voronoi vertex is shared by ~3 cells. Every copy is drawn at the average of where its
    // sharing cells put it, which is what makes a gap between two bonded cells unrepresentable.
    public int GrpMemberCount;

    // ── bonds ────────────────────────────────────────────────────────────────
    // baked
    public int[] BondA = Array.Empty<int>();
    public int[] BondB = Array.Empty<int>();
    public float[] BondLen = Array.Empty<float>();    // shared-edge length
    public float[] BondStr = Array.Empty<float>();    // structure multiplier: the ONLY thing the
                                                      // authoring layer writes (Weibull/grain/flaws)
    public float[] BondK0 = Array.Empty<float>();     // axial/shear stiffness
    public float[] BondKa0 = Array.Empty<float>();    // bending stiffness
    public float[] BondS0 = Array.Empty<float>();     // peak (elastic) stretch
    public float[] BondSy0 = Array.Empty<float>();    // yield stretch

    // live
    public float[] BondNx = Array.Empty<float>();     // axis, re-derived on split and rebake
    public float[] BondNy = Array.Empty<float>();
    public float[] BondRax = Array.Empty<float>();    // anchor levers, likewise
    public float[] BondRay = Array.Empty<float>();
    public float[] BondRbx = Array.Empty<float>();
    public float[] BondRby = Array.Empty<float>();
    public float[] BondSn = Array.Empty<float>();     // elastic stretch, per mode
    public float[] BondSt = Array.Empty<float>();
    public float[] BondSa = Array.Empty<float>();
    public float[] BondDmg = Array.Empty<float>();    // cohesive damage, monotone in [0,1]
    public float[] BondLmax = Array.Empty<float>();   // history max of the equivalent stretch
    public float[] BondRate = Array.Empty<float>();   // strain rate, for rate-dependent strength
    public bool[] BondBroken = Array.Empty<bool>();
    public byte[] BondMode = Array.Empty<byte>();     // 0 none, 1 tension, 2 shear

    public int BondCount;

    // ── bodies ───────────────────────────────────────────────────────────────
    public float[] BodyX = Array.Empty<float>();
    public float[] BodyY = Array.Empty<float>();
    public float[] BodyRot = Array.Empty<float>();
    public float[] BodyVx = Array.Empty<float>();
    public float[] BodyVy = Array.Empty<float>();
    public float[] BodyW = Array.Empty<float>();
    public float[] BodyWPrev = Array.Empty<float>();
    public float[] BodyAlpha = Array.Empty<float>();
    public float[] BodyM = Array.Empty<float>();
    public float[] BodyI = Array.Empty<float>();
    public float[] BodyEulL = Array.Empty<float>();   // Euler load's fictitious angular momentum

    /// <summary>
    /// Set when a body loses a bond or a cell; cleared once the topology pass has processed it.
    /// A break can only disconnect its own body, so this is what keeps the component walk local.
    /// </summary>
    public bool[] BodyDirty = Array.Empty<bool>();

    // per-body material, baked at creation so materials with different wave speeds coexist
    public float[] BodyRho = Array.Empty<float>();
    public float[] BodyCpx = Array.Empty<float>();    // wave speed in px/s
    public float[] BodyChi = Array.Empty<float>();    // cohesive softening ratio
    public float[] BodyVCrit = Array.Empty<float>();
    public float[] BodyDuct = Array.Empty<float>();   // plastic strain capacity, fraction of a cell
    public float[] BodyCellSize = Array.Empty<float>(); // sqrt(grain): per body, so scales can mix
    public float[] BodyCrush = Array.Empty<float>();   // contact stress at which this body comminutes
    public float[] BodyCrushCap = Array.Empty<float>(); // stress-seconds above it before powder

    // transfer accumulators for the deformation cap (zeroed and consumed within a substep)
    public float[] BodyCpxAcc = Array.Empty<float>();
    public float[] BodyCpyAcc = Array.Empty<float>();
    public float[] BodyClAcc = Array.Empty<float>();

    public int BodyCount;

    // ── membership, rebuilt on split ─────────────────────────────────────────
    public int[] BodyCellOff = Array.Empty<int>();
    public int[] BodyCellLen = Array.Empty<int>();
    public int[] BodyCells = Array.Empty<int>();
    public int BodyCellsCount;

    public int[] BodyBondOff = Array.Empty<int>();
    public int[] BodyBondLen = Array.Empty<int>();
    public int[] BodyBonds = Array.Empty<int>();
    public int BodyBondsCount;

    // per-cell bond adjacency, rebuilt by Reindex in bond index order
    public int[] AdjOff = Array.Empty<int>();
    public int[] AdjLen = Array.Empty<int>();
    public int[] AdjBond = Array.Empty<int>();
    public int AdjCount;

    // ── clock ────────────────────────────────────────────────────────────────
    public int Tick;
    public int Substep;

    // ══════════════════════════════════════════════════════════════════════════
    //  capacity
    // ══════════════════════════════════════════════════════════════════════════

    private static void Grow<T>(ref T[] a, int needed)
    {
        if (a.Length >= needed) return;
        int cap = a.Length == 0 ? 64 : a.Length;
        while (cap < needed) cap <<= 1;
        Array.Resize(ref a, cap);
    }

    public void EnsureCells(int n)
    {
        Grow(ref CellM, n); Grow(ref CellIm, n); Grow(ref CellIc, n); Grow(ref CellIic, n);
        Grow(ref CellArea, n); Grow(ref CellPerim, n); Grow(ref CellRad, n);
        Grow(ref CellSolo, n); Grow(ref CellSurf, n);
        Grow(ref CellRx, n); Grow(ref CellRy, n);
        Grow(ref CellCrush, n); Grow(ref CellDvx, n); Grow(ref CellDvy, n); Grow(ref CellDw, n);
        Grow(ref CellBody, n); Grow(ref CellDead, n); Grow(ref CellCracked, n);
        Grow(ref CellTouch, n); Grow(ref CellBorn, n);
        Grow(ref CellPx, n); Grow(ref CellPy, n);
        Grow(ref PolyOff, n); Grow(ref PolyLen, n);
        Grow(ref AdjOff, n); Grow(ref AdjLen, n);
    }

    public void EnsurePoly(int n)
    {
        Grow(ref PolyX, n); Grow(ref PolyY, n);
    }


    public void EnsureBonds(int n)
    {
        Grow(ref BondA, n); Grow(ref BondB, n); Grow(ref BondLen, n); Grow(ref BondStr, n);
        Grow(ref BondK0, n); Grow(ref BondKa0, n); Grow(ref BondS0, n); Grow(ref BondSy0, n);
        Grow(ref BondNx, n); Grow(ref BondNy, n);
        Grow(ref BondRax, n); Grow(ref BondRay, n); Grow(ref BondRbx, n); Grow(ref BondRby, n);
        Grow(ref BondSn, n); Grow(ref BondSt, n); Grow(ref BondSa, n);
        Grow(ref BondDmg, n); Grow(ref BondLmax, n); Grow(ref BondRate, n);
        Grow(ref BondBroken, n); Grow(ref BondMode, n);
        Grow(ref AdjBond, 2 * n);
    }

    public void EnsureBodies(int n)
    {
        Grow(ref BodyX, n); Grow(ref BodyY, n); Grow(ref BodyRot, n);
        Grow(ref BodyVx, n); Grow(ref BodyVy, n); Grow(ref BodyW, n);
        Grow(ref BodyWPrev, n); Grow(ref BodyAlpha, n);
        Grow(ref BodyM, n); Grow(ref BodyI, n); Grow(ref BodyEulL, n);
        Grow(ref BodyDirty, n);
        Grow(ref BodyRho, n); Grow(ref BodyCpx, n); Grow(ref BodyChi, n);
        Grow(ref BodyVCrit, n); Grow(ref BodyDuct, n); Grow(ref BodyCellSize, n);
        Grow(ref BodyCrush, n); Grow(ref BodyCrushCap, n);
        Grow(ref BodyCpxAcc, n); Grow(ref BodyCpyAcc, n); Grow(ref BodyClAcc, n);
        Grow(ref BodyCellOff, n); Grow(ref BodyCellLen, n);
        Grow(ref BodyBondOff, n); Grow(ref BodyBondLen, n);
    }

    public void EnsureBodyCells(int n) => Grow(ref BodyCells, n);
    public void EnsureBodyBonds(int n) => Grow(ref BodyBonds, n);

    public void Clear()
    {
        CellCount = 0; BondCount = 0; BodyCount = 0; PolyCount = 0;
        BodyCellsCount = 0; BodyBondsCount = 0; AdjCount = 0;
        Tick = 0; Substep = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  topology
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds per-body bond lists and per-cell adjacency from the surviving bonds, scanning bonds
    /// in index order. That scan order is what fixes the order of every later Gauss-Seidel sweep
    /// (the rebake in particular), so it is part of the determinism contract.
    /// </summary>
    public long ReindexCalls;
    public long ReindexBondScans;

    public void Reindex()
    {
        ReindexCalls++;
        ReindexBondScans += 2L * BondCount;
        for (int b = 0; b < BodyCount; b++) BodyBondLen[b] = 0;
        for (int c = 0; c < CellCount; c++) AdjLen[c] = 0;

        // pass 1 — count
        for (int k = 0; k < BondCount; k++)
        {
            if (BondBroken[k]) continue;
            int a = BondA[k];
            if (CellDead[a]) continue;
            int body = CellBody[a];
            if (body < 0 || body >= BodyCount) continue;
            BodyBondLen[body]++;
            AdjLen[a]++;
            AdjLen[BondB[k]]++;
        }

        int off = 0;
        for (int b = 0; b < BodyCount; b++) { BodyBondOff[b] = off; off += BodyBondLen[b]; }
        BodyBondsCount = off;
        EnsureBodyBonds(off);

        int aoff = 0;
        for (int c = 0; c < CellCount; c++) { AdjOff[c] = aoff; aoff += AdjLen[c]; }
        AdjCount = aoff;
        Grow(ref AdjBond, System.Math.Max(1, aoff));

        // pass 2 — fill, using the length fields as running cursors
        for (int b = 0; b < BodyCount; b++) BodyBondLen[b] = 0;
        for (int c = 0; c < CellCount; c++) AdjLen[c] = 0;

        for (int k = 0; k < BondCount; k++)
        {
            if (BondBroken[k]) continue;
            int a = BondA[k], bb = BondB[k];
            if (CellDead[a]) continue;
            int body = CellBody[a];
            if (body < 0 || body >= BodyCount) continue;
            BodyBonds[BodyBondOff[body] + BodyBondLen[body]++] = k;
            AdjBond[AdjOff[a] + AdjLen[a]++] = k;
            AdjBond[AdjOff[bb] + AdjLen[bb]++] = k;
        }
    }

    /// <summary>Live (non-dead) cell count — the figure the split cache compares against.</summary>
    public int LiveCellCount()
    {
        int n = 0;
        for (int c = 0; c < CellCount; c++) if (!CellDead[c]) n++;
        return n;
    }
}
