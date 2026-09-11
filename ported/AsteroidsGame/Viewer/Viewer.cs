using System;
using System.Diagnostics;
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
/// screen so that can be judged by eye, against
/// <c>prototypes/stress-fracture-v9.html</c>, whose colour scheme it deliberately copies so the two
/// can be compared side by side.</para>
///
/// <para><b>Not the game.</b> <c>Sim/SimRoot.cs</c> is the Phase-1 game host; this is a tool. They
/// are kept apart on purpose.</para>
///
/// <para><b>Geometry path.</b> The batched <c>RenderingServer</c> route Spike A validated: cell fills
/// as one triangle array, bonds and outlines as one multiline, buffers allocated once and reused.
/// Cell polygons come back in body-local space and the body transform is applied here, which is what
/// makes interpolation affordable — one rotation per body per frame rather than a re-skin.</para>
/// </remarks>
public partial class Viewer : Node2D
{
    // ── scenarios ────────────────────────────────────────────────────────────

    private static readonly string[] ScenarioNames =
    {
        "collide", "projectile", "spin", "glass projectile", "steel projectile", "field",
    };

    private static readonly SimMaterial[] Materials =
    {
        SimMaterial.Rock, SimMaterial.Ice, SimMaterial.Glass, SimMaterial.Sandstone, SimMaterial.Steel,
    };

    private int _scenario;
    private int _materialIndex;

    /// <summary>
    /// The impactor's material, independent of the target's. They were the same knob, which quietly
    /// made half the interesting questions unaskable: a steel slug into rock and a rock into steel
    /// are different experiments, and neither is a body colliding with itself.
    /// </summary>
    private int _impactorMaterialIndex = 4;   // steel
    private SimTuning _tune = SimTuning.Default;

    // Comminution multipliers on the selected materials' authored pair. See Crushed().
    private float _crushThrMul = 1f;
    private float _crushCapMul = 1f;
    private Scenarios.Result _scene;

    // Build-time knobs. Grain is cell AREA, so cell size is its square root: 30 gives 5.5 px cells,
    // 900 gives 30 px. Lowering it is the fastest way to make a body expensive — cell count scales
    // with 1/grain — which is why it belongs on a slider next to the frame timings.
    private float _grain = 900f;
    private float _impactorSpeed = 900f;
    private float _impactorMass = 3f;

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
    /// How bonds are coloured. A mode rather than a set of flags because a line carries one colour:
    /// damage and stress answer different questions about the same segment and cannot share it.
    /// </summary>
    private enum BondView { Off, Damage, Stress }

    /// <summary>How cell interiors are coloured.</summary>
    private enum FillView { Body, Flat, Crush }
    private FillView _fillView = FillView.Body;
    private BondView _bondView = BondView.Damage;

    /// <summary>The prototype's flat cell fill when body colouring is off: <c>#232a34</c>.</summary>
    private static readonly Color FlatFill = new(0.137f, 0.165f, 0.204f);

    // ── render resources ─────────────────────────────────────────────────────

    private Rid _fills;
    private Rid _lines;
    private Label _hud = null!;

    // Controls, kept as fields so the keyboard shortcuts can move them and the two stay in step.
    private OptionButton _scenarioPick = null!;
    private OptionButton _materialPick = null!;
    private HSlider _crushThrSlider = null!;
    private HSlider _crushCapSlider = null!;
    private HSlider _confineSlider = null!;
    private OptionButton _impactorPick = null!;
    private HSlider _grainSlider = null!, _speedSlider = null!, _massSlider = null!;
    private HSlider _strainSlider = null!, _toughSlider = null!, _substepSlider = null!;
    private HSlider _slowSlider = null!;
    private CheckBox _fillBox = null!, _outlineBox = null!;
    private OptionButton _fillPick = null!;
    private OptionButton _bondPick = null!;

    private Vector2[] _pts = Array.Empty<Vector2>();
    private Color[] _cols = Array.Empty<Color>();
    private int[] _idx = Array.Empty<int>();
    private int _ptCount, _idxCount;

    private Vector2[] _linePts = Array.Empty<Vector2>();
    private Color[] _lineCols = Array.Empty<Color>();
    private int _lineCount;

    private float[] _polyX = new float[64];
    private float[] _polyY = new float[64];

    // Interpolation: the pose each body had at the end of the previous tick.
    private float[] _prevX = Array.Empty<float>();
    private float[] _prevY = Array.Empty<float>();
    private float[] _prevRot = Array.Empty<float>();
    private int _prevCount;
    private bool _poseComparable;     // false when topology changed, so poses cannot be matched up

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
        "contact solve", "bond integrate", "decompose", "realize", "damage",
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
        _headless = DisplayServer.GetName() == "headless";
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

        // Step 5 rather than something coarser because grain is an AREA: the interesting end is the
        // low one, where 30 to 60 takes cells from 5.5 px to 7.7 px and multiplies the cell count by
        // two. The caption carries the square root, which is the number that is actually intuitive.
        _grainSlider = AddSlider(box, "grain", 30, 2500, 5, _grain,
            v => { _grain = (float)v; _needsReset = true; },
            v => $"{v:F0}  ({MathF.Sqrt((float)v):F0} px cells)");

        _speedSlider = AddSlider(box, "impactor speed", 100, 4000, 50, _impactorSpeed,
            v => { _impactorSpeed = (float)v; _needsReset = true; }, v => $"{v:F0} px/s");

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
        box.AddChild(new Label { Text = "comminution" });

        // Log2 multipliers: the useful range is multiplicative, and a linear slider over a 64x span
        // wastes nine tenths of its travel above x8. Captions carry the ABSOLUTE value for the
        // currently selected body material, since that is the number to copy back into Materials.cs.
        _crushThrSlider = AddSlider(box, "crush threshold", -4, 4, 1, 0,
            v => { _crushThrMul = MathF.Pow(2f, (float)v); _needsReset = true; },
            v => $"x{MathF.Pow(2f, (float)v):G3}  ({ActiveMaterial.Crush:E1})");

        // Capacity is the time constant: below roughly the wave transit time of a body the contact
        // powders before the interior is ever loaded, and the target stops fracturing altogether.
        // That transition is what this slider is for, so its range spans it.
        _crushCapSlider = AddSlider(box, "crush capacity", -5, 5, 1, 0,
            v => { _crushCapMul = MathF.Pow(2f, (float)v); _needsReset = true; },
            v => $"x{MathF.Pow(2f, (float)v):G3}  ({ActiveMaterial.CrushCap:E1})");

        // The one free constant in the pressure measure, weighting penetration strain against the
        // braking impulse. Live rather than reset-on-change: it is read every substep, so it can be
        // moved while watching a contact.
        _confineSlider = AddSlider(box, "confine weight", 0, 0.5, 0.005, _tune.CrushConfine,
            v => _tune.CrushConfine = (float)v, v => $"{v:F3}");

        box.AddChild(new HSeparator());

        _slowSlider = AddSlider(box, "slow motion", 0, 5, 1, 0,
            v => _slowMo = 1 << (int)v, v => v <= 0 ? "full speed" : $"1/{1 << (int)v}");

        box.AddChild(new Label { Text = "view" });

        _fillBox = new CheckBox { Text = "cell fills", ButtonPressed = _drawFills };
        _fillBox.Toggled += on => _drawFills = on;
        box.AddChild(_fillBox);

        _fillPick = new OptionButton();
        foreach (string n in new[] { "body colour", "flat", "crush dose" }) _fillPick.AddItem(n);
        _fillPick.Selected = (int)_fillView;
        _fillPick.ItemSelected += i => _fillView = (FillView)i;
        AddRow(box, "fill", _fillPick);

        _outlineBox = new CheckBox { Text = "cell outlines", ButtonPressed = _drawOutlines };
        _outlineBox.Toggled += on => _drawOutlines = on;
        box.AddChild(_outlineBox);

        _bondPick = new OptionButton();
        foreach (string n in new[] { "off", "damage", "stress" }) _bondPick.AddItem(n);
        _bondPick.Selected = (int)_bondView;
        _bondPick.ItemSelected += i => _bondView = (BondView)i;
        AddRow(box, "bonds", _bondPick);

        var buttons = new HBoxContainer();
        var reset = new Button { Text = "reset", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += Reset;
        buttons.AddChild(reset);

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
    private static HSlider AddSlider(VBoxContainer parent, string name,
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
        return slider;
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
                case "--grain": _grain = System.Math.Clamp(v, 30, 2500); break;
                case "--substeps": _tune.Substeps = System.Math.Clamp(v, 1, 64); break;
                case "--speed": _impactorSpeed = System.Math.Clamp(v, 100, 4000); break;
                case "--mass": _impactorMass = System.Math.Clamp(v, 1, 24); break;
                case "--outlines": _drawOutlines = v != 0; break;
                case "--fills": _drawFills = v != 0; break;
                case "--bonds": _bondView = (BondView)System.Math.Clamp(v, 0, 2); break;
                case "--fill": _fillView = (FillView)System.Math.Clamp(v, 0, 2); break;
            }
        }
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
    private SimMaterial Crushed(in SimMaterial m)
        => new(m.Name, m.Rho, m.C, m.Strain, m.Chi, m.Yield, m.Duct,
               m.Crush * _crushThrMul, m.CrushCap * _crushCapMul);

    /// <summary>The material the current scenario actually built with — scenarios 4 and 5 force
    /// their own, and a readout that showed the selected one instead would be lying.</summary>
    private SimMaterial ImpactorMaterial => Crushed(Materials[_impactorMaterialIndex]);

    private SimMaterial ActiveMaterial => _scenario switch
    {
        3 => Crushed(SimMaterial.Glass),
        4 => Crushed(SimMaterial.Steel),
        _ => Crushed(Materials[_materialIndex]),
    };

    private void Reset()
    {
        var m = ActiveMaterial;
        _scene = _scenario switch
        {
            0 => Scenarios.Collide(_tune, m, speed: 600f, grain: _grain),
            1 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, _grain,
                                      impactor: ImpactorMaterial),
            2 => Scenarios.Spin(_tune, m, grain: _grain),
            3 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, _grain,
                                      impactor: ImpactorMaterial),
            4 => Scenarios.Projectile(_tune, m, _impactorSpeed, _impactorMass, _grain,
                                      impactor: ImpactorMaterial),
            _ => Scenarios.Field(_tune, m, _fieldCols, _fieldRows, 60f, _fieldSpacing, 60f, _grain),
        };
        _needsReset = false;
        _scene.Solver.PhaseMark = p =>
        {
            _phaseAcc[(int)p] += _phaseSw.Elapsed.TotalMilliseconds;
            _phaseSw.Restart();
        };
        _ticks = 0;
        _shots = 0;
        _poseComparable = false;
        _prevCount = 0;

        int maxLen = System.Math.Max(8, _scene.Solver.MaxPolyLen);
        if (_polyX.Length < maxLen) { _polyX = new float[maxLen]; _polyY = new float[maxLen]; }

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
        int bodiesBefore = s.BodyCount;
        int brokenBefore = _scene.Solver.Broken;

        CapturePose(s);

        Array.Clear(_phaseAcc);
        _sw.Restart();
        _phaseSw.Restart();
        _scene.Solver.Step();
        _sw.Stop();
        _msTick = Smooth(_msTick, _sw.Elapsed.TotalMilliseconds);
        for (int i = 0; i < _phaseMs.Length; i++) _phaseMs[i] = Smooth(_phaseMs[i], _phaseAcc[i]);
        _ticks++;

        // A split renumbers bodies, so index b before the tick and index b after it need not be the
        // same body. Interpolating across that reads as a jump. Rather than track identity, notice
        // the topology moved and draw the current pose for one frame.
        _poseComparable = s.BodyCount == bodiesBefore && _scene.Solver.Broken == brokenBefore;
    }

    private void CapturePose(SimState s)
    {
        if (_prevX.Length < s.BodyCount)
        {
            int cap = System.Math.Max(64, s.BodyCount * 2);
            _prevX = new float[cap]; _prevY = new float[cap]; _prevRot = new float[cap];
        }
        for (int b = 0; b < s.BodyCount; b++)
        {
            _prevX[b] = s.BodyX[b]; _prevY[b] = s.BodyY[b]; _prevRot[b] = s.BodyRot[b];
        }
        _prevCount = s.BodyCount;
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

        float alpha = _running && _poseComparable
            ? (float)Engine.GetPhysicsInterpolationFraction()
            : 1f;

        _sw.Restart();
        BuildGeometry(alpha);
        _sw.Stop();
        _msBuild = Smooth(_msBuild, _sw.Elapsed.TotalMilliseconds);

        _sw.Restart();
        Submit();
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

    /// <summary>Interpolated world pose of a body, as sin/cos plus origin.</summary>
    private void Pose(SimState s, int b, float alpha,
        out float ox, out float oy, out float si, out float co)
    {
        float x = s.BodyX[b], y = s.BodyY[b], rot = s.BodyRot[b];
        if (alpha < 1f && b < _prevCount)
        {
            float ia = 1f - alpha;
            x = _prevX[b] * ia + x * alpha;
            y = _prevY[b] * ia + y * alpha;
            // Shortest-arc blend, so a body crossing the +/-pi seam does not spin backwards.
            float d = rot - _prevRot[b];
            while (d > MathF.PI) d -= MathF.Tau;
            while (d < -MathF.PI) d += MathF.Tau;
            rot = _prevRot[b] + d * alpha;
        }
        ox = x; oy = y;
        si = MathF.Sin(rot); co = MathF.Cos(rot);
    }

    private void BuildGeometry(float alpha)
    {
        SimState s = _scene.State;
        Solver solver = _scene.Solver;

        int maxLen = _polyX.Length;
        EnsureFillCapacity(s.CellCount * maxLen, s.CellCount * (maxLen - 2) * 3);
        EnsureLineCapacity((s.BondCount + s.CellCount * maxLen) * 2);

        _ptCount = 0; _idxCount = 0; _lineCount = 0;

        var outlineColor = new Color(0f, 0f, 0f, 0.45f);

        for (int b = 0; b < s.BodyCount; b++)
        {
            Pose(s, b, alpha, out float ox, out float oy, out float si, out float co);
            Color bodyFill = _fillView == FillView.Body ? BodyColor(b) : FlatFill;

            int off = s.BodyCellOff[b], len = s.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = s.BodyCells[off + i];
                if (s.CellDead[c]) continue;

                int n = solver.CellLocalPolygon(c, _polyX, _polyY);
                if (n < 3) continue;

                Color fill = _fillView == FillView.Crush ? CrushColour(s, c) : bodyFill;

                int baseVert = _ptCount;
                for (int v = 0; v < n; v++)
                {
                    float lx = _polyX[v], ly = _polyY[v];
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
                    for (int v = 0; v < n; v++)
                    {
                        int w = v + 1 == n ? 0 : v + 1;
                        AddSegment(_pts[baseVert + v], _pts[baseVert + w], outlineColor);
                    }
            }
        }

        if (_bondView != BondView.Off) BuildBondLines(s, alpha);
    }

    /// <summary>
    /// Bonds, coloured the way the prototype colours them: yellow through red with damage, green
    /// where the bond has flowed plastically. This is the readout that says whether cracks are
    /// forming as connected fronts reaching a surface, or scattering through the interior.
    /// </summary>
    private void BuildBondLines(SimState s, float alpha)
    {
        for (int k = 0; k < s.BondCount; k++)
        {
            if (s.BondBroken[k]) continue;
            int a = s.BondA[k], b2 = s.BondB[k];
            if (s.CellDead[a] || s.CellDead[b2]) continue;
            int body = s.CellBody[a];
            if (body != s.CellBody[b2] || body < 0 || body >= s.BodyCount) continue;

            Pose(s, body, alpha, out float ox, out float oy, out float si, out float co);

            float ax = s.CellRx[a], ay = s.CellRy[a];
            float bx = s.CellRx[b2], by = s.CellRy[b2];

            var pa = new Vector2(ox + ax * co - ay * si, oy + ax * si + ay * co);
            var pb = new Vector2(ox + bx * co - by * si, oy + bx * si + by * co);

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
        int bi = s.CellBody[c];
        if (bi < 0 || bi >= s.BodyCount) return FlatFill;
        float t = SimMath.Min(1f, s.CellCrush[c] / SimMath.Max(1e-6f, s.BodyCrushCap[bi]));
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

    private void Submit()
    {
        var xform = new Transform2D(0f, new Vector2(_zoom, _zoom), 0f, _pan);
        RenderingServer.CanvasItemSetTransform(_fills, xform);
        RenderingServer.CanvasItemSetTransform(_lines, xform);

        RenderingServer.CanvasItemClear(_fills);
        RenderingServer.CanvasItemClear(_lines);

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
                case Key.Z: FitView(); break;
                case Key.Bracketleft: Nudge(_slowSlider, +1); break;
                case Key.Bracketright: Nudge(_slowSlider, -1); break;
                case Key.O: Toggle(_outlineBox, ref _drawOutlines); break;
                case Key.F: Toggle(_fillBox, ref _drawFills); break;
                case Key.C: CycleFillView(); break;
                case Key.B: CycleBondView(); break;
                case Key.M: Pick(_materialPick, ref _materialIndex, Materials.Length); break;
                case Key.N: Pick(_impactorPick, ref _impactorMaterialIndex, Materials.Length); break;
                case Key.Key1: case Key.Key2: case Key.Key3:
                case Key.Key4: case Key.Key5: case Key.Key6:
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
        SimState s = _scene.State;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.CellDead[c]) continue;
            float r = s.CellRad[c];
            if (s.CellPx[c] - r < minX) minX = s.CellPx[c] - r;
            if (s.CellPy[c] - r < minY) minY = s.CellPy[c] - r;
            if (s.CellPx[c] + r > maxX) maxX = s.CellPx[c] + r;
            if (s.CellPy[c] + r > maxY) maxY = s.CellPy[c] + r;
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
        float fromX = world.X - 600f;
        float fromY = world.Y;
        _shots++;
        // Radius from mass the same way Scenarios.Projectile derives it, so the slider means the
        // same thing whether the impactor is built into the scene or fired into it.
        float radius = 15f * MathF.Sqrt(_impactorMass);
        _scene = Scenarios.FireAt(_scene, _tune, ImpactorMaterial,
            fromX, fromY, world.X, world.Y,
            speed: _impactorSpeed, radius: radius, grain: _grain, seed: _shots);
        _poseComparable = false;
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
            if (s.CellDead[c]) continue;
            int bi = s.CellBody[c];
            if (bi < 0 || bi >= s.BodyCount || s.BodyCrushCap[bi] <= 0f) continue;
            float f = s.CellCrush[c] / s.BodyCrushCap[bi];
            if (f > worst) worst = f;
        }

        solver.TotalMomentum(out float px, out float py);
        float live = SimMath.Hypot(px, py);
        float led = SimMath.Hypot(solver.ExportedPx, solver.ExportedPy);
        float tot = live + led;

        return $"crush thr {ActiveMaterial.Crush:E1} (x{_crushThrMul:G3})"
             + $"   cap {ActiveMaterial.CrushCap:E1} (x{_crushCapMul:G3})"
             + $"   confine {_tune.CrushConfine:F3}"
             + $"   peak dose {worst * 100f,5:F1}% of cap"
             + $"   ledger {(tot < 1f ? 0f : 100f * led / tot),5:F1}%\n";
    }

    private void UpdateHud()
    {
        SimState s = _scene.State;
        Solver solver = _scene.Solver;

        int live = 0;
        for (int c = 0; c < s.CellCount; c++) if (!s.CellDead[c]) live++;

        double frame = _msTick + _msBuild + _msSubmit;

        _hud.Text =
            $"[{_scenario + 1}] {ScenarioNames[_scenario]}   body {ActiveMaterial.Name} · impactor {ImpactorMaterial.Name}"
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
          + $"grain {_grain:F0} ({MathF.Sqrt(_grain):F0} px)  impactor {_impactorSpeed:F0} px/s x{_impactorMass:F1}"
          + $"   strain x{_tune.StrainScale:F1}  toughness x{_tune.ToughnessScale:F1}"
          + $"  substeps {_tune.Substeps}   zoom {_zoom:F2}\n"
          + $"view: fills {(_drawFills ? "on" : "off")} · {_fillView.ToString().ToLowerInvariant()}"
          + $" · outlines {(_drawOutlines ? "on" : "off")} · bonds {_bondView.ToString().ToLowerInvariant()}\n"
          + "\n1-6 scenario · M/N body,impactor material · R reset · space pause · . step · [ ] slow-mo\n"
          + "F fills · C fill view · O outlines · B bond view · G/H grain\n"
          + "-/= strain · ,/ toughness · ;/' substeps\n"
          + "left-click fires · right-drag pans · wheel zooms · Z refits · esc quits";
    }
}
