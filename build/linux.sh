#!/usr/bin/env bash
# Self-contained Linux build of the SDL2 + SkiaSharp game. Produces a folder that runs with no .NET
# install and no system SDL2/Skia — the native libs (libSDL2, libSkiaSharp) are bundled by the publish.
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-linux-x64}"                 # or linux-arm64
OUT="dist/$RID"

rm -rf "$OUT"
dotnet publish apps/Game.Sdl/AsteroidsGame.csproj -c Release -r "$RID" --self-contained -o "$OUT"
cp -r Assets "$OUT/Assets"            # loader finds Assets/ beside the exe

echo
echo "Published → $OUT   (run: ./$OUT/AsteroidsGame)"
