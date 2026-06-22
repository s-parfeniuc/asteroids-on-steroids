using System.Numerics;
using SkiaSharp;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Platform.Sdl;

/// <summary>
/// SkiaSharp implementation of the engine's IRenderer. Draws into the SKCanvas
/// owned by <see cref="SdlGameWindow"/>; the window uploads and presents the
/// backing bitmap. This and SdlGameWindow are the only types that know Skia.
/// </summary>
public sealed class SkiaRenderer : IRenderer, IDisposable
{
    private readonly SKCanvas _canvas;
    private readonly SKPaint  _fill   = new() { Style = SKPaintStyle.Fill,   IsAntialias = true };
    private readonly SKPaint  _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly Dictionary<string, SKPaint> _textPaints = new();

    public SkiaRenderer(SKCanvas canvas) => _canvas = canvas;

    public void Begin(Color clear)
    {
        _canvas.ResetMatrix();
        _canvas.Clear(ToSk(clear));
    }

    public void End() => _canvas.Flush();

    public void PushTransform(in Matrix3x2 transform)
    {
        var m = ToSk(transform);
        _canvas.Save();
        _canvas.Concat(ref m);
    }

    public void PopTransform() => _canvas.Restore();

    public void DrawLine(Vector2 a, Vector2 b, Color color, float width = 1f)
    {
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawLine(a.X, a.Y, b.X, b.Y, _stroke);
    }

    public void DrawPolygon(ReadOnlySpan<Vector2> verts, Color color, float width = 1f)
    {
        if (verts.Length < 2) return;
        using var path = BuildPath(verts);
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawPath(path, _stroke);
    }

    public void FillPolygon(ReadOnlySpan<Vector2> verts, Color color)
    {
        if (verts.Length < 3) return;
        using var path = BuildPath(verts);
        _fill.Color = ToSk(color);
        _canvas.DrawPath(path, _fill);
    }

    public void FillPath(ReadOnlySpan<Vector2> verts, ReadOnlySpan<int> contourLengths, Color color)
    {
        using var path = new SKPath();   // default FillType = Winding (nonzero) → seamless union
        int off = 0;
        foreach (int len in contourLengths)
        {
            if (len >= 3)
            {
                path.MoveTo(verts[off].X, verts[off].Y);
                for (int i = 1; i < len; i++) path.LineTo(verts[off + i].X, verts[off + i].Y);
                path.Close();
            }
            off += len;
        }
        _fill.Color = ToSk(color);
        _canvas.DrawPath(path, _fill);
    }

    public void DrawCircle(Vector2 center, float radius, Color color, float width = 1f)
    {
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawCircle(center.X, center.Y, radius, _stroke);
    }

    public void FillCircle(Vector2 center, float radius, Color color)
    {
        _fill.Color = ToSk(color);
        _canvas.DrawCircle(center.X, center.Y, radius, _fill);
    }

    public void DrawText(string text, Vector2 topLeft, Color color, in FontSpec font)
    {
        var paint = TextPaint(font);
        paint.Color = ToSk(color);
        // SKCanvas draws text at the baseline; offset by -ascent to honour top-left.
        float baseline = topLeft.Y - paint.FontMetrics.Ascent;
        _canvas.DrawText(text, topLeft.X, baseline, paint);
    }

    public Vector2 MeasureText(string text, in FontSpec font)
    {
        var paint = TextPaint(font);
        var m = paint.FontMetrics;
        return new Vector2(paint.MeasureText(text), m.Descent - m.Ascent);
    }

    private SKPaint TextPaint(in FontSpec f)
    {
        string key = $"{f.Family}|{f.Size}|{f.Bold}";
        if (!_textPaints.TryGetValue(key, out var p))
        {
            p = new SKPaint
            {
                IsAntialias = true,
                TextSize    = f.Size,
                Typeface    = SKTypeface.FromFamilyName(
                                  f.Family, f.Bold ? SKFontStyle.Bold : SKFontStyle.Normal),
            };
            _textPaints[key] = p;
        }
        return p;
    }

    private static SKPath BuildPath(ReadOnlySpan<Vector2> verts)
    {
        var path = new SKPath();
        path.MoveTo(verts[0].X, verts[0].Y);
        for (int i = 1; i < verts.Length; i++) path.LineTo(verts[i].X, verts[i].Y);
        path.Close();
        return path;
    }

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);

    // Matrix3x2 (row-vector, M3x = translation) → SKMatrix.
    private static SKMatrix ToSk(in Matrix3x2 m) => new(
        m.M11, m.M21, m.M31,
        m.M12, m.M22, m.M32,
        0f, 0f, 1f);

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        foreach (var p in _textPaints.Values) p.Dispose();
        _textPaints.Clear();
    }
}
