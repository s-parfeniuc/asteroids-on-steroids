using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Immediate-mode 2D drawing abstraction — the rendering half of the Platform
/// Abstraction Layer (PAL). Engine render systems and games draw exclusively
/// through this interface; each platform backend (SkiaSharp, GDI+/WinForms, ...)
/// supplies an implementation. No engine code references a concrete UI toolkit.
///
/// Conventions:
///   • Y-down screen space.
///   • Fill/DrawPolygon take CONVEX polygons (matches the engine's collision
///     shapes); a concave silhouette is drawn as several convex parts.
///   • Antialiasing is a backend concern (on by default).
///   • DrawText positions text by its TOP-LEFT corner; the backend handles the
///     baseline so callers never touch font metrics.
///
/// Image drawing (DrawImage) is added together with IResourceManager in the asset
/// step; the demos draw only vector primitives + text today.
/// </summary>
public interface IRenderer
{
    /// <summary>Begin a frame, clearing the target to <paramref name="clear"/>.</summary>
    void Begin(Color clear);

    /// <summary>Push a transform (composed with the current top of stack). Used for
    /// the camera transform and per-entity transforms.</summary>
    void PushTransform(in Matrix3x2 transform);
    void PopTransform();

    void DrawLine(Vector2 a, Vector2 b, Color color, float width = 1f);

    void DrawPolygon(ReadOnlySpan<Vector2> verts, Color color, float width = 1f);
    void FillPolygon(ReadOnlySpan<Vector2> verts, Color color);

    /// <summary>Fills a multi-contour path as ONE region in a single draw, so adjacent
    /// contours (e.g. all cells of a body) have no anti-aliasing seam between them.
    /// Contours are concatenated in <paramref name="verts"/>; <paramref name="contourLengths"/>
    /// gives each contour's vertex count. Nonzero winding.</summary>
    void FillPath(ReadOnlySpan<Vector2> verts, ReadOnlySpan<int> contourLengths, Color color);

    void DrawCircle(Vector2 center, float radius, Color color, float width = 1f);
    void FillCircle(Vector2 center, float radius, Color color);

    void DrawText(string text, Vector2 topLeft, Color color, in FontSpec font);

    /// <summary>Measured (width, height) of <paramref name="text"/> in the given font.</summary>
    Vector2 MeasureText(string text, in FontSpec font);

    /// <summary>Finish the frame. The backend flushes its draw commands;
    /// call IGameWindow.Present() to swap/display the result.</summary>
    void End();
}

/// <summary>
/// Backend-agnostic font description. The backend resolves and caches the actual
/// platform font (SKTypeface, GDI+ Font, ...) keyed by these fields.
/// </summary>
public readonly struct FontSpec
{
    public readonly string Family;
    public readonly float  Size;
    public readonly bool   Bold;

    public FontSpec(string family, float size, bool bold = false)
    {
        Family = family; Size = size; Bold = bold;
    }
}
