#!/usr/bin/env bash
# Self-contained macOS build of the SDL2 + SkiaSharp game. Defaults to Apple Silicon (osx-arm64);
# pass osx-x64 for Intel Macs. The native libs are bundled by the publish.
#
# UNVERIFIED: authored on Linux. Run on a Mac and check the first-run checklist in the top-level README.
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-osx-arm64}"                 # or osx-x64 for Intel Macs
OUT="dist/$RID"

rm -rf "$OUT"
dotnet publish apps/Game.Sdl/AsteroidsGame.csproj -c Release -r "$RID" --self-contained -o "$OUT"
cp -r Assets "$OUT/Assets"

echo
echo "Published → $OUT   (run: ./$OUT/AsteroidsGame)"
echo "If it can't find SDL2 at runtime, install it system-wide as a fallback: brew install sdl2"
