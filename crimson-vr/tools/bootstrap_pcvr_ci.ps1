# Restores the pinned Godot editor and desktop export templates on a clean
# Windows machine. Used by CI and intentionally useful from a local checkout.

param(
    [string]$CacheDir,
    [string]$GodotVersion = '4.7-stable',
    [string]$GodotEditorSha256 = '73087f2ef4940be2c0bff358280053912182aca82b85891d6e42d9ebc5c26880',
    [string]$GodotTemplatesSha256 = '4c02a0b99ad9c5bc243c2e79468628db3df89350d9db8fc995988f69d126e069'
)

$ErrorActionPreference = 'Stop'
$CacheDir = if ($CacheDir) { $CacheDir } elseif ($env:RUNNER_TEMP) {
    Join-Path $env:RUNNER_TEMP 'crimsonvr-pcvr-cache'
} else {
    Join-Path ([System.IO.Path]::GetTempPath()) 'crimsonvr-pcvr-cache'
}
New-Item -ItemType Directory -Force $CacheDir | Out-Null

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
$godotBase = "https://github.com/godotengine/godot-builds/releases/download/$GodotVersion"
Get-PinnedFile "$godotBase/Godot_v${GodotVersion}_mono_win64.zip" $editorZip $GodotEditorSha256
Get-PinnedFile "$godotBase/Godot_v${GodotVersion}_mono_export_templates.tpz" $templatesTpz $GodotTemplatesSha256

$editorDir = Join-Path $CacheDir "Godot_v${GodotVersion}_mono_win64"
if (-not (Test-Path $editorDir)) { Expand-Archive $editorZip $CacheDir -Force }
$godot = Get-ChildItem $editorDir -Filter '*console.exe' -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $godot) { throw "Godot console executable missing after extracting $editorZip" }

$templateVersion = ($GodotVersion -replace '-', '.') + '.mono'
$templateDir = Join-Path $env:APPDATA "Godot\export_templates\$templateVersion"
New-Item -ItemType Directory -Force $templateDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($templatesTpz)
try {
    foreach ($name in @('windows_release_x86_64.exe', 'linux_release.x86_64', 'version.txt')) {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "templates/$name" } |
            Select-Object -First 1
        if (-not $entry) { throw "export template entry missing: templates/$name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
            $entry, (Join-Path $templateDir $name), $true)
    }
}
finally { $archive.Dispose() }

Write-Host "PCVR CI toolchain ready:"
Write-Host "  Godot:     $godot"
Write-Host "  templates: $templateDir"
if ($env:GITHUB_OUTPUT) { Add-Content $env:GITHUB_OUTPUT "godot=$godot" }
