using System;
using AsteroidsSim.Config;
using AsteroidsSim.Fracture;
using Xunit;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The configuration file is the single source of truth, so a key that silently fails to apply is a
/// correctness bug: every value must arrive, and anything unrecognised must be refused.
/// </summary>
public class ConfigFileTests
{
    [Fact]
    public void TheShippedFileLoadsAndNamesEveryReferenceMaterial()
    {
        SimConfig cfg = SimConfigFile.Load();
        foreach (string name in new[] { "rock", "glass", "steel" }) cfg.Material(name);
        Assert.True(cfg.Tuning.Substeps >= 1);
        Assert.True(cfg.Tuning.Constants.StableCfl > 0f);
    }

    [Fact]
    public void AnUnknownKeyIsRejected()
    {
        string json = System.IO.File.ReadAllText(SimConfigFile.Find())
            .Replace("\"relax\":", "\"relaxx\":");
        var e = Assert.Throws<FormatException>(() => SimConfigFile.Parse(json));
        Assert.Contains("relaxx", e.Message);
    }

    [Fact]
    public void AMissingKeyIsRejected()
    {
        string json = System.IO.File.ReadAllText(SimConfigFile.Find())
            .Replace("\"contactSlop\": 0.05,", "");
        var e = Assert.Throws<FormatException>(() => SimConfigFile.Parse(json));
        Assert.Contains("contactSlop", e.Message);
    }
}
