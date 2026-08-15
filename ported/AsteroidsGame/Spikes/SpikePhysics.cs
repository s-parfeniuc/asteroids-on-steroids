using System;
using System.Diagnostics;
using AsteroidsSim.Math;
using AsteroidsSim.Spikes;
using Godot;

namespace AsteroidsGame.Spikes;

/// <summary>
/// Spike B — physics. PHASE0.md.
/// </summary>
/// <remarks>
/// <para><b>Question, stated as a decision rule:</b> <i>can we avoid writing a solver?</i></para>
///
/// <para>Adopt <c>godot-rapier-physics</c> (the "Slower Version with Cross Platform Deterministic"
/// build) if and only if all three hold:</para>
/// <list type="number">
///   <item>it meets the tick budget at 2,000 bodies / ~19,400 shapes;</item>
///   <item><c>Restore(Snapshot(s))</c> round-trips <b>bit-exactly</b> — required for Phase 8a rollback,
///         which §4.1 says is likely mandatory at 16 players;</item>
///   <item>its state hash matches across Linux, Windows and macOS.</item>
/// </list>
/// <para>Otherwise we port our own solver in Phase 2. Adopting it would save ~3–4 weeks.</para>
///
/// <para><b>Setup required before this runs</b> (see PHASE0.md):</para>
/// <list type="number">
///   <item>Install the Godot editor 4.7.x (.NET build).</item>
///   <item>Install <c>godot-rapier-physics</c> 2D, deterministic variant, into <c>addons/</c>.</item>
///   <item>Set <c>physics/2d/physics_engine</c> to the Rapier server in Project Settings.</item>
///   <item>Run this scene and record the readout into PHASE0.md.</item>
/// </list>
///
/// <para>The headless baseline (the existing <c>src/Engine</c> solver on the identical scenario) runs
/// separately in <c>tools/SpikePhysicsBaseline</c> and needs no Godot.</para>
/// </remarks>
public partial class SpikePhysics : Node2D
{
    private PhysicsScenario.BodySpec[] _spec = Array.Empty<PhysicsScenario.BodySpec>();
    private readonly System.Collections.Generic.List<Rid> _bodies = new();
    private readonly System.Collections.Generic.List<Rid> _shapes = new();

    private readonly double[] _tickMs = new double[PhysicsScenario.TickCount];
    private float _arenaScale = 1.0f;
    private int _tick;
    private bool _done;
    private Label _hud = null!;

    // Wall-clock cap: if the backend is so slow that 600 ticks would take forever,
    // report what we got rather than hanging. A backend that cannot finish inside
    // this budget has already failed the decision rule.
    private const double WallClockCapSeconds = 120.0;
    private readonly Stopwatch _wall = new();

    public override void _Ready()
    {
        // Env var rather than a command-line flag: Godot's arg parser rejects
        // user args placed after a scene path.
        float scale = 1.0f;
        string env = OS.GetEnvironment("AOS_ARENA_SCALE");
        if (!string.IsNullOrEmpty(env))
            float.TryParse(env, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out scale);
        if (scale <= 0f) scale = 1.0f;
        _arenaScale = scale;
        _spec = PhysicsScenario.Build(20260814, scale);

        _hud = new Label { Position = new Godot.Vector2(12, 12) };
        AddChild(_hud);

        GD.Print($"Spike B: {_spec.Length} bodies, {PhysicsScenario.TotalShapes(_spec):N0} shapes, arena x{_arenaScale:F1}, coverage {PhysicsScenario.ArealCoverage(_spec, _arenaScale) * 100:F0}%.");
        GD.Print($"Physics server: {ProjectSettings.GetSetting("physics/2d/physics_engine")}");

        var spawnWatch = Stopwatch.StartNew();
        SpawnBodies();
        spawnWatch.Stop();
        GD.Print($"Spawn took {spawnWatch.Elapsed.TotalSeconds:F2} s " +
                 $"({spawnWatch.Elapsed.TotalMilliseconds / _spec.Length:F3} ms/body).");
        _wall.Start();
    }

    public override void _ExitTree()
    {
        foreach (Rid r in _bodies) if (r.IsValid) PhysicsServer2D.FreeRid(r);
        foreach (Rid r in _shapes) if (r.IsValid) PhysicsServer2D.FreeRid(r);
    }

    private void SpawnBodies()
    {
        Rid space = GetWorld2D().Space;

        foreach (var b in _spec)
        {
            Rid body = PhysicsServer2D.BodyCreate();
            PhysicsServer2D.BodySetMode(body, PhysicsServer2D.BodyMode.Rigid);
            PhysicsServer2D.BodySetSpace(body, space);

            // One convex shape per cell — the compound collider, exactly as
            // CompoundShape works today. Per-shape disable is how a pulverised
            // cell leaves the broadphase.
            foreach (var poly in b.Shapes)
            {
                Rid shape = PhysicsServer2D.ConvexPolygonShapeCreate();
                var pts = new Godot.Vector2[poly.Length];
                for (int i = 0; i < poly.Length; i++) pts[i] = new Godot.Vector2(poly[i].X, poly[i].Y);
                PhysicsServer2D.ShapeSetData(shape, Variant.From(pts));
                PhysicsServer2D.BodyAddShape(body, shape);
                _shapes.Add(shape);
            }

            PhysicsServer2D.BodySetParam(body, PhysicsServer2D.BodyParameter.Mass, b.Mass);
            PhysicsServer2D.BodySetParam(body, PhysicsServer2D.BodyParameter.Inertia, b.Inertia);
            PhysicsServer2D.BodySetParam(body, PhysicsServer2D.BodyParameter.GravityScale, 0.0f);

            var xf = new Transform2D(b.Rotation, new Godot.Vector2(b.Position.X, b.Position.Y));
            PhysicsServer2D.BodySetState(body, PhysicsServer2D.BodyState.Transform, xf);
            PhysicsServer2D.BodySetState(body, PhysicsServer2D.BodyState.LinearVelocity,
                new Godot.Vector2(b.LinearVelocity.X, b.LinearVelocity.Y));
            PhysicsServer2D.BodySetState(body, PhysicsServer2D.BodyState.AngularVelocity,
                b.AngularVelocity);

            _bodies.Add(body);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;

        // Godot has already stepped the space for this tick by the time we get
        // here; we time the frame-to-frame physics cost via the engine's own
        // monitor rather than wrapping the step, which we do not control.
        double physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        _tickMs[_tick] = physicsMs;

        if (++_tick % 30 == 0)
            GD.Print($"  tick {_tick}/{PhysicsScenario.TickCount}  " +
                     $"last {physicsMs:F2} ms  wall {_wall.Elapsed.TotalSeconds:F1} s");

        if (_tick >= PhysicsScenario.TickCount || _wall.Elapsed.TotalSeconds > WallClockCapSeconds)
        {
            _done = true;
            Report();
        }
    }

    public override void _Process(double delta)
    {
        _hud.Text =
            $"Spike B — physics\n" +
            $"bodies {_spec.Length:N0}   shapes {PhysicsScenario.TotalShapes(_spec):N0}\n" +
            $"tick {_tick}/{PhysicsScenario.TickCount}\n" +
            (_done ? "DONE — see console output" : "running…");
    }

    private void Report()
    {
        var measured = new double[_tick];
        Array.Copy(_tickMs, measured, _tick);
        var sorted = measured;
        Array.Sort(sorted);   // presentation-side, not sim — unstable sort is fine here
        double median = sorted[sorted.Length / 2];
        double p99 = sorted[(int)(sorted.Length * 0.99)];
        double worst = sorted[^1];

        GD.Print("──────── Spike B result ────────");
        GD.Print($"backend        {ProjectSettings.GetSetting("physics/2d/physics_engine")}");
        GD.Print($"ticks measured {_tick}/{PhysicsScenario.TickCount}" +
                 (_tick < PhysicsScenario.TickCount ? "  (WALL-CLOCK CAP HIT)" : ""));
        GD.Print($"wall clock     {_wall.Elapsed.TotalSeconds:F1} s");
        GD.Print($"arena scale    x{_arenaScale:F1}  (coverage {PhysicsScenario.ArealCoverage(_spec, _arenaScale) * 100:F0}%)");
        GD.Print($"bodies         {_spec.Length:N0}");
        GD.Print($"shapes         {PhysicsScenario.TotalShapes(_spec):N0}");
        GD.Print($"median tick    {median:F3} ms");
        GD.Print($"p99 tick       {p99:F3} ms");
        GD.Print($"worst tick     {worst:F3} ms");
        GD.Print($"budget @60Hz   16.667 ms  (sim target: ~8 ms to leave room for a 16-player server)");
        GD.Print("");
        GD.Print("STILL TO MEASURE — see PHASE0.md Spike B decision rule:");
        GD.Print("  2. snapshot/restore round-trip fidelity (Rapier state serialization)");
        GD.Print("  3. cross-platform state hash (Linux / Windows / macOS)");
    }
}
