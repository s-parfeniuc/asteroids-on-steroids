namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// Optional advisory interface for systems that want to declare their
/// computation model and component access pattern.
///
/// The engine does not enforce this at runtime. It exists so that future
/// tooling (a parallel wave scheduler, debug overlays, automated tests)
/// can reason about systems without inspecting their source.
///
/// Systems that do not implement this interface are treated as opaque and
/// placed in their own sequential slot by any future scheduler.
/// </summary>
public interface ISystemMetadata
{
    /// <summary>Primary computation model. Determines which parallelism strategy applies.</summary>
    ComputationModel Model { get; }

    /// <summary>
    /// Which component types this system reads or writes.
    /// Null means "unknown — opt out of scheduling analysis".
    /// Used by a future wave scheduler to detect write-write and write-read conflicts.
    /// </summary>
    ComponentAccess[]? DeclaredAccess { get; }
}

// ---------------------------------------------------------------------------

public enum ComputationModel
{
    IndependentTransform = 1, // per-entity, no cross-entity reads, no events  → ForEachParallel
    Aggregating          = 2, // per-entity + shared accumulator               → ForEachParallel + Interlocked
    EventPublishing      = 3, // per-entity, may publish events                → ForEachParallel + ConcurrentQueue
    EventConsuming       = 4, // reacts to events; no ownership guarantee      → sequential only
    Singleton            = 5, // no entity iteration (input, camera, audio…)   → sequential only
    CrossEntityRead      = 6, // reads one external entity per iteration       → ForEachParallel (cache external)
    CustomParallel       = 7, // bespoke pipeline (CollisionSystem)            → see §5.9 of spec
}

// ---------------------------------------------------------------------------

public readonly struct ComponentAccess
{
    public Type       ComponentType { get; init; }
    public AccessMode Mode          { get; init; }

    public static ComponentAccess Read<T>()  => new() { ComponentType = typeof(T), Mode = AccessMode.Read  };
    public static ComponentAccess Write<T>() => new() { ComponentType = typeof(T), Mode = AccessMode.Write };
}

public enum AccessMode { Read, Write }
