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

pub const abi_version: u32 = 9;
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
};

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
};

const SessionBox = struct {
    generation: u32,
    runner: live_runner.LiveRunner,
    last_audio: live_runner.FrameAudioEvents = .{},
    last_terrain_fx: terrain_fx_mod.TerrainFxBatch = .{},
};

const max_sessions = 8;
const gpa = std.heap.page_allocator;

var session_slots: [max_sessions]?*SessionBox = [_]?*SessionBox{null} ** max_sessions;
var next_generation: u32 = 1;

var last_error: [1024]u8 = undefined;
var last_error_len: usize = 0;

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
        .status_quest_unlock_index_full = config.status_quest_unlock_index,
    } }) catch |err| {
        gpa.destroy(box);
        setErrorFmt("session init failed: {s}", .{@errorName(err)});
        return err_invalid_config;
    };

    box.* = .{
        .generation = next_generation,
        .runner = undefined,
    };
    box.runner.restoreSnapshot(staging);

    next_generation +%= 1;
    if (next_generation == 0) next_generation = 1;

    session_slots[slot_index] = box;
    out.* = handleFor(slot_index, box.generation);
    return ok;
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
    const update = box.runner.stepFrame(box.runner.session.dt_nominal, frame) catch |err| {
        setErrorFmt("tick failed: {s}", .{@errorName(err)});
        return err_generic;
    };
    box.last_audio = update.audio;
    box.last_terrain_fx = update.terrain_fx;

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
        @sizeOf(ParticleGlowSnap) * crimson_zig.particles.particle_pool_size;
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
        const choices = box.runner.currentPerkChoices();
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

    const required: u32 = @sizeOf(SnapshotHeader) +
        @sizeOf(PlayerSnap) * header.player_count +
        @sizeOf(CreatureSnap) * header.creature_count +
        @sizeOf(ProjectileSnap) * header.projectile_count +
        @sizeOf(SecondarySnap) * header.secondary_count +
        @sizeOf(BonusSnap) * header.bonus_count +
        @sizeOf(ParticleSnap) * header.particle_count +
        @sizeOf(ParticleGlowSnap) * header.glow_count;

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
        });
    }
    for (box.runner.session.creatures.entries) |entry| {
        if (!entry.active) continue;
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
            .flags = entry.flags | (if (entry.plague_infected) creature_wire_flag_plague else 0),
            .r = entry.color[0],
            .g = entry.color[1],
            .b = entry.color[2],
            .a = entry.color[3],
            .hit_flash_timer = entry.hit_flash_timer,
        });
    }
    for (box.runner.session.projectiles.entries) |entry| {
        if (!entry.active) continue;
        writeStruct(out, &offset, ProjectileSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .angle = entry.angle,
            .type_id = entry.type_id,
            .vx = entry.vel.x,
            .vy = entry.vel.y,
            .life_timer = entry.life_timer,
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
