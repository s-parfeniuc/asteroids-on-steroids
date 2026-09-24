using AsteroidsSim.Config;
using AsteroidsSim.Fracture;

namespace AsteroidsSim.Tests.Fracture;

/// <summary>
/// The configuration every test runs on: <c>Assets/sim.json</c>, loaded once. Tests that need a
/// variation copy <see cref="SimConfig.Tuning"/> (a struct) and change the copy.
/// </summary>
internal static class TestConfig
{
    public static readonly SimConfig Value = SimConfigFile.Load();

    public static SimTuning Tuning => Value.Tuning;

    public static Material Material(string name) => Value.Material(name);
}
