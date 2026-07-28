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

In a terminal in this folder:

1. Clear the "downloaded from the internet" quarantine flag:
       xattr -dr com.apple.quarantine .
2. Make the launcher executable:
       chmod +x AsteroidsGame
3. Run it:
       ./AsteroidsGame

Nothing to install: SDL2, Skia and the .NET runtime are all bundled in this folder.
Keep the Assets/ folder next to AsteroidsGame.

--- If it exits instantly with NO output (exit code 137) ---
That is NOT quarantine — it is code signing. Apple Silicon kills any unsigned or altered binary
the moment it loads, silently. Re-sign the launcher and the bundled libraries in place:

       codesign --force --sign - AsteroidsGame
       find . -name '*.dylib' -exec codesign --force --sign - {} \;

then run ./AsteroidsGame again. (A build packaged on a Mac is already signed and skips this.)

Controls: WASD thrust · mouse aim · left-click fire · Q/E/R skills · G grenade · Esc pause/quit.
EOF
      ;;
    *)
      echo "!! $rid is not a Unix RID this script can cross-publish (Windows uses build/windows.ps1)"; return 1 ;;
  esac
}

# Apple Silicon SIGKILLs any unsigned or signature-mismatched Mach-O at page-in (a silent exit 137, no
# output). Cross-publishing from Linux leaves the launcher unsigned and can leave stale dylib signatures,
# so ad-hoc re-sign the launcher + every dylib to match their on-disk bytes, then verify. codesign is
# macOS-only, so this can ONLY run on a Mac — on Linux we warn loudly that the osx build must be signed
# on a Mac before it will run.
sign_macos() {
  local dir="$1"
  if ! command -v codesign >/dev/null 2>&1; then
    echo "    !! NOT on macOS — this osx build is UNSIGNED and will be SIGKILLed on Apple Silicon."
    echo "       Re-run 'build/package.sh $RID' (or build/macos.sh) ON A MAC to sign it before shipping."
    return
  fi
  find "$dir" \( -name '*.dylib' -o -name AsteroidsGame -o -name createdump \) -type f \
    -exec codesign --force --sign - {} \; 2>/dev/null
  if find "$dir" \( -name '*.dylib' -o -name AsteroidsGame \) -type f -print0 \
       | xargs -0 -n1 codesign --verify >/dev/null 2>&1; then
    echo "    ad-hoc signed + verified"
  else
    echo "    !! signature verification FAILED — a binary may be corrupt (re-download / re-publish)"; exit 1
  fi
}

for RID in "${RIDS[@]}"; do
  NAME="AsteroidsGame-$RID"          # the folder the user sees after unzipping
  OUT="dist/$NAME"
  echo ">>> publishing $RID"
  rm -rf "$OUT"
  dotnet publish apps/Game.Sdl/AsteroidsGame.csproj -c Release -r "$RID" --self-contained -o "$OUT" >/dev/null
  cp -r Assets "$OUT/Assets"
  case "$RID" in osx-*) sign_macos "$OUT" ;; esac
  write_runtxt "$RID" "$OUT"
  ( cd dist && rm -f "$NAME.zip" && zip -rq "$NAME.zip" "$NAME" )
  echo "    -> dist/$NAME.zip"
done

echo
echo "Done. Upload the dist/AsteroidsGame-*.zip files as GitHub release assets."
echo "Windows (win-x64) is a separate WinForms build — run build/windows.ps1 on Windows."
