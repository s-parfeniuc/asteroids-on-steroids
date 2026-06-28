using System.Numerics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsEngine.Engine.Ui;
using AsteroidsGame;
using AsteroidsGame.Config;

namespace AsteroidDemo;

/// <summary>
/// Immediate-mode game editor overlay.
/// Call Draw() AFTER DemoRenderer.Draw() each frame so panels appear on top.
/// </summary>
sealed class Editor
{
    private readonly Ui         _ui = new();
    private readonly GameConfig _config;
    private readonly Dictionary<string, ShapeData> _shapes;
    private readonly string     _assetsDir;

    private static readonly string[] TabNames =
        ["Simulate", "Materials", "Weapons", "Asteroids", "Player", "Shapes", "Waves", "Aliens", "Fracture"];

    private const float TabH   = 32f;
    private const float LeftW  = 210f;
    private const float RightW = 375f;

    // Tab + selection state
    private int    _tab;
    private string _selMat   = "";
    private string _selWpn   = "";
    private string _selAst   = "";
    private string _selShape = "";
    private int    _selWave  = 0;

    // Rename state
    private string? _editingName; // key being renamed in the left panel
    private bool    _renameIsNew; // true if rename was triggered by New (autoFocus)

    // Player sub-selection ("" = base player params, else skill key)
    private string  _selSkill = "";

    // Param section open/close state
    private bool _secShotgun = true, _secGrenade = true, _secPiercing = true;

    private bool _dirty;

    // Wave tab state (not persisted — session controls + preview)
    private DemoSession? _session;
    private float _previewTime = 0f;

    public int    SelectedTab   => _tab;
    public string SelectedShape => _selShape;
    public bool   IsTextInputActive => _ui.IsTextInputActive;

    public void SetSession(DemoSession session) => _session = session;

    public Editor(GameConfig config, Dictionary<string, ShapeData> shapes, string assetsDir)
    {
        _config    = config;
        _shapes    = shapes;
        _assetsDir = assetsDir;
        _selMat    = config.Materials.Keys.FirstOrDefault() ?? "";
        _selWpn    = config.Weapons.Keys.FirstOrDefault()   ?? "";
        _selAst    = config.Asteroids.Keys.FirstOrDefault() ?? "";
        _selShape  = shapes.Keys.FirstOrDefault()           ?? "";
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the mouse is over a UI panel so gameplay input
    /// (shooting) should be suppressed.  Purely geometric — no draw needed.
    /// </summary>
    public bool IsMouseOverPanel(Vector2 mouse, int screenW)
    {
        if (mouse.Y < TabH) return true;           // tab bar always present
        if (_tab == 0) return false;               // Simulate: no side panels
        if (mouse.X < LeftW) return true;          // left asset list
        if (mouse.X > screenW - RightW) return true; // right param editor
        return false;
    }

    // ── Main draw entry ───────────────────────────────────────────────────────

    public void Draw(IRenderer r, InputSystem input, int screenW, int screenH)
    {
        _ui.BeginFrame(r, input);

        _ui.FillRect(0, 0, screenW, TabH, Ui.ColBg);
        _ui.Tabs(0, 0, screenW, TabH, TabNames, ref _tab);

        if (_tab != 0)
        {
            float panelH = screenH - TabH;

            _ui.BeginPanel(0, TabH, LeftW, panelH);
            DrawAssetList();
            _ui.EndPanel();

            _ui.BeginPanel(screenW - RightW, TabH, RightW, panelH);
            DrawParamEditor();
            _ui.EndPanel();
        }

        _ui.EndFrame();
    }

    // ── Left panel: asset list ────────────────────────────────────────────────

    private void DrawAssetList()
    {
        bool hasNewDelete = _tab is 1 or 2 or 3;

        if (hasNewDelete)
        {
            _ui.BeginRow(2);
            if (_ui.Button("+ New"))               OnNew();
            if (_ui.Button("Delete", Ui.ColDanger)) OnDelete();
            _ui.EndRow();
            _ui.Separator();
        }

        switch (_tab)
        {
            case 1: DrawStringKeyList(_config.Materials.Keys, ref _selMat); break;
            case 2: DrawStringKeyList(_config.Weapons.Keys,   ref _selWpn); break;
            case 3: DrawStringKeyList(_config.Asteroids.Keys, ref _selAst); break;
            case 4: DrawPlayerList(); break;
            case 5: DrawStringKeyList(_shapes.Keys,           ref _selShape); break;
            case 6: DrawWaveList(); break;
            case 8: _ui.Label("Global fracture model tuning.", Ui.ColTextDim); break;
            case 7: _ui.Label("Coming soon", Ui.ColTextDim); break;
        }
    }

    private void DrawStringKeyList(IEnumerable<string> keys, ref string selected)
    {
        foreach (var key in keys.ToList()) // ToList: safe during potential deletion
        {
            if (_editingName == key)
            {
                string buf = key;
                if (_ui.TextInput("rename_" + key, ref buf, autoFocus: _renameIsNew))
                {
                    CommitRename(buf, key);
                }
                else if (!_ui.IsTextInputActive)
                {
                    // Escape was pressed — cancel rename, restore selection
                    _editingName = null;
                    _renameIsNew = false;
                    selected = key; // keep the item selected after cancel
                }
            }
            else
            {
                if (_ui.Selectable(key, selected == key))
                {
                    selected = key;
                    _editingName = null; // clicking another item cancels any pending rename
                }
            }
        }
    }

    private void DrawPlayerList()
    {
        if (_ui.Selectable("Player", _selSkill == "")) _selSkill = "";
        _ui.Separator();
        _ui.Label("Skills", Ui.ColTextDim, Ui.FontSmall);
        foreach (var key in _config.Skills.Keys)
            if (_ui.Selectable(key, _selSkill == key)) _selSkill = key;
    }

    private void DrawWaveList()
    {
        if (_session == null) { _ui.Label("Session not connected.", Ui.ColTextDim); return; }

        _ui.Label("GAME TIME", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label($"{_session.GameTime:0.0}s", Ui.ColAccent, Ui.FontBold);
        _ui.Space(2f);

        _ui.BeginRow(2);
        if (_ui.Button("-60s")) _session.GameTime -= 60f;
        if (_ui.Button("-10s")) _session.GameTime -= 10f;
        _ui.EndRow();
        _ui.BeginRow(2);
        if (_ui.Button("+10s")) _session.GameTime += 10f;
        if (_ui.Button("+60s")) _session.GameTime += 60f;
        _ui.EndRow();
        if (_ui.Button("+5min")) _session.GameTime += 300f;

        _ui.Separator();
        _ui.Label("SESSION", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label($"Wave: {_session.WaveNumber}    Timer: {_session.WaveTimer:0.0}s", Ui.ColText);
        _ui.Label($"Cells: {_session.LiveCells} / {_session.MaxLiveCells}", Ui.ColText);
        if (_session.WavePending)
            _ui.Label("Spawning…", new Color(255, 200, 80));

        _ui.Separator();
        if (_ui.Button("Force Next Wave")) _session.ForceNextWave();

        _ui.Space(4f);
        _ui.Label("[  ←60s    ]  +60s", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("N  — force next wave", Ui.ColTextDim, Ui.FontSmall);
    }

    // ── Right panel: param editor ─────────────────────────────────────────────

    private void DrawParamEditor()
    {
        bool changed = false;

        switch (_tab)
        {
            case 1: changed = DrawMaterialEditor(); break;
            case 2: changed = DrawWeaponEditor();   break;
            case 3: changed = DrawAsteroidEditor(); break;
            case 4: changed = _selSkill == "" ? DrawPlayerEditor() : DrawSkillEditor(); break;
            case 5: DrawShapeInfo(); break;
            case 6: changed = DrawWaveEditor();     break;
            case 7: _ui.Label("Alien configuration coming in a future update.", Ui.ColTextDim); break;
            case 8: changed = DrawFractureEditor(); break;
            default: _ui.Label("Select an asset from the left panel.", Ui.ColTextDim); break;
        }

        if (changed) _dirty = true;

        if (_tab is (>= 1 and <= 6 and not 5) or 8)
        {
            _ui.Space(8f);
            _ui.Separator();
            _ui.BeginRow(2);
            if (_ui.Button("Save to JSON", _dirty ? Ui.ColAccent : null)) OnSave();
            if (_ui.Button("Revert")) OnRevert();
            _ui.EndRow();
        }
    }

    // Materials ──────────────────────────────────────────────────────────────

    private bool DrawMaterialEditor()
    {
        if (!_config.Materials.TryGetValue(_selMat, out var mat))
        { _ui.Label("No material selected.", Ui.ColTextDim); return false; }

        DrawAssetHeader(_selMat, 1);
        return ConfigEditorDefs.Material.Draw(mat, _ui);
    }

    // Fracture (global) ────────────────────────────────────────────────────────

    private bool DrawFractureEditor()
    {
        _ui.Label("Fracture model (global)", Ui.ColText, Ui.FontBold);
        bool changed = ConfigEditorDefs.Fracture.Draw(_config.Fracture, _ui);
        if (changed) GameContext.ApplyFractureTuning(_config.Fracture);   // push to engine statics live
        return changed;
    }

    // Weapons ────────────────────────────────────────────────────────────────

    private bool DrawWeaponEditor()
    {
        if (!_config.Weapons.TryGetValue(_selWpn, out var w))
        { _ui.Label("No weapon selected.", Ui.ColTextDim); return false; }

        DrawAssetHeader(_selWpn, 2);
        bool changed = ConfigEditorDefs.Weapon.Draw(w, _ui);

        // ── Shotgun section ──────────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Shotgun", ref _secShotgun);
        if (_secShotgun)
        {
            bool has = w.Rays.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { w.Rays = null; w.ConeAngle = null; has = false; }
                else     { w.Rays = 5; w.ConeAngle = 30f; }
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                int rays = w.Rays!.Value;
                if (_ui.SliderInt("Rays", ref rays, 2, 20)) { w.Rays = rays; changed = true; }
                float cone = w.ConeAngle ?? 30f;
                if (_ui.Slider("Cone Angle°", ref cone, 5f, 180f, "0")) { w.ConeAngle = cone; changed = true; }
            }
        }

        // ── Grenade section ──────────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Grenade / AOE", ref _secGrenade);
        if (_secGrenade)
        {
            bool has = w.FuseTime.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { w.FuseTime = null; w.ShrapnelCount = null; w.ShrapnelSpread = null; has = false; }
                else     { w.FuseTime = 2f; w.ShrapnelCount = 12; w.ShrapnelSpread = 360f; }
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                float fuse = w.FuseTime!.Value;
                if (_ui.Slider("Fuse Time", ref fuse, 0.1f, 8f)) { w.FuseTime = fuse; changed = true; }
                int shrapnel = w.ShrapnelCount ?? 12;
                if (_ui.SliderInt("Shrapnel", ref shrapnel, 1, 40)) { w.ShrapnelCount = shrapnel; changed = true; }
                float spread = w.ShrapnelSpread ?? 360f;
                if (_ui.Slider("Spread°", ref spread, 10f, 360f, "0")) { w.ShrapnelSpread = spread; changed = true; }
            }
        }

        // ── Piercing round section ────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Piercing Round", ref _secPiercing);
        if (_secPiercing)
        {
            bool has = w.Mass.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { w.Mass = null; w.LateralImpulseClamp = null; has = false; }
                else     { w.Mass = 5f; w.LateralImpulseClamp = 200f; }
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                float mass = w.Mass!.Value;
                if (_ui.Slider("Mass", ref mass, 0.5f, 50f)) { w.Mass = mass; changed = true; }
                float lat = w.LateralImpulseClamp ?? 200f;
                if (_ui.Slider("Lateral Clamp", ref lat, 10f, 1000f, "0")) { w.LateralImpulseClamp = lat; changed = true; }
            }
        }

        return changed;
    }

    // Asteroids ──────────────────────────────────────────────────────────────

    private bool DrawAsteroidEditor()
    {
        if (!_config.Asteroids.TryGetValue(_selAst, out var ast))
        { _ui.Label("No asteroid selected.", Ui.ColTextDim); return false; }

        DrawAssetHeader(_selAst, 3);

        bool changed = false;

        _ui.Label("Material", Ui.ColText, Ui.FontBold);
        int mCols = Math.Min(_config.Materials.Count, 4);
        if (mCols > 0)
        {
            _ui.BeginRow(mCols);
            foreach (var mk in _config.Materials.Keys)
            {
                if (_ui.Button(mk, ast.Material == mk ? Ui.ColAccent : null))
                { ast.Material = mk; changed = true; }
            }
            _ui.EndRow();
        }
        _ui.Space(4f);

        if (ast.Procedural is not null)
        {
            _ui.Label("Procedural shape", Ui.ColTextDim);
            _ui.Separator();
            changed = ConfigEditorDefs.ProceduralShape.Draw(ast.Procedural, _ui);
        }
        else if (ast.Shape is not null)
        {
            _ui.Label("Shape: " + ast.Shape, Ui.ColTextDim);
            _ui.Separator();
        }

        // Size and spin ranges
        _ui.Space(4f);
        _ui.Separator();
        if (ast.SizeRange.Length >= 2)
        {
            float sMin = ast.SizeRange[0], sMax = ast.SizeRange[1];
            if (_ui.Slider("Size Min",  ref sMin, 0.1f, 4f)) { ast.SizeRange[0] = sMin; changed = true; }
            if (_ui.Slider("Size Max",  ref sMax, 0.1f, 4f)) { ast.SizeRange[1] = sMax; changed = true; }
        }
        if (ast.SpinRange.Length >= 2)
        {
            float spMin = ast.SpinRange[0], spMax = ast.SpinRange[1];
            if (_ui.Slider("Spin Min",  ref spMin, 0f, 10f)) { ast.SpinRange[0] = spMin; changed = true; }
            if (_ui.Slider("Spin Max",  ref spMax, 0f, 10f)) { ast.SpinRange[1] = spMax; changed = true; }
        }
        float dm = ast.DensityMult;
        if (_ui.Slider("Density Mult", ref dm, 0.1f, 5f)) { ast.DensityMult = dm; changed = true; }

        return changed;
    }

    // Player ─────────────────────────────────────────────────────────────────

    private bool DrawPlayerEditor()
    {
        _ui.Label("Player", Ui.ColAccent, Ui.FontBold);
        _ui.Label("Shape: " + _config.Player.Shape, Ui.ColTextDim);
        _ui.Label("Material: " + _config.Player.Material, Ui.ColTextDim);
        _ui.Separator();
        bool changed = ConfigEditorDefs.Player.Draw(_config.Player, _ui);

        _ui.Space(4f);
        _ui.Label("Starting Weapon", Ui.ColText, Ui.FontBold);
        int wCols = Math.Min(_config.Weapons.Count, 4);
        if (wCols > 0)
        {
            _ui.BeginRow(wCols);
            foreach (var wk in _config.Weapons.Keys)
            {
                if (_ui.Button(wk, _config.Player.StartingWeapon == wk ? Ui.ColAccent : null))
                { _config.Player.StartingWeapon = wk; changed = true; }
            }
            _ui.EndRow();
        }
        return changed;
    }

    // Skills ─────────────────────────────────────────────────────────────────

    private bool _secDash = true, _secTurbo = true, _secSlowMo = true;

    private bool DrawSkillEditor()
    {
        if (!_config.Skills.TryGetValue(_selSkill, out var sk))
        { _ui.Label("No skill selected.", Ui.ColTextDim); return false; }

        _ui.Label(_selSkill, Ui.ColAccent, Ui.FontBold);
        _ui.Separator();

        bool changed = ConfigEditorDefs.Skill.Draw(sk, _ui);

        // ── Dash ────────────────────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Dash", ref _secDash);
        if (_secDash)
        {
            bool has = sk.VelocitySpike.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { sk.VelocitySpike = null; sk.InvincibilityTime = null; has = false; }
                else     { sk.VelocitySpike = 400f; sk.InvincibilityTime = 0.15f; }
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                float vs = sk.VelocitySpike!.Value;
                if (_ui.Slider("Velocity Spike", ref vs, 50f, 1000f, "0")) { sk.VelocitySpike = vs; changed = true; }
                float inv = sk.InvincibilityTime ?? 0f;
                if (_ui.Slider("Invincible Time", ref inv, 0f, 2f)) { sk.InvincibilityTime = inv; changed = true; }
            }
        }

        // ── Turbo ────────────────────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Turbo", ref _secTurbo);
        if (_secTurbo)
        {
            bool has = sk.ThrustMult.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { sk.ThrustMult = null; has = false; }
                else       sk.ThrustMult = 2.5f;
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                float tm = sk.ThrustMult!.Value;
                if (_ui.Slider("Thrust Mult", ref tm, 1f, 10f)) { sk.ThrustMult = tm; changed = true; }
            }
        }

        // ── Slow-Mo ──────────────────────────────────────────────────────────
        _ui.Space(4f);
        _ui.Header("Slow-Mo", ref _secSlowMo);
        if (_secSlowMo)
        {
            bool has = sk.TimeScale.HasValue;
            _ui.BeginRow(2);
            _ui.Label(has ? "Active" : "Inactive", has ? Ui.ColAccent : Ui.ColTextDim);
            if (has ? _ui.Button("Remove") : _ui.Button("Enable"))
            {
                if (has) { sk.TimeScale = null; sk.PlayerSpeedBoost = null; has = false; }
                else     { sk.TimeScale = 0.3f; sk.PlayerSpeedBoost = 1.5f; }
                changed = true;
            }
            _ui.EndRow();
            if (has)
            {
                float ts = sk.TimeScale!.Value;
                if (_ui.Slider("Time Scale", ref ts, 0.05f, 1f)) { sk.TimeScale = ts; changed = true; }
                float pb = sk.PlayerSpeedBoost ?? 1f;
                if (_ui.Slider("Player Speed×", ref pb, 0.5f, 4f)) { sk.PlayerSpeedBoost = pb; changed = true; }
            }
        }

        return changed;
    }

    // Shapes ─────────────────────────────────────────────────────────────────

    private void DrawShapeInfo()
    {
        if (!_shapes.TryGetValue(_selShape, out var sh))
        { _ui.Label("No shape selected.", Ui.ColTextDim); return; }

        _ui.Label(sh.Name, Ui.ColAccent, Ui.FontBold);
        _ui.Label("Material: " + sh.Material, Ui.ColTextDim);
        _ui.Label($"Outline verts: {sh.Outline.Length}", Ui.ColTextDim);
        _ui.Label($"Seeds: {sh.Seeds.Length}", Ui.ColTextDim);
        _ui.Separator();

        _ui.Label("Seed list", Ui.ColText, Ui.FontBold);
        foreach (var seed in sh.Seeds)
        {
            var rc = ShapeEditorViewport.RoleColor(seed.Role);
            _ui.Label($"  {seed.Role,-10} bond×{seed.BondMult:0.##}", rc, Ui.FontSmall);
        }

        _ui.Space(8f);
        _ui.Separator();
        _ui.Label("Viewport controls:", Ui.ColTextDim);
        _ui.Label("  Click  — select / add seed", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("  Tab    — cycle role", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("  Drag   — move seed", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("  RMB / Bksp — delete seed", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("  Ctrl+S — save shape", Ui.ColTextDim, Ui.FontSmall);
        _ui.Label("  Ctrl+Z — revert", Ui.ColTextDim, Ui.FontSmall);
    }

    // Waves ──────────────────────────────────────────────────────────────────

    private bool DrawWaveEditor()
    {
        var ws = _config.WaveSystem;

        _ui.Label("WAVE PREVIEW", Ui.ColAccent, Ui.FontBold);
        _ui.Separator();

        float pt = _previewTime;
        if (_ui.Slider("Preview Time (s)", ref pt, 0f, 900f, "0")) _previewTime = pt;

        // Derived values at preview time
        float gInterval = MathF.Max(1f, ws.GrowthIntervalSeconds);
        int intervals   = (int)(_previewTime / gInterval);
        int budget      = ws.BaseBudget + intervals * ws.BudgetGrowthPerInterval;
        int cellCap     = Math.Min(ws.BaseCellCap + intervals * ws.CellCapGrowthAmount, ws.MaxCellCap);
        float rampT     = ws.SizeBiasRampEnd > 0f ? MathF.Min(1f, _previewTime / ws.SizeBiasRampEnd) : 1f;
        float sizeBias  = ws.SizeBiasStart + rampT * (ws.SizeBiasEnd - ws.SizeBiasStart);

        _ui.Label($"Budget: {budget}     Cell Cap: {cellCap}", Ui.ColText);
        _ui.Label($"Size Bias: {sizeBias:+0.00;-0.00;0.00}", Ui.ColText);

        _ui.Separator();
        _ui.Label("Asteroid Weights", Ui.ColText, Ui.FontBold);
        bool anyAsteroid = false;
        foreach (var (key, entry) in ws.SpawnBias)
        {
            if (!_config.Asteroids.ContainsKey(key)) continue;
            anyAsteroid = true;
            float w = EvalBias(entry, _previewTime);
            var col = w > 0f ? Ui.ColText : Ui.ColTextDim;
            _ui.Label($"  {key,-12}  {(w > 0f ? $"{w:0.000}" : "—")}", col, Ui.FontSmall);
        }
        if (!anyAsteroid) _ui.Label("  (none in SpawnBias)", Ui.ColTextDim, Ui.FontSmall);

        _ui.Separator();
        _ui.Label("Alien Weights", Ui.ColText, Ui.FontBold);
        bool anyAlien = false;
        foreach (var (key, entry) in ws.SpawnBias)
        {
            if (!_config.Entities.ContainsKey(key)) continue;
            anyAlien = true;
            float w = EvalBias(entry, _previewTime);
            var col = w > 0f ? Ui.ColText : Ui.ColTextDim;
            _ui.Label($"  {key,-12}  {(w > 0f ? $"{w:0.000}" : "—")}", col, Ui.FontSmall);
        }
        if (!anyAlien) _ui.Label("  (none in SpawnBias)", Ui.ColTextDim, Ui.FontSmall);

        _ui.Separator();
        if (_session != null)
        {
            if (_ui.Button("Spawn at Preview Time"))
            {
                _session.GameTime = _previewTime;
                _session.ForceNextWave();
            }
            if (_ui.Button("Spawn Now")) _session.ForceNextWave();
        }
        else
        {
            _ui.Label("Session not connected.", Ui.ColTextDim);
        }

        return false;
    }

    private static float EvalBias(SpawnBiasEntry e, float t)
    {
        if (t <= e.T0) return e.W0;
        if (t >= e.T1) return e.W1;
        float f = (t - e.T0) / (e.T1 - e.T0);
        return e.W0 + f * (e.W1 - e.W0);
    }

    // ── Shared header row: name + Rename button ───────────────────────────────

    private void DrawAssetHeader(string name, int tab)
    {
        _ui.Label(name, Ui.ColAccent, Ui.FontBold);
        _ui.Separator();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void OnNew()
    {
        string baseName = _tab switch { 1 => "material", 2 => "weapon", 3 => "asteroid", _ => "item" };
        int n = 1;
        string name;
        while (true)
        {
            name = $"{baseName}_{n}";
            bool taken = _tab switch
            {
                1 => _config.Materials.ContainsKey(name),
                2 => _config.Weapons.ContainsKey(name),
                3 => _config.Asteroids.ContainsKey(name),
                _ => false
            };
            if (!taken) break;
            n++;
        }

        switch (_tab)
        {
            case 1: _config.Materials[name] = new MaterialConfig(); _selMat = name; break;
            case 2: _config.Weapons[name]   = new WeaponConfig();   _selWpn = name; break;
            case 3:
                _config.Asteroids[name] = new AsteroidConfig { Procedural = new ProceduralAsteroidConfig() };
                _selAst = name;
                break;
        }

        // Start rename immediately
        _editingName = name;
        _renameIsNew = true;
        _dirty = true;
    }

    private void OnDelete()
    {
        switch (_tab)
        {
            case 1: _config.Materials.Remove(_selMat); _selMat = _config.Materials.Keys.FirstOrDefault() ?? ""; break;
            case 2: _config.Weapons.Remove(_selWpn);   _selWpn = _config.Weapons.Keys.FirstOrDefault()   ?? ""; break;
            case 3: _config.Asteroids.Remove(_selAst); _selAst = _config.Asteroids.Keys.FirstOrDefault() ?? ""; break;
        }
        _editingName = null;
        _dirty = true;
    }

    private void CommitRename(string newName, string oldKey)
    {
        newName = SanitizeName(newName);
        if (newName.Length == 0 || newName == oldKey)
        { _editingName = null; _renameIsNew = false; return; }

        bool conflict = _tab switch
        {
            1 => _config.Materials.ContainsKey(newName),
            2 => _config.Weapons.ContainsKey(newName),
            3 => _config.Asteroids.ContainsKey(newName),
            _ => false
        };
        if (conflict) { _editingName = null; _renameIsNew = false; return; } // silently keep old name

        switch (_tab)
        {
            case 1:
                _config.Materials[newName] = _config.Materials[oldKey];
                _config.Materials.Remove(oldKey);
                _selMat = newName;
                break;
            case 2:
                _config.Weapons[newName] = _config.Weapons[oldKey];
                _config.Weapons.Remove(oldKey);
                _selWpn = newName;
                break;
            case 3:
                _config.Asteroids[newName] = _config.Asteroids[oldKey];
                _config.Asteroids.Remove(oldKey);
                _selAst = newName;
                break;
        }
        _editingName = null;
        _renameIsNew = false;
        _dirty = true;
    }

    private static string SanitizeName(string s)
    {
        // Allow lowercase letters, digits and underscores (JSON key friendly).
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        return sb.ToString().Trim('_');
    }

    private void OnSave()
    {
        GameConfigLoader.Save(_config, _assetsDir);
        _dirty = false;
    }

    private void OnRevert()
    {
        var (fresh, _) = GameConfigLoader.Load(_assetsDir);
        switch (_tab)
        {
            case 1 when fresh.Materials.TryGetValue(_selMat, out var m):
                _config.Materials[_selMat] = m; break;
            case 2 when fresh.Weapons.TryGetValue(_selWpn, out var w):
                _config.Weapons[_selWpn] = w; break;
            case 3 when fresh.Asteroids.TryGetValue(_selAst, out var a):
                _config.Asteroids[_selAst] = a; break;
            case 4:
                _config.Player.Thrust          = fresh.Player.Thrust;
                _config.Player.RotSpeed        = fresh.Player.RotSpeed;
                _config.Player.MaxSpeed        = fresh.Player.MaxSpeed;
                _config.Player.BrakeDrag       = fresh.Player.BrakeDrag;
                _config.Player.Impulse         = fresh.Player.Impulse;
                _config.Player.StartingWeapon  = fresh.Player.StartingWeapon;
                break;
            case 6:
                _config.MaxLiveCells = fresh.MaxLiveCells;
                if (_selWave < _config.Waves.Count && _selWave < fresh.Waves.Count)
                    _config.Waves[_selWave] = fresh.Waves[_selWave];
                break;
            case 8:
                _config.Fracture = fresh.Fracture;
                GameContext.ApplyFractureTuning(_config.Fracture);
                break;
        }
        _dirty = false;
    }
}
