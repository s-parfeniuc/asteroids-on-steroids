using System.Numerics;

namespace AsteroidsEngine.Engine.Diagnostics;

/// <summary>
/// Categories of velocity/spin/force application, usable as a filter mask so a tuning
/// session can enable only the channels of interest.
/// </summary>
[Flags]
public enum ForceCat
{
    None        = 0,
    Integration = 1 << 0,   // v += (F/m)·dt ; ω += (τ/I)·dt   (per body, per step — verbose)
    Drag        = 1 << 1,   // v *= e^(-drag·dt)               (per body, per step — verbose)
    Thrust      = 1 << 2,   // an applied force (player engine, explosion, …)
    Contact     = 1 << 3,   // collision normal + friction impulses (net per contact per step)
    Separation  = 1 << 4,   // positional correction (Baumgarte push-apart; not a velocity)
    Recoil      = 1 << 5,   // impact momentum kick applied to the struck body
    Fling       = 1 << 6,   // fragment ejection velocity + impact-shear spin
    Debris      = 1 << 7,   // polygon-debris velocity + spin
    Spawn       = 1 << 8,   // initial asteroid / bullet velocity + spin
    Energy      = 1 << 9,   // impact energy + fracture budget (tuned scalars, not a velocity)

    All         = ~0,
    // A sensible default for tuning the destruction feel: every discrete event, but not
    // the per-step integration/drag firehose (enable those explicitly when needed).
    Default     = All & ~Integration & ~Drag,
}

/// <summary>
/// Zero-overhead-when-off diagnostic sink for every place the engine or game changes a
/// body's velocity or spin. Each call site formats the actual formula with its live
/// numbers, so the log reads as "result = term + term + …" for offline analysis.
///
/// Usage: guard each site with <see cref="On"/> (so the interpolated string is only built
/// when that channel is live), then <see cref="Write"/>. The game sets <see cref="Frame"/>
/// once per fixed step and may point <see cref="Sink"/> at a file.
/// </summary>
public static class ForceLog
{
    public static bool Enabled;
    public static ForceCat Categories = ForceCat.Default;
    public static int EntityFilter = -1;          // -1 = all entities; else only this id
    public static long Frame;                      // set by the game loop each fixed step
    public static Action<string> Sink = Console.WriteLine;

    /// <summary>Entity context for engine code that runs before its result entities exist
    /// (fragment construction): the source body's id. Set by the fracture path.</summary>
    public static int CurrentBody = -1;

    public static bool On(ForceCat cat, int entity = -1)
        => Enabled
           && (Categories & cat) != 0
           && (EntityFilter < 0 || entity < 0 || entity == EntityFilter);

    public static void Write(ForceCat cat, int entity, string detail)
        => Sink($"[f{Frame,7}] {cat,-11} e{entity,-5} {detail}");

    /// <summary>Compact vector formatter, e.g. (12.3,-4.5).</summary>
    public static string V(Vector2 v) => $"({v.X:0.##},{v.Y:0.##})";
}
