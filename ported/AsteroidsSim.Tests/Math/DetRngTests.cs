using System;
using System.Collections.Generic;
using AsteroidsSim.Math;
using Xunit;

namespace AsteroidsSim.Tests.Math;

public sealed class DetRngTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = DetRng.FromSeed(12345);
        var b = DetRng.FromSeed(12345);
        for (int i = 0; i < 10_000; i++)
            Assert.Equal(a.NextUInt(RngStream.Waves), b.NextUInt(RngStream.Waves));
    }

    [Fact]
    public void DifferentSeeds_Diverge()
    {
        var a = DetRng.FromSeed(1);
        var b = DetRng.FromSeed(2);
        int same = 0;
        for (int i = 0; i < 1000; i++)
            if (a.NextUInt(RngStream.Waves) == b.NextUInt(RngStream.Waves)) same++;
        Assert.True(same < 5, $"{same} collisions in 1000 draws — streams are correlated");
    }

    /// <summary>
    /// The reason substreams exist: drawing from one must never perturb another.
    /// In the current game a shared <c>Random</c> means adding a particle effect
    /// silently shifts every subsequent wave roll.
    /// </summary>
    [Fact]
    public void Substreams_AreIndependent()
    {
        var undisturbed = DetRng.FromSeed(999);
        var expected = new uint[100];
        for (int i = 0; i < 100; i++) expected[i] = undisturbed.NextUInt(RngStream.Waves);

        var disturbed = DetRng.FromSeed(999);
        for (int i = 0; i < 100; i++)
        {
            // Hammer every other stream between draws.
            for (int k = 0; k < 37; k++)
            {
                disturbed.NextUInt(RngStream.Tessellation);
                disturbed.NextUInt(RngStream.Ai);
                disturbed.NextUInt(RngStream.Loot);
                disturbed.NextFloat(RngStream.Weapons);
                disturbed.NextInt(RngStream.Clusters, 10);
            }
            Assert.Equal(expected[i], disturbed.NextUInt(RngStream.Waves));
        }
    }

    [Fact]
    public void NextFloat_IsInUnitInterval()
    {
        var r = DetRng.FromSeed(7);
        for (int i = 0; i < 200_000; i++)
        {
            float v = r.NextFloat(RngStream.Ai);
            Assert.True(v >= 0f && v < 1f, $"NextFloat returned {v:R}");
        }
    }

    [Fact]
    public void NextFloat_IsExactlyRepresentable()
    {
        // Built from the top 24 bits scaled by 2^-24, so every result is exact —
        // no rounding, therefore no opportunity for platform variance.
        var r = DetRng.FromSeed(7);
        for (int i = 0; i < 50_000; i++)
        {
            float v = r.NextFloat(RngStream.Ai);
            Assert.Equal(v, (float)(double)v);
            Assert.Equal(v * 16777216f, MathF.Truncate(v * 16777216f));
        }
    }

    [Fact]
    public void BoundedInt_StaysInRange_AndCoversIt()
    {
        var r = DetRng.FromSeed(42);
        var seen = new HashSet<int>();
        for (int i = 0; i < 100_000; i++)
        {
            int v = r.NextInt(RngStream.Loot, 10);
            Assert.InRange(v, 0, 9);
            seen.Add(v);
        }
        Assert.Equal(10, seen.Count);
    }

    [Fact]
    public void BoundedInt_IsRoughlyUniform()
    {
        var r = DetRng.FromSeed(4242);
        var counts = new int[16];
        const int n = 320_000;
        for (int i = 0; i < n; i++) counts[r.NextInt(RngStream.Loot, 16)]++;

        int expected = n / 16;
        foreach (int c in counts)
            Assert.InRange(c, (int)(expected * 0.95), (int)(expected * 1.05));
    }

    [Fact]
    public void Rng_IsValueType_SoSnapshotIsACopy()
    {
        var original = DetRng.FromSeed(555);
        DetRng snapshot = original;                  // struct copy == snapshot

        for (int i = 0; i < 100; i++) original.NextUInt(RngStream.Waves);
        Assert.NotEqual(snapshot, original);

        original = snapshot;                         // restore
        var fresh = DetRng.FromSeed(555);
        for (int i = 0; i < 100; i++)
            Assert.Equal(fresh.NextUInt(RngStream.Waves), original.NextUInt(RngStream.Waves));
    }

    [Fact]
    public void ZeroBound_IsSafe()
    {
        var r = DetRng.FromSeed(1);
        Assert.Equal(0u, r.NextUInt(RngStream.Ai, 0));
        Assert.Equal(0, r.NextInt(RngStream.Ai, 0));
        Assert.Equal(5, r.NextInt(RngStream.Ai, 5, 5));
    }
}
