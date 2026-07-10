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
    # Windows x64 (host). FAIL CLOSED: a native command's non-zero exit does NOT
    # trip $ErrorActionPreference in PS 5.1, so without this check a failed zig
    # build silently copies the STALE zig-out artifact (shipped a v10 .so in a
    # v11 APK once - 'ABI mismatch' only visible on-device).
    & $zig build host-lib
    if ($LASTEXITCODE -ne 0) { throw "zig build host-lib (win) failed ($LASTEXITCODE)" }
    New-Item -ItemType Directory -Force (Join-Path $godotNative 'win-x64') | Out-Null
    Copy-Item (Join-Path $zigDir 'zig-out\bin\crimson_host.dll') (Join-Path $godotNative 'win-x64\') -Force
    Write-Output "win-x64: crimson_host.dll -> $godotNative\win-x64"

    # Quest / Android arm64. Zig 0.16 ships bionic stubs, so no NDK is required
    # to produce the .so; the NDK is only needed for on-device readelf/robustness
    # work. The .so lands in zig-out/lib (not bin) for non-Windows targets.
    if ($args -contains '-android') {
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

        & $zig build host-lib -Dtarget=aarch64-linux-android "-Dandroid-libc=$libcFile"
        if ($LASTEXITCODE -ne 0) { throw "zig build host-lib (android) failed ($LASTEXITCODE)" }
        New-Item -ItemType Directory -Force (Join-Path $godotNative 'android-arm64') | Out-Null
        Copy-Item (Join-Path $zigDir 'zig-out\lib\libcrimson_host.so') (Join-Path $godotNative 'android-arm64\') -Force
        Write-Output "android-arm64: libcrimson_host.so -> $godotNative\android-arm64 (bionic libc via NDK $ndkRoot)"
    }
}
finally {
    Pop-Location
}
