//! C-ABI host exports for embedding the deterministic runtime in external
//! frontends (crimson-vr Godot app and similar hosts).
//!
//! Contract notes:
//! - The canonical C header lives at `crimson-vr/abi/crimson_host.h`; keep the
//!   two in sync and bump `abi_version` on any layout or semantic change.
//! - Snapshot/audio payloads are packed little-endian extern structs with only
//!   4-byte fields, so there is no padding and layout is identical on
//!   x86_64 and aarch64.
//! - Nothing here may influence simulation outcomes except through
//!   `live_runner.FrameInput`.

const std = @import("std");
const crimson_zig = @import("crimson_zig");

const game_ids = crimson_zig.game_ids;
const live_runner = crimson_zig.live_runner;
const state_mod = crimson_zig.state;
const terrain_fx_mod = crimson_zig.terrain_fx;
const verify_native = crimson_zig.verify_native;
const replay_codec = crimson_zig.replay_codec;

// v21: replay_finish reports an empty recording as ok/size-0 instead of an
// error, AND recordings before this carry a corrupt weapon-usage header (a
// slice of dead stack) that desyncs re-simulation part-way through a run. The
// bump exists for the second reason: a stale v20 .so paired with this frontend
// would pass preflight and keep writing replays that cannot verify.
// v22: replay_detach / recording_encode / recording_destroy — lift a capture
// out of its session in O(1) so the encode, whose cost grows with match length,
// can run off the main thread. last_error is thread-local to suit.
pub const abi_version: u32 = 22;
pub const snapshot_magic: u32 = 0x31525643; // "CVR1" little-endian

// Synthetic wire-only bit OR'd into the exported creature flags to signal a
// plague-infected creature (creature.plague_infected is a bool, not a real
// CreatureFlags value). The real flags only use up to 0x400, so bit 31 is free.
// The frontend draws the black plague aura for creatures with this bit set.
const creature_wire_flag_plague: u32 = 0x8000_0000;

pub const ok: i32 = 0;
pub const err_generic: i32 = -1;
pub const err_invalid_handle: i32 = -2;
pub const err_buffer_too_small: i32 = -3;
pub const err_invalid_config: i32 = -4;
pub const err_out_of_sessions: i32 = -5;
pub const err_invalid_input: i32 = -6;

const input_flag_fire_down: u32 = 1 << 0;
const input_flag_fire_pressed: u32 = 1 << 1;
const input_flag_reload_pressed: u32 = 1 << 2;
const input_flag_reload_down: u32 = 1 << 3;
const input_flag_move_to_cursor_pressed: u32 = 1 << 4;

const audio_flag_perk_menu_opened: u32 = 1 << 0;
const audio_flag_trigger_game_tune: u32 = 1 << 1;
const audio_flag_quest_hit_sfx: u32 = 1 << 2;
const audio_flag_quest_completion_music: u32 = 1 << 3;

pub const CrimsonHostInput = extern struct {
    move_x: f32,
    move_y: f32,
    aim_x: f32,
    aim_y: f32,
    flags: u32,
    move_mode: i32, // MovementControlType id, or -1 for "unset"
    aim_scheme: i32, // AimScheme id, or -1 for "unset"
    perk_choice_index: i32, // -1 when not picking
    perk_menu_active: u32,
};

pub const CrimsonHostTickResult = extern struct {
    ticks_advanced: u32,
    paused_for_perk_pick: u32,
    all_players_dead: u32,
    perk_pending_count: i32,
    player_health: f32,
    player_level: i32,
    player_experience: i32,
    player_weapon_id: i32,
    creature_active_count: u32,
    bonus_active_count: u32,
    shots_fired: i32,
    shots_hit: i32,
    elapsed_ms_sim_lo: u32,
    elapsed_ms_sim_hi: u32,
    // Total creatures killed this run (creatures.kill_count) and the weapon the
    // local player fired the most (weapon_shots_fired argmax, current weapon
    // when nothing was fired) — the game-over score card's Frags + most-used
    // weapon row. Append-only (ABI v10).
    creature_kill_count: i32,
    most_used_weapon_id: i32,
    // Nonzero once the current quest's spawn timeline has been cleared
    // (session.quest_completed). Always 0 outside quest mode. The frontend
    // uses it to show quest results + advance the unlock index. Append-only
    // (ABI v14).
    quest_completed: u32,
};

pub const SnapshotHeader = extern struct {
    magic: u32,
    version: u32,
    tick_lo: u32,
    tick_hi: u32,
    game_mode: i32,
    world_size: f32,
    elapsed_ms_sim: f32,
    perk_pending_count: i32,
    perk_choice_count: u32,
    perk_choices: [7]i32,
    player_count: u32,
    creature_count: u32,
    projectile_count: u32,
    secondary_count: u32,
    bonus_count: u32,
    particle_count: u32,
    // Global energizer bonus timer (state.bonuses.energizer). Lets the frontend
    // render the energizer-blue creature tint + lifecycle fade at draw time
    // (draw.py draw_creatures) without a per-creature color field. Read-only
    // presentation data; no runtime/gameplay effect. Append-only (ABI v3).
    energizer_timer: f32,
    // Global freeze bonus timer (state.bonuses.freeze) for the per-creature
    // freeze-shatter overlay (draw_freeze_overlay). Read-only. Append-only (ABI v5).
    freeze_timer: f32,
    // Nonzero while the local player (players[0]) has the Monster Vision perk;
    // draw_creature_overlays then paints a yellow aura over every creature and
    // suppresses the creature drop-shadow. Read-only. Append-only (ABI v7).
    monster_vision: u32,
    // Number of active flame/bubblegun particles (state.particles) packed after
    // the effect pool. This is a SECOND particle system distinct from the effect
    // pool (draw_particle_pool). Append-only (ABI v7).
    glow_count: u32,
    // Number of active sprite effects (session.sprite_effects, the THIRD pool:
    // muzzle puffs / rocket exhaust / explosion smoke) packed after the glow
    // pool. draw_sprite_effect_pool renders every entry as the EXPLOSION_PUFF
    // atlas cell (full cell, NO 2px clamp), plain alpha blend, gated on
    // fx_detail >= 2. Append-only (ABI v13).
    sprite_effect_count: u32,
    // ABI v19 (append-only): the remaining global bonus timers, completing the
    // bonus-HUD slot set (collectHudBonusSpecs) alongside energizer + freeze.
    weapon_power_up_timer: f32,
    reflex_boost_timer: f32,
    double_experience_timer: f32,
};

pub const PlayerSnap = extern struct {
    x: f32,
    y: f32,
    heading: f32,
    aim_x: f32,
    aim_y: f32,
    aim_heading: f32,
    health: f32,
    size: f32,
    muzzle_flash_alpha: f32,
    weapon_id: i32,
    ammo: f32,
    clip_size: i32,
    reload_active: u32,
    reload_timer: f32,
    reload_timer_max: f32,
    experience: i32,
    level: i32,
    // Static per-weapon HUD data for the faithful HUD (ABI v8): the ui_wicons
    // atlas icon index and the ammo-bar class (0 bullet / 1 fire / 2 rocket /
    // 4 electric). Presentation-only. Append-only.
    weapon_icon_index: i32,
    weapon_ammo_class: i32,
    // ABI v11 (append-only), all presentation-only:
    // - spread_heat drives the aim-spread circle (overlays.py), adapted as a
    //   reticle spread ring in VR.
    // - shield_timer > 0 draws the counter-rotating SHIELD_RING pair
    //   (trooper.py:198-243).
    // - perk_flags: bit0 Doctor (target health bar), bit1 Radioactive (green
    //   aura), bit2 Sharpshooter (laser sight).
    spread_heat: f32,
    shield_timer: f32,
    perk_flags: u32,
    // ABI v15 (append-only): the walk-cycle phase driving the trooper LEG
    // frame (trooper.py:156 — leg = clamp(int(move_phase+0.5), 0, 14), torso =
    // leg + 16). Presentation-only.
    move_phase: f32,
    // ABI v17 (append-only): the death-animation countdown (16 -> below 0 at
    // 20/s once health <= 0; hits-while-dead drain an extra 28/s). Drives the
    // corpse frame ramp (trooper.py:276 — frame = clamp(32 + int((16 -
    // death_timer) * 1.25), 32, 52)); the flat game-over transition waits for
    // it to pass 0. Presentation-only.
    death_timer: f32,
    // ABI v19 (append-only): per-player bonus timers for the HUD slot set
    // (fire bullets / speed) and the weapon-pickup aux popup countdown
    // (aux_timer 2 -> 0; the name fades in over [2,1] and out over [1,0]).
    fire_bullets_timer: f32,
    speed_bonus_timer: f32,
    aux_timer: f32,
};

pub const player_perk_flag_doctor: u32 = 1 << 0;
pub const player_perk_flag_radioactive: u32 = 1 << 1;
pub const player_perk_flag_sharpshooter: u32 = 1 << 2;
// ABI v12: Ion Gun Master scales the ion chain-arc reach in the renderer.
pub const player_perk_flag_ion_gun_master: u32 = 1 << 3;

pub const CreatureSnap = extern struct {
    x: f32,
    y: f32,
    heading: f32,
    size: f32,
    anim_phase: f32,
    hp: f32,
    max_hp: f32,
    lifecycle_stage: f32,
    type_id: i32,
    flags: u32,
    // Per-creature tint RGBA multiplier + white hit-flash timer (ABI v4+).
    // Presentation-only; the frontend multiplies the sprite by (r,g,b,a) and
    // brightens toward white while hit_flash_timer > 0.
    r: f32,
    g: f32,
    b: f32,
    a: f32,
    hit_flash_timer: f32,
    // ABI v18: stable presentation identity. Pool slots are reused, so the
    // generation changes on every new occupant.
    pool_index: i32,
    generation: u32,
};

pub const ProjectileSnap = extern struct {
    x: f32,
    y: f32,
    angle: f32,
    type_id: i32,
    // Velocity (ABI v6). NOTE: this is a fixed-magnitude direction (cos,sin)*1.5,
    // NOT effective speed — it does not go to zero when a projectile stops, so use
    // life_timer (below) to detect a stopped/lingering projectile.
    vx: f32,
    vy: f32,
    // Projectile life timer (ABI v9). A projectile with life_timer < 0.4 has hit
    // and entered "linger" mode: it no longer moves (projectiles.zig update). The
    // host uses this to cull bullets the instant they stop.
    life_timer: f32,
    // ABI v12 (append-only), for the per-type draw variants (projectile_draw/):
    // spawn origin (bullet trails, beam bodies, pulse/splitter size), the plasma
    // tail step scale + travel budget (tail segment count), and the POOL slot
    // index (stable per projectile: blade spin / plague orbit phase — the dense
    // snapshot index shifts when others despawn).
    origin_x: f32,
    origin_y: f32,
    speed_scale: f32,
    travel_budget: f32,
    pool_index: i32,
};

pub const SecondarySnap = extern struct {
    x: f32,
    y: f32,
    angle: f32,
    detonation_t: f32,
    detonation_scale: f32,
    type_id: i32,
};

pub const BonusSnap = extern struct {
    x: f32,
    y: f32,
    time_left: f32,
    time_max: f32,
    bonus_id: i32,
    amount: i32,
};

// One live entry of the sprite EffectPool (blood, gibs, explosions, casings,
// glows). Mirrors what draw_effect_pool reads: an effect_id (-> particles atlas
// frame), size (half_width/height * scale), rotation, rgba color, and the
// flags/age liveness gate. Packed after bonuses in the snapshot (append-only).
pub const ParticleSnap = extern struct {
    x: f32,
    y: f32,
    half_width: f32,
    half_height: f32,
    scale: f32,
    rotation: f32,
    r: f32,
    g: f32,
    b: f32,
    a: f32,
    age: f32,
    effect_id: i32,
    flags: i32,
};

// One live entry of the flame/bubblegun ParticlePool (state.particles), a pool
// SEPARATE from the effect pool above. draw_particle_pool renders these
// additively: the normal glow uses effect-atlas frame 12 tinted
// (tint_r, tint_g, tint_b) with alpha = age and radius from intensity; the
// bubblegun style (style_id 8) uses effect frame 2 with a wobble size and white
// tint; a low-alpha large glow (frame 13) is drawn on every other entry. Packed
// after the effect pool in the snapshot (append-only, ABI v7).
pub const ParticleGlowSnap = extern struct {
    x: f32,
    y: f32,
    intensity: f32,
    spin: f32,
    tint_r: f32, // Particle.scale_x
    tint_g: f32, // Particle.scale_y
    tint_b: f32, // Particle.scale_z
    age: f32, // alpha multiplier (0..1)
    style_id: i32,
};

// One live entry of the sprite-effect pool (session.sprite_effects), the THIRD
// effect system (muzzle puffs, rocket exhaust, explosion smoke). Mirrors what
// draw_sprite_effect_pool reads: pos, scale (quad side in world units),
// rotation (radians), and the entry's rgba color. Every entry draws the same
// EXPLOSION_PUFF atlas cell — full cell rect (no 2px clamp, unlike the effect
// pool) — with plain alpha blending. Packed after the glow pool (ABI v13).
pub const SpriteEffectSnap = extern struct {
    x: f32,
    y: f32,
    scale: f32,
    rotation: f32,
    r: f32,
    g: f32,
    b: f32,
    a: f32,
};

pub const AudioHeader = extern struct {
    version: u32,
    flags: u32,
    shot_count: u32,
    reload_count: u32,
    hit_count: u32,
    sfx_count: u32,
};

pub const ShotAudioSnap = extern struct {
    weapon_id: i32,
    fire_bullets_active: u32,
};

pub const HitAudioSnap = extern struct {
    shock_hit: u32,
    bullet_hit_roll: i32, // -1 = none
    game_tune_roll: i32, // -1 = none
    trigger_game_tune: u32,
};

// Static terrain generation info (ABI v3). The base ground is stamped once at
// session start from three atlas slots seeded by terrain_seed; the frontend
// reproduces (or approximates) it from these. Query-once, not per-tick.
pub const TerrainInfo = extern struct {
    terrain_slot_0: i32, // base atlas slot (ter/ sheet index)
    terrain_slot_1: i32, // overlay atlas slot
    terrain_slot_2: i32, // detail atlas slot
    terrain_seed: u32, // rng.state at terrain generation
    terrain_size: i32, // square terrain side, floor(world_size)
    world_size: f32,
};

// Per-tick terrain FX drain (ABI v3), analogous to the audio-events drain.
// Payload layout (packed, in order):
//   TerrainFxHeader
//   TerrainDecalSnap  [decal_count]   ground splats (blood/scorch)
//   TerrainCorpseSnap [corpse_count]  rotated corpse stamps (creature death)
// Describes the most recent tick only; drain after every tick and paint the
// entries into a persistent decal layer (they are one-shot events, not state).
pub const TerrainFxHeader = extern struct {
    version: u32,
    decal_count: u32,
    corpse_count: u32,
};

pub const TerrainDecalSnap = extern struct {
    effect_id: i32, // ter/ decal atlas frame
    x: f32,
    y: f32,
    width: f32,
    height: f32,
    rotation: f32,
    r: f32,
    g: f32,
    b: f32,
    a: f32,
};

pub const TerrainCorpseSnap = extern struct {
    creature_type_id: i32, // bodyset/creature sheet id (7 = ping-pong fallback)
    x: f32, // top-left x
    y: f32, // top-left y
    rotation: f32,
    scale: f32,
    r: f32,
    g: f32,
    b: f32,
    a: f32,
};

const HostSessionConfig = struct {
    seed: u32 = 1,
    game_mode: i32 = 1,
    quest_level_key: i32 = 101,
    player_count: i32 = 1,
    world_size: f32 = 1024.0,
    tick_rate: i32 = 60,
    detail_preset: i32 = 5,
    gore_disabled: i32 = 0,
    hardcore: bool = false,
    preserve_bugs: bool = false,
    demo_mode_active: bool = false,
    status_quest_unlock_index: i32 = 0,
    // ABI v16: the full unlock index advances only on HARDCORE quest
    // completions natively (quests/results.py advance_quest_unlocks) — it
    // gates the Splitter Gun. Distinct from the frontier index; defaults 0.
    status_quest_unlock_index_full: i32 = 0,
    // ABI v16: persisted per-weapon usage counts (save-status parity: index =
    // weapon id, slot 0 unused). The sim increments on every weapon assign and
    // uses nonzero counts for the 50% used-weapon drop reroll
    // (weapon_pick_random_available). Exactly weapon_count_size entries when
    // present; omit for all zeros. Read back via
    // crimson_host_status_weapon_usage after (or during) a run.
    status_weapon_usage_counts: [state_mod.weapon_count_size]u32 =
        [_]u32{0} ** state_mod.weapon_count_size,
    // Debug fx showcase (VR debug menu): each reload press cycles the player
    // to the next real weapon so the whole arsenal can be toured in one run.
    // The visual force-toggles (auras, shield ring, laser, monster vision)
    // live entirely frontend-side now. Debug only — never set for replays or
    // verify.
    debug_fx_showcase: bool = false,
};

const SessionBox = struct {
    generation: u32,
    runner: live_runner.LiveRunner,
    last_audio: live_runner.FrameAudioEvents = .{},
    last_terrain_fx: terrain_fx_mod.TerrainFxBatch = .{},

    // Replay recording. `config` is snapshotted at create because the header
    // must describe the session EXACTLY — the verifier re-simulates from seed +
    // config, so reconstructing it later from the runner would risk a value
    // that has since drifted, and the resulting divergence would surface as a
    // stats mismatch rather than as a config error.
    config: HostSessionConfig = .{},
    recording: bool = false,
    record_ticks: std.ArrayList(RecordedTick) = .empty,
    record_overflow: bool = false,
    record_stats: RecordedStats = .{},
    record_events: std.ArrayList(replay_codec.ReplayEvent) = .empty,
    // Perk-menu edge state. NOT recording-only: the rising edge is what rolls
    // the offer, so it has to be tracked on every session or a recorded run and
    // an unrecorded one would play differently.
    menu_was_active: bool = false,
    // Perk traffic seen but not yet stamped. The menu PAUSES the sim, so the
    // frames carrying a menu-open or a card poke usually advance zero ticks --
    // stamping those with the current tick index can place an event on an index
    // the recording never contains, and the replay runner, which applies events
    // only when it reaches their tick, then silently never applies them. Held
    // here until a frame actually advances, then stamped with the first tick it
    // advances, which is by construction a tick the replay will run.
    record_pending_menu_open: bool = false,
    record_pending_pick: ?i32 = null,
};

/// The seven figures the verifier compares, harvested from the live run.
///
/// finish originally got these by re-simulating the recording and reading the
/// result, which made them correct by construction -- and cost a full re-run of
/// the game on the calling thread. In headset that was a 5.7 second freeze when
/// leaving the score screen. The live run already computes exactly these
/// fields every tick, so the re-sim was buying a guarantee the determinism gate
/// already provides: the slice-8 test feeds a recording built from THESE values
/// to the verifier, so if the live counters ever diverged from a re-simulation
/// that test fails rather than a player's replay silently failing later.
const RecordedStats = struct {
    elapsed_ms_sim: i64 = 0,
    player_experience: i32 = 0,
    creature_kill_count: i32 = 0,
    most_used_weapon_id: i32 = 0,
    shots_fired: i32 = 0,
    shots_hit: i32 = 0,
};

/// One SIM TICK of captured input. Rows are per tick, never per rendered
/// frame: the runner may advance more than one tick in a frame under load, and
/// a replay with one row per frame silently desynchronises from the run.
const RecordedTick = struct {
    players: [state_mod.max_players]replay_codec.ReplayPlayerInput,
    player_count: usize,
    dt: f32,
};

/// A recording lifted out of its session, so the session can be restarted (or
/// destroyed) while the bytes are still being produced.
///
/// Encoding is proportional to MATCH LENGTH — one msgpack row per tick — so
/// doing it inline stalls the frame by an amount that grows with the run. Making
/// it faster only moves the cliff: halve the constant and a match twice as long
/// is back where it started. Detaching is O(1) (two ArrayLists move by value),
/// which is what actually takes the cost off the main thread's clock.
///
/// Owns its config outright rather than borrowing the session's — the session
/// may be gone by the time this encodes.
const RecordingBox = struct {
    generation: u32 = 0,
    config: HostSessionConfig = .{},
    ticks: std.ArrayList(RecordedTick) = .empty,
    events: std.ArrayList(replay_codec.ReplayEvent) = .empty,
    stats: RecordedStats = .{},

    fn deinit(self: *RecordingBox) void {
        self.ticks.deinit(gpa);
        self.events.deinit(gpa);
    }
};

const max_sessions = 8;
/// One in flight is the normal case (the run that just ended). The spare covers
/// a player who dies, restarts and dies again before the first encode lands.
const max_recordings = 4;
const gpa = std.heap.page_allocator;

var session_slots: [max_sessions]?*SessionBox = [_]?*SessionBox{null} ** max_sessions;
var next_generation: u32 = 1;
var recording_slots: [max_recordings]?*RecordingBox = [_]?*RecordingBox{null} ** max_recordings;
var next_recording_generation: u32 = 1;
// The recording table is the ONE structure two threads touch: the host detaches
// on its main thread and destroys from the worker that encoded. The boxes
// themselves need no lock — a handle has exactly one owner at a time — but the
// slot array and generation counter are shared, so every read and write of them
// goes through this.
//
// A spin is right here rather than a blocking lock: the critical sections are a
// short scan of a 4-entry array, contention needs two deaths inside one encode,
// and spinning keeps this free of any dependency on the host's threading.
var recording_lock: std.atomic.Mutex = .unlocked;

fn lockRecordings() void {
    while (!recording_lock.tryLock()) std.atomic.spinLoopHint();
}

fn unlockRecordings() void {
    recording_lock.unlock();
}

// THREAD-LOCAL, because encoding a detached recording is meant to run on a
// background thread while the main thread keeps calling in. A shared buffer
// would let the two overwrite each other's messages, and the host reads the
// error straight after its own failing call. Safe with runOnBigStack: none of
// the closures it runs set an error — they return one, and the caller formats
// it back on its own thread.
threadlocal var last_error: [1024]u8 = undefined;
threadlocal var last_error_len: usize = 0;

fn setError(message: []const u8) void {
    const len = @min(message.len, last_error.len);
    @memcpy(last_error[0..len], message[0..len]);
    last_error_len = len;
}

fn setErrorFmt(comptime fmt: []const u8, args: anytype) void {
    const written = std.fmt.bufPrint(&last_error, fmt, args) catch {
        last_error_len = last_error.len;
        return;
    };
    last_error_len = written.len;
}

// Session init (and the replay verifier) build multi-megabyte structs on the
// stack. Host threads (e.g. .NET, 1 MiB) are too small, so stack-heavy entry
// points run to completion on a dedicated big-stack thread.
const dispatch_stack_size: usize = 64 * 1024 * 1024;

fn runOnBigStack(comptime func: anytype, args: anytype) !void {
    const Args = @TypeOf(args);
    const Task = struct {
        args: Args,
        err: ?anyerror = null,
        fn run(task: *@This()) void {
            @call(.auto, func, task.args) catch |e| {
                task.err = e;
            };
        }
    };
    var task = Task{ .args = args };
    const thread = try std.Thread.spawn(
        .{ .stack_size = dispatch_stack_size },
        Task.run,
        .{&task},
    );
    thread.join();
    if (task.err) |e| return e;
}

fn handleFor(index: usize, generation: u32) u64 {
    return (@as(u64, generation) << 32) | @as(u64, @intCast(index));
}

fn boxForHandle(handle: u64) ?*SessionBox {
    const index: usize = @intCast(handle & 0xFFFF_FFFF);
    const generation: u32 = @intCast(handle >> 32);
    if (index >= max_sessions) return null;
    const box = session_slots[index] orelse return null;
    if (box.generation != generation) return null;
    return box;
}

fn recordingForHandle(handle: u64) ?*RecordingBox {
    const index: usize = @intCast(handle & 0xFFFF_FFFF);
    const generation: u32 = @intCast(handle >> 32);
    if (index >= max_recordings) return null;
    lockRecordings();
    defer unlockRecordings();
    const rec = recording_slots[index] orelse return null;
    if (rec.generation != generation) return null;
    return rec;
}

/// Why a recording cannot be handed over yet, or `.ok`/`.empty` when it can.
/// Shared by finish and detach so the two cannot drift on what counts as a
/// usable capture.
const RecordingState = enum { ok, empty, not_started, overflow, short };

fn recordingState(box: *SessionBox) RecordingState {
    if (box.record_overflow) return .overflow;
    // Never started is a caller bug; started-but-empty is a session that ended
    // before it ticked, which the frontend hits on every trip through the menu.
    if (!box.recording) return .not_started;
    if (box.record_ticks.items.len == 0) return .empty;
    // FAIL CLOSED on a short recording. The claimed stats cover every tick the
    // SESSION ran; the replay only replays the rows captured. If the session
    // advanced ticks that were not recorded, the stats describe a longer run
    // than the replay contains and every cumulative field reads high while the
    // tick count still looks right (it is taken from the row count).
    if (box.record_ticks.items.len != box.runner.session.tick_index) return .short;
    return .ok;
}

fn setRecordingStateError(box: *SessionBox, state: RecordingState) void {
    switch (state) {
        .overflow => setError("replay recording ran out of memory; capture is incomplete"),
        .not_started => setError("replay recording was never started (call crimson_host_replay_begin)"),
        .short => setErrorFmt(
            "recording covers {d} ticks but the session ran {d}; stats would not match the replay",
            .{ box.record_ticks.items.len, box.runner.session.tick_index },
        ),
        .ok, .empty => {},
    }
}

/// Move the capture out of the session. O(1): the ArrayLists move by value and
/// the session is left with fresh empty ones, so it can be restarted or
/// destroyed immediately without waiting on the encode.
fn takeRecording(box: *SessionBox) RecordingBox {
    const rec: RecordingBox = .{
        .config = box.config,
        .ticks = box.record_ticks,
        .events = box.record_events,
        .stats = box.record_stats,
    };
    box.record_ticks = .empty;
    box.record_events = .empty;
    box.recording = false;
    return rec;
}

pub export fn crimson_host_abi_version() u32 {
    return abi_version;
}

pub export fn crimson_host_last_error(buf: ?[*]u8, len: u32) i32 {
    if (last_error_len == 0) return 0;
    const out = buf orelse return -@as(i32, @intCast(last_error_len));
    if (len < last_error_len) return -@as(i32, @intCast(last_error_len));
    @memcpy(out[0..last_error_len], last_error[0..last_error_len]);
    return @intCast(last_error_len);
}

pub export fn crimson_host_session_create(
    config_json: ?[*]const u8,
    config_len: u32,
    out_handle: ?*u64,
) i32 {
    last_error_len = 0;
    const out = out_handle orelse {
        setError("out_handle is null");
        return err_invalid_input;
    };

    var config: HostSessionConfig = .{};
    if (config_json) |ptr| {
        if (config_len > 0) {
            const parsed = std.json.parseFromSlice(
                HostSessionConfig,
                gpa,
                ptr[0..config_len],
                .{ .ignore_unknown_fields = true },
            ) catch |err| {
                setErrorFmt("invalid config json: {s}", .{@errorName(err)});
                return err_invalid_config;
            };
            defer parsed.deinit();
            config = parsed.value;
        }
    }

    const game_mode = std.enums.fromInt(game_ids.GameModeId, config.game_mode) orelse {
        setErrorFmt("invalid game_mode: {d}", .{config.game_mode});
        return err_invalid_config;
    };

    var slot_index: usize = max_sessions;
    for (session_slots, 0..) |slot, idx| {
        if (slot == null) {
            slot_index = idx;
            break;
        }
    }
    if (slot_index >= max_sessions) {
        setError("all session slots in use");
        return err_out_of_sessions;
    }

    const box = gpa.create(SessionBox) catch {
        setError("session allocation failed");
        return err_generic;
    };
    errdefer gpa.destroy(box);

    // LiveRunner holds internal pointers into itself (creature pool -> effect
    // pool, quest spawn entry slice). Init through a heap staging copy, then
    // restoreSnapshot rebinds those pointers relative to the final location.
    const staging = gpa.create(live_runner.LiveRunnerSnapshot) catch {
        gpa.destroy(box);
        setError("session allocation failed");
        return err_generic;
    };
    defer gpa.destroy(staging);

    const initStaging = struct {
        fn run(dst: *live_runner.LiveRunnerSnapshot, cfg: live_runner.LiveModeConfig) !void {
            dst.runner = try live_runner.LiveRunner.init(cfg);
        }
    }.run;
    runOnBigStack(initStaging, .{ staging, live_runner.LiveModeConfig{
        .seed = config.seed,
        .game_mode = game_mode,
        .quest_level_key = config.quest_level_key,
        .player_count = config.player_count,
        .world_size = config.world_size,
        .tick_rate = config.tick_rate,
        .detail_preset = config.detail_preset,
        .gore_disabled = config.gore_disabled,
        .hardcore = config.hardcore,
        .preserve_bugs = config.preserve_bugs,
        .demo_mode_active = config.demo_mode_active,
        .status_quest_unlock_index = config.status_quest_unlock_index,
        .status_quest_unlock_index_full = config.status_quest_unlock_index_full,
        .status_weapon_usage_counts = config.status_weapon_usage_counts,
        .debug_fx_showcase = config.debug_fx_showcase,
    } }) catch |err| {
        gpa.destroy(box);
        setErrorFmt("session init failed: {s}", .{@errorName(err)});
        return err_invalid_config;
    };

    box.* = .{
        .generation = next_generation,
        .runner = undefined,
        .config = config,
    };
    box.runner.restoreSnapshot(staging);

    next_generation +%= 1;
    if (next_generation == 0) next_generation = 1;

    session_slots[slot_index] = box;
    out.* = handleFor(slot_index, box.generation);
    return ok;
}

/// ABI v16: read the session's CURRENT per-weapon usage counts (persisted-in
/// values + this run's assigns; save-status parity, index = weapon id, slot 0
/// unused). Writes up to `max` u32 entries; returns the count written, or
/// -needed if the buffer is too small (call with null/0 to size). The host
/// persists these at run end so the used-weapon drop reroll and the Unlocked
/// Weapons Database survive across sessions like the native save status.
pub export fn crimson_host_status_weapon_usage(
    handle: u64,
    out_counts: ?[*]u32,
    max: u32,
) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const needed: u32 = @intCast(state_mod.weapon_count_size);
    const out = out_counts orelse return -@as(i32, @intCast(needed));
    if (max < needed) return -@as(i32, @intCast(needed));
    const counts = &box.runner.session.state.status_weapon_usage_counts;
    var idx: usize = 0;
    while (idx < state_mod.weapon_count_size) : (idx += 1) {
        out[idx] = counts.get(@enumFromInt(idx));
    }
    return @intCast(needed);
}

pub export fn crimson_host_session_destroy(handle: u64) void {
    const index: usize = @intCast(handle & 0xFFFF_FFFF);
    if (index >= max_sessions) return;
    const box = session_slots[index] orelse return;
    if (box.generation != @as(u32, @intCast(handle >> 32))) return;
    session_slots[index] = null;
    gpa.destroy(box);
}

fn frameInputFromHost(inputs: []const CrimsonHostInput) live_runner.FrameInput {
    var frame: live_runner.FrameInput = .{};
    const count = @min(inputs.len, frame.players.len);
    frame.player_count = count;
    for (inputs[0..count], 0..) |input, idx| {
        frame.players[idx] = .{
            .move_x = input.move_x,
            .move_y = input.move_y,
            .aim_x = input.aim_x,
            .aim_y = input.aim_y,
            .flags = .{
                .fire_down = input.flags & input_flag_fire_down != 0,
                .fire_pressed = input.flags & input_flag_fire_pressed != 0,
                .reload_pressed = input.flags & input_flag_reload_pressed != 0,
                .reload_down = input.flags & input_flag_reload_down != 0,
                .move_to_cursor_pressed = input.flags & input_flag_move_to_cursor_pressed != 0,
                .move_mode = if (input.move_mode >= 0) input.move_mode else null,
                .aim_scheme = if (input.aim_scheme >= 0) input.aim_scheme else null,
            },
        };
    }
    if (count > 0) {
        frame.player = frame.players[0];
        frame.perk_menu_active = inputs[0].perk_menu_active != 0;
        frame.perk_choice_index = if (inputs[0].perk_choice_index >= 0)
            inputs[0].perk_choice_index
        else
            null;
    }
    return frame;
}

/// Record the perk traffic for player 0.
///
/// The replay runner needs BOTH halves. `perk_menu_open` is what makes it draw
/// the same three choices as the live run (the selection comes off the RNG, so
/// missing the open means a different offer entirely), and `perk_pick` applies
/// the one taken. Without them a recording still decodes and runs, and diverges
/// from the player's first level-up onward — tick count intact, every other stat
/// wrong, which is exactly how the omission was found.
///
/// Both are derived from host input transitions, because that is what the live
/// sim itself reacts to: the frontend owns when the menu is up and which card
/// was poked.
/// ALSO rolls the offer, and that is the point: opening the menu is the moment
/// the choices are drawn, so it has to happen at a place the recording can name.
/// It used to happen wherever the offer was first READ — which was
/// crimson_host_snapshot, polled every frame — so the draw landed between ticks,
/// at a position no replay could reproduce.
///
/// Runs whether or not the session is recording. The roll is sim state, not
/// bookkeeping: making it conditional on recording would give recorded and
/// unrecorded runs different games.
fn notePerkInput(box: *SessionBox, inputs: []const CrimsonHostInput) void {
    if (inputs.len == 0) return;
    const input = inputs[0];

    const menu_active = input.perk_menu_active != 0;
    const opened = menu_active and !box.menu_was_active;
    box.menu_was_active = menu_active;

    // Guarded on a pick actually being owed, and the event is recorded only when
    // the roll really happened, so the replay's open lands on an offer rather
    // than on nothing.
    if (opened and box.runner.perkPendingCount() > 0) {
        _ = box.runner.openPerkMenu();
        if (box.recording and !box.record_overflow) {
            box.record_pending_menu_open = true;
        }
    }

    if (input.perk_choice_index >= 0 and box.recording and !box.record_overflow) {
        box.record_pending_pick = input.perk_choice_index;
    }
}

/// Stamp any pending perk traffic onto the first tick this frame advanced.
///
/// Called only when ticks_advanced > 0, so the stamped index always names a
/// tick the recording contains and the replay will therefore reach. Order
/// matters: the open must precede the pick, because opening is what makes the
/// re-simulation draw the choice list the index then selects from.
fn flushPerkEvents(box: *SessionBox, tick_index: u64) void {
    if (box.record_overflow) return;

    if (box.record_pending_menu_open) {
        box.record_events.append(gpa, .{ .perk_menu_open = .{
            .tick_index = @intCast(tick_index),
            .player_index = 0,
        } }) catch {
            box.record_overflow = true;
            return;
        };
        box.record_pending_menu_open = false;
    }

    if (box.record_pending_pick) |choice| {
        box.record_events.append(gpa, .{ .perk_pick = .{
            .tick_index = @intCast(tick_index),
            .player_index = 0,
            .choice_index = choice,
        } }) catch {
            box.record_overflow = true;
            return;
        };
        box.record_pending_pick = null;
    }
}

/// Append one row per tick the frame actually advanced.
///
/// The VR loop drives a fixed 60 Hz accumulator so this is normally 1:1, but
/// "normally" is not a guarantee: under load stepFrame can catch up several
/// ticks at once, and a row-per-FRAME recording would then claim fewer ticks
/// than were simulated and diverge on replay. Repeating the frame's input
/// across its own substeps is what the runner did to it anyway.
///
/// A recording that outgrows memory stops capturing and latches
/// `record_overflow`, so finish can refuse rather than emit a truncated replay
/// that fails verification for a reason nobody can see.
fn recordFrame(
    box: *SessionBox,
    inputs: []const CrimsonHostInput,
    ticks_advanced: u64,
    dt: f32,
) void {
    if (ticks_advanced == 0 or box.record_overflow) return;

    var row: RecordedTick = .{
        .players = undefined,
        .player_count = @min(inputs.len, state_mod.max_players),
        .dt = dt,
    };
    for (inputs[0..row.player_count], 0..) |input, idx| {
        row.players[idx] = .{
            .move_x = input.move_x,
            .move_y = input.move_y,
            .aim_x = input.aim_x,
            .aim_y = input.aim_y,
            .flags = replay_codec.packInputFlags(.{
                .fire_down = input.flags & input_flag_fire_down != 0,
                .fire_pressed = input.flags & input_flag_fire_pressed != 0,
                .reload_pressed = input.flags & input_flag_reload_pressed != 0,
                .reload_down = input.flags & input_flag_reload_down != 0,
                .move_mode = if (input.move_mode >= 0) input.move_mode else null,
                .aim_scheme = if (input.aim_scheme >= 0) input.aim_scheme else null,
            }),
        };
    }

    var remaining = ticks_advanced;
    while (remaining > 0) : (remaining -= 1) {
        box.record_ticks.append(gpa, row) catch {
            box.record_overflow = true;
            return;
        };
    }
}

pub export fn crimson_host_session_tick(
    handle: u64,
    inputs: ?[*]const CrimsonHostInput,
    input_count: u32,
    out_result: ?*CrimsonHostTickResult,
) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const input_ptr = inputs orelse {
        setError("inputs is null");
        return err_invalid_input;
    };
    if (input_count == 0) {
        setError("input_count is zero");
        return err_invalid_input;
    }

    const frame = frameInputFromHost(input_ptr[0..input_count]);
    const dt_nominal = box.runner.session.dt_nominal;
    // Captured BEFORE the step: perk events are applied by the replay runner at
    // the tick they are stamped with, which is the tick this call is about to
    // simulate, not the one it leaves behind.
    const tick_before = box.runner.session.tick_index;
    // Unconditional: this both rolls the perk offer and notes the event. The
    // roll belongs to the simulation, so it cannot depend on whether anyone is
    // recording. Placed before the step so the draw sits immediately ahead of
    // tick_before -- the same position the replay draws at when it applies the
    // perk_menu_open event stamped with that index.
    notePerkInput(box, input_ptr[0..input_count]);
    const update = box.runner.stepFrame(dt_nominal, frame) catch |err| {
        setErrorFmt("tick failed: {s}", .{@errorName(err)});
        return err_generic;
    };
    box.last_audio = update.audio;
    box.last_terrain_fx = update.terrain_fx;

    if (box.recording) {
        if (update.ticks_advanced > 0) {
            flushPerkEvents(box, tick_before);
        }
        recordFrame(box, input_ptr[0..input_count], update.ticks_advanced, dt_nominal);
        box.record_stats = .{
            .elapsed_ms_sim = update.elapsed_ms_sim,
            .player_experience = update.player_experience,
            .creature_kill_count = update.creature_kill_count,
            .most_used_weapon_id = update.most_used_weapon_id,
            .shots_fired = update.shots_fired,
            .shots_hit = update.shots_hit,
        };
    }

    if (out_result) |out| {
        const elapsed_bits: u64 = @bitCast(update.elapsed_ms_sim);
        out.* = .{
            .ticks_advanced = @intCast(update.ticks_advanced),
            .paused_for_perk_pick = @intFromBool(update.paused_for_perk_pick),
            .all_players_dead = @intFromBool(update.all_players_dead),
            .perk_pending_count = box.runner.perkPendingCount(),
            .player_health = update.player_health,
            .player_level = update.player_level,
            .player_experience = update.player_experience,
            .player_weapon_id = update.player_weapon_id,
            .creature_active_count = @intCast(update.creature_active_count),
            .bonus_active_count = @intCast(update.bonus_active_count),
            .shots_fired = update.shots_fired,
            .shots_hit = update.shots_hit,
            .elapsed_ms_sim_lo = @truncate(elapsed_bits),
            .elapsed_ms_sim_hi = @truncate(elapsed_bits >> 32),
            .creature_kill_count = update.creature_kill_count,
            .most_used_weapon_id = update.most_used_weapon_id,
            .quest_completed = @intFromBool(box.runner.session.quest_completed),
        };
    }
    return ok;
}

pub fn snapshotMaxSize() u32 {
    const total: usize = @sizeOf(SnapshotHeader) +
        @sizeOf(PlayerSnap) * state_mod.max_players +
        @sizeOf(CreatureSnap) * crimson_zig.creatures.max_creatures +
        @sizeOf(ProjectileSnap) * crimson_zig.projectiles.main_projectile_pool_size +
        @sizeOf(SecondarySnap) * crimson_zig.secondary_projectiles.secondary_projectile_pool_size +
        @sizeOf(BonusSnap) * crimson_zig.bonuses.bonus_pool_size +
        @sizeOf(ParticleSnap) * crimson_zig.effects.effect_pool_size +
        @sizeOf(ParticleGlowSnap) * crimson_zig.particles.particle_pool_size +
        @sizeOf(SpriteEffectSnap) * crimson_zig.effects.sprite_effect_pool_size;
    return @intCast(total);
}

pub export fn crimson_host_snapshot_max_size() u32 {
    return snapshotMaxSize();
}

fn writeStruct(buf: []u8, offset: *usize, value: anytype) void {
    const bytes = std.mem.asBytes(&value);
    @memcpy(buf[offset.*..][0..bytes.len], bytes);
    offset.* += bytes.len;
}

pub export fn crimson_host_snapshot(handle: u64, buf: ?[*]u8, len: ?*u32) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const len_ptr = len orelse {
        setError("len is null");
        return err_invalid_input;
    };

    var header: SnapshotHeader = .{
        .magic = snapshot_magic,
        .version = abi_version,
        .tick_lo = @truncate(box.runner.session.tick_index),
        .tick_hi = @truncate(box.runner.session.tick_index >> 32),
        .game_mode = @intFromEnum(box.runner.session.game_mode),
        .world_size = box.runner.session.world_size,
        .elapsed_ms_sim = box.runner.session.elapsed_ms_sim,
        .perk_pending_count = box.runner.perkPendingCount(),
        .perk_choice_count = 0,
        .perk_choices = [_]i32{0} ** 7,
        .player_count = 0,
        .creature_count = 0,
        .projectile_count = 0,
        .secondary_count = 0,
        .bonus_count = 0,
        .particle_count = 0,
        .energizer_timer = box.runner.session.state.bonuses.energizer,
        .freeze_timer = box.runner.session.state.bonuses.freeze,
        .monster_vision = 0,
        .glow_count = 0,
        .sprite_effect_count = 0,
        .weapon_power_up_timer = box.runner.session.state.bonuses.weapon_power_up,
        .reflex_boost_timer = box.runner.session.state.bonuses.reflex_boost,
        .double_experience_timer = box.runner.session.state.bonuses.double_experience,
    };

    // Monster Vision is a per-player perk that draws a yellow aura over every
    // creature (draw_creature_overlays / build_draw_context); surface it as a
    // global flag keyed on the local player.
    {
        const players_const = box.runner.session.playersConst();
        if (players_const.len > 0 and
            crimson_zig.perks.perkActive(&players_const[0], crimson_zig.perks.PerkId.monster_vision))
        {
            header.monster_vision = 1;
        }
    }

    if (header.perk_pending_count > 0) {
        // PREPARED, never "current": the current accessor GENERATES when the
        // offer has not been rolled yet, and this snapshot is polled every
        // frame. That made a read advance the sim rng between ticks, so replays
        // diverged from the player's first level-up. Reporting an empty offer
        // until the menu is opened is the correct answer here -- the roll
        // belongs to the open, which is a recorded moment.
        const choices = box.runner.preparedPerkChoices();
        header.perk_choice_count = @intCast(@min(choices.len, header.perk_choices.len));
        for (choices[0..header.perk_choice_count], 0..) |choice, idx| {
            header.perk_choices[idx] = @intFromEnum(choice);
        }
    }

    const players = box.runner.session.playersConst();
    header.player_count = @intCast(players.len);
    for (box.runner.session.creatures.entries) |entry| {
        if (entry.active) header.creature_count += 1;
    }
    for (box.runner.session.projectiles.entries) |entry| {
        if (entry.active) header.projectile_count += 1;
    }
    for (box.runner.session.secondary_projectiles.entries) |entry| {
        if (entry.active) header.secondary_count += 1;
    }
    for (box.runner.session.bonuses.entries) |entry| {
        if (entry.bonus_id != .unused and !entry.picked) header.bonus_count += 1;
    }
    // Live sprite-effect entries, using draw_effect_pool's liveness gate.
    for (box.runner.session.effects.entries) |entry| {
        if (entry.flags != 0 and entry.age >= 0.0) header.particle_count += 1;
    }
    // Live flame/bubblegun particles (draw_particle_pool's `active` gate).
    for (box.runner.session.particles.entries) |entry| {
        if (entry.active) header.glow_count += 1;
    }
    // Live sprite effects (draw_sprite_effect_pool's `active` gate).
    for (box.runner.session.sprite_effects.entries) |entry| {
        if (entry.active) header.sprite_effect_count += 1;
    }

    const required: u32 = @sizeOf(SnapshotHeader) +
        @sizeOf(PlayerSnap) * header.player_count +
        @sizeOf(CreatureSnap) * header.creature_count +
        @sizeOf(ProjectileSnap) * header.projectile_count +
        @sizeOf(SecondarySnap) * header.secondary_count +
        @sizeOf(BonusSnap) * header.bonus_count +
        @sizeOf(ParticleSnap) * header.particle_count +
        @sizeOf(ParticleGlowSnap) * header.glow_count +
        @sizeOf(SpriteEffectSnap) * header.sprite_effect_count;

    const out_ptr = buf orelse {
        len_ptr.* = required;
        return ok;
    };
    if (len_ptr.* < required) {
        len_ptr.* = required;
        setError("snapshot buffer too small");
        return err_buffer_too_small;
    }

    const out = out_ptr[0..len_ptr.*];
    var offset: usize = 0;
    writeStruct(out, &offset, header);

    for (players) |player| {
        writeStruct(out, &offset, PlayerSnap{
            .x = player.pos.x,
            .y = player.pos.y,
            .heading = player.heading,
            .aim_x = player.aim.x,
            .aim_y = player.aim.y,
            .aim_heading = player.aim_heading,
            .health = player.health,
            .size = player.size,
            .muzzle_flash_alpha = player.muzzle_flash_alpha,
            .weapon_id = @intFromEnum(player.weapon.weapon_id),
            .ammo = player.weapon.ammo,
            .clip_size = player.weapon.clip_size,
            .reload_active = @intFromBool(player.weapon.reload_active),
            .reload_timer = player.weapon.reload_timer,
            .reload_timer_max = player.weapon.reload_timer_max,
            .experience = player.experience,
            .level = player.level,
            .weapon_icon_index = crimson_zig.weapon_data.weaponIconIndex(player.weapon.weapon_id),
            .weapon_ammo_class = crimson_zig.weapon_data.weaponAmmoClass(player.weapon.weapon_id),
            .spread_heat = player.spread_heat,
            .shield_timer = player.shield_timer,
            .perk_flags = (if (crimson_zig.perks.perkActive(&player, crimson_zig.perks.PerkId.doctor)) player_perk_flag_doctor else 0) |
                (if (crimson_zig.perks.perkActive(&player, crimson_zig.perks.PerkId.radioactive)) player_perk_flag_radioactive else 0) |
                (if (crimson_zig.perks.perkActive(&player, crimson_zig.perks.PerkId.sharpshooter)) player_perk_flag_sharpshooter else 0) |
                (if (crimson_zig.perks.perkActive(&player, crimson_zig.perks.PerkId.ion_gun_master)) player_perk_flag_ion_gun_master else 0),
            .move_phase = player.move_phase,
            .death_timer = player.death_timer,
            .fire_bullets_timer = player.fire_bullets_timer,
            .speed_bonus_timer = player.speed_bonus_timer,
            .aux_timer = player.aux_timer,
        });
    }
    for (box.runner.session.creatures.entries, 0..) |entry, creature_slot| {
        if (!entry.active) continue;
        const wire_flags: u32 = entry.flags | (if (entry.plague_infected) creature_wire_flag_plague else 0);
        writeStruct(out, &offset, CreatureSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .heading = entry.heading,
            .size = entry.size,
            .anim_phase = entry.anim_phase,
            .hp = entry.hp,
            .max_hp = entry.max_hp,
            .lifecycle_stage = entry.lifecycle_stage,
            .type_id = entry.type_id,
            .flags = wire_flags,
            .r = entry.color[0],
            .g = entry.color[1],
            .b = entry.color[2],
            .a = entry.color[3],
            .hit_flash_timer = entry.hit_flash_timer,
            .pool_index = @intCast(creature_slot),
            .generation = entry.presentation_generation,
        });
    }
    for (box.runner.session.projectiles.entries, 0..) |entry, proj_slot| {
        if (!entry.active) continue;
        writeStruct(out, &offset, ProjectileSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .angle = entry.angle,
            .type_id = entry.type_id,
            .vx = entry.vel.x,
            .vy = entry.vel.y,
            .life_timer = entry.life_timer,
            .origin_x = entry.origin.x,
            .origin_y = entry.origin.y,
            .speed_scale = entry.speed_scale,
            .travel_budget = entry.travel_budget,
            .pool_index = @intCast(proj_slot),
        });
    }
    for (box.runner.session.secondary_projectiles.entries) |entry| {
        if (!entry.active) continue;
        writeStruct(out, &offset, SecondarySnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .angle = entry.angle,
            .detonation_t = entry.detonation_t,
            .detonation_scale = entry.detonation_scale,
            .type_id = @intFromEnum(entry.type_id),
        });
    }
    for (box.runner.session.bonuses.entries) |entry| {
        if (entry.bonus_id == .unused or entry.picked) continue;
        writeStruct(out, &offset, BonusSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .time_left = entry.time_left,
            .time_max = entry.time_max,
            .bonus_id = @intFromEnum(entry.bonus_id),
            .amount = entry.amount,
        });
    }
    for (box.runner.session.effects.entries) |entry| {
        if (entry.flags == 0 or entry.age < 0.0) continue;
        writeStruct(out, &offset, ParticleSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .half_width = entry.half_width,
            .half_height = entry.half_height,
            .scale = entry.scale,
            .rotation = entry.rotation,
            .r = entry.color.r,
            .g = entry.color.g,
            .b = entry.color.b,
            .a = entry.color.a,
            .age = entry.age,
            .effect_id = entry.effect_id,
            .flags = entry.flags,
        });
    }
    for (box.runner.session.particles.entries) |entry| {
        if (!entry.active) continue;
        writeStruct(out, &offset, ParticleGlowSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .intensity = entry.intensity,
            .spin = entry.spin,
            .tint_r = entry.scale_x,
            .tint_g = entry.scale_y,
            .tint_b = entry.scale_z,
            .age = entry.age,
            .style_id = @intFromEnum(entry.style_id),
        });
    }
    for (box.runner.session.sprite_effects.entries) |entry| {
        if (!entry.active) continue;
        writeStruct(out, &offset, SpriteEffectSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .scale = entry.scale,
            .rotation = entry.rotation,
            .r = entry.color.r,
            .g = entry.color.g,
            .b = entry.color.b,
            .a = entry.color.a,
        });
    }

    len_ptr.* = required;
    return ok;
}

pub export fn crimson_host_audio_events(handle: u64, buf: ?[*]u8, len: ?*u32) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const len_ptr = len orelse {
        setError("len is null");
        return err_invalid_input;
    };

    const audio = box.last_audio;
    var flags: u32 = 0;
    if (audio.perk_menu_opened) flags |= audio_flag_perk_menu_opened;
    if (audio.trigger_game_tune) flags |= audio_flag_trigger_game_tune;
    if (audio.quest_play_hit_sfx) flags |= audio_flag_quest_hit_sfx;
    if (audio.quest_play_completion_music) flags |= audio_flag_quest_completion_music;

    const header: AudioHeader = .{
        .version = abi_version,
        .flags = flags,
        .shot_count = @intCast(audio.shot_event_count),
        .reload_count = @intCast(audio.reload_event_count),
        .hit_count = @intCast(audio.hit_event_count),
        .sfx_count = @intCast(audio.sfx_event_count),
    };

    const required: u32 = @sizeOf(AudioHeader) +
        @sizeOf(ShotAudioSnap) * header.shot_count +
        @sizeOf(i32) * header.reload_count +
        @sizeOf(HitAudioSnap) * header.hit_count +
        @sizeOf(i32) * header.sfx_count;

    const out_ptr = buf orelse {
        len_ptr.* = required;
        return ok;
    };
    if (len_ptr.* < required) {
        len_ptr.* = required;
        setError("audio buffer too small");
        return err_buffer_too_small;
    }

    const out = out_ptr[0..len_ptr.*];
    var offset: usize = 0;
    writeStruct(out, &offset, header);
    for (audio.shot_events[0..audio.shot_event_count]) |event| {
        writeStruct(out, &offset, ShotAudioSnap{
            .weapon_id = event.weapon_id,
            .fire_bullets_active = @intFromBool(event.fire_bullets_active),
        });
    }
    for (audio.reload_weapon_ids[0..audio.reload_event_count]) |weapon_id| {
        writeStruct(out, &offset, weapon_id);
    }
    for (audio.hit_events[0..audio.hit_event_count]) |plan| {
        writeStruct(out, &offset, HitAudioSnap{
            .shock_hit = @intFromBool(plan.shock_hit),
            .bullet_hit_roll = if (plan.bullet_hit_roll) |roll| @intCast(roll) else -1,
            .game_tune_roll = if (plan.game_tune_roll) |roll| @intCast(roll) else -1,
            .trigger_game_tune = @intFromBool(plan.trigger_game_tune),
        });
    }
    for (audio.sfx_events[0..audio.sfx_event_count]) |sfx| {
        writeStruct(out, &offset, @as(i32, @intCast(@intFromEnum(sfx))));
    }

    len_ptr.* = required;
    return ok;
}

/// Static terrain generation info (ABI v3). Query once after session create;
/// the values never change for the life of the session.
pub export fn crimson_host_terrain_info(handle: u64, out_info: ?*TerrainInfo) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const out = out_info orelse {
        setError("out_info is null");
        return err_invalid_input;
    };
    const setup = box.runner.terrain_setup;
    out.* = .{
        .terrain_slot_0 = @intCast(setup.terrain_slots[0]),
        .terrain_slot_1 = @intCast(setup.terrain_slots[1]),
        .terrain_slot_2 = @intCast(setup.terrain_slots[2]),
        .terrain_seed = setup.terrain_seed,
        .terrain_size = box.runner.session.terrain_size,
        .world_size = box.runner.session.world_size,
    };
    return ok;
}

/// Terrain FX (blood/scorch splats + corpse stamps) emitted during the last
/// tick. Same buffer protocol as crimson_host_snapshot; drain after every tick.
pub export fn crimson_host_terrain_fx(handle: u64, buf: ?[*]u8, len: ?*u32) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const len_ptr = len orelse {
        setError("len is null");
        return err_invalid_input;
    };

    const batch = box.last_terrain_fx;
    const header: TerrainFxHeader = .{
        .version = abi_version,
        .decal_count = @intCast(batch.decal_count),
        .corpse_count = @intCast(batch.corpse_count),
    };

    const required: u32 = @sizeOf(TerrainFxHeader) +
        @sizeOf(TerrainDecalSnap) * header.decal_count +
        @sizeOf(TerrainCorpseSnap) * header.corpse_count;

    const out_ptr = buf orelse {
        len_ptr.* = required;
        return ok;
    };
    if (len_ptr.* < required) {
        len_ptr.* = required;
        setError("terrain fx buffer too small");
        return err_buffer_too_small;
    }

    const out = out_ptr[0..len_ptr.*];
    var offset: usize = 0;
    writeStruct(out, &offset, header);
    for (batch.decalsSlice()) |entry| {
        writeStruct(out, &offset, TerrainDecalSnap{
            .effect_id = entry.effect_id,
            .x = entry.pos.x,
            .y = entry.pos.y,
            .width = entry.width,
            .height = entry.height,
            .rotation = entry.rotation,
            .r = entry.color.r,
            .g = entry.color.g,
            .b = entry.color.b,
            .a = entry.color.a,
        });
    }
    for (batch.corpsesSlice()) |entry| {
        writeStruct(out, &offset, TerrainCorpseSnap{
            .creature_type_id = entry.creature_type_id,
            .x = entry.top_left.x,
            .y = entry.top_left.y,
            .rotation = entry.rotation,
            .scale = entry.scale,
            .r = entry.color.r,
            .g = entry.color.g,
            .b = entry.color.b,
            .a = entry.color.a,
        });
    }

    len_ptr.* = required;
    return ok;
}

/// Passthrough to the native replay verifier. Lets hosts (and the M1 test
/// gate) validate that this library links the exact verified replay stack.
/// Start (or restart) replay capture on a session. Safe to call mid-run; it
/// discards anything captured so far.
pub export fn crimson_host_replay_begin(handle: u64) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    // REFUSE to record a session running a sim mutation the replay format
    // cannot carry. debug_fx_showcase makes reload cycle the player through the
    // arsenal, so the live run and any re-simulation of it hold different
    // weapons from the first reload onward -- different kills, shots, xp and
    // most-used weapon, while ticks and elapsed stay exact because the tick
    // stream is faithful. That is precisely the failure real VR replays showed
    // while scripted gates passed, because the gate config never sets it.
    // Recording anyway would keep writing files that cannot verify.
    if (box.config.debug_fx_showcase) {
        setError("replay recording unavailable: debug_fx_showcase mutates the sim and cannot be replayed");
        return err_invalid_config;
    }

    box.record_ticks.clearRetainingCapacity();
    box.record_events.clearRetainingCapacity();
    // menu_was_active is deliberately NOT reset: it is sim state now, not
    // capture state. begin is safe to call mid-run, and clearing the edge there
    // would make an already-open menu read as newly opened on the next tick and
    // roll a second offer over the one the player is looking at.
    box.record_pending_menu_open = false;
    box.record_pending_pick = null;
    box.record_overflow = false;
    box.record_stats = .{};
    box.recording = true;
    return ok;
}

/// Encode the captured run as replay bytes, using the same
/// buffer-size protocol as `crimson_host_snapshot`: call with a null buffer to
/// learn the size, then again with one that large.
///
/// A size of 0 with `ok` means the session recorded nothing — see the zero-tick
/// case below. Callers should treat that as "no replay for this run", not as a
/// failure. Claimed stats come from the live counters; `encodeSessionReplay`
/// explains why, and what stands in for the determinism that re-simulating used
/// to guarantee.
pub export fn crimson_host_replay_finish(handle: u64, buf: ?[*]u8, len: ?*u32) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const len_ptr = len orelse {
        setError("len is null");
        return err_invalid_input;
    };
    const state = recordingState(box);
    switch (state) {
        // Recording on but nothing captured is NOT a failure — it is a session
        // that ended before it ticked, which the frontend hits every time a run
        // is started from the menu (the outgoing session is finished on the way
        // out). Reporting it as an error only produced a log line per visit.
        .empty => {
            len_ptr.* = 0;
            return ok;
        },
        .ok => {},
        else => {
            setRecordingStateError(box, state);
            return err_generic;
        },
    }

    var bytes_slot: ?[]u8 = null;
    runOnBigStack(encodeSessionReplay, .{ box, &bytes_slot }) catch |err| {
        setErrorFmt("replay encode failed: {s}", .{@errorName(err)});
        return err_generic;
    };
    const bytes = bytes_slot orelse {
        setError("replay encode produced no output");
        return err_generic;
    };
    defer gpa.free(bytes);

    const out_ptr = buf orelse {
        len_ptr.* = @intCast(bytes.len);
        return ok;
    };
    if (len_ptr.* < bytes.len) {
        len_ptr.* = @intCast(bytes.len);
        setError("replay buffer too small");
        return err_buffer_too_small;
    }
    @memcpy(out_ptr[0..bytes.len], bytes);
    len_ptr.* = @intCast(bytes.len);
    return ok;
}

/// Encode the captured run. ONE pass, with claims taken from the live counters.
///
/// This deliberately does NOT re-simulate to source the stats. Doing so made
/// them agree by construction, but cost a full replay of the run on the calling
/// thread -- 5.7 seconds of frozen headset on leaving the score screen. The
/// determinism guarantee comes from the slice-8 gate instead: it builds a
/// recording from these same live counters and hands it to the verifier, so a
/// divergence between live and re-simulated stats breaks the test rather than
/// quietly producing replays that fail to verify.
///
/// Still on the big-stack thread: the encode allocates and the msgpack writer
/// is not something to run on a 1 MiB host stack.
fn encodeSessionReplay(box: *SessionBox, out: *?[]u8) !void {
    return encodeCapture(
        &box.config,
        box.record_ticks.items,
        box.record_events.items,
        box.record_stats,
        out,
    );
}

fn encodeDetachedRecording(rec: *RecordingBox, out: *?[]u8) !void {
    return encodeCapture(&rec.config, rec.ticks.items, rec.events.items, rec.stats, out);
}

/// The one place a capture becomes bytes, so the inline and detached paths
/// cannot drift on what they write.
fn encodeCapture(
    cfg: *const HostSessionConfig,
    ticks: []const RecordedTick,
    events: []const replay_codec.ReplayEvent,
    stats: RecordedStats,
    out: *?[]u8,
) !void {
    const rows = try gpa.alloc([]const replay_codec.ReplayPlayerInput, ticks.len);
    defer gpa.free(rows);
    const dts = try gpa.alloc(f32, ticks.len);
    defer gpa.free(dts);
    for (ticks, 0..) |*row, idx| {
        rows[idx] = row.players[0..row.player_count];
        dts[idx] = row.dt;
    }

    var quest_buf: [16]u8 = undefined;
    const header = recordingHeaderFor(cfg, &quest_buf);

    out.* = try replay_codec.encodeRecording(gpa, header, rows, dts, .{
        .complete = true,
        // The recording's own row count IS the tick count -- rows are appended
        // per tick advanced, so this cannot drift from what a replay will run.
        .ticks = @intCast(ticks.len),
        .elapsed_ms = stats.elapsed_ms_sim,
        .score_xp = stats.player_experience,
        .kills = stats.creature_kill_count,
        .most_used_weapon_id = stats.most_used_weapon_id,
        .shots_fired = stats.shots_fired,
        .shots_hit = stats.shots_hit,
    }, events);
}

/// Lift the capture out of the session so the session can be restarted or
/// destroyed immediately, and hand back a handle to encode later — off the main
/// thread, where a cost proportional to match length belongs.
///
/// Writes 0 to `out_recording` when the session recorded nothing, which is the
/// same non-failure as finish reporting size 0.
pub export fn crimson_host_replay_detach(handle: u64, out_recording: ?*u64) i32 {
    last_error_len = 0;
    const box = boxForHandle(handle) orelse {
        setError("invalid session handle");
        return err_invalid_handle;
    };
    const out = out_recording orelse {
        setError("out_recording is null");
        return err_invalid_input;
    };
    const state = recordingState(box);
    switch (state) {
        .empty => {
            box.recording = false;
            out.* = 0;
            return ok;
        },
        .ok => {},
        else => {
            setRecordingStateError(box, state);
            return err_generic;
        },
    }

    const rec = gpa.create(RecordingBox) catch {
        setError("recording allocation failed");
        return err_generic;
    };

    lockRecordings();
    defer unlockRecordings();
    var slot: ?usize = null;
    for (recording_slots, 0..) |entry, idx| {
        if (entry == null) {
            slot = idx;
            break;
        }
    }
    const index = slot orelse {
        gpa.destroy(rec);
        setError("too many recordings in flight; encode or destroy one first");
        return err_out_of_sessions;
    };

    // Only NOW is the capture taken out of the session — after the slot is
    // secured, so a failure above cannot leave the rows owned by nobody.
    rec.* = takeRecording(box);
    rec.generation = next_recording_generation;
    next_recording_generation +%= 1;
    if (next_recording_generation == 0) next_recording_generation = 1;
    recording_slots[index] = rec;
    out.* = handleFor(index, rec.generation);
    return ok;
}

/// Encode a detached recording. Same size-then-fill protocol as finish.
///
/// SAFE TO CALL OFF THE MAIN THREAD: it touches only the recording, which owns
/// everything it needs, and the error buffer it writes is thread-local. The
/// session it came from may already have been restarted or destroyed.
pub export fn crimson_host_recording_encode(recording: u64, buf: ?[*]u8, len: ?*u32) i32 {
    last_error_len = 0;
    const rec = recordingForHandle(recording) orelse {
        setError("invalid recording handle");
        return err_invalid_handle;
    };
    const len_ptr = len orelse {
        setError("len is null");
        return err_invalid_input;
    };

    var bytes_slot: ?[]u8 = null;
    runOnBigStack(encodeDetachedRecording, .{ rec, &bytes_slot }) catch |err| {
        setErrorFmt("replay encode failed: {s}", .{@errorName(err)});
        return err_generic;
    };
    const bytes = bytes_slot orelse {
        setError("replay encode produced no output");
        return err_generic;
    };
    defer gpa.free(bytes);

    const out_ptr = buf orelse {
        len_ptr.* = @intCast(bytes.len);
        return ok;
    };
    if (len_ptr.* < bytes.len) {
        len_ptr.* = @intCast(bytes.len);
        setError("replay buffer too small");
        return err_buffer_too_small;
    }
    @memcpy(out_ptr[0..bytes.len], bytes);
    len_ptr.* = @intCast(bytes.len);
    return ok;
}

/// Release a detached recording. Every handle from replay_detach must reach
/// this, including after a failed encode — the rows are the largest allocation
/// the host holds.
pub export fn crimson_host_recording_destroy(recording: u64) i32 {
    last_error_len = 0;
    const index: usize = @intCast(recording & 0xFFFF_FFFF);
    const generation: u32 = @intCast(recording >> 32);
    if (index >= max_recordings) {
        setError("invalid recording handle");
        return err_invalid_handle;
    }
    // Clear the slot under the lock and free outside it: the lookup and the
    // clear have to be one atomic step or two threads can both take the same
    // box and double-free it.
    lockRecordings();
    const taken = blk: {
        const rec = recording_slots[index] orelse break :blk null;
        if (rec.generation != generation) break :blk null;
        recording_slots[index] = null;
        break :blk rec;
    };
    unlockRecordings();

    const rec = taken orelse {
        setError("invalid recording handle");
        return err_invalid_handle;
    };
    rec.deinit();
    gpa.destroy(rec);
    return ok;
}

/// The ruleset version recordings are stamped with. Must keep a prefix in
/// `replay_codec.latest_ruleset_game_version_prefixes` or the verifier applies
/// legacy rules to a run simulated under current ones.
const recording_game_version: []const u8 = "0.9.0";

/// `quest_buf` backs the formatted quest-level string and must outlive the
/// encode that consumes the returned header.
///
/// `cfg` MUST be a pointer into the box, never a by-value copy. The returned
/// header borrows `status_weapon_usage_counts` as a slice; slicing a local copy
/// hands the encoder a pointer to this frame's stack, which is dead — and
/// promptly reused by the encode's own locals, so recordings shipped a usage
/// array made of elapsed_ms, score_xp, tick count and world_size bits. Usage
/// counts reroll weapon drops, so the replay then took a different weapon
/// stream than the run and desynced part-way through: the 4000-tick gate's
/// off-by-one shots_hit, with short runs passing because 600 ticks end before a
/// reroll changes anything.
fn recordingHeaderFor(cfg: *const HostSessionConfig, quest_buf: []u8) replay_codec.RecordingHeader {
    // The config carries the level as major*100 + minor (101 = "1.1"); the wire
    // wants the dotted string. Only quests mode has one — every other mode
    // records the empty string, as the fixtures do.
    const quest_level: []const u8 = if (cfg.game_mode == @intFromEnum(game_ids.GameModeId.quests))
        std.fmt.bufPrint(quest_buf, "{d}.{d}", .{
            @divTrunc(cfg.quest_level_key, 100),
            @mod(cfg.quest_level_key, 100),
        }) catch ""
    else
        "";

    return .{
        .game_mode_id = cfg.game_mode,
        .seed = cfg.seed,
        .world_size = cfg.world_size,
        .tick_rate = cfg.tick_rate,
        .player_count = cfg.player_count,
        .detail_preset = cfg.detail_preset,
        .gore_disabled = cfg.gore_disabled,
        .hardcore = cfg.hardcore,
        .preserve_bugs = cfg.preserve_bugs,
        .quest_level = quest_level,
        .game_version = recording_game_version,
        .quest_unlock_index = cfg.status_quest_unlock_index,
        .quest_unlock_index_full = cfg.status_quest_unlock_index_full,
        .weapon_usage_counts = cfg.status_weapon_usage_counts[0..],
    };
}

pub export fn crimson_host_verify_replay_json(
    replay: ?[*]const u8,
    replay_len: u32,
    out: ?[*]u8,
    out_len: ?*u32,
) i32 {
    last_error_len = 0;
    const replay_ptr = replay orelse {
        setError("replay is null");
        return err_invalid_input;
    };
    const out_len_ptr = out_len orelse {
        setError("out_len is null");
        return err_invalid_input;
    };
    var output_slot: ?verify_native.CommandOutput = null;
    const runVerify = struct {
        fn run(slot: *?verify_native.CommandOutput, bytes: []const u8) !void {
            slot.* = try verify_native.runReplayVerifyBytesJson(gpa, "<host_abi>", bytes, null);
        }
    }.run;
    runOnBigStack(runVerify, .{ &output_slot, replay_ptr[0..replay_len] }) catch |err| {
        setErrorFmt("verify failed: {s}", .{@errorName(err)});
        return err_generic;
    };
    const output = output_slot orelse {
        setError("verify produced no output");
        return err_generic;
    };
    defer output.deinit(gpa);

    if (output.exit_code == 1 and output.stdout.len == 0 and output.stderr.len > 0) {
        setError(std.mem.trimEnd(u8, output.stderr, "\n"));
        return err_generic;
    }

    const out_ptr = out orelse {
        out_len_ptr.* = @intCast(output.stdout.len);
        return ok;
    };
    if (out_len_ptr.* < output.stdout.len) {
        out_len_ptr.* = @intCast(output.stdout.len);
        setError("verify output buffer too small");
        return err_buffer_too_small;
    }
    @memcpy(out_ptr[0..output.stdout.len], output.stdout);
    out_len_ptr.* = @intCast(output.stdout.len);
    return ok;
}

test {
    _ = @import("tests.zig");
}
