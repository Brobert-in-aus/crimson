# One-shot Quest APK pipeline: gradle Android export (headless) -> inject the
# arm64 crimson_host native lib -> zipalign + sign -> verify payload -> optional
# adb install.
# Wraps the two manual steps (Godot export, inject_native_and_sign.ps1) that
# ship a sideloadable Quest build.
#
# Works around a Godot quirk: `--headless --export-debug` finishes the export
# (the APK is fully written and the log prints `[ DONE ] export`) but the process
# then never returns — it errors reading the `export/android/shutdown_adb_on_exit`
# editor setting during teardown (EditorSettings doesn't exist in headless) and
# hangs. There is no clean-exit CLI flag for editor-mode export, so instead of
# waiting (a 10-minute timeout previously), we watch the export log for the
# `[ DONE ] export` marker, confirm the APK, then terminate Godot ourselves —
# turning the run into ~export time (a few minutes).
#
# Usage:
#   pwsh -File crimson-vr/tools/build_quest.ps1 [-Install] [-Device <ip:port>]
#     -Install        adb install -r the signed APK to -Device afterward
#     -Device         wireless adb target (default 192.168.8.100:5555)
#     -ExportTimeoutSec  hard cap on the export wait (default 600)

param(
    [switch]$Install,
    [string]$Device = '192.168.8.100:5555',
    [int]$ExportTimeoutSec = 600,
    [string]$Godot = 'D:\Projects\CrimsonVR\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $repoRoot 'crimson-vr\godot'
$apk = Join-Path $repoRoot 'artifacts\CrimsonVR.apk'
$questApk = Join-Path $repoRoot 'artifacts\CrimsonVR.quest.apk'
$inject = Join-Path $PSScriptRoot 'inject_native_and_sign.ps1'

if (-not (Test-Path $Godot)) { throw "Godot not found: $Godot" }
if (-not (Test-Path $inject)) { throw "inject script not found: $inject" }
New-Item -ItemType Directory -Force (Split-Path $apk) | Out-Null

# A Godot editor open on this project would hold the OpenXR vendors plugin DLL
# and fail the gradle export. Warn (don't kill — it might be another project).
$running = Get-Process -Name 'Godot*' -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning ("Godot is running (PID {0}). If the export fails on a locked plugin DLL, close the editor and retry." -f ($running.Id -join ', '))
}

$outLog = Join-Path $env:TEMP 'crimsonvr_export.out.log'
$errLog = Join-Path $env:TEMP 'crimsonvr_export.err.log'
Remove-Item $outLog, $errLog -ErrorAction SilentlyContinue
if (Test-Path $apk) { Remove-Item $apk -Force }

Write-Host "==> Exporting Android APK (headless gradle build)..." -ForegroundColor Cyan
# --xr-mode off keeps this local export process from trying to start XR (a
# flat-fallback popup); it does NOT disable XR in the export preset / the APK.
$p = Start-Process -FilePath $Godot `
    -ArgumentList @('--headless', '--xr-mode', 'off', '--path', $proj, '--export-debug', 'Android', $apk) `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru -WindowStyle Hidden

function Get-ExportLog {
    $t = ''
    foreach ($f in @($errLog, $outLog)) {
        if (Test-Path $f) { $t += (Get-Content $f -Raw -ErrorAction SilentlyContinue) }
    }
    # Strip ANSI color codes so the progress markers match cleanly.
    return ($t -replace "\x1b\[[0-9;]*m", '')
}

# Poll for the export-complete marker; Godot won't exit on its own (see header).
$deadline = (Get-Date).AddSeconds($ExportTimeoutSec)
$printed = 0
$done = $false
while ($true) {
    Start-Sleep -Seconds 3
    $log = Get-ExportLog

    # Echo new progress/step lines so the run isn't a silent black box.
    $lines = $log -split "`n"
    if ($lines.Count -gt $printed) {
        foreach ($ln in $lines[$printed..($lines.Count - 1)]) {
            if ($ln -match '\]\s*(Started|DONE|\w+_\w+)|Exporting|dotnet publish|ERROR') {
                Write-Host ("    " + $ln.TrimEnd())
            }
        }
        $printed = $lines.Count
    }

    if ($log -match '\[ DONE \]\s*export') { $done = $true; break }
    if ($p.HasExited) { break }
    if ((Get-Date) -gt $deadline) { break }
}

if (-not $done) {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    throw "export did not complete within ${ExportTimeoutSec}s (see $errLog)"
}

# Export finished writing the APK; terminate the hung Godot process.
if (-not $p.HasExited) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "==> Export done; terminated the (hung) Godot headless process." -ForegroundColor DarkGray
}
if (-not (Test-Path $apk)) { throw "export reported done but APK missing: $apk" }
$apkMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
Write-Host ("==> Exported {0} ({1} MB)" -f (Split-Path $apk -Leaf), $apkMb) -ForegroundColor Green

Write-Host "==> Injecting native lib + signing..." -ForegroundColor Cyan
& $inject -InputApk $apk -OutputApk $questApk
if ($LASTEXITCODE -ne 0) { throw "inject/sign failed ($LASTEXITCODE)" }

# Fail-closed payload check before a headset trip (learning from the Untitled VR
# Game build script): a broken export/inject otherwise only surfaces on-device as
# a flat app (missing OpenXR loader/vendor) or a dlopen failure (missing native
# lib). A cheap "jar tf" listing vs the required VR + native entries.
Write-Host "==> Verifying APK payload..." -ForegroundColor Cyan
$jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
$jar = if ($jdk) { Join-Path $jdk 'bin\jar.exe' } else { 'jar' }
$entries = & $jar tf $questApk 2>$null
$required = @(
    'classes.dex',                               # .NET/managed runtime present
    'lib/arm64-v8a/libcrimson_host.so',          # our injected native sim lib
    'lib/arm64-v8a/libopenxr_loader.so',         # real VR (else flat mode)
    'lib/arm64-v8a/libgodotopenxrvendors.so'     # OpenXR Vendors plugin
)
foreach ($e in $required) {
    if ($entries -notcontains $e) {
        throw "APK verification failed: '$e' missing from $questApk -- the export/inject is broken (would run flat or fail dlopen on-device)."
    }
}
Write-Host ("    payload OK ({0} required entries present)" -f $required.Count) -ForegroundColor DarkGray

if ($Install) {
    $adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if (-not $adb) { $adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe' }
    if (-not (Test-Path $adb)) { throw "adb not found (install to $Device manually)" }
    Write-Host "==> Installing to $Device..." -ForegroundColor Cyan
    & $adb -s $Device install -r $questApk
    if ($LASTEXITCODE -ne 0) { throw "adb install failed ($LASTEXITCODE)" }
}

$questMb = [math]::Round((Get-Item $questApk).Length / 1MB, 1)
Write-Host ""
Write-Host ("DONE: {0} ({1} MB)" -f $questApk, $questMb) -ForegroundColor Green
if (-not $Install) { Write-Host "Install with: adb -s $Device install -r `"$questApk`"" -ForegroundColor DarkGray }
Write-Host "Launch from the in-headset app library (adb launch is gated by the controllers dialog)." -ForegroundColor DarkGray
