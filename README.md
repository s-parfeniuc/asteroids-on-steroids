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
and run the executable. Keep the `Assets/` folder next to the executable (the scripts place it there).

| OS | Executable | Renderer |
|----|-----------|----------|
| **Windows** 10/11 | `AsteroidsGame.WinForms.exe` | WinForms + SkiaSharp (GPU) |
| **Linux** | `./AsteroidsGame` | SDL2 + SkiaSharp (GPU) |
| **macOS** | `./AsteroidsGame` | SDL2 + SkiaSharp (GPU) |

The Linux and macOS builds **bundle** their native libraries (`libSDL2`, `libSkiaSharp`), so there is
nothing to install. If a build ever fails to find SDL2 at runtime, install it system-wide as a fallback:
`sudo apt install libsdl2-2.0-0` (Debian/Ubuntu) or `brew install sdl2` (macOS).

---

## Build from source

Requires the **.NET 8 SDK**. From the repo root:

```bash
# Everything cross-platform (Linux/macOS + Windows) — the source of truth:
dotnet build AsteroidsOnSteroids.sln

# Run it (SDL2 + Skia):
cd apps/Game.Sdl && dotnet run
```

The two WinForms projects (`src/Platform.WinForms`, `apps/Game.WinForms`) are `net8.0-windows` and are
**not** in the solution — they need the Windows Desktop SDK and build only on Windows.

### Produce a distributable

```bash
build/linux.sh                 # → dist/linux-x64/     (or: build/linux.sh linux-arm64)
build/macos.sh                 # → dist/osx-arm64/     (or: build/macos.sh osx-x64  for Intel)
build/windows.ps1              # → dist/win-x64\       (Windows only; WinForms build)
```

Each script runs `dotnet publish -c Release -r <rid> --self-contained` and copies `Assets/` beside the
executable. Output goes to `dist/` (gitignored).

> **Platform verification status:** the **Linux** build is the tested source of truth. The **Windows**
> (WinForms) and **macOS** (SDL) targets are written correct-by-construction but are **unverified** —
> compile and run them on that hardware and use the first-run checklists (`apps/Game.WinForms/README.md`
> for Windows; below for macOS) to confirm.

**macOS / Windows first-run checklist:** window opens fullscreen; WASD + mouse-aim + click-fire + Q/E/R/G
+ Esc respond; menu → play → game-over → menu; HUD (timer/score, cooldown bars, ship widget, minimap)
renders; asteroids fracture per-cell; tracers/particles/starfield draw; `Z` toggles the profiler overlay;
the window closes cleanly.

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
