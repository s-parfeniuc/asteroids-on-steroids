// Asteroids on Steroids — SDL2 + SkiaSharp entry point.
//
//   WASD        thrust
//   Mouse       aim
//   Left-click  fire
//   Q/E/R       skills (dash / turbo / slow-mo)
//   Esc         quit
//
//   cd GameEngine/Game && dotnet run
//
// Backend-agnostic bootstrap + loop live in GameHost (AsteroidsGameCore); this file only
// constructs the concrete SDL window. The WinForms exe is the mirror of this file.

using AsteroidsGame;
using AsteroidsEngine.Platform.Sdl;

// Optional windowed-resolution override for perf testing: ASTEROIDS_RES=1280x720 dotnet run
// (renders at that size in a window, so you can A/B whether the frame is resolution/fill bound).
int W, H; bool fullscreen;
var resEnv = Environment.GetEnvironmentVariable("ASTEROIDS_RES");
if (!string.IsNullOrWhiteSpace(resEnv) && resEnv.Split('x') is [var ws, var hs]
    && int.TryParse(ws, out W) && int.TryParse(hs, out H))
{
    fullscreen = false;
}
else
{
    (W, H) = SdlGameWindow.QueryDisplaySize();
    fullscreen = true;
}
using var window = new SdlGameWindow("Asteroids on Steroids", W, H, fullscreen);
GameHost.Run(window);
