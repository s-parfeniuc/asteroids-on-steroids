using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Optional renderer capability: fill a flat triangle list with per-vertex colours in a <b>single</b>
/// GPU submission. Feature-detected via <c>renderer as IMeshBatch</c>, like <see cref="IPostEffects"/>;
/// callers fall back to per-polygon fills when a backend lacks it.
///
/// Used to draw a whole field of fracturable bodies as one seamless mesh — replacing the per-cell
/// <c>FillPolygon</c> storm (and its union-underlay overdraw) with one overdraw-free call.
/// </summary>
public interface IMeshBatch
{
    /// <summary>Every three consecutive entries in <paramref name="verts"/> form one triangle, coloured
    /// by the matching entries in <paramref name="colors"/>. Positions are in the current transform space.</summary>
    void FillMesh(ReadOnlySpan<Vector2> verts, ReadOnlySpan<Color> colors);
}
