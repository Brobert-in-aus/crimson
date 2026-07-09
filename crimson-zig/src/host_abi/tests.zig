//! M1 verification gate for the crimson_host C ABI.
//!
//! These tests exercise the public exports exactly as an external host would
//! (raw pointers, packed buffers) and pin three properties:
//!   1. the replay-verify stack reachable through the ABI matches the native
//!      verifier byte-for-byte on committed fixtures,
//!   2. the ABI adds nothing to the sim: driving identical inputs through the
//!      ABI and through LiveRunner directly yields identical summaries,
//!   3. sessions are deterministic: same config + inputs => identical
//!      snapshot bytes.

const std = @import("std");
const crimson_zig = @import("crimson_zig");
const exports = @import("exports.zig");

const live_runner = crimson_zig.live_runner;
const verify_native = crimson_zig.verify_native;

const survival_fixture = @embedFile("testdata/gameplay_diff_capture.survival.run1.crd");
const rush_fixture = @embedFile("testdata/gameplay_diff_capture.rush.run1.crd");

const test_config_json =
    \\{"seed": 1234, "game_mode": 1, "player_count": 1, "world_size": 1024.0}
;

fn scriptedInput(tick: usize) exports.CrimsonHostInput {
    // Deterministic pseudo-play: swirl the aim point around the arena center,
    // hold fire in bursts, and walk toward a moving point.
    const t: f32 = @floatFromInt(tick);
    const angle = t * 0.02;
    var flags: u32 = 0;
    if ((tick / 30) % 2 == 0) flags |= 1; // fire_down bursts
    if (tick % 90 == 0) flags |= 2; // fire_pressed
    return .{
        .move_x = 512.0 + 200.0 * @cos(angle * 0.5),
        .move_y = 512.0 + 200.0 * @sin(angle * 0.5),
        .aim_x = 512.0 + 300.0 * @cos(angle),
        .aim_y = 512.0 + 300.0 * @sin(angle),
        .flags = flags | 16, // move_to_cursor_pressed
        .move_mode = 4, // MOUSE_POINT_CLICK
        .aim_scheme = 0, // MOUSE
        .perk_choice_index = -1,
        .perk_menu_active = 0,
    };
}

fn createTestSession() !u64 {
    var handle: u64 = 0;
    const rc = exports.crimson_host_session_create(
        test_config_json.ptr,
        @intCast(test_config_json.len),
        &handle,
    );
    try std.testing.expectEqual(exports.ok, rc);
    return handle;
}

test "abi version reports v8" {
    try std.testing.expectEqual(@as(u32, 8), exports.crimson_host_abi_version());
}

test "abi verify passthrough matches native verifier byte for byte" {
    const allocator = std.testing.allocator;
    for ([_][]const u8{ survival_fixture, rush_fixture }) |fixture| {
        const expected = try verify_native.runReplayVerifyBytesJson(
            allocator,
            "<host_abi>",
            fixture,
            null,
        );
        defer expected.deinit(allocator);

        var out_len: u32 = 0;
        try std.testing.expectEqual(exports.ok, exports.crimson_host_verify_replay_json(
            fixture.ptr,
            @intCast(fixture.len),
            null,
            &out_len,
        ));
        try std.testing.expectEqual(@as(u32, @intCast(expected.stdout.len)), out_len);

        const out = try allocator.alloc(u8, out_len);
        defer allocator.free(out);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_verify_replay_json(
            fixture.ptr,
            @intCast(fixture.len),
            out.ptr,
            &out_len,
        ));
        try std.testing.expectEqualStrings(expected.stdout, out[0..out_len]);
    }
}

test "abi session matches direct LiveRunner on identical scripted inputs" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    const staging = try std.testing.allocator.create(live_runner.LiveRunnerSnapshot);
    defer std.testing.allocator.destroy(staging);
    staging.runner = try live_runner.LiveRunner.init(.{
        .seed = 1234,
        .game_mode = .survival,
        .player_count = 1,
        .world_size = 1024.0,
    });
    const direct = try std.testing.allocator.create(live_runner.LiveRunner);
    defer std.testing.allocator.destroy(direct);
    direct.restoreSnapshot(staging);

    var abi_result: exports.CrimsonHostTickResult = undefined;
    const total_ticks: usize = 1200; // 20 seconds of survival

    for (0..total_ticks) |tick| {
        const host_input = scriptedInput(tick);
        const inputs = [_]exports.CrimsonHostInput{host_input};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(
            handle,
            &inputs,
            1,
            &abi_result,
        ));

        var frame: live_runner.FrameInput = .{};
        frame.player_count = 1;
        frame.players[0] = .{
            .move_x = host_input.move_x,
            .move_y = host_input.move_y,
            .aim_x = host_input.aim_x,
            .aim_y = host_input.aim_y,
            .flags = .{
                .fire_down = host_input.flags & 1 != 0,
                .fire_pressed = host_input.flags & 2 != 0,
                .reload_pressed = false,
                .move_to_cursor_pressed = host_input.flags & 16 != 0,
                .move_mode = 4,
                .aim_scheme = 0,
            },
        };
        frame.player = frame.players[0];
        _ = try direct.stepFrame(direct.session.dt_nominal, frame);
    }

    const direct_summary = direct.summary();
    try std.testing.expectEqual(direct_summary.player_level, abi_result.player_level);
    try std.testing.expectEqual(direct_summary.player_experience, abi_result.player_experience);
    try std.testing.expectEqual(direct_summary.player_weapon_id, abi_result.player_weapon_id);
    try std.testing.expectEqual(
        @as(u32, @intCast(direct_summary.creature_active_count)),
        abi_result.creature_active_count,
    );
    try std.testing.expectEqual(direct.session.tick_index, @as(usize, total_ticks));

    // Player position must also match exactly (float parity).
    var snap_len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, null, &snap_len));
    const snap_buf = try std.testing.allocator.alloc(u8, snap_len);
    defer std.testing.allocator.free(snap_buf);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, snap_buf.ptr, &snap_len));

    var header: exports.SnapshotHeader = undefined;
    @memcpy(std.mem.asBytes(&header), snap_buf[0..@sizeOf(exports.SnapshotHeader)]);
    try std.testing.expectEqual(exports.snapshot_magic, header.magic);
    try std.testing.expectEqual(@as(u32, 1), header.player_count);

    var player: exports.PlayerSnap = undefined;
    @memcpy(
        std.mem.asBytes(&player),
        snap_buf[@sizeOf(exports.SnapshotHeader)..][0..@sizeOf(exports.PlayerSnap)],
    );
    const direct_player = direct.player0Const().?;
    try std.testing.expectEqual(direct_player.pos.x, player.x);
    try std.testing.expectEqual(direct_player.pos.y, player.y);
    try std.testing.expectEqual(direct_player.health, player.health);
}

test "abi sessions are deterministic across instances" {
    const allocator = std.testing.allocator;
    const handle_a = try createTestSession();
    defer exports.crimson_host_session_destroy(handle_a);
    const handle_b = try createTestSession();
    defer exports.crimson_host_session_destroy(handle_b);

    for (0..600) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle_a, &inputs, 1, null));
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle_b, &inputs, 1, null));
    }

    var len_a: u32 = 0;
    var len_b: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle_a, null, &len_a));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle_b, null, &len_b));
    try std.testing.expectEqual(len_a, len_b);
    try std.testing.expect(len_a > @sizeOf(exports.SnapshotHeader));
    try std.testing.expect(len_a <= exports.snapshotMaxSize());

    const buf_a = try allocator.alloc(u8, len_a);
    defer allocator.free(buf_a);
    const buf_b = try allocator.alloc(u8, len_b);
    defer allocator.free(buf_b);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle_a, buf_a.ptr, &len_a));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle_b, buf_b.ptr, &len_b));
    try std.testing.expectEqualSlices(u8, buf_a, buf_b);
}

test "abi audio events decode after ticking" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    // Tick with fire held until at least one shot event lands.
    var saw_shot = false;
    for (0..240) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));

        var len: u32 = 0;
        try std.testing.expectEqual(exports.ok, exports.crimson_host_audio_events(handle, null, &len));
        try std.testing.expect(len >= @sizeOf(exports.AudioHeader));
        var buf: [4096]u8 = undefined;
        try std.testing.expect(len <= buf.len);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_audio_events(handle, &buf, &len));

        var header: exports.AudioHeader = undefined;
        @memcpy(std.mem.asBytes(&header), buf[0..@sizeOf(exports.AudioHeader)]);
        if (header.shot_count > 0) {
            var shot: exports.ShotAudioSnap = undefined;
            @memcpy(
                std.mem.asBytes(&shot),
                buf[@sizeOf(exports.AudioHeader)..][0..@sizeOf(exports.ShotAudioSnap)],
            );
            try std.testing.expect(shot.weapon_id > 0);
            saw_shot = true;
            break;
        }
    }
    try std.testing.expect(saw_shot);
}

test "abi snapshot exposes sprite-effect particles" {
    const allocator = std.testing.allocator;
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    const buf = try allocator.alloc(u8, exports.snapshotMaxSize());
    defer allocator.free(buf);

    // Firing at creatures spawns sprite effects (bullet casings, blood), so over
    // a firing run the effect pool must be non-empty at some point and decode to
    // sane values. Pins that the ABI v2 particle stream actually carries data.
    var max_particles: u32 = 0;
    var checked_entry = false;
    for (0..900) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));

        var len: u32 = @intCast(buf.len);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, buf.ptr, &len));

        var header: exports.SnapshotHeader = undefined;
        @memcpy(std.mem.asBytes(&header), buf[0..@sizeOf(exports.SnapshotHeader)]);
        if (header.particle_count > max_particles) max_particles = header.particle_count;

        if (header.particle_count > 0 and !checked_entry) {
            // First ParticleSnap sits after all the preceding entity arrays.
            const off = @sizeOf(exports.SnapshotHeader) +
                @sizeOf(exports.PlayerSnap) * header.player_count +
                @sizeOf(exports.CreatureSnap) * header.creature_count +
                @sizeOf(exports.ProjectileSnap) * header.projectile_count +
                @sizeOf(exports.SecondarySnap) * header.secondary_count +
                @sizeOf(exports.BonusSnap) * header.bonus_count;
            var p: exports.ParticleSnap = undefined;
            @memcpy(std.mem.asBytes(&p), buf[off..][0..@sizeOf(exports.ParticleSnap)]);
            try std.testing.expect(p.flags != 0 and p.age >= 0.0);
            try std.testing.expect(p.effect_id >= 0 and p.effect_id <= 0x12);
            checked_entry = true;
        }
    }
    try std.testing.expect(max_particles > 0);
    try std.testing.expect(checked_entry);
}

test "abi creature snapshot carries tint and hit flash" {
    const allocator = std.testing.allocator;
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    const buf = try allocator.alloc(u8, exports.snapshotMaxSize());
    defer allocator.free(buf);

    // Survival creatures spawn with an XP-derived tint (not pure white), and
    // firing at them sets the white hit-flash timer. Over a firing run the ABI
    // v4 creature color + hit_flash_timer must both show up and decode sanely.
    var saw_tint = false;
    var saw_flash = false;
    for (0..900) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));

        var len: u32 = @intCast(buf.len);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, buf.ptr, &len));

        var header: exports.SnapshotHeader = undefined;
        @memcpy(std.mem.asBytes(&header), buf[0..@sizeOf(exports.SnapshotHeader)]);
        const off = @sizeOf(exports.SnapshotHeader) + @sizeOf(exports.PlayerSnap) * header.player_count;
        for (0..header.creature_count) |ci| {
            var c: exports.CreatureSnap = undefined;
            @memcpy(std.mem.asBytes(&c), buf[off + ci * @sizeOf(exports.CreatureSnap) ..][0..@sizeOf(exports.CreatureSnap)]);
            // Tint channels are valid [0,1] multipliers.
            try std.testing.expect(c.r >= 0.0 and c.r <= 1.0);
            try std.testing.expect(c.g >= 0.0 and c.g <= 1.0);
            try std.testing.expect(c.b >= 0.0 and c.b <= 1.0);
            try std.testing.expect(c.a >= 0.0 and c.a <= 1.0);
            if (c.r != 1.0 or c.g != 1.0 or c.b != 1.0) saw_tint = true;
            if (c.hit_flash_timer > 0.0) {
                try std.testing.expect(c.hit_flash_timer <= 0.2 + 1e-4);
                saw_flash = true;
            }
        }
        if (saw_tint and saw_flash) break;
    }
    try std.testing.expect(saw_tint);
    try std.testing.expect(saw_flash);
}

test "abi exposes static terrain info" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    var info: exports.TerrainInfo = undefined;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_terrain_info(handle, &info));
    try std.testing.expectEqual(@as(f32, 1024.0), info.world_size);
    try std.testing.expectEqual(@as(i32, 1024), info.terrain_size);
    // Survival with no unlock uses the default {0,1,0} slot triplet.
    try std.testing.expectEqual(@as(i32, 0), info.terrain_slot_0);
    try std.testing.expectEqual(@as(i32, 1), info.terrain_slot_1);
    try std.testing.expectEqual(@as(i32, 0), info.terrain_slot_2);

    // Info is stable for the life of the session (query-once contract).
    var info2: exports.TerrainInfo = undefined;
    for (0..120) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));
    }
    try std.testing.expectEqual(exports.ok, exports.crimson_host_terrain_info(handle, &info2));
    try std.testing.expectEqual(info.terrain_seed, info2.terrain_seed);
}

test "abi drains terrain fx over a firing run" {
    const allocator = std.testing.allocator;
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    // Terrain FX capacity is bounded (0x7F decals + 0x3F corpses); a 64 KiB
    // scratch buffer comfortably holds a full tick's batch.
    const buf = try allocator.alloc(u8, 64 * 1024);
    defer allocator.free(buf);

    // Firing at creatures produces blood splats and, on kills, corpse stamps.
    // Over a firing run the terrain-fx drain must carry at least one entry that
    // decodes to sane values. Pins that the ABI v3 terrain-fx stream works.
    var saw_fx = false;
    for (0..900) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));

        var len: u32 = @intCast(buf.len);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_terrain_fx(handle, buf.ptr, &len));
        try std.testing.expect(len >= @sizeOf(exports.TerrainFxHeader));

        var header: exports.TerrainFxHeader = undefined;
        @memcpy(std.mem.asBytes(&header), buf[0..@sizeOf(exports.TerrainFxHeader)]);
        try std.testing.expectEqual(exports.abi_version, header.version);

        if (header.decal_count > 0) {
            var d: exports.TerrainDecalSnap = undefined;
            @memcpy(std.mem.asBytes(&d), buf[@sizeOf(exports.TerrainFxHeader)..][0..@sizeOf(exports.TerrainDecalSnap)]);
            try std.testing.expect(d.a > 0.0);
            try std.testing.expect(d.width > 0.0);
            saw_fx = true;
            break;
        }
        if (header.corpse_count > 0) {
            const off = @sizeOf(exports.TerrainFxHeader) +
                @sizeOf(exports.TerrainDecalSnap) * header.decal_count;
            var c: exports.TerrainCorpseSnap = undefined;
            @memcpy(std.mem.asBytes(&c), buf[off..][0..@sizeOf(exports.TerrainCorpseSnap)]);
            try std.testing.expect(c.scale > 0.0);
            saw_fx = true;
            break;
        }
    }
    try std.testing.expect(saw_fx);
}

test "abi rejects invalid handles and configs" {
    var result: exports.CrimsonHostTickResult = undefined;
    const inputs = [_]exports.CrimsonHostInput{scriptedInput(0)};
    try std.testing.expectEqual(
        exports.err_invalid_handle,
        exports.crimson_host_session_tick(0xDEAD_BEEF_0000_0007, &inputs, 1, &result),
    );

    var handle: u64 = 0;
    const bad_json = "{\"game_mode\": 99}";
    try std.testing.expectEqual(
        exports.err_invalid_config,
        exports.crimson_host_session_create(bad_json.ptr, @intCast(bad_json.len), &handle),
    );

    var err_buf: [256]u8 = undefined;
    const err_len = exports.crimson_host_last_error(&err_buf, err_buf.len);
    try std.testing.expect(err_len > 0);

    // Destroyed handles must be rejected afterwards (generation check).
    const live = try createTestSession();
    exports.crimson_host_session_destroy(live);
    try std.testing.expectEqual(
        exports.err_invalid_handle,
        exports.crimson_host_session_tick(live, &inputs, 1, &result),
    );
}
