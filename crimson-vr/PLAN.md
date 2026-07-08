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
    src/               C# code:
                         Sim.cs        P/Invoke bindings + packed struct layouts
                         SimSession.cs session driver + snapshot decode
                         Mapper.cs     pure world<->arena<->game transforms (§4)
                         VrInput.cs    pure reticle/button -> CrimsonHostInput (§4)
                         Diorama.cs    MultiMesh colored-quad renderer + interp
                         Main.cs       OpenXR rig, arena, 60 Hz sim loop, reticles
    scenes/            .tscn (text) scenes
    native/            libcrimson binaries per rid (generated, gitignored)
    assets/            imported atlases (generated, gitignored)
  abi/                 C header for libcrimson host ABI (source of truth)
  tests/               CrimsonVR.Tests/ — xUnit math tests (net9.0 + GodotSharp)
  tools/               build glue + gen_vr_verify_replay.py (M2 .crd gate);
                       asset bake scripts (PAQ -> atlas) come in M3/M6
crimson-zig/
  src/host_abi/        C-ABI export layer (peer of the WASM ABI)
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

// Replay verify passthrough — runs the native verifier on .crd bytes, writing
// its JSON report. Lets the host validate it links the exact verified stack.
int32_t  crimson_host_verify_replay_json(const uint8_t* replay, uint32_t replay_len,
                                         /*out*/ uint8_t* out, /*inout*/ uint32_t* out_len);

// Live replay RECORDING (standard .crd) — DEFERRED TO M4. The Zig runtime is
// read/verify-only; there is no .crd encoder in crimson-zig. Until then, the
// M2 verify gate produces .crd via the Python recorder (crimson.replay,
// crimson-vr/tools/gen_vr_verify_replay.py); in-headset recording (M4) will add
// these exports backed by a new msgpack encoder:
//   int32_t crimson_host_replay_begin(uint64_t session);
//   int32_t crimson_host_replay_finish(uint64_t session, uint8_t* buf, uint32_t* len);

// Perk menu interaction (mirrors FrameInput.perk_choice_index / perk_menu_active).
// Folded into CrimsonHostInput rather than separate calls.

// Error reporting.
int32_t  crimson_host_last_error(/*out*/ char* buf, uint32_t len);
```

The checked-in header `crimson-vr/abi/crimson_host.h` is the authoritative
signature list (exact struct layouts, buffer protocols); the sketch above is
the intent. As of M1/M2 it implements everything except the two deferred
`replay_*` recording calls.

`CrimsonHostInput` mirrors `GameInput` (`crimson-zig/src/runtime/player.zig`):

```c
typedef struct {
  float move_x, move_y;        // analog move DIRECTION vector (unit-length or 0);
                               // NOT a target point — see note below
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

> **Movement semantics (important, discovered in M2).** `move_x/move_y` is the
> analog move *direction*, consumed as-is by the runtime — it is **not** the
> point-click target. The desktop's point-click mode converts a world target
> into this vector (`dir = normalize(target − player)`, zeroed within
> `point_click_stop_radius`) inside `local_input.zig`, and the host ABI
> **bypasses** that conversion. So the **frontend** must produce the normalized
> direction (see `VrInput.Build`); the `move_to_cursor` flag is a passive intent
> marker and does not itself drive movement through the ABI. The header comment
> in `crimson_host.h` is the corrected source of truth.

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
  height `h` (playfield plane), yaw `θ` (user-facing rotation). `ArenaRoot`'s
  position is normally static (recenter is a one-shot user action), but in
  **player-centered mode** (§5) it is recomputed every rendered frame to track
  the player avatar's game-space position — reticle projection (below) is
  unaffected either way since it always operates on the current `ArenaRoot`
  transform.
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
| Move-hand reticle, trigger **held** | `move_mode = MOUSE_POINT_CLICK`; frontend emits `move_x/move_y` = `normalize(reticle − player)`, zeroed within the stop radius — the ABI consumes a **direction**, not a target point (see §3 note). `move_to_cursor` flag set as an intent marker |
| Move-hand trigger released | zero move vector (player stops) |
| Aim-hand reticle | `aim_x/aim_y` = reticle point, `aim_scheme = MOUSE` |
| Aim-hand trigger | `fire_down` / `fire_pressed` |
| Aim-hand **grip** squeeze | `reload_pressed` (grip chosen over A/X so A/X stays free for recenter) |
| Either hand, menu / AX button | recenter arena (M0/M2); pause is M4 |
| Perk menu open | reticle-over-card + aim-hand trigger selects → `perk_choice_index` |
| Settings toggle | swap hand roles (move ↔ aim/fire) |

Notes:

- Dead zone: if the move reticle is within a small radius of the player, treat
  as zero movement to prevent jitter when "standing on" your own marker. M2
  defaults this to the desktop point-click stop radius (`point_click_stop_radius
  = 20` game units) for parity; PLAN's earlier ~12 is a comfort-tuning target
  for M4.
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
    slider max **2.0 m**. **M2 in-headset finding (2026-07): a seated player's
    reach envelope — chest to fully outstretched — is well under a metre, so
    `L = 1.0 m` overshoots it and the arena's far edge is unreachable while
    seated.** M2 hardcodes `L = 0.4 m` as the seated default; the movement/aim
    mechanic (project the hand straight down onto the table) requires the whole
    table to sit inside reach, so the default must track the *player's* reach,
    not a fixed guess.
  - Override: an "I know what I'm doing" setting unlocks the slider up to
    **10 m** (room-scale walk-inside-the-arena mode). Persisted per user.
- **Seated arena-size calibration (M4 setup step, flagged in M2).** Because the
  seated reach envelope varies per player and per chair, the seated default `L`
  should be *measured*, not guessed: a short calibration where the user holds a
  controller at a comfortable near point and a fully-outstretched far point, and
  `L` (and arena distance/height) is derived to fit that reach with margin —
  the seated analogue of the play-area-fit rule above. Persisted per user like
  scale/height. Until then M2 ships the fixed 0.4 m default.
- Height `h`: recenter places the arena a comfortable **drop below the current
  head pose** (M2 default 0.5 m, floored at a 0.35 m minimum below the head) and
  snaps yaw to face the player. **Tracking head height on every recenter is what
  resets the vertical** — an earlier fixed world height (0.75 m) left a *standing*
  player with the table far below them (M2 in-headset finding, 2026-07). The drop
  (and a min-below-head clamp) become tunables in the M4 arena-customisation tool.
- Seated and standing both work by construction (arena is world-anchored;
  posture just changes viewing angle — the "isometric" look). Reach, however,
  differs — see the seated calibration note above.
- Grabbable/rotatable arena: **not in v1** (agreed: likely a
  wanted-but-regretted feature). The recenter action + yaw snap covers the
  legitimate need. Revisit after playtesting.

### Player-centered (follow) mode

- **Control feature, optional setting, off by default.** An alternative to the
  fixed world-anchored arena: `ArenaRoot` is smoothly re-centered every frame
  so the player's in-game avatar position stays near the middle of the
  physical play area, instead of the user having to walk to reach far corners
  of a large arena. Yaw continues to follow the last recenter/snap, not the
  avatar's facing — only position tracks.
- Motion is a critically-damped follow (never a snap/teleport of the arena)
  to keep the visual displacement low-acceleration and reduce vection-induced
  discomfort; exposed as a comfort setting alongside the existing vignette
  option (see M4).
- This is the mechanism that decouples arena size from physical room size: it
  lets a large `world_size` arena (or the >2 m "walk-inside" override) be
  played from a small physical footprint, or lets a seated player reach the
  whole arena without leaning. Because of the overlap in what problem they
  solve, treat player-centered mode and the >2 m walk-inside override as
  alternatives presented together in settings, not required to compose.
- Implementation stays entirely in the frontend (`ArenaRoot` transform each
  frame); no ABI or sim changes — the sim only ever sees game-space
  coordinates, which are unaffected by where the arena sits in the room.
- Persisted per user, same as arena scale/height (§5).

---

## 6. Presentation (2.5D)

> **Known issue — off-arena spawn margin (found in M3 in-headset, 2026-07-08).**
> Survival creatures spawn **40 game units outside** the terrain bounds
> (`rand_survival_spawn_pos`: edge = `-40` or `terrain+40`) and walk in. The flat
> game's camera crops that margin so it's never seen; the VR diorama shows the
> whole world plane, so clusters visibly pop in *outside* the table and run onto
> it. Needs an elegant arena-edge treatment (e.g. a fade/vignette band or a
> raised rim that masks the spawn ring, or gently over-sizing the terrain quad
> past the playfield so the margin reads as "off-table" rather than floating).
> Presentation-only (no sim change — spawn positions must stay exact for parity).
> Defer to a later M3 polish pass or M4.

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

   > **Getting Crimsonland Classic (GOG).** This project needs the *classic*
   > 1.9.93 assets (`crimson.paq`, `sfx.paq`, grim engine) — **not** the 2014 HD
   > remake that GOG sells as "Crimsonland" (that ships `data.pak`/Galaxy and is
   > incompatible). The classic is included as a **bonus** with the GOG purchase.
   > User steps: **buy Crimsonland on GOG → in GOG Galaxy open the game → Extras
   > tab → download & install "Crimsonland Classic".** It installs alongside the
   > HD remake (e.g. `…/Crimsonland Classic/`) with the `.paq` files the import
   > wizard reads. (Verified 2026-07: `crimson.paq` + `sfx.paq` present; music is
   > loose Ogg in a `music/` folder, no `music.paq`.) The import UI must spell
   > this out, because owning "Crimsonland" on GOG does **not** by itself give the
   > user the right files — see the M6 release note.
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

**Status (2026-07): scaffold + Android export GO; in-headset verify pending.**
- `crimson-vr/godot/` builds (`dotnet build`, 0 errors) and imports headlessly
  in Godot 4.7 .NET with no script errors. Main.cs bootstraps OpenXR, the
  tabletop arena, and the two vertical-projection reticles + guide lines;
  Mapper.cs holds the coordinate transforms.
- **Headless C# Android APK export succeeds**: signed 99 MB arm64-v8a APK
  containing the .NET/Mono runtime and our AOT-published `CrimsonVR.dll`. The
  §9 risk-1 (Godot C# Android maturity) is **retired** — no GDScript fallback
  needed.
- **Toolchain fact (supersedes earlier setup note): Godot 4.7's Android export
  template requires `net9.0`, not `net8.0`.** A net8.0 project fails export with
  "export template only supports net9.0". `CrimsonVR.csproj` targets net9.0.
  (The desktop-only native smoke harness still uses net8; only the Godot
  project's TFM is constrained by the template.)
- Native `crimson_host.dll` (win-x64) round-trips through C# P/Invoke (session
  create + 600 survival ticks + snapshot decode). Note: session init and the
  replay verifier run on a dedicated 64 MiB thread because the sim builds
  multi-MB structs on the stack and host threads (.NET default ~1 MiB) overflow.
- **Remaining before in-headset run (needs the user + hardware):**
  1. ~~cross-compile `crimson_host` for `aarch64-linux-android`~~ **DONE
     (2026-07):** `zig build host-lib -Dtarget=aarch64-linux-android` now
     produces a valid aarch64 ELF `.so` with all 9 exports, staged into
     `godot/native/android-arm64/`. Fixes applied: `build.zig` now defines the
     raylib-free host lib first and early-returns for Android before the
     raylib/desktop/test steps (raylib's build.zig hard-panics on Android ABI);
     desktop `zig build`/`test` unaffected (18-failure baseline unchanged). Zig
     0.16 ships bionic stubs, so **no NDK was needed** to build (an NDK is
     installed only for on-device `readelf`/robustness work). Two on-device /
     integration items remain, both genuinely needing the Quest or Godot export
     wiring (not blockers to the PCVR spike):
       - the `.so` has one undefined symbol, `getauxval` (bionic), and **no
         `DT_NEEDED libc.so`** because Zig makes direct syscalls. It resolves
         from the process global scope inside the Godot APK (bionic already
         loaded), so it should load — but confirm on-device, and if it fails,
         link bionic explicitly via the installed NDK sysroot to get a proper
         `DT_NEEDED libc.so`.
       - wire the `.so` into the APK's `lib/arm64-v8a/` during Godot export
         (Godot's Android export doesn't pick up arbitrary P/Invoke natives
         automatically) — an M2 export-integration task.
  2. ~~launch PCVR spike via Virtual Desktop and confirm head/hand tracking~~
     **DONE (2026-07): PCVR spike verified in-headset.** OpenXR came up on
     VirtualDesktopXR 1.0.10 (Vulkan/RTX 4090); arena, status label ("sim abi
     v1" — C#→native DLL call confirmed), controller tracking, vertical
     projection, edge clamping, and trigger-brighten all correct. One bug found
     and fixed: off-arena dim used an alpha change on an opaque material (no-op);
     now darkens RGB.
  3. **PARTIAL (2026-07): Quest APK installs, launches, and runs the full
     managed+native stack on-device — but falls back to flat (no VR).**
     Verified via adb/logcat on Quest 3: APK installs, launches as an immersive
     HorizonOS app, the .NET runtime + our `CrimsonVR.dll` execute
     (`Main._Ready()`→`InitializeXr()` in the C# backtrace), and the arm64
     `libcrimson_host.so` loads with no dlopen/`getauxval` failure (that worry
     is retired). The native `.so` is bundled by post-processing the exported
     APK: `crimson-vr/tools/inject_native_and_sign.ps1` injects it into
     `lib/arm64-v8a/`, re-aligns (zipalign), and re-signs (apksigner). Manifest
     has `extractNativeLibs=true`, so compressed injection is fine.
     ~~Blocker for actual VR~~ **RESOLVED (2026-07): full VR on Quest 3 verified
     in-headset — runs identically to the PCVR build.** Recipe that worked
     (see also `Untitled VR Game/BUILD_FOR_QUEST.md` for the general version):
       - Installed **Godot OpenXR Vendors plugin 5.1.0** into
         `godot/addons/godotopenxrvendors/` (gitignored; restore via
         `tools/fetch_vendors_plugin.ps1`).
       - **Install Android Build Template from the editor** (Project menu) — a
         manually-staged template is NOT recognized; the editor's installer
         registers pieces the export checks for. (`.gdignore` must be at
         `android/build/.gdignore`, not `android/.gdignore`, or Godot ignores the
         whole template.)
       - Export preset: `gradle_build/use_gradle_build=true`,
         `xr_features/enable_meta_plugin=true`, `xr_features/xr_mode=1`,
         arm64-v8a only. This bundles `libopenxr_loader.so` + all VR manifest
         entries (headtracking feature, runtime-broker `<queries>`, IMMERSIVE_HMD
         + oculus VR launch categories).
       - Ran the gradle export **headless from the container** (which has the
         SDK/JDK/NDK the host lacks); **close the editor first** — editor and
         export can't both hold the plugin's `libgodotopenxrvendors.dll`.
       - Bundled `libcrimson_host.so` via `tools/inject_native_and_sign.ps1`,
         now using **`jar uf0`** (stored) + **`zipalign -P 16`** (Quest is a
         16 KB-page OS) + **v2/v3 signing**. (.NET `ZipArchive` injection made
         the installer reject the APK: `Failed to extract native libraries
         res=-2`; gradle sets `extractNativeLibs=false`.)
       - Install/launch over network adb; the Quest gates adb launches with a
         "controllers required" dialog — **launch from the in-headset app
         library** (Unknown Sources).
     **Known open issue:** the arena spawns at the OpenXR *stage* origin (play-
     space center), not in front of a seated player, and OS recenter doesn't fix
     it. Fix added (untested on Quest): `Main.cs` now places the arena in front
     of the head on the first valid frame and on a menu/AX-button recenter
     (PLAN §5). Verify next session; may also want to switch to `local` reference
     space or expose a scale/height/recenter settings panel.

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

**Status (2026-07): code-complete; 2 of 3 verify criteria met, in-headset
playtest pending.** New frontend code in `crimson-vr/godot/src/`:
- `SimSession.cs` — managed session driver over the C ABI: create/tick/restart/
  dispose, zero-copy `SnapshotView` decode, ping-pong buffers so the last two
  snapshots stay live for interpolation.
- `VrInput.cs` — pure §4 mapping (hand-role resolve, dead zone, fire/reload
  bits); emits `move_x/move_y` as a **normalized direction** (see §3 note).
- `Diorama.cs` — one `MultiMeshInstance3D` per entity layer (players, creatures,
  projectiles, secondaries, bonuses) as flat colored quads on the arena plane;
  index-matched interpolation between the last two snapshots.
- `Main.cs` rewired: fixed 60 Hz `_PhysicsProcess` sim loop (poses sampled at
  tick time), interpolated draw in `_Process` via
  `GetPhysicsInterpolationFraction`, death→restart; placeholder capsule removed.
- Verify status: **C# unit tests ✅** — 18 tests in
  `crimson-vr/tests/CrimsonVR.Tests/` (net9.0, GodotSharp from nuget), covering
  projection/clamping/round-trips/hand-swap/input construction. **Scripted
  `.crd` gate ✅** — `crimson-vr/tools/gen_vr_verify_replay.py` records a
  VR-schema survival sequence and `crimson-zig replay verify` re-simulates with
  `match=True`, exit 0 (3000 ticks, 12 kills). **Playable on PCVR ⏳** — needs
  headset; the win-x64 `crimson_host.dll` is staged and the scene boots + ticks
  + decodes headlessly.
- Toolchain notes: build the frontend assembly with `dotnet build
  crimson-vr/godot/CrimsonVR.csproj`; run the scene headless for a smoke test
  with the Godot console exe **without** `--build-solutions` (that flag opens the
  editor build pass and `--quit-after` exits before the scene runs). The `.crd`
  harness runs via the Roaming Python 3.13 `uv`
  (`%APPDATA%\Python\Python313\Scripts\uv.exe run ...`).
- Key finding (see §3 movement note): the host ABI takes a move **direction**,
  not a target point — the frontend does the point→direction conversion the
  desktop's `local_input.zig` normally does. Caught by a headless smoke test
  (player drifted off-center with no input) and fixed.

### M3 — Real presentation
- Asset bake pipeline; terrain + decal layer; creature/player/projectile/
  particle sprites with 2.5D tilt + shadows; positional audio + music;
  HUD panels; muzzle/explosion effects.
- **Id-based snapshot matching.** M2's renderer interpolates by dense index and
  skips interpolation on spawn/death frames (dense order shifts). Real sprites
  need persistent per-entity state (animation phase, hit flash), so the snapshot
  should carry a stable entity id (e.g. pool slot index) and the renderer should
  match by id — which also removes the last interpolation seam. Append-only ABI
  field; re-run the M1 replay gate.
- ✅ *Verify*: side-by-side with the desktop Python build on the same seed
  looks equivalent (spot-check video); Quest 3 standalone holds 72 Hz with
  late-game survival entity counts (use `replay benchmark`-style stress
  seeds); draw calls within budget (< ~50 for world layers).

**Status (2026-07-08): slice 1 done — real static sprites, correct facing,**
**native-matched z-stacking; in-headset validated (PCVR).** Assets come from the
user's Crimsonland Classic install (see §6 GOG note), extracted with `crimson
extract`. `crimson-vr/tools/bake_assets.py` stages the 8x8 sheets into
`godot/assets/sprites/` (gitignored) + a JSON manifest (per entity type: sheet,
frame, pivot, art-facing `offset_deg`, draw `priority`). `Diorama.cs` loads the
manifest and draws:
- One `MultiMeshInstance3D` per creature type (each bound to its sheet, static
  frame via UV offset); player from `trooper.png` frame 16 (torso; `bodyset` is
  corpse decals). Colored-quad fallback when assets are absent.
- **Facing:** sim heading θ → direction `(sin θ, −cos θ)`
  (`math_parity.heading_to_direction_f32`); the sprite basis is built directly
  from that forward (no `RotY` handedness flip), plus a per-sheet `offset_deg`
  for the art's baked facing (creatures −90°, player 0). Validated against a
  toggleable debug facing-needle (`DebugFacing`).
- **Z-stacking = the original's model.** No per-instance depth sort; a fixed
  painter's order by layer/type via material `RenderPriority` (alpha-blended,
  depth-write off, so draw order alone decides — no z-fighting, no physical
  height hack). Creature type order matches native
  `_NATIVE_CREATURE_SPRITE_DRAW_ORDER` (zombie<spider1<spider2<alien<lizard),
  then player < projectiles/effects < bonuses/UI. Within a type it's stable
  index order (as the native pool order is). See
  `src/crimson/render/world/draw.py`.
- Projectiles/secondaries/bonuses are still colored quads.

**Slice 2 done (2026-07-08): creature animation.** Creatures now animate:
`CreatureAnim.SelectFrame` (a faithful port of `creature_anim_select_frame`,
`src/crimson/creatures/anim.py`) picks the 8x8 frame per instance from the
snapshot's `anim_phase` + runtime `flags` (long-strip vs ping-pong, mirror fold,
ranged-shock +0x20 offset). Per-type `base_frame`/`mirror` come from the manifest
(baked from `CREATURE_ANIM`); flag bits (PING_PONG 0x04, SHOCK 0x10, LONG_STRIP
0x40) arrive verbatim in `CreatureSnap.flags`. Each creature MultiMesh now uses a
small unshaded sprite **shader** with per-instance **custom data** = (uvOffX,
uvOffY, uvScale, 0) selecting the cell — replacing the StandardMaterial static-UV
path. Frame uses the current tick's phase (no interp across the phase wrap).
28 new golden-value C# tests (`CreatureAnimTests`, vs the Python reference) — 46
total green; `dotnet build` clean; headless boot exercises the animated
custom-data path with no exceptions. Player stays a static torso frame (leg anim
needs a `move_phase` ABI field — deferred). GPU shader compile still pending
in-headset/PCVR confirmation (headless uses the dummy renderer).

**Slice 3 done (2026-07-08): positional SFX.** The host-ABI audio events
(`crimson_host_audio_events`, drained each tick via `SimSession.CaptureAudio` →
`AudioEventsView`) now play through `AudioBank` — a pool of spatialized
`AudioStreamPlayer3D` anchored under `ArenaRoot` so sound comes from the
tabletop. Routing mirrors `audio_router.py`: shot events map weapon_id→fire
sound (or Fire-Bullets + Plasma-Minigun when active), reload events
weapon_id→reload sound, hit events play shock/bullet-hit (ABI supplies the
resolved roll), loose sfx events are native sfx ids. Mappings come from a baked
`assets/audio/audio_manifest.json` (`bake_assets.py` now also stages the 70
distinct Oggs + emits the manifest); the sfx-id list is index-aligned to the Zig
`SfxId` enum (`@intFromEnum`), which matches Python `SFX_NATIVE_ORDER`. No
per-event position exists in the ABI (the original SFX aren't positional), so
shots/reload emit at the player and everything else at arena centre — subtle
across 0.4 m, but sound comes from the table not the head. `dotnet build` clean;
oggs import cleanly; headless boot routes audio with no exceptions. **Sound
output pending in-headset/PCVR confirmation.** Music (game-tune trigger) is
deferred — the hit-event game-tune path is recognized but plays nothing yet.

**Slice 4 done (2026-07-08): in-world HUD.** `Hud.cs` — a panel anchored under
`ArenaRoot` at the near edge (a Label3D + two unshaded quad bars), updated each
tick from `TickResult` + `PlayerSnap`: health (bar vs an assumed-100 max + exact
numeric), ammo/clip (bar) that switches to a reload-progress bar while reloading,
and a `HP / LV / FOES` line. No sim/ABI change. `dotnet build` clean; headless
boot updates the HUD with no exceptions. Placement/tilt are first-pass, to be
tuned in-headset.

**Slice 6a done (2026-07-08): projectile glow + muzzle/explosion fx.**
Projectiles and secondaries now render as additive glow **streaks** (a soft blob
elongated along travel via `StreakBasis`, oriented by the creature-heading
convention), **tinted per weapon type** by `known_proj_rgb`
(`ProjTints`: ion=blue, fire=orange, shrink=green, blade=magenta, else tan) via
MultiMesh instance colors. Two transient fx, captured at tick time and drawn into
a shared additive mesh: **muzzle flash** at the player's gun along aim (from
`PlayerSnap.MuzzleFlashAlpha`) and **explosions** at detonating secondaries (from
`SecondarySnap.detonation_t/scale`). No ABI change (all from existing snapshot
fields). Build clean; headless boot runs the streak/fx path with no exceptions.
Streak orientation + fx sizes are first-pass — tune in-headset (flip
`StreakBasis` forward if streaks read sideways).

The faithful per-weapon projectile rendering (the `render/projectile_draw/`
registry: beam/plasma tail-head-aura segments, bullet-trail textures) is a bigger
job left for later; the glow-streak captures the essential look meanwhile.

**Slice 6b done (2026-07-08): sprite-effect particles via the ABI (v2).** The
`EffectPool` (blood, gibs, explosions, casings, glows — `effects.zig`) is now in
the snapshot: **ABI bumped to v2**, `SnapshotHeader.particle_count` +
`crimson_host_particle_snap[]` appended after bonuses (pos, half_w/h, scale,
rotation, rgba, age, effect_id, flags), packed for live entries
(`flags != 0 && age >= 0`, `draw_effect_pool`'s gate). C# decodes it
(`Sim.ParticleSnap`, `SnapshotView.Particles`); `Diorama.RenderParticles` draws
them from **particles.png** with a per-instance UV+color shader, flat on the
plane, sized `half*2*scale`, rotated, alpha-blended, RenderPriority 23 (effects
draw over the world in the native order). The bake precomputes each `effect_id`'s
UV cell (`effect_atlas_table` from `EFFECT_ID_ATLAS_TABLE`, variable
size_code grid, 2px inset). **M1 gate re-run green** (443/461, the 18 baseline
failures only; append-only field doesn't touch determinism); both native libs
rebuilt (win-x64 + arm64). C# build + headless boot clean (v2 snapshot decodes,
no magic/offset error). Sim particles (flamethrower `ParticlePool`,
`SpriteEffectPool`) not yet exposed — a later add. On-device visual confirmation
pending.

**Slice 5 done (2026-07-08): 2.5D tilt + drop shadows.** Creature/player sprites
get a fixed back-tilt (`SpriteTilt`, default 22°, tunable) applied about the arena
X axis so the lean is the same for every sprite regardless of heading and tips
toward the player's near edge (-z); the sprite centre is raised by
`½·size·sin(tilt)` so the tilted base stays near the plane. A shared drop-shadow
MultiMesh (`BuildShadows`) draws a soft radial-gradient blob (generated in code,
no asset) flat under each creature/player, at the lowest RenderPriority so every
sprite sits on top. Build clean; headless boot runs the shadow/tilt path with no
exceptions. Tilt angle + shadow size/opacity are first-pass — tune in-headset.

**Remaining M3 slices:** (a) **combined atlas + single-mesh** (per-instance UV is
now done per-type; a combined atlas would add true per-instance depth sorting —
low priority); slice 6b effect/particle pools (needs ABI, above); faithful
per-weapon projectile rendering (registry port); (d) terrain + decals (needs an
ABI terrain seed/tile field); (h) id-based snapshot matching (above); music
(loose Ogg, game-tune trigger); the off-arena spawn-margin edge treatment (§6
known issue); plus player leg animation (needs a `move_phase` ABI field).

### M4 — Interaction polish
- Perk menu in VR, pause menu, arena placement/recenter/scale settings
  (§5 rules incl. play-area fit + override, and the **seated reach-envelope
  calibration** flagged in §5), player-centered follow mode toggle (§5), hand
  swap, dead-zone tuning, haptics, comfort pass.
- Replay recording on by default, saved to the standard runtime replays dir.
- ✅ *Verify*: full survival run start→death→highscore entirely in-headset
  without touching desktop; recorded `.crd` verifies; settings persist;
  player-centered follow mode tracks the avatar smoothly with no
  reported-comfort regressions in the comfort pass.

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
  - **The wizard MUST tell the user how to obtain the right files**, because the
    default GOG "Crimsonland" is the incompatible HD remake (§6): *buy
    Crimsonland on GOG → GOG Galaxy → the game → **Extras** tab → download &
    install "Crimsonland Classic" → point the wizard at that folder's `.paq`
    files.* Detect and reject an HD-remake folder (`data.pak`/Galaxy) with a
    message pointing at the Extras step, rather than a generic "no assets" error.
    This GOG-version confusion is the single most likely first-run failure, so
    the copy for it is a release requirement, not a nicety.
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

### M7 — Move-hand abilities (aim↔move remap)
- **Optional setting, off by default.** Preserve the locked hand mapping
  (left = movement, right = aim/fire) as the default experience; when the
  setting is enabled, one or more abilities normally triggered from the
  aim hand are instead triggered from the move hand, targeted at the
  move-hand reticle rather than the aim-hand reticle.
- Crimsonland's classic perk set is **entirely passive** — confirmed no
  active-use, player-triggered abilities exist today in
  `crimson-zig/src/runtime/perks.zig`. This milestone therefore starts with
  designing and implementing at least one new active-use ability at the sim
  level (a short-range **teleport/blink** is the working example: instant
  relocation to a targeted point, on a cooldown). This is genuine new
  gameplay, not a VR-motivated hack, so it is not exempted by the §11
  guardrail against touching runtime gameplay logic for VR reasons — it goes
  through the normal `crimson-zig` design/test process like any other
  runtime feature, VR is simply its first (and possibly only) consumer.
  If additional abilities are added later they get the same treatment.
- New ability input needs a dedicated flag (e.g. `ability_pressed`) added to
  `GameInput`/`CrimsonHostInput` alongside the existing fire/reload bits, plus
  an ability-target point (reuses the move-hand reticle's projected point when
  the setting is on).
- Binding stays additive on the move hand: whatever button/trigger drives
  movement continues to drive movement unchanged; the ability fires from a
  separate control (grip, or a distinct button) so movement is never lost
  when the setting is on.
- ✅ *Verify*: new ability covered by Zig unit tests plus at least one
  committed `.crd` fixture exercising it; M1's replay-through-ABI gate passes
  with the new input bit; toggling the setting off reproduces the original
  aim-only control scheme exactly (regression-tested against existing `.crd`
  fixtures); toggling it on lets a scripted-input harness trigger the ability
  from the move-hand reticle and produces a verifying replay.

### M8 — Post-v1 (unordered backlog)
- Rush/Quests/Typ-o modes (mostly shell work — sim already supports them).
- Steam Frame/arm64 Linux target when hardware exists.
- Optional: grabbable arena experiment, giant room-scale mode polish,
  spectator flat-screen mirror, 3D creature models (big art project — this
  would also double as replacement assets for M6's stretch goal).
- Additional move-hand abilities beyond the M7 teleport example, if
  playtesting shows demand.

---

## 9. Risks and mitigations

1. **Godot C# (.NET) Android export maturity.** ~~Historically experimental~~
   **RESOLVED at M0 (2026-07):** Godot 4.7 .NET exports a signed, self-contained
   C# arm64 Quest APK (`.NET/Mono runtime + AOT CrimsonVR.dll` bundled). The
   one gotcha — the 4.7 Android template requires `net9.0` — is handled by
   pinning the Godot project to net9.0. The GDScript fallbacks below are no
   longer needed but retained for the record: (A) GDScript frontend + ~200-line
   GDExtension shim over the same C ABI; (B) C# PCVR + GDScript Quest (avoid).
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
7. **Player-centered follow mode causes motion sickness.** Moving the arena
   under a stationary user is a vection source even when critically damped.
   *Mitigation*: off by default, tuned in the M4 comfort pass alongside the
   vignette option, and positioned in settings as an alternative to (not a
   requirement alongside) the >2 m walk-inside override.
8. **M7's new sim ability is scope creep against "no gameplay
   reimplementation."** Adding an active-use ability is new game design, not
   a port of existing behavior, and could balloon in scope (balance, VFX,
   perk-menu interactions). *Mitigation*: keep the M7 ability minimal (one
   ability, teleport, clearly specified) and gate it behind the same
   replay-verification process as any other runtime change (§11); treat
   further abilities as M8 backlog, not M7 scope.

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
  gate); the C# mapper/transform tests (`dotnet test
  crimson-vr/tests/CrimsonVR.Tests`); and `just check` for any Python-side
  tooling touched. When frontend input/mapping changes, regenerate the M2 gate
  (`uv run crimson-vr/tools/gen_vr_verify_replay.py <out.crd>` then
  `crimson-zig replay verify <out.crd>`).
- Keep Godot scenes minimal and prefer C#-constructed nodes over deep `.tscn`
  hierarchies — code is what LLMs (and reviewers) handle best; scenes are for
  static structure only.
- Commit `.crd` fixtures for any behavior being relied upon; when a bug is
  fixed, add the reproducing fixture.
- The C ABI header + this document are the contracts to feed into prompts;
  keep both current.

---

## 12. Immediate next steps

M0, M1, and M2 (code) are done — see each milestone's Status block. ~~M0 spike~~
~~/ Android export~~, ~~M1 host ABI + replay gate~~, and ~~M2 diorama skeleton +~~
~~C# tests + `.crd` verify gate~~ are complete. Remaining, in order:

1. **Finish M2**: in-headset PCVR playtest — build the desktop frontend, confirm
   the diorama boots/ticks/renders and move/aim/fire/reload feel right (the
   win-x64 `crimson_host.dll` is staged; the scene already runs headless).
2. **M3 — Real presentation**: asset bake pipeline, terrain + decal layer,
   2.5D sprites with tilt + shadows, positional audio, HUD; Quest 72 Hz stress
   pass. This retires the last big rendering risk (§9.2).
3. Development proceeds privately using upstream's asset flow; the
   banteg/10tons permission conversations happen with a finished build in
   hand (per §10), with M6 as the hard gate before anything ships.
