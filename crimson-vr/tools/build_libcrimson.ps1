# Builds libcrimson (crimson_host) and copies it into the Godot project's
# native/ dir (gitignored). Run from anywhere; requires Zig 0.16+ on PATH or
# at tools/zig/zig.exe in the repo root.

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$zigDir = Join-Path $repoRoot 'crimson-zig'
$godotNative = Join-Path $repoRoot 'crimson-vr\godot\native'

$zig = Join-Path $repoRoot 'tools\zig\zig.exe'
if (-not (Test-Path $zig)) { $zig = 'zig' }

Push-Location $zigDir
try {
    # Windows x64 (host)
    & $zig build host-lib
    New-Item -ItemType Directory -Force (Join-Path $godotNative 'win-x64') | Out-Null
    Copy-Item (Join-Path $zigDir 'zig-out\bin\crimson_host.dll') (Join-Path $godotNative 'win-x64\') -Force
    Write-Output "win-x64: crimson_host.dll -> $godotNative\win-x64"

    # Quest / Android arm64 (pass -AndroidNdk to enable once NDK libc paths are wired)
    if ($args -contains '-android') {
        & $zig build host-lib -Dtarget=aarch64-linux-android
        New-Item -ItemType Directory -Force (Join-Path $godotNative 'android-arm64') | Out-Null
        Copy-Item (Join-Path $zigDir 'zig-out\bin\libcrimson_host.so') (Join-Path $godotNative 'android-arm64\') -Force
        Write-Output "android-arm64: libcrimson_host.so -> $godotNative\android-arm64"
    }
}
finally {
    Pop-Location
}
