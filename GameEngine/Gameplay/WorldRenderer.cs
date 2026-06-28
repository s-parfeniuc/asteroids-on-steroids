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
    private static readonly Color GridCol = new(22, 24, 35);
    private const float GridSpacing = 200f;

    private readonly List<Vector2> _meshVerts = new();
    private readonly List<int>     _meshLens  = new();
    private readonly List<Vector2> _dbuf      = new();

    public void Draw(IRenderer r, World world, Camera camera, ParticleSystem fx,
                     Entity player, VfxConfig vfx, float alpha)
    {
        r.PushTransform(camera.GetViewMatrix());
        DrawGrid(r, camera);
        DrawBodies(r, world, camera, alpha);
        DrawDebris(r, world, camera, alpha);
        fx.Draw(r);
        DrawBullets(r, world, camera, vfx, alpha);
        DrawBlackHoles(r, world, alpha);
        DrawPlayerAim(r, world, player, alpha);
        r.PopTransform();
    }

    private static void DrawGrid(IRenderer r, Camera camera)
    {
        Vector2 tl = camera.ScreenToWorld(Vector2.Zero);
        Vector2 br = camera.ScreenToWorld(new Vector2(camera.ScreenWidth, camera.ScreenHeight));
        float gx0 = MathF.Floor(tl.X / GridSpacing) * GridSpacing;
        float gy0 = MathF.Floor(tl.Y / GridSpacing) * GridSpacing;
        for (float gx = gx0; gx <= br.X; gx += GridSpacing)
            r.DrawLine(new Vector2(gx, tl.Y), new Vector2(gx, br.Y), GridCol, 1f);
        for (float gy = gy0; gy <= br.Y; gy += GridSpacing)
            r.DrawLine(new Vector2(tl.X, gy), new Vector2(br.X, gy), GridCol, 1f);
    }

    private void DrawBodies(IRenderer r, World world, Camera camera, float alpha)
    {
        world.ForEach<Transform, FracturableBody, BodyColor>(
            (Entity e, ref Transform t, ref FracturableBody fb, ref BodyColor col) =>
        {
            var (pos, rot) = Interp(t, alpha);
            if (!Visible(camera, pos, 300f)) return;
            float c = MathF.Cos(rot), s = MathF.Sin(rot);

            bool[]? broken = null, pulv = null;
            if (world.HasComponent<FractureProcess>(e))
            {
                ref var fp = ref world.GetComponent<FractureProcess>(e);
                broken = fp.Broken; pulv = fp.Pulverized;
            }

            _meshVerts.Clear(); _meshLens.Clear();
            for (int ci = 0; ci < fb.Cells.Length; ci++)
            {
                if (pulv?[ci] == true) continue;
                var lv = fb.Cells[ci].Local;
                foreach (var v in lv)
                    _meshVerts.Add(new Vector2(v.X * c - v.Y * s + pos.X, v.X * s + v.Y * c + pos.Y));
                _meshLens.Add(lv.Length);
            }
            r.FillPath(CollectionsMarshal.AsSpan(_meshVerts),
                       CollectionsMarshal.AsSpan(_meshLens), col.Fill);

            if (broken != null && pulv != null)
            {
                var (ol, cr) = FractureMesh.ComputeEdgesLive(fb.Cells, fb.Bonds, broken, pulv);
                DrawEdgeSegs(r, ol, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, cr, pos, c, s, CrackColor(col.Fill), 1f);
            }
            else if (world.HasComponent<RenderOutline>(e))
            {
                ref var ro = ref world.GetComponent<RenderOutline>(e);
                DrawEdgeSegs(r, ro.Outline, pos, c, s, col.Outline, 1.5f);
                DrawEdgeSegs(r, ro.Cracks,  pos, c, s, CrackColor(col.Fill), 1f);
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

    private static void DrawBullets(IRenderer r, World world, Camera camera, VfxConfig vfx, float alpha)
    {
        float tracerLen = vfx.TracerLength, tracerW = vfx.TracerWidth;
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
                r.DrawLine(tail, p, new Color(255, 170, 60, 80), tracerW * 2.5f);   // glow
                r.DrawLine(tail, p, new Color(255, 240, 165, 220), tracerW);        // hot core
            }
            r.FillCircle(p, MathF.Max(2f, tracerW * 1.3f), bv.Color);
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
