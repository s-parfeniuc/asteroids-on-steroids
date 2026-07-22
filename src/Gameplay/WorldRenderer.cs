using System.Numerics;
using System.Runtime.InteropServices;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Effects;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;
using AsteroidsGame.Config;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Renders the shared game world — background grid, fracturable bodies (cells + outline +
/// cracks), polygon debris, particle FX, bullet tracers, black holes, and the player aim
/// line — under the camera transform. Used by both the game and the demo so the playfield
/// looks identical; each shell draws its own HUD/overlay on top.
/// </summary>
public sealed class WorldRenderer
{
    /// <summary>Debug toggles (bound in PlayingState): skip particle drawing (V) / body fills (C) to
    /// A/B each one's cost. Outlines still draw when fills are off, so the toggle isolates fill alone.</summary>
    public bool DrawParticles = true;
    public bool DrawFills     = true;

    private const float CellPad     = 0.75f;   // per-cell overdraw (world px) to hide AA seams

    private readonly List<Vector2> _meshVerts = new();
    private readonly List<int>     _meshLens  = new();
    private readonly List<Vector2> _triVerts  = new();   // batched body fill: world-space triangle list
    private readonly List<Color>   _triCols   = new();   // …and its matching per-vertex colours
    private readonly List<Vector2> _lineGlow  = new();   // batched tracer glow segments (a,b pairs)
    private readonly List<Vector2> _lineCore  = new();   // batched tracer core segments (a,b pairs)
    private readonly List<SpriteInstance> _headBuf = new();   // batched tracer heads → sprite batch
    private readonly List<SpriteInstance> _starBuf = new();   // batched starfield → sprite batch
    private readonly List<Vector2> _dbuf      = new();
    private readonly List<Vector2> _cellBuf   = new();   // one body cell polygon (reused)
    private bool[] _brokenScratch = System.Array.Empty<bool>();   // per-bond broken flags (reused)
    private bool[] _pulvFalse     = System.Array.Empty<bool>();   // all-false pulverized (no live process)

    // ── Parallax starfield (screen-space; layers scroll at fractions of camera motion for depth) ──
    private readonly record struct StarLayer(Vector2[] Stars, float Parallax, float Size, Color Color);
    private StarLayer[]? _starLayers;

    private void InitStars(float sw, float sh)
    {
        var rng = new Random(1337);
        StarLayer Make(int n, float parallax, float size, Color col)
        {
            var s = new Vector2[n];
            for (int i = 0; i < n; i++) s[i] = new Vector2((float)rng.NextDouble() * sw, (float)rng.NextDouble() * sh);
            return new StarLayer(s, parallax, size, col);
        }
        _starLayers = new[]
        {
            Make(150, 0.12f, 1.0f, new Color(110, 120, 160, 170)),  // far, dim, slow
            Make(90,  0.28f, 1.5f, new Color(165, 175, 215, 205)),  // mid
            Make(45,  0.56f, 2.2f, new Color(225, 230, 255, 235)),  // near, bright, fast
        };
    }

    private void DrawBackground(IRenderer r, Camera camera)
    {
        float sw = camera.ScreenWidth, sh = camera.ScreenHeight;
        if (_starLayers == null) InitStars(sw, sh);
        var sprites = r as IParticleBatch;   // hundreds of stars/frame → one batched submission
        _starBuf.Clear();
        foreach (var layer in _starLayers!)
        {
            // Scroll opposite to camera at the layer's parallax rate, wrapped into one tile.
            float ox = -(camera.Position.X * layer.Parallax) % sw; if (ox > 0) ox -= sw;
            float oy = -(camera.Position.Y * layer.Parallax) % sh; if (oy > 0) oy -= sh;
            foreach (var star in layer.Stars)
                for (float tx = 0; tx <= sw; tx += sw)
                    for (float ty = 0; ty <= sh; ty += sh)
                    {
                        float x = star.X + ox + tx, y = star.Y + oy + ty;
                        if (x >= -2 && x <= sw + 2 && y >= -2 && y <= sh + 2)
                        {
                            if (sprites != null) _starBuf.Add(new SpriteInstance(new Vector2(x, y), layer.Size, layer.Color));
                            else r.FillCircle(new Vector2(x, y), layer.Size, layer.Color);
                        }
                    }
        }
        if (sprites != null && _starBuf.Count > 0)
            sprites.DrawSprites(CollectionsMarshal.AsSpan(_starBuf));
    }

    public void Draw(IRenderer r, World world, Camera camera, ParticleSystem fx,
                     Entity player, VfxConfig vfx, float alpha)
    {
        DrawBackground(r, camera);   // screen-space, under the world transform
        r.PushTransform(camera.GetViewMatrix());
        DrawBodies(r, world, camera, alpha);
        DrawDebris(r, world, camera, alpha);
        if (DrawParticles) fx.Draw(r);   // debug toggle (V) — A/B the particle-fill cost
        DrawBullets(r, world, camera, vfx, alpha);
        DrawBlackHoles(r, world, alpha);
        DrawShockwaves(r, world, alpha);
        DrawPlayerAim(r, world, player, alpha);
        r.PopTransform();
    }

    private void DrawBodies(IRenderer r, World world, Camera camera, float alpha)
    {
        // Two passes so all the cell fills batch into one mesh under all the outlines:
        //   1. fill every visible body's cells (one big DrawVertices when IMeshBatch is available),
        //   2. draw outlines + cracks on top.
        if (DrawFills) DrawBodyFills(r, world, camera, alpha);
        DrawBodyOutlines(r, world, camera, alpha);
    }

    /// <summary>Fill pass: fan-triangulate every visible body's live cells into one shared world-space
    /// mesh and submit it in a single <see cref="IMeshBatch.FillMesh"/> — replacing ~1 union-path +
    /// N per-cell fills per body (and the 2× union+inflation overdraw) with one overdraw-free call.
    /// Falls back to the original union-underlay + per-cell fills when the backend lacks IMeshBatch.</summary>
    private void DrawBodyFills(IRenderer r, World world, Camera camera, float alpha)
    {
        var mesh = r as IMeshBatch;
        _triVerts.Clear(); _triCols.Clear();

        world.ForEach<Transform, FracturableBody, BodyColor>(
            (Entity e, ref Transform t, ref FracturableBody fb, ref BodyColor col) =>
        {
            var (pos, rot) = Interp(t, alpha);
            if (!Visible(camera, pos, 300f)) return;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);

            bool[]? pulv = world.HasComponent<FractureProcess>(e)
                ? world.GetComponent<FractureProcess>(e).Pulverized : null;
            bool shade = fb.Cells.Length > 0 && fb.Cells[0].FillColor.A != 0;

            if (mesh != null)
            {
                // Inflate each cell slightly from its centroid (as the old per-cell path did) so
                // neighbours overlap by CellPad — no seams even without the union underlay — then
                // fan-triangulate into the shared mesh with the cell's baked shade as the vertex colour.
                Vector2 W(Vector2 v, Vector2 cen)
                {
                    Vector2 d = v - cen; float dl = d.Length();
                    Vector2 iv = dl > 1e-4f ? v + d / dl * CellPad : v;
                    return new Vector2(iv.X * c - iv.Y * s + pos.X, iv.X * s + iv.Y * c + pos.Y);
                }
                for (int ci = 0; ci < fb.Cells.Length; ci++)
                {
                    if (pulv?[ci] == true) continue;
                    var lv = fb.Cells[ci].Local;
                    if (lv.Length < 3) continue;
                    var cen = fb.Cells[ci].Centroid;
                    Color cc = shade ? fb.Cells[ci].FillColor : col.Fill;
                    Vector2 v0 = W(lv[0], cen), vp = W(lv[1], cen);
                    for (int k = 2; k < lv.Length; k++)
                    {
                        Vector2 vk = W(lv[k], cen);
                        _triVerts.Add(v0); _triVerts.Add(vp); _triVerts.Add(vk);
                        _triCols.Add(cc);  _triCols.Add(cc);  _triCols.Add(cc);
                        vp = vk;
                    }
                }
                return;
            }

            // Fallback (no IMeshBatch): union underlay, then inflated per-cell fills.
            _meshVerts.Clear(); _meshLens.Clear();
            for (int ci = 0; ci < fb.Cells.Length; ci++)
            {
                if (pulv?[ci] == true) continue;
                var lv = fb.Cells[ci].Local;
                foreach (var v in lv)
                    _meshVerts.Add(new Vector2(v.X * c - v.Y * s + pos.X, v.X * s + v.Y * c + pos.Y));
                _meshLens.Add(lv.Length);
            }
            if (_meshVerts.Count > 0)
                r.FillPath(CollectionsMarshal.AsSpan(_meshVerts),
                           CollectionsMarshal.AsSpan(_meshLens), col.Fill);
            if (shade)
            {
                for (int ci = 0; ci < fb.Cells.Length; ci++)
                {
                    if (pulv?[ci] == true) continue;
                    var lv  = fb.Cells[ci].Local;
                    var cen = fb.Cells[ci].Centroid;
                    _cellBuf.Clear();
                    foreach (var v in lv)
                    {
                        Vector2 d = v - cen; float dl = d.Length();
                        Vector2 iv = dl > 1e-4f ? v + d / dl * CellPad : v;
                        _cellBuf.Add(new Vector2(iv.X * c - iv.Y * s + pos.X, iv.X * s + iv.Y * c + pos.Y));
                    }
                    r.FillPolygon(CollectionsMarshal.AsSpan(_cellBuf), fb.Cells[ci].FillColor);
                }
            }
        });

        if (mesh != null && _triVerts.Count >= 3)
            mesh.FillMesh(CollectionsMarshal.AsSpan(_triVerts), CollectionsMarshal.AsSpan(_triCols));
    }

    /// <summary>Outline pass: draw each visible body's silhouette + crack edges on top of the fills.
    /// Live break state comes from <c>Bond.Broken</c> (persists after the fracture process ends); an
    /// intact body uses its cached <see cref="RenderOutline"/>.</summary>
    private void DrawBodyOutlines(IRenderer r, World world, Camera camera, float alpha)
    {
        world.ForEach<Transform, FracturableBody, BodyColor>(
            (Entity e, ref Transform t, ref FracturableBody fb, ref BodyColor col) =>
        {
            var (pos, rot) = Interp(t, alpha);
            if (!Visible(camera, pos, 300f)) return;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);

            bool[]? pulv = world.HasComponent<FractureProcess>(e)
                ? world.GetComponent<FractureProcess>(e).Pulverized : null;

            if (_brokenScratch.Length < fb.Bonds.Length) _brokenScratch = new bool[fb.Bonds.Length];
            bool anyBroken = false;
            for (int i = 0; i < fb.Bonds.Length; i++)
            {
                bool bk = fb.Bonds[i].Broken;
                _brokenScratch[i] = bk; anyBroken |= bk;
            }

            if (anyBroken || pulv != null)
            {
                if (_pulvFalse.Length < fb.Cells.Length) _pulvFalse = new bool[fb.Cells.Length];
                var (ol, cr) = FractureMesh.ComputeEdgesLive(fb.Cells, fb.Bonds, _brokenScratch, pulv ?? _pulvFalse);
                DrawEdgeSegs(r, ol, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, cr, pos, c, s, CrackColor(col.Fill), 1.5f);
            }
            else if (world.HasComponent<RenderOutline>(e))
            {
                ref var ro = ref world.GetComponent<RenderOutline>(e);
                DrawEdgeSegs(r, ro.Outline, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, ro.Cracks,  pos, c, s, CrackColor(col.Fill), 1.5f);
            }
        });
    }

    private void DrawDebris(IRenderer r, World world, Camera camera, float alpha)
    {
        world.ForEach<Transform, DebrisPiece, TimeToLive>(
            (Entity _, ref Transform t, ref DebrisPiece dp, ref TimeToLive ttl) =>
        {
            var (pos, rot) = Interp(t, alpha);
            if (!Visible(camera, pos, 100f)) return;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);
            float k = dp.MaxTtl > 0f ? Math.Clamp(ttl.Remaining / dp.MaxTtl, 0f, 1f) : 1f;
            _dbuf.Clear();
            foreach (var lv in dp.Local)
                _dbuf.Add(new Vector2(lv.X * c - lv.Y * s + pos.X, lv.X * s + lv.Y * c + pos.Y));
            r.FillPolygon(CollectionsMarshal.AsSpan(_dbuf), dp.Color.WithAlpha((byte)(dp.Color.A * k)));
        });
    }

    private static readonly Color TracerGlow = new(255, 170, 60, 80);
    private static readonly Color TracerCore = new(255, 240, 165, 220);

    private void DrawBullets(IRenderer r, World world, Camera camera, VfxConfig vfx, float alpha)
    {
        float tracerLen = vfx.TracerLength, tracerW = vfx.TracerWidth;
        var lines   = r as ILineBatch;      // glow/core are constant-colour → one batched call each
        var sprites = r as IParticleBatch;  // heads → the same one-call sprite batch as particles
        _lineGlow.Clear(); _lineCore.Clear(); _headBuf.Clear();

        world.ForEach<Transform, BulletTag, BulletVisual>(
            (Entity _, ref Transform t, ref BulletTag _, ref BulletVisual bv) =>
        {
            var (p, _) = Interp(t, alpha);
            if (!Visible(camera, p, 10f)) return;
            Vector2 d = t.Position - t.PreviousPosition;
            Vector2 dir = d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : new Vector2(0f, -1f);
            if (tracerLen > 0f)
            {
                Vector2 tail = p - dir * tracerLen;
                if (lines != null)
                {
                    _lineGlow.Add(tail); _lineGlow.Add(p);
                    _lineCore.Add(tail); _lineCore.Add(p);
                }
                else
                {
                    r.DrawLine(tail, p, TracerGlow, tracerW * 2.5f);   // glow
                    r.DrawLine(tail, p, TracerCore, tracerW);          // hot core
                }
            }
            float hr = MathF.Max(2f, tracerW * 1.3f);
            if (sprites != null) _headBuf.Add(new SpriteInstance(p, hr, bv.Color));
            else                 r.FillCircle(p, hr, bv.Color);
        });

        if (lines != null)
        {
            if (_lineGlow.Count >= 2) lines.DrawLines(CollectionsMarshal.AsSpan(_lineGlow), TracerGlow, tracerW * 2.5f);
            if (_lineCore.Count >= 2) lines.DrawLines(CollectionsMarshal.AsSpan(_lineCore), TracerCore, tracerW);
        }
        if (sprites != null && _headBuf.Count > 0)
            sprites.DrawSprites(CollectionsMarshal.AsSpan(_headBuf));
    }

    private static void DrawShockwaves(IRenderer r, World world, float alpha)
    {
        world.ForEach<Transform, ShockwaveRing, TimeToLive>(
            (Entity _, ref Transform t, ref ShockwaveRing sw, ref TimeToLive ttl) =>
        {
            var (pos, _) = Interp(t, alpha);
            float k = Math.Clamp((sw.MaxAge - ttl.Remaining) / MathF.Max(0.01f, sw.MaxAge), 0f, 1f);
            byte a = (byte)(210 * (1f - k));
            r.DrawCircle(pos, sw.MaxRadius * k, new Color(190, 130, 255, a), 3f + 5f * (1f - k));
        });
    }

    private static void DrawBlackHoles(IRenderer r, World world, float alpha)
    {
        world.ForEach<Transform, BlackHoleTag>((Entity _, ref Transform t, ref BlackHoleTag bh) =>
        {
            var (pos, _) = Interp(t, alpha);
            r.FillCircle(pos, bh.CrushRadius * 3f,   new Color(20, 0, 40, 100));
            r.FillCircle(pos, bh.CrushRadius * 1.5f, new Color(10, 0, 25, 200));
            r.FillCircle(pos, bh.CrushRadius,        new Color(3, 0, 8, 255));
        });
    }

    private static void DrawPlayerAim(IRenderer r, World world, Entity player, float alpha)
    {
        if (!world.IsAlive(player)) return;
        if (!world.HasComponent<Transform>(player) || !world.HasComponent<AimComponent>(player)) return;
        var (p, _) = Interp(world.GetComponent<Transform>(player), alpha);
        var aim    = world.GetComponent<AimComponent>(player).Dir;
        // Circle fallback for a non-fracturable player (otherwise the ship mesh draws above).
        if (!world.HasComponent<FracturableBody>(player))
        {
            r.FillCircle(p, 15f, new Color(70, 130, 240));
            r.DrawCircle(p, 15f, new Color(170, 205, 255), 2f);
        }
        r.DrawLine(p, p + aim * 32f, new Color(170, 205, 255, 160), 2f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DrawEdgeSegs(IRenderer r, Vector2[] segs, Vector2 pos,
                                     float cos, float sin, Color color, float w)
    {
        for (int k = 0; k + 1 < segs.Length; k += 2)
        {
            Vector2 a = segs[k], b = segs[k + 1];
            r.DrawLine(
                new Vector2(a.X * cos - a.Y * sin + pos.X, a.X * sin + a.Y * cos + pos.Y),
                new Vector2(b.X * cos - b.Y * sin + pos.X, b.X * sin + b.Y * cos + pos.Y),
                color, w);
        }
    }

    private static Color CrackColor(Color fill) =>
        new((byte)(fill.R * 0.35f), (byte)(fill.G * 0.35f), (byte)(fill.B * 0.35f));

    private static bool Visible(Camera cam, Vector2 worldPos, float radius)
    {
        Vector2 s = cam.WorldToScreen(worldPos);
        return s.X > -radius && s.X < cam.ScreenWidth + radius
            && s.Y > -radius && s.Y < cam.ScreenHeight + radius;
    }

    private const float TeleSq = 200f * 200f;
    private static (Vector2 pos, float rot) Interp(in Transform t, float alpha)
    {
        Vector2 d = t.Position - t.PreviousPosition;
        if (d.LengthSquared() > TeleSq) return (t.Position, t.Rotation);
        float dr = t.Rotation - t.PreviousRotation;
        while (dr > MathF.PI) dr -= MathF.Tau;
        while (dr < -MathF.PI) dr += MathF.Tau;
        return (t.PreviousPosition + d * alpha, t.PreviousRotation + dr * alpha);
    }
}
