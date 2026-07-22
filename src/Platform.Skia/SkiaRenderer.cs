using System.Numerics;
using SkiaSharp;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Platform.Skia;

/// <summary>
/// SkiaSharp implementation of the engine's IRenderer + optional capabilities (IPostEffects,
/// IRenderStats, IParticleBatch, IMeshBatch, ILineBatch). Surface-agnostic: it draws into whatever
/// <see cref="SKSurface"/> it is handed, so both backends reuse it — the SDL backend gives it a GL
/// surface on an SDL window, the WinForms backend gives it a GL surface on an SKGLControl.
/// </summary>
public sealed class SkiaRenderer : IRenderer, IPostEffects, IRenderStats, IParticleBatch, IMeshBatch, ILineBatch, IDisposable
{
    private readonly SKSurface _surface;
    private readonly SKCanvas _canvas;
    private readonly int _width, _height;
    private long _drawCalls;                       // cumulative primitive count (IRenderStats)
    public long DrawCallCount => _drawCalls;

    // Batched-sprite (particle) draw state: a soft round-dot atlas + reused per-instance arrays,
    // submitted as one DrawAtlas call. See IParticleBatch.DrawSprites.
    private const int SpriteAtlasSize = 64;
    private SKImage? _spriteAtlas;
    private readonly SKPaint _spritePaint = new() { IsAntialias = true };
    private SKRotationScaleMatrix[] _spriteXf  = System.Array.Empty<SKRotationScaleMatrix>();
    private SKRect[]                _spriteTex = System.Array.Empty<SKRect>();
    private SKColor[]               _spriteCol = System.Array.Empty<SKColor>();

    // Batched-mesh (fracturable-body fill) state: one DrawVertices for the whole field.
    // AA off so adjacent triangles tile with no hairline seams (the outline pass draws the silhouette).
    private readonly SKPaint _meshPaint = new() { IsAntialias = false };
    private SKPoint[] _meshPos = System.Array.Empty<SKPoint>();
    private SKColor[] _meshCol = System.Array.Empty<SKColor>();

    // Batched-line (bullet tracers, outlines) state: one DrawPoints(Lines) per colour.
    private readonly SKPaint _linePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private SKPoint[] _linePts = System.Array.Empty<SKPoint>();
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
        _drawCalls++;
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawLine(a.X, a.Y, b.X, b.Y, _stroke);
    }

    public void DrawPolygon(ReadOnlySpan<Vector2> verts, Color color, float width = 1f)
    {
        if (verts.Length < 2) return;
        _drawCalls++;
        using var path = BuildPath(verts);
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawPath(path, _stroke);
    }

    public void FillPolygon(ReadOnlySpan<Vector2> verts, Color color)
    {
        if (verts.Length < 3) return;
        _drawCalls++;
        using var path = BuildPath(verts);
        _fill.Color = ToSk(color);
        _canvas.DrawPath(path, _fill);
    }

    public void FillPath(ReadOnlySpan<Vector2> verts, ReadOnlySpan<int> contourLengths, Color color)
    {
        _drawCalls++;
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
        _drawCalls++;
        _stroke.Color = ToSk(color); _stroke.StrokeWidth = width;
        _canvas.DrawCircle(center.X, center.Y, radius, _stroke);
    }

    public void FillCircle(Vector2 center, float radius, Color color)
    {
        _drawCalls++;
        _fill.Color = ToSk(color);
        _canvas.DrawCircle(center.X, center.Y, radius, _fill);
    }

    public void DrawText(string text, Vector2 topLeft, Color color, in FontSpec font)
    {
        _drawCalls++;
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

    public void DrawSprites(ReadOnlySpan<SpriteInstance> sprites)
    {
        int n = sprites.Length;
        if (n == 0) return;

        // DrawAtlas draws exactly array.Length instances (no count arg), so the arrays must be sized to
        // n. Reallocate only when n changes — steady particle counts reuse the buffers with no GC.
        if (_spriteXf.Length != n)
        {
            _spriteXf  = new SKRotationScaleMatrix[n];
            _spriteTex = new SKRect[n];
            _spriteCol = new SKColor[n];
        }

        var atlas = SpriteAtlas();
        var full  = new SKRect(0, 0, SpriteAtlasSize, SpriteAtlasSize);
        const float half = SpriteAtlasSize * 0.5f;
        for (int i = 0; i < n; i++)
        {
            var s = sprites[i];
            // Scale the atlas (diameter = SpriteAtlasSize) to the sprite's 2·radius, centred on Position.
            float scale = (s.Radius * 2f) / SpriteAtlasSize;
            _spriteXf[i]  = new SKRotationScaleMatrix(scale, 0f,
                                s.Position.X - scale * half, s.Position.Y - scale * half);
            _spriteTex[i] = full;
            _spriteCol[i] = ToSk(s.Color);
        }

        _drawCalls++;   // one batched submission, however many sprites
        // Modulate tints the white dot by the per-instance colour (rgb + alpha); the result composites
        // over the frame with the paint's default SrcOver.
        _canvas.DrawAtlas(atlas, _spriteTex, _spriteXf, _spriteCol, SKBlendMode.Modulate, _spritePaint);
    }

    public void FillMesh(ReadOnlySpan<Vector2> verts, ReadOnlySpan<Color> colors)
    {
        int n = verts.Length;
        if (n < 3) return;

        // DrawVertices draws exactly array.Length verts; resize only when the count changes.
        if (_meshPos.Length != n) { _meshPos = new SKPoint[n]; _meshCol = new SKColor[n]; }
        for (int i = 0; i < n; i++)
        {
            _meshPos[i] = new SKPoint(verts[i].X, verts[i].Y);
            _meshCol[i] = ToSk(colors[i]);
        }

        _drawCalls++;   // one batched submission for the whole mesh
        _canvas.DrawVertices(SKVertexMode.Triangles, _meshPos, _meshCol, _meshPaint);
    }

    public void DrawLines(ReadOnlySpan<Vector2> endpoints, Color color, float width)
    {
        int n = endpoints.Length;
        if (n < 2) return;

        if (_linePts.Length != n) _linePts = new SKPoint[n];
        for (int i = 0; i < n; i++) _linePts[i] = new SKPoint(endpoints[i].X, endpoints[i].Y);

        _linePaint.Color = ToSk(color);
        _linePaint.StrokeWidth = width;
        _drawCalls++;   // one batched submission for all the segments
        _canvas.DrawPoints(SKPointMode.Lines, _linePts, _linePaint);
    }

    // A soft round dot: solid core with an anti-aliased falloff at the rim, so scaled instances read
    // like the anti-aliased FillCircle they replace. Built once, lazily.
    private SKImage SpriteAtlas()
    {
        if (_spriteAtlas != null) return _spriteAtlas;
        var info = new SKImageInfo(SpriteAtlasSize, SpriteAtlasSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        surf.Canvas.Clear(SKColors.Transparent);
        float r = SpriteAtlasSize * 0.5f;
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(r, r), r,
            new[] { new SKColor(255, 255, 255, 255), new SKColor(255, 255, 255, 255), new SKColor(255, 255, 255, 0) },
            new[] { 0f, 0.8f, 1f }, SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Shader = shader };
        surf.Canvas.DrawCircle(r, r, r, p);
        _spriteAtlas = surf.Snapshot();
        return _spriteAtlas;
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
        _spritePaint.Dispose();
        _spriteAtlas?.Dispose();
        _meshPaint.Dispose();
        _linePaint.Dispose();
        foreach (var p in _textPaints.Values) p.Dispose();
        _textPaints.Clear();
    }
}
