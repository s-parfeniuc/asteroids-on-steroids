namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// Fixed-timestep accumulator. Decouples the simulation rate from the render
/// rate: feed it the real elapsed time each frame, run the simulation exactly
/// <see cref="Advance"/> times with the fixed <see cref="Step"/>, then render
/// using <see cref="Alpha"/> to interpolate between the last two sim states.
///
/// This keeps physics stable and deterministic regardless of frame rate, and is
/// the foundation the contact solver / stacking fragments rely on.
/// </summary>
public sealed class FixedTimestep
{
    /// <summary>Fixed simulation step in seconds (e.g. 1/120).</summary>
    public double Step { get; }

    private readonly double _maxAccum;
    private double _acc;

    public FixedTimestep(double step, double maxAccumulatedSeconds = 0.25)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        Step      = step;
        _maxAccum = maxAccumulatedSeconds;
    }

    /// <summary>
    /// Adds real elapsed time and returns how many fixed steps to run now.
    /// The accumulator is clamped to avoid a spiral of death after a long stall.
    /// </summary>
    public int Advance(double realDelta)
    {
        _acc += realDelta;
        if (_acc > _maxAccum) _acc = _maxAccum;

        int steps = 0;
        while (_acc >= Step) { _acc -= Step; steps++; }
        return steps;
    }

    /// <summary>Interpolation factor in [0,1) between the previous and current sim state.</summary>
    public float Alpha => (float)(_acc / Step);
}
