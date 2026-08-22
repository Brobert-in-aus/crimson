# PCVR release-candidate checklist

## Release disposition

The project owner accepted the current PCVR build for the 0.11.0 release on
2026-08-22 after a fresh-profile Windows OpenXR launch and current gameplay test.
The detailed unchecked rows below remain a reusable cross-runtime, Linux, and
post-release coverage matrix; they are not additional 0.11.0 blockers unless a
broader configuration is advertised.

Run this against both Windows x64 ZIP and Linux x64 tar.gz from the exact
candidate workflow. Record commit, workflow URL, package and payload hashes,
OS/GPU/driver, OpenXR runtime, headset/controllers, tester, and date.

## Package and clean-machine setup

- [ ] Hosted workflow produces both packages from an asset-free checkout and
  both pass the PAQ/PAK/pack/assets source and archive gates.
- [ ] `PERSONAL-BUILD.txt` commit and hashes match the downloaded Windows and
  Linux payloads; Linux executable permission survives extraction.
- [ ] On clean Windows, extract the whole ZIP and verify executable, PCK, .NET
  data, OpenXR loader, and `native/win-x64/crimson_host.dll` stay together.
- [ ] On clean Linux, extract the tarball and verify the equivalent files plus
  `native/linux-x64/libcrimson_host.so` and executable permission.
- [ ] `prepare_assets.ps1 -Pcvr` on Windows auto-detects GOG Classic or accepts
  `-GameDir`, rejects the HD remake clearly, and imports without admin access.
- [ ] The equivalent Linux pack/inbox path works from a tester-owned Classic
  installation; missing/corrupt/wrong packs give actionable recovery.
- [ ] Relaunch and application update reuse imported assets; a failed replacement
  preserves the prior working installation.

## OpenXR runtime and headset matrix

- [ ] Windows package boots through Meta Quest Link/Air Link OpenXR.
- [ ] Windows package boots through SteamVR OpenXR on a non-Quest headset.
- [ ] Linux package boots through the supported Monado/SteamVR OpenXR path chosen
  for release, or Linux VR is explicitly marked build-only.
- [ ] Runtime selection failures explain which OpenXR runtime is active/missing
  instead of leaving a flat, blank, or immediately exiting process.
- [ ] Controller poses and all trigger/grip/button/stick animations are correct
  for each tested runtime; optical hands are offered only where tracking exists.
- [ ] Focus loss, dashboard open/close, headset sleep/wake, controller reconnect,
  audio-device change, and runtime restart recover or fail with useful messaging.

## UX and gameplay parity

- [ ] Repeat every applicable item in the
  [Quest headset checklist](quest-release-checklist.md), including first-run,
  Tutorial/Skip, menu distance, both layout modes, perk confirmation, tutorial,
  depth ordering, quit safety, recenter, and saved settings.
- [ ] Complete Survival, Rush, one Quest, Tutorial, death/results, high-score name,
  replay recording, and replay verification on Windows and the supported Linux path.
- [ ] Validate audio, haptics, text legibility, stereo comfort, render-scale and
  edge-smoothing settings, and 30+ minute performance without leaks or drift.

## Networking

- [ ] Windows native/PCVR and Quest complete direct LAN in both host directions.
- [ ] PCVR joins and hosts the operated relay with two, three, and four players.
- [ ] Complete the remaining impairment, reconnect/resync, slot, mode, perk,
  death/results, replay, and progression rows in
  [the multiplayer matrix](multiplayer-implementation.md#post-release-physical-and-operated-matrix).
- [ ] If Python mixed-runtime compatibility is advertised, complete its separate
  cross-runtime proof matrix; otherwise label it unsupported for this release.

## Evidence

Attach workflow manifests, hashes, clean-machine command logs, runtime/headset
matrix, performance captures, failures and dispositions to the canonical release
record. Do not infer Linux runtime support from package construction alone.
