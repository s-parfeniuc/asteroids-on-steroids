using AsteroidsSim.Fracture;
using Xunit;
using Xunit.Abstractions;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The lockstep contract: the same build must produce the same state, whatever the thread count.
/// </summary>
public class DeterminismTests
{
    private readonly ITestOutputHelper _out;
    public DeterminismTests(ITestOutputHelper output) => _out = output;

    private static string RunAndFingerprint(int ticks, SimJobs? jobs = null)
    {
        var r = Scenarios.Reference("collide", SimTuning.Default);
        r.Solver.Jobs = jobs;
        for (int i = 0; i < ticks; i++) r.Solver.Step();
        return SimFingerprint.Hex(r.State);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 4)]
    [InlineData(3, 16)]
    [InlineData(7, 64)]
    [InlineData(11, 256)]
    public void ParallelMatchesSequentialExactly(int workers, int chunks)
    {
        // Chunks write only their own bodies and cross-chunk accumulators merge in chunk order, so
        // the result may not depend on how many workers ran or how they interleaved.
        string expected = RunAndFingerprint(200);
        using var jobs = new SimJobs(workers, chunks);
        Assert.Equal(expected, RunAndFingerprint(200, jobs));
    }

    [Fact]
    public void SameBuildProducesTheSameStateTwice()
    {
        string a = RunAndFingerprint(120);
        string b = RunAndFingerprint(120);
        _out.WriteLine($"fingerprint: {a}");
        Assert.Equal(a, b);
    }

    [Fact]
    public void FingerprintChangesWhenStateChanges()
    {
        // A hash that never changes would pass the test above while detecting nothing.
        var r = Scenarios.Reference("collide", SimTuning.Default);
        string atStart = SimFingerprint.Hex(r.State);
        for (int i = 0; i < 30; i++) r.Solver.Step();
        Assert.NotEqual(atStart, SimFingerprint.Hex(r.State));
    }

    [Fact]
    public void DifferentScenariosDoNotCollide()
    {
        var t = SimTuning.Default;
        float grain = Scenarios.FloorGrain(t, Material.Rock);
        var a = Scenarios.Collide(t, Material.Rock, 600f, grain);
        var b = Scenarios.Collide(t, Material.Rock, 601f, grain);
        for (int i = 0; i < 60; i++) { a.Solver.Step(); b.Solver.Step(); }
        Assert.NotEqual(SimFingerprint.Hex(a.State), SimFingerprint.Hex(b.State));
    }
}
