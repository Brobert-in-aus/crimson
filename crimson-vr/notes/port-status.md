# CrimsonVR port status — implemented vs. remaining

A map of what the VR frontend does today, what the embedded sim already provides,
and what's still missing to reach faithful parity with the base game. The
**sim** (`crimson-zig`, embedded as `crimson_host`) runs the full game; the VR
work is almost entirely the **presentation + interaction** layer, so most gaps
below are "not surfaced in VR yet", not "not simulated".

_Last updated: 2026-07-09 (full-game feature audit: six-way sweep of screens/flow,
HUD, render pipeline, audio, gameplay/meta, and VR-claim verification. This pass
found the doc had been tracking parity at feature/asset granularity; it now also
tracks behavioral fidelity. Corrections and new gaps are marked **[audit]**.)_

## Implemented in VR

**Flow / screens**
- Boot straight into a **main menu** (custom, using the original `ui_signCrimson`
  logo + `ui_menuItem` neon-bar plates): Play Game, Options, Statistics (inert),
  Quit. **[audit]** Fidelity caveats: the base menu items slide-in/rotate on a
  staggered timeline with hover-fade alpha ramps and an additive "ready" glow
  (`menu.py:405-410,467-480,510-523`); VR plates are static poke targets. The
  base game's pulsing additive menu **cursor** (`ui/cursor.py:41-92`) has no VR
  analog by design (poke interaction) — `ui_cursor` is repurposed as the
  move-hand reticle.
- **Survival gameplay** rendered as the tabletop diorama.
- **Pause** menu (flat toggle + Resume / Settings / Quit). Quit returns to the
  main menu; the main menu's Quit exits the app. **[audit]** The base pause menu
  is a reskin of the animated main-menu system (plates + sign + slide-in +
  world-fade background, `pause_menu.py:45-437`); VR's flat panel is a deliberate
  interaction substitute, not a fidelity match. Base layout is Options/Quit/Back
  (ESC resumes); VR is Resume/Settings/Quit.
- **Options** screen mirroring the base game (segmented `ui_rectOn/Off` sliders:
  Sound / Music / Graphics detail; `ui_checkOn/Off` "UI Info texts") + a **VR
  Settings** submenu (movement hand, dead zone, debug overlays, **render-scale
  supersampling 0.6-1.6× and MSAA Off/2×/4×** — the latter two were undocumented;
  `SettingsMenu.cs:99-118`, `Main.cs:405-417`).
  **[audit] "UI Info texts" is INERT**: the checkbox persists
  `UserSettings.UiInfoTexts` but nothing reads it (`Main.cs:198`). Undisclosed
  until now — either wire it (bonus hover labels are its base-game consumer) or
  label it inert like Statistics.
  **[audit]** "Graphics detail" only culls VR-side nodes
  (`Diorama.SetGraphicsDetail`, `Diorama.cs:156-174`); it does not send
  `detail_preset` to the sim (ABI supports it, `crimson_host.h:339`), so sim-side
  particle-spawn thinning never varies. See also the `fx_detail` semantics row in
  the draw-pass table.
- **Level-up / perk pick**: level-up button + accumulated counter + `ui_levelUp`
  sound; poke to open cards; per-card "?" press-and-hold description popup.
  **[audit]** This *replaces* (not omits) the base game's perk-prompt banner — a
  `ui_menuItem` bar that hinge-swings in from the top-right carrying
  `ui_textLevelUp` with an additive pulse (`perk_prompt_ui.py:91-131`) — and the
  base `ui_menuPanel` perk list (sponsor line + tighter rows under Perk
  Expert/Master, neon hover items). Intentional VR substitution; noted so the
  banner isn't counted twice as "unused art".
- **Game-over / results screen** (2026-07-09, ABI v10): death → ~1.2 s pacing
  delay (stand-in for the base death VO + death-timer) → the results panel
  appears immediately as ONE composite death screen (base two-phase panel):
  when the score ranks (base top-100 gate; ours is the local top-10) the panel
  opens raised/pushed back with the virtual keyboard in front ("State your
  name, trooper!", buttons hidden), then drops to the buttons phase on Enter;
  unranked deaths open straight in the buttons phase — with the `ui_textReaper` banner,
  score, rank ordinal, game time as mm:ss + the animated
  `ui_clockTable`/`ui_clockPointer` gauge (6°/s), most-used weapon icon
  (`ui_wicons`) + display name (weapons table baked into the sprite manifest),
  frags, hit %, and Play Again / Main Menu buttons; `UI_PANELCLICK` cue on
  open. Fidelity caveats: no ease-out slide-in / world alpha-fade, no
  High-scores button (browser screen doesn't exist yet), local top-10 not
  top-100, no hover tooltips, keyboard replaces inline text entry (by design).
  Kill count + most-used weapon crossed the ABI in **v10**
  (`creature_kill_count`, `most_used_weapon_id` in the tick result).
- **Arena recenter** — left `menu_button` / right `ax_button` reposition + re-yaw
  the tabletop, plus initial auto-place (`Main.cs:1076-1122`). **[audit]**
  (was undocumented).
- **Validation checklist** (dev tool) standing to the right of the arena.

**Presentation (diorama)**
- Creatures (animated sheets, per-type tint, energizer/freeze/hit-flash, death →
  ground + corpse stamp incl. drop-to-ground death staging), projectiles +
  secondaries (glow streaks — see per-type variants gap), bonuses/powerup icons,
  blood/scorch decals, muzzle flash (see approximation note), explosions,
  drop-shadows, terrain floor + surrounding world floor, grey-fog skybox.
- Stopped-projectile cull (ABI v9): projectiles with `LifeTimer < 0.4` are
  dropped except gauss/ion linger beams (`Diorama.cs:1278-1335`). **[audit]**
  Side effect: the base game's *fade-stage* visuals (plasma/pulse/beam-core
  end-of-life flashes) are culled with them.
- Per-type projectile glow tints (ION blue / FIRE_BULLETS orange / SHRINKIFIER
  green / BLADE_GUN magenta, `Diorama.cs:301-313`). **[audit]** (undocumented).
- **MR passthrough** path (Quest 3 ALPHA_BLEND) — built but **compile-time only**.
  **[audit]** `_displayMode` is hardcoded to Skybox with no runtime toggle
  (`Main.cs:114`); "opt-in" overstated user-reachability. The ValidationChecklist
  "passthrough" item cannot currently be exercised. Settings toggle is M4.

**Systems**
- Audio: SFX routed from the sim's per-tick audio events; menu music
  (`crimson_theme`) + in-game (`gt1_ingame`); level-up cue. **[audit]** Multiple
  behavioral gaps vs. the base game — see the new **Audio parity** section.
- Haptics (fire / damage / reload). Persisted settings + local highscores
  (top-10; base game keeps top-**100** per-mode tables — see meta section).
- Fire-Bullets shot audio plays the base game's dual-sample substitution
  (`FIRE_BULLETS` + `PLASMA_MINIGUN`, `AudioBank.cs:165-170`). **[audit]**
  (undocumented faithful detail).

## Provided by the sim already (just needs surfacing)

The embedded sim simulates the **whole game**, so these need only VR UI/render:
- **All game modes** — `HostSessionConfig.game_mode` (+ `quest_level_key`) selects
  Survival / Quest / Rush / Typo'Shooter / Tutorial. VR currently **hardcodes
  Survival** (`game_mode:1`). **[audit]** The enum also has **DEMO=0** (attract
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
| **World aim spread circle** (`overlays.py:22-50`) | **Adapt** | Don't port as a world overlay; surface `spread_heat` as a **reticle spread ring**. Still needs `spread_heat` added to the ABI. |
| **Attract / demo mode** (`demo.py`) | **Skip (default)** | Storefront feature; idle headsets get removed, not watched. Optional novelty (diorama plays itself behind the menu) if ever cheap. |
| **Boot publisher-logo sequence** (`boot.py` 10tons/Reflexive splashes) | **Skip logos** | Desktop launch convention + third-party logo rights. A custom VR boot may still use the `intro` track. |
| **RTX beam mode** (`render/rtx/`) | **Skip** | Alternate desktop renderer path, not classic parity. |
| **Demo-trial / shareware gating** (`demo_trial.py`) | **Skip** | Shareware-build-only; VR is a full build. |
| **Mods screen** (`mods.py`) | **Skip (v1)** | Desktop mod loader; out of scope. |
| **"Show internet scores"** (high-scores browser checkbox) | **Skip** | Service defunct; local tables only. |
| **Typo'Shooter surfacing** | **Blocked on design** | Sim supports it, but typing via poke keyboard is impractical at gameplay speed. Needs a VR input design (voice? hybrid?) before the mode is surfaced. Not a render/UI port task. |
| **Network / co-op** | Out of scope v1 | (unchanged) |

## Screens & flow not yet in VR (exists in the base game)

| Area | Base-game source | Status in VR |
|---|---|---|
| **Game-over / results screen** **[audit]** | `screens/results/game_over.py` | **DONE 2026-07-09** (see Implemented; ABI v10). Remaining fidelity deltas: slide-in/world-fade animation, High scores button (blocked on the browser screen), top-100 table, hover tooltips. |
| **Quest results** **[audit]** | `screens/quest_views/quest_results.py`, `quests/results.py:25-192` | Missing. Animated time breakdown (base time counts up, life + unpicked-perk bonuses tick in 1s steps w/ `UI_CLINK_01`), Well-Done banner, unlock-reveal (weapon/perk), Play Again / Play Next / High Scores / Main Menu; 5.10 routes to end-note. |
| **Quest failed** **[audit]** | `screens/quest_views/quest_failed.py:47-419` | Missing. Reaper banner, retry-count-dependent taunt lines, score preview, Play Again / Play Another / Main Menu. |
| **End-note (game ending)** **[audit]** | `screens/quest_views/end_note.py:38-297` | Missing. Post-5.10 victory screen; hardcore-vs-normal body text (Splitter Gun / Typo unlock). |
| **High-scores browser** **[audit]** | `screens/high_scores_view/*` | Missing. 100-entry scrollable table + right panel with 4 dropdowns (date filter / player count / mode / name slot), internet-scores checkbox, quest prev/next. VR keeps a local top-10 nothing displays. |
| **Game-mode select** | `play_game.py` | Missing — Survival hardcoded. Note it's a **two-level** flow: mode select (player-count dropdown 1-4, per-mode tooltips, times-played counts) then the **quest stage/level select** (`quest_views/quests_menu.py`: 5 stage pages × 10 rows, hardcore checkbox, unlock gating). |
| **Attract / demo mode** **[audit]** | `demo.py:74-811`, idle trigger `menu.py:276-283` | **Skip by default** (see VR-inapplicable table). Base: after 23s menu idle the game plays itself — 6 scripted variants, AI bots, "DEMO MODE (n/6)" overlay. |
| **Boot sequence** | `screens/boot.py:38-262` | Partial-scope: publisher logo splashes **skipped by design** (see VR-inapplicable table); a custom VR boot with the `intro` track remains a future step. Listed so "boot straight into menu" isn't read as done. |
| **Statistics** | `stats.py:90-343` | Menu item present but inert. **[audit]** Base screen is a hub: total playtime + links to High Scores / Weapon DB / Perk DB / Credits; backed by persisted 52-weapon usage counts + quest play counts (`save_status.py:52-55`). Plays `shortie_monk`. |
| **Controls** | `controls.py` (+ `ui_textControls`) | Missing |
| **Databases** (encyclopedia) | `databases_perks.py`, `databases_weapons.py` | Missing |
| **Credits** | `credits.py` | Missing. **[audit]** Hides the secret button (`credits.py:507-525`) that opens AlienZooKeeper. |
| **AlienZooKeeper easter egg** **[audit]** | `panels/alien_zookeeper.py:129-546` | Missing. Hidden, fully playable 6×6 **match-3 minigame** ("a puzzle game unfinished / ..or something more?"): click-to-swap, +2s per match, score, Reset/Back. Reached Statistics → Credits → secret button. |
| **Mods** | `mods.py` | **Skip v1** (see VR-inapplicable table) |
| **Network / co-op** | `network_lobby.py`, `network_session.py` | Missing (out of scope for v1) |
| **Demo-trial gating** **[audit]** | `demo_trial.py`, `ui/demo_trial_overlay.py` | **Skip** (see VR-inapplicable table — shareware-build-only). |
| **Replay playback mode** | `modes/replay_playback_mode.py` | Missing (desktop tool); native **.crd recorder** deferred (`notes/replay-recording-plan.md`). |
| First-run seated **calibration** + **arena scale** UI | (VR-specific) | Deferred slices (StartPrompt built, always `Skip()`s). |

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
| **Enemy/target health bar** | `hud.py:245-254` — floating bar, color lerps red→green with HP ratio; drawn every frame in all modes | **Missing & was untracked.** High gameplay-readability value. |
| XP roll-up animation | `HudState.smooth_xp` (`hud.py:95-120`) — displayed XP eases toward target | VR snaps (`Hud.cs:187`). |
| Ammo "+N" overflow text | shown when clip > bar cap (`hud.py:521-527`) | Missing; and VR clamps bars at 20 while base allows up to 30 (`HUD_AMMO_BAR_LIMIT`, `hud.py:46-47` vs `AmmoBarMax`, `Hud.cs:30`) — clips 21-30 under-display. |
| HUD fade on perk-menu open/close | whole HUD alpha eases (`survival_mode.py:486`) | VR hard-toggles `_hud.Visible` (`Main.cs:302`). |
| Reload indicator | world-space clock gauge from `reload_timer/max` (`overlays.py:53-89`, `draw.py:482-537`) | Missing; data already crosses the ABI (`Sim.cs:118-119`) but is only used for haptics. |
| Aim spread circle | radius scales with `player.spread_heat` (`overlays.py:22-50`) | Missing — **adapt as a reticle spread ring**, not a world overlay (see VR-inapplicable table). **`spread_heat` is not in the ABI at all.** "Custom (VR reticles)" masked both this and the reload gauge. |
| Bonus-HUD slots | 16 slots sliding in/out from screen-left, `ui_indPanel` + icon + timer bar(s), dual-timer 2P variant, compact mode (`bonuses/hud.py`, `hud.py:738-874`) | Missing (needs bonus-HUD state over ABI). |
| Weapon-name popup | on weapon change: panel + icon + name, 1s fade-in/hold/1s fade-out via `aux_timer` (`hud.py:876-936`) | Missing. |
| Quest HUD | sliding top panel, progress panel + green bar, analog clock (pointer = 6°/s), mm:ss text; XP/bonus HUD shifts down 80px (`hud.py:530-634,62`) | Missing (mode not surfaced). |
| Rush/Typo time HUD | clock + "{N} seconds" (`hud.py:692-736`); Typo adds typing box + floating creature name labels (`typo_mode.py:255-271`) | Missing (modes not surfaced). |
| XP threshold source | sim-side | VR **reimplements the Survival curve in the frontend** (`Hud.cs:200-212`) — will drift if sim changes; fine for now, flagged. |
| On-screen banners | `ui_textLevelUp/PickAPerk/LevComp/Quest/Reaper/WellDone`; quest title/timer overlay + level-complete banner have fade/scale timelines (`ui/overlays/quest_run.py:23-70`) | Not used (perk prompt intentionally replaced — see Implemented). |

**Confirmed non-gaps:** base HUD renders numbers with font text, not `ui_num*`
sprites — VR's `Label3D`s are not a fidelity loss; there is **no** low-health
vignette/red-flash in the base game (the heart's faster pulse is the only cue,
and VR reproduces it); `ui/shadow.py` shadows apply to menu panels only, not the
HUD; tutorial has a 9-stage scripted prompt overlay (`tutorial/timeline.py:18-48`)
— mode not surfaced, listed under modes not HUD.

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
| **player shield ring** **[audit]** | additive | **Missing.** Two counter-rotating pulsing `SHIELD_RING` quads while `shield_timer>0` (`trooper.py:198-243`). Needs shield timer over ABI. |
| **player radioactive aura** **[audit]** | additive | **Missing.** Pulsing green `AURA` under Radioactive-perk players (`trooper.py:107-132`). |
| **Sharpshooter laser sight** **[audit]** | additive | **Missing.** Red gradient trail quad 15→512u along aim (`projectiles.py:110-180`), drawn first in the projectile pass. |
| creatures (sprites + tint) | alpha | Yes — energizer tint / lifecycle fade / death staging / hit-flash verified. Creature **shadow** sub-pass (tinted silhouette 1.07×, `creatures.py:55-68`) approximated by a generic soft blob. |
| creature overlays (poison/plague/monster-vision auras) | alpha | Yes (ABI v7) — **[audit]** but fixed-size (90/80/60) regardless of creature `Size`; boss auras don't scale (`Diorama.cs:693-743`). |
| freeze overlay (`draw_freeze_overlay`, per-creature `FREEZE_SHATTER`) | alpha | **Yes** (`Diorama.cs:820-860`) — **[audit]** was implemented but missing from this table. |
| **projectiles / secondaries — per-type draw variants** **[audit]** | additive **+ one subtractive** | **Color-only parity — the biggest open render gap (ground-gen trap, again).** Base dispatches per-type routines (`primary_dispatch.py`, `secondary_dispatch.py`): textured **bullet trails** w/ per-type head color + head sprite (`primary_bullet.py`); **plasma** tail-segment trains + aura, per-weapon configs (`primary_plasma.py`, `projectile_render_registry.py:33-83`); **beam** stepped body + head, **Ion chain arcs** to nearby creatures, Fire-Bullets glow overlay (`primary_beam.py:59-306`); **pulse** distance-scaled expansion; **splitter/blade** sprites w/ blade spin; **Plague Spreader** 5 orbiting "hole" quads in a **custom SRC=ZERO darken blend** (`primary_special.py:16-243`); per-rocket glow style table (`secondary_rocket.py:23-149`); two-quad detonation core+halo (`secondary_detonation.py:12-67`). VR renders every projectile as one tinted additive soft-circle streak; detonations are a single generic orange burst. ABI already ships `type_id`/`angle`/`life_timer`/`vx,vy`. |
| `draw_effect_pool` alpha pass (flags & 0x40): smoke, casings, blood | alpha | Yes |
| `draw_effect_pool` additive pass: ring, flash, shockwave burst | additive | Yes (b5cea3c0) — restored explosion ring/flash/shockwave, enemy hit-sparks, projectile muzzle/impact flashes. |
| `draw_particle_pool` (`state.particles`): additive glows/sparks | additive | Yes (ABI v7) — `RenderGlowPool` big/normal/bubblegun sub-passes; formulae verified. |
| **`draw_sprite_effect_pool`** (`state.sprite_effects`) **[audit]** | alpha | **Missing — a THIRD pool, untracked.** `EXPLOSION_PUFF` quads gated on `fx_detail_2` (`effects.py:118-164`), spawned e.g. by bubblegun hits. **Not in the ABI** (no stream; `crimson_host.h:113-132`). |
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

**Ground generator (tracked gap).** The terrain floor was marked "implemented",
but that masked a fidelity gap: the diorama only **tiles the base terrain slot**
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

- **In-game music trigger**: base **stops** music on entering a run and starts a
  **randomly chosen** game tune (50/50 `gt1_ingame` / `gt2_harppen`, from
  `music/game_tunes.txt`) on the **first projectile hit on a creature**, with
  crossfade (`audio_router.py:100-119`, `music.py:269-290`); suppressed in demo
  and **Rush** (Rush has no in-game music, `audio_router.py:114`). VR hard-starts
  `gt1_ingame` immediately, always. → **Correction:** `gt2_harppen` is NOT
  "wire when mode-select lands" — it's in the already-shipped Survival pool.
- **Music crossfade / exclusive channels**: base fades out old track (0.5/s) and
  fades in new (1.0/s) only after silence (`music.py:229-256,293-344`); VR
  hard-swaps the stream (`AudioBank.cs:238-252`).
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
- **Volume semantics**: base music volume 0 hard-stops playback, up-ramps are
  gradual (`music.py:303-363`); VR maps 0 → -80dB and keeps streaming.
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
2. **Game-mode select** (two-level: modes + quest stage/level select) — the sim
   supports all modes; needs `game_mode`/`quest_level_key` wiring, per-mode
   highscore tables, quest results/failed screens, and **unlock-progression
   persistence** (`quest_unlock_index`) to be meaningful. Typo'Shooter stays
   hidden until its VR input design is settled (see VR-inapplicable table).
3. **Per-projectile-type render variants** (bullet trails, plasma trains, beam
   bodies + ion chains, plague darken pass, per-rocket glow, two-quad
   detonations) — the largest remaining in-combat fidelity delta; ABI already
   carries the needed fields. Add the **sprite-effect pool** stream to the ABI
   while touching it, plus shield ring / radioactive aura / laser sight (need
   small ABI additions: shield timer, perk flags).
4. **HUD behaviors**: enemy health bar (untracked until now), reload gauge (data
   already in ABI), XP roll-up, ammo "+N"/30-bar cap, HUD fade; then bonus-HUD
   slots + weapon-name popup (need bonus-HUD state over the ABI); `spread_heat`
   → ABI for the aim circle.
5. **Audio behaviors**: first-hit random game-tune + crossfade + `gt2_harppen`;
   music-volume-0 stop; reflex-boost pitch (Godot `pitch_scale`); wire "UI Info
   texts" to bonus hover labels or mark it inert.
6. **Statistics** (playtime + weapon usage + DB hub) + **Controls** screens;
   **high-scores browser** (top-100 per-mode tables need persistence beyond the
   current top-10).
7. **Databases** (perk/weapon encyclopedia) — data already in the manifest/sim.
8. Custom VR boot with the `intro` track (publisher logos + attract mode are
   skipped per the VR-inapplicable table); per-mode music; pause world-fade;
   optional cosmetic polish: panel-slide timelines + brief world-fade
   transitions.
9. Credits (+ AlienZooKeeper secret, if we're feeling faithful).

**Lesson (updated):** a feature/asset parity check misses render-pipeline and
*behavioral* correctness. Any base-game `begin_blend_mode` / draw pass / animated
or randomized behavior should be explicitly matched in the diorama, and every
"Yes/Done" in this doc should say *at what fidelity*. The 2026-07-09 audit found
the previous doc's gaps were exactly of this class: ground generator, per-type
projectile draws, first-hit random music, HUD behaviors, and the missing
game-over screen were all hidden behind feature-level checkmarks.
