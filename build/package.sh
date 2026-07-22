#!/usr/bin/env bash
# Publish + package the SDL builds into ready-to-share zips (one per RID) for a GitHub release.
# Each zip contains the self-contained game (.NET runtime + SDL2 + Skia all bundled), the Assets/
# folder, and a RUN.txt with per-OS launch instructions.
#
#   build/package.sh                      # defaults: linux-x64 osx-arm64
#   build/package.sh linux-x64 osx-x64    # pick RIDs explicitly
#
# Cross-publishable from Linux/macOS: linux-x64, linux-arm64, osx-arm64, osx-x64.
# The Windows (WinForms) build must be packaged ON Windows — run build/windows.ps1 there.
set -euo pipefail
cd "$(dirname "$0")/.."

RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(linux-x64 osx-arm64)

mkdir -p dist

write_runtxt() {
  local rid="$1" dir="$2"
  case "$rid" in
    linux-*)
      cat > "$dir/RUN.txt" <<'EOF'
Asteroids on Steroids — Linux
=============================

1. Open a terminal in this folder.
2. Make the launcher executable (unzipping usually drops the exec bit):
       chmod +x AsteroidsGame
3. Run it:
       ./AsteroidsGame

Nothing to install: SDL2, Skia and the .NET runtime are all bundled in this folder.
It needs only a normal desktop Linux (OpenGL / libGL + libfontconfig, present by default).

Keep the Assets/ folder next to AsteroidsGame.

Controls: WASD thrust · mouse aim · left-click fire · Q/E/R skills · G grenade · Esc pause/quit.
EOF
      ;;
    osx-*)
      cat > "$dir/RUN.txt" <<'EOF'
Asteroids on Steroids — macOS
=============================

macOS blocks unsigned downloaded apps (Gatekeeper). In a terminal in this folder:

1. Clear the "downloaded from the internet" quarantine flag:
       xattr -dr com.apple.quarantine .
2. Make the launcher executable:
       chmod +x AsteroidsGame
3. Run it:
       ./AsteroidsGame

(First launch alternative: right-click AsteroidsGame in Finder -> Open -> Open.)

Nothing to install: SDL2, Skia and the .NET runtime are all bundled in this folder.
Keep the Assets/ folder next to AsteroidsGame.

Controls: WASD thrust · mouse aim · left-click fire · Q/E/R skills · G grenade · Esc pause/quit.
EOF
      ;;
    *)
      echo "!! $rid is not a Unix RID this script can cross-publish (Windows uses build/windows.ps1)"; return 1 ;;
  esac
}

for RID in "${RIDS[@]}"; do
  NAME="AsteroidsGame-$RID"          # the folder the user sees after unzipping
  OUT="dist/$NAME"
  echo ">>> publishing $RID"
  rm -rf "$OUT"
  dotnet publish apps/Game.Sdl/AsteroidsGame.csproj -c Release -r "$RID" --self-contained -o "$OUT" >/dev/null
  cp -r Assets "$OUT/Assets"
  write_runtxt "$RID" "$OUT"
  ( cd dist && rm -f "$NAME.zip" && zip -rq "$NAME.zip" "$NAME" )
  echo "    -> dist/$NAME.zip"
done

echo
echo "Done. Upload the dist/AsteroidsGame-*.zip files as GitHub release assets."
echo "Windows (win-x64) is a separate WinForms build — run build/windows.ps1 on Windows."
