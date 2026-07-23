# Asteroids on Steroids — WinForms build (Windows only)

The **Windows** entry point. It runs the same game as `apps/Game.Sdl` (SDL2 + SkiaSharp) but hosts the
renderer in a **WinForms** window, using **SkiaSharp on the GPU** via an `SKGLControl` (an OpenGL surface
embedded in the form). It reuses the *exact same* `SkiaRenderer` as the SDL build — so every renderer
feature (draw-call batching, `IPostEffects`, mesh/particle/line batches) works identically. There is no
GDI+ renderer anymore.

## Requirements

- **Windows** (10/11).
- .NET 8 SDK **with the Windows Desktop workload** (`Microsoft.WindowsDesktop.App`). The stock SDK on
  Windows includes it; on Linux/macOS it does **not exist**, so these projects **cannot be built or run
  off Windows**. They are deliberately kept out of `AsteroidsOnSteroids.sln`, so a Linux build never
  pulls them in unless you name the `.csproj` explicitly. Native Skia (`libSkiaSharp`) is supplied by the
  `SkiaSharp.NativeAssets.Win32` package — no manual install.

## Build & run

From the repository root, on Windows:

```powershell
dotnet build src/Platform.WinForms/Engine.Platform.WinForms.csproj
dotnet build apps/Game.WinForms/AsteroidsGame.WinForms.csproj
dotnet run   --project apps/Game.WinForms
```

Assets resolve automatically: `GameConfigLoader.FindAssetsDir` walks up from the exe directory to the
repo-root `Assets/`, so no copying is needed.

## How it fits the engine

The engine is UI-toolkit-free; backends are separate assemblies implementing the PAL interfaces
(`IGameWindow`, `IRenderer`, and the optional capabilities). The renderer is now shared:

```
AsteroidsEngine (net8.0, UI-free)
├── Platform.Skia    (net8.0)          SkiaRenderer  — surface-agnostic, backend-neutral
├── Platform.Sdl     (net8.0)          SdlGameWindow      → apps/Game.Sdl      (SDL exe, all platforms)
└── Platform.WinForms(net8.0-windows)  WinFormsGameWindow → apps/Game.WinForms (this, Windows only)

GameCore (net8.0)  — states + GameHost (the shared bootstrap + fixed-timestep loop); no platform.
```

Both windows build a GPU `SKSurface` on their GL context and hand it to the shared `SkiaRenderer`; both
exes are a thin `Program.cs` that constructs their window and calls `GameHost.Run(window)`.

## Backend specifics & known caveats

- **Rendering**: `WinFormsGameWindow` creates the GL context once (`SKGLControl.MakeCurrent()`), builds a
  persistent `SKSurface` on the default framebuffer (id 0, `GL_RGBA8`, bottom-left origin — same as SDL),
  and constructs `SkiaRenderer` over it. `Present()` = `Canvas.Flush()` + `GRContext.Flush()` +
  `SKGLControl.SwapBuffers()`. **The engine drives the loop**, so we do *not* use the control's
  `PaintSurface` event and never call `Application.Run`; `PollEvents()` pumps the queue via
  `Application.DoEvents()`.
- **Input**: `KeyCode`/`MouseButton` values equal `System.Windows.Forms.Keys`/`MouseButtons`, so keys map
  with a direct cast (no lookup table). Events are wired on the `SKGLControl` (which holds focus), not the
  form.
- **Verified on Windows** — authored on Linux (where net8.0-windows can't compile) and confirmed working
  on a Windows machine: the self-contained build runs, renders, and plays correctly. The
  `SKGLControl`-hosted GPU path and the self-contained publish are both good.
  - **If the screen is blank or GL init throws:** the likely culprit is the `SKGLControl` GL-context
    setup — confirm `MakeCurrent()` runs after `Show()`/`DoEvents()` (so the handle exists) and that
    `GRGlInterface.Create()` returns non-null. As a documented fallback, a CPU surface via `SKControl`
    (raster `SKBitmap`) can replace `SKGLControl` — slower, but no GL context to negotiate.
