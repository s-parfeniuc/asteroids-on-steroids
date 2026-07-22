# Publish + package the WinForms + SkiaSharp (GPU) game into a ready-to-share zip for a GitHub release.
# Self-contained: the .NET runtime + native Skia are bundled; no install needed on the target PC.
#
# UNVERIFIED off Windows: net8.0-windows needs the Windows Desktop SDK, so this only runs on Windows.
# Check the first-run checklist in apps/Game.WinForms/README.md on first launch.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$RID  = if ($args.Count -ge 1) { $args[0] } else { "win-x64" }
$NAME = "AsteroidsGame-$RID"          # the folder the user sees after unzipping
$OUT  = "dist/$NAME"

if (Test-Path $OUT) { Remove-Item -Recurse -Force $OUT }
dotnet publish apps/Game.WinForms/AsteroidsGame.WinForms.csproj -c Release -r $RID --self-contained -o $OUT
Copy-Item -Recurse Assets (Join-Path $OUT "Assets")   # loader finds Assets\ beside the exe

$run = @"
Asteroids on Steroids — Windows
===============================

Double-click  AsteroidsGame.WinForms.exe  to play.

The first time, Windows SmartScreen may warn "unknown publisher" (the exe is unsigned):
click  More info  ->  Run anyway.

Nothing to install: the .NET runtime and Skia are bundled in this folder.
Keep the Assets\ folder next to the exe.

Controls: WASD thrust - mouse aim - left-click fire - Q/E/R skills - G grenade - Esc pause/quit.
"@
Set-Content -Path (Join-Path $OUT "RUN.txt") -Value $run

$zip = "dist/$NAME.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $OUT -DestinationPath $zip

Write-Host ""
Write-Host "Done -> $zip   (upload as a GitHub release asset)"
Write-Host "Run locally: $OUT\AsteroidsGame.WinForms.exe"
