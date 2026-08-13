# Quest personal-build CI

The Quest workflow is the fallback for the outcome where permission to
redistribute binaries linked with upstream code is not granted. It produces an
asset-free APK from source without requiring the user to install Godot, Zig,
.NET, Java, or the Android SDK locally.

Windows/Linux personal packages follow the parallel policy and workflow in
[`pcvr-ci.md`](pcvr-ci.md).

## Why this is not a normal public-fork artifact

All forks of a public GitHub repository are public. Workflow artifacts in a
public repository can be downloaded by any signed-in GitHub user with read
access. Uploading `CrimsonVR.quest.apk` from a public fork would therefore be
binary redistribution, even with one-day retention.

The no-grant flow is instead:

1. Import or mirror this repository into a new **private standalone GitHub
   repository**. Do not use GitHub's Fork button, because a public fork cannot
   be made private.
2. Ensure `.github/workflows/quest.yml` is on that repository's default branch.
3. Open **Actions -> Quest personal build -> Run workflow**.
4. Set `publish_artifact` to `true`.
5. Download `CrimsonVR-Quest-*` as soon as the run completes. It expires after
   one day.
6. Keep both the APK and `crimsonvr-ci.keystore`. The key and password recorded
   in `QUEST-PERSONAL-BUILD.txt` are required to sign an in-place update. A new
   CI run currently generates a new key, so installing its APK requires
   uninstalling the previous build unless the saved key is supplied locally.

On a public repository, leave `publish_artifact=false`. CI performs the entire
clean build and payload validation but deliberately uploads no binary.

## Fresh Quest install

The private build artifact contains the APK, its generated signing keystore, and
`QUEST-PERSONAL-BUILD.txt`. Preserve all three. Because a new workflow run
currently generates a new key, replacing an existing CrimsonVR install normally
requires uninstalling `xyz.crimsonvr.app`; that erases its settings, scores,
imported assets, and replays. Pull anything valuable first.

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

This workflow is one gate, not release approval. The 2026-08-13 audit remains a
no-go because native networking, private-copy dispatch, physical validation,
and legal/distribution work are still open. Record the eventual workflow run,
artifact hashes, device validation, and legal disposition in the canonical
[Release Preparation](../../docs/contributor/project-tracking/release-preparation.md)
evidence table.

The workflow is manually triggered, needs no repository secrets, and pins:

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

The M6 runtime half is now implemented and tested: the asset-free APK presents
an asset-independent recovery panel, imports a locally created pack atomically,
and has completed a clean on-headset Quest import. Source and APK payload gates
reject bundled original assets. Remaining release work is operational/legal:
exercise the private-copy workflow end to end, complete the derived-content
audit, and settle what upstream-linked binaries/code may be redistributed.

On 2026-08-11 the workflow's build contract was reproduced locally with a fresh
CI-format keystore. The resulting asset-free Release APK
(`9612C1D62AEBD0C462E2206472887C26E6C35DB4413954EEB6906B6675CAB6C4`) was
clean-installed on Quest 3 with the staged pack
(`B43B203B70C3DD181E90EC19F32F6BB620DD89A2DB662292E23BDADF95F0736F`). Android
reported a new first-install time and `stopped=true, notLaunched=true`; remote and
local pack hashes matched. This validates the build/install contract, not the
still-pending real private-repository GitHub Actions dispatch.
