param(
    [Parameter(Mandatory = $true)][string]$Apk,
    [string]$Jar
)

$ErrorActionPreference = 'Stop'
$Apk = (Resolve-Path $Apk).Path
$Jar = if ($Jar) { $Jar } else { (Get-Command jar -ErrorAction Stop).Source }
if (-not (Test-Path $Jar)) { throw "jar executable not found: $Jar" }

$entries = & $Jar tf $Apk
if ($LASTEXITCODE -ne 0) { throw "could not list APK payload: $Apk" }

$rawArchives = $entries | Where-Object {
    $_ -match '(^|/)(crimson|sfx|music)\.paq$' -or
    $_ -match '\.pak$' -or
    $_ -match '\.pack$'
}
if ($rawArchives) {
    throw "Bundled-assets APK contains forbidden source archives:`n  $($rawArchives -join "`n  ")"
}

$required = @(
    'assets/assets/sprites/sprite_manifest.json',
    'assets/assets/audio/audio_manifest.json'
)
foreach ($entry in $required) {
    if ($entries -notcontains $entry) {
        throw "Bundled-assets APK gate failed: '$entry' is missing from $Apk"
    }
}

$spritePayload = $entries | Where-Object { $_ -match '^assets/\.godot/imported/.+\.ctex$' }
$audioPayload = $entries | Where-Object { $_ -match '^assets/\.godot/imported/.+\.(oggvorbisstr|sample)$' }
if ($spritePayload.Count -lt 20) { throw 'Bundled-assets APK gate failed: baked sprite imports are missing.' }
if ($audioPayload.Count -lt 5) { throw 'Bundled-assets APK gate failed: baked audio imports are missing.' }

Write-Host "Bundled-assets APK gate passed: $Apk"
