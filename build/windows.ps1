# Self-contained Windows build of the WinForms + SkiaSharp (GPU) game. Produces a folder that runs with
# no .NET install; native Skia is bundled via SkiaSharp.NativeAssets.Win32.
#
# UNVERIFIED off Windows: net8.0-windows needs the Windows Desktop SDK, so this only builds on Windows.
# Run it there and check the first-run checklist in apps/Game.WinForms/README.md.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$RID = if ($args.Count -ge 1) { $args[0] } else { "win-x64" }
$OUT = "dist/$RID"

if (Test-Path $OUT) { Remove-Item -Recurse -Force $OUT }
dotnet publish apps/Game.WinForms/AsteroidsGame.WinForms.csproj -c Release -r $RID --self-contained -o $OUT
Copy-Item -Recurse Assets (Join-Path $OUT "Assets")   # loader finds Assets\ beside the exe

Write-Host ""
Write-Host "Published -> $OUT   (run: $OUT\AsteroidsGame.WinForms.exe)"
