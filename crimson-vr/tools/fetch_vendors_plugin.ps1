# Fetches the Godot OpenXR Vendors plugin addon into the Godot project.
# The addon (~46 MB of prebuilt per-vendor libs/AARs) is gitignored; run this
# after a fresh clone to restore it. Pin the version here to match Godot.

param([string]$Version = '5.1.0-stable')

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$addonsDir = Join-Path $repoRoot 'crimson-vr\godot\addons'
$tmpZip = Join-Path $env:TEMP "oxr_vendors_$Version.zip"
$tmpDir = Join-Path $env:TEMP "oxr_vendors_$Version"

$url = "https://github.com/GodotVR/godot_openxr_vendors/releases/download/$Version/godotopenxrvendorsaddon.zip"
Write-Output "Fetching $url"
curl.exe -sL -o $tmpZip $url
Expand-Archive $tmpZip $tmpDir -Force
New-Item -ItemType Directory -Force $addonsDir | Out-Null
Copy-Item (Join-Path $tmpDir 'asset\addons\godotopenxrvendors') $addonsDir -Recurse -Force
Write-Output "Installed godotopenxrvendors $Version -> $addonsDir"
