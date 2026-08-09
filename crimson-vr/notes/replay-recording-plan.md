# M4 slice 8 - native .crd replay recorder (implemented)

> **IMPLEMENTED AND QUEST-CONFIRMED 2026-08-09 (ABI v21).** Recording is on by
> default and writes standard `.crd` files under `user://replays`. The real Quest
> recording `20260809-190628.crd` replayed all 6,256 ticks exactly after the live
> and replay perk-offer refresh paths were aligned (160 kills, 8,620 XP, final
> RNG 1127891099). The plan below is retained as the design record; its original
> ABI estimate and “not started” status are historical.

Original status: **not started** (deferred from the 2026-07-09 M4 run as the one
mini-milestone-sized, invasive-ABI slice). Everything below is fully mapped from
the existing `crimson-zig` replay machinery; the hard parts (a msgpack encoder,
the stat computation) already exist and are reused.

Goal (PLAN M4 slice 8): replay recording on by default, saving a standard `.crd`
to the runtime replays dir, and the recorded `.crd` must pass `replay verify`.
Best-practice choice (agreed): the encoder lives in the native deterministic
stack (not C#), so recordings can't drift from the verifier.

## Key facts (already confirmed in the codebase)

- **msgpack encode exists.** `replay_codec.zig` imports `msgpack` and encodes a
  `ReplayWire` via `msgpack.encode(...)`; `buildSmokeTestReplayPayload(allocator)`
  (~line 1321) is a working template that builds a canonical `ReplayWire` from
  fixed inputs and returns encoded bytes.
- **Input wire form is trivial.** `ReplayInputWire = { move_x, move_y, aim_x,
  aim_y, flags: i32 }` (encoded as an array). `flags` packs fire/reload bits +
  move_mode + aim_scheme; the decode is `replay_codec.unpackInputFlags` (line
  172). Add a `pub fn packInputFlags(InputFlags) u32` (invert it): constants
  `fire_down_flag`/`fire_pressed_flag`/`reload_pressed_flag`/`reload_down_flag`,
  `move_mode_present_flag`/`_shift`/`_mask`, `aim_scheme_present_flag`/`_shift`/
  `_mask` (aim_scheme -1 encodes as `raw == mask`). The VR schema uses
  move_mode = 4 (MOUSE_POINT_CLICK), aim_scheme = 0 (MOUSE).
- **verify compares claimed_stats to the re-sim.** `verify_native.zig`
  `buildHeaderClaimPayload` (line 433) compares 7 fields to `run_result`: ticks,
  elapsed_ms, score_xp, kills, most_used_weapon_id, shots_fired, shots_hit
  (`complete` is NOT compared). A mismatch names the exact field, so iterating is
  precise. `run_result` comes from `replay_runner.runReplayWithOptions(replay)`
  (pub, root-exported: `crimson_zig.replay_runner`).

## The robust approach (guarantees verify passes)

Don't hand-source the stats. In `finish()`:
1. Build a `ReplayWire` from the captured inputs + session config, `claimed_stats
   = .{}` (default).
2. Encode -> bytes0. Decode bytes0 -> runtime `Replay`.
   `replay_runner.runReplayWithOptions(replay, .{})` -> `run` (the SAME
   computation verify does).
3. Set `claimed_stats` from `run` (ticks, elapsed_ms_sim, player_experience,
   creature_kill_count, most_used_weapon_id, shots_fired, shots_hit).
4. Re-encode -> final bytes.

Because the recorded inputs + seed are replayed deterministically, `run` equals
the actual play-through, so `claimed_stats` matches by construction. (This mirrors
what the Python recorder does: record inputs, then simulate to fill claimed stats.)

## Implementation steps

1. **`replay_codec.zig`**: add `pub fn packInputFlags`; add a `pub fn
   encodeRecording(allocator, cfg, inputs: []const []const ReplayPlayerInput,
   dt: []const f32, claimed: ReplayClaimedStats) ![]u8` that builds the
   `ReplayWire` (converting to `ReplayInputWire`) and encodes. Keeps the wire
   types file-scope (no pub-surface explosion). `ReplayPlayerInput` +
   `ReplayClaimedStats` are already pub.
2. **`host_abi/exports.zig`**: on `SessionBox`, add recording state (a bool +
   an `ArrayList(ReplayPlayerInput)` + captured `HostSessionConfig` snapshot;
   store the config at create). In `crimson_host_session_tick`, if recording,
   append `ReplayPlayerInput{ move_x, move_y, aim_x, aim_y, flags =
   packInputFlags(from the CrimsonHostInput) }` (player 0). Add exports:
   - `crimson_host_replay_begin(session)` - reset + start capture.
   - `crimson_host_replay_finish(session, buf, len)` - the 4-step encode above,
     on the 64 MiB dispatch thread (the re-sim + encode allocate); same
     buffer-size protocol as `crimson_host_snapshot`.
   Bump `abi_version` 5 -> 6 (new exports only; snapshot layout unchanged, so the
   determinism gate stays green). Update the C header + `Sim.ExpectedAbiVersion`.
3. **Gate test (`host_abi/tests.zig`)**: create session -> begin -> tick ~600
   scripted inputs -> finish -> feed the bytes to
   `crimson_host_verify_replay_json` -> assert the JSON reports `"match": True`.
   This is the oracle: green == the recorder produces verifiable `.crd`.
4. **Frontend**: `SimSession.ReplayBegin()` / `ReplayFinish() -> byte[]` (grow +
   retry buffer). `Main` calls begin at session start/restart, and on death (in
   `RestartGame`, before `Restart()`) calls finish and writes the bytes to
   `user://replays/<timestamp>.crd` (Godot FileAccess). Recording is on by
   default.
5. Rebuild both native libs (`build_libcrimson.ps1 -android`).

## Risks / watch-items

- **Config fidelity**: the header must reflect the exact session config (seed,
  game_mode_id, world_size, tick_rate, player_count, detail_preset, gore_disabled,
  hardcore, preserve_bugs; bootstrap_kind "none", quest_level "", input_quantization
  "f32", a plausible game_version). Wrong seed/config -> re-sim diverges -> verify
  fails. Snapshot the parsed `HostSessionConfig` at create for this.
- **Input capture point**: capture what the runtime actually consumed for player 0
  each ticked frame (the ABI already maps CrimsonHostInput -> FrameInput). Multi-
  substep frames: the LiveRunner may advance >1 tick per stepFrame under load; the
  recording must have one input row per SIM TICK, not per frame. Confirm the frame
  advanced exactly one tick (the VR loop uses a fixed 60 Hz accumulator, so it
  should be 1:1, but assert it).
- **Memory/threads**: finish runs on the big-stack thread (like session init); the
  ArrayList + encoded buffer are gpa-allocated and freed after copy-out.
- It is only "done" when the slice-8 gate test is green (record -> verify ->
  match). Iterate on the field the mismatch message names.
