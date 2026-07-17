using System.Numerics;
using SkiaSharp;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Platform.Sdl;

/// <summary>
/// SkiaSharp implementation of the engine's IRenderer. Draws into the SKCanvas
/// owned by <see cref="SdlGameWindow"/>; the window uploads and presents the
/// backing bitmap. This and SdlGameWindow are the only types that know Skia.
/// </summary>
public sealed class SkiaRenderer : IRenderer, IPostEffects, IDisposable
{
    private readonly SKSurface _surface;
    private readonly SKCanvas _canvas;
    private readonly int _width, _height;
    private readonly SKPaint  _fill   = new() { Style = SKPaintStyle.Fill,   IsAntialias = true };
    private readonly SKPaint  _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint  _warp   = new() { IsAntialias = true };
    private readonly Dictionary<string, SKPaint> _textPaints = new();

    // Reusable warp scratch (triangle list, 6 verts per mesh cell) — kept exactly sized for CreateCopy.
    private SKPoint[] _warpPos = System.Array.Empty<SKPoint>();
    private SKPoint[] _warpTex = System.Array.Empty<SKPoint>();

    public SkiaRenderer(SKSurface surface, int width, int height)
    {
        _surface = surface;
        _canvas  = surface.Canvas;
        _width   = width;
        _height  = height;
    }

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

    public void Distort(Vector2 regionMin, Vector2 regionMax, int gridX, int gridY, Func<Vector2, Vector2> sourceOf)
    {
        // Clamp the region to the framebuffer; bail on anything degenerate.
        int x0 = (int)MathF.Floor(Math.Clamp(MathF.Min(regionMin.X, regionMax.X), 0, _width));
        int y0 = (int)MathF.Floor(Math.Clamp(MathF.Min(regionMin.Y, regionMax.Y), 0, _height));
        int x1 = (int)MathF.Ceiling(Math.Clamp(MathF.Max(regionMin.X, regionMax.X), 0, _width));
        int y1 = (int)MathF.Ceiling(Math.Clamp(MathF.Max(regionMin.Y, regionMax.Y), 0, _height));
        if (x1 - x0 < 2 || y1 - y0 < 2) return;
        gridX = Math.Max(1, gridX); gridY = Math.Max(1, gridY);

        // Snapshot the region as it stands, so the warp resamples the frame drawn so far.
        _canvas.Flush();
        using var image = _surface.Snapshot(new SKRectI(x0, y0, x1, y1));
        if (image == null) return;
        // The snapshot's pixel (0,0) is surface (x0,y0); a plain image shader samples in those
        // local coords, so texture coords are sourceScreen − regionOrigin.
        var origin = new Vector2(x0, y0);
        using var shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        _warp.Shader = shader;

        int need = gridX * gridY * 6;
        if (_warpPos.Length != need) { _warpPos = new SKPoint[need]; _warpTex = new SKPoint[need]; }

        float cellW = (x1 - x0) / (float)gridX, cellH = (y1 - y0) / (float)gridY;
        SKPoint Dst(int i, int j) => new(x0 + i * cellW, y0 + j * cellH);
        SKPoint Src(SKPoint d)
        {
            Vector2 s = sourceOf(new Vector2(d.X, d.Y)) - origin;
            return new SKPoint(s.X, s.Y);
        }

        int v = 0;
        for (int j = 0; j < gridY; j++)
        for (int i = 0; i < gridX; i++)
        {
            SKPoint a = Dst(i, j),     b = Dst(i + 1, j);
            SKPoint c = Dst(i, j + 1), e = Dst(i + 1, j + 1);
            // two triangles: a,b,c and c,b,e
            _warpPos[v]   = a; _warpTex[v++]   = Src(a);
            _warpPos[v]   = b; _warpTex[v++]   = Src(b);
            _warpPos[v]   = c; _warpTex[v++]   = Src(c);
            _warpPos[v]   = c; _warpTex[v++]   = Src(c);
            _warpPos[v]   = b; _warpTex[v++]   = Src(b);
            _warpPos[v]   = e; _warpTex[v++]   = Src(e);
        }

        using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, _warpPos, _warpTex, null);
        _canvas.Save();
        _canvas.ResetMatrix();   // positions are absolute screen px
        _canvas.DrawVertices(verts, SKBlendMode.Src, _warp);   // Src = overwrite the region with its warp
        _canvas.Restore();
        _warp.Shader = null;
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
        _warp.Dispose();
        foreach (var p in _textPaints.Values) p.Dispose();
        _textPaints.Clear();
    }
}
