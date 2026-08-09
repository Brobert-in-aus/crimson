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
$forbidden = $entries | Where-Object {
    $_ -match '(^|/)(crimson|sfx|music)\.paq$' -or
    $_ -match '\.pak$' -or
    $_ -match '\.pack$' -or
    $_ -match '(^|/)crimson-assets\.json$' -or
    $_ -match '(^|/)(sprite|audio)_manifest\.json$' -or
    $_ -match '(^|/)assets/(sprites|audio)/'
}
if ($forbidden) {
    throw "Asset-free APK gate failed; forbidden payload entries:`n  $($forbidden -join "`n  ")"
}

Write-Host "Asset-free APK gate passed: $Apk"
