# Launch CrimsonVR on PCVR (Virtual Desktop / VDXR).
#
# Prereq the SCRIPT can't do for you: the OpenXR runtime must be live.
#   1. Put on the Quest, start Virtual Desktop, connect to this PC.
#   2. In the VD app (or streamer), make sure VDXR (VirtualDesktopXR) is the
#      active OpenXR runtime.
# Then run this script. It builds the C# assembly and launches the project
# windowed; Godot brings up OpenXR and renders to the headset (the desktop
# window is the mirror). If no runtime is active it falls back to a flat window.
#
# Usage:  powershell -File crimson-vr/tools/run_pcvr.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $repoRoot 'crimson-vr\godot'
$dll  = Join-Path $proj 'native\win-x64\crimson_host.dll'
$godot = 'D:\Projects\CrimsonVR\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe'

if (-not (Test-Path $dll))   { throw "native crimson_host.dll missing: $dll (run tools/build_libcrimson.ps1)" }
if (-not (Test-Path $godot)) { throw "Godot editor not found: $godot" }

Write-Host "Building CrimsonVR.dll..." -ForegroundColor Cyan
& 'C:\Program Files\dotnet\dotnet.exe' build (Join-Path $proj 'CrimsonVR.csproj') -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

# Active OpenXR runtime (informational — VDXR must be selected in the VD app).
$active = (Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -Name ActiveRuntime -ErrorAction SilentlyContinue).ActiveRuntime
Write-Host "Active OpenXR runtime: $active" -ForegroundColor Yellow

Write-Host "Launching project (windowed + OpenXR)..." -ForegroundColor Cyan
& $godot --path $proj
