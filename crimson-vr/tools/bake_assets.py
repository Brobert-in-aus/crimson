"""Bake extracted Crimsonland Classic sprites into the Godot project (M3).

Reads the PNG sheets produced by `crimson extract <game_dir> artifacts/assets`
and stages the ones the VR frontend renders into
`crimson-vr/godot/assets/sprites/` (gitignored — original art is never
committed, PLAN §10), alongside a JSON manifest describing how each entity type
maps to a sheet + frame. The manifest holds only factual layout metadata
(sheet name, grid, frame index, pivot), not asset-derived art.

Sheets are 8x8 grids (creature_render_type, src/crimson/creatures/anim.py). The
manifest carries each creature's animation layout (base_frame, mirror); the
renderer selects the live frame per instance from the snapshot's anim_phase +
flags (CreatureAnim.SelectFrame, slice 2). Orientation comes from the entity
heading, applied by the renderer. The player is still a single static torso
frame (leg animation needs a move-phase ABI field; deferred).

Usage:
    uv run crimson-vr/tools/bake_assets.py [assets_dir] [out_dir]
      assets_dir default: artifacts/assets
      out_dir     default: crimson-vr/godot/assets/sprites
"""

from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

# Repo root (…/crimson) so `import src.crimson…` works when the audio bake reads
# the reference sfx/weapon tables regardless of the caller's cwd.
_REPO_ROOT = Path(__file__).resolve().parents[2]
if str(_REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(_REPO_ROOT))

# Creature type_id -> sheet (src/crimson/creatures/spawn_ids.py CreatureTypeId).
CREATURE_SHEETS: dict[int, str] = {
    0: "zombie.png",
    1: "lizard.png",
    2: "alien.png",
    3: "spider_sp1.png",
    4: "spider_sp2.png",
    5: "trooper.png",
}
CREATURE_GRID = 8  # 8x8 atlas (creature_render_type)

# Per-type animation layout (src/crimson/sim/world_defs.py CREATURE_ANIM):
# base frame index + whether the long-strip walk cycle mirror-folds. The live
# anim_phase + runtime flags (both in the ABI snapshot) pick the actual frame at
# render time via CreatureAnim.SelectFrame (a port of creature_anim_select_frame).
# anim_rate is a sim-side quantity (it advances anim_phase) and is not needed by
# the frontend. type_id -> (base_frame, mirror).
CREATURE_ANIM: dict[int, tuple[int, bool]] = {
    0: (0x20, False),  # zombie
    1: (0x10, True),   # lizard
    2: (0x20, False),  # alien
    3: (0x10, True),   # spider_sp1
    4: (0x10, True),   # spider_sp2
    5: (0x00, False),  # trooper
}

# Draw-order priority per type (higher = drawn on top), matching the original's
# fixed creature draw order (_NATIVE_CREATURE_SPRITE_DRAW_ORDER, bottom->top):
# ZOMBIE, SPIDER_SP1, SPIDER_SP2, ALIEN, LIZARD. The game does no per-instance
# Y-sort; within a type it's pool order. Trooper isn't in the native list, so it
# gets a mid value.
CREATURE_PRIORITY: dict[int, int] = {
    0: 6,   # zombie (bottom)
    3: 7,   # spider_sp1
    4: 8,   # spider_sp2
    5: 9,   # trooper (not in native pass)
    2: 9,   # alien
    1: 10,  # lizard (top)
}
PLAYER_PRIORITY = 15  # above creatures, below projectiles/bonuses (native pass order)

# Corpse decals (fx_queue_rotated) are drawn from bodyset.png, a 4x4 grid
# (grim.terrain_render._corpse_src: cell = 0.25 * dim). creature_type_id ->
# bodyset frame (creature_corpse_frame_for_type / _CREATURE_CORPSE_FRAMES);
# type 7 is the ping-pong-strip corpse fallback. Unknown types fall back to
# (type_id & 0xF) in the renderer. Corpses are baked into the ground in the
# original, so they draw UNDER the living sprites/shadows.
BODYSET_GRID = 4
CORPSE_FRAMES: dict[int, int] = {
    0: 0,  # zombie
    1: 3,  # lizard
    2: 4,  # alien
    3: 1,  # spider_sp1
    4: 2,  # spider_sp2
    5: 7,  # trooper
    7: 6,  # ping-pong strip corpse fallback
}
CORPSE_PRIORITY = -2  # under blood decals? no: over blood (-3), under shadows (1)

# Bonus pickups draw from bonuses.png, a 4x4 grid (render/world/bonuses.py
# bonus_icon_src). bonus_id -> icon frame (src/crimson/bonuses/ids.py BONUS_TABLE
# icon_id). Frame 0 is the bubble/container; WEAPON (id 3, icon_id -1) draws a
# weapon icon from a different sheet in the original - we fall back to the bubble.
BONUS_GRID = 4
BONUS_ICONS: dict[int, int] = {
    1: 12,   # POINTS (13 when amount == 1000; handled in the renderer)
    2: 10,   # ENERGIZER
    4: 7,    # WEAPON_POWER_UP
    5: 1,    # NUKE
    6: 4,    # DOUBLE_EXPERIENCE
    7: 3,    # SHOCK_CHAIN
    8: 2,    # FIREBLAST
    9: 5,    # REFLEX_BOOST
    10: 6,   # SHIELD
    11: 8,   # FREEZE
    12: 14,  # MEDIKIT
    13: 9,   # SPEED
    14: 11,  # FIRE_BULLETS
}
BONUS_PRIORITY = 25  # matches the old colored-quad bonus layer (over the world)

# Sheets to stage: (source-relative-path, dest-name).
GAME = "crimson/game"
TER = "crimson/ter"
UI = "crimson/ui"
LOAD = "crimson/load"  # boot-time singles (bullet head sprite, trail gradient)

# Terrain slot index -> ground sheet (src/crimson/terrain_slots.py
# _TEXTURE_ID_BY_TERRAIN_SLOT). Even slots are the quadrant BASE texture, odd are
# its overlay (tex1). The ABI terrain-info slots[0] picks the base; survival
# default is {0,1,0} = ter_q1_base. Slots advance by quest-unlock (q2/q3/q4).
TERRAIN_SLOT_FILES: dict[int, str] = {
    0: "ter_q1_base.png",
    1: "ter_q1_tex1.png",
    2: "ter_q2_base.png",
    3: "ter_q2_tex1.png",
    4: "ter_q3_base.png",
    5: "ter_q3_tex1.png",
    6: "ter_q4_base.png",
    7: "ter_q4_tex1.png",
}


def effect_atlas_table(particles_size: list[int] | None) -> dict[str, dict] | None:
    """effect_id -> {uv_off:[x,y], uv_scale} for the particles.png sprite-effect
    atlas (EFFECT_ID_ATLAS_TABLE). Each effect_id occupies one cell of a
    per-effect grid (size_code); we precompute the UV rect so the renderer just
    looks it up by the ABI ParticleSnap.effect_id and feeds it as per-instance UV.

    The rect matches the native effect pool EXACTLY: origin at the cell corner,
    size = cell - 2px, i.e. the 2px clamp trims the RIGHT/BOTTOM edges only
    (grim draws Rectangle(x, y, cell_w-2, cell_h-2)). An earlier version inset
    2px on EVERY side, which shifted the sample window and cropped art that runs
    to the cell edge (freeze shards, glow) mid-gradient — that crop was the real
    cause of the "square edge" artifacts, misread as linear-filter bleed."""
    if not particles_size:
        return None
    from src.crimson.effects_atlas import EFFECT_ID_ATLAS_TABLE, SIZE_CODE_GRID

    tex_w = float(particles_size[0])
    clamp = 2.0 / tex_w  # native cell_size-2px clamp (right/bottom only)
    out: dict[str, dict] = {}
    for e in EFFECT_ID_ATLAS_TABLE:
        grid = SIZE_CODE_GRID[e.size_code]
        cell = 1.0 / grid
        col = e.frame % grid
        row = e.frame // grid
        out[str(e.effect_id)] = {
            "uv_off": [col * cell, row * cell],
            "uv_scale": cell - clamp,
        }
    return out


def weapon_table() -> dict[str, dict]:
    """weapon_id -> {name, icon_index} (WEAPON_BY_ID), for the game-over score
    card's most-used-weapon row (icon from ui_wicons, 8x8 grid, frame =
    icon_index*2 spanning two cells — same layout the HUD uses)."""
    from src.crimson.weapons import WEAPON_BY_ID, weapon_display_name

    return {
        str(int(wid)): {
            "name": weapon_display_name(wid, preserve_bugs=False),
            "icon_index": int(meta.icon_index),
        }
        for wid, meta in WEAPON_BY_ID.items()
    }


def perk_names() -> dict[str, str]:
    """perk_id -> display name for the VR perk-menu card labels. Resolved via
    perk_display_name with preserve_bugs=False (the VR session default), so
    original typos the reference fixes by default are fixed here too — e.g.
    'Fire Caugh' -> 'Fire Cough' (an original-game typo, kept only under
    preserve_bugs)."""
    from src.crimson.perks.ids import PERK_BY_ID, perk_display_name

    return {str(int(pid)): perk_display_name(pid, preserve_bugs=False) for pid in PERK_BY_ID}


def perk_descriptions() -> dict[str, str]:
    """perk_id -> description for the VR perk-menu '?' popups (same
    preserve_bugs=False resolution as the names)."""
    from src.crimson.perks.ids import PERK_BY_ID, perk_display_description

    return {str(int(pid)): perk_display_description(pid, preserve_bugs=False) for pid in PERK_BY_ID}


def main() -> None:
    assets_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("artifacts/assets")
    out_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("crimson-vr/godot/assets/sprites")
    out_dir.mkdir(parents=True, exist_ok=True)

    needed = set(CREATURE_SHEETS.values()) | {"bodyset.png", "projs.png", "bonuses.png", "particles.png"}
    staged: dict[str, list[int]] = {}
    for name in sorted(needed):
        src = assets_dir / GAME / name
        if not src.exists():
            print(f"WARN missing sheet: {src}")
            continue
        shutil.copy2(src, out_dir / name)
        # Record pixel size so the manifest is self-describing for the renderer.
        from PIL import Image

        with Image.open(src) as im:
            staged[name] = list(im.size)

    # Projectile-draw singles live under crimson/load/: the bullet head sprite
    # (bullet16) and the additive trail gradient (bulletTrail) used by the
    # per-type projectile renderers (projectile_draw/).
    from PIL import Image

    for name in ("bullet16.png", "bulletTrail.png"):
        src = assets_dir / LOAD / name
        if not src.exists():
            print(f"WARN missing load sheet: {src}")
            continue
        shutil.copy2(src, out_dir / name)
        with Image.open(src) as im:
            staged[name] = list(im.size)

    # Terrain ground sheets live under crimson/ter/ (not crimson/game/).

    for name in sorted(set(TERRAIN_SLOT_FILES.values())):
        src = assets_dir / TER / name
        if not src.exists():
            print(f"WARN missing terrain sheet: {src}")
            continue
        shutil.copy2(src, out_dir / name)
        with Image.open(src) as im:
            staged[name] = list(im.size)

    # UI textures live under crimson/ui/. ui_aim = the aim-hand crosshair,
    # ui_cursor = the move-hand pointer (VR reticles project onto the play plane);
    # ui_signCrimson/ui_menuItem/ui_itemTexts are the original main-menu art the VR
    # main menu reuses (logo, item plate, and the label atlas respectively).
    for name in (
        "ui_aim.png", "ui_cursor.png",
        # Main menu art: logo, item plate, label atlas.
        "ui_signCrimson.png", "ui_menuItem.png", "ui_itemTexts.png",
        # Options screen art: panel bg, segmented-slider cells, checkbox states.
        "ui_menuPanel.png", "ui_rectOn.png", "ui_rectOff.png", "ui_checkOn.png", "ui_checkOff.png",
        # Faithful in-game HUD art (ui/hud.py): top bar, pulsing heart, health bar,
        # info panel, weapon-icon atlas (8x8), and per-class ammo-bar sprites.
        "ui_gameTop.png", "ui_lifeHeart.png", "ui_indLife.png", "ui_indPanel.png", "ui_wicons.png",
        "ui_indBullet.png", "ui_indFire.png", "ui_indRocket.png", "ui_indElectric.png",
        # Game-over / results screen art (screens/results/game_over.py): the
        # Reaper / Well Done banners and the analog game-time gauge.
        "ui_textReaper.png", "ui_textWellDone.png", "ui_clockTable.png", "ui_clockPointer.png",
    ):
        src = assets_dir / UI / name
        if not src.exists():
            print(f"WARN missing ui sheet: {src}")
            continue
        shutil.copy2(src, out_dir / name)
        with Image.open(src) as im:
            staged[name] = list(im.size)

    manifest = {
        "note": "Generated by crimson-vr/tools/bake_assets.py from user-supplied "
        "Crimsonland Classic assets. Layout metadata only; no art is committed.",
        "sheets": {
            name: {"size": size, "grid": CREATURE_GRID if name in CREATURE_SHEETS.values() else None}
            for name, size in staged.items()
        },
        # offset_deg corrects each sheet's baked art facing relative to the sim
        # heading. Creature sheets are drawn 90 deg CW of the trooper torso, so
        # rotate them -90 (validated in-headset against the debug needle).
        "creatures": {
            str(type_id): {
                "sheet": sheet, "grid": CREATURE_GRID,
                # frame is the static fallback (used only if animation is off or
                # the layer draws colored quads); base_frame + mirror drive the
                # animated per-instance frame selection.
                "frame": CREATURE_ANIM.get(type_id, (0, False))[0],
                "base_frame": CREATURE_ANIM.get(type_id, (0, False))[0],
                "mirror": CREATURE_ANIM.get(type_id, (0, False))[1],
                "pivot": [0.5, 0.5], "offset_deg": -90.0,
                "priority": CREATURE_PRIORITY.get(type_id, 8),
            }
            for type_id, sheet in CREATURE_SHEETS.items()
            if sheet in staged
        },
        # Player is drawn from trooper.png (8x8), same sheet as the trooper
        # creature (src/crimson/render/world/trooper.py). torso_frame = leg_frame
        # + 16; static torso pose is frame 16, rotated by aim direction. (bodyset
        # is corpse decals, not the living player.)
        "player": {
            "sheet": "trooper.png", "grid": 8, "frame": 16, "pivot": [0.5, 0.5],
            "offset_deg": 0.0, "priority": PLAYER_PRIORITY,
        }
        if "trooper.png" in staged
        else None,
        # Sprite-effect atlas (particles.png): effect_id -> the UV cell to sample.
        # Each effect uses its own grid (size_code), so precompute per-effect
        # uv_off/uv_scale here (with the native 2px cell inset) for the renderer's
        # per-instance UV. Keyed off ABI ParticleSnap.effect_id.
        "effects": effect_atlas_table(staged.get("particles.png")),
        "effects_sheet": "particles.png" if "particles.png" in staged else None,
        # Terrain blood/scorch splats (ABI v3 terrain-fx decals) reuse the SAME
        # particles.png atlas via effect_id -> the "effects" UV table above; no
        # separate mapping needed. Corpse stamps (ABI v3 terrain-fx corpses) use
        # bodyset.png: creature_type_id -> a 4x4-grid frame (with an &0xF fallback
        # applied by the renderer for unmapped types).
        "corpses": {
            "sheet": "bodyset.png", "grid": BODYSET_GRID,
            "frames": {str(t): f for t, f in CORPSE_FRAMES.items()},
            "priority": CORPSE_PRIORITY,
        }
        if "bodyset.png" in staged
        else None,
        # Bonus pickups: bonus_id -> icon frame in bonuses.png (4x4 grid). The
        # renderer applies the POINTS@1000 +1 and the WEAPON bubble fallback.
        "bonuses": {
            "sheet": "bonuses.png", "grid": BONUS_GRID,
            "icons": {str(b): i for b, i in BONUS_ICONS.items()},
            "priority": BONUS_PRIORITY,
        }
        if "bonuses.png" in staged
        else None,
        # Terrain ground sheets: slot index -> filename. The frontend picks the
        # base slot (ABI terrain-info slots[0]) to texture the arena floor.
        "terrain": {
            "slots": {str(i): fn for i, fn in TERRAIN_SLOT_FILES.items() if fn in staged},
        }
        if any(fn in staged for fn in TERRAIN_SLOT_FILES.values())
        else None,
        # Perk id -> display name (src/crimson/perks/ids.py PERK_BY_ID) for the VR
        # perk-menu cards. Factual metadata only; no art. The ABI snapshot header
        # sends perk_choices[] as PerkId values, which the frontend labels from this.
        "perks": perk_names(),
        # Perk id -> description for the VR perk-menu '?' press-and-hold popups.
        "perk_descriptions": perk_descriptions(),
        # Weapon id -> display name + ui_wicons icon index, for the game-over
        # score card's most-used-weapon row (ABI v10 most_used_weapon_id).
        "weapons": weapon_table(),
    }

    manifest_path = out_dir / "sprite_manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2))
    print(f"staged {len(staged)} sheets -> {out_dir}")
    print(f"wrote {manifest_path}")

    # Audio lives beside the sprites (…/assets/audio) unless out_dir was given.
    audio_out = out_dir.parent / "audio" if out_dir.name == "sprites" else out_dir / "audio"
    stage_audio(assets_dir, audio_out)


def stage_audio(assets_dir: Path, out_dir: Path) -> None:
    """Stage the sfx Oggs + an audio manifest the frontend uses to route the
    host-ABI audio events (crimson_host_audio_events) to sounds.

    The ABI emits sfx events as `@intFromEnum(SfxId)`; the Zig SfxId enum order
    is identical to the Python SFX_NATIVE_ORDER (both derived from
    audio_init_sfx / sfx_load_sample load order), so a sound's native id is its
    index in that tuple. Shot/reload events carry a weapon_id, which we resolve
    to a fire/reload native id via the weapons table. No art/audio bytes are
    committed — only this factual routing metadata (PLAN §10).
    """
    from src.crimson.weapons import WEAPON_BY_ID, WeaponId
    from src.grim.sfx_map import SFX_NATIVE_ORDER, SFX_SPECS, SfxId

    out_dir.mkdir(parents=True, exist_ok=True)
    # native id (index) -> ogg filename, in enum order (index == ABI sfx id).
    sfx_files = [SFX_SPECS[sid].entry_name for sid in SFX_NATIVE_ORDER]
    idx = {sid.value: i for i, sid in enumerate(SFX_NATIVE_ORDER)}

    staged = 0
    for name in sorted(set(sfx_files)):
        src = assets_dir / "sfx" / name
        if not src.exists():
            print(f"WARN missing sfx: {src}")
            continue
        shutil.copy2(src, out_dir / name)
        staged += 1

    # Music: stage the original tracks (menu theme + in-game) so the frontend can
    # play them like the base game. track name (no ext) -> ogg filename.
    music_tracks = (
        "crimson_theme", "crimsonquest", "gt1_ingame", "gt2_harppen", "intro", "shortie_monk",
    )
    music = {}
    music_dir = out_dir / "music"
    music_dir.mkdir(parents=True, exist_ok=True)
    for track in music_tracks:
        src = assets_dir / "music" / f"{track}.ogg"
        if not src.exists():
            print(f"WARN missing music: {src}")
            continue
        shutil.copy2(src, music_dir / f"{track}.ogg")
        music[track] = f"music/{track}.ogg"

    manifest = {
        "note": "Generated by crimson-vr/tools/bake_assets.py from user-supplied "
        "Crimsonland Classic assets. Routing metadata only; no audio is committed.",
        # index = native sfx id (ABI @intFromEnum(SfxId)); value = ogg filename.
        "sfx": sfx_files,
        # weapon_id -> native sfx id for its fire / reload sound (weapons table).
        "weapon_fire": {str(int(w)): idx[WEAPON_BY_ID[w].fire_sound.value] for w in WEAPON_BY_ID},
        "weapon_reload": {str(int(w)): idx[WEAPON_BY_ID[w].reload_sound.value] for w in WEAPON_BY_ID},
        # Fire-Bullets suppresses the per-weapon shot sfx and plays these two
        # instead (audio_router.handle_player_audio).
        "fire_bullets_weapon_id": int(WeaponId.FIRE_BULLETS),
        "plasma_minigun_weapon_id": int(WeaponId.PLASMA_MINIGUN),
        # Hit sfx: shock hits -> shock_hit; else one of the six bullet-hit
        # samples (the ABI supplies the resolved roll index).
        "bullet_hit": [idx[getattr(SfxId, f"BULLET_HIT_0{n}").value] for n in range(1, 7)],
        "shock_hit": idx[SfxId.SHOCK_HIT_01.value],
        # Music track name -> ogg path (menu theme = crimson_theme, in-game = gt1_ingame).
        "music": music,
    }
    manifest_path = out_dir / "audio_manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2))
    print(f"staged {staged} sfx -> {out_dir}")
    print(f"wrote {manifest_path}")


if __name__ == "__main__":
    main()
