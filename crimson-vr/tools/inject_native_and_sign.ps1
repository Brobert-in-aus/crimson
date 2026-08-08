# Post-processes a Godot-exported (gradle) Android APK to bundle the
# crimson_host native library into lib/arm64-v8a/, then re-aligns and re-signs
# it for sideloading on Quest.
#
# Usage: inject_native_and_sign.ps1 <exported.apk> <output.apk>
# Requires: JDK (jar), Android build-tools (zipalign, apksigner), debug keystore.
#
# Notes learned the hard way:
#  - Add the .so with `jar uf0` (STORED/uncompressed). Do NOT use .NET
#    System.IO.Compression.ZipArchive — its full-archive rewrite produces a zip
#    the Quest installer rejects (INSTALL_FAILED_INVALID_APK: Failed to extract
#    native libraries, res=-2) for gradle APKs (extractNativeLibs=false).
#  - Quest runs a 16 KB-page OS, so align native libs with `zipalign -P 16`
#    (4 KB alignment installs fine on desktop but fails on Quest).
#  - Sign v2/v3 only; injecting after Godot's signing invalidates it, and
#    leaving stale v1 signature files around breaks v1 verification.

param(
    [string]$InputApk = 'D:\Projects\games-xr\crimson\artifacts\CrimsonVR.apk',
    [string]$OutputApk = 'D:\Projects\games-xr\crimson\artifacts\CrimsonVR.quest.apk'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$so = Join-Path $repoRoot 'crimson-vr\godot\native\android-arm64\libcrimson_host.so'
$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$buildTools = Join-Path $sdk 'build-tools\35.0.0'
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$keystore = Join-Path $env:USERPROFILE '.android\debug.keystore'
$jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-17*' | Select-Object -First 1 -ExpandProperty FullName
$jar = Join-Path $jdk 'bin\jar.exe'
$env:JAVA_HOME = $jdk

if (-not (Test-Path $so)) { throw "native lib not found: $so (build with build_libcrimson.ps1 -android)" }

# Stage the .so at its APK-relative path in a temp dir (jar preserves the path).
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("apkinject_" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $staging 'lib\arm64-v8a') | Out-Null
Copy-Item $so (Join-Path $staging 'lib\arm64-v8a\libcrimson_host.so') -Force

$work = [System.IO.Path]::ChangeExtension($OutputApk, '.work.apk')
Copy-Item $InputApk $work -Force

Push-Location $staging
try {
    & $jar uf0 $work -C $staging 'lib/arm64-v8a/libcrimson_host.so'
    if ($LASTEXITCODE -ne 0) { throw "jar add failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
    Remove-Item $staging -Recurse -Force
}

# Align (16 KB pages for Quest), then sign (v2/v3 only).
if (Test-Path $OutputApk) { Remove-Item $OutputApk -Force }
& $zipalign -P 16 -f 4 $work $OutputApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed ($LASTEXITCODE)" }
Remove-Item $work -Force
& $apksigner sign --v1-signing-enabled false --v2-signing-enabled true --v3-signing-enabled true `
    --ks $keystore --ks-pass pass:android --ks-key-alias androiddebugkey --key-pass pass:android $OutputApk
if ($LASTEXITCODE -ne 0) { throw "apksigner failed ($LASTEXITCODE)" }
Write-Output "signed Quest APK: $OutputApk"
