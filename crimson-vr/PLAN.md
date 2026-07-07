# Crimsonland VR — Conversion Plan

A VR "diorama" frontend for the Crimsonland rewrite: the arena is rendered as a
tabletop playfield in world space, viewed from any angle (isometric-by-posture),
with motion controllers projected vertically onto the playfield plane to drive
movement and aiming.

This document is the source of truth for the VR effort. It assumes the
decisions recorded below; change them here first if they change.

---

## 1. Locked decisions

| Topic | Decision |
|---|---|
| Platforms | PCVR (SteamVR / Virtual Desktop) first, Quest 3 standalone second, SteamOS / Steam Frame later |
| Dev headset | Quest 3 (standalone + Virtual Desktop PCVR) |
| Engine | Godot 4.x (latest stable), OpenXR, C# frontend |
| Simulation | Embed `crimson-zig` runtime as a C-ABI native library (`libcrimson`) — **no gameplay reimplementation** |
| Hands | Left = movement, right = aim/fire, swappable in settings |
| Movement trigger | Player moves toward the move-hand reticle **only while that hand's trigger is held** |
| Presentation | 2.5D: flat terrain plane + billboarded/lifted sprites |
| Arena scale | Uniform scale, user-adjustable from 0.4 m up to play-area fit; override allows much larger |
| Grabbable arena | Deferred (revisit post-v1; likely a novelty that degrades play) |
| v1 scope | Survival mode only, minimal VR-native menu shell |
| Upstream | No upstreaming; develop in this fork as `crimson-vr/` |
| Distribution | Public open source intended for **our own code only**; distributed builds ship **no original assets** — users supply their own legally obtained Crimsonland files (or a future original asset set). See §10 and M6. |
| Replays | VR sessions record standard `.crd` replays |

---

## 2. Architecture overview

```
┌────────────────────────────────────────────────────────────┐
│ Godot 4 (.NET) — crimson-vr/godot/                         │
│                                                            │
│  OpenXR (headset + controllers)                            │
│    │ controller poses (render rate, 72–120 Hz)             │
│    ▼                                                       │
│  InputMapper ── vertical projection onto arena plane ──►   │
│    game-space move target / aim point / buttons            │
│    │                                                       │
│    ▼ (fixed 60 Hz accumulator)                             │
│  P/Invoke ────────────────────────────────────────────┐    │
└───────────────────────────────────────────────────────┼────┘
                                                        ▼
┌────────────────────────────────────────────────────────────┐
│ libcrimson (crimson-zig, C ABI shared library)             │
│   crimson_host_session_create(mode, seed, config)          │
│   crimson_host_session_tick(inputs) -> tick result         │
│   crimson_host_snapshot(...)   entity/effect/HUD state     │
│   crimson_host_audio_events(...)  drained per tick         │
│   crimson_host_replay_*        .crd record/flush           │
│   (deterministic runtime, identical to desktop/replay)     │
└────────────────────────────────────────────────────────────┘
                                                        ▲
                    verified against existing tooling:  │
                    replay verify / checkpoint diff ────┘
```

Principles:

- **The sim is a black box.** All gameplay, RNG, timing, and float behavior
  stays in `crimson-zig/src/runtime/`. The VR layer only produces `GameInput`
  and consumes snapshots. Behavioral parity and the differential-testing
  infrastructure are preserved for free.
- **The ABI is the contract.** Every piece of LLM-generated code on either side
  of the boundary is verifiable: the Zig side by replaying known-good `.crd`
  files through the ABI and diffing checkpoints, the C# side by golden-input
  unit tests against the mapper math.
- **The frontend is thin.** Rendering, input mapping, audio playback, menus,
  settings. Nothing in the frontend may influence simulation outcomes except
  through `GameInput`.

### Repo layout

```
crimson-vr/
  PLAN.md              this document
  godot/               Godot 4 project (frontend)
    project.godot
    src/               C# code (Sim bindings, InputMapper, renderers, UI)
    scenes/            .tscn (text) scenes
    assets/            imported atlases (generated, gitignored)
  abi/                 C header for libcrimson host ABI (source of truth)
  tools/               asset bake scripts (PAQ -> atlas), build glue
crimson-zig/
  src/host_abi/        new: C-ABI export layer (peer of the WASM ABI)
```

The fork remains a monorepo. Upstream (`banteg/crimson`) is pulled into
`master` periodically; `crimson-vr/` and `crimson-zig/src/host_abi/` are
additive, so merge friction stays low.

---

## 3. libcrimson host ABI

A new export set in `crimson-zig` (peer of the existing freestanding WASM ABI,
built as a normal shared library: `.dll` / `.so` / Android `.so`). It wraps the
existing `live_runner` (`crimson-zig/src/runtime/live_runner.zig`), which
already provides exactly the needed loop: `FrameInput` in, state + audio
events out.

### Exports (v1 surface)

```c
// Versioning — checked by the frontend at startup.
uint32_t crimson_host_abi_version(void);

// Session lifecycle. config_json mirrors LiveModeConfig
// (seed, game_mode, player_count, world_size, tick_rate, ...).
int32_t  crimson_host_session_create(const char* config_json, /*out*/ uint64_t* session);
void     crimson_host_session_destroy(uint64_t session);

// Fixed-timestep tick. inputs = packed CrimsonHostInput[player_count].
int32_t  crimson_host_session_tick(uint64_t session, const CrimsonHostInput* inputs,
                                   uint32_t input_count, /*out*/ CrimsonHostTickResult* result);

// State snapshot for rendering: flat, packed, versioned struct arrays.
// Caller owns the buffer; function reports required size when buf is null.
int32_t  crimson_host_snapshot(uint64_t session, /*out*/ uint8_t* buf, /*inout*/ uint32_t* len);

// Audio events accumulated during the last tick (FrameAudioEvents, flattened).
int32_t  crimson_host_audio_events(uint64_t session, /*out*/ uint8_t* buf, /*inout*/ uint32_t* len);

// Replay recording (standard .crd, same format as desktop).
int32_t  crimson_host_replay_begin(uint64_t session);
int32_t  crimson_host_replay_finish(uint64_t session, /*out*/ uint8_t* buf, /*inout*/ uint32_t* len);

// Perk menu interaction (mirrors FrameInput.perk_choice_index / perk_menu_active).
// Folded into CrimsonHostInput rather than separate calls.

// Error reporting.
int32_t  crimson_host_last_error(/*out*/ char* buf, uint32_t len);
```

`CrimsonHostInput` mirrors `GameInput` (`crimson-zig/src/runtime/player.zig`):

```c
typedef struct {
  float move_x, move_y;        // analog move vector (game units/normalized)
  float aim_x, aim_y;          // aim point in game-world coordinates
  uint32_t flags;              // fire_down, fire_pressed, reload_pressed,
                               // reload_down, move_to_cursor_pressed bits
  int32_t move_mode;           // MovementControlType or -1
  int32_t aim_scheme;          // AimScheme or -1
  int32_t perk_choice_index;   // -1 when not choosing
  uint8_t perk_menu_active;
  uint8_t _pad[3];
} CrimsonHostInput;
```

### Snapshot contents (v1)

Flat arrays, little-endian, fixed layouts, a version/word-count header. No
pointers, no allocation on the Zig side beyond the session.

- **Players**: pos, facing angle, health, weapon id, ammo/clip, reload
  progress, XP/level, active perk flags needed for presentation.
- **Creatures**: pos, angle, scale, creature type, anim frame, hit flash,
  alive/dying state.
- **Projectiles + secondary projectiles**: pos, angle, type.
- **Bonuses/pickups**: pos, type, remaining time.
- **Particles/effects**: pos, type, frame, alpha (or: effect *spawn events*
  and let the frontend simulate visual-only particle motion — decided during
  M2 by measuring snapshot bandwidth; start with full state, it's simpler and
  a few thousand packed structs at 60 Hz is trivial over P/Invoke).
- **Terrain decal events**: append-only stream of (decal type, pos, angle,
  scale, tint) since last drain — the frontend paints them into a decal
  texture. Terrain base is generated once at session start (expose terrain
  seed/tile ids in session info).
- **Game/UI state**: mode timer, score, perk-menu-open + candidate perk ids,
  game-over state.

### Verification (the hard gate for LLM-written ABI code)

`crimson-zig` gains a test target: for each committed `.crd` fixture, drive the
session **through the public C ABI** (create → tick with replay inputs →
snapshot each N ticks) and assert the final stats/checkpoints match
`replay verify` output exactly. Any LLM change to the ABI or runtime that
breaks determinism fails this gate. This reuses the project's strongest
existing asset — the parity/replay infrastructure — as an automated reviewer.

---

## 4. Coordinate mapping and controller projection

### Spaces

- **Game space**: 2D, origin top-left, `x ∈ [0, world_size]`, `y ∈ [0,
  world_size]`, `world_size = 1024` for Survival. Square.
- **Arena space**: a square in Godot world space; node `ArenaRoot` with
  uniform scale `s` (meters per full arena side / world_size), positioned at
  height `h` (playfield plane), yaw `θ` (user-facing rotation).
- Mapping: `godot_pos = ArenaRoot.transform * vec3(gx * k, 0, gy * k)` where
  `k = arena_side_m / world_size`. Game +y (screen down) maps to Godot +z so
  the arena reads correctly when the player stands at its "south" edge.

### Controller projection (the core mechanic)

Per rendered frame, for each hand:

1. Take the controller grip pose position `p` in Godot world space.
2. Project **vertically along world −Y** onto the arena plane `y = h`:
   `hit = (p.x, h, p.z)`. (Spec: straight down/up the Y axis — *not* a pointer
   ray along the controller's facing. Controller orientation is ignored for
   targeting.)
3. Transform `hit` into arena-local space, then into game space; clamp to
   `[0, world_size]²`.
4. Render a reticle at `hit` on the plane (always, both hands, distinct
   visuals; a faint vertical guide line from controller to reticle, since
   vertical projection is not something players can otherwise see).

Edge handling: if a hand is outside the arena footprint, clamp the reticle to
the arena edge and dim it. Aim uses the clamped point; movement while clamped
still moves toward the clamped edge point.

### Input mapping (per sim tick, sampled from latest poses)

| VR input | Sim input |
|---|---|
| Move-hand reticle, trigger **held** | `move_mode = MOUSE_POINT_CLICK`, `aim`-independent move target = reticle point, `move_to_cursor` semantics; move vector derived exactly as the desktop point-click mode does |
| Move-hand trigger released | zero move vector (player stops) |
| Aim-hand reticle | `aim_x/aim_y` = reticle point, `aim_scheme = MOUSE` |
| Aim-hand trigger | `fire_down` / `fire_pressed` |
| Aim-hand lower (grip or A/X) button | `reload_pressed` |
| Either hand, menu button | pause |
| Perk menu open | reticle-over-card + aim-hand trigger selects → `perk_choice_index` |
| Settings toggle | swap hand roles (move ↔ aim/fire) |

Notes:

- Dead zone: if the move reticle is within a small radius of the player
  (~12 game units, tuned in M4), treat as zero movement to prevent jitter
  when "standing on" your own marker.
- The sim ticks at 60 Hz via a fixed accumulator inside `_PhysicsProcess`;
  controller poses are sampled at tick time (latest predicted pose). Rendering
  interpolates entity positions between the last two snapshots at headset
  refresh (72/80/90/120 Hz).
- Because everything funnels through `CrimsonHostInput`, `.crd` recording is
  identical to desktop and replays verify with existing tooling.

---

## 5. Arena placement and scaling

- **Uniform scale only.** Arena side length `L` in meters:
  - Minimum: **0.4 m**.
  - Default/maximum with known play bounds: largest axis-aligned square that
    fits the OpenXR play-area rectangle (`XR_REFERENCE_SPACE_TYPE_STAGE`
    bounds), minus a 0.25 m safety margin per side.
  - Fallback when bounds are unavailable (common on PCVR/Virtual Desktop):
    default `L = 1.0 m`, slider max **2.0 m**.
  - Override: an "I know what I'm doing" setting unlocks the slider up to
    **10 m** (room-scale walk-inside-the-arena mode). Persisted per user.
- Height `h`: default 0.75 m (desk height), adjustable 0.4–1.4 m; a "recenter"
  action places the arena centered in front of the current head pose at the
  configured height and snaps yaw to face the player.
- Seated and standing both work by construction (arena is world-anchored;
  posture just changes viewing angle — the "isometric" look).
- Grabbable/rotatable arena: **not in v1** (agreed: likely a
  wanted-but-regretted feature). The recenter action + yaw snap covers the
  legitimate need. Revisit after playtesting.

---

## 6. Presentation (2.5D)

- **Terrain**: flat quad with the generated terrain texture; decals (blood,
  scorch) painted into a `SubViewport` decal layer composited over it —
  driven by the ABI decal event stream.
- **Creatures/players**: original sprites on quads, **lifted slightly above
  the plane and tilted toward vertical** (classic 2.5D billboard-at-fixed-tilt,
  not full camera-facing billboards — with a tabletop viewed from above, a
  ~30–40° back-tilt reads as "standing" from typical viewing angles without
  breaking when the user walks around; tune in M3). Drop shadows as flat
  blob quads on the plane to ground them.
- **Projectiles/effects**: flat on the plane (they read as ground-plane
  phenomena) except muzzle flashes/explosions, which get slight lift.
- **Instancing**: `MultiMeshInstance3D` per atlas layer (terrain decals,
  creatures, projectiles, particles) — thousands of quads in a handful of
  draw calls. This is the single most important Quest-perf decision.
- **HUD**: health/ammo/XP as small panels attached to the arena's near edge,
  plus ammo count floating at the aim reticle. Perk menu: cards hovering
  above the arena center, selected by aim-reticle + trigger.
- **Audio**: `FrameAudioEvents` from the ABI → positional `AudioStreamPlayer3D`
  at the emitting entity's arena position; music/UI audio non-positional.
  SFX/music come from the existing `sfx.paq` / `music.paq` extraction.
- **Haptics**: fire = short pulse on aim hand scaled by weapon; player-hit =
  strong pulse both hands; reload-complete = tick on aim hand.

### Asset pipeline

Build-time step during development, becoming a **first-run, on-device import
wizard** before public release (see M6). Uses the existing extractors
(`crimson extract` / `zig build asset-extract`):

1. Locate PAQs from a **user-provided** Crimsonland Classic install
   (GOG copy; the extractors already accept `--assets-dir path/to/game_dir`).
   During private development only, the upstream first-launch PAQ download is
   an acceptable convenience.
2. Extract PAQs → PNGs (already supported, JAZ→PNG with alpha).
3. Pack into atlases + a JSON manifest (sprite name → atlas, uv rect, pivot),
   consumed by both the C# renderer and kept diffable in git (manifest only;
   atlas PNGs are local build artifacts, gitignored — see §10 licensing).
4. Convert SFX/music to Ogg for Godot import.

Nothing derived from original assets is ever committed or shipped in a
distributed build; the import runs on the user's machine against files they
own. The manifest must therefore key off PAQ contents (names/dimensions), not
hand-tuned per-sprite data that would embed asset-derived information beyond
factual metadata.

---

## 7. Platform matrix

| Platform | Frontend | libcrimson | Status/notes |
|---|---|---|---|
| Windows PCVR (SteamVR/VD) | Godot Windows x64 | `zig build` → `crimson.dll` (x86_64-windows) | Primary dev target; test via Virtual Desktop + Quest 3 |
| Quest 3 standalone | Godot Android export, OpenXR (Meta loader) | `libcrimson.so` (aarch64-linux-android) — Zig cross-compiles natively | **Validate C#-on-Android export in M0** (see risks) |
| Linux / SteamOS x64 | Godot Linux x64 | `libcrimson.so` (x86_64-linux) | Low-effort byproduct |
| Steam Frame (SteamOS ARM) | Godot Linux arm64 export | `libcrimson.so` (aarch64-linux) | Speculative until hardware/spec ships; entire stack is portable, revisit then |

Zig's first-class cross-compilation means one build machine produces all four
`libcrimson` binaries; `justfile` gains `just vr-lib-all`.

---

## 8. Milestones

Each milestone has explicit verification criteria — these double as the review
gate for LLM-generated work.

### M0 — Spikes (de-risk, throwaway code allowed)
- Godot 4 + C# + OpenXR "hello world" running on: PCVR via Virtual Desktop,
  **and exported to Quest 3 standalone**.
- Controller vertical-projection demo: plane + two reticles + guide lines.
- ✅ *Verify*: both builds render at native refresh with head/controller
  tracking; reticles track hands correctly. **Go/no-go on Godot C# Android
  export happens here** (fallback plan in §9).

### M1 — libcrimson host ABI
- `crimson-zig/src/host_abi/` implementing §3; builds for win-x64 first.
- C header in `crimson-vr/abi/crimson_host.h` as the checked-in contract.
- ✅ *Verify*: new Zig test target replays ≥3 committed `.crd` fixtures
  through the C ABI and matches `replay verify` stats + checkpoint diffs
  exactly; `zig build test` green; ABI version handshake works.
- Known Windows baseline (Zig 0.16.0, measured 2026-07): 18 upstream tests
  fail on Windows — all UDP/lockstep/rollback networking tests
  (`error.ConcurrencyUnavailable` from Zig std threaded IO on Windows) plus
  one asset-extract path-conversion test. All deterministic runtime, replay,
  and codec tests pass. "Green" for our gates means no regressions beyond
  this baseline.

### M2 — Diorama skeleton (programmer art)
- Godot project skeleton, P/Invoke bindings (`Sim.cs`), 60 Hz fixed-step
  accumulator, snapshot decode, colored-quad rendering via MultiMesh,
  interpolation between snapshots.
- Survival session boots; move/aim/fire/reload via §4 mapping; death →
  restart.
- ✅ *Verify*: playable on PCVR; a scripted-input harness (fixed synthetic
  `CrimsonHostInput` sequence) produces a `.crd` that passes
  `crimson-zig replay verify`; mapper math covered by C# unit tests
  (projection, clamping, game↔arena transforms, hand swap).

### M3 — Real presentation
- Asset bake pipeline; terrain + decal layer; creature/player/projectile/
  particle sprites with 2.5D tilt + shadows; positional audio + music;
  HUD panels; muzzle/explosion effects.
- ✅ *Verify*: side-by-side with the desktop Python build on the same seed
  looks equivalent (spot-check video); Quest 3 standalone holds 72 Hz with
  late-game survival entity counts (use `replay benchmark`-style stress
  seeds); draw calls within budget (< ~50 for world layers).

### M4 — Interaction polish
- Perk menu in VR, pause menu, arena placement/recenter/scale settings
  (§5 rules incl. play-area fit + override), hand swap, dead-zone tuning,
  haptics, comfort pass.
- Replay recording on by default, saved to the standard runtime replays dir.
- ✅ *Verify*: full survival run start→death→highscore entirely in-headset
  without touching desktop; recorded `.crd` verifies; settings persist.

### M5 — Shell + builds
- VR-native minimal menu (start survival, settings, quit), version/about.
- CI builds: Windows x64, Quest APK, Linux x64 (GitHub Actions; Zig cross-
  compile + Godot headless export). Builds remain private until M6 clears.
- The workflow must be **fork-runnable**: `workflow_dispatch` trigger, no
  repo secrets required, APK signed with an auto-generated keystore — this is
  the §10 worst-case distribution path, so it's a requirement, not a nicety.
- ✅ *Verify*: clean-machine install test on PCVR and Quest; CI produces all
  artifacts from one tag.

### M6 — Asset removal + licensing decoupling (public-release gate)
Nothing ships publicly until this milestone is done.

- **Remove all original-asset acquisition from distributed builds**: no
  bundled PAQs, no automatic download from the upstream project's channel
  (that permission is project-specific and does not extend to this fork).
- **First-run asset import wizard**: user points the app at their own
  Crimsonland Classic install (GOG). On Quest (SideQuest-distributed, so
  developer mode + PC connection are a given): user drops the PAQs into the
  headset's Download folder via SideQuest's file manager or adb; the app
  requests all-files access (fine for sideloaded apps), detects them, and
  imports. No companion desktop app. Extraction/atlas-bake runs locally (§6).
  Clear messaging when assets are absent.
- **Audit the repo and build outputs** for asset-derived content: committed
  fixtures, screenshots in docs, atlas manifests, test data. Anything derived
  from original art/audio is removed or regenerated-on-device.
- **Code licensing resolution** (see §10): confirm what we may publish.
  Expected outcome is publishing only `crimson-vr/`-original code plus an
  overlay/patch for `host_abi`, with build instructions that fetch banteg's
  repo — unless explicit permission for more is granted.
- *(Stretch / alternative)* **Original replacement asset set**: a
  CC-licensed minimal sprite/audio pack so the VR build is playable with zero
  external files. Large art effort; only worth starting if user-supplied
  import proves too hostile or permissions fall through entirely.
- ✅ *Verify*: a clean build from the public repo contains zero
  original-asset bytes (automated check comparing build outputs against known
  PAQ-derived hashes); fresh user flow works end-to-end with only a GOG
  install as input; legal checklist in §10 fully resolved or the release is
  scoped down accordingly.

### M7 — Post-v1 (unordered backlog)
- Rush/Quests/Typ-o modes (mostly shell work — sim already supports them).
- Steam Frame/arm64 Linux target when hardware exists.
- Optional: grabbable arena experiment, giant room-scale mode polish,
  spectator flat-screen mirror, 3D creature models (big art project — this
  would also double as replacement assets for M6's stretch goal).

---

## 9. Risks and mitigations

1. **Godot C# (.NET) Android export maturity.** Historically experimental;
   quality varies by Godot 4.x release. *Mitigation*: M0 validates it before
   any real investment. Fallback A: write the frontend in GDScript (Rider
   supports GDScript; the frontend is thin so the LLM-codegen penalty is
   modest; P/Invoke is replaced by a ~200-line GDExtension shim over the same
   C ABI). Fallback B: C# on PCVR + GDExtension/GDScript only for the Quest
   build (avoid — two frontends).
2. **Quest 3 fill-rate/draw calls with thousands of sprites.** *Mitigation*:
   MultiMesh instancing from day one (M2), atlas packing, no per-entity
   nodes; stress-test in M3 with worst-case seeds.
3. **Snapshot ABI churn.** LLM iterations will want to add fields.
   *Mitigation*: versioned header + append-only layout rule + the M1 replay
   gate; the C header in `crimson-vr/abi/` is the single reviewed contract.
4. **Vertical projection ergonomics.** Holding hands over a table for long
   sessions is tiring ("gorilla-arm, horizontal edition"). *Mitigation*:
   arena height/scale tuning, dead zone, trigger-held movement (already
   chosen) so arms rest between actions; if playtests still hurt, add an
   optional angled-projection mode (project along a fixed tilted axis) —
   settings-level change, no architecture impact.
5. **Upstream drift.** `crimson-zig/runtime` evolves upstream. *Mitigation*:
   `host_abi` is additive and thin over `live_runner`; the M1 replay gate
   catches breakage on every upstream merge.
6. **Licensing blocks public release.** Upstream code is unlicensed
   (source-available only; "MIT?" issue closed as not planned) and asset
   permission is upstream-project-specific. *Mitigation*: M6 release gate —
   distributed builds ship zero original assets and, absent a code grant, only
   our own code plus an overlay build against the user's own clone (§10).
   Worst case, the project remains fully buildable-from-source rather than
   binary-distributable; development is unaffected either way.

---

## 10. Licensing and distribution

Confirmed findings (2026-07):

- **Upstream code is source-available, not open source.** No LICENSE file, no
  license declaration in `pyproject.toml`, and an issue asking "MIT?" was
  **closed as "not planned"** — so an upstream OSS license should not be
  assumed forthcoming. Default copyright applies: GitHub's ToS permits
  viewing/forking public repos, but **not** reproducing, distributing, or
  creating derivative works beyond that. Redistributing any build or source
  containing banteg's code (including `crimson-zig/src/runtime/`, which
  `libcrimson` links) requires explicit written permission.
- **Assets are even more restricted.** The PAQs and source art are distributed
  "with permission from the original developer" *for the upstream project
  specifically* — not a public asset license, and not permission for
  downstream forks. Additionally, the upstream asset pack is a curated hybrid
  (selection, conversion, stitched sprite sheets), so banteg may hold separate
  rights in the pack itself on top of 10tons' rights in the underlying art.

### Permissions that full open-sourcing would require

1. **banteg / repo copyright holders**: license to reuse, modify, and
   redistribute the code (or an upstream OSS license).
2. **Crimsonland rightsholder (10tons / original developer)**: permission to
   copy, modify, redistribute, and package the assets, stating whether they
   may be sublicensed to downstream users or remain proprietary bundled by
   special permission.
3. **banteg again for the curated asset pack**, if any pack-derived files were
   ever distributed (the plan avoids this entirely).

### Distribution model (what we do regardless of permissions)

- **Ship no original assets, ever** (M6 is the release gate): distributed
  builds contain only our code and original content; users provide their own
  legally obtained Crimsonland Classic files, imported and baked locally on
  first run. No automatic download via the upstream project's channel.
- **Publishable-by-default surface = our own code only**: the Godot frontend
  (`crimson-vr/godot/`), tools, ABI header, and docs. The `host_abi` layer is
  our code but links against banteg's runtime, so prebuilt `libcrimson` (and
  any APK containing it) is not distributable without a grant. However,
  GitHub's ToS permits public forks, so the fork itself — banteg's code with
  `host_abi` and `crimson-vr/` in place — can stay public on GitHub as one
  clonable repo. Overlay/patch packaging is only needed if we ever distribute
  source outside GitHub.
- **Worst-case user build flow (no code grant)**: primary path is
  **fork-and-CI** — the user forks the repo on GitHub and runs the release
  workflow in their own fork; cloud CI produces their APK / desktop build
  (auto-generated signing keystore included) with zero local toolchain. The
  local build script (bootstraps Zig, Android SDK/NDK, JDK, Godot + export
  templates, .NET) is the fallback. Either way the resulting APK is
  asset-free; assets are imported on-device afterward (M6 flow), so even
  self-built APKs contain no 10tons content and one import path serves all
  scenarios.
- **Ask anyway**: approach banteg with the finished result for a code
  redistribution grant (which would let us ship prebuilt `libcrimson` /
  all-in-one builds), and 10tons for an asset arrangement. Treat both as
  upside, not as plan-of-record.
- **Quest channel is SideQuest** (Meta Store/App Lab review would likely
  reject a Crimsonland-derived title on IP grounds anyway). Note that hosting
  the APK on SideQuest is still binary redistribution of `libcrimson` and
  therefore still requires the code grant; without it, the SideQuest presence
  is a listing pointing at the build-it-yourself flow. SideQuest's real win is
  the asset-import UX (dev-mode users, built-in file manager — see M6).
- Our original code is licensed MIT from the first public commit.

---

## 11. LLM-assisted development guardrails

This codebase is unusually well-suited to LLM code generation *because it can
verify itself*. Rules to keep it that way:

- **Never let generated code touch `crimson-zig/src/runtime/` gameplay logic**
  for VR reasons. VR needs go in `host_abi/` or the frontend.
- Every PR-sized change must pass: `zig build test` (incl. the M1 ABI replay
  gate), C# unit tests for mapper/transform math, and `just check` for any
  Python-side tooling touched.
- Keep Godot scenes minimal and prefer C#-constructed nodes over deep `.tscn`
  hierarchies — code is what LLMs (and reviewers) handle best; scenes are for
  static structure only.
- Commit `.crd` fixtures for any behavior being relied upon; when a bug is
  fixed, add the reproducing fixture.
- The C ABI header + this document are the contracts to feed into prompts;
  keep both current.

---

## 12. Immediate next steps

1. M0 spike: install Godot 4 (latest stable, .NET edition) + Android build
   templates; validate Quest 3 C# export (go/no-go).
2. M1: scaffold `crimson-zig/src/host_abi/` + the replay-through-ABI test
   gate. Keep it structured as a clean overlay (self-contained directory,
   minimal touches to upstream build files) so the §10 patch-distribution
   model stays cheap.
3. Development proceeds privately using upstream's asset flow; the
   banteg/10tons permission conversations happen with a finished build in
   hand (per §10), with M6 as the hard gate before anything ships.
