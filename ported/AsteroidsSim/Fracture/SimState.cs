using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Per-cell boolean state, packed into one byte.
/// </summary>
/// <remarks>
/// Room is deliberately left in the high bits: further per-cell boolean state belongs here rather
/// than in yet more parallel arrays.
/// </remarks>
[Flags]
public enum CellFlag : byte
{
    None = 0,
    /// <summary>Removed from the simulation. The hottest early-out in the solver.</summary>
    Dead = 1 << 0,
    /// <summary>Built as a single cell: a legitimate pebble, never dust.</summary>
    Solo = 1 << 1,
}

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

    /// <summary>
    /// Material identity, one byte indexing <see cref="MatTable"/>.
    /// </summary>
    /// <remarks>
    /// A static TAG, not a per-cell property set. Density is not live — carving holds it constant and
    /// sheds mass — so <c>BodyRho</c>/<c>BodyCpx</c> stay as they are and the contact compliance is
    /// untouched. Only the comminution parameters read the table, which is a few hundred bytes and
    /// permanently cache-resident. This is what lets one body hold cells of different materials.
    /// </remarks>
    public byte[] CellMat = Array.Empty<byte>();

    /// <summary>Area at build. Carving is measured against it — see the material's shed limit.</summary>
    public float[] CellArea0 = Array.Empty<float>();

    /// <summary>Area a cell has been asked to shed but that was too small to be worth a clip yet.</summary>
    /// <remarks>
    /// The rate law yields a recession per <i>substep</i>, which is a couple of hundredths of a
    /// pixel. Clipping each of those immediately is what made carving churn: a cut that shallow
    /// cannot shave a corner, it replaces a whole side with a parallel one just behind it, and every
    /// such replacement re-labels the polygon's edges. Holding the demand until it is worth a real
    /// cut keeps the request continuous while the geometry changes in visible steps.
    /// </remarks>
    public float[] CellCarvePend = Array.Empty<float>();

    /// <summary>
    /// The Voronoi seed this cell was grown from, in the SAME cell-local frame as
    /// <see cref="PolyX"/> — i.e. relative to the cell centroid.
    /// </summary>
    /// <remarks>
    /// <para>A cell's shared edge with a neighbour lies on the perpendicular bisector of their two
    /// SEEDS, which is not the bisector of their centroids: a Voronoi cell's centroid is not its
    /// seed. Carving needs that plane exactly, to guarantee it can never eat into an edge a bonded
    /// neighbour also owns, so the seed has to survive rather than be re-derived.</para>
    /// <para>Stored cell-local so it rides along with the geometry: carving re-centres the polygon on
    /// its new centroid and shifts the seed by the same amount, and a body re-centring moves
    /// <c>CellR</c> without touching either.</para>
    /// </remarks>
    public float[] CellSeedX = Array.Empty<float>();
    public float[] CellSeedY = Array.Empty<float>();

    // live
    public float[] CellRx = Array.Empty<float>();     // rest offset in the body frame
    public float[] CellRy = Array.Empty<float>();
    // NO realized displacement. Cells sit exactly at their rest offsets: deformation is bookkept on
    // the bonds as stretch and never becomes visible or collidable geometry, so a cell's body-local
    // polygon is CellR + q and two cells sharing a corner agree on it by construction.

    public float[] CellDvx = Array.Empty<float>();    // deviation velocity field
    public float[] CellDvy = Array.Empty<float>();
    public float[] CellDw = Array.Empty<float>();
    public int[] CellBody = Array.Empty<int>();
    public CellFlag[] CellFlags = Array.Empty<CellFlag>();
    public int[] CellTouch = Array.Empty<int>();      // last tick this cell was in a contact
    public int[] CellBorn = Array.Empty<int>();       // tick it became a lone single, or int.MinValue

    // Flag accessors. Aggressively inlined because Dead is tested in the innermost loop of the
    // narrow phase, the bond passes and every topology walk — a call there would be a real cost.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Dead(int c) => (CellFlags[c] & CellFlag.Dead) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Solo(int c) => (CellFlags[c] & CellFlag.Solo) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFlag(int c, CellFlag f, bool on)
    {
        if (on) CellFlags[c] |= f;
        else CellFlags[c] &= ~f;
    }

    // scratch, rebuilt every substep — never snapshotted
    public float[] CellPx = Array.Empty<float>();
    public float[] CellPy = Array.Empty<float>();

    // ── cell polygons, cell-local and centroid-relative ──────────────────────
    //
    // NO LONGER BAKED: carving clips these in place, so they are live state and are snapshotted and
    // fingerprinted like anything else that changes.
    //
    // PACKED WITH SLACK, not a uniform stride. Clipping a convex polygon by a half-plane can ADD a
    // vertex — cut off exactly one corner and you drop 1 but gain 2 crossings — and because this is
    // one flat array shared by every cell, a cell that outgrows its span would write into its
    // NEIGHBOUR's vertices. Silently: no crash, just another cell's collider quietly becoming wrong.
    //
    // A uniform stride would fix that too, but measured build counts run 3..9 vertices with a mode
    // of 6, so a stride sized for the worst case wastes about half of every cache line the narrow
    // phase pulls in — and the narrow phase is the most expensive stage in the tick. Per-cell slack
    // keeps cells adjacent and costs PolySlack vertices each instead.
    //
    // Growth is rarer than it looks: re-carving in the SAME direction is vertex-neutral, because the
    // deeper cut removes the two endpoints of the face the previous cut left and adds two crossings.
    // Only a genuinely new carve direction can grow a cell, and then by at most one.
    public const int PolySlack = 4;

    public int[] PolyOff = Array.Empty<int>();
    public int[] PolyLen = Array.Empty<int>();
    /// <summary>Slots reserved for this cell at <see cref="PolyOff"/>: its build length plus slack.</summary>
    public int[] PolyCap = Array.Empty<int>();

    /// <summary>
    /// Per EDGE, one of three things. Edge <c>i</c> runs from vertex <c>i</c> to <c>i+1</c>,
    /// indexed like <see cref="PolyX"/>.
    /// <list type="bullet">
    /// <item><c>&gt;= 0</c> — the bond across it: an interior side.</item>
    /// <item><see cref="SideReal"/> (−1) — <b>real surface</b>: the body's silhouette, or a face
    /// erosion has cut. Open to the world.</item>
    /// <item><see cref="SideCrack"/> (−2) — <b>crack surface</b>: opened by a bond breaking.</item>
    /// </list>
    /// The two kinds of open side are distinguished because a crack that curls round and meets
    /// itself would ring-fence the cells inside it. A side whose BOTH ends touch only crack surface
    /// is that closing move, and refusing it is what keeps cracks from detaching interior material.
    /// </summary>
    /// <remarks>
    /// <para>Maintained, not re-derived. It was computed geometrically at first — test each edge
    /// against each bonded neighbour's seed bisector — and that is wrong as soon as anything moves:
    /// carving replaces one endpoint of a shared edge with a new vertex on the carve plane, the
    /// "both endpoints lie on the bisector" test then fails, and the edge is reported FREE. Interior
    /// sides were shed and gaps opened inside solid bodies.</para>
    ///
    /// <para>Surface is a property of a SIDE, not of a cell. A cell being "at a surface" says
    /// nothing about WHICH of its sides is exposed, and that is the thing every consumer actually
    /// needs: a crack may only advance along a side adjacent to one already open, and carving may
    /// only cut a side facing open space.</para>
    ///
    /// <para>Written at build, where geometry is pristine; at bond break, where two shared sides
    /// become surface at once; and remapped through the clip when carving changes the vertex list.
    /// </para>
    /// </remarks>
    public short[] PolyBond = Array.Empty<short>();

    /// <summary>Per polygon slot: the touch record for the side starting here, or −1 if none.</summary>
    public short[] SideTouch = Array.Empty<short>();

    // ── touch records: adjacency as a first-class thing ──────────────────────
    //
    // ONE record per pair of cells that meet, shared by both — not one per cell. This is the entity
    // the model was missing. Bonds were standing in for it, and a bond is a MECHANICAL object that
    // can break or be skipped for being too short, while adjacency is a TOPOLOGICAL fact. Every
    // classification bug traced to that conflation: cells touching with no bond read as surface,
    // broken bonds read as surface while the cells still touched, and the two cells kept private
    // copies of a side they share with nothing forcing them to agree.
    //
    // The shared segment is stored as an INTERVAL along the seed bisector rather than as two points.
    // Both cells' copies of a side lie on that line by construction, a half-plane clip of a convex
    // polygon truncates a side from one end so the overlap is always contiguous, and the line itself
    // is stable: seeds are held cell-local and shifted with the centroid, so their body-frame
    // position survives carving, and a body re-centring moves both cells equally so t is unchanged.
    // Two floats instead of four, and the shared length is single-valued — so the two cells
    // disagreeing about it is not merely absent but unrepresentable.
    public int[] TouchA = Array.Empty<int>();
    public int[] TouchB = Array.Empty<int>();
    /// <summary>The bond along this adjacency, or −1 when there is none.</summary>
    public short[] TouchBond = Array.Empty<short>();
    /// <summary>Shared extent along the bisector, signed from its midpoint.</summary>
    public float[] TouchT0 = Array.Empty<float>();
    public float[] TouchT1 = Array.Empty<float>();

    /// <summary>The span as built — immutable. What a neighbour has receded from is <c>[T1, S1]</c>.</summary>
    /// <remarks>
    /// Carving v2 narrows <c>T0/T1</c> from an exposed end. The bare stretch a cell then presents
    /// along this line — the part its neighbour no longer covers — is the difference between the
    /// built span and the current one, so the built span has to be kept.
    /// </remarks>
    public float[] TouchS0 = Array.Empty<float>();
    public float[] TouchS1 = Array.Empty<float>();

    /// <summary>Which ends of the record lie on open surface: bit 0 for the T0 end, bit 1 for T1.</summary>
    /// <remarks>
    /// A surface vertex IS the exposed end of a record, and erosion moves it inward along the record.
    /// Set at build for ends on the body outline; set on propagation when a record is spent and the
    /// interior vertex it ended at becomes surface.
    /// </remarks>
    public byte[] TouchOpen = Array.Empty<byte>();
    public const byte TouchOpen0 = 1, TouchOpen1 = 2;

    public int TouchCount;

    public void EnsureTouch(int n)
    {
        Grow(ref TouchA, n); Grow(ref TouchB, n); Grow(ref TouchBond, n);
        Grow(ref TouchT0, n); Grow(ref TouchT1, n);
        Grow(ref TouchS0, n); Grow(ref TouchS1, n); Grow(ref TouchOpen, n);
    }

    /// <summary>The other cell of a touch record.</summary>
    public int TouchOther(int r, int c) => TouchA[r] == c ? TouchB[r] : TouchA[r];

    /// <summary>Shared length this record currently describes.</summary>
    public float TouchLen(int r) => Math.SimMath.Max(0f, TouchT1[r] - TouchT0[r]);

    public const short SideReal = -1;
    public const short SideCrack = -2;

    /// <summary>
    /// Touching a live cell of the same body, but carrying no bond — the shared edge was shorter
    /// than <c>MinSharedEdge</c> so none was built.
    /// </summary>
    /// <remarks>
    /// Not surface. There is material on the other side, so nothing may crack from it and nothing
    /// may carve it. Without this state such an edge falls through as real surface, because the
    /// labelling walks bonds and there is no bond to find — and then cracks start from specks in
    /// the middle of a solid body.
    /// </remarks>
    public const short SideSealed = -3;
    public float[] PolyX = Array.Empty<float>();
    public float[] PolyY = Array.Empty<float>();
    public int PolyCount;

    public int CellCount;

    // ── material table ───────────────────────────────────────────────────────
    //
    // Per SimState rather than static, for two reasons. Static mutable state inside the tick is a
    // determinism hazard and cannot be snapshotted (PORT_PLAN §6 rule 5). And a fixed global table
    // makes ad-hoc material VARIANTS impossible: a tuning tool that nudges one parameter would have
    // its variant silently resolve back to the canonical entry, which is exactly what happened when
    // ids were looked up by name — every row of a calibration sweep came out identical.
    //
    // Registration appends on first sight and returns the existing id for an exact match, so a
    // variant is a distinct material and a repeat is free.
    public Material[] MatTable = new Material[8];
    public int MatCount;

    /// <summary>The material of cell <paramref name="c"/>.</summary>
    public ref readonly Material Mat(int c) => ref MatTable[CellMat[c]];

    public byte RegisterMaterial(in Material m)
    {
        for (int i = 0; i < MatCount; i++)
            if (MatTable[i].SameAs(m)) return (byte)i;
        if (MatCount >= MatTable.Length)
        {
            if (MatCount >= 255) return 0;                 // pathological; keep the sim running
            var bigger = new Material[MatTable.Length * 2];
            Array.Copy(MatTable, bigger, MatCount);
            MatTable = bigger;
        }
        MatTable[MatCount] = m;
        return (byte)MatCount++;
    }

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

    /// <summary>Cohesive softening ratio for THIS bond, trims already applied.</summary>
    /// <remarks>
    /// Per bond rather than per body because a body may be built from more than one material, and a
    /// bond between two of them fails as the more brittle side does. Baked at build: the toughness
    /// trim is reset-on-change in the viewer, so there is nothing live to track.
    /// </remarks>
    public float[] BondChi = Array.Empty<float>();

    // live
    public float[] BondNx = Array.Empty<float>();     // axis, re-derived on split and on carve
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
    public float[] BodyCellSize = Array.Empty<float>(); // sqrt(grain): per body, so scales can mix

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
        Grow(ref CellMat, n); Grow(ref CellArea0, n); Grow(ref CellCarvePend, n);
        Grow(ref CellSeedX, n); Grow(ref CellSeedY, n);
        Grow(ref CellRx, n); Grow(ref CellRy, n);
        Grow(ref CellDvx, n); Grow(ref CellDvy, n); Grow(ref CellDw, n);
        Grow(ref CellBody, n); Grow(ref CellFlags, n);
        Grow(ref CellTouch, n); Grow(ref CellBorn, n);
        Grow(ref CellPx, n); Grow(ref CellPy, n);
        Grow(ref PolyOff, n); Grow(ref PolyLen, n); Grow(ref PolyCap, n);
        Grow(ref AdjOff, n); Grow(ref AdjLen, n);
    }

    public void EnsurePoly(int n)
    {
        Grow(ref PolyX, n); Grow(ref PolyY, n); Grow(ref PolyBond, n); Grow(ref SideTouch, n);
    }


    public void EnsureBonds(int n)
    {
        Grow(ref BondA, n); Grow(ref BondB, n); Grow(ref BondLen, n); Grow(ref BondStr, n);
        Grow(ref BondK0, n); Grow(ref BondKa0, n); Grow(ref BondS0, n);
        Grow(ref BondChi, n);
        Grow(ref BondNx, n); Grow(ref BondNy, n);
        Grow(ref BondRax, n); Grow(ref BondRay, n); Grow(ref BondRbx, n); Grow(ref BondRby, n);
        Grow(ref BondSn, n); Grow(ref BondSt, n); Grow(ref BondSa, n);
        Grow(ref BondDmg, n); Grow(ref BondLmax, n);
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
        Grow(ref BodyRho, n); Grow(ref BodyCpx, n); Grow(ref BodyCellSize, n);
        Grow(ref BodyCellOff, n); Grow(ref BodyCellLen, n);
        Grow(ref BodyBondOff, n); Grow(ref BodyBondLen, n);
    }

    public void EnsureBodyCells(int n) => Grow(ref BodyCells, n);
    public void EnsureBodyBonds(int n) => Grow(ref BodyBonds, n);

    public void Clear()
    {
        CellCount = 0; BondCount = 0; BodyCount = 0; PolyCount = 0; MatCount = 0; TouchCount = 0;
        BodyCellsCount = 0; BodyBondsCount = 0; AdjCount = 0;
        Tick = 0; Substep = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  topology
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds per-body bond lists and per-cell adjacency from the surviving bonds, scanning bonds
    /// in index order. That scan order is what fixes the order of every later Gauss-Seidel sweep
    /// so it is part of the determinism contract.
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
            if (Dead(a)) continue;
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
            if (Dead(a)) continue;
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
        for (int c = 0; c < CellCount; c++) if (!Dead(c)) n++;
        return n;
    }
}
