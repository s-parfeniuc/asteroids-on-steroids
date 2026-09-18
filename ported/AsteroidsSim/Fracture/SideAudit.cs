using System;
using System.Text;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Audits the side classification — the answer to "which parts of this body are boundary" that
/// carving, cracking and comminution all read.
/// </summary>
/// <remarks>
/// <para><b>This is the specification, not a patch.</b> The current representation stores one
/// physical shared side TWICE, once in each of the two cells, at unrelated local indices, with
/// nothing enforcing that the two copies agree. Every defect in this area has lived in that gap, and
/// each individual fix looked correct in isolation while the classification stayed wrong. What was
/// missing is a statement of what correct means that a machine can check.</para>
///
/// <para>So the invariants below define the contract. They are written against the data model rather
/// than against any particular implementation of it, which means they remain the acceptance test for
/// a replacement representation — a cleaner structure is one that satisfies these by construction
/// instead of by maintenance.</para>
///
/// <para><b>Vocabulary</b>, since this has been a source of confusion. A <i>side</i> is the segment
/// from a cell's local vertex <c>i</c> to vertex <c>i+1</c>, labelled at slot <c>i</c> of
/// <c>PolyBond</c>. Sides are per CELL: a vertex of the Voronoi mesh is shared by about three cells,
/// but within one cell's closed polygon each vertex has exactly two incident sides, so the local
/// ordering is unambiguous. A <i>shared side</i> is the same physical segment as seen from each of
/// the two cells that meet along it — two records of one thing, which is the crux.</para>
/// </remarks>
internal static class SideAudit
{
    public enum Fault
    {
        /// <summary>A side names a bond that does not connect this cell.</summary>
        BondNotIncident,
        /// <summary>An unbroken bond has no side naming it in one of its two cells.</summary>
        BondHasNoSide,
        /// <summary>The two cells disagree about how long the side they share is.</summary>
        LengthDisagreement,
        /// <summary>BondLen no longer describes the geometry it was derived from.</summary>
        BondLenStale,
        /// <summary>A side names a bond but does not lie on that bond's seed bisector.</summary>
        SideOffBisector,
        /// <summary>A side is labelled open, but a live cell of the same body occupies it.</summary>
        OpenSideIsCovered,
        /// <summary>A side is labelled sealed, but nothing is actually across it.</summary>
        SealedSideIsBare,

        // ── touch-record invariants ──────────────────────────────────────────
        /// <summary>A side links a record that does not name this cell.</summary>
        TouchNotIncident,
        /// <summary>Both cells of a record must link a side to it.</summary>
        TouchUnlinked,
        /// <summary>The record's shared extent is empty or inverted.</summary>
        TouchEmpty,
        /// <summary>The shared extent is not contained by the side it describes.</summary>
        TouchOutsideSide,
        /// <summary>A record names a bond that does not connect the same two cells.</summary>
        TouchBondMismatch,
        /// <summary>A side has no record, but material of the same body is right across it.</summary>
        MissingTouchRecord,
        /// <summary>A side links a live record, but the record's span no longer overlaps the side.</summary>
        CoverageLost,

        // ── carving v2: records are the truth, the polygon is derived from them ──
        /// <summary>A side carries a record but its label does not say what the record says.</summary>
        LabelRecordMismatch,
        /// <summary>A record end is marked exposed but sits between two records in both cells, or vice versa.</summary>
        OpenBitWrong,
        /// <summary>The polygon vertex at a record end is not where the record puts it.</summary>
        EndOffVertex,
        /// <summary>A surface side of zero length survived — a spent interface left as a notch.</summary>
        ZeroLengthSurfaceSide,
    }

    public sealed class Report
    {
        public readonly long[] Counts = new long[Enum.GetValues<Fault>().Length];
        public int FirstTick = -1;
        public string FirstDetail = "";
        public long Total;
        public string? FirstUnlinked;

        /// <summary>Set to a list to retain every detail line, for narrowing to one cell.</summary>
        public System.Collections.Generic.List<string>? All;

        public void Add(int tick, Fault f, string detail)
        {
            Counts[(int)f]++;
            Total++;
            All?.Add($"{f}: {detail}");
            if (FirstTick >= 0) return;
            FirstTick = tick;
            FirstDetail = $"{f}: {detail}";
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Total} violations");
            if (Total > 0) sb.Append($", first at tick {FirstTick} — {FirstDetail}");
            sb.AppendLine();
            foreach (Fault f in Enum.GetValues<Fault>())
                if (Counts[(int)f] > 0) sb.AppendLine($"    {f,-20} {Counts[(int)f]}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Checks every invariant over the whole scene. Relative tolerances, so the same thresholds
    /// mean the same thing at any grain.
    /// </summary>
    public static void Audit(SimState s, Report r, int tick)
    {
        AuditTouch(s, r, tick);

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            if (len < 3) continue;
            int body = s.CellBody[c];
            float tol = SimMath.Max(1e-3f, s.CellRad[c] * 5e-3f);

            for (int v = 0; v < len; v++)
            {
                short k = s.PolyBond[off + v];
                if (k >= 0)
                {
                    // ── a side naming a bond must be a side of that bond ──────
                    if (k >= s.BondCount || (s.BondA[k] != c && s.BondB[k] != c))
                    {
                        r.Add(tick, Fault.BondNotIncident, $"cell {c} side {v} names bond {k}");
                        continue;
                    }
                    int o = s.BondA[k] == c ? s.BondB[k] : s.BondA[k];
                    if (!OnBisector(s, c, o, off, v, tol))
                        r.Add(tick, Fault.SideOffBisector,
                            $"cell {c} side {v} names bond {k} to cell {o} but is not on their bisector");
                }
                else if (k == SimState.SideReal || (k == SimState.SideCrack && !LiveRecord(s, c, off, v)))
                {
                    // ── a bare side must have nothing across it ───────────────
                    // Real surface, or a crack face whose neighbour has gone (record retired).
                    int cover = CoveringNeighbour(s, c, body, off, v, tol);
                    if (cover >= 0)
                        r.Add(tick, Fault.OpenSideIsCovered,
                            $"cell {c} side {v} is open but cell {cover} of the same body covers it");
                }
                else if (k == SimState.SideCrack)
                {
                    // ── a crack face with a live record has its neighbour across it ──
                    // The bond is broken but the material is not gone: that is what a crack IS.
                    // Counting these as "open but covered" made the fault mostly noise.
                    if (CoveringNeighbour(s, c, body, off, v, tol) < 0)
                        r.Add(tick, Fault.SealedSideIsBare,
                            $"cell {c} side {v} is a crack face with a live record but nothing is across it");
                }
                else if (k == SimState.SideSealed)
                {
                    if (CoveringNeighbour(s, c, body, off, v, tol) < 0)
                        r.Add(tick, Fault.SealedSideIsBare,
                            $"cell {c} side {v} is sealed but nothing is across it");
                }
            }
        }

        // ── the two copies of one shared side must agree ─────────────────────
        for (int k = 0; k < s.BondCount; k++)
        {
            if (s.BondBroken[k]) continue;
            int a = s.BondA[k], b = s.BondB[k];
            if (a < 0 || b < 0 || s.Dead(a) || s.Dead(b)) continue;

            float la = LabelledLength(s, a, k);
            float lb = LabelledLength(s, b, k);
            if (la <= 0f || lb <= 0f)
            {
                r.Add(tick, Fault.BondHasNoSide,
                    $"bond {k} ({a},{b}) has length {la:F3} in A and {lb:F3} in B");
                continue;
            }

            float tol = SimMath.Max(1e-2f, 0.02f * SimMath.Max(la, lb));
            if (SimMath.Abs(la - lb) > tol)
                r.Add(tick, Fault.LengthDisagreement,
                    $"bond {k} ({a},{b}) is {la:F3} long in A but {lb:F3} in B");

            float shared = SimMath.Min(la, lb);
            if (SimMath.Abs(s.BondLen[k] - shared) > SimMath.Max(1e-2f, 0.05f * s.BondLen[k]))
                r.Add(tick, Fault.BondLenStale,
                    $"bond {k} records length {s.BondLen[k]:F3} but the sides measure {shared:F3}");
        }
    }

    /// <summary>
    /// The touch-record invariants — the contract the new representation must satisfy.
    /// </summary>
    /// <remarks>
    /// Note what is NOT here: any check that the two cells agree about the shared length. Under this
    /// representation there is one interval, so disagreement is unrepresentable rather than merely
    /// absent. That is the point of the change, and the audit shrinking is the evidence for it.
    /// </remarks>
    /// <summary>
    /// The v2 invariants: the polygon is derived from the records, so every place the two could
    /// disagree is checked directly rather than inferred from geometry.
    /// </summary>
    private static void AuditRecordsV2(SimState s, Report r, int tick)
    {
        for (int rec = 0; rec < s.TouchCount; rec++)
        {
            int a = s.TouchA[rec], b = s.TouchB[rec];
            if (a < 0 || b < 0 || s.Dead(a) || s.Dead(b) || s.CellBody[a] != s.CellBody[b]) continue;

            // Body-local bisector, oriented A -> B like the solver's, so T means the same thing here.
            float sax = s.CellRx[a] + s.CellSeedX[a], say = s.CellRy[a] + s.CellSeedY[a];
            float sbx = s.CellRx[b] + s.CellSeedX[b], sby = s.CellRy[b] + s.CellSeedY[b];
            float dx = sbx - sax, dy = sby - say;
            float el = SimMath.Hypot(dx, dy);
            if (el < 1e-6f) continue;
            float ex = -dy / el, ey = dx / el;                  // along the bisector LINE, as TouchBisector
            float mx = 0.5f * (sax + sbx), my = 0.5f * (say + sby);

            short bond = s.TouchBond[rec];
            bool liveBond = bond >= 0 && bond < s.BondCount && !s.BondBroken[bond];
            bool onOutline0 = false, onOutline1 = false;

            for (int side = 0; side < 2; side++)
            {
                int c = side == 0 ? a : b;
                int off = s.PolyOff[c], len = s.PolyLen[c];
                float tol = SimMath.Max(1e-2f, s.CellRad[c] * 5e-3f);
                for (int v = 0; v < len; v++)
                {
                    if (s.SideTouch[off + v] != rec) continue;
                    int w = v + 1 == len ? 0 : v + 1;
                    int pv = v == 0 ? len - 1 : v - 1;      // the side ending at vertex v
                    int nv = w;                              // the side starting at vertex w

                    short want = liveBond ? bond : bond >= 0 ? SimState.SideCrack : SimState.SideSealed;
                    if (s.PolyBond[off + v] != want)
                        r.Add(tick, Fault.LabelRecordMismatch,
                            $"cell {c} side {v} carries record {rec} (bond {bond}{(liveBond ? "" : bond >= 0 ? " broken" : "")}) but is labelled {s.PolyBond[off + v]}");

                    // Which polygon vertex is which record end: by projection onto the bisector.
                    float u0 = (s.CellRx[c] + s.PolyX[off + v] - mx) * ex + (s.CellRy[c] + s.PolyY[off + v] - my) * ey;
                    float u1 = (s.CellRx[c] + s.PolyX[off + w] - mx) * ex + (s.CellRy[c] + s.PolyY[off + w] - my) * ey;
                    bool startIsT0 = SimMath.Abs(u0 - s.TouchT0[rec]) <= SimMath.Abs(u0 - s.TouchT1[rec]);
                    int vT0 = startIsT0 ? v : w, vT1 = startIsT0 ? w : v;
                    int adjT0 = startIsT0 ? pv : nv, adjT1 = startIsT0 ? nv : pv;   // the OTHER side at that vertex

                    for (int end = 0; end < 2; end++)
                    {
                        float tt = end == 0 ? s.TouchT0[rec] : s.TouchT1[rec];
                        int vert = end == 0 ? vT0 : vT1, adj = end == 0 ? adjT0 : adjT1;
                        float px = mx + tt * ex - s.CellRx[c], py = my + tt * ey - s.CellRy[c];
                        float gap = SimMath.Hypot(s.PolyX[off + vert] - px, s.PolyY[off + vert] - py);
                        if (gap > tol)
                            r.Add(tick, Fault.EndOffVertex,
                                $"cell {c} vertex {vert} is {gap:F3} px from record {rec}'s T{end} end");
                        bool outline = s.SideTouch[off + adj] < 0;
                        if (end == 0) onOutline0 |= outline; else onOutline1 |= outline;
                    }
                }
            }

            bool open0 = (s.TouchOpen[rec] & SimState.TouchOpen0) != 0, open1 = (s.TouchOpen[rec] & SimState.TouchOpen1) != 0;
            if (open0 != onOutline0)
                r.Add(tick, Fault.OpenBitWrong, $"record {rec} ({a},{b}) T0 end is {(open0 ? "marked exposed but sits between records" : "on the outline but not marked exposed")}");
            if (open1 != onOutline1)
                r.Add(tick, Fault.OpenBitWrong, $"record {rec} ({a},{b}) T1 end is {(open1 ? "marked exposed but sits between records" : "on the outline but not marked exposed")}");
        }

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            if (len <= 3) continue;
            for (int v = 0; v < len; v++)
            {
                if (s.SideTouch[off + v] >= 0) continue;
                int w = v + 1 == len ? 0 : v + 1;
                if (SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v], s.PolyY[off + w] - s.PolyY[off + v]) <= 0.05f)
                    r.Add(tick, Fault.ZeroLengthSurfaceSide, $"cell {c} side {v} is a zero-length surface side");
            }
        }
    }

    private static void AuditTouch(SimState s, Report r, int tick)
    {
        AuditRecordsV2(s, r, tick);
        for (int rec = 0; rec < s.TouchCount; rec++)
        {
            int a = s.TouchA[rec], b = s.TouchB[rec];
            if (a < 0 || b < 0) continue;            // retired: the adjacency ended
            if (s.Dead(a) || s.Dead(b)) continue;

            if (s.TouchT1[rec] - s.TouchT0[rec] <= 0f)
            {
                r.Add(tick, Fault.TouchEmpty,
                    $"record {rec} ({a},{b}) spans [{s.TouchT0[rec]:F3},{s.TouchT1[rec]:F3}]");
                continue;
            }

            short k = s.TouchBond[rec];
            if (k >= 0 && k < s.BondCount)
            {
                bool same = (s.BondA[k] == a && s.BondB[k] == b) || (s.BondA[k] == b && s.BondB[k] == a);
                if (!same)
                    r.Add(tick, Fault.TouchBondMismatch, $"record {rec} ({a},{b}) names bond {k}");
            }

            bool la = Linked(s, a, rec), lb = Linked(s, b, rec);
            if (!la || !lb)
            {
                string why = s.CellBody[a] != s.CellBody[b] ? "different bodies"
                           : (!la && !lb) ? "neither cell links it"
                           : "one cell lost its side";
                r.FirstUnlinked ??= $"tick {tick}: record {rec} ({a} body {s.CellBody[a]}, "
                                  + $"{b} body {s.CellBody[b]}) — {why}";
                r.Add(tick, Fault.TouchUnlinked, why);
            }
        }

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            for (int v = 0; v < len; v++)
            {
                short rec = s.SideTouch[off + v];
                if (rec < 0) continue;
                if (rec >= s.TouchCount || (s.TouchA[rec] != c && s.TouchB[rec] != c))
                {
                    r.Add(tick, Fault.TouchNotIncident, $"cell {c} side {v} links record {rec}");
                    continue;
                }
                if (s.TouchA[rec] < 0) continue;
                int o2 = s.TouchOther(rec, c);
                if (o2 < 0 || o2 >= s.CellCount || s.Dead(o2) || s.CellBody[o2] != s.CellBody[c]) continue;

                // The side links a live adjacency but the record's span has drifted clear of it, so
                // the side reports itself fully exposed while material is still across it.
                if (!Overlaps(s, c, rec, off, v))
                    r.Add(tick, Fault.CoverageLost,
                        $"cell {c} side {v} links live record {rec} (with {o2}) but the span misses it");
            }
        }
    }

    private static bool LiveRecord(SimState s, int c, int off, int v)
    {
        short rec = s.SideTouch[off + v];
        if (rec < 0 || rec >= s.TouchCount || s.TouchA[rec] < 0) return false;
        int o = s.TouchOther(rec, c);
        return o >= 0 && o < s.CellCount && !s.Dead(o) && s.CellBody[o] == s.CellBody[c];
    }

    /// <summary>
    /// Probes just outside each unlinked side and asks whether material of the same body is there.
    /// </summary>
    /// <remarks>
    /// Deliberately geometric and independent of <c>SideTouch</c>. Every other check reads the
    /// records, so a MISSING record is invisible to all of them — the side simply looks like real
    /// surface and everything agrees, wrongly. This is the only check that can catch adjacency that
    /// was never recorded, which is why it exists separately and why the audit passed while real
    /// surface was showing up inside solid bodies.
    /// </remarks>
    public static void AuditGeometry(SimState s, Report r, int tick)
    {
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            if (len < 3) continue;
            int body = s.CellBody[c];
            float probe = SimMath.Max(0.05f, s.CellRad[c] * 0.03f);

            for (int v = 0; v < len; v++)
            {
                if (s.SideTouch[off + v] >= 0) continue;
                int w = v + 1 == len ? 0 : v + 1;
                float x0 = s.PolyX[off + v], y0 = s.PolyY[off + v];
                float x1 = s.PolyX[off + w], y1 = s.PolyY[off + w];
                float dx = x1 - x0, dy = y1 - y0;
                float dl = SimMath.Hypot(dx, dy);
                if (dl < 1e-4f) continue;

                // Outward for a CCW polygon is to the right of the side direction.
                float px = s.CellRx[c] + 0.5f * (x0 + x1) + (dy / dl) * probe;
                float py = s.CellRy[c] + 0.5f * (y0 + y1) - (dx / dl) * probe;

                int cover = CellContaining(s, px, py, body, c);
                if (cover >= 0)
                    r.Add(tick, Fault.MissingTouchRecord,
                        $"cell {c} side {v} has no record but cell {cover} is across it");
            }
        }
    }

    private static int CellContaining(SimState s, float px, float py, int body, int skip)
    {
        for (int o = 0; o < s.CellCount; o++)
        {
            if (o == skip || s.Dead(o) || s.CellBody[o] != body) continue;
            int off = s.PolyOff[o], len = s.PolyLen[o];
            if (len < 3) continue;
            float qx = px - s.CellRx[o], qy = py - s.CellRy[o];
            if (SimMath.Hypot(qx, qy) > s.CellRad[o]) continue;

            bool inside = true;
            for (int v = 0; v < len && inside; v++)
            {
                int w = v + 1 == len ? 0 : v + 1;
                float ax = s.PolyX[off + v], ay = s.PolyY[off + v];
                float bx = s.PolyX[off + w], by = s.PolyY[off + w];
                inside = (bx - ax) * (qy - ay) - (by - ay) * (qx - ax) >= -1e-3f;
            }
            if (inside) return o;
        }
        return -1;
    }

    private static bool Overlaps(SimState s, int c, int rec, int off, int v)
    {
        int a = s.TouchA[rec], b = s.TouchB[rec];
        double sax = s.CellRx[a] + s.CellSeedX[a], say = s.CellRy[a] + s.CellSeedY[a];
        double sbx = s.CellRx[b] + s.CellSeedX[b], sby = s.CellRy[b] + s.CellSeedY[b];
        double dx = sbx - sax, dy = sby - say;
        double d = System.Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9) return true;
        double ex = -dy / d, ey = dx / d;
        double mx = 0.5 * (sax + sbx), my = 0.5 * (say + sby);

        int len = s.PolyLen[c];
        int w = v + 1 == len ? 0 : v + 1;
        double u0 = (s.CellRx[c] + s.PolyX[off + v] - mx) * ex + (s.CellRy[c] + s.PolyY[off + v] - my) * ey;
        double u1 = (s.CellRx[c] + s.PolyX[off + w] - mx) * ex + (s.CellRy[c] + s.PolyY[off + w] - my) * ey;
        double lo = System.Math.Min(u0, u1), hi = System.Math.Max(u0, u1);
        return System.Math.Min(hi, s.TouchT1[rec]) - System.Math.Max(lo, s.TouchT0[rec]) > 1e-3;
    }

    private static bool Linked(SimState s, int c, int rec)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        for (int v = 0; v < len; v++) if (s.SideTouch[off + v] == rec) return true;
        return false;
    }

    /// <summary>Total length of the sides of cell <paramref name="c"/> labelled with bond k.</summary>
    private static float LabelledLength(SimState s, int c, int k)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        float total = 0f;
        for (int v = 0; v < len; v++)
        {
            if (s.PolyBond[off + v] != k) continue;
            int w = v + 1 == len ? 0 : v + 1;
            total += SimMath.Hypot(s.PolyX[off + w] - s.PolyX[off + v],
                                   s.PolyY[off + w] - s.PolyY[off + v]);
        }
        return total;
    }

    /// <summary>Does cell c's side v lie on the seed bisector it shares with cell o?</summary>
    private static bool OnBisector(SimState s, int c, int o, int off, int v, float tol)
    {
        if (o < 0 || o >= s.CellCount) return false;
        if (!Bisector(s, c, o, out float ex, out float ey, out float mx, out float my)) return false;
        int len = s.PolyLen[c];
        int w = v + 1 == len ? 0 : v + 1;
        float d0 = (s.PolyX[off + v] - mx) * ex + (s.PolyY[off + v] - my) * ey;
        float d1 = (s.PolyX[off + w] - mx) * ex + (s.PolyY[off + w] - my) * ey;
        return SimMath.Abs(d0) <= tol && SimMath.Abs(d1) <= tol;
    }

    /// <summary>
    /// A live cell of the same body whose bisector with c contains side v — i.e. something is
    /// actually across that side, so calling it open is a lie.
    /// </summary>
    private static int CoveringNeighbour(SimState s, int c, int body, int off, int v, float tol)
    {
        // Answered from the touch record, NOT from the bond list. Asking AdjBond whether anything is
        // across a side reproduces the very blind spot that created the sealed-side bug: a cell pair
        // whose shared side was too short to bond has no bond to find, so the check concluded
        // "nothing is there" about material that plainly is.
        short rec = s.SideTouch[off + v];
        if (rec < 0 || rec >= s.TouchCount) return -1;
        if (s.TouchA[rec] < 0) return -1;
        int o = s.TouchOther(rec, c);
        if (o < 0 || o >= s.CellCount || s.Dead(o) || s.CellBody[o] != body) return -1;
        return o;
    }

    private static bool Bisector(SimState s, int c, int o,
        out float ex, out float ey, out float mx, out float my)
    {
        float sxa = s.CellSeedX[c], sya = s.CellSeedY[c];
        float sxb = s.CellRx[o] + s.CellSeedX[o] - s.CellRx[c];
        float syb = s.CellRy[o] + s.CellSeedY[o] - s.CellRy[c];
        ex = sxb - sxa; ey = syb - sya;
        float el = SimMath.Hypot(ex, ey);
        mx = 0.5f * (sxa + sxb); my = 0.5f * (sya + syb);
        if (el < 1e-6f) { ex = 1f; ey = 0f; return false; }
        ex /= el; ey /= el;
        return true;
    }
}
