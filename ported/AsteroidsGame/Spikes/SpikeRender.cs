using System;
using System.Diagnostics;
using AsteroidsSim.Math;
using Godot;

namespace AsteroidsGame.Spikes;

/// <summary>
/// Spike A — batched rendering throughput. PHASE0.md.
/// </summary>
/// <remarks>
/// <para><b>Question.</b> Does batched <c>RenderingServer</c> submission carry the target cell counts,
/// and what does C#→Godot marshalling actually cost?</para>
///
/// <para><b>Method.</b> Generate N cells' worth of geometry from plain C# arrays and submit it, timing
/// three stages separately: geometry build (pure C#), marshalling into Godot packed arrays, and
/// submission. Then the same for a <c>MultiMesh</c> of debris instances.</para>
///
/// <para><b>Also answers:</b> can the packed arrays be reused across frames rather than reallocated?
/// Do 8-bit vertex colours band on a smooth gradient (Godot 4 forces 8-bit in 2D; Godot 3 allowed
/// float, and <c>CellColorizer</c> produces Laplacian-smoothed gradients)?</para>
///
/// <para>Run it, watch the on-screen readout, then press <c>1/2/3</c> to change cell count and
/// <c>G</c> to toggle the gradient test pattern.</para>
/// </remarks>
public partial class SpikeRender : Node2D
{
    private static readonly int[] CellCountSteps = { 5_000, 20_000, 50_000 };
    private int _step;

    private const int VertsPerCell = 6;   // a hexagonal cell fan-triangulated
    private const int TrisPerCell = 4;

    private Rid _canvasItem;
    private Rid _multimesh;
    private Rid _multimeshItem;
    private Rid _quadMesh;

    // Reused every frame. Never reallocated once sized.
    private Godot.Vector2[] _points = Array.Empty<Godot.Vector2>();
    private Color[] _colors = Array.Empty<Color>();
    private int[] _indices = Array.Empty<int>();
    private float[] _mmBuffer = Array.Empty<float>();
    private float[] _debrisX = Array.Empty<float>(), _debrisY = Array.Empty<float>();
    private float[] _debrisVx = Array.Empty<float>(), _debrisVy = Array.Empty<float>();
    private float[] _debrisRot = Array.Empty<float>();

    private const int CellsPerBody = 30;   // matches PhysicsScenario's compound bodies

    private DetRng _rng = DetRng.FromSeed(20260814);
    private Godot.Vector2[] _localVerts = Array.Empty<Godot.Vector2>();  // cached, body-local
    private Color[] _cellColor = Array.Empty<Color>();
    private Godot.Vector2[] _bodyOrigin = Array.Empty<Godot.Vector2>();
    private float[] _bodyPhase = Array.Empty<float>();
    private float[] _bodySpin = Array.Empty<float>();

    private bool _gradientMode;
    private bool _useSpanPath = true;

    private static double Smooth(double prev, double now) => prev == 0 ? now : prev * 0.9 + now * 0.1;
    private const int MultimeshInstances = 20_000;
    private double _tBuild, _tSubmit, _tMultimesh;
    private Label _hud = null!;

    private int CellCount => CellCountSteps[_step];

    public override void _Ready()
    {
        _canvasItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_canvasItem, GetCanvasItem());

        SetupMultimesh();

        _hud = new Label
        {
            Position = new Vector2(12, 12),
            Theme = new Theme(),
        };
        AddChild(_hud);

        Rebuild();
    }

    public override void _ExitTree()
    {
        foreach (Rid r in new[] { _canvasItem, _multimeshItem, _multimesh, _quadMesh })
            if (r.IsValid) RenderingServer.FreeRid(r);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        switch (k.Keycode)
        {
            case Key.Key1: _step = 0; Rebuild(); break;
            case Key.Key2: _step = 1; Rebuild(); break;
            case Key.Key3: _step = 2; Rebuild(); break;
            case Key.G: _gradientMode = !_gradientMode; break;
            case Key.S: _useSpanPath = !_useSpanPath; break;
            case Key.Escape: GetTree().Quit(); break;
        }
    }

    private void Rebuild()
    {
        int cells = CellCount;
        int verts = cells * VertsPerCell;
        int idx = cells * TrisPerCell * 3;
        int bodies = (cells + CellsPerBody - 1) / CellsPerBody;

        _points = new Godot.Vector2[verts];
        _colors = new Color[verts];
        _indices = new int[idx];
        _localVerts = new Godot.Vector2[verts];
        _cellColor = new Color[cells];
        _bodyOrigin = new Godot.Vector2[bodies];
        _bodyPhase = new float[bodies];
        _bodySpin = new float[bodies];

        Godot.Vector2 size = GetViewportRect().Size;
        _rng = DetRng.FromSeed(20260814);

        for (int bi = 0; bi < bodies; bi++)
        {
            _bodyOrigin[bi] = new Godot.Vector2(
                _rng.NextFloat(RngStream.Tessellation, 0, size.X),
                _rng.NextFloat(RngStream.Tessellation, 0, size.Y));
            _bodyPhase[bi] = _rng.NextFloat(RngStream.Tessellation, 0, SimMath.TwoPI);
            _bodySpin[bi] = _rng.NextFloat(RngStream.Tessellation, -1.0f, 1.0f);
        }

        // Cell polygons, cached ONCE in body-local space. Never rebuilt.
        for (int k = 0; k < cells; k++)
        {
            int bi = k / CellsPerBody;
            float ca = _rng.NextFloat(RngStream.Tessellation, 0, SimMath.TwoPI);
            float cd = _rng.NextFloat(RngStream.Tessellation, 0, 90f);
            SimMath.SinCos(ca, out float sa, out float caCos);
            Godot.Vector2 centre = new(caCos * cd, sa * cd);

            float h = _gradientMode ? (bi % 97) / 97f : ((k * 0.618f) % 1f);
            _cellColor[k] = Color.FromHsv(h, _gradientMode ? 0.25f : 0.55f, 0.85f);

            int b = k * VertsPerCell;
            for (int i = 0; i < VertsPerCell; i++)
            {
                float a = i * (SimMath.TwoPI / VertsPerCell);
                float r = 5f * _rng.NextFloat(RngStream.Tessellation, 0.85f, 1.15f);
                SimMath.SinCos(a, out float sv, out float cv);
                _localVerts[b + i] = new Godot.Vector2(centre.X + cv * r, centre.Y + sv * r);
            }
        }

        // Index buffer is static: cell k occupies vertices [k*6, k*6+6), fan-triangulated.
        int w = 0;
        for (int k = 0; k < cells; k++)
        {
            int b = k * VertsPerCell;
            for (int tri = 0; tri < TrisPerCell; tri++)
            {
                _indices[w++] = b;
                _indices[w++] = b + tri + 1;
                _indices[w++] = b + tri + 2;
            }
        }

        GD.Print($"rebuilt: {cells:N0} cells in {bodies:N0} bodies, {verts:N0} verts, {idx / 3:N0} tris.");
    }

    private void SetupMultimesh()
    {
        // One instanced quad per debris chip — the T2 tier's render path.
        _quadMesh = RenderingServer.MeshCreate();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)RenderingServer.ArrayType.Max);
        arrays[(int)RenderingServer.ArrayType.Vertex] = new Vector2[]
        {
            new(-2, -2), new(2, -2), new(2, 2), new(-2, 2),
        };
        arrays[(int)RenderingServer.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        RenderingServer.MeshAddSurfaceFromArrays(_quadMesh, RenderingServer.PrimitiveType.Triangles, arrays);

        _multimesh = RenderingServer.MultimeshCreate();
        RenderingServer.MultimeshSetMesh(_multimesh, _quadMesh);

        _multimeshItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_multimeshItem, GetCanvasItem());
    }

    // ── automated sweep ──────────────────────────────────────────────────────
    // Cycles every (cellCount × submitPath) combination, warms up, measures, and
    // prints a table. Interactive keys still work if you want to poke at it.
    private const int WarmupFrames = 45;
    private const int MeasureFrames = 180;
    private int _frameInPhase;
    private int _phase;                       // 0..5 = 3 cell counts × 2 submit paths
    private bool _sweepDone;
    private readonly System.Collections.Generic.List<string> _results = new();
    private double _accBuild, _accSubmit, _accMm;

    public override void _Process(double delta)
    {
        int cells = CellCount;
        float t = (float)Time.GetTicksMsec() * 0.001f;
        Godot.Vector2 size = GetViewportRect().Size;

        // ── stage 1: geometry build (pure C#) ────────────────────────────────
        long s0 = Stopwatch.GetTimestamp();
        BuildGeometry(cells, t);
        long s1 = Stopwatch.GetTimestamp();

        // ── stage 2: clear, then submit across the FFI boundary ──────────────
        RenderingServer.CanvasItemClear(_canvasItem);
        long s2 = Stopwatch.GetTimestamp();

        if (_useSpanPath)
        {
            // Godot 4.7 exposes a ReadOnlySpan overload: no managed array copy at
            // the boundary, so geometry buffers can be built once and reused.
            // The span overload declares no defaults — every argument is explicit.
            RenderingServer.CanvasItemAddTriangleArray(
                _canvasItem,
                new ReadOnlySpan<int>(_indices),
                new ReadOnlySpan<Godot.Vector2>(_points),
                new ReadOnlySpan<Color>(_colors),
                ReadOnlySpan<Godot.Vector2>.Empty,
                ReadOnlySpan<int>.Empty,
                ReadOnlySpan<float>.Empty,
                default,
                -1);
        }
        else
        {
            RenderingServer.CanvasItemAddTriangleArray(_canvasItem, _indices, _points, _colors);
        }
        long s3 = Stopwatch.GetTimestamp();

        // ── stage 3: multimesh (the T2 debris tier) ──────────────────────────
        long s4 = Stopwatch.GetTimestamp();
        UpdateMultimesh(MultimeshInstances, t, size);
        long s5 = Stopwatch.GetTimestamp();

        double toMs = 1000.0 / Stopwatch.Frequency;
        double build = (s1 - s0) * toMs, submit = (s3 - s2) * toMs, mm = (s5 - s4) * toMs;
        _tBuild = Smooth(_tBuild, build);
        _tSubmit = Smooth(_tSubmit, submit);
        _tMultimesh = Smooth(_tMultimesh, mm);

        if (!_sweepDone) AdvanceSweep(build, submit, mm);

        _hud.Text =
            $"Spike A — batched rendering\n" +
            $"cells {cells:N0}   verts {cells * VertsPerCell:N0}   tris {cells * TrisPerCell:N0}\n" +
            $"submit path: {(_useSpanPath ? "ReadOnlySpan" : "managed array")}\n" +
            $"[G] gradient: {(_gradientMode ? "ON" : "off")}\n\n" +
            $"build (C#)      {_tBuild,7:F3} ms\n" +
            $"submit (FFI)    {_tSubmit,7:F3} ms\n" +
            $"multimesh {MultimeshInstances / 1000}k   {_tMultimesh,7:F3} ms\n" +
            $"fps             {Engine.GetFramesPerSecond(),7:F0}\n" +
            (_sweepDone ? "\nSWEEP DONE — see console" : $"\nsweep phase {_phase + 1}/6");
    }

    private void AdvanceSweep(double build, double submit, double mm)
    {
        _frameInPhase++;
        if (_frameInPhase <= WarmupFrames) return;

        _accBuild += build; _accSubmit += submit; _accMm += mm;

        if (_frameInPhase < WarmupFrames + MeasureFrames) return;

        int n = MeasureFrames;
        _results.Add(string.Format(
            "{0,8:N0} {1,9:N0} {2,10:N0} | {3,-14} {4,8:F3} {5,8:F3} {6,8:F3} {7,8:F3} {8,7:F0}",
            CellCount, CellCount * VertsPerCell, CellCount * TrisPerCell,
            _useSpanPath ? "ReadOnlySpan" : "managed array",
            _accBuild / n, _accSubmit / n, _accMm / n,
            (_accBuild + _accSubmit + _accMm) / n,
            Engine.GetFramesPerSecond()));

        _accBuild = _accSubmit = _accMm = 0;
        _frameInPhase = 0;
        _phase++;

        if (_phase >= 6) { _sweepDone = true; PrintReport(); return; }

        _useSpanPath = (_phase % 2) == 0;
        _step = _phase / 2;
        Rebuild();
    }

    private void PrintReport()
    {
        GD.Print("");
        GD.Print("════════════ SPIKE A — batched rendering throughput ════════════");
        GD.Print($"renderer: {ProjectSettings.GetSetting("rendering/renderer/rendering_method")}   " +
                 $"multimesh instances: {MultimeshInstances:N0}   measured over {MeasureFrames} frames after {WarmupFrames} warmup");
        GD.Print("");
        GD.Print("   cells     verts      tris | submit path      build   submit      mm    total     fps");
        GD.Print("   ────────────────────────────────────────────────────────────────────────────────────");
        foreach (string r in _results) GD.Print("   " + r);
        GD.Print("");
        GD.Print("   build  = geometry generation, pure C#");
        GD.Print("   submit = CanvasItemAddTriangleArray, i.e. the C#->Godot boundary");
        GD.Print("   mm     = MultimeshSetBuffer for the debris tier");
        GD.Print("   budget = 16.667 ms/frame at 60 Hz");
        GD.Print("════════════════════════════════════════════════════════════════");
        GetTree().Quit();
    }

    /// <summary>
    /// Representative geometry build: cell polygons are cached in BODY-local space and
    /// transformed by one rotation per BODY, not per vertex.
    /// </summary>
    /// <remarks>
    /// The first version of this spike ran a <c>SinCos</c> per vertex, which measured the
    /// benchmark rather than the renderer — 120k trig calls a frame that the real path
    /// never performs. Bodies own ~30 cells, so this is 1 <c>SinCos</c> per 30 cells plus
    /// a 2x2 transform per vertex, which is what WorldRenderer actually does.
    /// </remarks>
    private void BuildGeometry(int cells, float t)
    {
        int bodies = (cells + CellsPerBody - 1) / CellsPerBody;

        for (int bi = 0; bi < bodies; bi++)
        {
            // One trig evaluation per body.
            float rot = _bodyPhase[bi] + t * _bodySpin[bi];
            SimMath.SinCos(rot, out float sr, out float cr);
            Godot.Vector2 origin = _bodyOrigin[bi];

            int first = bi * CellsPerBody;
            int last = System.Math.Min(first + CellsPerBody, cells);

            for (int k = first; k < last; k++)
            {
                int b = k * VertsPerCell;
                Color c = _cellColor[k];

                for (int i = 0; i < VertsPerCell; i++)
                {
                    // Cached body-local vertex, rotated and translated.
                    Godot.Vector2 lv = _localVerts[b + i];
                    _points[b + i] = new Godot.Vector2(
                        origin.X + lv.X * cr - lv.Y * sr,
                        origin.Y + lv.X * sr + lv.Y * cr);
                    _colors[b + i] = c;
                }
            }
        }
    }

    private void UpdateMultimesh(int instances, float t, Vector2 size)
    {
        // 2D transform + colour = 8 + 4 floats per instance.
        const int Stride = 12;
        if (_mmBuffer.Length != instances * Stride)
        {
            _mmBuffer = new float[instances * Stride];
            _debrisX = new float[instances]; _debrisY = new float[instances];
            _debrisVx = new float[instances]; _debrisVy = new float[instances];
            _debrisRot = new float[instances];
            var dr = DetRng.FromSeed(7);
            for (int i = 0; i < instances; i++)
            {
                _debrisX[i] = dr.NextFloat(RngStream.Tessellation, 0, size.X);
                _debrisY[i] = dr.NextFloat(RngStream.Tessellation, 0, size.Y);
                _debrisVx[i] = dr.NextFloat(RngStream.Tessellation, -20f, 20f);
                _debrisVy[i] = dr.NextFloat(RngStream.Tessellation, -20f, 20f);
                _debrisRot[i] = dr.NextFloat(RngStream.Tessellation, 0, SimMath.TwoPI);
            }
            // (rid, instances, transformFormat, useColors, useCustomData) — positional:
            // the C# binding does not name these parameters.
            RenderingServer.MultimeshAllocateData(
                _multimesh, instances, RenderingServer.MultimeshTransformFormat.Transform2D, true, false);
            RenderingServer.CanvasItemClear(_multimeshItem);
            RenderingServer.CanvasItemAddMultimesh(_multimeshItem, _multimesh);
        }

        // Representative: debris positions/rotations come from the sim's SoA arrays,
        // so per frame this is one SinCos and a buffer write per chip — no RNG.
        for (int i = 0; i < instances; i++)
        {
            float x = _debrisX[i] + t * _debrisVx[i];
            float y = _debrisY[i] + t * _debrisVy[i];
            float rot = _debrisRot[i] + t;
            SimMath.SinCos(rot, out float s, out float c);

            int o = i * Stride;
            _mmBuffer[o + 0] = c; _mmBuffer[o + 1] = -s; _mmBuffer[o + 2] = 0; _mmBuffer[o + 3] = x;
            _mmBuffer[o + 4] = s; _mmBuffer[o + 5] = c; _mmBuffer[o + 6] = 0; _mmBuffer[o + 7] = y;
            _mmBuffer[o + 8] = 0.6f; _mmBuffer[o + 9] = 0.6f; _mmBuffer[o + 10] = 0.7f; _mmBuffer[o + 11] = 1f;
        }

        RenderingServer.MultimeshSetBuffer(_multimesh, new ReadOnlySpan<float>(_mmBuffer));
    }
}
