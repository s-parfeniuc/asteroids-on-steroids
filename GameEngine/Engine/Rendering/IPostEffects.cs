using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Optional screen-space post-processing, layered on top of <see cref="IRenderer"/>. A backend may
/// implement it (SkiaSharp does, via a framebuffer snapshot); callers feature-detect with
/// <c>renderer as IPostEffects</c> and simply skip the effect when it is null, so the engine keeps
/// its zero-UI-toolkit-dependency rule and headless/other backends still run.
/// </summary>
public interface IPostEffects
{
    /// <summary>
    /// Warp the frame drawn so far within the screen-space rectangle [<paramref name="regionMin"/>,
    /// <paramref name="regionMax"/>]: the pixel shown at screen point <c>p</c> is resampled from
    /// <c><paramref name="sourceOf"/>(p)</c> (also screen space). <paramref name="gridX"/>×
    /// <paramref name="gridY"/> is the warp mesh resolution — higher = smoother curves, more cost.
    /// Must be called in screen space (no camera transform pushed). A no-op for a degenerate region.
    /// </summary>
    void Distort(Vector2 regionMin, Vector2 regionMax, int gridX, int gridY, Func<Vector2, Vector2> sourceOf);
}
