using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Optional renderer capability: draw many line segments of one colour/width in a <b>single</b>
/// submission. Feature-detected via <c>renderer as ILineBatch</c>, like <see cref="IPostEffects"/>;
/// callers fall back to per-segment <c>DrawLine</c> when a backend lacks it.
///
/// Used to collapse the bullet-tracer storm (constant-colour glow/core lines, ~3 draws per bullet)
/// into a couple of calls.
/// </summary>
public interface ILineBatch
{
    /// <summary>Endpoints come in pairs — (a0,b0, a1,b1, …) — each pair one segment, all drawn with
    /// <paramref name="color"/> and <paramref name="width"/>. Positions are in the current transform space.</summary>
    void DrawLines(ReadOnlySpan<Vector2> endpoints, Color color, float width);
}
