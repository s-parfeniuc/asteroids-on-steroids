using System;
using System.Collections.Generic;
using System.Diagnostics;
using AsteroidsSim.Config;
using AsteroidsSim.Fracture;
using AsteroidsSim.Math;
using Godot;

// AsteroidsSim.Fracture.Material and Godot.Material are both in scope here.
using SimMaterial = AsteroidsSim.Fracture.Material;

namespace AsteroidsGame.Viewer;

/// <summary>
/// A development viewer for the ported fracture model.
/// </summary>
/// <remarks>
/// <para>Everything validating the port so far has been numeric — conservation invariants,
/// morphology asserted over perturbation ensembles, a state fingerprint. Those catch arithmetic.
/// They cannot say whether glass still shatters, whether steel still necks rather than snapping, or
/// whether cracks still run to a surface instead of appearing inside a body. This puts the solver on
/// screen so that can be judged by eye.</para>
///
/// <para><b>Configuration.</b> Every number the model is built from comes from <c>Assets/sim.json</c>,
/// loaded at start and reloaded with L. The panel's sliders are live overrides on top of it; a
/// reload discards them.</para>
///
/// <para><b>Not the game.</b> <c>Sim/SimRoot.cs</c> is the Phase-1 game host; this is a tool. They
/// are kept apart on purpose.</para>
///
/// <para><b>Geometry path.</b> The batched <c>RenderingServer</c> route Spike A validated: cell fills
/// as one triangle array, bonds and outlines as one multiline, buffers allocated once and reused.
/// Cell polygons come back in body-local space and the body transform is applied here, which is what
/// makes interpolation affordable.</para>
/// </remarks>
public partial class Viewer : Node2D
{
    // ── scenarios ────────────────────────────────────────────────────────────

    private static readonly string[] ScenarioNames =
    {
        "collide", "projectile", "spin", "glass projectile", "steel projectile", "field",
        "steel shell + rock core", "blast",
    };

    /// <summary>The loaded configuration: tuning, model constants and the material table.</summary>
    private SimConfig _cfg = null!;
    /// <summary>Why the last reload was refused, shown on the readout until the next good one.</summary>
    private string? _configError;
    private SimMaterial[] Materials => _cfg.Materials;

    private int _scenario;
    private int _materialIndex;

    /// <summary>
    /// The impactor's material, independent of the target's. They were the same knob, which quietly
    /// made half the interesting questions unaskable: a steel slug into rock and a rock into steel
    /// are different experiments, and neither is a body colliding with itself.
    /// </summary>
    private int _impactorMaterialIndex;
    private SimTuning _tune;

    // Comminution multipliers on the selected materials' authored pair. See Crushed().
    /// <summary>
    /// Per-role multipliers over every authored material field, so a material can be tuned in the
    /// viewer without editing the table.
    /// </summary>
    /// <remarks>
    /// Multipliers rather than absolute values, because they compose with whichever base material is
    /// picked: x2 crush means the same thing for ice as for steel. Index 0 is the body, 1 the
    /// impactor (and the round left-click fires), so a round can be tuned against a target without
    /// changing the target.
    /// </remarks>
    private enum MatField { Rho, C, Strain, Chi, Crush, Rate, Shed, Dent }
    private static readonly string[] MatFieldNames =
        { "density", "wave speed", "failure strain", "chi (softening)", "crush threshold", "erosion rate", "shed limit", "dent width" };
    private const int MatRoles = 2;
    private static readonly string[] MatRoleNames = { "body", "impactor / round" };
    private readonly float[,] _matMul = new float[MatRoles, 8];
    private int _matRole;
    private OptionButton _matRolePick = null!;
    private readonly HSlider[] _matSliders = new HSlider[8];
    private readonly Label[] _matLabels = new Label[8];
    private Scenarios.Result _scene;

    // Build-time knobs. Grain is cell AREA, so cell size is its square root: 900 gives 30 px cells.
    // 0 means the floor — the finest grain the scene's materials may be built at, which is what the
    // tests use (Scenarios.FloorGrain) — and is the default.
    private float _grain;
    private float _impactorSpeed = 900f;
    private float _impactorMass = 3f;
    /// Round radius in px, independent of its mass. 0 keeps the legacy 15*sqrt(mass).
    private float _impactorSize = 10f;

    /// <summary>
    /// Set when a build-time knob moves. The rebuild is deferred to <see cref="_Process"/> and held
    /// while the mouse is down, so dragging a slider rebuilds once on release rather than on every
    /// pixel — at grain 100 a rebuild is tens of milliseconds and dragging would lock the window.
    /// </summary>
    private bool _needsReset;

    // ── run control ──────────────────────────────────────────────────────────

    private bool _running = true;
    private bool _stepOnce;
    private int _slowMo = 1;          // step once every N physics frames
    private int _slowCounter;
    private int _ticks;
    private int _shots;

    // ── view ─────────────────────────────────────────────────────────────────

    private Vector2 _pan = new(60f, 40f);
    private float _zoom = 1f;
    private bool _panning;
    // View layers, each independently switchable. "Outline only" is fills off with outlines on.
    private bool _drawFills = true;
    private bool _drawOutlines = true;

    /// <summary>
    /// Cell indices drawn in place — the same numbers the bench diagnostics print.
    /// </summary>
    /// <remarks>
    /// Drawn through the Node2D's own canvas rather than the batched RenderingServer path, because
    /// text is not something that path does and this is a debugging aid rather than a hot path. It
    /// is capped and viewport-culled so turning it on in a shattered scene does not stall the frame.
    /// </remarks>
    private bool _showIds;

    /// <summary>
    /// How bonds are coloured. A mode rather than a set of flags because a line carries one colour:
    /// damage and stress answer different questions about the same segment and cannot share it.
    /// </summary>
    private enum BondView { Off, Damage, Stress }

    /// <summary>How cell interiors are coloured.</summary>
    /// <summary>How cell interiors are coloured. Surface shows which sides face open space.</summary>
    private enum FillView { Body, Flat, Crush, Surface }
    private FillView _fillView = FillView.Body;
    private BondView _bondView = BondView.Damage;

    /// <summary>The prototype's flat cell fill when body colouring is off: <c>#232a34</c>.</summary>
    private static readonly Color FlatFill = new(0.137f, 0.165f, 0.204f);

    /// <summary>The body's real boundary: silhouette, or a face erosion has cut.</summary>
    private static readonly Color RealSurface = new(1.00f, 0.72f, 0.20f);
    /// <summary>Surface a crack opened — drawn fine, so cracks read as cracks.</summary>
    private static readonly Color CrackSurface = new(0.95f, 0.32f, 0.28f);
    /// <summary>A side still carrying a bond. Dim: it is the material, not the boundary.</summary>
    private static readonly Color InteriorSide = new(0.20f, 0.26f, 0.36f);
    /// <summary>Touching material with no bond — too short to build one. Interior, but unbonded.</summary>
    private static readonly Color SealedSide = new(0.34f, 0.24f, 0.44f);

    // ── render resources ─────────────────────────────────────────────────────

    private Rid _fills;
    private Rid _lines;
    private Label _hud = null!;

    // Controls, kept as fields so the keyboard shortcuts can move them and the two stay in step.
    private OptionButton _scenarioPick = null!;
    private OptionButton _materialPick = null!;
    private HSlider _confineSlider = null!;
    private HSlider _biasSlider = null!, _maxBiasSlider = null!;
    private CheckBox _crackPushBox = null!, _splitBox = null!, _grainLockBox = null!;
    private HSlider _crackCapSlider = null!, _ceilingSlider = null!;
    private OptionButton _roundPick = null!;
    private HSlider _blastPresSlider = null!, _blastRadSlider = null!, _fuseSlider = null!;
    private HSlider _weibullSlider = null!, _anisoSlider = null!, _grainSlider2 = null!, _flawSlider = null!;

    /// <summary>What left-click fires. The material model is the same for all of them; what differs
    /// is how the load arrives — a lump of matter, a rod, a pressure front, or a lump then a front.</summary>
    private enum Round { Bullet, Pierce, Blast, Explosive }
    private static readonly string[] RoundNames = { "bullet", "piercing rod", "blast", "explosive" };
    private Round _round = Round.Bullet;
    private float _blastPressure = 3e5f, _blastRadius = 260f;
    private int _fuseTicks = 150;
    private int _pendingFuse = -1;                       // ticks left before an explosive round goes off
    private float _fuseBodyX, _fuseBodyY;
    private int _fuseBody = -1;
    private OptionButton _impactorPick = null!;
    private HSlider _grainSlider = null!, _speedSlider = null!, _massSlider = null!;
    private HSlider _strainSlider = null!, _toughSlider = null!, _substepSlider = null!;
    private HSlider _slowSlider = null!;

    /// <summary>Each slider's caption and formatter, so a value can be shown without firing its
    /// handler — which would round a configured value to the slider's step and write it back.</summary>
    private readonly Dictionary<HSlider, (Label Caption, string Name, Func<double, string> Format)> _captions = new();
    private CheckBox _fillBox = null!, _outlineBox = null!, _idBox = null!;
    private OptionButton _fillPick = null!;
    private OptionButton _bondPick = null!;

    private Vector2[] _pts = Array.Empty<Vector2>();
    private Color[] _cols = Array.Empty<Color>();
    private int[] _idx = Array.Empty<int>();
    private int _ptCount, _idxCount;

    private Vector2[] _linePts = Array.Empty<Vector2>();
    private Color[] _lineCols = Array.Empty<Color>();
    private int _lineCount;

    // A second batch submitted at a heavier width. CanvasItemAddMultiline takes one width per call,
    // so weight is per batch, not per segment — which is exactly enough to tell the two kinds of
    // open side apart at a glance.
    private Vector2[] _boldPts = Array.Empty<Vector2>();
    private Rid _marks;
    private Color[] _boldCols = Array.Empty<Color>();
    private int _boldCount;


    // Interpolation, per CELL: each cell's world centre and orientation at the start of the tick
    // being shown. A cell's world pose is continuous through a split — the fragment is re-centred,
    // not moved — so this interpolates straight across topology changes, which a per-body pose
    // cannot: a split renumbers bodies.
    private float[] _prevCx = Array.Empty<float>(), _prevCy = Array.Empty<float>();
    private float[] _prevRot = Array.Empty<float>();
    private bool[] _prevLive = Array.Empty<bool>();
    private int _prevCells;

    // The pose each cell is drawn at this frame, shared by fills, bonds, vertex marks and ids.
    private float[] _drawCx = Array.Empty<float>(), _drawCy = Array.Empty<float>();
    private float[] _drawSi = Array.Empty<float>(), _drawCo = Array.Empty<float>();

    /// <summary>Whether the last <see cref="_Draw"/> put ids on screen; one more redraw clears them.</summary>
    private bool _idsOnScreen;

    // ── timing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when running with <c>--headless</c>. The viewer then acts as a smoke test: it steps a
    /// fixed number of ticks, prints the readout it would have drawn, and exits — so "does the
    /// renderer still produce geometry" is answerable without a display, and in CI.
    /// </summary>
    private bool _headless;
    private int _headlessTicks = 120;

    /// <summary>
    /// <c>--shot &lt;path&gt;</c>: run to the tick limit with a real window, save a PNG of the frame,
    /// and quit. The UI is the part that cannot be checked from a text readout — the panel was laid
    /// out off-screen once already and nothing but a picture would have said so.
    /// </summary>
    private string? _shotPath;
    private bool _shotPending;
    private int _fieldCols = 6, _fieldRows = 5;
    // 150 spreads them out; 128 is bodies just touching; 118 starts half-interpenetrating.
    private float _fieldSpacing = 150f;

    private readonly Stopwatch _sw = new();
    private double _msTick, _msBuild, _msSubmit;

    // Per-stage cost, straight off the solver's own phase marks. Accumulated across a tick (most
    // stages run once per substep) and smoothed, so the readout is what each stage costs per TICK.
    private readonly Stopwatch _phaseSw = new();
    private readonly double[] _phaseAcc = new double[(int)SolverPhase.Count];
    private readonly double[] _phaseMs = new double[(int)SolverPhase.Count];

    private static readonly string[] PhaseNames =
    {
        "broadphase", "narrow phase", "contact refresh", "inertial loads", "bond forces",
        "contact solve", "bond integrate", "decompose", "damping", "damage",
        "split", "settle", "dust",
    };
    private static double Smooth(double prev, double now) => prev <= 0 ? now : prev * 0.9 + now * 0.1;

    // ─────────────────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        _fills = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_fills, GetCanvasItem());
        _lines = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_lines, GetCanvasItem());
        _marks = RenderingServer.CanvasItemCreate();          // created last: draws over the lines
        RenderingServer.CanvasItemSetParent(_marks, GetCanvasItem());

        _hud = new Label
        {
            Position = new Vector2(12, 8),
            Modulate = new Color(0.83f, 0.86f, 0.90f),
            // Explicit: a Stop filter here would eat every click in the top-left of the world.
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _hud.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_hud);

        RenderingServer.SetDefaultClearColor(new Color(0.027f, 0.039f, 0.055f));
        for (int r = 0; r < MatRoles; r++) for (int f = 0; f < 8; f++) _matMul[r, f] = 1f;
        _headless = DisplayServer.GetName() == "headless";
        _cfg = SimConfigFile.Load();
        _tune = _cfg.Tuning;
        _impactorMaterialIndex = MaterialIndex("steel");
        ParseArgs();
        if (!_headless) BuildUi();
        Reset();
    }

    /// <summary>
    /// The control panel. Every knob is on it, so nothing needs a keyboard shortcut to be reachable.
    /// </summary>
    /// <remarks>
    /// Godot Controls consume the clicks that land on them before <see cref="_UnhandledInput"/> sees
    /// them, so dragging a slider cannot also fire a projectile or pan the view. The keyboard
    /// shortcuts are kept, but they move the controls rather than the fields behind them — one
    /// source of truth, and the panel always shows what the simulation is actually running with.
    /// </remarks>
    private void BuildUi()
    {
        // UI goes under a CanvasLayer, not straight onto the Node2D. A Control parented to a Node2D
        // has no parent rect to anchor against, so SetAnchorsPreset resolves against a zero-size
        // origin and the panel is laid out off-screen — present, receiving nothing, invisible.
        var layer = new CanvasLayer { Layer = 1 };
        AddChild(layer);

        // Full-rect host so the anchors below have something to mean. Ignore on the host itself, or
        // it swallows every click before the viewport sees it and firing and panning stop working.
        var host = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(host);

        // The readout moves in here too, so it shares the layer and cannot be covered by the world.
        _hud.GetParent()?.RemoveChild(_hud);
        host.AddChild(_hud);

        // Anchored down the whole right edge, not just the top corner: the panel is taller than a
        // 648-line window and the buttons at its foot were being cut off. Full height plus a scroll
        // container means it fits whatever the window is.
        var root = new MarginContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        root.GrowHorizontal = Control.GrowDirection.Begin;
        foreach (string side in new[] { "top", "bottom" })
            root.AddThemeConstantOverride($"margin_{side}", 8);
        root.AddThemeConstantOverride("margin_right", 12);
        host.AddChild(root);

        var panel = new PanelContainer();
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.09f, 0.12f, 0.92f),
            BorderColor = new Color(0.25f, 0.29f, 0.35f),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        };
        bg.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", bg);
        root.AddChild(panel);

        var inner = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            inner.AddThemeConstantOverride($"margin_{side}", 10);
        panel.AddChild(inner);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(268, 0),
        };
        inner.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(box);

        _scenarioPick = new OptionButton();
        foreach (string n in ScenarioNames) _scenarioPick.AddItem(n);
        _scenarioPick.Selected = _scenario;
        _scenarioPick.ItemSelected += i => { _scenario = (int)i; Reset(); };
        AddRow(box, "scenario", _scenarioPick);

        _materialPick = new OptionButton();
        foreach (var m in Materials) _materialPick.AddItem(m.Name);
        _materialPick.Selected = _materialIndex;
        _materialPick.ItemSelected += i => { _materialIndex = (int)i; Reset(); };
        AddRow(box, "body", _materialPick);

        _impactorPick = new OptionButton();
        foreach (var m in Materials) _impactorPick.AddItem(m.Name);
        _impactorPick.Selected = _impactorMaterialIndex;
        _impactorPick.ItemSelected += i => { _impactorMaterialIndex = (int)i; Reset(); };
        AddRow(box, "impactor", _impactorPick);

        box.AddChild(new HSeparator());

        // Step 5 rather than something coarser because grain is an AREA. The caption carries the
        // square root, which is the number that is actually intuitive. 0 is the floor: the finest
        // grain the scene's materials may be built at, the same one the tests use.
        _grainSlider = AddSlider(box, "grain", 0, 2500, 5, _grain,
            v => { _grain = (float)v; _needsReset = true; },
            v =>
            {
                // Bodies are never built finer than their materials can be integrated at the current
                // substep count (BodyBuilder.MinGrain), so say what the scene is actually built at.
                float floor = SceneFloorGrain();
                if (v <= 0) return $"floor {floor:F0}  ({MathF.Sqrt(floor):F1} px cells), as the tests";
                return v < floor
                    ? $"{v:F0} -> {floor:F0}, the floor for this scene  ({MathF.Sqrt(floor):F1} px cells)"
                    : $"{v:F0}  ({MathF.Sqrt((float)v):F1} px cells)";
            });

        _speedSlider = AddSlider(box, "impactor speed", 100, 4000, 50, _impactorSpeed,
            v => { _impactorSpeed = (float)v; _needsReset = true; }, v => $"{v:F0} px/s");

        AddSlider(box, "impactor size", 0, 60, 1, _impactorSize,
            v => { _impactorSize = (float)v; _needsReset = true; },
            v => v <= 0 ? "from mass" : $"{v:F0} px");
        _massSlider = AddSlider(box, "impactor mass", 0.5, 24, 0.5, _impactorMass,
            v => { _impactorMass = (float)v; _needsReset = true; },
            v => $"x{v:F1}  (r {15f * MathF.Sqrt((float)v):F0} px)");

        box.AddChild(new HSeparator());

        _strainSlider = AddSlider(box, "strain scale", 0.1, 4, 0.1, _tune.StrainScale,
            v => { _tune.StrainScale = (float)v; _needsReset = true; }, v => $"x{v:F1}");

        _toughSlider = AddSlider(box, "toughness scale", 0.1, 4, 0.1, _tune.ToughnessScale,
            v => { _tune.ToughnessScale = (float)v; _needsReset = true; }, v => $"x{v:F1}");

        _substepSlider = AddSlider(box, "substeps", 1, 24, 1, _tune.Substeps,
            v => { _tune.Substeps = (int)v; _needsReset = true; }, v => $"{v:F0}");

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "material (multipliers over sim.json)" });

        // Which material the sliders below edit. Body and round are separate sets, so a round can be
        // tuned against a target without changing the target.
        _matRolePick = new OptionButton();
        foreach (string n in MatRoleNames) _matRolePick.AddItem(n);
        _matRolePick.Selected = _matRole;
        _matRolePick.ItemSelected += i => { _matRole = (int)i; RefreshMatSliders(); };
        box.AddChild(_matRolePick);

        // Log2, because the useful range is multiplicative and a linear slider over a 64x span
        // wastes nine tenths of its travel. Captions carry the ABSOLUTE value for the material the
        // role currently resolves to, since that is the number to copy back into sim.json.
        for (int f = 0; f < MatFieldNames.Length; f++)
        {
            int fi = f;
            var caption = new Label();
            box.AddChild(caption);
            _matLabels[fi] = caption;
            var sl = new HSlider
            {
                MinValue = -6, MaxValue = 6, Step = 0.25,
                Value = MathF.Log2(_matMul[_matRole, fi]),
                CustomMinimumSize = new Vector2(0, 18),
            };
            sl.ValueChanged += v =>
            {
                _matMul[_matRole, fi] = MathF.Pow(2f, (float)v);
                _needsReset = true;
                MatCaption(fi);
            };
            box.AddChild(sl);
            _matSliders[fi] = sl;
            MatCaption(fi);
        }

        // The Baumgarte positional bias. Numerical, not physical: it pushes overlapping cells apart
        // at a velocity proportional to their overlap. Carving now owns overlap relief (peak overlap
        // does not move with the bias any more), so what the bias still controls is how hard
        // fragments are thrown apart — 241 px/s at 0.20 down to 36 px/s at 0.02 on the grain-170
        // collide. Live, so it can be swept while watching a break-up.
        _biasSlider = AddSlider(box, "contact bias", 0, 0.5, 0.01, _tune.ContactBias,
            v => _tune.ContactBias = (float)v, v => $"{v:F2}");
        _maxBiasSlider = AddSlider(box, "contact max bias", 0, 8, 0.25, _tune.ContactMaxBias,
            v => _tune.ContactMaxBias = (float)v, v => $"{v:F2}");

        // A crack can push but not pull: broken bonds between live cells keep their compressive
        // normal force. Without it a detached front row slides into the row behind it with no
        // resistance until the body is split at the end of the tick. Live; read every substep.
        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "round (left-click fires)" });

        // The material model is identical for all four; what differs is how the load arrives.
        _roundPick = new OptionButton();
        foreach (string n in RoundNames) _roundPick.AddItem(n);
        _roundPick.Selected = (int)_round;
        _roundPick.ItemSelected += i => _round = (Round)i;
        box.AddChild(_roundPick);

        _blastPresSlider = AddSlider(box, "blast pressure", 1e4, 2e6, 1e4, _blastPressure,
            v => _blastPressure = (float)v, v => $"{v:E1}");
        _blastRadSlider = AddSlider(box, "blast radius", 40, 600, 10, _blastRadius,
            v => _blastRadius = (float)v, v => $"{v:F0} px");
        _fuseSlider = AddSlider(box, "explosive max flight", 10, 400, 10, _fuseTicks,
            v => _fuseTicks = (int)v, v => $"{v:F0} ticks");

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "structure (per body, at build)" });

        // Heterogeneity: what decides WHERE a body cracks, as opposed to how hard that is. All of it
        // is applied once at build and scales bond strength only, so every one of these resets.
        _weibullSlider = AddSlider(box, "weibull m", 0, 30, 0.5, _tune.WeibullM,
            v => { _tune.WeibullM = (float)v; _needsReset = true; },
            v => v <= 0 ? "off (uniform)" : $"{v:F1}");
        _anisoSlider = AddSlider(box, "anisotropy", 0, 0.9, 0.05, _tune.Aniso,
            v => { _tune.Aniso = (float)v; _needsReset = true; },
            v => v <= 0 ? "off (isotropic)" : $"{v:F2}");
        _grainSlider2 = AddSlider(box, "grain angle", 0, 180, 5, _tune.GrainAngle * 180f / MathF.PI,
            v => { _tune.GrainAngle = (float)v * MathF.PI / 180f; _needsReset = true; },
            v => $"{v:F0}°");
        _grainLockBox = new CheckBox { Text = "lock grain (else random per body)", ButtonPressed = _tune.GrainLock };
        _grainLockBox.Toggled += on => { _tune.GrainLock = on; _needsReset = true; };
        box.AddChild(_grainLockBox);
        _flawSlider = AddSlider(box, "surface flaws", 0, 0.9, 0.05, _tune.SurfFlaw,
            v => { _tune.SurfFlaw = (float)v; _needsReset = true; },
            v => v <= 0 ? "off" : $"-{v * 100:F0}% at the boundary");

        box.AddChild(new HSeparator());
        _crackPushBox = new CheckBox { Text = "cracks transmit compression", ButtonPressed = _tune.CrackPush };
        _crackPushBox.Toggled += on => _tune.CrackPush = on;
        box.AddChild(_crackPushBox);

        // MUST stay above zero while cracks push. A broken bond can only push, and same-body pairs
        // never reach the contact solver, so nothing else bounds how far two cells can be driven
        // together: uncapped it diverged to 1e18 px/s within a tick.
        _crackCapSlider = AddSlider(box, "crack push cap", 0, 20, 0.5, _tune.CrackPushCap,
            v => _tune.CrackPushCap = (float)v,
            v => v <= 0 ? "UNCAPPED (diverges)" : $"{v:F1}x failure stretch");

        // A body splits along a crack only once it has opened: while a broken bond's faces are still
        // pressed together the two sides are one body, so contact impulses on a pressed fragment go
        // to the whole body. Needs "cracks transmit compression". Live.
        _splitBox = new CheckBox { Text = "split only when cracks open", ButtonPressed = _tune.SplitOnOpen };
        _splitBox.Toggled += on => _tune.SplitOnOpen = on;
        box.AddChild(_splitBox);

        // (min clip area: v1 only, lone cells; removed from the panel with carving v2)

        // The one free constant in the pressure measure, weighting penetration strain against the
        // braking impulse. Live rather than reset-on-change: it is read every substep, so it can be
        // moved while watching a contact.
        _confineSlider = AddSlider(box, "confine weight", 0, 4, 0.05, _tune.CrushConfine,
            v => _tune.CrushConfine = (float)v, v => $"{v:F3}");
        _ceilingSlider = AddSlider(box, "overlap ceiling", 0, 1, 0.05, _tune.OverlapBackstop,
            v => _tune.OverlapBackstop = (float)v,
            v => v <= 0 ? "off" : $"{v:F2} of the thinner cell's half-extent");

        box.AddChild(new HSeparator());

        _slowSlider = AddSlider(box, "slow motion", 0, 5, 1, 0,
            v => _slowMo = 1 << (int)v, v => v <= 0 ? "full speed" : $"1/{1 << (int)v}");

        box.AddChild(new Label { Text = "view" });

        _fillBox = new CheckBox { Text = "cell fills", ButtonPressed = _drawFills };
        _fillBox.Toggled += on => _drawFills = on;
        box.AddChild(_fillBox);

        _fillPick = new OptionButton();
        foreach (string n in new[] { "body colour", "flat", "shed fraction", "surface" }) _fillPick.AddItem(n);
        _fillPick.Selected = (int)_fillView;
        _fillPick.ItemSelected += i => _fillView = (FillView)i;
        AddRow(box, "fill", _fillPick);

        _outlineBox = new CheckBox { Text = "cell outlines", ButtonPressed = _drawOutlines };
        _outlineBox.Toggled += on => _drawOutlines = on;
        box.AddChild(_outlineBox);

        _idBox = new CheckBox { Text = "cell ids (I)", ButtonPressed = _showIds };
        _idBox.Toggled += on => _showIds = on;
        box.AddChild(_idBox);

        _bondPick = new OptionButton();
        foreach (string n in new[] { "off", "damage", "stress" }) _bondPick.AddItem(n);
        _bondPick.Selected = (int)_bondView;
        _bondPick.ItemSelected += i => _bondView = (BondView)i;
        AddRow(box, "bonds", _bondPick);

        var buttons = new HBoxContainer();
        var reset = new Button { Text = "reset", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += Reset;
        buttons.AddChild(reset);

        var reload = new Button { Text = "reload", TooltipText = "re-read Assets/sim.json (L)",
                                  SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reload.Pressed += ReloadConfig;
        buttons.AddChild(reload);

        var pause = new Button { Text = "pause", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pause.Pressed += () => { _running = !_running; pause.Text = _running ? "pause" : "resume"; };
        buttons.AddChild(pause);

        var step = new Button { Text = "step", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        step.Pressed += () => { _stepOnce = true; _running = false; pause.Text = "resume"; };
        buttons.AddChild(step);

        box.AddChild(buttons);
    }

    private static void AddRow(VBoxContainer parent, string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(96, 0) });
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        parent.AddChild(row);
    }

    /// <summary>A labelled slider whose caption shows the value in the units that mean something.</summary>
    private HSlider AddSlider(VBoxContainer parent, string name,
        double min, double max, double step, double value,
        Action<double> apply, Func<double, string> format)
    {
        var caption = new Label { Text = $"{name}   {format(value)}" };
        parent.AddChild(caption);

        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            CustomMinimumSize = new Vector2(0, 18),
        };
        slider.ValueChanged += v =>
        {
            apply(v);
            caption.Text = $"{name}   {format(v)}";
        };
        parent.AddChild(slider);
        _captions[slider] = (caption, name, format);
        return slider;
    }

    /// <summary>
    /// Shows <paramref name="value"/> on a slider without firing its handler. The caption gets the
    /// exact value; the slider's knob snaps to its step, but nothing is written back.
    /// </summary>
    private void ShowValue(HSlider? slider, double value)
    {
        if (slider == null || !_captions.TryGetValue(slider, out var c)) return;
        slider.SetValueNoSignal(value);
        c.Caption.Text = $"{c.Name}   {c.Format(value)}";
    }

    /// <summary>
    /// Arguments after <c>--</c>: <c>--scenario N</c>, <c>--material N</c>, <c>--ticks N</c>.
    /// Used with <c>--headless</c> to collect the frame split scenario by scenario.
    /// </summary>
    private void ParseArgs()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--shot") { _shotPath = args[i + 1]; continue; }
            if (!int.TryParse(args[i + 1], out int v)) continue;
            switch (args[i])
            {
                case "--scenario": _scenario = System.Math.Clamp(v, 0, ScenarioNames.Length - 1); break;
                case "--material": _materialIndex = System.Math.Clamp(v, 0, Materials.Length - 1); break;
                case "--impactor": _impactorMaterialIndex = System.Math.Clamp(v, 0, Materials.Length - 1); break;
                case "--ticks": _headlessTicks = System.Math.Max(1, v); break;
                case "--cols": _fieldCols = System.Math.Clamp(v, 1, 64); break;
                case "--rows": _fieldRows = System.Math.Clamp(v, 1, 64); break;
                case "--spacing": _fieldSpacing = System.Math.Clamp(v, 60, 400); break;
                case "--grain": _grain = System.Math.Clamp(v, 0, 2500); break;
                case "--substeps": _tune.Substeps = System.Math.Clamp(v, 1, 64); break;
                case "--speed": _impactorSpeed = System.Math.Clamp(v, 100, 4000); break;
                case "--mass": _impactorMass = System.Math.Clamp(v, 1, 24); break;
                case "--outlines": _drawOutlines = v != 0; break;
                case "--fills": _drawFills = v != 0; break;
                case "--bonds": _bondView = (BondView)System.Math.Clamp(v, 0, 2); break;
                case "--fill": _fillView = (FillView)System.Math.Clamp(v, 0, 2); break;
                // --matmul <role*100 + field> <log2 x100>, so the material editor can be driven
                // headless and its plumbing checked without a window.
                case "--matmul":
                {
                    int role = System.Math.Clamp(v / 100, 0, MatRoles - 1), field = System.Math.Clamp(v % 100, 0, 7);
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out int lg))
                        _matMul[role, field] = MathF.Pow(2f, lg / 100f);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Re-reads <c>Assets/sim.json</c> and rebuilds the scene from it.
    /// </summary>
    /// <remarks>
    /// The file is what runs afterwards: the panel's tuning overrides are replaced by the file's
    /// values and the material multipliers go back to x1, so a value copied from the panel into the
    /// file is not applied twice. A file that fails to load is refused, the running configuration
    /// is kept, and the reason is shown on the readout.
    /// </remarks>
    private void ReloadConfig()
    {
        SimConfig next;
        try
        {
            next = SimConfigFile.Load();
            foreach (string name in new[] { "rock", "glass", "steel", "penetrator" }) next.Material(name);
        }
        catch (Exception e)
        {
            _configError = e.Message;
            GD.PrintErr($"sim.json not reloaded: {e.Message}");
            return;
        }

        string body = Materials[_materialIndex].Name, impactor = Materials[_impactorMaterialIndex].Name;
        _cfg = next;
        _configError = null;
        _tune = next.Tuning;
        _materialIndex = MaterialIndex(body);
        _impactorMaterialIndex = MaterialIndex(impactor);
        for (int r = 0; r < MatRoles; r++) for (int f = 0; f < 8; f++) _matMul[r, f] = 1f;
        RefreshPanel();
        Reset();
    }

    /// <summary>Puts every configured value back on the panel, without firing any handler.</summary>
    private void RefreshPanel()
    {
        if (_materialPick == null) return;                       // headless: there is no panel
        foreach (var (pick, index) in new[] { (_materialPick, _materialIndex), (_impactorPick, _impactorMaterialIndex) })
        {
            pick.Clear();
            foreach (var m in Materials) pick.AddItem(m.Name);
            pick.Selected = index;
        }
        ShowValue(_strainSlider, _tune.StrainScale);
        ShowValue(_toughSlider, _tune.ToughnessScale);
        ShowValue(_substepSlider, _tune.Substeps);
        ShowValue(_biasSlider, _tune.ContactBias);
        ShowValue(_maxBiasSlider, _tune.ContactMaxBias);
        ShowValue(_weibullSlider, _tune.WeibullM);
        ShowValue(_anisoSlider, _tune.Aniso);
        ShowValue(_grainSlider2, _tune.GrainAngle * 180f / MathF.PI);
        ShowValue(_flawSlider, _tune.SurfFlaw);
        ShowValue(_crackCapSlider, _tune.CrackPushCap);
        ShowValue(_confineSlider, _tune.CrushConfine);
        ShowValue(_ceilingSlider, _tune.OverlapBackstop);
        _grainLockBox.SetPressedNoSignal(_tune.GrainLock);
        _crackPushBox.SetPressedNoSignal(_tune.CrackPush);
        _splitBox.SetPressedNoSignal(_tune.SplitOnOpen);
        RefreshMatSliders();
    }

    /// <summary>Saves the frame that has just been drawn, then exits.</summary>
    private void SaveShot()
    {
        RenderingServer.Singleton.FramePostDraw -= SaveShot;
        Image img = GetViewport().GetTexture().GetImage();
        Error e = img.SavePng(_shotPath);
        GD.Print(e == Error.Ok ? $"wrote {_shotPath}" : $"screenshot failed: {e}");
        GD.Print(_hud.Text);
        GetTree().Quit();
    }

    public override void _ExitTree()
    {
        if (_fills.IsValid) { RenderingServer.FreeRid(_fills); _fills = default; }
        if (_lines.IsValid) { RenderingServer.FreeRid(_lines); _lines = default; }
        if (_marks.IsValid) { RenderingServer.FreeRid(_marks); _marks = default; }
    }

    /// <summary>
    /// The comminution pair, retuned by the panel's two multipliers.
    /// </summary>
    /// <remarks>
    /// <para>Multipliers rather than absolute sliders, because the five materials' authored values
    /// span more than a decade and one linear range could not serve them. x1 is always whatever
    /// <c>Materials.cs</c> currently says, so the panel reads as "how far off is the authored value"
    /// and the readout prints the absolute number to copy back into the table.</para>
    ///
    /// <para>Applied to the impactor as well as the target. They are usually different materials, so
    /// applying it to one only would silently change the ratio between them, which is exactly the
    /// thing being tuned.</para>
    /// </remarks>
    /// <summary>Applies one role's multipliers to a base material.</summary>
    private SimMaterial Tuned(in SimMaterial m, int role)
        => new(m.Name,
               m.Rho * _matMul[role, (int)MatField.Rho],
               m.C * _matMul[role, (int)MatField.C],
               m.Strain * _matMul[role, (int)MatField.Strain],
               SimMath.Max(_tune.Constants.MinChi, 1f + (m.Chi - 1f) * _matMul[role, (int)MatField.Chi]),
               m.Crush * _matMul[role, (int)MatField.Crush],
               m.CrushRate * _matMul[role, (int)MatField.Rate],
               SimMath.Min(0.95f, m.ShedLimit * _matMul[role, (int)MatField.Shed]),
               m.Dent * _matMul[role, (int)MatField.Dent]);

    private SimMaterial Crushed(in SimMaterial m) => Tuned(m, 0);

    /// <summary>The material a role currently resolves to, with its multipliers applied.</summary>
    private SimMaterial RoleMaterial(int role)
        => role == 0 ? ActiveMaterial
         : _round == Round.Pierce ? Tuned(_cfg.Material("penetrator"), 1) : ImpactorMaterial;

    /// <summary>Caption for one material slider: the multiplier, and the absolute it produces.</summary>
    private void MatCaption(int f)
    {
        if (_matLabels[f] == null) return;
        SimMaterial m = RoleMaterial(_matRole);
        float mul = _matMul[_matRole, f];
        string abs = (MatField)f switch
        {
            MatField.Rho => $"{m.Rho:F0} kg/m3",
            MatField.C => $"{m.C:F0} m/s",
            MatField.Strain => $"{m.Strain:F4}",
            MatField.Chi => $"{m.Chi:F1}",
            MatField.Crush => $"{m.Crush:E1}",
            MatField.Rate => $"{m.CrushRate:G3}",
            MatField.Shed => $"{m.ShedLimit * 100f:F0}% of area",
            _ => $"{m.Dent:F1} radii",
        };
        _matLabels[f].Text = $"{MatFieldNames[f]}   x{mul:G3}  ({m.Name}: {abs})";
    }

    /// <summary>Points the material sliders at whichever role is now selected.</summary>
    private void RefreshMatSliders()
    {
        for (int f = 0; f < _matSliders.Length; f++)
        {
            if (_matSliders[f] == null) continue;
            _matSliders[f].SetValueNoSignal(MathF.Log2(_matMul[_matRole, f]));
            MatCaption(f);
        }
    }

    /// <summary>The material the current scenario actually built with — scenarios 4 and 5 force
    /// their own, and a readout that showed the selected one instead would be lying.</summary>
    private SimMaterial ImpactorMaterial => Tuned(Materials[_impactorMaterialIndex], 1);

    private SimMaterial ActiveMaterial => _scenario switch
    {
        3 => Crushed(_cfg.Material("glass")),
        4 => Crushed(_cfg.Material("steel")),
        6 => Crushed(_cfg.Material("rock")),            // the core; the shell is steel
        _ => Crushed(Materials[_materialIndex]),
    };

    /// <summary>The shell scenario's shell: steel, with the body role's multipliers.</summary>
    private SimMaterial ShellMaterial => Tuned(_cfg.Material("steel"), 0);

    /// <summary>Every material the current scenario builds a body from.</summary>
    private SimMaterial[] SceneMaterials() => _scenario switch
    {
        0 or 2 or 5 or 7 => new[] { ActiveMaterial },                           // no impactor body
        6 => new[] { ShellMaterial, ActiveMaterial, ImpactorMaterial },
        _ => new[] { ActiveMaterial, ImpactorMaterial },
    };

    /// <summary>The finest grain every material in the scene may be built at — what the tests use.</summary>
    private float SceneFloorGrain() => Scenarios.FloorGrain(_tune, SceneMaterials());

    /// <summary>The grain the scene is built at: the slider's, never below the scene's floor.</summary>
    private float SceneGrain() => MathF.Max(_grain, SceneFloorGrain());

    private int MaterialIndex(string name)
    {
        for (int i = 0; i < Materials.Length; i++) if (Materials[i].Name == name) return i;
        return 0;
    }

    private void Reset()
    {
        var m = ActiveMaterial;
        float grain = SceneGrain();
        _scene = _scenario switch
        {
            0 => Scenarios.Collide(_tune, m, speed: 600f, grain: grain),
            1 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, grain,
                                      impactor: ImpactorMaterial),
            2 => Scenarios.Spin(_tune, m, grain: grain),
            3 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, grain,
                                      impactor: ImpactorMaterial),
            4 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, grain,
                                      impactor: ImpactorMaterial),
            // Shell and core are both parts of the BODY, so both take the body role's multipliers;
            // the round that hits them takes the impactor role.
            6 => Scenarios.Shell(_tune, ShellMaterial, m,
                                 _impactorSpeed, _impactorMass, grain, impactor: ImpactorMaterial),
            7 => Scenarios.Blast(Scenarios.Collide(_tune, m, 0f, grain), _tune,
                                 370f, 350f, _blastPressure, _blastRadius),
            _ => Scenarios.Field(_tune, m, _fieldCols, _fieldRows, 60f, _fieldSpacing, 60f, grain),
        };
        _needsReset = false;
        ShowValue(_grainSlider, _grain);           // the floor moves with the scene and substeps
        _scene.Solver.PhaseMark = p =>
        {
            _phaseAcc[(int)p] += _phaseSw.Elapsed.TotalMilliseconds;
            _phaseSw.Restart();
        };
        _ticks = 0;
        _shots = 0;
        _prevCells = 0;

        if (!_headless) FitView();
    }

    // ── simulation ───────────────────────────────────────────────────────────

    public override void _PhysicsProcess(double delta)
    {
        if (!_running && !_stepOnce) return;
        if (_running && ++_slowCounter < _slowMo) return;
        _slowCounter = 0;
        _stepOnce = false;

        SimState s = _scene.State;
        CapturePose(s);

        Array.Clear(_phaseAcc);
        _sw.Restart();
        _phaseSw.Restart();
        _scene.Solver.Step();
        AdvanceFuse();
        _sw.Stop();
        _msTick = Smooth(_msTick, _sw.Elapsed.TotalMilliseconds);
        for (int i = 0; i < _phaseMs.Length; i++) _phaseMs[i] = Smooth(_phaseMs[i], _phaseAcc[i]);
        _ticks++;
    }

    /// <summary>Records every live cell's world centre and orientation before the tick is stepped.</summary>
    private void CapturePose(SimState s)
    {
        int n = s.CellCount;
        if (_prevCx.Length < n)
        {
            int cap = System.Math.Max(256, n * 2);
            _prevCx = new float[cap]; _prevCy = new float[cap]; _prevRot = new float[cap];
            _prevLive = new bool[cap];
        }
        Array.Clear(_prevLive, 0, n);
        for (int b = 0; b < s.BodyCount; b++)
        {
            float rot = s.BodyRot[b], si = MathF.Sin(rot), co = MathF.Cos(rot);
            int off = s.BodyCellOff[b], len = s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = s.BodyCells[off + i];
                if (s.Dead(c)) continue;
                _prevCx[c] = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
                _prevCy[c] = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;
                _prevRot[c] = rot;
                _prevLive[c] = true;
            }
        }
        _prevCells = n;
    }

    /// <summary>
    /// The pose every live cell is drawn at this frame: its current world centre and orientation, or
    /// the blend from where it was at the start of the tick when <paramref name="alpha"/> is below 1.
    /// A cell that did not exist then (a round just fired) is drawn where it is.
    /// </summary>
    private void ComputeDrawPoses(SimState s, float alpha)
    {
        int n = s.CellCount;
        if (_drawCx.Length < n)
        {
            int cap = System.Math.Max(256, n * 2);
            _drawCx = new float[cap]; _drawCy = new float[cap]; _drawSi = new float[cap]; _drawCo = new float[cap];
        }
        float ia = 1f - alpha;
        for (int b = 0; b < s.BodyCount; b++)
        {
            float rot = s.BodyRot[b], si = MathF.Sin(rot), co = MathF.Cos(rot);
            int off = s.BodyCellOff[b], len = s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = s.BodyCells[off + i];
                if (s.Dead(c)) continue;
                float cx = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
                float cy = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;
                if (alpha < 1f && c < _prevCells && _prevLive[c])
                {
                    // Shortest-arc blend, so a cell crossing the +/-pi seam does not spin backwards.
                    float d = rot - _prevRot[c];
                    while (d > MathF.PI) d -= MathF.Tau;
                    while (d < -MathF.PI) d += MathF.Tau;
                    float r = _prevRot[c] + d * alpha;
                    _drawCx[c] = _prevCx[c] * ia + cx * alpha;
                    _drawCy[c] = _prevCy[c] * ia + cy * alpha;
                    _drawSi[c] = MathF.Sin(r); _drawCo[c] = MathF.Cos(r);
                }
                else
                {
                    _drawCx[c] = cx; _drawCy[c] = cy; _drawSi[c] = si; _drawCo[c] = co;
                }
            }
        }
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_needsReset && !Input.IsMouseButtonPressed(MouseButton.Left)) Reset();

        // In screenshot mode the framing is held every frame. This environment delivers spurious
        // wheel events to a window that has no real cursor over it, which walked the zoom to its
        // clamp and put the scene off-screen — a property of the capture rig, not of the viewer,
        // but it made the pictures useless for checking anything.
        if (_shotPath != null) FitView();

        // Progress through the interval between two ticks. In slow motion a tick is stepped only every
        // _slowMo physics frames, so the interval spans all of them: interpolating per physics frame
        // instead replays the same prev->current blend _slowMo times, and the bodies snap back at
        // the start of each one.
        float alpha = _running
            ? MathF.Min(1f, (_slowCounter + (float)Engine.GetPhysicsInterpolationFraction()) / _slowMo)
            : 1f;

        _sw.Restart();
        BuildGeometry(alpha);
        _sw.Stop();
        _msBuild = Smooth(_msBuild, _sw.Elapsed.TotalMilliseconds);

        _sw.Restart();
        Submit();
        if (_showIds || _idsOnScreen) QueueRedraw();      // one more redraw clears ids switched off
        _sw.Stop();
        _msSubmit = Smooth(_msSubmit, _sw.Elapsed.TotalMilliseconds);

        UpdateHud();

        if (_headless && _ticks >= _headlessTicks)
        {
            GD.Print(_hud.Text);
            GetTree().Quit();
        }
        else if (_shotPath != null && !_shotPending && _ticks >= _headlessTicks)
        {
            _shotPending = true;
            RenderingServer.Singleton.FramePostDraw += SaveShot;
        }
    }

    private void BuildGeometry(float alpha)
    {
        SimState s = _scene.State;
        ComputeDrawPoses(s, alpha);

        int maxLen = System.Math.Max(8, _scene.Solver.MaxPolyLen);
        EnsureFillCapacity(s.CellCount * maxLen, s.CellCount * (maxLen - 2) * 3);
        EnsureLineCapacity((s.BondCount + s.CellCount * maxLen) * 2);

        _ptCount = 0; _idxCount = 0; _lineCount = 0; _boldCount = 0;

        var outlineColor = new Color(0f, 0f, 0f, 0.45f);

        for (int b = 0; b < s.BodyCount; b++)
        {
            Color bodyFill = _fillView == FillView.Body ? BodyColor(b) : FlatFill;

            int off = s.BodyCellOff[b], len = s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = s.BodyCells[off + i];
                if (s.Dead(c)) continue;

                int n = s.PolyLen[c];
                if (n < 3) continue;
                int poff = s.PolyOff[c];
                float ox = _drawCx[c], oy = _drawCy[c], si = _drawSi[c], co = _drawCo[c];

                Color fill = _fillView switch
                {
                    FillView.Crush => CrushColour(s, c),
                    FillView.Surface => FlatFill,
                    _ => bodyFill,
                };

                int baseVert = _ptCount;
                for (int v = 0; v < n; v++)
                {
                    // Cell-local, centroid-relative: the cell's own frame is its body's orientation.
                    float lx = s.PolyX[poff + v], ly = s.PolyY[poff + v];
                    _pts[_ptCount] = new Vector2(ox + lx * co - ly * si, oy + lx * si + ly * co);
                    _cols[_ptCount] = fill;
                    _ptCount++;
                }

                // Fan-triangulate. Voronoi cells are convex, so a fan from vertex 0 is valid.
                if (_drawFills)
                    for (int v = 1; v < n - 1; v++)
                    {
                        _idx[_idxCount++] = baseVert;
                        _idx[_idxCount++] = baseVert + v;
                        _idx[_idxCount++] = baseVert + v + 1;
                    }

                if (_drawOutlines)
                {
                    // In the surface view each edge is drawn for what it IS: a side facing open
                    // space, or one shared with a live cell of the same body. That distinction is
                    // what decides where a crack may separate and how far carving may cut, so being
                    // able to see it directly is the point of the view.
                    for (int v = 0; v < n; v++)
                    {
                        int w = v + 1 == n ? 0 : v + 1;
                        Vector2 p0 = _pts[baseVert + v], p1 = _pts[baseVert + w];

                        if (_fillView != FillView.Surface)
                        {
                            AddSegment(p0, p1, outlineColor);
                            continue;
                        }

                        // Sides, drawn for what they ARE — classified from the touch records, so
                        // the picture is the same function the rules read. A side can be PARTLY
                        // covered: erosion on the far cell leaves this one's side facing material
                        // over only part of its length, and the rest has become real surface. Both
                        // parts are drawn, which is the thing a per-side flag could never show.
                        var kind = _scene.Solver.ClassifySide(c, v, out float cf0, out float cf1);
                        if (kind == Solver.SideKind.RealSurface)
                        {
                            AddBoldSegment(p0, p1, RealSurface);
                            continue;
                        }

                        Vector2 m0 = p0.Lerp(p1, cf0), m1 = p0.Lerp(p1, cf1);
                        if (cf0 > 1e-3f) AddBoldSegment(p0, m0, RealSurface);
                        if (cf1 < 1f - 1e-3f) AddBoldSegment(m1, p1, RealSurface);

                        Color cc = kind switch
                        {
                            Solver.SideKind.Crack => CrackSurface,
                            Solver.SideKind.Sealed => SealedSide,
                            _ => InteriorSide,
                        };
                        AddSegment(m0, m1, cc);
                    }
                }
            }
        }

        if (_bondView != BondView.Off) BuildBondLines(s);
    }

    /// <summary>
    /// Bonds, coloured the way the prototype colours them: yellow through red with damage, green
    /// where the bond has flowed plastically. This is the readout that says whether cracks are
    /// forming as connected fronts reaching a surface, or scattering through the interior.
    /// </summary>
    private void BuildBondLines(SimState s)
    {
        for (int k = 0; k < s.BondCount; k++)
        {
            if (s.BondBroken[k]) continue;
            int a = s.BondA[k], b2 = s.BondB[k];
            if (s.Dead(a) || s.Dead(b2)) continue;
            int body = s.CellBody[a];
            if (body != s.CellBody[b2] || body < 0 || body >= s.BodyCount) continue;

            var pa = new Vector2(_drawCx[a], _drawCy[a]);
            var pb = new Vector2(_drawCx[b2], _drawCy[b2]);

            Color col = _bondView == BondView.Damage
                ? DamageColour(s.BondDmg[k])
                : StressColour(s, k);

            AddSegment(pa, pb, col);
        }
    }

    /// <summary>
    /// Appends one line segment. <c>CanvasItemAddMultiline</c> takes one colour per SEGMENT, not per
    /// point, so the colour array is half the length of the point array.
    /// </summary>
    /// <summary>Yellow through red as damage accumulates — the prototype's crack colouring.</summary>
    private static Color DamageColour(float d) => Hsl(60f * (1f - d) / 360f, 0.90f, 0.55f, 0.35f + 0.65f * d);

    /// <summary>
    /// Blue through red with the load a bond is currently carrying, as a fraction of the stretch at
    /// which it starts to soften.
    /// </summary>
    /// <remarks>
    /// A different question from damage, which is a history maximum and never falls. Stress is the
    /// present moment, so this is the view that shows a wave crossing a body and where the load
    /// actually flows — and it goes back to blue behind the wave, which damage never does.
    /// </remarks>
    private static Color StressColour(SimState s, int k)
    {
        float lambda = SimMath.Hypot(s.BondSn[k], s.BondSt[k])
                     + SimMath.Abs(s.BondSa[k]) * s.BondLen[k] * 0.5f;
        float t = SimMath.Min(1f, lambda / SimMath.Max(1e-6f, s.BondS0[k]));
        return Hsl((240f - 240f * t) / 360f, 0.85f, 0.55f, 0.25f + 0.75f * t);
    }

    /// <summary>
    /// How close a cell is to being comminuted: accumulated contact stress over time, as a fraction
    /// of the dose that destroys it.
    /// </summary>
    /// <remarks>
    /// The counterpart to the bond views. Damage and stress are about the bond network pulling
    /// itself apart; this is about material being crushed at a contact, which is a different failure
    /// mode with a different trigger. Watching it is the only way to see the crushed zone form and
    /// stop, which is the behaviour the pressure gate exists to produce.
    /// </remarks>
    private Color CrushColour(SimState s, int c)
    {
        // Shed fraction: how much of its original area this cell has lost, against the limit at
        // which it comminutes. This is the number to tune against — a scene that never carves and
        // one that carves constantly both show zero crushed cells, at opposite extremes.
        float a0 = s.CellArea0[c];
        if (a0 <= 0f) return FlatFill;
        float shed = 1f - s.CellArea[c] / a0;
        float lim = SimMath.Max(0.01f, s.Mat(c).ShedLimit);
        float t = SimMath.Min(1f, SimMath.Max(0f, shed) / lim);
        if (t <= 0f) return FlatFill;
        return Hsl((40f - 40f * t) / 360f, 0.85f, 0.25f + 0.35f * t, 1f);
    }

    private void AddSegment(Vector2 a, Vector2 b, Color colour)
    {
        if (_lineCount + 2 > _linePts.Length) return;
        _lineCols[_lineCount >> 1] = colour;
        _linePts[_lineCount++] = a;
        _linePts[_lineCount++] = b;
    }

    private void AddBoldSegment(Vector2 a, Vector2 b, Color colour)
    {
        if (_boldPts.Length < _linePts.Length)
        {
            _boldPts = new Vector2[System.Math.Max(64, _linePts.Length)];
            _boldCols = new Color[_boldPts.Length >> 1];
        }
        if (_boldCount + 2 > _boldPts.Length) return;
        _boldCols[_boldCount >> 1] = colour;
        _boldPts[_boldCount++] = a;
        _boldPts[_boldCount++] = b;
    }

    private void Submit()
    {
        var xform = new Transform2D(0f, new Vector2(_zoom, _zoom), 0f, _pan);
        RenderingServer.CanvasItemSetTransform(_fills, xform);
        RenderingServer.CanvasItemSetTransform(_lines, xform);
        RenderingServer.CanvasItemSetTransform(_marks, xform);

        RenderingServer.CanvasItemClear(_fills);
        RenderingServer.CanvasItemClear(_lines);
        RenderingServer.CanvasItemClear(_marks);

        if (_idxCount > 0)
            RenderingServer.CanvasItemAddTriangleArray(
                _fills,
                _idx.AsSpan(0, _idxCount),
                _pts.AsSpan(0, _ptCount),
                _cols.AsSpan(0, _ptCount),
                ReadOnlySpan<Vector2>.Empty,
                ReadOnlySpan<int>.Empty,
                ReadOnlySpan<float>.Empty,
                default,
                -1);

        if (_lineCount > 0)
            RenderingServer.CanvasItemAddMultiline(
                _lines,
                _linePts.AsSpan(0, _lineCount),
                _lineCols.AsSpan(0, _lineCount >> 1),
                -1f);

        if (_boldCount > 0)
            RenderingServer.CanvasItemAddMultiline(
                _lines,
                _boldPts.AsSpan(0, _boldCount),
                _boldCols.AsSpan(0, _boldCount >> 1),
                2.5f);

        if (_fillView == FillView.Surface) BuildVertexMarks(_scene.State);
    }

    /// <summary>
    /// What carving v2 can move, drawn over the surface. Every polygon vertex is marked for what it
    /// is to the driver, through the same classification the driver uses: a filled amber dot is an
    /// EXPOSED RECORD END (slides along its interface), a cyan ring is a CORNER (moves toward the
    /// centroid), a small grey ring is a triangle's corner (cannot recede); interior vertices are
    /// not marked. A loaded vertex with no mark is one the driver will not move — which is the thing
    /// worth being able to see. Sizes are in screen pixels, so zooming in does not shrink them, and
    /// each mark sits on a dark disc so it reads over any side colour.
    /// </summary>
    private void BuildVertexMarks(SimState s)
    {
        var exposed = new Color(1f, 0.78f, 0.2f, 1f);
        var corner = new Color(0.3f, 0.92f, 1f, 1f);
        var fixedCorner = new Color(0.65f, 0.65f, 0.65f, 0.9f);
        var backing = new Color(0.02f, 0.03f, 0.05f, 0.85f);
        float px = 1f / SimMath.Max(1e-3f, _zoom);              // one screen pixel, in world units
        int marks = 0;

        for (int c = 0; c < s.CellCount && marks < 6000; c++)
        {
            if (s.Dead(c)) continue;
            int b = s.CellBody[c];
            if (b < 0 || b >= s.BodyCount || c >= _drawCx.Length) continue;
            float cx = _drawCx[c], cy = _drawCy[c], si = _drawSi[c], co = _drawCo[c];
            int off = s.PolyOff[c], len = s.PolyLen[c];
            for (int v = 0; v < len; v++)
            {
                var kind = _scene.Solver.ClassifyVertex(c, v);
                if (kind == Solver.VertexKind.Interior) continue;
                float lx = s.PolyX[off + v], ly = s.PolyY[off + v];
                var p = new Vector2(cx + lx * co - ly * si, cy + lx * si + ly * co);
                switch (kind)
                {
                    case Solver.VertexKind.ExposedEnd:
                        RenderingServer.CanvasItemAddCircle(_marks, p, 5.0f * px, backing);
                        RenderingServer.CanvasItemAddCircle(_marks, p, 3.4f * px, exposed);
                        break;
                    case Solver.VertexKind.Corner:
                        RenderingServer.CanvasItemAddCircle(_marks, p, 5.4f * px, backing);
                        RenderingServer.CanvasItemAddCircle(_marks, p, 4.0f * px, corner);
                        RenderingServer.CanvasItemAddCircle(_marks, p, 2.2f * px, backing);
                        break;
                    default:
                        RenderingServer.CanvasItemAddCircle(_marks, p, 3.6f * px, backing);
                        RenderingServer.CanvasItemAddCircle(_marks, p, 2.6f * px, fixedCorner);
                        RenderingServer.CanvasItemAddCircle(_marks, p, 1.4f * px, backing);
                        break;
                }
                marks++;
            }
        }
    }

    /// <summary>Cell indices, so a number in a diagnostic can be found on screen.</summary>
    public override void _Draw()
    {
        // Returning without drawing is what clears the ids: _Process queues this one extra redraw
        // after they are switched off, or the last frame's labels stay on screen.
        _idsOnScreen = false;
        if (!_showIds) return;
        SimState s = _scene.State;
        int w = GetViewportRect().Size.X > 0 ? (int)GetViewportRect().Size.X : 1920;
        int h = GetViewportRect().Size.Y > 0 ? (int)GetViewportRect().Size.Y : 1080;
        var font = ThemeDB.FallbackFont;
        if (font == null) return;

        int drawn = 0;
        for (int c = 0; c < s.CellCount && drawn < 900; c++)
        {
            if (s.Dead(c) || c >= _drawCx.Length) continue;
            int b = s.CellBody[c];
            if (b < 0 || b >= s.BodyCount) continue;

            // At the drawn pose, so the label stays on its cell between ticks.
            var p = new Vector2(_drawCx[c] * _zoom + _pan.X, _drawCy[c] * _zoom + _pan.Y);
            if (p.X < -40f || p.Y < -40f || p.X > w + 40f || p.Y > h + 40f) continue;

            DrawString(font, p, c.ToString(), HorizontalAlignment.Center,
                       -1f, 11, new Color(1f, 1f, 1f, 0.75f));
            drawn++;
        }
        _idsOnScreen = drawn > 0;
    }

    private void EnsureFillCapacity(int verts, int indices)
    {
        if (_pts.Length < verts) { _pts = new Vector2[verts * 2]; _cols = new Color[verts * 2]; }
        if (_idx.Length < indices) _idx = new int[indices * 2];
    }

    private void EnsureLineCapacity(int points)
    {
        if (_linePts.Length >= points) return;
        _linePts = new Vector2[points * 2];
        _lineCols = new Color[points];        // one per segment
    }

    // ── colour ───────────────────────────────────────────────────────────────

    /// <summary>The prototype's body hue: <c>hsl((body*67)%360 45% 55%)</c>.</summary>
    private static Color BodyColor(int body) => Hsl((body * 67 % 360) / 360f, 0.45f, 0.55f, 1f);

    /// <summary>
    /// CSS HSL, which is not Godot's HSV. Written out rather than approximated because the whole
    /// point of these colours is that a frame here can be compared with a frame of the prototype.
    /// </summary>
    private static Color Hsl(float h, float s, float l, float a)
    {
        float c = (1f - MathF.Abs(2f * l - 1f)) * s;
        float hp = h * 6f;
        float x = c * (1f - MathF.Abs(hp % 2f - 1f));
        float r = 0f, g = 0f, b = 0f;
        switch ((int)hp)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        float m = l - c * 0.5f;
        return new Color(r + m, g + m, b + m, a);
    }

    // ── input ────────────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.Space: _running = !_running; break;
                case Key.Period: _stepOnce = true; _running = false; break;
                case Key.R: Reset(); break;
                case Key.L: ReloadConfig(); break;
                case Key.Z: FitView(); break;
                case Key.Bracketleft: Nudge(_slowSlider, +1); break;
                case Key.Bracketright: Nudge(_slowSlider, -1); break;
                case Key.O: Toggle(_outlineBox, ref _drawOutlines); break;
                case Key.I: Toggle(_idBox, ref _showIds); break;
                case Key.F: Toggle(_fillBox, ref _drawFills); break;
                case Key.C: CycleFillView(); break;
                case Key.B: CycleBondView(); break;
                case Key.M: Pick(_materialPick, ref _materialIndex, Materials.Length); break;
                case Key.N: Pick(_impactorPick, ref _impactorMaterialIndex, Materials.Length); break;
                case Key.Key1: case Key.Key2: case Key.Key3: case Key.Key4:
                case Key.Key5: case Key.Key6: case Key.Key7: case Key.Key8:
                    Select(_scenarioPick, ref _scenario, (int)(key.Keycode - Key.Key1));
                    break;
                case Key.Minus: Nudge(_strainSlider, -1); break;
                case Key.Equal: Nudge(_strainSlider, +1); break;
                case Key.Comma: Nudge(_toughSlider, -1); break;
                case Key.Slash: Nudge(_toughSlider, +1); break;
                case Key.Semicolon: Nudge(_substepSlider, -1); break;
                case Key.Apostrophe: Nudge(_substepSlider, +1); break;
                case Key.G: Nudge(_grainSlider, -4); break;
                case Key.H: Nudge(_grainSlider, +4); break;
                case Key.Escape: GetTree().Quit(); break;
            }
            return;
        }

        if (ev is InputEventMouseButton mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Right: _panning = mb.Pressed; break;
                case MouseButton.WheelUp when mb.Pressed: ZoomAt(mb.Position, 1.1f); break;
                case MouseButton.WheelDown when mb.Pressed: ZoomAt(mb.Position, 1f / 1.1f); break;
                case MouseButton.Left when mb.Pressed: FireAt(ToWorld(mb.Position)); break;
            }
            return;
        }

        if (ev is InputEventMouseMotion mm && _panning) _pan += mm.Relative;
    }

    // The shortcuts move the controls; the controls' signals do the work. Without that the panel and
    // the simulation drift apart and the readout starts lying.
    private static void Nudge(HSlider? slider, int steps)
    {
        if (slider != null) slider.Value += steps * slider.Step;
    }

    private void CycleFillView()
    {
        var next = (FillView)(((int)_fillView + 1) % 3);
        if (_fillPick != null) _fillPick.Selected = (int)next;
        _fillView = next;
    }

    private void CycleBondView()
    {
        var next = (BondView)(((int)_bondView + 1) % 3);
        if (_bondPick != null) { _bondPick.Selected = (int)next; _bondView = next; }
        else _bondView = next;
    }

    private static void Toggle(CheckBox? box, ref bool fallback)
    {
        if (box != null) box.ButtonPressed = !box.ButtonPressed;
        else fallback = !fallback;
    }

    private void Pick(OptionButton? pick, ref int index, int count)
    {
        int next = (index + 1) % count;
        if (pick != null) { pick.Selected = next; pick.EmitSignal(OptionButton.SignalName.ItemSelected, next); }
        else { index = next; Reset(); }
    }

    private void Select(OptionButton? pick, ref int index, int value)
    {
        if (pick != null) { pick.Selected = value; pick.EmitSignal(OptionButton.SignalName.ItemSelected, value); }
        else { index = value; Reset(); }
    }

    /// <summary>
    /// Frames every live cell in the area left of the control panel.
    /// </summary>
    /// <remarks>
    /// Called on every reset, because the knobs change what there is to look at: a grain change
    /// alters cell size tenfold and a scenario change moves the scene entirely. Without this,
    /// switching scenario leaves you looking at empty space and wondering whether the model broke.
    /// </remarks>
    private void FitView()
    {
        // Cell positions come from the body pose. The solver's CellPx/CellPy are scratch it fills
        // during a step, so on a freshly built scene they are all zero and the fit framed the origin.
        SimState s = _scene.State;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int b = 0; b < s.BodyCount; b++)
        {
            float si = MathF.Sin(s.BodyRot[b]), co = MathF.Cos(s.BodyRot[b]);
            int off = s.BodyCellOff[b], len = s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = s.BodyCells[off + i];
                if (s.Dead(c)) continue;
                float x = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
                float y = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;
                float r = s.CellRad[c];
                minX = MathF.Min(minX, x - r); maxX = MathF.Max(maxX, x + r);
                minY = MathF.Min(minY, y - r); maxY = MathF.Max(maxY, y + r);
            }
        }
        if (minX > maxX) return;

        Vector2 view = GetViewportRect().Size;
        float usableW = MathF.Max(200f, view.X - PanelWidth);
        float usableH = MathF.Max(200f, view.Y - 40f);

        float w = MathF.Max(1f, maxX - minX), h = MathF.Max(1f, maxY - minY);
        _zoom = System.Math.Clamp(MathF.Min(usableW / w, usableH / h) * 0.9f, 0.05f, 8f);

        // Centre the content in the usable area, which is offset from the window by the panel.
        _pan = new Vector2(usableW * 0.5f - (minX + maxX) * 0.5f * _zoom,
                           view.Y * 0.5f - (minY + maxY) * 0.5f * _zoom);
    }

    private const float PanelWidth = 320f;

    private Vector2 ToWorld(Vector2 screen) => (screen - _pan) / _zoom;

    private void ZoomAt(Vector2 screen, float factor)
    {
        Vector2 before = ToWorld(screen);
        _zoom = System.Math.Clamp(_zoom * factor, 0.05f, 8f);
        _pan += screen - (before * _zoom + _pan);
    }

    /// <summary>
    /// Fires a projectile from off-screen left, through the clicked point.
    /// </summary>
    /// <remarks>
    /// The shot is seeded from the shot counter, so a session is reproducible: click the same places
    /// in the same order from a fresh scene and the same thing happens.
    /// </remarks>
    private void FireAt(Vector2 world)
    {
        _shots++;

        // A blast needs no round at all: the front arrives where you clicked.
        if (_round == Round.Blast)
        {
            _scene = Scenarios.Blast(_scene, _tune, world.X, world.Y, _blastPressure, _blastRadius);
            return;
        }

        float fromX = world.X - 600f;
        float fromY = world.Y;
        // Radius from mass the same way Scenarios.Projectile derives it, so the slider means the
        // same thing whether the impactor is built into the scene or fired into it.
        // SIZE AND MASS ARE SEPARATE. The mass slider still means what it always did — the round
        // weighs what a 15*sqrt(mass) blob of this material weighs — but the round is now BUILT at
        // whatever size is asked for, and its density is solved to carry that mass. A small, dense
        // round is the result. Measured on a rock target: holding mass while shrinking kept the
        // damage and then raised it (0.7% of the target at radius 26, 0.9% at 8.7, 2.9% at 4.3),
        // where shrinking without it collapsed to 0.2%.
        float massRadius = 15f * MathF.Sqrt(_impactorMass);
        float radius = _impactorSize > 0f ? _impactorSize : massRadius;

        // The rod is the same mass redistributed: a small face, and a material that resists its own
        // comminution, so it stays a rod instead of mushrooming into a wide crater.
        SimMaterial round = _round == Round.Pierce ? Tuned(_cfg.Material("penetrator"), 1) : ImpactorMaterial;
        float rx = _round == Round.Pierce ? radius * 3.5f : radius;
        float ry = _round == Round.Pierce ? radius / 3.5f : radius;
        float mrx = _round == Round.Pierce ? massRadius * 3.5f : 0f;
        float mry = _round == Round.Pierce ? massRadius / 3.5f : 0f;
        float wantMass = Scenarios.RoundMass(round, massRadius, mrx, mry, _shots);

        _scene = Scenarios.FireAt(_scene, _tune, round,
            fromX, fromY, world.X, world.Y,
            speed: _impactorSpeed, radius: radius, grain: _grain, seed: _shots,
            radiusX: rx, radiusY: ry, roundMass: wantMass);

        // An explosive round is the bullet path plus a front, a few ticks later, wherever it got to.
        // Scheduled here rather than inside Scenarios, so Blast stays a primitive with no fuse in it.
        if (_round == Round.Explosive)
        {
            _fuseBody = _scene.State.BodyCount - 1;
            _pendingFuse = _fuseTicks;
        }
    }

    /// <summary>Sets an explosive round off where it actually strikes something.</summary>
    /// <remarks>
    /// A fixed countdown from the spawn detonated in mid-air whenever the flight took longer than
    /// the fuse, and inside the target whenever it took less — the blast landed wherever the timer
    /// happened to expire, which is why it read as arbitrary. The round now goes off on its FIRST
    /// contact, which the solver reports directly. The fuse slider stays as a flight-time limit, so
    /// a round that misses everything still detonates instead of sailing on forever.
    /// </remarks>
    private void AdvanceFuse()
    {
        if (_pendingFuse < 0) return;
        SimState s = _scene.State;
        if (_fuseBody >= 0 && _fuseBody < s.BodyCount) { _fuseBodyX = s.BodyX[_fuseBody]; _fuseBodyY = s.BodyY[_fuseBody]; }
        bool struck = _fuseBody >= 0 && _fuseBody < s.BodyCount && _scene.Solver.BodyTouchedThisTick(_fuseBody);
        if (!struck && --_pendingFuse > 0) return;
        _pendingFuse = -1;
        _scene = Scenarios.Blast(_scene, _tune, _fuseBodyX, _fuseBodyY, _blastPressure, _blastRadius);
    }

    // ── readout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the tick actually goes, largest first. Every stage but the broadphase, the split and
    /// the settle runs once per SUBSTEP, so these are per-tick totals — which is the number that
    /// matters, and the reason the substep count dominates everything.
    /// </summary>
    private string PhaseBreakdown()
    {
        Span<int> order = stackalloc int[_phaseMs.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        for (int i = 1; i < order.Length; i++)         // insertion sort, descending
            for (int j = i; j > 0 && _phaseMs[order[j]] > _phaseMs[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);

        double total = 0;
        foreach (double v in _phaseMs) total += v;
        if (total <= 0) return "";

        var sb = new System.Text.StringBuilder("\nstage breakdown (per tick)\n");
        for (int i = 0; i < order.Length; i++)
        {
            int p = order[i];
            if (_phaseMs[p] < 0.005) continue;
            int bar = (int)MathF.Round((float)(_phaseMs[p] / total) * 28f);
            sb.Append($"  {PhaseNames[p],-16} {_phaseMs[p],6:F2} ms {100 * _phaseMs[p] / total,5:F1}%  ")
              .Append('#', bar).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The CFL number: how far a stress wave travels in one substep, as a fraction of a cell.
    /// </summary>
    /// <remarks>
    /// The single most useful number on this readout, and the one whose absence hid a real bug. The
    /// bond passes are an explicit integrator, so they are stable only while a wave crosses less than
    /// about a quarter of a cell per substep. Substeps are a fixed constant while cell size is a
    /// slider, so shrinking the grain walks this number up until the deviation field diverges — and
    /// the damage model then faithfully reports numerical divergence as fracture. A spinning body at
    /// grain 30 shatters into 349 pieces at 9 substeps and holds together perfectly at 16.
    /// </remarks>
    private string CflLine()
    {
        SimState s = _scene.State;
        float worst = 0f;
        int worstBody = -1;
        for (int b = 0; b < s.BodyCount; b++)
        {
            float cell = s.BodyCellSize[b];
            if (cell <= 0f) continue;
            float cfl = s.BodyCpx[b] * (Solver.Dt / _tune.Substeps) / cell;
            if (cfl > worst) { worst = cfl; worstBody = b; }
        }
        if (worstBody < 0) return "";

        string verdict = worst < 0.25f ? "converged"
                       : worst < 0.5f ? "marginal — impacts will over-fracture"
                       : "UNSTABLE — fracture here is numerical, not physical";
        int need = (int)MathF.Ceiling(worst / 0.25f * _tune.Substeps);
        string advice = worst < 0.25f ? "" : $"  (needs ~{need} substeps)";
        return $"\nCFL {worst:F2} of 0.25 safe — {verdict}{advice}\n";
    }

    /// <summary>
    /// What comminution is doing right now, and how close the scene is to doing it.
    /// </summary>
    /// <remarks>
    /// <para>The peak dose fraction is the number to tune against: it says how near the worst-loaded
    /// cell came to its capacity without necessarily reaching it. A scenario that never crushes and
    /// one that crushes constantly both read zero crushed cells at opposite extremes, and only this
    /// separates them.</para>
    /// <para>The ledger share is the honesty check. Momentum booked there has left the physics
    /// rather than reaching whatever was pressing on the cell, so a large number here means material
    /// is vanishing out of a collision — which reads as clean conservation, because the drift figure
    /// counts the ledger.</para>
    /// </remarks>
    private string CrushLine(SimState s, Solver solver)
    {
        float worst = 0f;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int bi = s.CellBody[c];
            if (bi < 0 || bi >= s.BodyCount || s.CellArea0[c] <= 0f) continue;
            float lim = SimMath.Max(0.01f, s.Mat(c).ShedLimit);
            float f = (1f - s.CellArea[c] / s.CellArea0[c]) / lim;
            if (f > worst) worst = f;
        }

        solver.TotalMomentum(out float px, out float py);
        float live = SimMath.Hypot(px, py);
        float led = SimMath.Hypot(solver.ExportedPx, solver.ExportedPy);
        float tot = live + led;

        return $"crush thr {ActiveMaterial.Crush:E1}"
             + $"   erosion rate {ActiveMaterial.CrushRate:G3}"
             + $"   shed limit {ActiveMaterial.ShedLimit * 100f:F0}%"
             + $"   peak shed {worst * 100f,5:F1}% of limit"
             + $"   carved {solver.ShedArea:F2} cells"
             + $"   ledger {(tot < 1f ? 0f : 100f * led / tot),5:F1}%\n";
    }

    private void UpdateHud()
    {
        SimState s = _scene.State;
        Solver solver = _scene.Solver;

        int live = 0;
        for (int c = 0; c < s.CellCount; c++) if (!s.Dead(c)) live++;

        double frame = _msTick + _msBuild + _msSubmit;

        _hud.Text =
            (_configError != null ? $"sim.json NOT reloaded: {_configError}\n" : "")
          + $"[{_scenario + 1}] {ScenarioNames[_scenario]}   body {ActiveMaterial.Name} · impactor {ImpactorMaterial.Name}"
          + $"   {(_running ? (_slowMo > 1 ? $"1/{_slowMo} speed" : "running") : "paused")}   tick {_ticks}\n"
          + $"cells {live}   bodies {s.BodyCount}   bonds {s.BondCount}   broken {solver.Broken}"
          + $"   dust {solver.Dust}   crushed {solver.Crushed}\n"
          + CrushLine(s, solver)
          + $"momentum drift {_scene.MomentumDrift() * 100f,7:F3}%   energy {_scene.EnergyFraction() * 100f,6:F1}%"
          + $"   peak overlap {solver.MaxOverlap,5:F1} px\n"
          + $"tick {_msTick,6:F2} ms   build {_msBuild,5:F2} ms   submit {_msSubmit,5:F2} ms"
          + $"   frame {frame,6:F2} ms of 16.67\n"
          + $"triangles {_idxCount / 3}   segments {_lineCount / 2}\n"
          + PhaseBreakdown()
          + CflLine()
          + $"grain {SceneGrain():F0} ({MathF.Sqrt(SceneGrain()):F1} px{(_grain <= 0 ? ", floor" : "")})  impactor {_impactorSpeed:F0} px/s x{_impactorMass:F1}"
          + $"   strain x{_tune.StrainScale:F1}  toughness x{_tune.ToughnessScale:F1}"
          + $"  substeps {_tune.Substeps}   zoom {_zoom:F2}\n"
          + $"round {RoundNames[(int)_round]}"
          + (_round == Round.Blast || _round == Round.Explosive
              ? $" (p {_blastPressure:E1}, r {_blastRadius:F0}px{(_round == Round.Explosive ? $", max flight {_fuseTicks}" : "")})" : "")
          + $"   structure: weibull {(_tune.WeibullM <= 0 ? "off" : _tune.WeibullM.ToString("F1"))}"
          + $" · aniso {(_tune.Aniso <= 0 ? "off" : _tune.Aniso.ToString("F2"))}"
          + $" · flaws {(_tune.SurfFlaw <= 0 ? "off" : _tune.SurfFlaw.ToString("F2"))}"
          + $" · grain {(_tune.GrainLock ? $"{_tune.GrainAngle * 180f / MathF.PI:F0}° locked" : "random per body")}\n"
          + $"view: fills {(_drawFills ? "on" : "off")} · {_fillView.ToString().ToLowerInvariant()}"
          + $" · ids {(_showIds ? "on" : "off")} · outlines {(_drawOutlines ? "on" : "off")} · bonds {_bondView.ToString().ToLowerInvariant()}\n"
          + "\n1-8 scenario · M/N body,impactor material · R reset · L reload sim.json · space pause · . step · [ ] slow-mo\n"
          + "F fills · C fill view · O outlines · I cell ids · B bond view · G/H grain\n"
          + "-/= strain · ,/ toughness · ;/' substeps\n"
          + "left-click fires · right-drag pans · wheel zooms · Z refits · esc quits";
    }
}
