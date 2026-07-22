namespace AsteroidsEngine.Engine.Diagnostics;

/// <summary>
/// Lightweight per-section frame profiler: callers time named sections (systems, draw phases) and the
/// profiler keeps an exponential moving average of each per rendered frame, plus overall frame ms / FPS.
/// Zero cost when <see cref="Enabled"/> is false (callers gate their Stopwatch on it). Insertion order
/// is preserved so an overlay lists sections in execution order. Not thread-safe — single game thread.
/// </summary>
public sealed class FrameProfiler
{
    /// <summary>The profiler the running state exposes to the shared game loop, so loop-level phases
    /// (e.g. <c>window.Present()</c>) that live outside any state can record into it. Set by the owning
    /// state on Enter, cleared on Exit; null when no profiling state is active.</summary>
    public static FrameProfiler? Active;

    public bool Enabled;

    private readonly List<string> _order = new();
    private readonly Dictionary<string, double> _cur = new();   // ms accumulated this frame (summed over steps)
    private readonly Dictionary<string, double> _ema = new();   // smoothed ms per section
    private const double Alpha = 0.1;

    public double FrameMsEma { get; private set; }
    public double Fps        { get; private set; }

    /// <summary>Accumulate a timing for <paramref name="name"/> this frame (summed across fixed steps).</summary>
    public void Add(string name, double ms)
    {
        if (!Enabled) return;
        if (!_cur.ContainsKey(name))
        {
            _cur[name] = 0;
            if (!_ema.ContainsKey(name)) { _ema[name] = 0; _order.Add(name); }
        }
        _cur[name] += ms;
    }

    /// <summary>Fold this frame's accumulated section times into their EMAs and reset. Call once per
    /// rendered frame with the whole-frame time.</summary>
    public void CommitFrame(double frameMs)
    {
        if (!Enabled) return;
        FrameMsEma += (frameMs - FrameMsEma) * Alpha;
        Fps = FrameMsEma > 1e-4 ? 1000.0 / FrameMsEma : 0.0;
        foreach (var name in _order)
        {
            double v = _cur.TryGetValue(name, out var c) ? c : 0.0;
            _ema[name] += (v - _ema[name]) * Alpha;
            _cur[name] = 0.0;
        }
    }

    public IReadOnlyList<string> Sections => _order;
    public double Ema(string name) => _ema.TryGetValue(name, out var v) ? v : 0.0;

    /// <summary>A pasteable text snapshot of the current smoothed table (for reporting a measurement).</summary>
    public string FormatTable()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FPS {Fps:F0}   frame {FrameMsEma:F2} ms");
        double summed = 0;
        foreach (var n in _order) { double v = Ema(n); summed += v; sb.AppendLine($"  {n,-26} {v,8:F3} ms"); }
        sb.AppendLine($"  {"unaccounted",-26} {FrameMsEma - summed,8:F3} ms");
        return sb.ToString();
    }
}
