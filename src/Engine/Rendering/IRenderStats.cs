namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Optional renderer capability exposing a running count of primitive draw calls issued
/// (lines, polygons, paths, circles, text). Feature-detected via <c>renderer as IRenderStats</c>,
/// like <see cref="IPostEffects"/>; a backend may not implement it. Cumulative and monotonic —
/// callers sample it before/after a draw block and take the delta to count that block's calls.
/// </summary>
public interface IRenderStats
{
    long DrawCallCount { get; }
}
