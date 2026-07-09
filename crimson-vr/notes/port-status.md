# CrimsonVR port status — implemented vs. remaining

A map of what the VR frontend does today, what the embedded sim already provides,
and what's still missing to reach faithful parity with the base game. The
**sim** (`crimson-zig`, embedded as `crimson_host`) runs the full game; the VR
work is almost entirely the **presentation + interaction** layer, so most gaps
below are "not surfaced in VR yet", not "not simulated".

_Last updated: 2026-07-09 (render-correctness pass: creature auras, particle-glow
pool, faithful HUD, UI click SFX, perk-select fade)._

## Implemented in VR

**Flow / screens**
- Boot straight into a **main menu** (custom, using the original `ui_signCrimson`
  logo + `ui_menuItem` neon-bar plates): Play Game, Options, Statistics (inert),
  Quit.
- **Survival gameplay** rendered as the tabletop diorama.
- **Pause** menu (flat toggle + Resume / Settings / Quit). Quit returns to the
  main menu; the main menu's Quit exits the app.
- **Options** screen mirroring the base game (segmented `ui_rectOn/Off` sliders:
  Sound / Music / Graphics detail; `ui_checkOn/Off` "UI Info texts") + a **VR
  Settings** submenu (movement hand, dead zone, debug overlays).
- **Level-up / perk pick**: level-up button + accumulated counter + `ui_levelUp`
  sound; poke to open cards; per-card "?" press-and-hold description popup.
- **Highscore entry** (in-VR virtual keyboard) on death.
- **Validation checklist** (dev tool) standing to the right of the arena.

**Presentation (diorama)**
- Creatures (animated sheets, per-type tint, energizer/freeze/hit-flash, death →
  ground + corpse stamp), projectiles + secondaries (glow streaks), bonuses/
  powerup icons, blood/scorch decals, muzzle flash, explosions (see gaps),
  drop-shadows, terrain floor + surrounding world floor, grey-fog skybox.
- **MR passthrough** path (Quest 3 ALPHA_BLEND) — built but **opt-in / off** by
  default (see [[crimson-vr-m4-ui]]).

**Systems**
- Audio: SFX routed from the sim's per-tick audio events; menu music
  (`crimson_theme`) + in-game (`gt1_ingame`); level-up cue.
- Haptics (fire / damage / reload). Persisted settings + local highscores.

## Provided by the sim already (just needs surfacing)

The embedded sim simulates the **whole game**, so these need only VR UI/render:
- **All game modes** — `HostSessionConfig.game_mode` (+ `quest_level_key`) selects
  Survival / Quest / Rush / Typo'Shooter / Tutorial. VR currently **hardcodes
  Survival** (`game_mode:1`).
- All **weapons**, **58 perks**, **~17 bonuses**, creature types, terrain gen.

## Not yet in VR (exists in the base game)

| Area | Base-game screen(s) | Status in VR |
|---|---|---|
| **Game-mode select** | `play_game.py` ("1 player" + Quests/Rush/Survival/Typo/Tutorial) | Missing — Survival hardcoded |
| **Statistics** | `stats.py` | Menu item present but inert |
| **Controls** | `controls.py` (+ `ui_textControls`) | Missing |
| **Databases** (encyclopedia) | `databases_perks.py`, `databases_weapons.py` | Missing |
| **Credits** | `credits.py` | Missing |
| **Mods** | `mods.py` | Missing |
| **Network / co-op** | `network_lobby.py`, `network_session.py` | Missing (out of scope for v1) |
| ~~**Faithful HUD**~~ | `ui_gameTop`, `ui_ind*`, `ui_lifeHeart`, `ui_wicons` | **Done** (ABI v8) — real HUD art; `ui_num*`/`ui_iconAim` still unused |
| **On-screen banners** | `ui_textLevelUp`, `ui_textPickAPerk`, `ui_textLevComp`, `ui_textQuest`, `ui_textReaper`, `ui_textWellDone` | Not used |
| First-run seated **calibration** + **arena scale** UI | (VR-specific) | Deferred slices |
| Native **.crd replay recorder** | (VR-specific) | Deferred (`notes/replay-recording-plan.md`) |

## Unused assets currently staged/available

**Music (staged, not played):** `crimsonquest` (quest/demo theme),
`gt2_harppen` (alt in-game), `intro` (boot theme), `shortie_monk`.
→ Wire when mode-select / boot sequence land.

**UI SFX:** `UI_BUTTONCLICK` (poke buttons), `UI_TYPECLICK`, `UI_TYPEENTER` (VR
keyboard), and `UI_LEVELUP` are now wired. Still unused: `UI_PANELCLICK`,
`UI_CLINK`, `UI_BONUS`.

**UI textures in the source assets but NOT staged/used:**
- HUD: `ui_gameTop`, `ui_indPanel`, `ui_indBullet/Electric/Fire/Life/Rocket`,
  `ui_num1..5`, `ui_lifeHeart`, `ui_wicons`, `ui_iconAim`.
- Banners: `ui_textLevelUp`, `ui_textPickAPerk`, `ui_textLevComp`, `ui_textQuest`,
  `ui_textReaper`, `ui_textWellDone`, `ui_textControls`.
- Controls/widgets: `ui_button_64/82/128/145x32`, `ui_dropDownOn/Off`, `ui_arrow`,
  `ui_clockTable`, `ui_clockPointer`.
- `ui_menuPanel` is staged but currently unused (dropped the full-screen panel).

## Render-pipeline parity (draw-pass audit) — NEW

The additive-pass bug (b5cea3c0) showed our earlier parity check was *feature/asset*
level (what screens/textures/sounds are missing) and did **not** verify that each
of the base game's **draw passes / blend modes** is reproduced. That's a different
audit. Mapping `src/crimson/render/world/` to the diorama:

| Base-game pass (`render/world`) | Blend | In VR? |
|---|---|---|
| ground / decals / corpses / shadows | alpha | Partial — decals/corpses/shadows yes; **ground only tiles the base slot**, not the seeded base+overlay+detail composite (see below) |
| creatures (sprites + tint) | alpha | Yes |
| **creature overlays** (`draw_creature_overlays`): monster-vision aura, plague/poison auras | alpha | **Yes now** (ABI v7) — poison/plague/monster-vision auras drawn |
| projectiles / secondaries (glow streaks) | additive | Yes |
| `draw_effect_pool` **alpha** pass (flags & 0x40): smoke, casings, blood | alpha | Yes |
| `draw_effect_pool` **additive** pass (else): ring, flash, shockwave burst | additive | **Yes now** (b5cea3c0) — was dropped |
| **`draw_particle_pool`** (`state.particles`, separate pool): additive glows/sparks | additive | **Yes now** (ABI v7) — glow stream exported + rendered |
| bonus pickups + hover labels | alpha | Partial (icons yes, hover labels no) |
| aim indicators / gauges / clock | alpha | Custom (VR reticles) |
| HUD | alpha | **Faithful now** (ABI v8) — game HUD art (top bar/heart/health/weapon icon/ammo bars/XP panel) |

**What the additive-pass fix actually restored** (all were dark before — only the
alpha smoke/blood/casings showed): explosion ring + bright flash + shockwave burst
(nuke, rockets, grenades, barrels), **enemy hit-sparks** (`BURST`/`RING` on creature
damage), and **projectile muzzle/impact flashes** (gauss/plasma/rocket `RING`+`BURST`
in `projectiles/effects.py`). i.e. the whole "bright, glowy" combat-feedback layer.

**Two render gaps this audit surfaced — both now CLOSED (ABI v7):**
1. ~~**`state.particles` pool**~~ — DONE. A `ParticleGlowSnap` stream (+ header
   `glow_count`) is packed after the effect pool and rendered additively in
   `RenderGlowPool` (big/normal/bubblegun sub-passes matching `draw_particle_pool`).
2. ~~**Creature overlays**~~ — DONE. `RenderCreatureOverlays` draws the AURA-frame
   poison (red, `flags & 0x01`), plague (black, wire bit `0x80000000`), and
   monster-vision (yellow, header flag) auras with the lifecycle fade.

**Lesson:** a feature/asset parity check misses render-pipeline correctness. Any
base-game `begin_blend_mode` / draw pass should be explicitly matched in the diorama.

**Ground generator (NEW gap — same trap).** The terrain floor was marked "implemented"
(above + the table), but that masked a fidelity gap: the diorama only **tiles the
base terrain slot** (`ter_qN_base`, Nearest-filtered for crispness), whereas the
base game builds the ground with grim's **seeded generator** compositing all three
slots — base + overlay (`tex1`) + detail — into a 1024² bitmap with the dirt/grass
patches placed by `terrain_seed` (`world/render_resources.py`
`schedule_ground_generation`, `terrain_slots.resolve_terrain_slots`). The ABI already
ships the 3 slot ids + seed (`crimson_host_terrain_info`, v3); reproducing the patch
placement needs a port of the grim ground-gen algorithm (bake a composite texture per
seed at load, or a runtime detail-blend approximation). Until then the arena floor
reads flatter than the original. Not yet scheduled.

## Known render gaps / polish backlog (from in-headset testing)

- **Nuke** blast visual ~½ the effective radius (particle-scale work).
- ~~Perk-select fade~~ — DONE (each candidate set eases in over ~220ms; VrButton.SetFade).
- ~~HUD is non-faithful~~ — DONE (faithful HUD art, ABI v8; first-pass layout, tune in-headset).
- ~~Poke buttons + keyboard silent~~ — DONE (VrButton.OnAnyPress → ui_buttonClick /
  keyboard ui_typeClick / ui_typeEnter).

## Suggested next faithful-parity steps (rough order)

Render-correctness + HUD + UI-SFX + perk-fade are DONE (this pass). Remaining:

1. **Game-mode select** screen (Play Game submenu) — highest gameplay value; the
   sim already supports all modes, so it's UI + wiring `game_mode`/`quest_level_key`.
   (Would also light up the HUD's quest/rush/typo timer + quest panels, already in
   `hud.py` but VR renders only the Survival subset today.)
2. **Statistics** + **Controls** screens.
3. **Databases** (perk/weapon encyclopedia) — data already in the manifest/sim.
4. Boot/attract sequence with `intro` music; per-mode music (`crimsonquest`, etc.).
5. HUD follow-ups: bonus-HUD slots + weapon-name popup (need bonus-HUD state over
   the ABI); the aim-indicator `ui_iconAim`; nuke blast-radius scale.
