# Post-processes a Godot-exported Android APK to bundle the crimson_host
# native library into lib/arm64-v8a/ (Godot's export does not carry P/Invoke
# natives), then re-aligns and re-signs it for sideloading.
#
# Usage: inject_native_and_sign.ps1 <exported.apk> <output.apk>
# Requires: Android build-tools (zipalign, apksigner), a JDK, and the debug
# keystore at ~/.android/debug.keystore.

param(
    [string]$InputApk = 'D:\Projects\crimson\artifacts\CrimsonVR.apk',
    [string]$OutputApk = 'D:\Projects\crimson\artifacts\CrimsonVR.quest.apk'
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
$env:JAVA_HOME = $jdk

if (-not (Test-Path $so)) { throw "native lib not found: $so (run build_libcrimson.ps1 -android)" }

$work = [System.IO.Path]::ChangeExtension($OutputApk, '.work.apk')
Copy-Item $InputApk $work -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($work, 'Update')
try {
    # Strip Godot's signature (adding a file invalidates it; apksigner re-signs).
    @($zip.Entries | Where-Object { $_.FullName -match '^META-INF/.*\.(SF|RSA|DSA|EC)$' }) |
        ForEach-Object { $_.Delete() }
    # Add/replace the native lib.
    $existing = $zip.GetEntry('lib/arm64-v8a/libcrimson_host.so')
    if ($existing) { $existing.Delete() }
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $so, 'lib/arm64-v8a/libcrimson_host.so')
}
finally {
    $zip.Dispose()
}

# Align, then sign (order matters: zipalign must precede apksigner).
if (Test-Path $OutputApk) { Remove-Item $OutputApk -Force }
& $zipalign -f 4 $work $OutputApk
Remove-Item $work -Force
& $apksigner sign --ks $keystore --ks-pass pass:android --ks-key-alias androiddebugkey --key-pass pass:android $OutputApk
& $apksigner verify --print-certs $OutputApk | Select-Object -First 2
Write-Output "signed APK: $OutputApk"
