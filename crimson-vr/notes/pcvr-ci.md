# PCVR personal-build CI

Windows and Linux PCVR use the same personal-build model as Quest: each user
forks the source and may request short-lived, asset-free packages from their own
fork. The project does not attach maintainer-built packages to a release.

## User flow

1. Fork the approved repository into your GitHub account.
2. Open **Actions -> PCVR personal build -> Run workflow**.
3. Set `publish_artifact` to `true`.
4. Download `CrimsonVR-PCVR-*` within one day and choose the Windows ZIP or Linux
   tar.gz.
5. Extract the whole archive. Do not move the executable away from its `data_*`,
   `native`, PCK, or OpenXR-library siblings.
6. Prepare the legally owned Crimsonland Classic assets locally. On Windows,
   `crimson-vr\setup_pcvr.bat` detects the normal GOG locations and fills the
   per-user inbox; then run `CrimsonVR.exe` with SteamVR, VDXR, or another active
   OpenXR runtime. Pass a custom Classic directory as the batch file's first
   argument when auto-detection is not suitable.

No signing key is needed for PCVR updates. Application upgrades reuse the
per-user imported assets; keep the locally generated pack for recovery.

## Build contract

This workflow is one gate, not release approval. The project owner has approved
the source + user-owned CI + user-supplied-assets release model; no game assets
are included. Clean-machine packaging, physical validation, and operated-relay
work remain open. Record the eventual workflow run, package manifests/hashes,
platform smoke tests, and distribution scope in the
canonical [Release Preparation](../../docs/contributor/project-tracking/release-preparation.md)
evidence table.

`.github/workflows/pcvr.yml` is manual, secret-free, and runs on Windows Server
2025. It pins .NET 9, Zig 0.16.0, and the SHA-256-verified Godot 4.7 Mono editor
and export templates. One runner:

- rejects a checkout containing original asset payloads;
- runs the managed frontend tests;
- builds ReleaseSafe `crimson_host.dll` for Windows x64;
- cross-builds ReleaseSafe `libcrimson_host.so` for Linux x64;
- exports asset-free Godot/.NET packages for both platforms;
- fails if the export log packs `res://assets`, if a loose PAQ/PAK/pack or asset
  tree appears, or if the executable, PCK, managed assembly or native library is
  missing;
- places each native library loose under `native/<rid>` so .NET can load it;
- records executable, PCK, managed-assembly and native-library hashes in
  `PERSONAL-BUILD.txt`;
- packages Windows as ZIP and Linux as tar.gz with the executable bit preserved;
- uploads the archives for one day only when the fork owner explicitly requests
  publication.

The same path is locally reproducible on Windows:

```powershell
.\crimson-vr\tools\build_libcrimson.ps1 -Linux
.\crimson-vr\tools\build_pcvr.ps1 -Godot "C:\path\to\Godot_console.exe"
```

`bootstrap_pcvr_ci.ps1` can restore the pinned editor/templates on a clean
machine. Local output lands under `artifacts/pcvr/`.

Package construction alone is not runtime approval. Complete the Windows/Linux
clean-machine and headset matrix in the
[PCVR release checklist](pcvr-release-checklist.md).
