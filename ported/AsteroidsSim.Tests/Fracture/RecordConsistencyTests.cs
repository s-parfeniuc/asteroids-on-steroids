using System;
using System.Collections.Generic;
using System.Linq;
using AsteroidsSim.Fracture;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The touch records are the adjacency truth and every polygon is derived from them, so any
/// disagreement between the two is a bug — not a tolerance to tune. <see cref="SideAudit"/> is the
/// specification; this asserts it holds at build and through the impact, on every reference scene
/// and on the configurations that once produced an internal surface.
/// </summary>
/// <remarks>
/// The <c>repro:</c> rows keep the exact tuning, grain and speed that exposed a bug, because the
/// mesh is the repro: grain 170 was the 224/245 shave, grain 255 the build-time label/record
/// contradiction at a near-4-valent vertex, steel-on-rock at grain 900 the 62/49/35 uncollapsed
/// stub. A new internal surface should become a new row, not a new tolerance.
/// </remarks>
public class RecordConsistencyTests
{
    private readonly ITestOutputHelper _out;
    public RecordConsistencyTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Scenes()
    {
        foreach (string n in Scenarios.ReferenceNames) yield return new object[] { n };
        yield return new object[] { "repro: collide/rock g170" };
        yield return new object[] { "repro: steel/steel g255 2250" };
        yield return new object[] { "repro: steel/rock g900 900" };
    }

    private static Scenarios.Result Build(string name)
    {
        var t = SimTuning.Default;
        switch (name)
        {
            case "repro: collide/rock g170":
                t.ToughnessScale = 1.1f; t.CarveContinuity = 0.75f; t.CrushConfine = 0.10f;
                return Scenarios.Collide(t, Material.Rock, 600f, 170f);
            case "repro: steel/steel g255 2250":
            {
                t.ToughnessScale = 1.7f;
                var hard = new Material("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 2.0e6f, 0.01875f, 0.50f, 2.5f);
                return Scenarios.Projectile(t, hard, 2250f, 5.5f, 255f, impactor: hard);
            }
            case "repro: steel/rock g900 900": return Scenarios.Projectile(t, Material.Rock, 900f, 3f, 900f, impactor: Material.Steel);
            default: return Scenarios.Reference(name, t);
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void RecordsAndPolygonsAgreeThroughTheImpact(string scene)
    {
        var r = Build(scene);
        var rep = new SideAudit.Report { All = new List<string>() };

        SideAudit.Audit(r.State, rep, 0);
        Assert.True(rep.Total == 0, $"{scene}: {rep.Total} violations at BUILD — {rep.FirstDetail}");

        for (int tick = 1; tick <= 150; tick++)
        {
            r.Solver.Step();
            SideAudit.Audit(r.State, rep, tick);
            if (rep.Total > 0)
            {
                foreach (string line in rep.All!.Take(6)) _out.WriteLine(line);
                Assert.Fail($"{scene}: first violation at tick {tick} — {rep.FirstDetail}");
            }
        }
        _out.WriteLine($"{scene}: 0 violations over 150 ticks");
    }
}
