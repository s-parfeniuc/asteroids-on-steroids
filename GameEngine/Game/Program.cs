// Asteroids on Steroids
//
//   WASD        thrust
//   Mouse       aim
//   Left-click  fire
//   Q/E/R       skills (dash / turbo / slow-mo)  [stub]
//   Esc         quit
//
//   cd GameEngine/Game && dotnet run

using System.Diagnostics;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Platform.Sdl;
using AsteroidsGame;
using AsteroidsGame.States;
using AsteroidsGame.Config;

const int W = 1920, H = 1080;

// ── Config ────────────────────────────────────────────────────────────────────

string assetsDir = GameConfigLoader.FindAssetsDir(AppContext.BaseDirectory);
var (config, shapes) = GameConfigLoader.Load(assetsDir);

// ── Window + input ────────────────────────────────────────────────────────────

using var window = new SdlGameWindow("Asteroids on Steroids", W, H);
var input = new InputSystem();
window.KeyDown          += k       => input.OnKeyDown(k);
window.KeyUp            += k       => input.OnKeyUp(k);
window.MouseMoved       += p       => input.OnMouseMove(p);
window.MouseButtonChanged += (b, pr) => input.OnMouseButton(b, pr);

// ── State machine setup ───────────────────────────────────────────────────────

var ctx   = new GameContext(config, shapes, input, W, H);
IGameState state = new MainMenuState(ctx);
state.Enter();

// ── Timing ────────────────────────────────────────────────────────────────────

const double FixedDt = 1.0 / 120.0;
var fixedStep = new FixedTimestep(FixedDt);
var sw        = Stopwatch.StartNew();
long lastTicks = sw.ElapsedTicks;

// ── Main loop ─────────────────────────────────────────────────────────────────

while (!window.ShouldClose)
{
    window.PollEvents();

    long   now       = sw.ElapsedTicks;
    double frameTime = (double)(now - lastTicks) / Stopwatch.Frequency;
    lastTicks = now;

    input.BeginFrame();
    if (input.IsPressed(KeyCode.Escape)) break;

    // Fixed-step updates.
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

    // Render at the sub-step alpha position.
    state.Draw(window.Renderer, fixedStep.Alpha);
    window.Present();

    // Soft frame cap: sleep until the next 120 Hz slot if we finished early.
    double elapsed = (double)(sw.ElapsedTicks - now) / Stopwatch.Frequency;
    int sleepMs = (int)((1.0 / 120.0 - elapsed) * 1000);
    if (sleepMs > 1) Thread.Sleep(sleepMs);
}

state.Exit();
