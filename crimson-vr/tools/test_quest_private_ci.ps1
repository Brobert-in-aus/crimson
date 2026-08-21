# Operated end-to-end test for the private standalone Quest build path.
#
# Creates a brand-new private GitHub repository from a fresh clone of an exact
# pushed revision, dispatches and verifies two asset-free Quest builds, and
# confirms that the second build reuses the first build's signing key. All local
# state and downloaded evidence stays under -ScratchRoot. The private repository
# is deliberately retained for inspection; this script never deletes it.

param(
    [string]$SourceRepo = 'Brobert-in-aus/crimson',
    [string]$SourceBranch = 'vr-control-rectangle',
    [string]$DestinationOwner,
    [string]$RepositoryName,
    [string]$ScratchRoot = 'D:\Projects\_scratch'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Exe,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Argv
    )
    & $Exe @Argv
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe failed with exit code $LASTEXITCODE"
    }
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Exe,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Argv
    )
    $text = (& $Exe @Argv | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe failed with exit code $LASTEXITCODE"
    }
    return $text
}

function Wait-WorkflowIndexed {
    param([string]$Repository)
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        & gh workflow view quest.yml --repo $Repository *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 5
    }
    throw "Quest workflow was not indexed on the default branch of $Repository"
}

function Start-QuestRun {
    param([string]$Repository, [long]$PreviousRunId = 0)
    Invoke-Checked gh workflow run quest.yml --repo $Repository --ref main -f publish_artifact=true
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $json = Invoke-Captured gh run list --repo $Repository --workflow quest.yml `
            --branch main --event workflow_dispatch --limit 5 `
            --json 'databaseId,createdAt,status,conclusion,url,headSha'
        if ($json -and $json -ne '[]') {
            $candidate = $json | ConvertFrom-Json |
                Where-Object { $_.databaseId -ne $PreviousRunId } |
                Sort-Object createdAt -Descending |
                Select-Object -First 1
            if ($candidate) { return $candidate }
        }
        Start-Sleep -Seconds 5
    }
    throw "Dispatched Quest workflow did not appear for $Repository"
}

function Download-And-VerifyBundle {
    param(
        [string]$Repository,
        [long]$RunId,
        [string]$Destination,
        [string]$ExpectedCommit,
        [string]$ExpectedKeySource,
        [string]$ClonePath
    )
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Invoke-Checked gh run download $RunId --repo $Repository --dir $Destination

    $apk = Get-ChildItem $Destination -Filter 'CrimsonVR.quest.apk' -File -Recurse
    $key = Get-ChildItem $Destination -Filter 'crimsonvr-ci.keystore' -File -Recurse
    $manifest = Get-ChildItem $Destination -Filter 'QUEST-PERSONAL-BUILD.txt' -File -Recurse
    if ($apk.Count -ne 1 -or $key.Count -ne 1 -or $manifest.Count -ne 1) {
        throw "Expected exactly one APK, keystore, and manifest under $Destination"
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $manifest.FullName) {
        $split = $line -split '=', 2
        if ($split.Count -eq 2) { $values[$split[0]] = $split[1] }
    }
    if ($values.commit -ne $ExpectedCommit) {
        throw "Manifest commit mismatch: expected $ExpectedCommit, got $($values.commit)"
    }
    if ($values.signing_key_source -ne $ExpectedKeySource) {
        throw "Signing source mismatch: expected $ExpectedKeySource, got $($values.signing_key_source)"
    }
    $apkHash = (Get-FileHash -LiteralPath $apk.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($values.apk_sha256 -ne $apkHash) {
        throw "APK SHA-256 mismatch: manifest=$($values.apk_sha256), downloaded=$apkHash"
    }

    Invoke-Checked pwsh -NoProfile -File (Join-Path $ClonePath 'crimson-vr/tools/assert_asset_free_apk.ps1') `
        -Apk $apk.FullName

    return [pscustomobject]@{
        Apk = $apk.FullName
        ApkSha256 = $apkHash
        Keystore = $key.FullName
        KeystoreSha256 = (Get-FileHash -LiteralPath $key.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Manifest = $manifest.FullName
        SigningKeySource = $values.signing_key_source
    }
}

function Get-ApkSigner {
    $sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT }
        elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME }
        else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    if (-not (Test-Path $sdk)) { throw "Android SDK not found: $sdk" }
    $tool = Get-ChildItem (Join-Path $sdk 'build-tools') -Filter 'apksigner.bat' -File -Recurse |
        Sort-Object { [version]$_.Directory.Name } -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "apksigner.bat not found under $sdk\build-tools" }
    return $tool.FullName
}

function Get-SignerDigest {
    param([string]$Apk, [string]$ApkSigner)
    $output = Invoke-Captured $ApkSigner verify --print-certs $Apk
    $line = $output -split "`r?`n" |
        Where-Object { $_ -match '^Signer #1 certificate SHA-256 digest:' } |
        Select-Object -First 1
    if (-not $line) { throw "Could not read signer digest from $Apk" }
    return ($line -split ':', 2)[1].Trim().ToLowerInvariant()
}

$gh = Get-Command gh -ErrorAction Stop | Select-Object -ExpandProperty Source
$git = Get-Command git -ErrorAction Stop | Select-Object -ExpandProperty Source
$pwsh = Get-Command pwsh -ErrorAction Stop | Select-Object -ExpandProperty Source
$null = $gh, $git, $pwsh # make tool preflight explicit without shadowing commands

Invoke-Checked gh auth status
if (-not $DestinationOwner) {
    $DestinationOwner = Invoke-Captured gh api user --jq .login
}
$sourceCommit = Invoke-Captured gh api "repos/$SourceRepo/commits/$SourceBranch" --jq .sha
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Invalid source commit: $sourceCommit" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $RepositoryName) { $RepositoryName = "crimsonvr-ci-e2e-$stamp" }
if ($RepositoryName -notmatch '^[A-Za-z0-9_.-]+$') { throw "Unsafe repository name: $RepositoryName" }
$destinationRepo = "$DestinationOwner/$RepositoryName"

$scratchFull = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath((Join-Path $scratchFull $RepositoryName))
if (-not $runRoot.StartsWith($scratchFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run directory escaped scratch root: $runRoot"
}
if (Test-Path -LiteralPath $runRoot) { throw "Scratch destination already exists: $runRoot" }
New-Item -ItemType Directory -Force $runRoot | Out-Null

$clone = Join-Path $runRoot 'source'
$repositoryUrl = "https://github.com/$destinationRepo"
$evidencePath = Join-Path $runRoot 'evidence.json'
$repoCreated = $false

try {
    Write-Host "==> Cloning exact source $SourceRepo@$SourceBranch ($sourceCommit)"
    Invoke-Checked git clone --no-tags --single-branch --branch $SourceBranch `
        "https://github.com/$SourceRepo.git" $clone
    $cloneCommit = Invoke-Captured git -C $clone rev-parse HEAD
    if ($cloneCommit -ne $sourceCommit) { throw "Clone commit mismatch: $cloneCommit" }
    Invoke-Checked pwsh -NoProfile -File (Join-Path $clone 'crimson-vr/tools/assert_quest_asset_free.ps1')

    Write-Host "==> Creating private standalone repository $destinationRepo"
    Invoke-Checked gh repo create $destinationRepo --private --disable-issues --disable-wiki `
        --description "Temporary CrimsonVR private Quest CI end-to-end test $stamp"
    $repoCreated = $true
    Invoke-Checked gh auth setup-git
    Invoke-Checked git -C $clone push "$repositoryUrl.git" 'HEAD:refs/heads/main'
    Invoke-Checked gh repo edit $destinationRepo --default-branch main

    $repoState = Invoke-Captured gh repo view $destinationRepo `
        --json 'isPrivate,isFork,defaultBranchRef,url'
    $repoInfo = $repoState | ConvertFrom-Json
    if (-not $repoInfo.isPrivate -or $repoInfo.isFork -or $repoInfo.defaultBranchRef.name -ne 'main') {
        throw "Destination repository is not a private standalone repo with main as default: $repoState"
    }
    Wait-WorkflowIndexed $destinationRepo

    Write-Host '==> Dispatching first build (new generated signing key)'
    $run1 = Start-QuestRun $destinationRepo
    Invoke-Checked gh run watch $run1.databaseId --repo $destinationRepo --interval 15 --exit-status
    $bundle1 = Download-And-VerifyBundle -Repository $destinationRepo -RunId $run1.databaseId `
        -Destination (Join-Path $runRoot 'run-1') -ExpectedCommit $sourceCommit `
        -ExpectedKeySource 'generated_for_this_run' -ClonePath $clone

    Write-Host '==> Saving first-run signing key as the private repository secret'
    $encodedKey = [Convert]::ToBase64String([IO.File]::ReadAllBytes($bundle1.Keystore))
    $encodedKey | & gh secret set QUEST_KEYSTORE_BASE64 --repo $destinationRepo
    if ($LASTEXITCODE -ne 0) { throw 'Could not save QUEST_KEYSTORE_BASE64' }
    $encodedKey = $null

    Write-Host '==> Dispatching second build (must reuse saved signing key)'
    $run2 = Start-QuestRun $destinationRepo -PreviousRunId $run1.databaseId
    Invoke-Checked gh run watch $run2.databaseId --repo $destinationRepo --interval 15 --exit-status
    $bundle2 = Download-And-VerifyBundle -Repository $destinationRepo -RunId $run2.databaseId `
        -Destination (Join-Path $runRoot 'run-2') -ExpectedCommit $sourceCommit `
        -ExpectedKeySource 'repository_secret' -ClonePath $clone

    if ($bundle1.KeystoreSha256 -ne $bundle2.KeystoreSha256) {
        throw 'The repeat build did not return the same signing keystore.'
    }
    $apksigner = Get-ApkSigner
    $signer1 = Get-SignerDigest -Apk $bundle1.Apk -ApkSigner $apksigner
    $signer2 = Get-SignerDigest -Apk $bundle2.Apk -ApkSigner $apksigner
    if ($signer1 -ne $signer2) { throw 'APK signing certificate changed between builds.' }

    $evidence = [ordered]@{
        status = 'PASS'
        tested_at_utc = [DateTime]::UtcNow.ToString('o')
        source_repository = $SourceRepo
        source_branch = $SourceBranch
        source_commit = $sourceCommit
        private_repository = $destinationRepo
        private_repository_url = $repositoryUrl
        scratch_directory = $runRoot
        run_1 = [ordered]@{ id = $run1.databaseId; url = $run1.url; apk_sha256 = $bundle1.ApkSha256; signing_key_source = $bundle1.SigningKeySource }
        run_2 = [ordered]@{ id = $run2.databaseId; url = $run2.url; apk_sha256 = $bundle2.ApkSha256; signing_key_source = $bundle2.SigningKeySource }
        keystore_sha256 = $bundle1.KeystoreSha256
        signer_certificate_sha256 = $signer1
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Host "PASS: private Quest CI end-to-end test" -ForegroundColor Green
    Write-Host "Repository: $repositoryUrl"
    Write-Host "Evidence:   $evidencePath"
}
catch {
    $failure = [ordered]@{
        status = 'FAIL'
        tested_at_utc = [DateTime]::UtcNow.ToString('o')
        error = $_.Exception.Message
        source_repository = $SourceRepo
        source_branch = $SourceBranch
        source_commit = $sourceCommit
        private_repository = if ($repoCreated) { $destinationRepo } else { $null }
        private_repository_url = if ($repoCreated) { $repositoryUrl } else { $null }
        scratch_directory = $runRoot
    }
    $failure | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Host "Retained diagnostic state under $runRoot" -ForegroundColor Yellow
    if ($repoCreated) { Write-Host "Retained private repository: $repositoryUrl" -ForegroundColor Yellow }
    throw
}
