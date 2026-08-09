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
#     -InstallOnly    skip the build; install the existing signed APK
#     -Device         PREFERRED wireless adb target (default 192.168.8.100:5555);
#                     a USB-attached Quest is used automatically if it is unreachable
#     -ExportTimeoutSec  hard cap on the export wait (default 600)
#     -WaitDeviceSec  how long to wait for a sleeping/powered-off Quest before
#                     skipping the install (default 300; the APK is kept either way)

param(
    [switch]$Install,
    [switch]$InstallOnly,
    [switch]$Release,
    [switch]$InstallAndroidBuildTemplate,
    [string]$Device = '192.168.8.100:5555',
    [int]$ExportTimeoutSec = 600,
    [int]$WaitDeviceSec = 300,
    [string]$Godot = 'D:\Projects\games-xr\_tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe',
    [string]$AndroidSdk,
    [string]$AndroidNdk,
    [string]$Jdk,
    [string]$Keystore,
    [string]$KeyAlias = 'androiddebugkey',
    [string]$StorePassword = 'android',
    [string]$KeyPassword = 'android'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $repoRoot 'crimson-vr\godot'
$apk = Join-Path $repoRoot 'artifacts\CrimsonVR.apk'
$questApk = Join-Path $repoRoot 'artifacts\CrimsonVR.quest.apk'
$inject = Join-Path $PSScriptRoot 'inject_native_and_sign.ps1'

$AndroidSdk = if ($AndroidSdk) { $AndroidSdk } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$AndroidNdk = if ($AndroidNdk) { $AndroidNdk } elseif ($env:ANDROID_NDK_ROOT) { $env:ANDROID_NDK_ROOT } elseif ($env:ANDROID_NDK_HOME) { $env:ANDROID_NDK_HOME } else {
    Get-ChildItem (Join-Path $AndroidSdk 'ndk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
$Jdk = if ($Jdk) { $Jdk } elseif ($env:JAVA_HOME_17_X64) { $env:JAVA_HOME_17_X64 } elseif ($env:JAVA_HOME) { $env:JAVA_HOME } else {
    Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
$Keystore = if ($Keystore) { $Keystore } else { Join-Path $env:USERPROFILE '.android\debug.keystore' }
$buildTools = Get-ChildItem (Join-Path $AndroidSdk 'build-tools') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending |
    Where-Object { Test-Path (Join-Path $_.FullName 'apksigner.bat') } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not (Test-Path $Godot)) { throw "Godot not found: $Godot" }
if (-not (Test-Path $inject)) { throw "inject script not found: $inject" }

function Get-UsbSerial {
    # First adb device in 'device' state whose serial is NOT a tcp target
    # (tcp serials contain a colon). Lines in other states -- 'unauthorized',
    # 'offline', 'no permissions' -- deliberately do not match, so a half-
    # connected headset is never mistaken for an installable one.
    param([string]$DeviceList)
    foreach ($line in ($DeviceList -split "`r?`n")) {
        if ($line -match '^(\S+)\s+device\b' -and $matches[1] -notmatch ':') {
            return $matches[1]
        }
    }
    return $null
}

function Install-QuestApk {
    # Installs the signed APK, treating an unreachable Quest as a normal state
    # rather than a build failure. Wireless is PREFERRED (no cable, and it is
    # the default workflow), but a USB-attached headset is accepted on every
    # attempt as a fallback rather than only after the wireless wait expires.
    #
    # That fallback exists because of a real 300s stall: Quest drops adb tcpip
    # mode on reboot, so port 5555 goes dead while the headset is awake, on the
    # network and pingable -- and a perfectly good USB cable sat attached for
    # the whole wait. Reachability of the IP says nothing about the port.
    #
    # Returns $true only when the install succeeded; on timeout the APK stays on
    # disk and the caller prints the follow-up.
    param([string]$Apk)
    $adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if (-not $adb) { $adb = Join-Path $AndroidSdk 'platform-tools\adb.exe' }
    if (-not (Test-Path $adb)) { throw "adb not found (install to $Device manually)" }

    Write-Host "==> Installing (wireless $Device, falling back to USB)..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($WaitDeviceSec)
    $announced = $false
    while ($true) {
        # tcp serials must be adb-connect'ed each attempt; when the host is down
        # the TCP timeout (~20s) provides most of the retry pacing by itself.
        # Probes run via cmd /c: under EAP Stop, PS 5.1 turns any native stderr
        # line (e.g. adb's "device not found") into a terminating error.
        if ($Device -match ':') { cmd /c "`"$adb`" connect $Device >nul 2>nul" | Out-Null }
        $list = (cmd /c "`"$adb`" devices 2>nul" | Out-String)

        $target = $null
        if ($list -match ([regex]::Escape($Device) + "\s+device")) {
            $target = $Device
            Write-Host "    wireless target up: $Device" -ForegroundColor DarkGray
        }
        else {
            $usb = Get-UsbSerial $list
            if ($usb) {
                $target = $usb
                Write-Host ("    {0} unreachable; using USB device {1}" -f $Device, $usb) -ForegroundColor Yellow
            }
        }

        if ($target) {
            & $adb -s $target install -r $Apk
            if ($LASTEXITCODE -ne 0) { throw "adb install failed ($LASTEXITCODE)" }
            return $true
        }

        if ((Get-Date) -gt $deadline) { break }
        if (-not $announced) {
            Write-Host ("    No Quest on wireless or USB (powered off / asleep?). Waiting up to {0}s -- wake it or plug it in; Ctrl+C is safe, the APK is already built." -f $WaitDeviceSec) -ForegroundColor Yellow
            $announced = $true
        }
        Start-Sleep -Seconds 5
    }
    Write-Warning ("No Quest reachable on {0} or USB after {1}s -- install skipped, the signed APK is kept." -f $Device, $WaitDeviceSec)
    Write-Host "    Install once it's awake with: build_quest.ps1 -InstallOnly" -ForegroundColor DarkGray
    Write-Host "    (If it is awake and pingable, wireless adb is probably unarmed: adb tcpip 5555 over USB.)" -ForegroundColor DarkGray
    return $false
}

if ($InstallOnly) {
    if (-not (Test-Path $questApk)) { throw "-InstallOnly: no signed APK at $questApk (run a full build first)" }
    if (-not (Install-QuestApk $questApk)) { exit 1 }
    Write-Host ""
    Write-Host ("DONE: installed {0}" -f $questApk) -ForegroundColor Green
    Write-Host "Launch from the in-headset app library (adb launch is gated by the controllers dialog)." -ForegroundColor DarkGray
    exit 0
}

# Preflight everything the post-export steps need, so a missing keystore / tool /
# native lib / vendors plugin fails now instead of after the multi-minute export.
$preflight = [ordered]@{
    'arm64 native lib (build_libcrimson.ps1 -android)' = (Join-Path $proj 'native\android-arm64\libcrimson_host.so')
    'OpenXR Vendors plugin (fetch_vendors_plugin.ps1)' = (Join-Path $proj 'addons\godotopenxrvendors')
    'JDK 17'                                           = $Jdk
    'Android SDK'                                      = $AndroidSdk
    'Android NDK'                                      = $AndroidNdk
    'Android build-tools (zipalign/apksigner)'         = $buildTools
    'signing keystore'                                 = $Keystore
}
foreach ($item in $preflight.GetEnumerator()) {
    if (-not $item.Value -or -not (Test-Path $item.Value)) {
        throw "preflight failed: missing $($item.Key): $($item.Value)"
    }
}

# Fail-closed ABI check: the arm64 .so about to be injected must report the same
# CRIMSON_HOST_ABI_VERSION the C# frontend expects (Sim.ExpectedAbiVersion), else
# the app boots to 'sim unavailable' on-device. This slipped through once: a
# silently-failed android zig build left a stale .so that a signed, payload-
# verified APK shipped anyway. The constant is read by disassembling
# crimson_host_abi_version (mov w0, #N) with the NDK's llvm-objdump.
$simCs = Join-Path $proj 'src\Sim.cs'
$abiExpected = $null
if (Test-Path $simCs) {
    $abiMatch = Select-String -Path $simCs -Pattern 'ExpectedAbiVersion\s*=\s*(\d+)' | Select-Object -First 1
    if ($abiMatch) { $abiExpected = [int]$abiMatch.Matches[0].Groups[1].Value }
}
$objdump = Get-ChildItem (Join-Path $AndroidNdk 'toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-objdump.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $abiExpected) {
    throw "preflight failed: could not read ExpectedAbiVersion from $simCs"
}
if (-not $objdump) {
    throw "preflight failed: llvm-objdump not found under the Android NDK; cannot verify the arm64 native ABI"
}
$dis = & $objdump.FullName -d --disassemble-symbols=crimson_host_abi_version $preflight['arm64 native lib (build_libcrimson.ps1 -android)'] 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "preflight failed: llvm-objdump could not disassemble crimson_host_abi_version"
}
$mv = $dis | Select-String 'mov\s+w0, #(0x[0-9a-fA-F]+|\d+)' | Select-Object -First 1
if (-not $mv) {
    throw "preflight failed: could not decode crimson_host_abi_version from the arm64 native library"
}
$rawVer = $mv.Matches[0].Groups[1].Value
$libVer = if ($rawVer -like '0x*') { [Convert]::ToInt32($rawVer.Substring(2), 16) } else { [int]$rawVer }
if ($libVer -ne $abiExpected) {
    throw "preflight failed: arm64 libcrimson_host.so reports ABI v$libVer but Sim.cs expects v$abiExpected - rebuild it (build_libcrimson.ps1 -android)"
}
Write-Host "    arm64 lib ABI v$libVer matches Sim.cs" -ForegroundColor DarkGray
if ($Install) {
    $adbPre = (Get-Command adb -ErrorAction SilentlyContinue).Source
    if (-not $adbPre) { $adbPre = Join-Path $AndroidSdk 'platform-tools\adb.exe' }
    if (-not (Test-Path $adbPre)) { throw "preflight failed: -Install requested but adb not found (Android platform-tools)" }
}

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
$env:JAVA_HOME = $Jdk
$env:ANDROID_HOME = $AndroidSdk
$env:ANDROID_SDK_ROOT = $AndroidSdk
$env:ANDROID_NDK_ROOT = $AndroidNdk
$env:GODOT_ANDROID_KEYSTORE_DEBUG_PATH = $Keystore
$env:GODOT_ANDROID_KEYSTORE_DEBUG_USER = $KeyAlias
$env:GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD = $StorePassword
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = $Keystore
$env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = $KeyAlias
$env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = $StorePassword
$exportMode = if ($Release) { '--export-release' } else { '--export-debug' }
$godotArgs = @('--headless', '--xr-mode', 'off', '--path', $proj)
if ($InstallAndroidBuildTemplate) { $godotArgs += '--install-android-build-template' }
$godotArgs += @($exportMode, 'Android', $apk)
$p = Start-Process -FilePath $Godot `
    -ArgumentList $godotArgs `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru -WindowStyle Hidden

function Get-ExportLog {
    $t = ''
    foreach ($f in @($errLog, $outLog)) {
        if (Test-Path $f) { $t += (Get-Content $f -Raw -ErrorAction SilentlyContinue) }
    }
    # Strip ANSI color codes so the progress markers match cleanly.
    return ($t -replace "\x1b\[[0-9;]*m", '')
}

function Stop-ProcessTree {
    # Kill a process and ONLY its descendants, by PID (taskkill /T) — never
    # unrelated Godot/Java processes by name/timestamp. taskkill /T is the PS 5.1
    # -safe equivalent of .NET Process.Kill($true) (which is PS7-only). The gradle
    # daemon detaches, so it is not a child and build caching is unaffected.
    param([int]$Id)
    # Godot can exit between the poll above and this cleanup. taskkill reports
    # that normal race as an error; it must not turn a successful export into a
    # failed build.
    try {
        & taskkill.exe /PID $Id /T /F 2>$null | Out-Null
    }
    catch {
        # Already stopped is the desired end state.
    }
    $global:LASTEXITCODE = 0
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

# Godot may have flushed the marker as it exited; re-read once before deciding.
if (-not $done -and ((Get-ExportLog) -match '\[ DONE \]\s*export')) { $done = $true }

if ($done) {
    # Marker seen: the APK is written and Godot typically hangs in teardown — end it.
    if (-not $p.HasExited) {
        Stop-ProcessTree $p.Id
        Write-Host "==> Export done; terminated the (hung) Godot process tree." -ForegroundColor DarkGray
    }
}
elseif ($p.HasExited) {
    # Exited WITHOUT the marker: distinguish a real failure from a rare clean-but-
    # markerless exit (don't mislabel an early non-zero exit as a timeout).
    $code = $p.ExitCode
    if ($code -ne 0) { throw "Godot export exited with code $code before completing (see $errLog)." }
    if (-not (Test-Path $apk)) { throw "Godot export exited (code 0) without producing an APK (see $errLog)." }
    Write-Warning "Godot export exited cleanly without the '[ DONE ] export' marker; APK exists, continuing."
}
else {
    # Still running at the safety ceiling.
    Stop-ProcessTree $p.Id
    if (-not (Test-Path $apk)) { throw "export hit the ${ExportTimeoutSec}s ceiling with no APK (see $errLog)." }
    Write-Warning "export hit the ${ExportTimeoutSec}s ceiling but the APK exists; continuing (raise -ExportTimeoutSec if this recurs)."
}
if (-not (Test-Path $apk)) { throw "export reported done but APK missing: $apk" }
$apkMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
Write-Host ("==> Exported {0} ({1} MB)" -f (Split-Path $apk -Leaf), $apkMb) -ForegroundColor Green

Write-Host "==> Injecting native lib + signing..." -ForegroundColor Cyan
& $inject -InputApk $apk -OutputApk $questApk -AndroidSdk $AndroidSdk -BuildTools $buildTools `
    -Jdk $Jdk -Keystore $Keystore -KeyAlias $KeyAlias -StorePassword $StorePassword -KeyPassword $KeyPassword
if ($LASTEXITCODE -ne 0) { throw "inject/sign failed ($LASTEXITCODE)" }

# Fail-closed payload check before a headset trip (learning from the Untitled VR
# Game build script): a broken export/inject otherwise only surfaces on-device as
# a flat app (missing OpenXR loader/vendor) or a dlopen failure (missing native
# lib). A cheap "jar tf" listing vs the required VR + native entries.
Write-Host "==> Verifying APK payload..." -ForegroundColor Cyan
$jar = Join-Path $Jdk 'bin\jar.exe'   # JDK resolved + preflighted above
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

# Public/personal builds must contain only our frontend/native code. Keep this
# output-level check beside the payload check so local builds cannot bypass the
# source-tree CI gate merely by exporting from a dirty development checkout.
& (Join-Path $PSScriptRoot 'assert_asset_free_apk.ps1') -Apk $questApk -Jar $jar
if ($LASTEXITCODE -ne 0) { throw "asset-free APK verification failed ($LASTEXITCODE)" }

$installed = $false
if ($Install) {
    $installed = Install-QuestApk $questApk
}

$questMb = [math]::Round((Get-Item $questApk).Length / 1MB, 1)
Write-Host ""
Write-Host ("DONE: {0} ({1} MB){2}" -f $questApk, $questMb, $(if ($installed) { ' -- installed' } else { '' })) -ForegroundColor Green
if (-not $installed) {
    $hint = if ($Install) { "build_quest.ps1 -InstallOnly" } else { "build_quest.ps1 -InstallOnly  (or: adb -s $Device install -r `"$questApk`")" }
    Write-Host "Install with: $hint" -ForegroundColor DarkGray
}
Write-Host "Launch from the in-headset app library (adb launch is gated by the controllers dialog)." -ForegroundColor DarkGray
