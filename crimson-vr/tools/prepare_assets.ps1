param(
    [string]$GameDir,
    [switch]$Quest,
    [switch]$Pcvr,
    [string]$Apk,
    [string]$Device
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$uv = Get-Command uv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $uv) {
    $common = Join-Path $env:USERPROFILE '.local\bin\uv.exe'
    if (Test-Path $common) { $uv = $common }
}
if (-not $uv) {
    throw @'
uv is required for this checkout helper. Install it once with:
  winget install --id=astral-sh.uv -e
Then run this command again. No game assets are uploaded anywhere.
'@
}
if ($Quest -and $Pcvr) { throw 'Choose either -Quest or -Pcvr.' }

$argsList = @('run', 'python', 'crimson-vr/tools/prepare_assets.py')
if ($GameDir) { $argsList += $GameDir }
if ($Quest) { $argsList += '--quest' }
if ($Pcvr) { $argsList += '--pcvr' }
if ($Apk) { $argsList += @('--apk', $Apk) }
if ($Device) { $argsList += @('--device', $Device) }

Push-Location $repoRoot
try {
    & $uv @argsList
    if ($LASTEXITCODE -ne 0) { throw "Asset preparation failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
