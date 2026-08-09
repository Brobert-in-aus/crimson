# Fail closed if a clean-build Quest APK could absorb development-only
# Crimsonland art/audio. M6 will extend this with known-derived hashes.

param([string]$Project)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Project = if ($Project) { $Project } else { Join-Path $repoRoot 'crimson-vr\godot' }
$assets = Join-Path $Project 'assets'

if (Test-Path $assets) {
    $files = Get-ChildItem $assets -File -Recurse -Force
    if ($files) {
        $sample = ($files | Select-Object -First 10 -ExpandProperty FullName) -join "`n  "
        throw "Quest CI refuses to package the development asset tree:`n  $sample"
    }
}

$paqs = Get-ChildItem $Project -File -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.paq', '.pak') }
if ($paqs) { throw "Quest CI refuses to package PAQ/PAK files: $($paqs.FullName -join ', ')" }

$packs = Get-ChildItem $Project -File -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.pack' }
if ($packs) { throw "Quest CI refuses to package user-created asset packs: $($packs.FullName -join ', ')" }

Write-Host 'Asset-free source gate passed (no Godot asset tree or PAQ/PAK files).'
