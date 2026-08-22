# Quest release-candidate headset checklist

## Release disposition

The project owner accepted the current Quest build for the 0.11.0 release on
2026-08-22 after the clean asset-free install, first-run import, onboarding, and
current gameplay test. The detailed unchecked rows below remain a reusable
coverage and post-release hardening matrix; they are not additional 0.11.0
blockers unless their behaviour is advertised beyond the tested configuration.
Release exports hide the developer debug-menu toggle.

Use this checklist on the exact candidate commit and record the APK SHA-256,
headset model, Quest system version, OpenXR runtime, input method, tester, and
date. A pass without those fields is not release evidence. Test Quest 3 first;
repeat the performance, input-model, and complete-match sections on Quest 2.

## Candidate and clean install

- [ ] Record candidate commit, workflow run, APK SHA-256, signer certificate,
  headset model/OS, and tester/date.
- [ ] Confirm the CI APK passes the asset-free payload gate and contains no
  PAQ/PAK/pack, game manifests, or baked game art/audio.
- [ ] Uninstall `xyz.crimsonvr.app`, install the candidate without launching,
  stage a pack made from the tester's own Classic install, and record both hashes.
- [ ] First launch imports once, reports useful progress/errors, and shows the
  first-run panel only after import succeeds.
- [ ] Relaunch reuses imported assets without showing recovery or first-run again.
- [ ] Missing, corrupt, and wrong-version packs produce actionable recovery and
  Retry succeeds without reinstalling the APK.

## First-run and menu UX

- [ ] First-run shows **Edit Layout** on the left and **Skip** on the right; text
  stays inside and in front of its panel in both eyes.
- [ ] First Edit Layout entry shows the movable-object guide once, then exposes
  handles after **Start Editing**.
- [ ] First **Play Game** shows **Tutorial / Skip** once; both routes are correct
  and the prompt does not repeat after either choice.
- [ ] Main, options, controls, statistics, databases, multiplayer, perk, results,
  keyboard, error, and tutorial panels share the chosen comfortable menu depth.
- [ ] The blue menu-distance grip moves only toward/away, is labelled the right
  way round, sits above perk-preview toggle, persists, and never changes height,
  yaw, pitch, or lateral position.
- [ ] Every seated menu row is reachable and crisp in stereo; sprites never draw
  over panel text.
- [ ] Quit/leave confirmations require a deliberate second movement and cannot
  be triggered by the same hand position as the initiating button.
- [ ] Hold-to-recenter progress appears in front of the player, not at a stale
  menu position, and release-before-threshold cancels safely.

## Layout modes and interaction

- [ ] Tabletop arena is horizontal. Height reads upward correctly, defaults to
  0.5 m, and the size button cycles Small 0.75× / Medium 1.0× / Large 1.25×.
- [ ] Cabinet clean defaults match the approved arena, control-pad, Pause, and
  Level Up placement; all are comfortably reachable while seated.
- [ ] Grabbing Pause/Level Up maps controller forward/back rotation directly,
  not inverted, and two-point manipulation remains stable.
- [ ] Perk preview toggle actually hides/shows the preview and leaves a clear
  edit view in both Tabletop and Cabinet.
- [ ] Poke markers default on. Direct poke works across all panels without
  accidental double presses; record whether an optional ray is still needed.
- [ ] Perk cards show information before commitment; Confirm appears only after
  selection and disappears before the fresh offer, preventing hand-overlap picks.
- [ ] Pause and Level Up are easy to locate during play and retain saved placement.

## Tutorial and tracked input

- [ ] Tutorial panel stays out of the play arena, faces the player in Cabinet and
  Tabletop, advances after every clear/collect gate, and reaches completion.
- [ ] Controller models track full six-degree pose. Triggers, grips, face/menu
  buttons, and both stick axes animate with the correct direction on both hands.
- [ ] Switching to optical hands automatically replaces controllers with the
  chosen glove; every reported joint follows one-to-one and controllers return
  when hand tracking ends.
- [ ] Focus loss, permissions/interstitials, sleep/resume, boundary recenter, and
  controller reconnect recover without a blank screen or stale interaction state.

## Gameplay, comfort, and performance

- [ ] Complete Survival, Rush, one Quest, Tutorial, perk selection, pause/resume,
  death/name entry, results, replay recording, and return-to-menu flows.
- [ ] Stereo depth, scale, tilt, text size, haptics, positional audio, and 30+
  minute seated comfort are acceptable with no persistent arm/neck strain.
- [ ] Record sustained frame timing, thermal state, memory, battery drain, and
  rollback catch-up behaviour on Quest 3 and Quest 2; no progressive degradation.
- [ ] Complete direct-LAN and operated-relay rows in
  [the multiplayer matrix](multiplayer-implementation.md#post-release-physical-and-operated-matrix).

## Evidence

Attach checklist results, failures with reproduction steps, screenshots/video,
logs, workflow URL, hashes, and explicit dispositions for any waived row to the
release evidence record. Every failure must be fixed, deferred as a documented
known limitation, or removed from the advertised release scope.
