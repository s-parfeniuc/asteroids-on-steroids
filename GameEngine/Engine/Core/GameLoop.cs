using System.Diagnostics;
using AsteroidsEngine.Engine.Input;

namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// Runs the game on a dedicated background thread.
///
/// Each frame:
///   1. Measure dt with Stopwatch
///   2. InputSystem.BeginFrame()
///   3. Run all ISystem.Update(world, dt) in registration order
///   4. World.FlushDeferred() — destroy marked entities
///   5. Invoke OnDraw() — GameWindow creates Graphics, renders, swaps buffers
///   6. Sleep remaining frame budget
///
/// GameLoop has no dependency on WinForms or bitmaps.
/// GameWindow owns the buffer lifecycle and registers OnDraw.
/// </summary>
public sealed class GameLoop
{
    private readonly World         _world;
    private readonly InputSystem   _input;
    private readonly List<ISystem> _systems = new();

    private Thread?       _thread;
    private volatile bool _running;

    public int TargetFps { get; set; } = 60;

    /// <summary>
    /// Called each frame after all systems have updated.
    /// GameWindow registers a callback that creates Graphics from the back
    /// buffer, calls draw systems, swaps buffers, and calls Invalidate().
    /// </summary>
    public Action? OnDraw { get; set; }

    /// <summary>Raised on the game thread when the loop stops.</summary>
    public event Action? Stopped;

    public GameLoop(World world, InputSystem input)
    {
        _world = world;
        _input = input;
    }

    // -------------------------------------------------------------------------
    // System registration
    // -------------------------------------------------------------------------

    public void AddSystem(ISystem system)
    {
        _systems.Add(system);
    }

    public void AddSystems(params ISystem[] systems)
    {
        foreach (var s in systems) _systems.Add(s);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread  = new Thread(Loop) { IsBackground = true, Name = "GameLoop" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
    }

    public void Join() => _thread?.Join();

    // -------------------------------------------------------------------------
    // The loop
    // -------------------------------------------------------------------------

    private void Loop()
    {
        var  stopwatch   = Stopwatch.StartNew();
        long lastTicks   = stopwatch.ElapsedTicks;
        long ticksPerSec = Stopwatch.Frequency;

        while (_running)
        {
            long  nowTicks = stopwatch.ElapsedTicks;
            double dt      = (double)(nowTicks - lastTicks) / ticksPerSec;
            lastTicks      = nowTicks;

            // Clamp dt: if the loop stalls (debugger, OS scheduling hiccup),
            // don't let entities teleport across the screen.
            dt = Math.Min(dt, 0.1);

            // 1. Commit input state for this frame
            _input.BeginFrame();

            // 2. Update all systems
            foreach (var system in _systems)
                system.Update(_world, dt);

            // 3. Remove entities/components marked for deferred destruction
            _world.FlushDeferred();

            // 4. Render — GameWindow's callback handles buffer lifecycle
            OnDraw?.Invoke();

            // 5. Sleep the remainder of the frame budget
            double frameBudget = 1.0 / TargetFps;
            double elapsed     = (double)(stopwatch.ElapsedTicks - nowTicks) / ticksPerSec;
            int    sleepMs     = (int)((frameBudget - elapsed) * 1000);
            if (sleepMs > 1) Thread.Sleep(sleepMs);
        }

        Stopped?.Invoke();
    }
}
