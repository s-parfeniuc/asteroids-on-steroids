#!/usr/bin/env bash
# Self-contained macOS build of the SDL2 + SkiaSharp game. Defaults to Apple Silicon (osx-arm64);
# pass osx-x64 for Intel Macs. The native libs (SDL2, Skia) + .NET runtime are bundled by the publish.
#
# RUN THIS ON A MAC. Apple Silicon SIGKILLs any unsigned or signature-mismatched Mach-O at page-in
# (a silent exit 137, no output), so this script ad-hoc code-signs the launcher + every bundled dylib
# and verifies them. codesign is macOS-only — cross-publishing from Linux produces an UNSIGNED build
# that will not run on Apple Silicon.
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-osx-arm64}"                 # or osx-x64 for Intel Macs
OUT="dist/$RID"

rm -rf "$OUT"
dotnet publish apps/Game.Sdl/AsteroidsGame.csproj -c Release -r "$RID" --self-contained -o "$OUT"
cp -r Assets "$OUT/Assets"

if command -v codesign >/dev/null 2>&1; then
  echo "ad-hoc signing launcher + dylibs…"
  find "$OUT" \( -name '*.dylib' -o -name AsteroidsGame -o -name createdump \) -type f \
    -exec codesign --force --sign - {} \; 2>/dev/null
  find "$OUT" \( -name '*.dylib' -o -name AsteroidsGame \) -type f -print0 \
    | xargs -0 -n1 codesign --verify \
    && echo "signatures valid." \
    || { echo "!! signature verification FAILED — a binary may be corrupt (re-publish)"; exit 1; }
else
  echo "!! codesign not found — you are not on macOS. This build is UNSIGNED and will be SIGKILLed on"
  echo "   Apple Silicon. Re-run this script on a Mac to produce a runnable build."
fi

echo
echo "Published → $OUT   (run: ./$OUT/AsteroidsGame)"
