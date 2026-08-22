# Quest personal-build CI

The Quest workflow is the fallback for the outcome where permission to
redistribute binaries linked with upstream code is not granted. It produces an
asset-free APK from source without requiring the user to install Godot, Zig,
.NET, Java, or the Android SDK locally.

Windows/Linux personal packages follow the parallel policy and workflow in
[`pcvr-ci.md`](pcvr-ci.md).

## Distribution boundary

The project owner has approved source publication plus user-owned fork CI and
user-supplied assets as the release model. GitHub's fork mechanism is explicitly
the supported source path, and the workflow never includes Crimsonland game
assets. The resulting APK is a personal build created in the user's own fork;
the project does not publish a maintainer-built APK as a release asset.

The personal-build flow is:

1. Each tester forks the approved public repository into their own GitHub
   account and selects the approved revision or branch.
2. Ensure `.github/workflows/quest.yml` is on that repository's default branch.
3. Open **Actions -> Quest personal build -> Run workflow**.
4. Set `publish_artifact` to `true`.
5. Download the complete `CrimsonVR-Quest-*` bundle as soon as the run
   completes. It expires after one day.
6. Follow [`quest-playtest.md`](quest-playtest.md) to preserve the signing key,
   install the APK, supply locally owned assets, and record results.

The maintainer repository uses `publish_artifact=false` for validation. A user
sets it to `true` in their own fork to receive their personal asset-free bundle.

## Fresh Quest install

The personal build artifact contains the APK, its signing keystore, and
`QUEST-PERSONAL-BUILD.txt`. Preserve all three. After the first build, encode
the downloaded keystore and save it as the fork's Actions secret
`QUEST_KEYSTORE_BASE64`:

```powershell
$bytes = [IO.File]::ReadAllBytes('.\crimsonvr-ci.keystore')
[Convert]::ToBase64String($bytes) | gh secret set QUEST_KEYSTORE_BASE64 --repo OWNER/REPOSITORY
```

Later workflow runs restore that key and can update the installed APK without
deleting settings, scores, imported assets, checklist results, or replays. The
password and alias remain the personal-build values recorded in the manifest.
If the secret is absent, CI generates a new key and says so in both the log and
manifest; that APK requires uninstalling any differently signed build first.

For a maintainer checkout with ADB available, stage a known-good locally created
asset pack outside app storage and use the guarded clean-install script. Its
default invocation is read-only and prints the exact device, APK, pack, package,
sizes, and hashes:

```powershell
.\crimson-vr\tools\clean_install_quest.ps1 -Device 192.168.8.100:5555
```

After checking that preflight, explicitly authorize the destructive operation:

```powershell
.\crimson-vr\tools\clean_install_quest.ps1 -Device 192.168.8.100:5555 -Execute
```

The script force-stops and uninstalls only `xyz.crimsonvr.app`, installs the
selected APK, pushes `crimson-assets.pack` into the app-owned external inbox,
verifies both package and inbox, and leaves the app stopped. Launch it from the
headset library to perform the first-run import. End users can instead use
`prepare_assets.ps1 -Quest -Apk <path>` after following
[`asset-import.md`](asset-import.md).

## Build contract

This workflow is one gate, not release approval. Distribution scope is settled
for source + user-owned asset-free builds, while physical validation remains open.
Record the workflow run,
artifact hashes, device validation, and legal disposition in the canonical
[Release Preparation](../../docs/contributor/project-tracking/release-preparation.md)
evidence table.

The workflow is manually triggered. Its first run needs no repository secrets;
repeat in-place updates use the optional `QUEST_KEYSTORE_BASE64` secret. It pins:

- Windows Server 2025;
- .NET 9;
- Zig 0.16.0;
- Godot 4.7 stable Mono editor and Mono export templates by SHA-256;
- Godot OpenXR Vendors 5.1.0 by SHA-256.

It rejects a checkout containing the development `godot/assets/` tree or any
PAQ/PAK under the Godot project, runs the managed VR tests, cross-compiles the
ReleaseSafe arm64 host library, installs a fresh Gradle Android template,
exports a release APK, injects the host library, aligns native libraries for
Quest's 16 KiB pages, signs it, and verifies the managed/native/OpenXR payload.
The workflow passes `-AssetMode AssetFree` explicitly; it never inherits the
local builder's bundled-assets default. The dedicated export preset excludes
`assets/**`, and the final APK gate independently verifies that exclusion.

For an owner's own headset, the separate local convenience path is:

```powershell
.\crimson-vr\tools\build_quest.ps1 -Release
```

That creates `CrimsonVR.personal-assets.quest.apk` from locally owned assets and
must not be uploaded to GitHub or shared. See [`asset-import.md`](asset-import.md).

The M6 runtime half is now implemented and tested: the asset-free APK presents
an asset-independent recovery panel, imports a locally created pack atomically,
and has completed a clean on-headset Quest import. Source and APK payload gates
reject bundled original assets. Remaining release work is operational and
physical: close the Quest/PCVR checklists and the operated-relay matrix while
keeping every CI artifact asset-free.

On 2026-08-11 the workflow's build contract was reproduced locally with a fresh
CI-format keystore. The resulting asset-free Release APK
(`9612C1D62AEBD0C462E2206472887C26E6C35DB4413954EEB6906B6675CAB6C4`) was
clean-installed on Quest 3 with the staged pack
(`B43B203B70C3DD181E90EC19F32F6BB620DD89A2DB662292E23BDADF95F0736F`). Android
reported a new first-install time and `stopped=true, notLaunched=true`; remote and
local pack hashes matched.

On 2026-08-22, `test_quest_private_ci.ps1` exercised the complete hosted build
from a fresh clone of commit `7e804816885d3bfb2419c268fd0c7c323af08dd3`:

```powershell
.\crimson-vr\tools\test_quest_private_ci.ps1 -ScratchRoot 'D:\Projects\_scratch'
```

It created an isolated repository with `main` as its default branch, passed the
clean source gate, completed and downloaded two Quest
artifacts, rechecked both APK manifests/hashes and asset-free payloads locally,
stored the first keystore as `QUEST_KEYSTORE_BASE64`, and proved that the second
build reused the exact keystore and APK signing certificate. Evidence:

- generated-key run: [32534616770](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32534616770), APK SHA-256
  `83932d2eebd2355642117f0b2db26fdb32e6c04b0afdf7c50b1e804ab0232fa8`;
- saved-key run: [32535378646](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32535378646), APK SHA-256
  `1ae0ff1eccb957ddded28437eef21f3cf5bc1dfceb192ff34f263099eea4bc32`;
- shared keystore SHA-256
  `da9befc3a82a97e9b2b42f2724dce9bdc14be4981930281910205dddaa5b9465`;
- shared signer certificate SHA-256
  `168999965f03bf6502ed405944449a58d7f00478534aa6d398d708bfad41a090`;
- local evidence file:
  `D:\Projects\_scratch\cvr-e2e-20260822-084949\evidence.json`.

This isolated-repository rehearsal predates the final public-fork policy update,
but exercises the same workflow build, artifact, signing, and update steps. The
operated test exposed and fixed clean-run gaps in Android template ordering,
Godot template metadata, the asset-independent app icon, APK-finalization
waiting, and export-failure logging. The repository is retained for inspection;
its uploaded Actions artifacts expire after one day.
