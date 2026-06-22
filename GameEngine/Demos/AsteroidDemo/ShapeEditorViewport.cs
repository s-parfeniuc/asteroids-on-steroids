using System.Numerics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Config;

namespace AsteroidDemo;

sealed class ShapeEditorViewport
{
    private static readonly string[] AllRoles =
        ["generic", "cockpit", "cannon", "shotgun", "piercing", "grenade", "propeller", "bumper", "spawner"];

    public static Color RoleColor(string role) => role switch
    {
        "cockpit"   => new Color(255, 215,   0),
        "cannon"    => new Color(230,  55,  55),
        "shotgun"   => new Color(220, 110,  50),
        "piercing"  => new Color(200,  45,  80),
        "grenade"   => new Color(230,  80,  30),
        "propeller" => new Color( 50, 220, 220),
        "bumper"    => new Color( 70, 110, 230),
        "spawner"   => new Color(180,  70, 230),
        _           => new Color(200, 200, 200), // generic
    };

    private readonly Dictionary<string, ShapeData> _shapes;
    private readonly string _assetsDir;

    private int     _selectedSeed = -1;
    private bool    _dragging;
    private Vector2 _dragOffset;
    private bool    _prevMouseLeft;
    private bool    _prevMouseRight;
    private bool    _dirty;

    private const float SeedRadius   = 7f;
    private const float SelectRadius = 14f;
    private static readonly FontSpec LabelFont = new("monospace", 11f);
    private static readonly FontSpec HelpFont  = new("monospace", 12f);
    private static readonly Color    BgColor   = new( 8,   9,  14);
    private static readonly Color    GridColor = new(25,  28,  38);
    private static readonly Color    AxisColor = new(45,  55,  75);
    private static readonly Color    FillColor = new(28,  32,  48);
    private static readonly Color    EdgeColor = new(75,  85, 115);

    public bool IsDirty => _dirty;

    public ShapeEditorViewport(Dictionary<string, ShapeData> shapes, string assetsDir)
    {
        _shapes    = shapes;
        _assetsDir = assetsDir;
    }

    // Returns true if the Tab key was consumed (caller should NOT toggle the panel).
    public bool Update(InputSystem input, string shapeName,
                       float viewLeft, float viewRight, float tabH, int screenH)
    {
        bool tabConsumed = false;
        if (!_shapes.TryGetValue(shapeName, out var shape)) { UpdatePrev(input); return false; }

        var (origin, scale) = ComputeLayout(shape, viewLeft, viewRight, tabH, screenH);
        Vector2 mouse = input.MouseScreen;
        bool curLeft  = input.IsMouseLeftRaw;
        bool curRight = input.IsMouseRight;
        bool justPressedLeft  = curLeft  && !_prevMouseLeft;
        bool justReleasedLeft = !curLeft && _prevMouseLeft;
        bool justPressedRight = curRight && !_prevMouseRight;

        bool inViewport = mouse.X >= viewLeft && mouse.X <= viewRight
                       && mouse.Y >= tabH     && mouse.Y <= screenH;

        if (inViewport)
        {
            Vector2 shapePos = (mouse - origin) / scale;

            if (justPressedLeft)
            {
                int nearest = NearestSeed(shape, shapePos, SelectRadius / scale);
                if (nearest >= 0)
                {
                    _selectedSeed = nearest;
                    _dragging     = true;
                    _dragOffset   = shapePos - new Vector2(shape.Seeds[nearest].X, shape.Seeds[nearest].Y);
                }
                else
                {
                    // Add new seed at click position
                    shape.Seeds   = [.. shape.Seeds, new SeedData { X = shapePos.X, Y = shapePos.Y }];
                    _selectedSeed = shape.Seeds.Length - 1;
                    _dragging     = true;
                    _dragOffset   = Vector2.Zero;
                    _dirty        = true;
                }
            }

            if (justPressedRight)
            {
                int nearest  = NearestSeed(shape, (mouse - origin) / scale, SelectRadius / scale);
                int toDelete = nearest >= 0 ? nearest : _selectedSeed;
                if (toDelete >= 0 && toDelete < shape.Seeds.Length)
                    DeleteSeed(shape, toDelete);
            }
        }

        // Drag continues even when mouse leaves the viewport
        if (_dragging && curLeft && _selectedSeed >= 0 && _selectedSeed < shape.Seeds.Length)
        {
            var (o2, sc2) = ComputeLayout(shape, viewLeft, viewRight, tabH, screenH);
            Vector2 sp = (mouse - o2) / sc2 - _dragOffset;
            shape.Seeds[_selectedSeed].X = sp.X;
            shape.Seeds[_selectedSeed].Y = sp.Y;
            _dirty = true;
        }

        if (justReleasedLeft) _dragging = false;

        // Tab: cycle role of selected seed
        if (input.IsPressed(KeyCode.Tab) && _selectedSeed >= 0 && _selectedSeed < shape.Seeds.Length)
        {
            CycleRole(shape.Seeds[_selectedSeed]);
            _dirty      = true;
            tabConsumed = true;
        }

        // Backspace: delete selected seed
        if (input.IsPressed(KeyCode.Back) && _selectedSeed >= 0 && _selectedSeed < shape.Seeds.Length)
            DeleteSeed(shape, _selectedSeed);

        // Ctrl+S: save
        if (input.IsHeld(KeyCode.Control) && input.IsPressed(KeyCode.S))
            SaveShape(shapeName);

        // Ctrl+Z: revert from disk
        if (input.IsHeld(KeyCode.Control) && input.IsPressed(KeyCode.Z))
            RevertShape(shapeName);

        UpdatePrev(input);
        return tabConsumed;
    }

    public void SaveShape(string shapeName)
    {
        if (!_shapes.TryGetValue(shapeName, out var shape)) return;
        GameConfigLoader.SaveShape(shape, _assetsDir, shapeName);
        _dirty = false;
    }

    public void RevertShape(string shapeName)
    {
        var fresh = GameConfigLoader.LoadShapes(Path.Combine(_assetsDir, "shapes"));
        if (fresh.TryGetValue(shapeName, out var fd))
            _shapes[shapeName] = fd;
        _selectedSeed = -1;
        _dragging     = false;
        _dirty        = false;
    }

    public void Draw(IRenderer r, string shapeName,
                     float viewLeft, float viewRight, float tabH, int screenH)
    {
        if (!_shapes.TryGetValue(shapeName, out var shape))
        {
            DrawBackground(r, viewLeft, viewRight, tabH, screenH);
            r.DrawText("No shape selected.", new Vector2(viewLeft + 10, tabH + 10), new Color(100, 110, 130), HelpFont);
            return;
        }

        var (origin, scale) = ComputeLayout(shape, viewLeft, viewRight, tabH, screenH);

        DrawBackground(r, viewLeft, viewRight, tabH, screenH);
        DrawGrid(r, origin, scale, viewLeft, viewRight, tabH, screenH);

        // Outline polygon
        if (shape.Outline.Length >= 3)
        {
            var poly = new Vector2[shape.Outline.Length];
            for (int i = 0; i < shape.Outline.Length; i++)
                poly[i] = origin + new Vector2(shape.Outline[i][0], shape.Outline[i][1]) * scale;
            r.FillPolygon(poly, FillColor);
            r.DrawPolygon(poly, EdgeColor, 1.5f);
        }

        // Seeds
        for (int i = 0; i < shape.Seeds.Length; i++)
        {
            var     seed = shape.Seeds[i];
            Vector2 sp   = origin + new Vector2(seed.X, seed.Y) * scale;
            Color   rc   = RoleColor(seed.Role);
            bool    sel  = i == _selectedSeed;
            float   rad  = sel ? SeedRadius + 2f : SeedRadius;

            r.FillCircle(sp, rad, rc);
            if (sel) r.DrawCircle(sp, rad + 3.5f, Color.White, 2f);
            r.DrawText(seed.Role, new Vector2(sp.X + rad + 4f, sp.Y - 8f), rc.WithAlpha(210), LabelFont);
        }

        // Status
        float infoY = tabH + 8f;
        if (_dirty)
        {
            r.DrawText("* unsaved changes", new Vector2(viewLeft + 10, infoY), new Color(255, 200, 80), HelpFont);
            infoY += 18f;
        }
        r.DrawText($"{shapeName}  |  {shape.Seeds.Length} seeds  |  {shape.Outline.Length} outline verts",
                   new Vector2(viewLeft + 10, infoY), new Color(90, 105, 135), HelpFont);

        // Help text
        string selLine = _selectedSeed >= 0 && _selectedSeed < shape.Seeds.Length
            ? $"[{_selectedSeed}] {shape.Seeds[_selectedSeed].Role}  |  Tab: cycle role  |  Backspace: delete"
            : "Left-click: select / add seed  |  Right-click: delete";
        r.DrawText(selLine,
                   new Vector2(viewLeft + 10, screenH - 40f), new Color(120, 130, 155), HelpFont);
        r.DrawText("Drag: move seed  |  Ctrl+S: save  |  Ctrl+Z: revert",
                   new Vector2(viewLeft + 10, screenH - 22f), new Color(90, 100, 120), HelpFont);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private void DeleteSeed(ShapeData shape, int index)
    {
        var list = shape.Seeds.ToList();
        list.RemoveAt(index);
        shape.Seeds   = list.ToArray();
        _selectedSeed = shape.Seeds.Length == 0 ? -1 : Math.Min(_selectedSeed, shape.Seeds.Length - 1);
        _dragging     = false;
        _dirty        = true;
    }

    private void UpdatePrev(InputSystem input)
    {
        _prevMouseLeft  = input.IsMouseLeftRaw;
        _prevMouseRight = input.IsMouseRight;
    }

    private static void CycleRole(SeedData seed)
    {
        int idx = Array.IndexOf(AllRoles, seed.Role);
        seed.Role = AllRoles[(idx < 0 ? 0 : (idx + 1)) % AllRoles.Length];
    }

    private static int NearestSeed(ShapeData shape, Vector2 shapePos, float threshold)
    {
        int nearest = -1; float minDist = threshold;
        for (int i = 0; i < shape.Seeds.Length; i++)
        {
            float d = Vector2.Distance(new Vector2(shape.Seeds[i].X, shape.Seeds[i].Y), shapePos);
            if (d < minDist) { minDist = d; nearest = i; }
        }
        return nearest;
    }

    private static (Vector2 origin, float scale) ComputeLayout(
        ShapeData shape, float viewLeft, float viewRight, float tabH, int screenH)
    {
        float cx = (viewLeft + viewRight) * 0.5f;
        float cy = (tabH + screenH) * 0.5f;

        if (shape.Seeds.Length == 0 && shape.Outline.Length == 0)
            return (new Vector2(cx, cy), 4f);

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var s in shape.Seeds)
        {
            minX = MathF.Min(minX, s.X); maxX = MathF.Max(maxX, s.X);
            minY = MathF.Min(minY, s.Y); maxY = MathF.Max(maxY, s.Y);
        }
        foreach (var ov in shape.Outline)
        {
            if (ov.Length < 2) continue;
            minX = MathF.Min(minX, ov[0]); maxX = MathF.Max(maxX, ov[0]);
            minY = MathF.Min(minY, ov[1]); maxY = MathF.Max(maxY, ov[1]);
        }

        float sw = MathF.Max(maxX - minX, 1f);
        float sh = MathF.Max(maxY - minY, 1f);
        float availW = (viewRight - viewLeft) * 0.72f;
        float availH = (screenH - tabH) * 0.72f;
        float scale  = MathF.Max(MathF.Min(availW / sw, availH / sh), 0.5f);

        float shapeCX = (minX + maxX) * 0.5f;
        float shapeCY = (minY + maxY) * 0.5f;
        return (new Vector2(cx - shapeCX * scale, cy - shapeCY * scale), scale);
    }

    private static void DrawBackground(IRenderer r, float viewLeft, float viewRight, float tabH, int screenH)
    {
        Span<Vector2> bg = stackalloc Vector2[4]
        {
            new(viewLeft,  tabH),    new(viewRight, tabH),
            new(viewRight, screenH), new(viewLeft,  screenH)
        };
        r.FillPolygon(bg, BgColor);
    }

    private static void DrawGrid(IRenderer r, Vector2 origin, float scale,
                                  float viewLeft, float viewRight, float tabH, int screenH)
    {
        float step = 20f * scale;
        if (step < 6f) return;

        // Start at origin and find first grid line >= viewLeft/tabH
        float gx = origin.X + MathF.Ceiling((viewLeft - origin.X) / step) * step;
        for (; gx <= viewRight; gx += step)
            r.DrawLine(new(gx, tabH), new(gx, screenH), GridColor, 1f);

        float gy = origin.Y + MathF.Ceiling((tabH - origin.Y) / step) * step;
        for (; gy <= screenH; gy += step)
            r.DrawLine(new(viewLeft, gy), new(viewRight, gy), GridColor, 1f);

        // Axis lines through shape origin
        r.DrawLine(new(origin.X, tabH),       new(origin.X, screenH),  AxisColor, 1f);
        r.DrawLine(new(viewLeft, origin.Y),    new(viewRight, origin.Y), AxisColor, 1f);
    }
}
