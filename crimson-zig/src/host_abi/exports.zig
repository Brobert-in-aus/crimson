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
const verify_native = crimson_zig.verify_native;

pub const abi_version: u32 = 1;
pub const snapshot_magic: u32 = 0x31525643; // "CVR1" little-endian

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
};

pub const ProjectileSnap = extern struct {
    x: f32,
    y: f32,
    angle: f32,
    type_id: i32,
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

    staging.runner = live_runner.LiveRunner.init(.{
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
    }) catch |err| {
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
        @sizeOf(BonusSnap) * crimson_zig.bonuses.bonus_pool_size;
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
    };

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

    const required: u32 = @sizeOf(SnapshotHeader) +
        @sizeOf(PlayerSnap) * header.player_count +
        @sizeOf(CreatureSnap) * header.creature_count +
        @sizeOf(ProjectileSnap) * header.projectile_count +
        @sizeOf(SecondarySnap) * header.secondary_count +
        @sizeOf(BonusSnap) * header.bonus_count;

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
            .flags = entry.flags,
        });
    }
    for (box.runner.session.projectiles.entries) |entry| {
        if (!entry.active) continue;
        writeStruct(out, &offset, ProjectileSnap{
            .x = entry.pos.x,
            .y = entry.pos.y,
            .angle = entry.angle,
            .type_id = entry.type_id,
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
    const output = verify_native.runReplayVerifyBytesJson(
        gpa,
        "<host_abi>",
        replay_ptr[0..replay_len],
        null,
    ) catch |err| {
        setErrorFmt("verify failed: {s}", .{@errorName(err)});
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
