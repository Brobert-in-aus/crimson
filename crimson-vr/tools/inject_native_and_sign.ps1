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
    [string]$InputApk,
    [string]$OutputApk,
    [string]$AndroidSdk,
    [string]$BuildTools,
    [string]$Jdk,
    [string]$Keystore,
    [string]$KeyAlias = 'androiddebugkey',
    [string]$StorePassword = 'android',
    [string]$KeyPassword = 'android'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$InputApk = if ($InputApk) { $InputApk } else { Join-Path $repoRoot 'artifacts\CrimsonVR.apk' }
$OutputApk = if ($OutputApk) { $OutputApk } else { Join-Path $repoRoot 'artifacts\CrimsonVR.quest.apk' }
$InputApk = [System.IO.Path]::GetFullPath($InputApk)
$OutputApk = [System.IO.Path]::GetFullPath($OutputApk)
$so = Join-Path $repoRoot 'crimson-vr\godot\native\android-arm64\libcrimson_host.so'
$AndroidSdk = if ($AndroidSdk) { $AndroidSdk } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not $BuildTools) {
    $BuildTools = Get-ChildItem (Join-Path $AndroidSdk 'build-tools') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        Where-Object { Test-Path (Join-Path $_.FullName 'apksigner.bat') } |
        Select-Object -First 1 -ExpandProperty FullName
}
$Jdk = if ($Jdk) { $Jdk } elseif ($env:JAVA_HOME_17_X64) { $env:JAVA_HOME_17_X64 } elseif ($env:JAVA_HOME) { $env:JAVA_HOME } else {
    Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
$Keystore = if ($Keystore) { $Keystore } else { Join-Path $env:USERPROFILE '.android\debug.keystore' }
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$jar = Join-Path $jdk 'bin\jar.exe'
$env:JAVA_HOME = $jdk

if (-not (Test-Path $so)) { throw "native lib not found: $so (build with build_libcrimson.ps1 -android)" }
foreach ($item in ([ordered]@{
    'input APK' = $InputApk
    'Android zipalign' = $zipalign
    'Android apksigner' = $apksigner
    'JDK jar' = $jar
    'signing keystore' = $Keystore
}).GetEnumerator()) {
    if (-not $item.Value -or -not (Test-Path $item.Value)) { throw "missing $($item.Key): $($item.Value)" }
}

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
    --ks $Keystore --ks-pass "pass:$StorePassword" --ks-key-alias $KeyAlias --key-pass "pass:$KeyPassword" $OutputApk
if ($LASTEXITCODE -ne 0) { throw "apksigner failed ($LASTEXITCODE)" }
Write-Output "signed Quest APK: $OutputApk"
