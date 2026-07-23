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
> working**. **macOS** (SDL) is written correct-by-construction but **not yet run on hardware** — build it
> on a Mac and use the first-run checklist below to confirm.

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
