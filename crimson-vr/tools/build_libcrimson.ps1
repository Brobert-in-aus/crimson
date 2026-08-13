# Builds libcrimson (crimson_host) and copies it into the Godot project's
# native/ dir (gitignored). Run from anywhere; requires Zig 0.16+ on PATH or
# at tools/zig/zig.exe in the repo root.
#
#   build_libcrimson.ps1                    Windows x64
#   build_libcrimson.ps1 -Linux             Windows + Linux x64
#   build_libcrimson.ps1 -Android           Windows + Quest arm64
#   build_libcrimson.ps1 -Linux -Android    all distribution targets
#   build_libcrimson.ps1 -Optimize Debug    unoptimized, for a native debugger
#
# OPTIMIZE MATTERS A LOT and used to be left unset, which meant Debug: Zig's
# standardOptimizeOption defaults there, so the Quest shipped an unoptimized
# simulation for its whole life. It ran, because the sim is cheap per tick --
# what it showed up as was a stutter on leaving the score screen, where the
# replay encode walks every recorded tick at once and the per-tick cost finally
# lands in one frame.
#
# ReleaseSafe, not ReleaseFast: this is a deterministic simulation whose replays
# are verified by re-simulation, and the safety checks ReleaseFast removes are
# exactly the ones that would turn a silent integer overflow into a run that
# cannot be reproduced. Keep the checks; take the ~10x anyway.

param(
    [ValidateSet('Debug', 'ReleaseSafe', 'ReleaseFast', 'ReleaseSmall')]
    [string]$Optimize = 'ReleaseSafe',
    [switch]$Linux,
    [switch]$Android
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$zigDir = Join-Path $repoRoot 'crimson-zig'
$godotNative = Join-Path $repoRoot 'crimson-vr\godot\native'

$zig = Join-Path $repoRoot 'tools\zig\zig.exe'
if (-not (Test-Path $zig)) { $zig = 'zig' }

Push-Location $zigDir
try {
    # Windows x64 (host). FAIL CLOSED: a native command's non-zero exit does NOT
    # trip $ErrorActionPreference in PS 5.1, so without this check a failed zig
    # build silently copies the STALE zig-out artifact (shipped a v10 .so in a
    # v11 APK once - 'ABI mismatch' only visible on-device).
    & $zig build host-lib "-Doptimize=$Optimize"
    if ($LASTEXITCODE -ne 0) { throw "zig build host-lib (win) failed ($LASTEXITCODE)" }
    New-Item -ItemType Directory -Force (Join-Path $godotNative 'win-x64') | Out-Null
    Copy-Item (Join-Path $zigDir 'zig-out\bin\crimson_host.dll') (Join-Path $godotNative 'win-x64\') -Force
    Write-Output "win-x64: crimson_host.dll -> $godotNative\win-x64 ($Optimize)"

    if ($Linux) {
        & $zig build host-lib -Dtarget=x86_64-linux-gnu "-Doptimize=$Optimize"
        if ($LASTEXITCODE -ne 0) { throw "zig build host-lib (linux) failed ($LASTEXITCODE)" }
        New-Item -ItemType Directory -Force (Join-Path $godotNative 'linux-x64') | Out-Null
        Copy-Item (Join-Path $zigDir 'zig-out\lib\libcrimson_host.so') (Join-Path $godotNative 'linux-x64\') -Force
        Write-Output "linux-x64: libcrimson_host.so -> $godotNative\linux-x64 ($Optimize)"
    }

    # Quest / Android arm64. Zig 0.16 ships bionic stubs, so no NDK is required
    # to produce the .so; the NDK is only needed for on-device readelf/robustness
    # work. The .so lands in zig-out/lib (not bin) for non-Windows targets.
    if ($Android) {
        # The .so must link bionic libc (proper TLS/pthread/getauxval) or it fails
        # to dlopen / aborts on Quest. Point Zig at the NDK's bionic via a libc file.
        $ndkRoot = $env:ANDROID_NDK_ROOT
        if (-not $ndkRoot -or -not (Test-Path $ndkRoot)) {
            $ndkBase = Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'
            $ndkRoot = (Get-ChildItem -Directory $ndkBase | Sort-Object Name | Select-Object -Last 1).FullName
        }
        if (-not $ndkRoot -or -not (Test-Path $ndkRoot)) { throw "Android NDK not found (set ANDROID_NDK_ROOT)" }
        $sysroot = Join-Path $ndkRoot 'toolchains\llvm\prebuilt\windows-x86_64\sysroot'
        $api = 29 # matches the export minSdk
        $crtDir = Join-Path $sysroot "usr\lib\aarch64-linux-android\$api"
        if (-not (Test-Path $crtDir)) { throw "NDK crt dir not found: $crtDir" }
        $inc = (Join-Path $sysroot 'usr\include')
        $libcFile = Join-Path $zigDir 'zig-out\android-libc.txt'
        New-Item -ItemType Directory -Force (Split-Path $libcFile) | Out-Null
        # LF line endings only: a trailing CR corrupts the paths Zig parses.
        $libcLines = @(
            "include_dir=$inc"
            "sys_include_dir=$inc"
            "crt_dir=$crtDir"
            "msvc_lib_dir="
            "kernel32_lib_dir="
            "gcc_dir="
        )
        [System.IO.File]::WriteAllText($libcFile, ($libcLines -join "`n") + "`n")

        & $zig build host-lib -Dtarget=aarch64-linux-android "-Dandroid-libc=$libcFile" "-Doptimize=$Optimize"
        if ($LASTEXITCODE -ne 0) { throw "zig build host-lib (android) failed ($LASTEXITCODE)" }
        New-Item -ItemType Directory -Force (Join-Path $godotNative 'android-arm64') | Out-Null
        Copy-Item (Join-Path $zigDir 'zig-out\lib\libcrimson_host.so') (Join-Path $godotNative 'android-arm64\') -Force
        Write-Output "android-arm64: libcrimson_host.so -> $godotNative\android-arm64 ($Optimize, bionic libc via NDK $ndkRoot)"
    }
}
finally {
    Pop-Location
}
