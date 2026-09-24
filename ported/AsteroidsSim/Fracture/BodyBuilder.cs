using System;
using System.Collections.Generic;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Builds a fracturable body: Voronoi tessellation, bond detection, the structure authoring layer,
/// the material bake, side labels and touch records.
/// </summary>
/// <remarks>
/// <para><b>RNG order is part of the contract.</b> The generator is consumed in exactly this
/// sequence: the outline ring (by the caller, via <see cref="MakeBlob"/>), then two draws per
/// tessellation grid point <i>including the points that are rejected</i> in a y-then-x scan, then
/// one draw for the body's grain axis (taken even when anisotropy is zero), then one Weibull draw
/// per bond. Change any of that and the bodies differ, which is a desync under lockstep.</para>
///
/// <para><b>Body creation order is likewise part of the contract</b> — bodies share one RNG stream,
/// so the order in which a scene is built decides what every body in it looks like.</para>
/// </remarks>
public static class BodyBuilder
{
    /// <summary>
    /// A convex hull over a jittered ellipse ring. Consumes <paramref name="n"/> draws.
    /// </summary>
    /// <remarks>
    /// The ring angles go through <see cref="SimMath"/>, i.e. single precision, because the
    /// deterministic contract forbids the platform's double-precision trig.
    /// </remarks>
    public static List<Vec2d> MakeBlob(ref ProtoRng rng,
        double cx, double cy, double rx, double ry, double wob = 0.12, int n = 20)
    {
        var ring = new List<Vec2d>(n);
        for (int k = 0; k < n; k++)
        {
            float a = SimMath.TwoPI * k / n;
            double r = 1 + rng.Range(-wob, wob);
            SimMath.SinCos(a, out float sa, out float ca);
            ring.Add(new Vec2d(cx + ca * rx * r, cy + sa * ry * r));
        }
        return Geometry2D.ConvexHull(ring);
    }

    /// <summary>
    /// The finest grain a body of this material may be built at, for the tuning's substep count.
    /// </summary>
    /// <remarks>
    /// <para>Bond forces are integrated explicitly, so they are stable only while a stress wave
    /// crosses less than a fraction of a cell per substep: c * (Dt / substeps) / cellSize, with
    /// cellSize = sqrt(grain). Past the limit the deviation field diverges, and the damage model
    /// cannot tell numerical divergence from load, so a body shatters from nothing and then goes
    /// non-finite. Measured: steel at grain 30 runs at 1.04 with the default 9 substeps and blew up
    /// within 10-22 ticks at every speed tried; at 16 substeps (0.58) it held.</para>
    /// <para>The limit is <see cref="ModelConstants.StableCfl"/>. Raising substeps lowers the floor
    /// as their square, so a finer grain stays available to anyone who pays for it.</para>
    /// <para>It depends on the wave speed only, not the density, so a small dense round is capped
    /// exactly like a large light one of the same material.</para>
    /// </remarks>
    public static float MinGrain(in Material material, in SimTuning tune)
    {
        int sub = tune.Substeps > 1 ? tune.Substeps : 1;
        double cpx = material.C * tune.PxPerMetre;
        double cell = cpx * (Solver.Dt / sub) / tune.Constants.StableCfl;
        return (float)(cell * cell);
    }

    /// <summary>
    /// Tessellates <paramref name="outline"/> and appends the resulting body to
    /// <paramref name="s"/>. <paramref name="member"/> optionally carves the shape — rejected seeds
    /// still take part in the clipping ("phantom seeds"), without which a carved shape comes out as
    /// its bounding box.
    /// </summary>
    public static void AddBody(SimState s, ref ProtoRng rng, in SimTuning tune,
        List<Vec2d> outline, float velX, float velY, float omega,
        in Material material, float grain, Func<Vec2d, bool>? member = null,
        BodyStructure? structure = null, Func<Vec2d, Material>? matAt = null)
    {
        var mp = new MaterialProps(material, tune);
        ref readonly ModelConstants km = ref tune.Constants;
        // Never finer than the material can be integrated at (see MinGrain). A matAt caller has to
        // pass a grain that satisfies every material it maps to: only the nominal one is known here.
        grain = System.Math.Max(grain, MinGrain(material, tune));
        double step = System.Math.Sqrt(grain);
        Geometry2D.BBox(outline, out double minX, out double minY, out double maxX, out double maxY);

        // ── seeds ────────────────────────────────────────────────────────────
        var seedP = new List<Vec2d>();
        var seedReal = new List<bool>();
        for (double y = minY - step; y < maxY + step; y += step)
            for (double x = minX - step; x < maxX + step; x += step)
            {
                // Both draws happen for every grid point, before the accept test.
                double jx = rng.Range(-step * km.SeedJitter, step * km.SeedJitter);
                double jy = rng.Range(-step * km.SeedJitter, step * km.SeedJitter);
                var p = new Vec2d(x + jx, y + jy);
                bool inO = Geometry2D.PointInPoly(p, outline);
                bool real = inO && (member == null || member(p));
                if (real) { seedP.Add(p); seedReal.Add(true); }
                else if (member != null && inO) { seedP.Add(p); seedReal.Add(false); }
            }
        bool anyReal = false;
        for (int i = 0; i < seedReal.Count; i++) if (seedReal[i]) { anyReal = true; break; }
        if (!anyReal) { seedP.Add(Geometry2D.Centroid(outline)); seedReal.Add(true); }

        // ── Voronoi cells ────────────────────────────────────────────────────
        var rawPoly = new List<List<Vec2d>>();
        var rawCent = new List<Vec2d>();
        var rawSeed = new List<Vec2d>();
        var sealed_ = new List<int>();   // cell pairs that touch but are too short to bond
        var touching = new List<int>();  // every cell pair that meets, bonded or not
        var touchBondOf = new List<int>();
        var rawArea = new List<double>();

        var work = new List<Vec2d>();
        var swap = new List<Vec2d>();

        for (int si = 0; si < seedP.Count; si++)
        {
            if (!seedReal[si]) continue;
            Vec2d sd = seedP[si];

            work.Clear();
            work.AddRange(outline);

            for (int ti = 0; ti < seedP.Count; ti++)
            {
                if (ti == si) continue;
                Vec2d t = seedP[ti];
                double mx = (sd.X + t.X) / 2, my = (sd.Y + t.Y) / 2;
                double nx = sd.X - t.X, ny = sd.Y - t.Y;
                double L = System.Math.Sqrt(nx * nx + ny * ny);
                if (L < 1e-6) continue;
                nx /= L; ny /= L;

                // BIT-SAFE SKIP. ClipHalf leaves a polygon untouched when every vertex is strictly
                // inside the half-plane, so skipping such a clip is identical to performing it —
                // not an approximation. This is what takes construction off O(seeds^2) in practice.
                if (!HalfPlaneCanCut(work, mx, my, nx, ny)) continue;

                Geometry2D.ClipHalf(work, new Vec2d(mx, my), new Vec2d(nx, ny), swap);
                (work, swap) = (swap, work);
                if (work.Count < 3) break;
            }

            if (work.Count < 3) continue;
            var poly = new List<Vec2d>(work);
            DropSliverSides(poly, km.SliverSide);
            if (poly.Count < 3) continue;
            if (Geometry2D.Area(poly) < 0) poly.Reverse();
            double A = System.Math.Abs(Geometry2D.Area(poly));
            if (A < km.MinCellArea) continue;
            rawPoly.Add(poly);
            rawCent.Add(Geometry2D.Centroid(poly));
            rawSeed.Add(sd);
            rawArea.Add(A);
        }

        if (rawPoly.Count == 0) return;

        // ── body frame ───────────────────────────────────────────────────────
        double M = 0, cx = 0, cy = 0;
        for (int i = 0; i < rawPoly.Count; i++)
        {
            double m = rawArea[i] * mp.Rho;
            M += m; cx += rawCent[i].X * m; cy += rawCent[i].Y * m;
        }
        cx /= M; cy /= M;

        int bi = s.BodyCount;
        s.EnsureBodies(bi + 1);
        s.BodyX[bi] = (float)cx; s.BodyY[bi] = (float)cy; s.BodyRot[bi] = 0f;
        s.BodyVx[bi] = velX; s.BodyVy[bi] = velY; s.BodyW[bi] = omega;
        s.BodyWPrev[bi] = omega; s.BodyAlpha[bi] = 0f;
        s.BodyRho[bi] = mp.Rho; s.BodyCpx[bi] = mp.Cpx;
        s.BodyCellSize[bi] = (float)step;
        s.BodyEulL[bi] = 0f;
        s.BodyCount = bi + 1;

        // One id for the whole body unless the caller maps material by position, in which case each
        // cell resolves its own from its seed. RegisterMaterial deduplicates by value, so a two-
        // material body costs two table slots however many cells it has.
        byte matId = s.RegisterMaterial(material);
        bool solo = rawPoly.Count == 1;   // built as one cell: a legitimate pebble, never dust
        int cellStart = s.CellCount;
        double bodyI = 0;

        s.EnsureCells(cellStart + rawPoly.Count);
        for (int i = 0; i < rawPoly.Count; i++)
        {
            var poly = rawPoly[i];
            Vec2d cent = rawCent[i];
            double rx = cent.X - cx, ry = cent.Y - cy;
            double m = rawArea[i] * mp.Rho;
            double Ic = Geometry2D.PolyInertia(poly, cent) * mp.Rho;
            bodyI += Ic + m * (rx * rx + ry * ry);

            double rad2 = 0;
            for (int v = 0; v < poly.Count; v++)
            {
                double dx = poly[v].X - cent.X, dy = poly[v].Y - cent.Y;
                double rr = dx * dx + dy * dy;
                if (rr > rad2) rad2 = rr;
            }

            int ci = cellStart + i;
            int cap = poly.Count + SimState.PolySlack;
            s.EnsurePoly(s.PolyCount + cap);
            s.PolyOff[ci] = s.PolyCount;
            s.PolyLen[ci] = poly.Count;
            s.PolyCap[ci] = cap;
            for (int v = 0; v < poly.Count; v++)
            {
                s.PolyX[s.PolyCount + v] = (float)(poly[v].X - cent.X);
                s.PolyY[s.PolyCount + v] = (float)(poly[v].Y - cent.Y);
            }
            s.PolyCount += cap;          // the slack is reserved, not written

            s.CellRx[ci] = (float)rx; s.CellRy[ci] = (float)ry;
            s.CellDvx[ci] = 0f; s.CellDvy[ci] = 0f; s.CellDw[ci] = 0f;
            s.CellM[ci] = (float)m; s.CellIm[ci] = (float)(1.0 / m);
            s.CellIc[ci] = (float)Ic; s.CellIic[ci] = (float)(1.0 / Ic);
            s.CellArea[ci] = (float)rawArea[i];
            s.CellPerim[ci] = (float)Geometry2D.Perimeter(poly);
            s.CellRad[ci] = (float)System.Math.Sqrt(rad2);
            s.CellBody[ci] = bi;
            s.SetFlag(ci, CellFlag.Dead, false); s.SetFlag(ci, CellFlag.Solo, solo);
            s.CellTouch[ci] = int.MinValue; s.CellBorn[ci] = int.MinValue;
            s.CellMat[ci] = matAt == null ? matId : s.RegisterMaterial(matAt(rawSeed[i]));
            s.CellSeedX[ci] = (float)(rawSeed[i].X - cent.X);
            s.CellSeedY[ci] = (float)(rawSeed[i].Y - cent.Y);
            s.CellArea0[ci] = s.CellArea[ci];
        }
        s.CellCount = cellStart + rawPoly.Count;
        s.BodyI[bi] = (float)bodyI;
        s.BodyM[bi] = (float)M;

        // ── bonds: cells sharing a Voronoi edge ──────────────────────────────
        int bondStart = s.BondCount;
        int n2 = rawPoly.Count;
        for (int i = 0; i < n2; i++)
            for (int j = i + 1; j < n2; j++)
            {
                int ca = cellStart + i, cb = cellStart + j;

                // BIT-SAFE SKIP: SegOverlap returns 0 for every edge pair of two cells whose
                // bounding circles do not touch, so the bond would not have been created.
                double ddx = s.CellRx[cb] - s.CellRx[ca], ddy = s.CellRy[cb] - s.CellRy[ca];
                double rsum = s.CellRad[ca] + s.CellRad[cb] + 1.0;
                if (ddx * ddx + ddy * ddy > rsum * rsum) continue;

                var pa = rawPoly[i]; var pb = rawPoly[j];
                Vec2d oa = new(s.CellRx[ca], s.CellRy[ca]);
                Vec2d ob = new(s.CellRx[cb], s.CellRy[cb]);

                double sh = 0, mx = 0, my = 0; int cnt = 0;
                for (int a = 0; a < pa.Count; a++)
                {
                    Vec2d a0 = new(pa[a].X - rawCent[i].X + oa.X, pa[a].Y - rawCent[i].Y + oa.Y);
                    Vec2d a1n = pa[(a + 1) % pa.Count];
                    Vec2d a1 = new(a1n.X - rawCent[i].X + oa.X, a1n.Y - rawCent[i].Y + oa.Y);
                    for (int z = 0; z < pb.Count; z++)
                    {
                        Vec2d b0 = new(pb[z].X - rawCent[j].X + ob.X, pb[z].Y - rawCent[j].Y + ob.Y);
                        Vec2d b1n = pb[(z + 1) % pb.Count];
                        Vec2d b1 = new(b1n.X - rawCent[j].X + ob.X, b1n.Y - rawCent[j].Y + ob.Y);
                        double ov = Geometry2D.SegOverlap(a0, a1, b0, b1, km.EdgeOverlap);
                        if (ov > 0)
                        {
                            sh += ov;
                            mx += (a0.X + a1.X) / 2; my += (a0.Y + a1.Y) / 2;
                            cnt++;
                        }
                    }
                }
                // TOUCHING is recorded whether or not a bond follows. A bond is skipped when the
                // shared side is shorter than the minimum shared edge, and inferring adjacency from bonds is
                // what made those sides read as open surface in the middle of solid material.
                if (sh > 0 && cnt > 0) { touching.Add(ca); touching.Add(cb); }
                if (sh <= km.MinSharedEdge || cnt == 0)
                {
                    if (sh > 0 && cnt > 0) { sealed_.Add(ca); sealed_.Add(cb); }
                    continue;
                }
                mx /= cnt; my /= cnt;

                double nx = s.CellRx[cb] - s.CellRx[ca], ny = s.CellRy[cb] - s.CellRy[ca];
                double L = System.Math.Sqrt(nx * nx + ny * ny);
                if (L == 0) L = 1;
                nx /= L; ny /= L;

                double Lb = System.Math.Max(1.0, sh);

                // TWO HALF-BONDS IN SERIES. Each cell owns half the bond, so its half is twice as
                // stiff as the whole would be; in series they give 2 ka kb / (ka + kb), which is
                // exactly ka when the two materials match — so a uniform body is unchanged, and a
                // genuine material boundary gets the impedance mismatch it should have.
                // Identical materials take the single-material expression verbatim, because
                // 2kk/(k+k) is not bit-exactly k in floating point and a uniform body must not move.
                double ka = CellStiffness(s, ca, tune, Lb, step);
                double k0 = s.CellMat[ca] == s.CellMat[cb] ? ka : Series(ka, CellStiffness(s, cb, tune, Lb, step));

                int bk = s.BondCount;
                s.EnsureBonds(bk + 1);
                touchBondOf.Add(bk);
                s.BondA[bk] = ca; s.BondB[bk] = cb;
                s.BondLen[bk] = (float)sh;
                s.BondStr[bk] = 1f;
                s.BondK0[bk] = (float)k0;
                s.BondKa0[bk] = (float)(k0 * Lb * Lb / 12.0);
                s.BondS0[bk] = 0f;
                s.BondRax[bk] = (float)(mx - s.CellRx[ca]);
                s.BondRay[bk] = (float)(my - s.CellRy[ca]);
                s.BondRbx[bk] = (float)(mx - s.CellRx[cb]);
                s.BondRby[bk] = (float)(my - s.CellRy[cb]);
                s.BondNx[bk] = (float)nx; s.BondNy[bk] = (float)ny;
                s.BondSn[bk] = 0f; s.BondSt[bk] = 0f; s.BondSa[bk] = 0f;
                s.BondDmg[bk] = 0f; s.BondLmax[bk] = 0f; s.BondBroken[bk] = false; s.BondMode[bk] = 0;
                s.BondCount = bk + 1;
            }

        ApplyStructure(s, ref rng, km, structure ?? BodyStructure.From(tune), bondStart, s.BondCount);

        // Peak stretch bakes AFTER structure: the authoring layer scales STRENGTH, never
        // stiffness — heterogeneous stiffness would make the wave field heterogeneous too.
        for (int k = bondStart; k < s.BondCount; k++)
        {
            // THE WEAKER CELL GOVERNS, on both axes. A bond is an interface, and an interface fails
            // as its weaker side does — so the failure stretch is the smaller failure strain and the
            // softening is the more brittle chi. Identical materials reproduce the old value exactly.
            ref readonly Material ma = ref s.Mat(s.BondA[k]);
            ref readonly Material mb = ref s.Mat(s.BondB[k]);
            var weak = new MaterialProps(ma.Strain <= mb.Strain ? ma : mb, tune);
            var mpA = new MaterialProps(ma, tune);
            var mpB = new MaterialProps(mb, tune);

            // The expression is the original one, evaluated on the weaker material, so a uniform
            // body reproduces its old value to the bit rather than to a rounding.
            s.BondS0[k] = (float)(weak.VCrit * step / weak.Cpx) * s.BondStr[k];
            s.BondChi[k] = SimMath.Min(mpA.Chi, mpB.Chi);
        }

        RebuildMembership(s);
        s.Reindex();
        LabelPolyEdges(s, km, cellStart, s.CellCount, sealed_);
        BuildTouchRecords(s, km, touching);
    }

    /// <summary>Removes vertices that sit within rounding of their predecessor.</summary>
    /// <remarks>
    /// Clipping a Voronoi cell by several half-planes can put two consecutive vertices a few
    /// hundredths of a pixel apart where three planes nearly meet at a point. The sliver side
    /// between them is collinear with a real shared side, so the touch-record builder links it to
    /// that record — and it then lies outside the span the two cells actually share. That was every
    /// audit fault present at tick 0: labels contradicting their record before the simulation had
    /// taken a step. Everything downstream (area, centroid, inertia, bonds) derives from this list,
    /// so cleaning it here keeps the build self-consistent. The threshold is
    /// <see cref="ModelConstants.SliverSide"/>, two orders below any side a bond is built on.
    /// </remarks>
    private static void DropSliverSides(List<Vec2d> poly, double eps)
    {
        for (int v = poly.Count - 1; v >= 0 && poly.Count > 3; v--)
        {
            int u = v == 0 ? poly.Count - 1 : v - 1;
            double dx = poly[v].X - poly[u].X, dy = poly[v].Y - poly[u].Y;
            if (dx * dx + dy * dy < eps * eps) poly.RemoveAt(v);
        }
    }

    /// <summary>
    /// True when the half-plane could remove at least one vertex. Used only to skip clips that
    /// provably do nothing — see the call site.
    /// </summary>
    private static bool HalfPlaneCanCut(List<Vec2d> poly, double px, double py, double nx, double ny)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            double d = (poly[i].X - px) * nx + (poly[i].Y - py) * ny;
            if (d < 0) return true;
        }
        return false;
    }

    /// <summary>Two half-bonds in series: 2 ka kb / (ka + kb), which is ka when the two agree.</summary>
    private static double Series(double ka, double kb) => ka + kb > 0 ? 2.0 * ka * kb / (ka + kb) : 0.0;

    /// <summary>A cell's own contribution to a bond's stiffness: rho c^2 scaled by the bond's share.</summary>
    private static double CellStiffness(SimState s, int c, in SimTuning tune, double Lb, double step)
    {
        // Through MaterialProps, not recomputed: it carries Rho and Cpx as FLOATS, and steel's
        // 7850/1000 is not exactly representable, so dividing in double here moved the steel
        // fingerprint while rock and glass (which divide cleanly) stayed put.
        var mp = new MaterialProps(s.Mat(c), tune);
        return mp.Rho * mp.Cpx * mp.Cpx * (Lb / step);
    }

    /// <summary>
    /// The structure authoring layer. Writes exactly one number per bond — <c>BondStr</c> — from
    /// composable patterns; the solver never learns how it was produced, and fragments inherit it
    /// for free because each bond carries its own value through splitting.
    /// </summary>
    private static void ApplyStructure(SimState s, ref ProtoRng rng, in ModelConstants km, in BodyStructure st,
        int bondStart, int bondEnd)
    {
        if (bondEnd <= bondStart) return;

        // Drawn unconditionally when the grain is not locked, even at zero anisotropy — the
        // draw is consumed either way, and the stream position is part of the contract.
        double g = st.GrainLock ? st.GrainAngle : rng.Range(0, System.Math.PI);

        // neighbour counts, for the surface-flaw pattern
        var nb = new Dictionary<int, int>();
        for (int k = bondStart; k < bondEnd; k++)
        {
            nb.TryGetValue(s.BondA[k], out int va); nb[s.BondA[k]] = va + 1;
            nb.TryGetValue(s.BondB[k], out int vb); nb[s.BondB[k]] = vb + 1;
        }

        float m = st.WeibullM;
        float med = m > 0 ? SimMath.Pow(0.6931471805599453f, 1f / m) : 1f;

        for (int k = bondStart; k < bondEnd; k++)
        {
            float str = 1f;

            // Weibull scatter, median-normalised to 1. Weakest-link nucleation: this is what turns
            // "the whole shocked region crosses threshold together" into cracks that start
            // somewhere. One draw per bond, in bond order.
            if (m > 0)
            {
                double u = System.Math.Max(1e-9, 1.0 - rng.NextDouble());
                str *= SimMath.Pow(-SimMath.Log((float)u), 1f / m) / med;
            }

            // Bedding-plane anisotropy. A bond along the grain is stronger, across it weaker;
            // doubled angle because a bedding plane is an axis.
            if (st.Aniso > 0)
            {
                int a = s.BondA[k], b = s.BondB[k];
                float ba = SimMath.Atan2(s.CellRy[b] - s.CellRy[a], s.CellRx[b] - s.CellRx[a]);
                str *= SimMath.Max(km.AnisoFloor, 1f + st.Aniso * SimMath.Cos(2f * (ba - (float)g)));
            }

            // Surface flaws: bonds whose cells sit at the boundary are weaker. Real brittle solids
            // crack from surface defects, and this is what lets glass initiate at all.
            if (st.SurfFlaw > 0)
            {
                nb.TryGetValue(s.BondA[k], out int na);
                nb.TryGetValue(s.BondB[k], out int nbb);
                if (System.Math.Min(na, nbb) < km.SurfFlawNeighbours) str *= 1f - st.SurfFlaw;
            }

            s.BondStr[k] = SimMath.Clamp(str, km.BondStrengthMin, km.BondStrengthMax);
        }
    }

    /// <summary>
    /// Labels every cell edge with the bond across it, or −1 for a surface side.
    /// </summary>
    /// <remarks>
    /// <para>Done ONCE, here, where the geometry is exactly as the tessellator left it: a shared
    /// edge lies precisely on the bisector of the two cells' seeds, so the match is unambiguous.
    /// Afterwards the labelling is MAINTAINED — bond breaks turn sides into surface, carving remaps
    /// them through the clip — because re-deriving it from moved geometry is what produced interior
    /// sides reported as free.</para>
    /// <para>Runs after <c>Reindex</c>, so <c>AdjBond</c> is available and each cell need only test
    /// its own neighbours rather than every cell in the body.</para>
    /// </remarks>
    public static void LabelPolyEdges(SimState s, in ModelConstants km, int cellFrom, int cellTo,
        List<int>? sealedPairs = null)
    {
        for (int c = cellFrom; c < cellTo; c++)
        {
            int off = s.PolyOff[c], len = s.PolyLen[c];
            for (int v = 0; v < len; v++) { s.PolyBond[off + v] = -1; s.SideTouch[off + v] = -1; }
            if (len < 3) continue;

            double eps = System.Math.Max(km.LabelMatchAbs, s.CellRad[c] * km.LabelMatchRel);
            int aoff = s.AdjOff[c], alen = s.AdjLen[c];
            int extra = sealedPairs?.Count ?? 0;
            for (int j = 0; j < alen + extra / 2; j++)
            {
                int k, o;
                if (j < alen)
                {
                    k = s.AdjBond[aoff + j];
                    o = s.BondA[k] == c ? s.BondB[k] : s.BondA[k];
                }
                else
                {
                    int e = (j - alen) * 2;
                    int p0 = sealedPairs![e], p1 = sealedPairs[e + 1];
                    if (p0 != c && p1 != c) continue;
                    o = p0 == c ? p1 : p0;
                    k = SimState.SideSealed;          // interior, but nothing to reference
                }
                if (o < 0 || o >= s.CellCount) continue;

                // The shared edge lies on the perpendicular bisector of the two SEEDS — not of the
                // two centroids, which is a different line entirely and matches almost nothing.
                double sxa = s.CellSeedX[c], sya = s.CellSeedY[c];
                double sxb = s.CellRx[o] + s.CellSeedX[o] - s.CellRx[c];
                double syb = s.CellRy[o] + s.CellSeedY[o] - s.CellRy[c];
                double ex = sxb - sxa, ey = syb - sya;
                double el = System.Math.Sqrt(ex * ex + ey * ey);
                if (el < 1e-9) continue;
                ex /= el; ey /= el;
                double mx = 0.5 * (sxa + sxb), my = 0.5 * (sya + syb);

                for (int v = 0; v < len; v++)
                {
                    int w = v + 1 == len ? 0 : v + 1;
                    double d0 = (s.PolyX[off + v] - mx) * ex + (s.PolyY[off + v] - my) * ey;
                    double d1 = (s.PolyX[off + w] - mx) * ex + (s.PolyY[off + w] - my) * ey;
                    if (System.Math.Abs(d0) <= eps && System.Math.Abs(d1) <= eps)
                        s.PolyBond[off + v] = (short)k;
                }
            }
        }
    }

    /// <summary>Whether body-frame point (px,py) is a vertex of cell c at which a free side starts or ends.</summary>
    private static bool EndOnFreeSide(SimState s, in ModelConstants km, int c, double px, double py)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        double tol = km.FreeSideEnd * System.Math.Max(1f, s.CellRad[c]);
        for (int v = 0; v < len; v++)
        {
            if (s.PolyBond[off + v] != SimState.SideReal) continue;
            int w = v + 1 == len ? 0 : v + 1;
            double x0 = s.CellRx[c] + s.PolyX[off + v], y0 = s.CellRy[c] + s.PolyY[off + v];
            double x1 = s.CellRx[c] + s.PolyX[off + w], y1 = s.CellRy[c] + s.PolyY[off + w];
            if (System.Math.Abs(x0 - px) <= tol && System.Math.Abs(y0 - py) <= tol) return true;
            if (System.Math.Abs(x1 - px) <= tol && System.Math.Abs(y1 - py) <= tol) return true;
        }
        return false;
    }

    /// <summary>
    /// Creates one touch record per adjacent cell pair and links both cells' sides to it.
    /// </summary>
    /// <remarks>
    /// Runs once, here, where the geometry is exactly as the tessellator left it — the two cells'
    /// copies of a shared side coincide to float rounding, so projecting both onto the bisector and
    /// intersecting gives the true shared extent. Afterwards the record is MAINTAINED: carving
    /// narrows the interval, and both cells see that through the one record rather than through two
    /// copies that have to be kept in step.
    /// </remarks>
    public static void BuildTouchRecords(SimState s, in ModelConstants km, List<int> touching)
    {
        for (int i = 0; i + 1 < touching.Count; i += 2)
        {
            int a = touching[i], b = touching[i + 1];
            if (!Bisector(s, a, b, out double ex, out double ey, out double mx, out double my))
                continue;

            if (!SideOnLine(s, km, a, ex, ey, mx, my, out double a0, out double a1)) continue;
            if (!SideOnLine(s, km, b, ex, ey, mx, my, out double b0, out double b1)) continue;

            double t0 = System.Math.Max(a0, b0), t1 = System.Math.Min(a1, b1);
            if (t1 - t0 <= km.BuildSpanMin) continue;

            int r = s.TouchCount;
            s.EnsureTouch(r + 1);
            s.TouchA[r] = a; s.TouchB[r] = b;
            s.TouchBond[r] = -1;
            s.TouchT0[r] = (float)t0; s.TouchT1[r] = (float)t1;
            s.TouchS0[r] = (float)t0; s.TouchS1[r] = (float)t1;
            s.TouchCount = r + 1;

            LinkSide(s, km, a, ex, ey, mx, my, (short)r);
            LinkSide(s, km, b, ex, ey, mx, my, (short)r);
            s.TouchOpen[r] = 0;
        }

        // Attach the bond that runs along each adjacency, where one was built.
        for (int k = 0; k < s.BondCount; k++)
        {
            int a = s.BondA[k], b = s.BondB[k];
            for (int r = 0; r < s.TouchCount; r++)
                if ((s.TouchA[r] == a && s.TouchB[r] == b) || (s.TouchA[r] == b && s.TouchB[r] == a))
                { s.TouchBond[r] = (short)k; break; }
        }

        // ── LABELS FOLLOW RECORDS ─────────────────────────────────────────────
        // LabelPolyEdges and this pass match sides to neighbours independently, each with its own
        // tolerance, and at a near-4-valent vertex — two copies of a vertex 0.04 px apart — the
        // label pass missed a 3.85 px interface the record pass found. The side sat labelled REAL
        // with a live record and a live bond, and the exposed-end tagging below then read that REAL
        // side as outline and marked three interior records open: an internal surface at tick 7 of
        // a grain-255 scene. Records are the adjacency truth; a side that carries one is not free.
        for (int c = 0; c < s.CellCount; c++)
        {
            int off = s.PolyOff[c], len = s.PolyLen[c];
            for (int v = 0; v < len; v++)
            {
                short r = s.SideTouch[off + v];
                if (r < 0 || s.TouchA[r] < 0) continue;
                short k = s.TouchBond[r];
                s.PolyBond[off + v] = k >= 0 ? k : SimState.SideSealed;
            }
        }

        // ── EXPOSED ENDS ──────────────────────────────────────────────────────
        // An end of a record is exposed when it is an outline vertex: a vertex of either cell that a
        // free side starts or ends at. Interior ends are where three records meet. Only now, with
        // the labels consistent, can a free side be trusted to mean outline.
        for (int r = 0; r < s.TouchCount; r++)
        {
            int a = s.TouchA[r], b = s.TouchB[r];
            if (a < 0 || !Bisector(s, a, b, out double ex, out double ey, out double mx, out double my)) continue;
            double t0 = s.TouchT0[r], t1 = s.TouchT1[r];
            byte open = 0;
            if (EndOnFreeSide(s, km, a, mx + t0 * ex, my + t0 * ey) || EndOnFreeSide(s, km, b, mx + t0 * ex, my + t0 * ey))
                open |= SimState.TouchOpen0;
            if (EndOnFreeSide(s, km, a, mx + t1 * ex, my + t1 * ey) || EndOnFreeSide(s, km, b, mx + t1 * ex, my + t1 * ey))
                open |= SimState.TouchOpen1;
            s.TouchOpen[r] = open;
        }
    }

    /// <summary>The perpendicular bisector of two cells' seeds, in body coordinates.</summary>
    private static bool Bisector(SimState s, int a, int b,
        out double ex, out double ey, out double mx, out double my)
    {
        double sax = s.CellRx[a] + s.CellSeedX[a], say = s.CellRy[a] + s.CellSeedY[a];
        double sbx = s.CellRx[b] + s.CellSeedX[b], sby = s.CellRy[b] + s.CellSeedY[b];
        double dx = sbx - sax, dy = sby - say;
        double d = System.Math.Sqrt(dx * dx + dy * dy);
        mx = 0.5 * (sax + sbx); my = 0.5 * (say + sby);
        if (d < 1e-9) { ex = 1; ey = 0; return false; }
        ex = -dy / d; ey = dx / d;              // along the bisector
        return true;
    }

    /// <summary>Extent, along the bisector, of whichever side of cell c lies on it.</summary>
    private static bool SideOnLine(SimState s, in ModelConstants km, int c, double ex, double ey, double mx, double my,
        out double t0, out double t1)
    {
        // UNION of every side on that line, not the first one found. One shared boundary can be
        // split across several collinear sides of a cell — a third cell meeting it partway adds a
        // vertex — and taking only the first makes the recorded overlap shorter than the boundary
        // really is. Measured: 250 of 537 interior sides then reported themselves partly exposed at
        // build, before anything had moved, drawn as surface scattered through solid material.
        t0 = 0; t1 = 0; bool any = false;
        int off = s.PolyOff[c], len = s.PolyLen[c];
        double nx = -ey, ny = ex;                                  // normal to the bisector
        double tol = System.Math.Max(km.SideLineAbs, s.CellRad[c] * km.SideLineRel);
        for (int v = 0; v < len; v++)
        {
            int w = v + 1 == len ? 0 : v + 1;
            double px = s.CellRx[c] + s.PolyX[off + v] - mx, py = s.CellRy[c] + s.PolyY[off + v] - my;
            double qx = s.CellRx[c] + s.PolyX[off + w] - mx, qy = s.CellRy[c] + s.PolyY[off + w] - my;
            if (System.Math.Abs(px * nx + py * ny) > tol) continue;
            if (System.Math.Abs(qx * nx + qy * ny) > tol) continue;
            double u0 = px * ex + py * ey, u1 = qx * ex + qy * ey;
            if (!any) { t0 = System.Math.Min(u0, u1); t1 = System.Math.Max(u0, u1); any = true; }
            else
            {
                t0 = System.Math.Min(t0, System.Math.Min(u0, u1));
                t1 = System.Math.Max(t1, System.Math.Max(u0, u1));
            }
        }
        return any;
    }

    private static void LinkSide(SimState s, in ModelConstants km, int c, double ex, double ey, double mx, double my, short r)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        double nx = -ey, ny = ex;
        double tol = System.Math.Max(km.SideLineAbs, s.CellRad[c] * km.SideLineRel);
        double t0 = s.TouchT0[r], t1 = s.TouchT1[r];
        for (int v = 0; v < len; v++)
        {
            int w = v + 1 == len ? 0 : v + 1;
            double px = s.CellRx[c] + s.PolyX[off + v] - mx, py = s.CellRy[c] + s.PolyY[off + v] - my;
            double qx = s.CellRx[c] + s.PolyX[off + w] - mx, qy = s.CellRy[c] + s.PolyY[off + w] - my;
            if (System.Math.Abs(px * nx + py * ny) > tol) continue;
            if (System.Math.Abs(qx * nx + qy * ny) > tol) continue;

            // On the line is not enough: the side has to lie within the span the two cells share. A
            // sliver collinear with a shared side but beyond its end faces a THIRD cell — it is that
            // pair's business, and linking it here left a label contradicting its record at tick 0.
            double u0 = px * ex + py * ey, u1 = qx * ex + qy * ey;
            double lo = System.Math.Min(u0, u1), hi = System.Math.Max(u0, u1);
            if (System.Math.Min(hi, t1) - System.Math.Max(lo, t0) <= km.LinkOverlapMin) continue;

            s.SideTouch[off + v] = r;                              // EVERY such side, not just one
        }
    }

    /// <summary>
    /// Rebuilds the per-body cell lists by scanning cells in index order, so membership order — and
    /// therefore every per-body loop — is a pure function of cell indices.
    /// </summary>
    public static void RebuildMembership(SimState s)
    {
        for (int b = 0; b < s.BodyCount; b++) s.BodyCellLen[b] = 0;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int b = s.CellBody[c];
            if (b >= 0 && b < s.BodyCount) s.BodyCellLen[b]++;
        }
        int off = 0;
        for (int b = 0; b < s.BodyCount; b++) { s.BodyCellOff[b] = off; off += s.BodyCellLen[b]; }
        s.BodyCellsCount = off;
        s.EnsureBodyCells(System.Math.Max(1, off));

        for (int b = 0; b < s.BodyCount; b++) s.BodyCellLen[b] = 0;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int b = s.CellBody[c];
            if (b < 0 || b >= s.BodyCount) continue;
            s.BodyCells[s.BodyCellOff[b] + s.BodyCellLen[b]++] = c;
        }
    }
}
