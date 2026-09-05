using System;
using System.Collections.Generic;
using AsteroidsSim.Fracture;
using Xunit;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// Parity of body construction against the JavaScript reference
/// (<c>prototypes/stress-fracture-v9.html</c>, scenario "collide", material rock, defaults).
/// </summary>
/// <remarks>
/// <para><b>Why the outline is injected rather than generated.</b> The reference computes its
/// outline ring with double-precision trig, which the deterministic contract forbids here. Feeding
/// both implementations the same outline isolates what this test is actually about — the seed scan,
/// the RNG stream, half-plane clipping, cell integrals and bond detection — from a difference that
/// is understood and deliberate. The generator is still advanced by the 20 draws the ring consumed,
/// so the seed scan starts from the same state the reference had.</para>
///
/// <para>Geometry here uses only <c>+ - * /</c> and <c>Sqrt</c> in double, all exactly specified,
/// so agreement should be to the last bits rather than approximate. Anything worse than that means
/// a transcription error, not accumulated error.</para>
/// </remarks>
public class TessellationParityTests
{
    // makeBlob(250, 350, 130, 120) from seed 12345, captured from the reference.
    private static List<Vec2d> ReferenceOutline() => new()
    {
        new Vec2d(113.13313470905845, 308.9500858818313),
        new Vec2d(134.96794555513594, 272.85321879567215),
        new Vec2d(164.98067034743565, 241.98239856212155),
        new Vec2d(208.596973423614, 232.37654158457914),
        new Vec2d(289.9151987782641, 236.6033690803619),
        new Vec2d(362.0484280839595, 274.85420166232603),
        new Vec2d(384.4731678375874, 309.66801768129335),
        new Vec2d(394.96752195414155, 350),
        new Vec2d(354.7735330977238, 420.26685625065255),
        new Vec2d(332.24265015474, 454.48981239811064),
        new Vec2d(290.2631112910962, 464.385029024057),
        new Vec2d(250, 465.6071895815432),
        new Vec2d(168.70249906128186, 453.2889942814215),
        new Vec2d(132.2872305101651, 428.944615191068),
        new Vec2d(116.71827260663676, 389.9746385028141),
    };

    /// <summary>
    /// Relative comparison, for quantities whose magnitude makes an absolute tolerance meaningless
    /// (inertia is ~1e9, stiffness ~1e7). The values are stored as float, so ~1e-6 relative is the
    /// tightest a correct port can be.
    /// </summary>
    private static void AssertRelative(double expected, double actual, double tol, string what)
    {
        double denom = System.Math.Max(System.Math.Abs(expected), 1e-300);
        double rel = System.Math.Abs(actual - expected) / denom;
        Assert.True(rel <= tol,
            $"{what}: expected {expected:R}, got {actual:R}, relative error {rel:E3} > {tol:E1}");
    }

    private static SimState BuildReferenceBody()
    {
        var s = new SimState();
        var rng = new ProtoRng(ProtoRng.DefaultSeed);

        // The ring consumed 20 draws before the seed scan began.
        for (int i = 0; i < 20; i++) rng.NextDouble();

        var tune = SimTuning.Default;
        BodyBuilder.AddBody(s, ref rng, tune, ReferenceOutline(),
            velX: 300f, velY: 0f, omega: 0.4f, Material.Rock, grain: 900f);
        return s;
    }

    [Fact]
    public void RngMatchesTheReferenceStreamAfterTheRing()
    {
        var rng = new ProtoRng(ProtoRng.DefaultSeed);
        for (int i = 0; i < 20; i++) rng.NextDouble();
        // State captured from the reference after makeBlob(250,350,130,120).
        Assert.Equal(2271590237u, rng.State);
    }

    [Fact]
    public void TessellationTopologyMatchesTheReference()
    {
        var s = BuildReferenceBody();
        Assert.Equal(1, s.BodyCount);
        Assert.Equal(59, s.CellCount);   // reference: body0 cells=59
        Assert.Equal(146, s.BondCount);  // reference: body0 bonds=146
        Assert.Equal(340, s.PolyCount);  // reference: body0 polygon vertices=340
        Assert.Equal(129, s.GrpCount);   // reference: body0 shared-vertex groups=129
                                         // (the reference's headline "245" is both bodies)
    }

    [Fact]
    public void BodyAggregatesMatchTheReference()
    {
        var s = BuildReferenceBody();

        // Reference: M=157302.661680  I=1336269038.649  cx=247.272360  cy=349.069984
        Assert.Equal(157302.661680, s.BodyM[0], 1);
        Assert.Equal(247.272360, s.BodyX[0], 3);
        Assert.Equal(349.069984, s.BodyY[0], 3);
        AssertRelative(1336269038.649, s.BodyI[0], 1e-6, "body inertia");

        double area = 0, perim = 0;
        for (int c = 0; c < s.CellCount; c++) { area += s.CellArea[c]; perim += s.CellPerim[c]; }
        Assert.Equal(52434.2206, area, 1);    // reference: area=52434.2206
        Assert.Equal(7024.3242, perim, 1);    // reference: perim=7024.3242
    }

    [Fact]
    public void FirstCellMatchesTheReference()
    {
        var s = BuildReferenceBody();

        // Reference: cell0 rx=-26.152422 ry=-106.278850 area=864.643851
        //            perim=124.446900 rad=26.947353 nverts=6 surf=true
        Assert.Equal(-26.152422, s.CellRx[0], 3);
        Assert.Equal(-106.278850, s.CellRy[0], 3);
        Assert.Equal(864.643851, s.CellArea[0], 2);
        Assert.Equal(124.446900, s.CellPerim[0], 3);
        Assert.Equal(26.947353, s.CellRad[0], 3);
        Assert.Equal(6, s.PolyLen[0]);
        Assert.True(s.CellSurf[0]);
    }

    [Fact]
    public void FirstBondMatchesTheReference()
    {
        var s = BuildReferenceBody();

        // Reference: bond0 len=17.866903 str=1.191285 k0=12078026.167
        //            s0=0.357386 nx=0.999194
        Assert.Equal(17.866903, s.BondLen[0], 3);
        Assert.Equal(0.999194, s.BondNx[0], 4);
        AssertRelative(12078026.167, s.BondK0[0], 1e-6, "bond stiffness");

        // str goes through single-precision Pow/Log, so it agrees to about seven digits rather
        // than exactly — a deliberate consequence of the banned double transcendentals.
        Assert.Equal(1.191285, s.BondStr[0], 4);
        Assert.Equal(0.357386, s.BondS0[0], 4);
    }

    [Fact]
    public void EveryCellHasPositiveMassAndFiniteInertia()
    {
        var s = BuildReferenceBody();
        for (int c = 0; c < s.CellCount; c++)
        {
            Assert.True(s.CellM[c] > 0f, $"cell {c} mass");
            Assert.True(float.IsFinite(s.CellIc[c]) && s.CellIc[c] > 0f, $"cell {c} inertia");
            Assert.True(float.IsFinite(s.CellIm[c]), $"cell {c} inverse mass");
        }
    }

    [Fact]
    public void SharedVerticesAreGroupedInThrees()
    {
        var s = BuildReferenceBody();

        // A Voronoi vertex is shared by about three cells; boundary vertices by one or two. What
        // matters is that no group is empty and every polygon vertex belongs to exactly one.
        int members = 0;
        for (int g = 0; g < s.GrpCount; g++)
        {
            Assert.True(s.GrpLen[g] >= 1, $"group {g} is empty");
            members += s.GrpLen[g];
        }
        Assert.Equal(s.PolyCount, members);

        for (int v = 0; v < s.PolyCount; v++)
            Assert.InRange(s.PolyGroup[v], 0, s.GrpCount - 1);
    }

    [Fact]
    public void AdjacencyIsSymmetricAndComplete()
    {
        var s = BuildReferenceBody();
        int total = 0;
        for (int c = 0; c < s.CellCount; c++) total += s.AdjLen[c];
        Assert.Equal(2 * s.BondCount, total);

        for (int k = 0; k < s.BondCount; k++)
        {
            Assert.Contains(k, Slice(s, s.BondA[k]));
            Assert.Contains(k, Slice(s, s.BondB[k]));
        }

        static IEnumerable<int> Slice(SimState s, int cell)
        {
            for (int i = 0; i < s.AdjLen[cell]; i++) yield return s.AdjBond[s.AdjOff[cell] + i];
        }
    }
}
