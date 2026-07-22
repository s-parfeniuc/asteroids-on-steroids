using System.Diagnostics;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Config;
using AsteroidsGame.States;

namespace AsteroidsGame;

/// <summary>
/// The backend-agnostic game bootstrap + main loop. Each platform executable is a thin entry point
/// that constructs its own concrete <see cref="IGameWindow"/> (SDL or WinForms) and hands it here;
/// everything after — input wiring, config load, the fixed-timestep loop — is identical, so it lives
/// once in this shared library which references no platform backend.
/// </summary>
public static class GameHost
{
    private const double FixedDt = 1.0 / 120.0;

    /// <summary>Loads config, wires the window's input into an <see cref="InputSystem"/>, and runs the
    /// fixed-timestep update/draw/present loop until the window closes or a state requests quit.</summary>
    public static void Run(IGameWindow window)
    {
        // ── Config ────────────────────────────────────────────────────────────
        string assetsDir = GameConfigLoader.FindAssetsDir(AppContext.BaseDirectory);
        var (config, shapes) = GameConfigLoader.Load(assetsDir);

        // ── Input: the window raises the five PAL events; route them into InputSystem ──
        var input = new InputSystem();
        window.KeyDown            += k       => input.OnKeyDown(k);
        window.KeyUp              += k       => input.OnKeyUp(k);
        window.MouseMoved         += p       => input.OnMouseMove(p);
        window.MouseButtonChanged += (b, pr) => input.OnMouseButton(b, pr);
        window.TextInput          += s       => input.OnTextInput(s);

        // ── State machine ─────────────────────────────────────────────────────
        var ctx = new GameContext(config, shapes, input, window.Width, window.Height);
        IGameState state = new MainMenuState(ctx);
        state.Enter();

        // ── Timing ────────────────────────────────────────────────────────────
        var fixedStep = new FixedTimestep(FixedDt);
        var sw = Stopwatch.StartNew();
        long lastTicks = sw.ElapsedTicks;

        // ── Main loop ─────────────────────────────────────────────────────────
        while (!window.ShouldClose)
        {
            window.PollEvents();

            long now = sw.ElapsedTicks;
            double frameTime = (double)(now - lastTicks) / Stopwatch.Frequency;
            lastTicks = now;

            input.BeginFrame();

            // Fixed-step updates. Esc is handled by the states (menu quits, PlayingState pauses); a
            // state sets ctx.QuitRequested to exit.
            int steps = fixedStep.Advance(frameTime);
            for (int i = 0; i < steps; i++)
            {
                IGameState? next = state.Update(FixedDt);
                if (next is not null)
                {
                    state.Exit();
                    state = next;
                    state.Enter();
                }
            }
            if (ctx.QuitRequested) break;

            // Render at the sub-step alpha position.
            state.Draw(window.Renderer, fixedStep.Alpha);

            // Present is where the GPU actually rasterizes the frame's recorded commands + swaps
            // buffers — usually the largest cost the per-system profiler can't see, so time it here.
            var prof = FrameProfiler.Active;
            if (prof is { Enabled: true })
            {
                long p0 = sw.ElapsedTicks;
                window.Present();
                prof.Add("Present", (double)(sw.ElapsedTicks - p0) / Stopwatch.Frequency * 1000.0);
            }
            else window.Present();

            // Soft frame cap: sleep until the next 120 Hz slot if we finished early.
            double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
            int sleepMs = (int)((FixedDt - elapsed) * 1000);
            if (sleepMs > 1) Thread.Sleep(sleepMs);
        }

        state.Exit();
    }
}
