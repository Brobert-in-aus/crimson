# CrimsonVR port status — implemented vs. remaining

A map of what the VR frontend does today, what the embedded sim already provides,
and what's still missing to reach faithful parity with the base game. The
**sim** (`crimson-zig`, embedded as `crimson_host`) runs the full game; the VR
work is almost entirely the **presentation + interaction** layer, so most gaps
below are "not surfaced in VR yet", not "not simulated".

_Last updated: 2026-08-22._

## Release readiness

The 2026-08-13 release audit is **NO-GO**. The current candidate builds, but the
reordered rollback recovery smoke fails and native Windows networking tests are
nondeterministic. Broader headset/relay validation, clean-machine workflow
rehearsals, derived-content review, and code/binary distribution decisions also
remain open. The canonical blocker register, commands, and evidence template are
in [Release Preparation](../../docs/contributor/project-tracking/release-preparation.md).

Feature status below describes implementation coverage; it must not be read as
release approval.

## Feature-review reconciliation

- **Tutorial is implemented.** Its reachable prompt/actions use a sidecar panel
  outside the playfield sightline in both Cabinet and Tabletop, aimed toward the
  recentered seat with the same stable upright-yaw rule as Cabinet power-up
  information. The native runtime already owned the full event-driven nine-stage
  lesson. ABI v24 exposes prompt/hint indices and fades; the VR panel translates
  them to controller/hand language and provides Skip, Repeat and Play-a-game
  exits. Practice-round copy explicitly requires collecting each dropped
  power-up as well as clearing its wave, matching the director's advance gate.
  On a profile that has not completed it, Play Game moves **Tutorial - Start
  Here** to the first row without blocking the other modes. Completion persists
  and restores the normal ordering.
- **Typo'Shooter remains deliberately unsurfaced.** A poke keyboard is not a
  viable real-time combat input. It needs a separate voice or physical-keyboard
  design; this is a product/input decision, not missing simulation work.
- **Multiplayer remains post-v1, with direct LAN implemented through slice 4.**
  ABI v27 and the VR frontend support 2-4 player Survival/Rush hosting and
  joining, negotiated-slot controls/HUD/results, and per-player FX. Hardware
  validation remains. Canonical perk commands/replays plus relay room-code UI,
  DNS and resume reconnect are implemented; an operated relay and physical
  recovery validation remain. Score
  records use the negotiated player count and the browser's 1-4 filter.
- **Statistics is complete for local/offline use:** lifetime mode stats,
  Weapons/Perks databases, a top-100 score browser and Credits. The browser has
  mode/date/player/name filters, paging and quest navigation. The original
  internet-score option is omitted because its service is defunct.
- **Credits and AlienZooKeeper are implemented.** The direct Secret route is an
  accessible VR adaptation of the original obscure text-click puzzle. The
  minigame retains its 6x6 board, any-two swap, horizontal-first match scan,
  9.6s timer, +2s per match, score, Reset and Back.

## Implemented in VR

**Flow / screens**
- First launch opens a short **direct-touch guide** with Continue and Adjust
  Reach. Its bounded copy and explicit UI render priority keep the text inside
  and in front of the ClassicPanel backing; returning players boot straight into
  the **main menu** (custom, using the original `ui_signCrimson`
  logo + `ui_menuItem` neon-bar plates): Play Game, Options, Statistics,
  Quit. **[audit]** Fidelity caveats: the base menu items slide-in/rotate on a
  staggered timeline with hover-fade alpha ramps and an additive "ready" glow
  (`menu.py:405-410,467-480,510-523`); VR plates are static poke targets. The
  base game's pulsing additive menu **cursor** (`ui/cursor.py:41-92`) has no VR
  analog by design (poke interaction) — `ui_cursor` is repurposed as the
  move-hand reticle.
- Offline run-creation failure is recoverable in-headset through a dedicated
  Retry/Main Menu panel. The asset-free startup screen presents numbered setup
  instructions, visible import progress, and duplicate-poke protection.
- **Survival, Rush, Quests and Tutorial gameplay** rendered as the diorama.
- **Pause** menu (flat toggle + Resume / Settings / Exit to Main Menu). Leaving
  a run requires a consequence confirmation; the main menu's Quit exits the app. **[audit]** The base pause menu
  is a reskin of the animated main-menu system (plates + sign + slide-in +
  world-fade background, `pause_menu.py:45-437`); VR's flat panel is a deliberate
  interaction substitute, not a fidelity match. Base layout is Options/Quit/Back
  (ESC resumes); VR is Resume/Settings/Quit.
- **Options** screen mirroring the applicable base-game controls (segmented
  `ui_rectOn/Off` sliders: Sound / Music / Graphics detail) + a **VR
  Settings** submenu (movement hand, named movement dead-zone levels, debug
  overlays, **named resolution levels backed by 0.6-1.6× render scale and named
  edge smoothing backed by MSAA Off/2×/4×**;
  `SettingsMenu.cs:99-118`, `Main.cs:405-417`).
  The inert "UI Info texts" checkbox was removed from the player-facing menu on
  2026-08-11; the persisted field remains only for config compatibility until
  bonus hover labels provide a real consumer.
  **[audit]** "Graphics detail" only culls VR-side nodes
  (`Diorama.SetGraphicsDetail`, `Diorama.cs:156-174`); it does not send
  `detail_preset` to the sim (ABI supports it, `crimson_host.h:339`), so sim-side
  particle-spawn thinning never varies. See also the `fx_detail` semantics row in
  the draw-pass table.
- **Level-up / perk pick**: level-up button + accumulated counter + `ui_levelUp`
  sound; a card poke selects it and pins its description, then the separately
  positioned Confirm Perk Selection button commits. Confirm is absent until a
  card in the current offer is selected and hides immediately when pressed.
  **[audit]** This *replaces* (not omits) the base game's perk-prompt banner — a
  `ui_menuItem` bar that hinge-swings in from the top-right carrying
  `ui_textLevelUp` with an additive pulse (`perk_prompt_ui.py:91-131`) — and the
  base `ui_menuPanel` perk list (sponsor line + tighter rows under Perk
  Expert/Master, neon hover items). Intentional VR substitution; noted so the
  banner isn't counted twice as "unused art".
- **Game-over / results screen** (2026-07-09, ABI v10): death → ~1.2 s pacing
  delay (stand-in for the base death VO + death-timer) → the results panel
  appears immediately as ONE composite death screen (base two-phase panel):
  when the score ranks (the base top-100 gate) the panel
  opens raised/pushed back with the virtual keyboard in front ("State your
  name, trooper!", buttons hidden), then drops to the buttons phase on Enter;
  unranked deaths open straight in the buttons phase — with the `ui_textReaper` banner,
  score, rank ordinal, game time as mm:ss + the animated
  `ui_clockTable`/`ui_clockPointer` gauge (6°/s), most-used weapon icon
  (`ui_wicons`) + display name (weapons table baked into the sprite manifest),
  frags, hit %, and Play Again / Main Menu buttons; `UI_PANELCLICK` cue on
  open. Fidelity caveats: no ease-out slide-in / world alpha-fade, no
  no hover tooltips; keyboard replaces inline text entry (by design). The full
  local browser is reached through Statistics.
  Kill count + most-used weapon crossed the ABI in **v10**
  (`creature_kill_count`, `most_used_weapon_id` in the tick result).
- **Arena recenter** — hold left `menu_button` / right `ax_button` for 700 ms to
  reposition + re-yaw the tabletop, with head-relative progress/completion
  feedback directly in front of the player rather than attached to the menu,
  plus initial auto-place. The mapping is included in first-run and Controls.
- **Validation checklist** (dev tool) is a paged poke panel whose pass/fail state
  persists. Passed rows are retired between test batches; current untested and
  changed behavior receives fresh persistence keys.
- **Controller and optical-hand input**: menus use fingertip poke rather than a
  laser; left hand moves, right hand aims/fires with pinch, and the weapon-swap
  perk has a reload gesture. VR Display separately persists the controller
  presentation (poke orbs or models) and optical-hand skin: Glove Caucasian
  Green Camo (Hand Model 1, the clean-profile default) or Glove African Dark
  Camo (Hand Model 2). Tac Gloves are suppressed while controllers are in use
  and appear automatically when unobstructed optical tracking becomes active;
  Godot's
  full hand-skeleton modifier applies every reported joint position and rotation.
  Controller presentation follows Godot's official render-model demo on runtimes
  supporting `XR_EXT_interaction_render_model`. Quest instead supplies its accurate
  Touch model through `XR_FB_render_model`; Crimson loads that runtime glTF and
  drives its trigger, grip, thumbstick, and button skeleton bones from the same
  live OpenXR actions as gameplay. A separate-part Touch-style model remains the
  fallback when neither runtime path supplies a model. The Tac Glove skeleton is
  renamed from its Blender convention to the
  exact Godot humanoid bone names required by XRHandModifier3D before binding.
- **Arena & Layout editor**: Cabinet placement exposes arena scale/tilt/
  distance/drop; Tabletop enforces flat/reachable geometry and uses one cycling
  Small/Medium/Large button (0.75x/1.0x/1.25x). Both expose sprite height,
  floor-up arena height (0 m floor, 1 m above, default 0.5 m), aim-line length,
  and grab-editable Pause,
  Level-Up and hand-control rectangle. Edit mode previews moving real creature
  sprites, the maximum seven perk cards and an x3 level-up badge. Layout dumps
  are durable and ADB-readable; the 2026-08-09 headset layout is baked into clean
  profile defaults with exactly mirrored action buttons. "Reset buttons & pad"
  now requires confirmation and offers an in-place undo for the current mode.
  Preview sprites render below the perk-description backing and text rather than
  puncturing its reading surface. A button in the normal Confirm location toggles
  the full perk preview so the arena remains visible while editing; snapshot
  refresh no longer re-shows it on the following tick. Cabinet's
  built-in Pause/Level-Up positions now match Tabletop's, and two-hand controller
  pitch maps directly rather than rotating the held button backwards.

**Presentation (diorama)**
- Creatures (animated sheets, per-type tint, energizer/freeze/hit-flash, death →
  ground + corpse stamp incl. drop-to-ground death staging), projectiles +
  secondaries (glow streaks — see per-type variants gap), bonuses/powerup icons,
  blood/scorch decals, muzzle flash (see approximation note), explosions,
  drop-shadows, terrain floor + surrounding world floor, grey-fog skybox.
- Projectile presentation is not cropped to the arena or diorama. Projectiles
  preserve their simulated travel, continue into the surrounding world, and
  naturally disappear at their runtime lifetime/travel limit; fade-stage
  visuals remain visible.
- Per-type projectile glow tints (ION blue / FIRE_BULLETS orange / SHRINKIFIER
  green / BLADE_GUN magenta, `Diorama.cs:301-313`). **[audit]** (undocumented).
- **MR passthrough** is a runtime VR Settings option shown only when the OpenXR
  device reports support. It keeps the diorama floor, removes the large world
  floor and fog, and uses MR-safe premultiplied/composited transparency for
  particles and effects. The terrain seam is hidden by a floor vignette that
  begins slightly inside the arena, reaches 70% darkness one-third across the
  outer margin, then holds. Creatures/shadows/auras/freeze overlays retain the
  preferred soft off-arena fade; the rejected hard playfield clip is gone.

**Systems**
- Audio: SFX routed from the sim's per-tick audio events; menu music
  (`crimson_theme`) + in-game (`gt1_ingame`); level-up cue. **[audit]** Multiple
  behavioral gaps vs. the base game — see the new **Audio parity** section.
- Haptics (fire / damage / reload). Persisted settings + local top-100
  high-score tables with timestamp, player-count and run-stat metadata.
- Fire-Bullets shot audio plays the base game's dual-sample substitution
  (`FIRE_BULLETS` + `PLASMA_MINIGUN`, `AudioBank.cs:165-170`). **[audit]**
  (undocumented faithful detail).
- Native `.crd` replay recording is on by default and Quest-confirmed through a
  6,256-tick exact replay on ABI v21.
- Asset-free builds recover through a generated first-run panel. A local helper
  turns a user-owned Crimsonland Classic install into an integrity-checked pack
  for PCVR or the Quest app inbox; import is traversal-safe and atomic, and a
  failed replacement preserves the working asset tree. Clean PCVR and Quest
  imports have both passed. On 2026-08-11 the private-copy CI build contract was
  reproduced locally and its fresh-key Release APK was clean-installed on Quest
  with the known-good pack staged; the app was deliberately left stopped for a
  clean first-launch import check. APK SHA-256 is `9612C1D6…CAB6C4`; the matching
  local/headset pack SHA-256 is `B43B203B…F0736F`.
- Reproducible asset-free PCVR packaging now builds Windows x64 and cross-builds
  Linux x64 from one Windows host. The local scripts and private-repository CI
  emit self-contained archives with loose, hash-recorded native libraries; public
  CI validates without uploading upstream-linked binaries.

## Provided by the sim already (just needs surfacing)

The embedded sim simulates the **whole game**, so these need only VR UI/render:
- **All game modes** — `HostSessionConfig.game_mode` (+ `quest_level_key`) selects
  Survival / Quest / Rush / Typo'Shooter / Tutorial. VR surfaces Survival,
  Rush, Quests and Tutorial. **[audit]** The enum also has **DEMO=0** (attract
  mode) and Tutorial is id **8** (non-contiguous), plus a replay-playback mode
  exists desktop-side (`modes/replay_playback_mode.py`).
- All **weapons** (~43 defined, 33 droppable), **58 perks** (verified,
  `perks/ids.py` 0..57), **15 bonus ids / 14 usable** (`bonuses/ids.py:11-27`;
  id 0 UNUSED is disabled — the old "~17 bonuses" figure was **wrong**),
  creature types (6 base types, ~68 spawn templates incl. quest bosses),
  terrain gen.
- **[audit] Unlock progression is part of the feature surface**, not just the
  content lists: pistol + AR/Shotgun/SMG (Survival) are free; everything else is
  quest-completion-gated via `quest_unlock_index`
  (`weapon_runtime/availability.py:16-50`); Splitter Gun needs full-hardcore
  unlock ≥0x28. Perks: base ids 1..27 + 4 always-on high-ids are available; the
  rest unlock via quest `unlock_perk_id` (`perks/availability.py:11-42`). A VR
  build that surfaces modes without persistence of `quest_unlock_index` would
  silently hand out everything.
- **[audit] Quest structure**: 50 quests = 5 tiers × 10 (`quests/level.py:7-9`),
  each with title, time limit, start weapon, unlock reward (weapon or perk),
  terrain slots, and a hand-authored spawn timeline (`quests/tier1..5.py`).
  Hardcore variants. Completion advances unlocks; stage-5 quests have no
  play-count save slots (`quests/status.py:5-33`).

## VR-inapplicable / substituted by design **[sanity pass 2026-07-09]**

Not every base-game behavior *should* be ported. These are decided dispositions
— they are **not** open parity gaps, and rows elsewhere in this doc reference
them. Revisit only if the interaction model changes.

| Base-game behavior | Disposition | Why / substitute |
|---|---|---|
| **Camera shake** (`camera.py`, nuke etc.) | **Substitute** | Never shake the VR camera (comfort). Candidate substitutes: subtle diorama-**table** shake or a controller haptic burst on big detonations. Decide in-headset. |
| **Menu cursor** (pulsing additive glow, `ui/cursor.py`) | **N/A** | Mouse affordance; poke interaction replaces it. `ui_cursor` art already repurposed as the move-hand reticle. |
| **Menu hover-fade / ready-glow / TAB-keyboard nav** (`menu.py:467-523,253-273`) | **N/A** | Hover and keyboard focus don't exist in poke UI. Panel/menu **slide-in timelines** are portable cosmetics — optional polish, not required parity. |
| **Full-screen fade transitions** (`transitions.py`) | **Substitute (optional)** | 2D-screen concept. At most a brief world-fade for comfort when entering/leaving gameplay; not parity work. |
| **Aim cursor (`ui_iconAim`) + direction arrows** (`overlays.py:108-138`) | **N/A** | Replaced by hand reticles (different affordance, already shipped). |
| **World aim spread circle** (`overlays.py:22-50`) | **Adapted** | `spread_heat` crosses ABI v11 and drives a reticle spread ring rather than a world-space overlay. |
| **Attract / demo mode** (`demo.py`) | **Skip (default)** | Storefront feature; idle headsets get removed, not watched. Optional novelty (diorama plays itself behind the menu) if ever cheap. |
| **Boot publisher-logo sequence** (`boot.py` 10tons/Reflexive splashes) | **Skip logos** | Desktop launch convention + third-party logo rights. A custom VR boot may still use the `intro` track. |
| **RTX beam mode** (`render/rtx/`) | **Skip** | Alternate desktop renderer path, not classic parity. |
| **Demo-trial / shareware gating** (`demo_trial.py`) | **Skip** | Shareware-build-only; VR is a full build. |
| **Mods screen** (`mods.py`) | **Skip (v1)** | Desktop mod loader; out of scope. |
| **"Show internet scores"** (high-scores browser checkbox) | **Skip** | Service defunct; local tables only. |
| **Typo'Shooter surfacing** | **Blocked on design** | Sim supports it, but typing via poke keyboard is impractical at gameplay speed. Needs a VR input design (voice? hybrid?) before the mode is surfaced. Not a render/UI port task. |
| **Network / co-op** | **In progress post-v1** | Slices 1-6 are implemented: native/VR direct LAN and relay room-code flow, explicit all-player ready-up, Survival/Rush, slot-aware presentation/results, canonical perk commands, multiplayer replay capture, DNS and reconnect/resync lifecycle. A two-player PC-host/Quest-client direct-LAN match and 59.6 Hz pacing are validated; broader hardware coverage and an operated relay remain pending. Casual Quests and the Python/native proof matrix are slices 7-8. See `notes/multiplayer-plan.md` and `notes/multiplayer-implementation.md`. |

## Screens & flow not yet in VR (exists in the base game)

| Area | Base-game source | Status in VR |
|---|---|---|
| **Game-over / results screen** **[audit]** | `screens/results/game_over.py` | **DONE 2026-07-09** (see Implemented; ABI v10). Remaining fidelity deltas are cosmetic slide/world-fade and hover tooltips. |
| **Quest results** **[audit]** | `screens/quest_views/quest_results.py`, `quests/results.py:25-192` | **Functional VR adaptation DONE.** Completion time, unlock advancement, Next/Retry/Quest Menu/Main Menu and the 5.10 end-note route are present. The animated life/unpicked-perk bonus breakdown and unlock-reveal presentation remain polish/parity gaps. |
| **Quest failed** **[audit]** | `screens/quest_views/quest_failed.py:47-419` | **Functional VR adaptation DONE.** Failure result, Retry, Quest Menu and Main Menu are present; retry-count taunts/score preview remain. |
| **End-note (game ending)** **[audit]** | `screens/quest_views/end_note.py:38-297` | **DONE.** Post-5.10 normal/hardcore text and Survival/Rush/Main Menu routes. |
| **High-scores browser** **[audit]** | `screens/high_scores_view/*` | **DONE 2026-08-10 (local/offline).** Top-100 persistence, 10-row pages, date/player/mode/name filters and quest prev/next. Internet scores skipped because the service is defunct. |
| **Game-mode select** | `play_game.py` | **DONE for supported VR modes.** Quests/Rush/Survival/Tutorial plus quest selection and hardcore. Typ'o is blocked on input design; multiplayer is post-v1. |
| **Attract / demo mode** **[audit]** | `demo.py:74-811`, idle trigger `menu.py:276-283` | **Skip by default** (see VR-inapplicable table). Base: after 23s menu idle the game plays itself — 6 scripted variants, AI bots, "DEMO MODE (n/6)" overlay. |
| **Boot sequence** | `screens/boot.py:38-262` | Partial-scope: publisher logo splashes **skipped by design** (see VR-inapplicable table); a custom VR boot with the `intro` track remains a future step. Listed so "boot straight into menu" isn't read as done. |
| **Statistics** | `stats.py:90-343` | **DONE 2026-08-10.** Lifetime per-mode statistics and routes to High Scores / Weapon DB / Perk DB / Credits. Per-screen music is cosmetic polish. |
| **Controls** | `controls.py` (+ `ui_textControls`) | **DONE as a VR-specific controls reference.** |
| **Databases** (encyclopedia) | `databases_perks.py`, `databases_weapons.py` | **DONE.** Unlock-aware perk and weapon browsing. |
| **Credits** | `credits.py` | **DONE 2026-08-10.** Paged VR credits; direct Secret route replaces the original text-click puzzle. |
| **AlienZooKeeper easter egg** **[audit]** | `panels/alien_zookeeper.py:129-546` | **DONE 2026-08-10.** Fully playable 6×6 swap/match minigame, +2s per match, score, timer, Reset/Back. |
| **Mods** | `mods.py` | **Skip v1** (see VR-inapplicable table) |
| **Network / co-op** | `network_lobby.py`, `network_session.py`, `crimson-zig/src/net/` | **Slices 1-6 implemented; broader headset/operated-relay validation pending.** The poke-only flow defaults to relay room codes, keeps direct LAN under Advanced, and requires every connected player to Ready before starting. Survival/Rush, local-slot input/HUD/results, per-player effects, canonical perk choices, all-slot replay capture, DNS and resume reconnect/resync are wired. A PC-host/Quest-client direct match is validated at 59.6 Hz. Casual Quests and the Python/native proof matrix remain; see `notes/multiplayer-implementation.md`. |
| **Demo-trial gating** **[audit]** | `demo_trial.py`, `ui/demo_trial_overlay.py` | **Skip** (see VR-inapplicable table — shareware-build-only). |
| **Replay playback mode** | `modes/replay_playback_mode.py` | Playback remains missing; native **.crd recording is implemented and Quest-confirmed** (`notes/replay-recording-plan.md`). |
| First-run seated **calibration** + **arena scale** UI | (VR-specific) | First-run now teaches poke/recenter and routes Adjust Reach directly into the persisted Arena & Layout editor. Measured reach sampling remains deferred. |

**Flow behaviors (cross-screen) not in VR [audit]:**
- **Screen-fade transitions** — global black fade in/out entering gameplay
  (`transitions.py:11-27`, `FADE_TO_GAME_ACTIONS`). Disposition: **optional
  substitute** (brief world-fade), see VR-inapplicable table.
- **Panel slide/rotate timelines** — every base panel animates in (elements
  rotate π/2→0 + slide on staggered windows, `menu.py:585-615`) with a
  `UI_PANELCLICK` cue on open (`menu.py:242-246`). Optional cosmetic polish
  (see VR-inapplicable table); the `UI_PANELCLICK` cue is portable regardless.
- **Pause/results background** — base freezes the world and draws it behind the
  panel, fading entities out over 500ms on exit-to-menu
  (`pause_menu.py:181-191`), and recaptures the gameplay ground into the menu
  background (`loop_view.py:1024-1039`). VR keeps rendering the last frame with
  no deliberate fade.
- **Death sequence pacing** — death VO, a death-timer delay, then the game-over
  panel slides in (`player_damage.py:93-127`); special-death paths (Final
  Revenge, Jinxed/Fatal-Lottery style) feed the same flow. **Partially closed
  2026-07-09**: VR now waits ~1.2 s before the death flow; death VO + panel
  slide-in still absent.

## HUD parity (element-level) **[audit — re-scoped]**

Previous claim "Faithful HUD — Done (ABI v8)" is accurate as a **static
art-inventory statement for the Survival subset** (top bar, pulsing heart incl.
low-HP fast pulse, health bar, weapon icon, ammo bars w/ class textures, XP
panel — art, UV math, alphas and thresholds verified matching `Hud.cs` vs
`hud.py`). It is NOT yet behavioral fidelity. Outstanding:

| Element / behavior | Base | VR status |
|---|---|---|
| **Enemy/target health bar** | `hud.py:245-254` — floating bar, color lerps red→green with HP ratio; **Doctor-perk-gated** (`base_gameplay_mode.py:625-669`, first creature within 12u of aim) | **DONE 2026-07-10** (Diorama.UpdateTargetHealthBar; faithful colours/alphas/geometry, Doctor-gated). |
| XP roll-up animation | `HudState.smooth_xp` (`hud.py:95-120`) — displayed XP eases toward target | **DONE 2026-07-10** (Hud.SmoothXp, faithful step rule). |
| Ammo "+N" overflow text | shown when clip > bar cap (`hud.py:521-527`) | **DONE 2026-07-10**: bars = clip up to 30, >30 collapses to 20 (native rule), "+ N" text after the row. |
| HUD fade on perk-menu open/close | whole HUD alpha eases (`survival_mode.py:486`) | **DONE 2026-07-10**: 400 ms ease (also fades on death), via GeometryInstance3D.Transparency. |
| Reload indicator | world-space clock gauge from `reload_timer/max` (`overlays.py:53-89`, `draw.py:482-537`) | **DONE 2026-07-10**: ui_clockTable/ui_clockPointer gauge at the aim reticle, pointer = progress×360°. In-headset check pending (pointer direction). |
| Aim spread circle | radius scales with `player.spread_heat` (`overlays.py:22-50`) | **DONE 2026-07-10** as the planned reticle spread ring: `spread_heat` now crosses the ABI (**v11**), ring radius = max(6, dist×heat×0.5)+2 game units at the aim point. |
| Bonus-HUD slots | 16 slots sliding in/out from screen-left, `ui_indPanel` + icon + timer bar(s), dual-timer 2P variant, compact mode (`bonuses/hud.py`, `hud.py:738-874`) | Missing (needs bonus-HUD state over ABI). |
| Weapon-name popup | on weapon change: panel + icon + name, 1s fade-in/hold/1s fade-out via `aux_timer` (`hud.py:876-936`) | Missing. |
| Quest HUD | sliding top panel, progress panel + green bar, analog clock (pointer = 6°/s), mm:ss text; XP/bonus HUD shifts down 80px (`hud.py:530-634,62`) | Missing even though Quests are surfaced; the sim still enforces the limit. |
| Rush/Typo time HUD | clock + "{N} seconds" (`hud.py:692-736`); Typo adds typing box + floating creature name labels (`typo_mode.py:255-271`) | Rush is surfaced but its faithful clock presentation is missing; Typo remains deliberately unsurfaced. |
| XP threshold source | sim-side | VR **reimplements the Survival curve in the frontend** (`Hud.cs:200-212`) — will drift if sim changes; fine for now, flagged. |
| On-screen banners | `ui_textLevelUp/PickAPerk/LevComp/Quest/Reaper/WellDone`; quest title/timer overlay + level-complete banner have fade/scale timelines (`ui/overlays/quest_run.py:23-70`) | Not used (perk prompt intentionally replaced — see Implemented). |

**Confirmed non-gaps:** base HUD renders numbers with font text, not `ui_num*`
sprites — VR's `Label3D`s are not a fidelity loss; there is **no** low-health
vignette/red-flash in the base game (the heart's faster pulse is the only cue,
and VR reproduces it); `ui/shadow.py` shadows apply to menu panels only, not the
HUD. Tutorial's 9-stage scripted prompt (`tutorial/timeline.py:18-48`) is surfaced
through the VR sidecar described above rather than treated as ordinary HUD.

## Render-pipeline parity (draw-pass audit)

The additive-pass bug (b5cea3c0) showed our earlier parity check was *feature/asset*
level and did **not** verify that each of the base game's **draw passes / blend
modes** is reproduced. Master draw order is `draw_world`
(`render/world/draw.py:73-105`): background → dead players → creatures
(overlays + sprites + shadows) → freeze overlay → alive players → projectiles
(laser-sight → primary → particle-pool → secondary → sprite-effect-pool →
effect-pool) → bonuses → labels → aim indicators → arrows → cursor.

| Base-game pass | Blend | In VR? |
|---|---|---|
| ground / decals / corpses / shadows | alpha (+darken sub-pass) | Partial — decals/corpses/shadows yes; **ground only tiles the base slot** (see ground-generator section); **[audit]** base bakes decals/corpses permanently into the 1024² ground RT with a two-pass corpse **shadow darken** (ZERO/INV_SRC_ALPHA, `terrain_render.py:506-539`) — VR ring-buffers quads and skips the darken pass. |
| **player trooper** **[audit]** | alpha | **Approximated & was untracked as a pass.** Base: separate leg frame from `move_phase` + torso with **recoil offset**, per-part shadows, 2P tint, dead-frame ramp (`trooper.py:72-296`). VR: single static aimed torso (`Diorama.cs:16` — "leg animation needs a move-phase ABI field; deferred"). |
| **player shield ring** **[audit]** | additive | **DONE 2026-07-10** (`DioramaProjectiles.RenderPlayerFx`) — counter-rotating pulsing `SHIELD_RING` pair, centred 3u along aim, strength ramp on the last second, native sizes/tint/alphas; shield timer came over ABI v11. In-headset validation pending. |
| **player radioactive aura** **[audit]** | additive | **DONE 2026-07-10** (`RenderPlayerFx`) — pulsing green `AURA` (100u, native alpha curve) on a priority-14 mesh so it draws UNDER the player sprite (15); drawn regardless of health like the native pass. In-headset validation pending. |
| **Sharpshooter laser sight** **[audit]** | additive | **Missing.** Red gradient trail quad 15→512u along aim (`projectiles.py:110-180`), drawn first in the projectile pass. |
| creatures (sprites + tint) | alpha | Yes — energizer tint / lifecycle fade / death staging / hit-flash verified. Creature **shadow** sub-pass (tinted silhouette 1.07×, `creatures.py:55-68`) approximated by a generic soft blob. |
| creature overlays (poison/plague/monster-vision auras) | alpha | Yes (ABI v7) — **[audit]** but fixed-size (90/80/60) regardless of creature `Size`; boss auras don't scale (`Diorama.cs:693-743`). |
| freeze overlay (`draw_freeze_overlay`, per-creature `FREEZE_SHATTER`) | alpha | **Yes** (`Diorama.cs:820-860`) — **[audit]** was implemented but missing from this table. |
| **projectiles / secondaries — per-type draw variants** **[audit]** | additive **+ one subtractive** | **DONE 2026-07-10 (ABI v12, DioramaProjectiles.cs)** — full port of the dispatch: bullet trails (gradient quad + per-type head colour + bullet16 head sprite), plasma tail trains + head + aura per weapon config, ion/fire beam stepped bodies + heads + **ion chain arcs** to collidable creatures (Ion Gun Master reach ×1.2 via perk flag), Fire-Bullets glow overlay, pulse expansion, splitter/blade sprites (blade spin from the stable pool index), **Plague Spreader darken pass** (SRC=ZERO/INV_SRC_ALPHA reproduced EXACTLY with blend_mul + pow-2.2, which commutes through the linear pipeline), per-rocket glow styles, two-quad detonations (replacing the synthetic EmitFx blobs — also resolves the flagged double-draw), Sharpshooter laser sight, and the fade-stage visuals for life<0.4 (the old blanket cull is gone). ABI v12 added origin/speed_scale/travel_budget/pool_index. In-headset validation pending (batched). |
| `draw_effect_pool` alpha pass (flags & 0x40): smoke, casings, blood | alpha | Yes |
| `draw_effect_pool` additive pass: ring, flash, shockwave burst | additive | Yes (b5cea3c0) — restored explosion ring/flash/shockwave, enemy hit-sparks, projectile muzzle/impact flashes. |
| `draw_particle_pool` (`state.particles`): additive glows/sparks | additive | Yes (ABI v7) — `RenderGlowPool` big/normal/bubblegun sub-passes; formulae verified. |
| **`draw_sprite_effect_pool`** (`state.sprite_effects`) **[audit]** | alpha | **DONE 2026-07-10 (ABI v13)** — `SpriteEffectSnap` stream packed after the glow pool (gate test pins it); rendered as `EXPLOSION_PUFF` quads, FULL cell rect (this pool skips the effect pool's 2px clamp), plain alpha, priority 22 (between secondaries and the effect pool, native order), gated on graphics detail ≥2 like `fx_detail_2`. In-headset validation pending. |
| muzzle flash | additive | **Approximated [audit].** Base: `muzzle_flash.png` at recoil offset, **suppressed** by weapon flag 0x8, half-size by 0x4 (`trooper.py:245-273`). VR: generic soft blob for any `MuzzleFlashAlpha>0.01`, flags ignored (`EmitFx`, `Diorama.cs:1407-1419`). **Possible double-draw**: the synthetic blob + the real effect-pool muzzle burst (restored by b5cea3c0) may both render — verify in-headset. Same question for `EmitFx` detonation blobs vs effect-pool ring/flash. |
| bonus pickups | alpha | Partial — icons yes; **[audit]** missing the **bubble container** behind every pickup, `sin⁴` pulse scale, rotation wobble, and the distinct weapon-drop `ui_wicons` variant (`bonuses.py:50-121`). Hover labels also missing (base-game consumer of "UI Info texts"). |
| aim indicators / gauges / clock | alpha | Custom VR reticles — cursor/arrows **N/A by design** (see VR-inapplicable table). But the *data*-carrying pieces are real gaps: reload gauge absent (data in ABI, unused) and spread feedback absent (**adapt** as reticle spread ring; `spread_heat` not in ABI). |
| **camera shake** **[audit]** | n/a (camera) | **Substitute-by-design** (see VR-inapplicable table): never shake the VR camera; candidate table-shake or haptic burst. Base: RNG jitter offset, e.g. nuke sets 0x14 pulses / 0.2s (`camera.py`). Distinct from the nuke-visual-scale backlog item. |
| **RTX beam mode** **[audit]** | shader | **Skip** (see VR-inapplicable table). Alternate GLSL "virtual beam" renderer path (`render/rtx/beam.py`), selectable at runtime; not required for classic parity. |
| HUD | alpha | See HUD parity section (art faithful for Survival subset; behaviors open). |

**`fx_detail` semantics [audit]:** base graphics detail is three independent
booleans (`grim/config.py:149-160`): `fx_detail_0`=creature shadows,
`fx_detail_1`=big ambient glow/plasma aura/rocket glow, `fx_detail_2`=sprite-
effect pool — plus a separate sim-side `detail_preset` 1-5 for spawn thinning.
VR maps one slider: shadows ≥3, all particles/glow ≥2, and never sends
`detail_preset`. Non-faithful mapping (base can run shadows-on/effects-off).

**Confirmed non-gaps [audit]:** the base game has **no** lighting, day/night,
bloom, or color-grading pass (`grim/color.py` is a plain struct; fixed terrain
tints only) — nothing to port there. `render/pipeline.py`/`frame.py`/`sink.py`
are orchestration only. Base camera is a fit-to-window scale, not a follow-cam —
no zoom/follow behavior exists to miss; only the shake (above) is real.

**Ground generator — DONE 2026-07-10.** The diorama now reproduces the native
ground RT: a one-shot SubViewport canvas replays `grim/terrain_render.py`
(CrtRand LCG from `terrain_seed`; base/overlay/detail scatter passes 1600/70/30
at 1024²; 128px patches rotated about centre, RNG order rotation→Y→X; native
tints over the 63/56/25 clear). Scatter math is golden-tested against the
reference (TerrainGen.cs + TerrainGenTests). The RT maps 1:1 onto the playable
zone (margin wraps) and the surrounding world floor shares it; missing sheets
fall back to the old base-slot tiling. Decal/corpse BAKING into the RT (the
native permanent-bake + corpse shadow darken) remains ring-buffered quads —
still tracked in the draw-pass table. In-headset validation pending.
Historical context of the gap:
The terrain floor was marked "implemented",
but that masked a fidelity gap: the diorama only **tiled the base terrain slot**
(`ter_qN_base`, Nearest-filtered), whereas the base game builds the ground with
grim's **seeded generator** compositing all three slots — base + overlay (`tex1`)
+ detail — into a 1024² bitmap with dirt/grass patches placed by `terrain_seed`
(`grim/terrain_render.py:230-274,412-441`; scheduling in
`world/render_resources.py`, `terrain_slots.resolve_terrain_slots`). The ABI
already ships the 3 slot ids + seed (`crimson_host_terrain_info`, v3);
reproducing placement needs a port of the grim ground-gen algorithm (bake a
composite per seed at load, or a runtime detail-blend approximation). Until then
the arena floor reads flatter than the original. Not yet scheduled.

## Audio parity **[audit — new section]**

Architecture: the sim resolves all SFX randomness deterministically and emits
final `SfxId`s + a `trigger_game_tune` flag; screens emit their own UI SFX. VR
consumes the sim stream (`SimSession.CaptureAudio` → `AudioBank.Route`) and
reimplements screen audio itself. Systemic gaps:

- ~~**In-game music trigger**~~ — **DONE 2026-07-10**: entering a run (and Play
  Again) now STOPS the menu theme; the game tune starts on the sim's
  `trigger_game_tune` (first creature hit) picking gt1/gt2 by the ABI's
  resolved roll.
- ~~**Music crossfade / exclusive channels**~~ — **DONE 2026-07-10**: AudioBank
  fades out at 0.5/s and fades the next track in at 1.0/s only after silence
  (music.py rates).
- **Reflex-Boost pitch-down**: grim slows SFX playback toward half rate during
  reflex boost (`grim/sfx.py:63-80`); VR has no pitch path. (Desktop rewrite
  currently stubs the timer too — `audio_router.py:31-43` — but the mechanism is
  native behavior.)
- **Voice model**: base = 4 voices per distinct sample, uncapped globally
  (`grim/sfx.py:17,93-100`); VR = shared pool of 24 mono 3D players,
  oldest-steal (`AudioBank.cs:32,134-147`). Different overlap under load.
- **Spatialization**: the base rewrite plays everything **flat** (no pan — the
  "panned" comments are unimplemented); VR positions shots at the player and the
  rest at arena centre with attenuation **disabled**. VR is *more* spatial than
  the rewrite; fine, now documented.
- ~~**Volume semantics**~~ — **DONE 2026-07-10**: music volume 0 now hard-stops
  playback (resumes the desired track, fading in, when raised).
- **Quest victory sting**: `crimsonquest` doubles as the quest-completion music,
  ramped from 0 (`quest_mode.py:345-356`) — not just the "quest/demo theme".
- **Verify**: possible `UI_LEVELUP` double-play in VR (both `Route`d from loose
  SFX and via `PlayLevelUp`) — check against the zig ABI's emission.

**Music inventory:** `crimson_theme` (menu — wired), `gt1_ingame` +
`gt2_harppen` (Survival game-tune pool — VR plays only gt1, wrongly
deterministic), `crimsonquest` (attract theme + quest music + victory sting —
unused), `intro` (boot logos — unused), `shortie_monk` (**Statistics screen**
music — unused).

**UI SFX:** wired — `UI_BUTTONCLICK`, `UI_TYPECLICK_01/02`, `UI_TYPEENTER`,
`UI_LEVELUP`, `UI_PANELCLICK` (game-over panel open, 2026-07-09). Unused
(screen-only, screens not in VR) — `UI_CLINK_01` (quest-results ticks +
AlienZooKeeper), `QUESTHIT` (quest-objective hit). → **Correction:** `UI_BONUS` was mislisted as unused —
it's a **sim** event (bonus spawn/pickup, `creatures/runtime.py:469`,
`presentation_step.py:410`) and **already plays in VR** via the loose-SFX stream.

## Unused assets currently staged/available

**UI textures in the source assets but NOT staged/used:**
- HUD: `ui_gameTop`†, `ui_indPanel`†, `ui_indBullet/Electric/Fire/Life/Rocket`†,
  `ui_num1..5`, `ui_lifeHeart`†, `ui_wicons`† (†now used by the ABI-v8 HUD),
  `ui_iconAim`.
- Banners: `ui_textLevelUp`, `ui_textPickAPerk`, `ui_textLevComp`, `ui_textQuest`,
  `ui_textControls` (`ui_textReaper`† now used by the game-over panel;
  `ui_textWellDone`† staged for quest results).
- Controls/widgets: `ui_button_64/82/128/145x32`, `ui_dropDownOn/Off`, `ui_arrow`
  (`ui_clockTable`†/`ui_clockPointer`† now used by the game-over gauge; still
  wanted by the reload gauge + quest/rush clocks).
- `ui_menuPanel` is staged but currently unused (dropped the full-screen panel;
  note the missing results screens all use the classic 3-slice panel renderer,
  `ui/menu_panel.py` — porting any of them implies a VR panel treatment).

## Known render gaps / polish backlog (from in-headset testing + audit)

- ~~Effect "square edge" workarounds~~ — **root-caused + made faithful
  (2026-07-09)**: the bake's effect UV rect inset 2px on every side (native =
  cell corner + 2px right/bottom clamp only), cropping edge-running art
  (freeze shards through α≈226, glow through α=255) — THAT was the "square
  edge", not filter bleed. The two workarounds it spawned (1px shader inset in
  8521d3e4, texture-alpha squaring in 233b7018) dimmed/shrank every effect
  (pickup ring, freeze shards visibly thin). Fixed at the source: bake emits
  the native rect; both particle shaders restored to reference blend math
  (alpha pass `c.a * col.a`, additive premultiplied `c.rgb*col.rgb*c.a*col.a`).
  **Follow-up (same day, post-screenshot):** the remaining milkiness is
  BLEND-SPACE — the original composites in sRGB, Godot in linear; the same
  numbers blended in linear read brighter for translucent sources. Both
  particle shaders now sample RAW (no `source_color`), do the native
  display-referred tint math, and approximate sRGB-space compositing with
  numerically fitted curves (alpha pass: luma-dependent alpha exponent
  0.6→1.6, mean err 0.086→0.018 for white sources; additive: pow 1.1 on the
  display product, err 0.053 vs 0.156 all-linear). Fitted against arena-toned
  backgrounds — revisit constants if the ground palette changes. Verify
  in-headset that no edge artifact returns (any residual right/bottom crop on
  full-bleed glow cells is native-faithful). Note the giant freeze/reflex
  pickup sphere (`spawn_ring`, lifetime 1 s, scale 45×/s) natively out-grows
  the screen in ~0.3 s — the diorama shows its whole life; if it still reads
  as too dominant, that's a VR-view adaptation question (clip/fade at arena
  bounds), not blend math.
  **Root cause #3 — the actual "square around the effect" (user screenshot
  diff): FOG on additive materials.** Godot applies distance fog AFTER the
  fragment shader, lifting even ALBEDO=0 fragments to fog_color×fog_amount;
  under `blend_add` that ADDS a faint uniform wash over the quad's entire
  footprint — a translucent square around the pickup/freeze ring and square
  edges on the pickup burst sparks, regardless of texture alpha (verified:
  imported texture alpha is exactly 0 there; the wash was uniform in the
  screenshot pixel-diff). Fixed with `fog_disabled` on both particle shaders
  and `DisableFog` on the additive glow StandardMaterial (streaks + EmitFx).
  Alpha-blend materials can't leak this way (zero alpha contributes nothing
  in mix blending).
- **Nuke** blast visual ~½ the effective radius (particle-scale work).
- **[audit]** Verify muzzle-flash / detonation **double-draw** (synthetic
  `EmitFx` blobs vs restored effect-pool bursts) in-headset; if confirmed, drop
  the synthetic path in favor of the pool.
- **[audit]** Creature-overlay auras don't scale with creature size.
- **[audit]** Corpse-stamp orientation + projectile-streak orientation still
  carry "validate in-headset" comments (`Diorama.cs:1146,1496`).
- **[audit]** `VrSlider` (grab-drag) is dead code; `SettingsMenu.cs:8-13` +
  ValidationChecklist still describe the dead-zone control as grab-drag but it's
  a poke `VrSegmentedSlider`. Fix comments or revive the widget.
- ~~Perk-select fade~~ — DONE. ~~HUD art~~ — DONE (ABI v8; behaviors tracked in
  HUD section). ~~Poke/keyboard SFX~~ — DONE.

## Suggested next faithful-parity steps (rough order)

1. ~~**Game-over / results screen**~~ — **DONE 2026-07-09** (ABI v10; see
   Implemented). Follow-ups fold into other items: High-scores button → the
   high-scores browser (item 6), world-fade + slide-in → cosmetic polish
   (item 8).
2. ~~**Game-mode select**~~ — **DONE 2026-07-10 (ABI v14)**: Play Game panel
   (Quests/Rush/Survival, native order), quest stage/level select gated by
   persisted `quest_unlock_index` (frontier-stage default, locked rows
   dimmed), quest completed/failed end panels (Next Quest advances the
   frontier; Retry on death; no highscores in quests), per-mode highscore
   tables (Rush has its own), `game_mode`/`quest_level_key`/`status_quest_
   unlock_index` wired into session create, per-run terrain re-apply (quest
   terrain slots). ABI v14 appends tick-result `quest_completed`. In-headset
   validation pending. Remaining from this item: quest TIME-LIMIT HUD
   (timer not drawn; sim enforces), quest end-note screens (5.10 finale),
   hardcore toggle (unlock>=40), Typo'Shooter (still blocked on VR input
   design — see VR-inapplicable table). **Validated 2026-07-10: all five
   checklist items functionally PASS; `playmenu` marked FAIL on THEMING
   only** (the new panels are plain VrButton stacks, not base-game art) —
   see the menu-theming pass, item 10.
3. ~~**Per-projectile-type render variants**~~ — **DONE 2026-07-10** (see the
   draw-pass table; ABI v12, validated in-headset 6/6). The Sharpshooter
   **laser sight** landed with it. ~~Still open from this cluster~~: the
   **sprite-effect pool** stream (ABI v13) and the **shield-ring /
   radioactive-aura** passes are **DONE 2026-07-10** too — the whole
   render-pass cluster is now ported (in-headset validation pending). Note:
   `draw_secondary_projectile`'s `alpha` param is the global world-fade
   (`ctx.entity_alpha`), which VR skips by design — no ABI field needed.
4. ~~**HUD behaviors**~~ — **mostly DONE 2026-07-10** (enemy health bar, reload
   gauge, XP roll-up, ammo "+N"/30-cap, HUD fade, spread ring; ABI v11 also
   exports `shield_timer` + perk flags for Radioactive/Sharpshooter — their
   RENDER passes are still open). Remaining: bonus-HUD slots + weapon-name
   popup (need bonus-HUD state over the ABI).
5. **Audio behaviors**: first-hit random game-tune + crossfade + `gt2_harppen`;
   music-volume-0 stop; reflex-boost pitch (Godot `pitch_scale`). The inert UI
   Info Texts control is no longer surfaced; reintroduce it only with hover labels.
6. ~~**Statistics / Controls / high-score browser**~~ — **DONE**. Local top-100
   tables, filters/paging, lifetime stats and all hub routes are implemented.
7. ~~**Databases**~~ — **DONE** (perk/weapon encyclopedia with unlock rules).
8. Custom VR boot with the `intro` track (publisher logos + attract mode are
   skipped per the VR-inapplicable table); per-mode music; pause world-fade;
   optional cosmetic polish: panel-slide timelines + brief world-fade
   transitions.
9. ~~Credits + AlienZooKeeper~~ — **DONE 2026-08-10**.
10. **Base-game menu THEMING pass — DONE 2026-07-10** (in-headset validation
    pending): baked small font rendered as glyph-quad meshes
    (SmallFontLabel), VrButton classic skin (ui_button plates + native hover
    fill/click flash) + icon mode, ui_menuPanel 3-slice backdrops,
    itemTexts title rows + banner arts, quest stage icons; applied to
    PlayGameMenu / QuestSelectMenu / QuestResultPanel / GameOverPanel
    buttons / the new StatsMenu. Remaining polish: Options/VR-Settings
    button retrofit, slide-in timelines, hover tooltips, hardcore checkbox.
    Also landed in the same batch: **quest time-limit HUD timer**,
    **player leg animation + torso recoil (ABI v15 move_phase)**, and the
    **Statistics screen + high-scores browser** (lifetime per-mode stats
    persisted at run end). The original scoping notes (kept for the
    remaining polish):
    - **Buttons**: `ui_buttonSm`/`ui_buttonMd` plate art, 32px tall, width
      82 (short labels) / 145 (`button_width`, perk_menu.py:276-282); label
      in the game's SMALL FONT centered at y+10, alpha 0.7 idle → 1.0
      hovered; a hover HIGHLIGHT FILL rect at (x+12, y+5, w−24, 22) tinted
      (0.5,0.5,0.7) with alpha = hover ramp (6/ms up, 4/ms down, 0..1000)
      and a click bias toward blue-white (+0.0005/+0.0007 per press-ms) —
      `button_draw`/`button_update` (perk_menu.py:258-365). VR adaptation:
      teach VrButton an optional themed skin (plate texture + hover quad +
      font) while keeping the poke mechanics/depths identical; hover ramp
      maps to poke-proximity or gaze-less dwell.
    - **Panel backdrop**: `ui_menuPanel` with the native 3-slice + 1px
      border inset (`draw_classic_menu_panel`, ui/menu_panel.py:23) behind
      every panel; panel titles from the `ui_itemTexts` rows where one
      exists (Play Game = row 1 title art, menu.py MENU_LABEL_ROW_*).
    - **Small font**: grim's small font sheet — bake the atlas + advance
      metrics and render text as quads (Label3D system font is the single
      biggest "not Crimsonland" tell). Reusable for HUD popups later.
    - **Quest select**: native stage ICONS (QUEST_STAGE_ICON_* layout,
      selected icon full-scale, others 0.8) instead of numbered tabs; quest
      rows in small font with the row hover pad; hardcore checkbox art at
      unlock>=40 (quest_views/shared.py offsets).
    - **Quest results/failed**: match quest_views/quest_results.py +
      quest_failed.py layouts (banner text, stats block) once themed.
    - **Motion/sfx**: panel slide-in timelines (PANEL_TIMELINE_START/END,
      `_ui_element_anim` slide-from-side) + ui_panelClick on open (cue
      already in AudioBank); mode-button tooltips (hover-fade alpha ramp
      0.0009/ms) under the list like `_draw_tooltips`.
    - **Bake needs**: stage `ui_buttonSm`, `ui_buttonMd`, `ui_menuPanel`,
      the small-font sheet, quest stage icons.
    - **In scope**: the three new panels + retro-fit PauseMenu/Options/
      VR-Settings buttons. **Exempt**: dev tooling (checklist, Debug FX
      menu) and the GameOverPanel (already uses the real reaper/clock/
      wicons art; only its buttons pick up the themed skin).

**Lesson (updated):** a feature/asset parity check misses render-pipeline and
*behavioral* correctness. Any base-game `begin_blend_mode` / draw pass / animated
or randomized behavior should be explicitly matched in the diorama, and every
"Yes/Done" in this doc should say *at what fidelity*. The 2026-07-09 audit found
the previous doc's gaps were exactly of this class: ground generator, per-type
projectile draws, first-hit random music, HUD behaviors, and the missing
game-over screen were all hidden behind feature-level checkmarks.
