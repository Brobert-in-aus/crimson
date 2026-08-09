# Pull the newest .crd off the Quest and run it through `crimson-zig replay
# verify`. This is the slice-8 oracle: a VR run only counts as recorded
# correctly when the desktop verifier re-simulates it and agrees.
#
#   verify_replay.ps1                 newest replay on the device
#   verify_replay.ps1 -All            every replay on the device, oldest first
#   verify_replay.ps1 -Local a.crd    skip the device, verify a local file
#   verify_replay.ps1 -Keep           leave the pulled .crd in artifacts/replays
#
# Two device quirks are baked in because both cost real time to rediscover:
#   * app-private storage needs `run-as`, and `adb pull` cannot read it;
#     `exec-as ... cat` streamed to a local file is the way in.
#   * the stream MUST be captured as raw bytes. PowerShell's pipeline would
#     decode it as text and corrupt every byte above 0x7F, so the output is
#     redirected by cmd rather than passed through PowerShell.

param(
    [string]$Device = '192.168.8.100:5555',
    [string]$Package = 'xyz.crimsonvr.app',
    [switch]$All,
    [string]$Local,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$outDir = Join-Path $repoRoot 'artifacts\replays'
$verifier = Join-Path $repoRoot 'crimson-zig\zig-out\bin\crimson-zig.exe'

if (-not (Test-Path $verifier)) {
    throw "verifier not built: $verifier (run: zig build -Doptimize=ReleaseFast in crimson-zig)"
}

# Pull one app-private file as RAW BYTES.
#
# Three things here are load-bearing, each learned the hard way:
#   * `adb pull` cannot read app-private storage at all; it has to go through
#     `run-as`, which only cats to stdout.
#   * that stdout must never touch the PowerShell pipeline, which decodes it as
#     text and corrupts every byte above 0x7F. Reading BaseStream keeps it raw.
#   * `exec-out` hands argv straight to the device with no shell, so quoting the
#     remote path in single quotes makes the quotes PART OF THE FILENAME. The
#     double quotes below are consumed by adb's own Windows arg parsing, which
#     is what actually delivers a name containing a space as one argument.
function Copy-DeviceFile {
    param([string]$Target, [string]$Package, [string]$Name, [string]$Dest)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'adb'
    $psi.Arguments = "-s $Target exec-out run-as $Package cat `"files/replays/$Name`""
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $out = [System.IO.File]::Create($Dest)
    try {
        $proc.StandardOutput.BaseStream.CopyTo($out)
    }
    finally {
        $out.Close()
        $proc.WaitForExit()
    }
}

function Invoke-Verify([string]$path) {
    $name = Split-Path $path -Leaf
    $json = & $verifier replay verify $path --format json | Out-String
    try {
        $v = $json | ConvertFrom-Json
    } catch {
        Write-Host "$name : verifier produced no JSON" -ForegroundColor Red
        Write-Host $json
        return $false
    }

    # The verdict is header_claim.match. The top-level `status` only reports
    # whether the verifier RAN -- reading that as the result reports success on
    # a replay that just failed.
    $claim = $v.header_claim
    if ($claim -and $claim.match) {
        Write-Host "$name : VERIFIED" -ForegroundColor Green
        Write-Host ("    ticks={0} elapsed={1}ms xp={2} kills={3} weapon={4} shots={5}/{6}" -f `
            $v.run_result.ticks, $v.run_result.elapsed_ms, $v.run_result.score_xp, `
            $v.run_result.creature_kill_count, $v.run_result.most_used_weapon_id, `
            $v.run_result.shots_fired, $v.run_result.shots_hit)
        return $true
    }

    Write-Host "$name : FAILED ($($v.status))" -ForegroundColor Red
    if ($claim) {
        Write-Host "    mismatched: $($claim.mismatched_fields -join ', ')" -ForegroundColor Yellow
        foreach ($f in $claim.mismatched_fields) {
            Write-Host ("    {0}: claimed={1} simulated={2}" -f `
                $f, $claim.expected.$f, $claim.simulated.$f)
        }
    }
    Find-DivergenceTick -Path $path
    return $false
}

# Bisect the first tick where the live run and its replay part company.
#
# The live rng, sampled per tick, is written beside the .crd by the recorder;
# `replay verify --max-ticks N` reports the re-simulation's rng at the same
# point. Comparing end-of-run totals only ever says THAT they disagree — this
# says WHERE, which is the difference between reading the code at that tick and
# guessing at the whole run.
function Find-DivergenceTick([string]$Path) {
    $rngPath = "$Path.rng"
    if (-not (Test-Path $rngPath)) {
        Write-Host "    (no .rng sidecar; recorded before ABI v23 -- cannot bisect)" -ForegroundColor DarkGray
        return
    }
    $raw = [System.IO.File]::ReadAllBytes($rngPath)
    $count = [int]($raw.Length / 4)
    if ($count -lt 2) { return }

    function LiveRng([int]$tick) { return [System.BitConverter]::ToUInt32($raw, ($tick - 1) * 4) }
    function SimRng([int]$tick) {
        $j = & $verifier replay verify $Path --format json --max-ticks $tick | Out-String | ConvertFrom-Json
        return [uint32]$j.run_result.rng_state
    }

    if ((SimRng $count) -eq (LiveRng $count)) {
        Write-Host "    rng agrees at the final tick; divergence is not in the sim stream" -ForegroundColor Yellow
        return
    }
    $lo = 1
    $hi = $count
    while ($lo -lt $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ((SimRng $mid) -eq (LiveRng $mid)) { $lo = $mid + 1 } else { $hi = $mid }
    }
    Write-Host "    FIRST DIVERGING TICK = $lo (of $count)" -ForegroundColor Yellow
}

if ($Local) {
    exit (& { if (Invoke-Verify (Resolve-Path $Local)) { 0 } else { 1 } })
}

# Prefer the wireless target, fall back to whatever single device is attached.
$target = $Device
& adb connect $Device 2>&1 | Out-Null
$devices = (& adb devices) -split "`n" | Where-Object { $_ -match "\sdevice$" }
if (-not ($devices -match [regex]::Escape($Device))) {
    if ($devices.Count -eq 0) { throw "no adb device (wireless $Device unreachable, none on USB)" }
    $target = ($devices[0] -split "\s+")[0]
    Write-Host "wireless $Device unreachable; using $target"
}

# -1t: newest first, ONE PER LINE. Without -1, Android's ls packs several names
# onto a line and escapes spaces as "\ ", which turns two filenames into one
# unusable string -- and the early recordings really do have spaces in their
# names, from before the stamp format was fixed.
$listing = & adb -s $target exec-out run-as $Package ls -1t files/replays 2>&1
if ($LASTEXITCODE -ne 0 -or $listing -match 'No such file') {
    throw "no replays on device ($Package files/replays). Play a run to the death screen first."
}
# ls ESCAPES the space in the legacy names as "\ ". Left as-is that backslash
# ends up in the local path and File::Create fails on a directory that does not
# exist, which reads as a pull failure rather than a parsing one.
$names_all = @($listing -split "`n" | ForEach-Object { ($_.Trim() -replace '\\ ', ' ') })
$names = @($names_all | Where-Object { $_ -like '*.crd' })
if ($names.Count -eq 0) { throw "no .crd files in $Package files/replays" }
if (-not $All) { $names = @($names[0]) } else { [array]::Reverse($names) }

New-Item -ItemType Directory -Force $outDir | Out-Null
$failed = 0
foreach ($name in $names) {
    # Older recordings have a space in the name (from before the stamp format
    # was fixed); the local copy drops it so nothing downstream needs quoting.
    $dest = Join-Path $outDir ($name -replace ' ', '-')
    Copy-DeviceFile -Target $target -Package $Package -Name $name -Dest $dest
    # The rng sidecar is a separate file and the listing filter above only keeps
    # .crd, so it has to be fetched explicitly -- without it the bisect silently
    # reports "cannot bisect" on a run that actually carries one.
    if ($names_all -contains "$name.rng") {
        Copy-DeviceFile -Target $target -Package $Package -Name "$name.rng" -Dest "$dest.rng"
    }
    if (-not (Test-Path $dest) -or (Get-Item $dest).Length -eq 0) {
        Write-Host "$name : pull produced an empty file" -ForegroundColor Red
        $failed++
        continue
    }
    if (-not (Invoke-Verify $dest)) { $failed++ }
    if (-not $Keep) { Remove-Item $dest -Force }
}

if (-not $Keep) { Write-Host "(pass -Keep to retain the pulled .crd under artifacts/replays)" }
if ($failed -gt 0) { throw "$failed of $($names.Count) replay(s) failed verification" }
Write-Host "$($names.Count) replay(s) verified" -ForegroundColor Green
