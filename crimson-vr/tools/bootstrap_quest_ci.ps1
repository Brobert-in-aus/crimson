# Restores the pinned Quest build toolchain on a clean Windows machine.
# Intended for GitHub Actions, but deliberately usable from a local checkout.

param(
    [string]$CacheDir,
    [string]$GodotVersion = '4.7-stable',
    [string]$GodotEditorSha256 = '73087f2ef4940be2c0bff358280053912182aca82b85891d6e42d9ebc5c26880',
    [string]$GodotTemplatesSha256 = '4c02a0b99ad9c5bc243c2e79468628db3df89350d9db8fc995988f69d126e069',
    [string]$VendorsVersion = '5.1.0-stable',
    [string]$VendorsSha256 = '6a838dbdf4115549e4511ebee0da9a5dcc8f9f6258d4cc2f2ee57a907a3e2911',
    [string]$AndroidSdk,
    [string]$Jdk,
    [string]$Keystore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $repoRoot 'crimson-vr\godot'
$CacheDir = if ($CacheDir) { $CacheDir } elseif ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'crimsonvr-quest-cache' } else { Join-Path ([System.IO.Path]::GetTempPath()) 'crimsonvr-quest-cache' }
$AndroidSdk = if ($AndroidSdk) { $AndroidSdk } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$Jdk = if ($Jdk) { $Jdk } elseif ($env:JAVA_HOME_17_X64) { $env:JAVA_HOME_17_X64 } elseif ($env:JAVA_HOME) { $env:JAVA_HOME } else { $null }
$Keystore = if ($Keystore) { $Keystore } else { Join-Path $repoRoot 'artifacts\crimsonvr-ci.keystore' }

if (-not $Jdk -or -not (Test-Path $Jdk)) { throw "JDK 17 not found; pass -Jdk or set JAVA_HOME_17_X64" }
if (-not (Test-Path $AndroidSdk)) { throw "Android SDK not found: $AndroidSdk" }

New-Item -ItemType Directory -Force $CacheDir | Out-Null
New-Item -ItemType Directory -Force (Split-Path $Keystore) | Out-Null

function Get-PinnedFile {
    param([string]$Url, [string]$Path, [string]$Sha256)
    if (-not (Test-Path $Path) -or (Get-FileHash $Path -Algorithm SHA256).Hash -ne $Sha256) {
        Write-Host "Downloading $Url"
        $partial = "$Path.partial"
        & curl.exe --fail --location --retry 3 --output $partial $Url
        if ($LASTEXITCODE -ne 0) { throw "download failed ($LASTEXITCODE): $Url" }
        Move-Item $partial $Path -Force
    }
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path (expected $Sha256, got $actual)"
    }
}

$editorZip = Join-Path $CacheDir "Godot_v${GodotVersion}_mono_win64.zip"
$templatesTpz = Join-Path $CacheDir "Godot_v${GodotVersion}_mono_export_templates.tpz"
$vendorsZip = Join-Path $CacheDir "godotopenxrvendors-$VendorsVersion.zip"
$godotBase = "https://github.com/godotengine/godot-builds/releases/download/$GodotVersion"
Get-PinnedFile "$godotBase/Godot_v${GodotVersion}_mono_win64.zip" $editorZip $GodotEditorSha256
Get-PinnedFile "$godotBase/Godot_v${GodotVersion}_mono_export_templates.tpz" $templatesTpz $GodotTemplatesSha256
Get-PinnedFile "https://github.com/GodotVR/godot_openxr_vendors/releases/download/$VendorsVersion/godotopenxrvendorsaddon.zip" $vendorsZip $VendorsSha256

$editorDir = Join-Path $CacheDir "Godot_v${GodotVersion}_mono_win64"
if (-not (Test-Path $editorDir)) { Expand-Archive $editorZip $CacheDir -Force }
$godot = Get-ChildItem $editorDir -Filter '*console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $godot) { throw "Godot console executable missing after extracting $editorZip" }

# A full Mono TPZ is over 1 GB. Quest CI only needs the Android entries.
$templateVersion = ($GodotVersion -replace '-', '.') + '.mono'
$templateDir = Join-Path $env:APPDATA "Godot\export_templates\$templateVersion"
New-Item -ItemType Directory -Force $templateDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($templatesTpz)
try {
    foreach ($name in @('android_debug.apk', 'android_release.apk', 'android_source.zip', 'version.txt')) {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "templates/$name" } | Select-Object -First 1
        if (-not $entry) { throw "export template entry missing: templates/$name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $templateDir $name), $true)
    }
}
finally { $archive.Dispose() }

$vendorsStage = Join-Path $CacheDir "vendors-$VendorsVersion"
if (Test-Path $vendorsStage) { Remove-Item $vendorsStage -Recurse -Force }
Expand-Archive $vendorsZip $vendorsStage -Force
$vendorsSource = Join-Path $vendorsStage 'asset\addons\godotopenxrvendors'
$addonsDir = Join-Path $project 'addons'
New-Item -ItemType Directory -Force $addonsDir | Out-Null
Copy-Item $vendorsSource $addonsDir -Recurse -Force

if (-not (Test-Path $Keystore)) {
    $keytool = Join-Path $Jdk 'bin\keytool.exe'
    & $keytool -genkeypair -keystore $Keystore -storepass android -alias androiddebugkey `
        -keypass android -dname 'CN=CrimsonVR Personal Build, OU=Quest CI, O=CrimsonVR, C=AU' `
        -keyalg RSA -keysize 2048 -validity 10000
    if ($LASTEXITCODE -ne 0) { throw "keytool failed ($LASTEXITCODE)" }
}

# Godot stores Android SDK/JDK locations in per-user editor settings.
$settingsDir = Join-Path $env:APPDATA 'Godot'
$settings = Join-Path $settingsDir 'editor_settings-4.7.tres'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$slashJdk = $Jdk.Replace('\', '/')
$slashSdk = $AndroidSdk.Replace('\', '/')
$slashKey = $Keystore.Replace('\', '/')
$settingsText = @"
[gd_resource type="EditorSettings" format=3]

[resource]
export/android/java_sdk_path = "$slashJdk"
export/android/android_sdk_path = "$slashSdk"
export/android/debug_keystore = "$slashKey"
export/android/debug_keystore_pass = "android"
"@
[System.IO.File]::WriteAllText($settings, $settingsText.Replace("`r`n", "`n"))

Write-Host "Quest CI toolchain ready:"
Write-Host "  Godot:     $godot"
Write-Host "  templates: $templateDir"
Write-Host "  vendors:   $vendorsSource"
Write-Host "  keystore:  $Keystore"

if ($env:GITHUB_OUTPUT) {
    Add-Content $env:GITHUB_OUTPUT "godot=$godot"
    Add-Content $env:GITHUB_OUTPUT "keystore=$Keystore"
    Add-Content $env:GITHUB_OUTPUT "android_sdk=$AndroidSdk"
    Add-Content $env:GITHUB_OUTPUT "jdk=$Jdk"
}
