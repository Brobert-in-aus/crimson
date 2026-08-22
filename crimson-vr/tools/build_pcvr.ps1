# Export asset-free Windows and Linux PCVR packages. Native libraries must have
# been prepared first with build_libcrimson.ps1 -Linux.

param(
    [ValidateSet('All', 'Windows', 'Linux')]
    [string]$Platform = 'All',
    [string]$Godot,
    [string]$OutputDir,
    [int]$ExportTimeoutSec = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $repoRoot 'crimson-vr\godot'
$packager = Join-Path $PSScriptRoot 'package_pcvr.py'
$OutputDir = if ($OutputDir) { [System.IO.Path]::GetFullPath($OutputDir) } else {
    Join-Path $repoRoot 'artifacts\pcvr'
}
if (-not $Godot) {
    if ($env:GODOT4 -and (Test-Path -LiteralPath $env:GODOT4 -PathType Leaf)) {
        $Godot = $env:GODOT4
    } else {
        $command = Get-Command godot, godot4 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $Godot = $command.Source }
    }
}
if (-not $Godot) {
    $localGodot = 'D:\Projects\games-xr\_tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe'
    if (Test-Path -LiteralPath $localGodot -PathType Leaf) { $Godot = $localGodot }
}
if (-not $Godot -or -not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw 'Godot editor not found; pass -Godot, set GODOT4, or add godot to PATH.'
}
$python = Get-Command python -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python not found; it is required to create portable PCVR archives.' }

function Stop-ProcessTree {
    param([int]$Id)
    try { & taskkill.exe /PID $Id /T /F 2>$null | Out-Null } catch { }
    $global:LASTEXITCODE = 0
}

function Export-GodotProject {
    param([string]$Preset, [string]$Binary, [string]$LogStem)
    $stdout = "$LogStem.out.log"
    $stderr = "$LogStem.err.log"
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $Godot -ArgumentList @(
        '--headless', '--xr-mode', 'off', '--path', "`"$project`"",
        '--export-release', "`"$Preset`"", "`"$Binary`""
    ) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($ExportTimeoutSec)
    $done = $false
    function Test-DesktopPayloadReady {
        $base = Split-Path $Binary
        $dataDirs = @(Get-ChildItem -LiteralPath $base -Directory -Filter 'data_CrimsonVR_*' -ErrorAction SilentlyContinue)
        if ($dataDirs.Count -ne 1) { return $false }
        $data = $dataDirs[0].FullName
        $runtimeFiles = if ($Preset -eq 'Windows Desktop') {
            @('coreclr.dll', 'hostfxr.dll')
        } else {
            @('libcoreclr.so', 'libhostfxr.so')
        }
        foreach ($name in @('CrimsonVR.dll', 'CrimsonVR.deps.json',
            'CrimsonVR.runtimeconfig.json', 'GodotSharp.dll',
            'System.Private.CoreLib.dll', 'System.Runtime.dll', 'WindowsBase.dll') + $runtimeFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $data $name) -PathType Leaf)) { return $false }
        }
        $vendorsSource = Join-Path $project 'addons\godotopenxrvendors\plugin.gdextension'
        if (Test-Path -LiteralPath $vendorsSource -PathType Leaf) {
            $vendorsOutput = if ($Preset -eq 'Windows Desktop') {
                Join-Path $base 'libgodotopenxrvendors.dll'
            } else {
                Join-Path $base 'libgodotopenxrvendors.so'
            }
            if (-not (Test-Path -LiteralPath $vendorsOutput -PathType Leaf)) { return $false }
        }
        return $true
    }
    function Get-PayloadSignature {
        $files = @(Get-ChildItem -LiteralPath (Split-Path $Binary) -Recurse -File -ErrorAction SilentlyContinue)
        $bytes = ($files | Measure-Object -Property Length -Sum).Sum
        return "$($files.Count):$bytes"
    }
    $readySignature = $null
    $readySince = $null
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $log = ''
        foreach ($path in @($stderr, $stdout)) {
            if (Test-Path $path) { $log += Get-Content $path -Raw -ErrorAction SilentlyContinue }
        }
        $log = $log -replace "\x1b\[[0-9;]*m", ''
        if ($log -match '\[ DONE \]\s*export') {
            $done = $true
            break
        }
        if ($log -match '\[ DONE \]\s*savepack' -and (Test-DesktopPayloadReady)) {
            $signature = Get-PayloadSignature
            if ($signature -ne $readySignature) {
                $readySignature = $signature
                $readySince = Get-Date
            } elseif (((Get-Date) - $readySince).TotalSeconds -ge 2) {
                $done = $true
                break
            }
        } else {
            $readySignature = $null
            $readySince = $null
        }
    }
    if (-not $done) {
        $log = ''
        foreach ($path in @($stderr, $stdout)) {
            if (Test-Path $path) { $log += Get-Content $path -Raw -ErrorAction SilentlyContinue }
        }
        $log = $log -replace "\x1b\[[0-9;]*m", ''
        $done = ($log -match '\[ DONE \]\s*export') -or
            (($log -match '\[ DONE \]\s*savepack') -and (Test-DesktopPayloadReady))
    }
    if ($process.HasExited) { $process.WaitForExit() }
    if ($done -and -not $process.HasExited) { Stop-ProcessTree $process.Id }
    elseif (-not $done -and -not $process.HasExited) {
        Stop-ProcessTree $process.Id
        throw "$Preset export timed out after ${ExportTimeoutSec}s (see $stdout and $stderr)"
    }
    elseif (-not $done -and $process.ExitCode -ne 0) {
        throw "$Preset export failed ($($process.ExitCode); see $stdout and $stderr)"
    }
    if ($log -match 'Storing File:\s+res://assets/') {
        throw "$Preset export attempted to pack development assets (see $stdout and $stderr)"
    }
}

$targets = if ($Platform -eq 'All') { @('Windows', 'Linux') } else { @($Platform) }
foreach ($target in $targets) {
    $targetIsWindows = $target -eq 'Windows'
    $native = if ($targetIsWindows) {
        Join-Path $project 'native\win-x64\crimson_host.dll'
    } else {
        Join-Path $project 'native\linux-x64\libcrimson_host.so'
    }
    if (-not (Test-Path -LiteralPath $native -PathType Leaf)) {
        throw "native $target library missing: $native (run build_libcrimson.ps1 -Linux)"
    }

    $preset = if ($targetIsWindows) { 'Windows Desktop' } else { 'Linux/X11' }
    $folder = Join-Path $OutputDir $target.ToLowerInvariant()
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDir).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $folder = [System.IO.Path]::GetFullPath($folder)
    if (-not $folder.StartsWith($outputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to reset output outside ${OutputDir}: $folder"
    }
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    New-Item -ItemType Directory -Force $folder | Out-Null
    $binary = if ($targetIsWindows) { Join-Path $folder 'CrimsonVR.exe' } else { Join-Path $folder 'CrimsonVR.x86_64' }
    Write-Host "Exporting $preset -> $binary"
    Export-GodotProject $preset $binary (Join-Path $OutputDir "export-$($target.ToLowerInvariant())")
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "$preset output missing: $binary" }
    $pck = [System.IO.Path]::ChangeExtension($binary, '.pck')
    if (-not (Test-Path -LiteralPath $pck -PathType Leaf)) { throw "$preset PCK missing: $pck" }
    $managedCandidates = @(Get-ChildItem -LiteralPath $folder -Recurse -Filter 'CrimsonVR.dll' -File)
    if ($managedCandidates.Count -ne 1) {
        throw "$preset expected one managed CrimsonVR.dll, found $($managedCandidates.Count)"
    }
    $managed = $managedCandidates[0].FullName

    # NativeLibrary cannot dlopen a file embedded in the PCK. Keep the native
    # simulation loose at the exported res:// path used by Sim.ResolveNative.
    $rid = if ($targetIsWindows) { 'win-x64' } else { 'linux-x64' }
    $nativeOut = Join-Path $folder "native\$rid"
    New-Item -ItemType Directory -Force $nativeOut | Out-Null
    Copy-Item -LiteralPath $native -Destination $nativeOut -Force
    $copiedNative = Join-Path $nativeOut (Split-Path $native -Leaf)
    if ((Get-FileHash $native -Algorithm SHA256).Hash -ne
        (Get-FileHash $copiedNative -Algorithm SHA256).Hash) {
        throw "native library verification failed: $copiedNative"
    }

    $forbidden = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object {
        $_.Extension -in @('.paq', '.pak', '.pack') -or $_.FullName -match '[\\/]assets[\\/]'
    })
    if ($forbidden.Count) {
        throw "asset-free package gate failed: $($forbidden.FullName -join ', ')"
    }

    $manifest = Join-Path $folder 'PERSONAL-BUILD.txt'
    @(
        'CrimsonVR personal PCVR build'
        "platform=$target"
        "commit=$(git -C $repoRoot rev-parse HEAD)"
        "binary_sha256=$((Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant())"
        "pck_sha256=$((Get-FileHash $pck -Algorithm SHA256).Hash.ToLowerInvariant())"
        "managed_sha256=$((Get-FileHash $managed -Algorithm SHA256).Hash.ToLowerInvariant())"
        "native_sha256=$((Get-FileHash $copiedNative -Algorithm SHA256).Hash.ToLowerInvariant())"
        'Assets are supplied locally after installation; this package contains no Crimsonland art or audio.'
    ) | Set-Content $manifest

    $archive = if ($targetIsWindows) {
        Join-Path $OutputDir 'CrimsonVR-PCVR-Windows.zip'
    } else {
        $legacyZip = Join-Path $OutputDir 'CrimsonVR-PCVR-Linux.zip'
        if (Test-Path -LiteralPath $legacyZip) { Remove-Item -LiteralPath $legacyZip -Force }
        Join-Path $OutputDir 'CrimsonVR-PCVR-Linux.tar.gz'
    }
    & $python.Source $packager --platform $target --source $folder --output $archive
    if ($LASTEXITCODE -ne 0) { throw "$target archive creation failed ($LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "$target archive missing: $archive" }
    Write-Host "Built $archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
}
