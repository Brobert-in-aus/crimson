param(
    [string]$Apk = (Join-Path $PSScriptRoot '..\..\artifacts\CrimsonVR.quest.apk'),
    [string]$Pack = (Join-Path $PSScriptRoot '..\..\artifacts\clean-quest-test\crimson-assets.pack'),
    [string]$Device,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$package = 'xyz.crimsonvr.app'
$adb = Get-Command adb -ErrorAction Stop | Select-Object -ExpandProperty Source
$Apk = [System.IO.Path]::GetFullPath($Apk)
$Pack = [System.IO.Path]::GetFullPath($Pack)

if (-not (Test-Path -LiteralPath $Apk -PathType Leaf)) { throw "APK not found: $Apk" }
if (-not (Test-Path -LiteralPath $Pack -PathType Leaf)) { throw "asset pack not found: $Pack" }

$devices = & $adb devices | Select-String '^\S+\s+device$' | ForEach-Object {
    ($_ -split '\s+')[0]
}
if ($Device) {
    if ($Device -notin $devices) { throw "Quest is not connected/authorized: $Device" }
    $serial = $Device
}
elseif ($devices.Count -eq 1) {
    $serial = $devices[0]
}
elseif ($devices.Count -eq 0) {
    throw 'No authorized Quest is connected.'
}
else {
    throw 'Multiple Android devices are connected; pass -Device SERIAL.'
}

$apkHash = (Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash
$packHash = (Get-FileHash -LiteralPath $Pack -Algorithm SHA256).Hash
Write-Host "Quest: $serial"
Write-Host "APK:   $Apk ($apkHash)"
Write-Host "Pack:  $Pack ($packHash)"

if (-not $Execute) {
    Write-Host ''
    Write-Host 'READY: preflight passed; no headset data was changed.' -ForegroundColor Green
    Write-Host 'This test deletes CrimsonVR app data, installs the APK, transfers the pack, and leaves the app stopped.'
    Write-Host 'Run again with -Execute when the current headset results/replays have been collected.'
    exit 0
}

# Exact-package clean install. Uninstall intentionally removes settings, imported
# assets, highscores, and replays; callers must opt in with -Execute.
& $adb -s $serial shell am force-stop $package
& $adb -s $serial uninstall $package
if ($LASTEXITCODE -ne 0) { throw "Could not uninstall $package" }
& $adb -s $serial install $Apk
if ($LASTEXITCODE -ne 0) { throw 'Could not install the clean Quest APK.' }

$inbox = "/sdcard/Android/data/$package/files"
& $adb -s $serial shell mkdir -p $inbox
& $adb -s $serial push $Pack "$inbox/crimson-assets.pack"
if ($LASTEXITCODE -ne 0) { throw 'Could not transfer the asset pack.' }
& $adb -s $serial shell am force-stop $package

$installed = (& $adb -s $serial shell pm path $package | Out-String).Trim()
$remotePack = (& $adb -s $serial shell ls -l "$inbox/crimson-assets.pack" | Out-String).Trim()
if ($installed -notmatch '^package:') { throw 'Package verification failed after install.' }
if (-not $remotePack) { throw 'Asset inbox verification failed after transfer.' }
Write-Host 'DONE: clean APK and first-run asset inbox are ready; CrimsonVR was not launched.' -ForegroundColor Green
