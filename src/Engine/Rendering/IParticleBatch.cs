using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>One instance in a batched sprite draw: a soft round dot at <see cref="Position"/> with
/// the given radius and colour, in the renderer's current transform space.</summary>
public readonly struct SpriteInstance
{
    public readonly Vector2 Position;
    public readonly float   Radius;
    public readonly Color   Color;

    public SpriteInstance(Vector2 position, float radius, Color color)
    {
        Position = position;
        Radius   = radius;
        Color    = color;
    }
}

/// <summary>
/// Optional renderer capability: draw many soft round sprites (particles/motes) in a <b>single</b>
/// GPU submission instead of one call per dot. Feature-detected via <c>renderer as IParticleBatch</c>,
/// like <see cref="IPostEffects"/>; callers fall back to per-sprite fills when a backend lacks it.
/// Collapses thousands of per-particle draw calls into one, so particle cost stops scaling with count.
/// </summary>
public interface IParticleBatch
{
    void DrawSprites(ReadOnlySpan<SpriteInstance> sprites);
}
