# Quest personal-build CI

The Quest workflow is the fallback for the outcome where permission to
redistribute binaries linked with upstream code is not granted. It produces an
asset-free APK from source without requiring the user to install Godot, Zig,
.NET, Java, or the Android SDK locally.

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

## Build contract

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

This is preparation, not the M6 release gate. M6 still needs the on-device
first-run importer and a hash-based audit for derived asset content.
