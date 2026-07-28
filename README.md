# Asteroids on Steroids

A physics-based space shooter with a custom ECS engine: the map is ~10× the screen, and every asteroid
and enemy ship is a body of convex-polygon **cells** joined by spring-like **bonds** that crack, split,
and pulverize under impact. Waves of asteroids and enemies escalate; a vortex pulls at the field and the
map border erodes anything that camps the edge.

**Controls:** `WASD` thrust · mouse aim · left-click fire · `Q`/`E`/`R` skills (dash / turbo / slow-mo) ·
`G` grenade · `F`/piercing · `Esc` pause/quit.

---

## Play a packaged build

Each OS gets a **self-contained** folder — no .NET install required. Download (or build, below), unzip,
and run the executable. Keep the `Assets/` folder next to the executable (the scripts place it there). The self-contained folders are in the release section on GitHub (https://github.com/s-parfeniuc/asteroids-on-steroids). 

| OS | Executable | Renderer |
|----|-----------|----------|
| **Windows** 10/11 | `AsteroidsGame.WinForms.exe` | WinForms + SkiaSharp (GPU) |
| **Linux** | `./AsteroidsGame` | SDL2 + SkiaSharp (GPU) |
| **macOS** | `./AsteroidsGame` | SDL2 + SkiaSharp (GPU) |

The Linux and macOS builds **bundle** their native libraries (`libSDL2`, `libSkiaSharp`), so there is
nothing to install. If a build ever fails to find SDL2 at runtime, install it system-wide as a fallback:
`sudo apt install libsdl2-2.0-0` (Debian/Ubuntu) or `brew install sdl2` (macOS).

> **macOS (Apple Silicon):** binaries must be code-signed or the kernel silently kills them at launch
> (instant exit `137`, no output — this is *not* Gatekeeper quarantine). A build **packaged on a Mac**
> (`build/macos.sh`) is already ad-hoc signed and just runs. If you got an unsigned build, the `RUN.txt`
> in the folder has the two-line `codesign` fix.

---

## Build from source

You need the **.NET 8 SDK** (`dotnet --version` should print `8.x`). Everything else — SDL2, Skia — is
pulled in as NuGet packages, so there is nothing else to install for the SDL build. Per OS:

### Linux

```bash
sudo apt install dotnet-sdk-8.0            # or your distro's package / https://dotnet.microsoft.com
dotnet build AsteroidsOnSteroids.sln       # build everything
cd apps/Game.Sdl && dotnet run             # run (SDL2 + Skia)
```

### macOS

```bash
brew install dotnet-sdk                     # or the installer from https://dotnet.microsoft.com
dotnet build AsteroidsOnSteroids.sln
cd apps/Game.Sdl && dotnet run              # SDL2 + Skia; forces a GL 3.3 core context (needed on macOS)
```

Runs on both Apple-Silicon and Intel. (If a bare run ever has focus/menubar quirks, that's a
consequence of not being a signed `.app` bundle — packaging polish, not a code issue.)

### Windows

```powershell
# Install the .NET 8 SDK from https://dotnet.microsoft.com (or: winget install Microsoft.DotNet.SDK.8)
dotnet build AsteroidsOnSteroids.sln        # the cross-platform projects
cd apps\Game.Sdl; dotnet run                # SDL2 + Skia build, OR:

# The native Windows build (WinForms + GPU Skia) — needs the Windows Desktop SDK, which the
# Windows .NET 8 SDK includes. Not in the solution, so build it explicitly:
dotnet run --project apps\Game.WinForms
```

Windows can run **either** backend: the cross-platform **SDL** build (`apps/Game.Sdl`) or the native
**WinForms** build (`apps/Game.WinForms`, the shipped Windows target). Both render with GPU Skia.

> The two WinForms projects (`src/Platform.WinForms`, `apps/Game.WinForms`) are `net8.0-windows`, need the
> Windows Desktop SDK, and are deliberately **not** in `AsteroidsOnSteroids.sln` — so a Linux/macOS build
> never trips over them.

### Produce a distributable

A single-folder self-contained build (runnable in place, for testing):

```bash
build/linux.sh                 # → dist/linux-x64/     (or: build/linux.sh linux-arm64)
build/macos.sh                 # → dist/osx-arm64/     (or: build/macos.sh osx-x64  for Intel)
build/windows.ps1              # → dist/AsteroidsGame-win-x64\  (Windows only; also zips)
```

### Package for a GitHub release

`build/package.sh` publishes **and zips** the shippable builds, dropping a `RUN.txt` (per-OS launch
instructions) into each — the zips are ready to drag into a GitHub Release as assets:

```bash
build/package.sh                       # → dist/AsteroidsGame-{linux-x64,osx-arm64}.zip
build/package.sh linux-x64 osx-x64     # pick RIDs explicitly
build/windows.ps1                      # → dist/AsteroidsGame-win-x64.zip   (run on Windows)
```

Each zip unpacks to a self-descriptive `AsteroidsGame-<rid>/` folder containing the launcher, the bundled
runtime + natives, `Assets/`, and `RUN.txt`. **Don't commit the folders/zips** — `dist/` is gitignored;
distribute the zips as **Release assets** (not in the repo), so users download only their platform.
`dotnet publish -c Release -r <rid> --self-contained` does the heavy lifting; the Windows (WinForms) zip
must be produced on Windows.

> **Platform verification status:** **Linux** (SDL) and **Windows** (WinForms) builds are **tested and
> working**. The **macOS** (SDL, Apple Silicon) build **launches and runs** once ad-hoc signed —
> `build/macos.sh` now does this automatically. Full playthrough / visual rendering on macOS is worth an
> eyeball with the checklist below.

**macOS first-run checklist:** window opens fullscreen; WASD + mouse-aim + click-fire + Q/E/R/G + Esc
respond; menu → play → game-over → menu; HUD (timer/score, cooldown bars, ship widget, minimap) renders;
asteroids fracture per-cell; tracers/particles/starfield draw; `Z` toggles the profiler overlay; the
window closes cleanly.

---

## Repository layout

```
AsteroidsOnSteroids.sln     cross-platform projects (the net8.0-windows WinForms apps build separately)
Assets/                     game_config.json + shapes/*.json (loader walks up from the exe to find it)
src/    Engine  GameConfig  Gameplay  GameCore  Platform.Skia  Platform.Sdl  Platform.WinForms
apps/   Game.Sdl  Game.WinForms
tools/  Editor                     shape/config editor + sandbox
build/  linux.sh  macos.sh  windows.ps1
docs/                              design notes
```

See `CLAUDE.md` for the full architecture (ECS core, destruction engine, PAL, batched renderer).
