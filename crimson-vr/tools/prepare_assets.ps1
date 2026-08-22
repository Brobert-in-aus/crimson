param(
    [string]$GameDir,
    [switch]$Quest,
    [switch]$Pcvr,
    [switch]$StageProject,
    [string]$ProjectAssets,
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
$python = Join-Path $repoRoot '.venv\Scripts\python.exe'
if (-not $uv -and -not (Test-Path $python)) {
    throw @'
uv is required for this checkout helper. Install it once with:
  winget install --id=astral-sh.uv -e
Then run this command again. No game assets are uploaded anywhere.
'@
}
if (@($Quest, $Pcvr, $StageProject).Where({ $_ }).Count -gt 1) {
    throw 'Choose only one of -Quest, -Pcvr, or -StageProject.'
}
if ($ProjectAssets -and -not $StageProject) { throw '-ProjectAssets requires -StageProject.' }

$runner = if ($uv) { $uv } else { $python }
$argsList = if ($uv) {
    @('run', 'python', 'crimson-vr/tools/prepare_assets.py')
}
else {
    @('crimson-vr/tools/prepare_assets.py')
}
if ($GameDir) { $argsList += $GameDir }
if ($Quest) { $argsList += '--quest' }
if ($Pcvr) { $argsList += '--pcvr' }
if ($StageProject) {
    $destination = if ($ProjectAssets) { $ProjectAssets } else { Join-Path $repoRoot 'crimson-vr\godot\assets' }
    $argsList += @('--stage-project', $destination)
}
if ($Apk) { $argsList += @('--apk', $Apk) }
if ($Device) { $argsList += @('--device', $Device) }

Push-Location $repoRoot
try {
    & $runner @argsList
    if ($LASTEXITCODE -ne 0) { throw "Asset preparation failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
