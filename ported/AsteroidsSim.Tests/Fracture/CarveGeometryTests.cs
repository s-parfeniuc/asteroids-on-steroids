using AsteroidsSim.Fracture;
using AsteroidsSim.Math;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The geometric contract carving rests on, asserted directly on the kernel rather than inferred
/// from how a scene looks after it.
/// </summary>
/// <remarks>
/// <para>Carving is the one place in the solver that mutates collider geometry at runtime, and every
/// consumer of that geometry fails <i>silently</i> when it is wrong: SAT reports separation for
/// overlapping concave shapes, a winding flip inverts every contact normal, and a stale
/// <c>CellRad</c> lets a cell escape its own bounding circle so the broadphase drops the pair. None
/// of those raise anything. They just make the simulation quietly incorrect.</para>
///
/// <para>So the invariants are pinned here, on a cell carved directly, where a violation names
/// itself.</para>
/// </remarks>
public class CarveGeometryTests
{
    private readonly ITestOutputHelper _out;
    public CarveGeometryTests(ITestOutputHelper output) => _out = output;

    /// <summary>Twice the signed area — positive for CCW, and the sign is what SAT depends on.</summary>
    private static float SignedArea2(SimState s, int c)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        float a2 = 0f;
        for (int i = 0; i < len; i++)
        {
            int j = i + 1 == len ? 0 : i + 1;
            a2 += s.PolyX[off + i] * s.PolyY[off + j] - s.PolyX[off + j] * s.PolyY[off + i];
        }
        return a2;
    }

    private static void AssertConvexCcw(SimState s, int c, string where)
    {
        int off = s.PolyOff[c], len = s.PolyLen[c];
        Assert.True(len >= 3, $"{where}: degenerate polygon, {len} vertices");
        Assert.True(SignedArea2(s, c) > 0f, $"{where}: winding flipped");
        for (int i = 0; i < len; i++)
        {
            int p = i == 0 ? len - 1 : i - 1;
            int q = i + 1 == len ? 0 : i + 1;
            float ux = s.PolyX[off + i] - s.PolyX[off + p], uy = s.PolyY[off + i] - s.PolyY[off + p];
            float vx = s.PolyX[off + q] - s.PolyX[off + i], vy = s.PolyY[off + q] - s.PolyY[off + i];
            Assert.True(ux * vy - uy * vx >= -1e-3f, $"{where}: reflex vertex at {i}");
        }
    }

    [Fact]
    public void CarvingKeepsCellsConvexAndCounterClockwise()
    {
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 0f);
        SimState s = r.State;

        // Carve every cell from many directions, deeply enough to force the vertex budget to bind.
        int carved = 0;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            AssertConvexCcw(s, c, $"cell {c} before");
            for (int k = 0; k < 16; k++)
            {
                float ang = k * 0.39269908f;              // 22.5 degrees apart, so every clip is a NEW direction
                float nx = SimMath.Cos(ang), ny = SimMath.Sin(ang);
                float depth = s.CellRad[c] * 0.80f;       // plane inside the bounding circle: it must bite
                r.Solver.CarveCell(c, nx * depth, ny * depth, nx, ny);
                if (s.PolyLen[c] >= 3) AssertConvexCcw(s, c, $"cell {c} after clip {k}");
                Assert.True(s.PolyLen[c] <= s.PolyCap[c],
                    $"cell {c} overran its span: {s.PolyLen[c]} > {s.PolyCap[c]}");
            }
            carved++;
        }

        _out.WriteLine($"carved {carved} cells, {r.Solver.CarveClips} clips, "
                     + $"{r.Solver.CarveSimplifications} simplifications");
        Assert.True(carved > 0);
    }

    [Fact]
    public void CarvingOnlyEverRemovesArea()
    {
        // The property the shed-mass accounting rests on. If a clip could ADD area, carving would be
        // inventing material and the mass it sheds would be fictional.
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 0f);
        SimState s = r.State;
        float worstGain = 0f;

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            for (int k = 0; k < 8; k++)
            {
                float before = s.CellArea[c];
                float ang = k * 0.7853982f;
                float nx = SimMath.Cos(ang), ny = SimMath.Sin(ang);
                float removed = r.Solver.CarveCell(c, nx * s.CellRad[c] * 0.7f,
                                                      ny * s.CellRad[c] * 0.7f, nx, ny);
                if (s.PolyLen[c] < 3) break;
                float gain = s.CellArea[c] - before;
                if (gain > worstGain) worstGain = gain;
                Assert.True(removed >= 0f, $"cell {c}: negative removal {removed}");
            }
        }

        _out.WriteLine($"worst area gain across all carves: {worstGain:E3}");
        Assert.True(worstGain <= 1e-3f, $"a carve ADDED {worstGain:F4} of area");
    }

    [Fact]
    public void CarvingKeepsTheDerivedQuantitiesConsistent()
    {
        // CellR is the polygon centroid by contract — it is the lever-arm origin in SolveContact and
        // the mass point in RecomputeBody. CellRad must bound every vertex, or the broadphase and the
        // deep-overlap normal guard silently drop pairs.
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 0f);
        SimState s = r.State;
        float worstCentroid = 0f, worstRadius = 0f;

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            float massBefore = s.CellM[c];
            for (int k = 0; k < 6; k++)
            {
                float ang = 1.1f + k * 1.04719755f;
                float nx = SimMath.Cos(ang), ny = SimMath.Sin(ang);
                r.Solver.CarveCell(c, nx * s.CellRad[c] * 0.75f, ny * s.CellRad[c] * 0.75f, nx, ny);
                if (s.PolyLen[c] < 3) break;
            }
            if (s.PolyLen[c] < 3) continue;

            // Carving must not move mass. Mass leaves only through the shed path, which is the
            // caller's job — the kernel touching it would double-count the loss.
            Assert.Equal(massBefore, s.CellM[c]);

            int off = s.PolyOff[c], len = s.PolyLen[c];
            float a2 = 0f, cx = 0f, cy = 0f, rad = 0f;
            for (int i = 0; i < len; i++)
            {
                int j = i + 1 == len ? 0 : i + 1;
                float x0 = s.PolyX[off + i], y0 = s.PolyY[off + i];
                float x1 = s.PolyX[off + j], y1 = s.PolyY[off + j];
                float cr = x0 * y1 - x1 * y0;
                a2 += cr; cx += (x0 + x1) * cr; cy += (y0 + y1) * cr;
                rad = SimMath.Max(rad, SimMath.Hypot(x0, y0));
            }
            cx /= 3f * a2; cy /= 3f * a2;

            worstCentroid = SimMath.Max(worstCentroid, SimMath.Hypot(cx, cy) / s.CellRad[c]);
            worstRadius = SimMath.Max(worstRadius, rad / s.CellRad[c]);
        }

        _out.WriteLine($"worst centroid offset {worstCentroid:E3} of radius, "
                     + $"worst vertex {worstRadius:F4}x CellRad");
        Assert.True(worstCentroid < 1e-3f, $"CellR drifted off the centroid by {worstCentroid:P2}");
        Assert.True(worstRadius <= 1.001f, $"a vertex sat {worstRadius:F3}x outside CellRad");
    }

    [Fact]
    public void AClipThatCannotReachTheCellIsExactlyANoOp()
    {
        // The skip has to be bit-exact, not merely harmless: carving runs inside the substep loop, so
        // a clip that perturbed a cell it never touched would move the fingerprint on every tick.
        var r = Scenarios.Collide(SimTuning.Default, Material.Rock, speed: 0f);
        SimState s = r.State;

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c) || s.PolyLen[c] < 3) continue;
            int off = s.PolyOff[c], len = s.PolyLen[c];
            var bx = new float[len];
            var by = new float[len];
            for (int v = 0; v < len; v++) { bx[v] = s.PolyX[off + v]; by[v] = s.PolyY[off + v]; }
            float area = s.CellArea[c], rad = s.CellRad[c], rx = s.CellRx[c];

            // A plane well outside the bounding circle cannot reach any vertex.
            float removed = r.Solver.CarveCell(c, 0f, s.CellRad[c] * 4f, 0f, 1f);

            Assert.Equal(0f, removed);
            Assert.Equal(len, s.PolyLen[c]);
            Assert.Equal(area, s.CellArea[c]);
            Assert.Equal(rad, s.CellRad[c]);
            Assert.Equal(rx, s.CellRx[c]);
            for (int v = 0; v < len; v++)
            {
                Assert.Equal(bx[v], s.PolyX[off + v]);
                Assert.Equal(by[v], s.PolyY[off + v]);
            }
        }
    }
}
