/*
 * crimson_host — C ABI for embedding the Crimsonland deterministic runtime.
 *
 * This header is the source-of-truth contract between libcrimson
 * (crimson-zig/src/host_abi/) and external frontends (crimson-vr Godot app).
 * Keep it in sync with exports.zig and bump CRIMSON_HOST_ABI_VERSION on any
 * layout or semantic change (layouts are append-only within a version).
 *
 * All payload structs use only 4-byte fields: no padding, identical layout on
 * x86_64 and aarch64, little-endian.
 *
 * Threading: the library is not thread-safe; call from a single thread (or
 * externally synchronize). Sessions are cheap to tick, expensive to create.
 */

#ifndef CRIMSON_HOST_H
#define CRIMSON_HOST_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CRIMSON_HOST_ABI_VERSION 2u
#define CRIMSON_HOST_SNAPSHOT_MAGIC 0x31525643u /* "CVR1" */

/* Return codes */
#define CRIMSON_HOST_OK 0
#define CRIMSON_HOST_E_GENERIC (-1)
#define CRIMSON_HOST_E_INVALID_HANDLE (-2)
#define CRIMSON_HOST_E_BUFFER_TOO_SMALL (-3)
#define CRIMSON_HOST_E_INVALID_CONFIG (-4)
#define CRIMSON_HOST_E_OUT_OF_SESSIONS (-5)
#define CRIMSON_HOST_E_INVALID_INPUT (-6)

/* CrimsonHostInput.flags bits */
#define CRIMSON_HOST_INPUT_FIRE_DOWN (1u << 0)
#define CRIMSON_HOST_INPUT_FIRE_PRESSED (1u << 1)
#define CRIMSON_HOST_INPUT_RELOAD_PRESSED (1u << 2)
#define CRIMSON_HOST_INPUT_RELOAD_DOWN (1u << 3)
#define CRIMSON_HOST_INPUT_MOVE_TO_CURSOR (1u << 4)

/* Audio header flags bits */
#define CRIMSON_HOST_AUDIO_PERK_MENU_OPENED (1u << 0)
#define CRIMSON_HOST_AUDIO_TRIGGER_GAME_TUNE (1u << 1)
#define CRIMSON_HOST_AUDIO_QUEST_HIT_SFX (1u << 2)
#define CRIMSON_HOST_AUDIO_QUEST_COMPLETION_MUSIC (1u << 3)

/* game_mode values (config json + SnapshotHeader.game_mode) */
#define CRIMSON_HOST_MODE_SURVIVAL 1
#define CRIMSON_HOST_MODE_RUSH 2
#define CRIMSON_HOST_MODE_QUESTS 3
#define CRIMSON_HOST_MODE_TYPO 4
#define CRIMSON_HOST_MODE_TUTORIAL 8

/* move_mode values (MovementControlType; -1 = unset) */
#define CRIMSON_HOST_MOVE_RELATIVE 1
#define CRIMSON_HOST_MOVE_STATIC 2
#define CRIMSON_HOST_MOVE_DUAL_ACTION_PAD 3
#define CRIMSON_HOST_MOVE_MOUSE_POINT_CLICK 4

/* aim_scheme values (AimScheme; -1 = unset) */
#define CRIMSON_HOST_AIM_MOUSE 0
#define CRIMSON_HOST_AIM_JOYSTICK 2

/* Per-player input for one 60 Hz tick.
 * move_x/move_y: the analog move DIRECTION vector (typically unit length, or
 *   0 to stand still) -- the runtime consumes it directly as the move axis.
 *   It is NOT an absolute target point: the frontend does the point->direction
 *   conversion itself (dir = normalize(target - player), zeroed within a small
 *   stop radius), mirroring the desktop point-click path in local_input.zig.
 *   The ABI passes move_x/move_y straight through and does no such conversion.
 * aim_x/aim_y: game-world aim point (absolute).
 * flags MOVE_TO_CURSOR: a passive intent marker recorded for replays; it does
 *   NOT itself drive movement through this ABI (movement is purely move_x/y). */
typedef struct crimson_host_input {
    float move_x;
    float move_y;
    float aim_x;
    float aim_y;
    uint32_t flags;
    int32_t move_mode;         /* -1 = keep session default */
    int32_t aim_scheme;        /* -1 = keep session default */
    int32_t perk_choice_index; /* -1 = not picking */
    uint32_t perk_menu_active; /* nonzero pauses sim while perks pending */
} crimson_host_input;

typedef struct crimson_host_tick_result {
    uint32_t ticks_advanced;
    uint32_t paused_for_perk_pick;
    uint32_t all_players_dead;
    int32_t perk_pending_count;
    float player_health;
    int32_t player_level;
    int32_t player_experience;
    int32_t player_weapon_id;
    uint32_t creature_active_count;
    uint32_t bonus_active_count;
    int32_t shots_fired;
    int32_t shots_hit;
    uint32_t elapsed_ms_sim_lo; /* low/high halves of an int64 ms counter */
    uint32_t elapsed_ms_sim_hi;
} crimson_host_tick_result;

/* Snapshot payload layout (packed, in order):
 *   crimson_host_snapshot_header
 *   crimson_host_player_snap    [player_count]
 *   crimson_host_creature_snap  [creature_count]
 *   crimson_host_projectile_snap[projectile_count]
 *   crimson_host_secondary_snap [secondary_count]
 *   crimson_host_bonus_snap     [bonus_count]
 *   crimson_host_particle_snap  [particle_count]   (ABI v2+)
 */
typedef struct crimson_host_snapshot_header {
    uint32_t magic;   /* CRIMSON_HOST_SNAPSHOT_MAGIC */
    uint32_t version; /* CRIMSON_HOST_ABI_VERSION */
    uint32_t tick_lo;
    uint32_t tick_hi;
    int32_t game_mode;
    float world_size;
    float elapsed_ms_sim;
    int32_t perk_pending_count;
    uint32_t perk_choice_count; /* 0..7 */
    int32_t perk_choices[7];    /* PerkId values */
    uint32_t player_count;
    uint32_t creature_count;
    uint32_t projectile_count;
    uint32_t secondary_count;
    uint32_t bonus_count;
    uint32_t particle_count; /* ABI v2+; sprite-effect pool (blood/gibs/etc.) */
} crimson_host_snapshot_header;

typedef struct crimson_host_player_snap {
    float x;
    float y;
    float heading;
    float aim_x;
    float aim_y;
    float aim_heading;
    float health;
    float size;
    float muzzle_flash_alpha;
    int32_t weapon_id;
    float ammo;
    int32_t clip_size;
    uint32_t reload_active;
    float reload_timer;
    float reload_timer_max;
    int32_t experience;
    int32_t level;
} crimson_host_player_snap;

typedef struct crimson_host_creature_snap {
    float x;
    float y;
    float heading;
    float size;
    float anim_phase;
    float hp;
    float max_hp;
    float lifecycle_stage; /* 16.0 = alive (native death-timer convention) */
    int32_t type_id;
    uint32_t flags;
} crimson_host_creature_snap;

typedef struct crimson_host_projectile_snap {
    float x;
    float y;
    float angle;
    int32_t type_id;
} crimson_host_projectile_snap;

typedef struct crimson_host_secondary_snap {
    float x;
    float y;
    float angle;
    float detonation_t;
    float detonation_scale;
    int32_t type_id;
} crimson_host_secondary_snap;

typedef struct crimson_host_bonus_snap {
    float x;
    float y;
    float time_left;
    float time_max;
    int32_t bonus_id;
    int32_t amount;
} crimson_host_bonus_snap;

/* One live sprite-effect (blood, gibs, explosions, casings, glows) from the
 * effect pool. effect_id indexes the particles atlas; the render size is
 * half_width/half_height * scale; flags & 0x40 + age >= 0 gate the alpha pass. */
typedef struct crimson_host_particle_snap {
    float x;
    float y;
    float half_width;
    float half_height;
    float scale;
    float rotation;
    float r;
    float g;
    float b;
    float a;
    float age;
    int32_t effect_id;
    int32_t flags;
} crimson_host_particle_snap;

/* Audio payload layout (packed, in order):
 *   crimson_host_audio_header
 *   crimson_host_shot_audio [shot_count]
 *   int32_t reload_weapon_ids [reload_count]
 *   crimson_host_hit_audio  [hit_count]
 *   int32_t sfx_ids         [sfx_count]
 * Events describe the most recent tick only; drain after every tick. */
typedef struct crimson_host_audio_header {
    uint32_t version;
    uint32_t flags;
    uint32_t shot_count;
    uint32_t reload_count;
    uint32_t hit_count;
    uint32_t sfx_count;
} crimson_host_audio_header;

typedef struct crimson_host_shot_audio {
    int32_t weapon_id;
    uint32_t fire_bullets_active;
} crimson_host_shot_audio;

typedef struct crimson_host_hit_audio {
    uint32_t shock_hit;
    int32_t bullet_hit_roll; /* -1 = none */
    int32_t game_tune_roll;  /* -1 = none */
    uint32_t trigger_game_tune;
} crimson_host_hit_audio;

/* ---- Functions ---- */

uint32_t crimson_host_abi_version(void);

/* Copies the last error message (not NUL-terminated) into buf.
 * Returns copied length, 0 if no error, or negative required length. */
int32_t crimson_host_last_error(uint8_t *buf, uint32_t len);

/* config_json (UTF-8, may be NULL for defaults) accepts:
 *   { "seed": 1, "game_mode": 1, "quest_level_key": 101, "player_count": 1,
 *     "world_size": 1024.0, "tick_rate": 60, "detail_preset": 5,
 *     "gore_disabled": 0, "hardcore": false, "preserve_bugs": false,
 *     "demo_mode_active": false, "status_quest_unlock_index": 0 }
 * Unknown fields are ignored. */
int32_t crimson_host_session_create(const uint8_t *config_json,
                                    uint32_t config_len,
                                    uint64_t *out_handle);

void crimson_host_session_destroy(uint64_t handle);

/* Advances exactly one fixed tick (1/tick_rate seconds).
 * inputs must contain one entry per player. out_result may be NULL. */
int32_t crimson_host_session_tick(uint64_t handle,
                                  const crimson_host_input *inputs,
                                  uint32_t input_count,
                                  crimson_host_tick_result *out_result);

/* Worst-case snapshot size; allocate once and reuse. */
uint32_t crimson_host_snapshot_max_size(void);

/* buf == NULL: writes required size to *len and returns OK.
 * Otherwise writes the payload and updates *len to the written size. */
int32_t crimson_host_snapshot(uint64_t handle, uint8_t *buf, uint32_t *len);

/* Same buffer protocol as crimson_host_snapshot. */
int32_t crimson_host_audio_events(uint64_t handle, uint8_t *buf, uint32_t *len);

/* Runs the native replay verifier on .crd bytes; writes its JSON report.
 * Same buffer protocol as crimson_host_snapshot. */
int32_t crimson_host_verify_replay_json(const uint8_t *replay,
                                        uint32_t replay_len,
                                        uint8_t *out,
                                        uint32_t *out_len);

#ifdef __cplusplus
}
#endif

#endif /* CRIMSON_HOST_H */
