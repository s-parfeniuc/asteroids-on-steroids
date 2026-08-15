using System;
using AsteroidsSim.Math;
using Godot;

namespace AsteroidsGame.Sim;

/// <summary>
/// The single node that owns and drives the simulation.
/// </summary>
/// <remarks>
/// <para><b>Architecture.</b> PORT_PLAN.md §3.3–3.4. Godot calls us exactly twice per frame:
/// <see cref="_PhysicsProcess"/> at a true fixed 60 Hz advances the simulation, and
/// <see cref="_Process"/> once per rendered frame builds and submits geometry. Everything between
/// those two calls is plain C# with zero engine contact.</para>
///
/// <para><b>Why one node and not one per entity.</b> An overridden <c>_PhysicsProcess</c> on N nodes is
/// N managed→native transitions per tick before any work happens, and rollback would become scene-graph
/// reconciliation rather than a <c>memcpy</c>. Swarm bodies are array rows; only hero entities
/// (players, boss, named elites) get pooled view nodes.</para>
///
/// <para><b>Phase 0 status.</b> This is the skeleton: it draws one triangle through the batched
/// <c>RenderingServer</c> path to prove the submission route end to end. `SimState` and the system list
/// arrive in Phase 1.</para>
/// </remarks>
public partial class SimRoot : Node2D
{
    /// <summary>Fixed simulation timestep. Must match <c>physics/common/physics_ticks_per_second</c>.</summary>
    public const float FixedDt = 1.0f / 60.0f;

    private Rid _canvasItem;
    private uint _tick;
    private DetRng _rng = DetRng.FromSeed(1);

    // Reusable geometry buffers. Allocated once; overwritten each frame.
    // Never reallocate per frame — allocation is the measured bottleneck in the
    // current build (~2 KB and ~110 objects per hit). See PORT_PLAN.md §5.
    private Vector2[] _points = Array.Empty<Vector2>();
    private Color[] _colors = Array.Empty<Color>();
    private int[] _indices = Array.Empty<int>();

    public override void _Ready()
    {
        _canvasItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_canvasItem, GetCanvasItem());

        EnsureCapacity(3);
        GD.Print($"SimRoot ready. Fixed tick {1.0f / FixedDt:F0} Hz.");
    }

    public override void _ExitTree()
    {
        if (_canvasItem.IsValid)
        {
            RenderingServer.FreeRid(_canvasItem);
            _canvasItem = default;
        }
    }

    /// <summary>Fixed-rate simulation step. Godot runs this 0..N times per frame to catch up.</summary>
    public override void _PhysicsProcess(double delta)
    {
        // Phase 1 replaces this with:
        //     var inputs = _net.InputsForTick(_state.Tick);
        //     foreach (var s in _systems) s.Update(ref _state, in inputs, FixedDt);
        _tick++;
    }

    /// <summary>Once per rendered frame: build geometry from sim state and submit it batched.</summary>
    public override void _Process(double delta)
    {
        RenderingServer.CanvasItemClear(_canvasItem);

        // Placeholder geometry: a rotating triangle, driven through SimMath so the
        // deterministic path is exercised from frame one.
        float angle = _tick * FixedDt;
        Vector2 centre = GetViewportRect().Size * 0.5f;
        const float radius = 160f;

        for (int i = 0; i < 3; i++)
        {
            float a = angle + i * (SimMath.TwoPI / 3f);
            SimMath.SinCos(a, out float sa, out float ca);
            _points[i] = centre + new Vector2(ca * radius, sa * radius);
            _colors[i] = Color.FromHsv((i / 3f + angle * 0.05f) % 1f, 0.6f, 0.9f);
            _indices[i] = i;
        }

        // The batched submission path — the analogue of the current build's
        // IMeshBatch.FillMesh. At full scale this carries every live cell in the
        // world in one call.
        RenderingServer.CanvasItemAddTriangleArray(_canvasItem, _indices, _points, _colors);
    }

    private void EnsureCapacity(int vertexCount)
    {
        if (_points.Length >= vertexCount) return;
        _points = new Vector2[vertexCount];
        _colors = new Color[vertexCount];
        _indices = new int[vertexCount];
    }
}
