# User-supplied asset import

## Decision

Public/personal builds contain no Crimsonland art or audio. Assets are not
injected into the APK or PCVR executable: a user-owned Crimsonland Classic
1.9.93 installation is converted locally into a versioned
`crimson-assets.pack`, then imported into Godot's writable `user://assets`
directory. `res://assets` remains a development-only fallback.

One pack is preferable to granting broad Android storage access. On Quest the
helper transfers the pack into CrimsonVR's app-owned external-files inbox. On
PCVR the same helper auto-detects Classic and fills the per-user inbox before
launch. APK/application updates reuse the import. Uninstalling removes
app-private data, so the source pack should be retained for re-import.

The required source is the GOG bonus **Crimsonland Classic**, containing
`crimson.paq`, `sfx.paq`, and loose Ogg files under `music/`. The 2014 HD remake
contains `data.pak` and is incompatible; every entry point must diagnose that
case and direct the user to GOG Galaxy -> Crimsonland -> Extras -> Crimsonland
Classic.

## User flow

### PCVR

1. Run `prepare_assets.ps1 -Pcvr`; it auto-detects the usual GOG installation,
   validates Classic versus HD, then extracts, bakes, and fills the PCVR inbox.
2. Launch CrimsonVR; it installs the pack before XR resources are cached.
3. Later launches load `user://assets`. Re-running the helper safely replaces a
   broken or old installation; a failed replacement preserves the working copy.
4. If the helper was not used, an asset-free desktop launch offers a `.pack`
   picker as a recovery path.

### Quest

1. Install the asset-free personal APK.
2. On a PC, run the distributable asset helper against the Classic directory.
   It builds `crimson-assets.pack` and uses ADB to copy it to the app-owned
   `/sdcard/Android/data/xyz.crimsonvr.app/files/` inbox.
3. First launch detects, validates, and atomically imports it into
   `user://assets`, then archives the inbox pack to prevent repeat imports.
4. Retain the PC copy of the pack for uninstall/reinstall recovery.

The helper is original tooling and ships no game data. A pack is created and
transferred only on the user's machine and must never be uploaded by CI.

## Pack contract (schema 1)

```text
crimson-assets.json
sprites/sprite_manifest.json
sprites/*.png
audio/audio_manifest.json
audio/**/*.ogg
```

The `.pack` is a ZIP container. Its marker contains `schema_version`, a
deterministic content hash, and a SHA-256 for every payload. Import rejects path
traversal, missing manifests, unsupported schemas, and hash mismatches. A staged
atomic swap ensures a failed import preserves the last working installation.

## Implementation status

- [x] Central store prefers complete `user://assets` and retains the
  `res://assets` development fallback.
- [x] All frontend PNG/Ogg/manifest callers support the writable asset root.
- [x] Deterministic local pack builder (`tools/pack_assets.py`).
- [x] Traversal-safe, integrity-checked, atomic installer with unit tests.
- [ ] **PARTIAL:** Desktop has automatic and manually selected pack inboxes plus
  a persistent diagnostic; a polished non-XR bootstrap screen remains.
- [x] Quest ADB inbox import needs no Android storage permission or plugin.
- [x] The checkout helper auto-discovers Classic, diagnoses the HD
  remake and ADB authorization/device ambiguity, prepares PCVR, and can install
  an APK plus transfer to Quest without launching.
- [ ] *(Optional post-release UX)* Package extraction/baking without a Python
  checkout. The clone-first flow needs only the repository's existing `uv`
  prerequisite and is the supported release flow.
- [ ] **PARTIAL:** Disk-space and archive-size preflights plus atomic retry are
  implemented; progress/cancel and schema-migration UI remain.
- [ ] **PARTIAL:** Source and APK-output asset-free gates are integrated into
  CI/local builds; clean PCVR import passes and on-headset import remains.

## Verification log

- 2026-08-09: two independent builds of a 258-file local pack were byte-for-byte
  deterministic; installer traversal, rollback, schema, and SHA-256 failure
  paths pass in the frontend test suite.
- 2026-08-09: an Android release export excluded `assets/**`; APK inspection
  found no sprite/audio tree, PAQ/PAK, or `.pack` payload. The signed APK and a
  pack in its app-owned inbox were pushed to the Quest, then the package was
  force-stopped without launching. On-headset consumption remains the next
  launch check.
- 2026-08-09: a real PCVR inbox import and subsequent atomic re-import both
  completed using `user://assets`. Cross-platform hash ordering was corrected,
  Godot `.import` cache sidecars were removed from the format, and the cleaned
  pack contains 130 declared payload files plus its marker.
- 2026-08-09: Quest's .NET `DriveInfo` reported zero free bytes against the
  wrong filesystem root despite 79 GiB being available. Android now skips that
  unreliable advisory probe; bounded staged extraction remains the authority.

The foundation can be exercised manually today:

```powershell
crimson extract "C:\Games\Crimsonland Classic" artifacts/assets
uv run crimson-vr/tools/bake_assets.py artifacts/assets crimson-vr/godot/assets/sprites
uv run crimson-vr/tools/pack_assets.py crimson-vr/godot/assets crimson-assets.pack
# Or perform the complete pipeline (add --quest to transfer without launching):
uv run python crimson-vr/tools/prepare_assets.py "C:\Games\Crimsonland Classic"
# One-command PCVR inbox preparation:
.\crimson-vr\tools\prepare_assets.ps1 -Pcvr
# Complete Quest setup (installs, transfers, and leaves the app stopped):
.\crimson-vr\tools\prepare_assets.ps1 -Quest -Apk ".\CrimsonVR.quest.apk"
```

The wrapper searches the usual GOG locations. Add `-GameDir "D:\...\Crimsonland Classic"`
only for a custom installation path.
