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
const state_mod = crimson_zig.state;
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

test "abi version reports v21" {
    try std.testing.expectEqual(@as(u32, 21), exports.crimson_host_abi_version());
}

test "recorded replay verifies through the ABI" {
    // THE gate for M4 slice 8. Recording is only worth anything if the bytes it
    // produces pass the same verifier a hand-made .crd goes through, so the
    // oracle is the verifier itself rather than any assertion about the
    // recorder's internals: play a scripted run, finish it, and feed the result
    // straight back into crimson_host_verify_replay_json.
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(handle));

    for (0..600) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(handle, &inputs, 1, null),
        );
    }

    // Size-then-fill, the same protocol as crimson_host_snapshot.
    var size: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, null, &size));
    try std.testing.expect(size > 0);

    const bytes = try std.testing.allocator.alloc(u8, size);
    defer std.testing.allocator.free(bytes);
    var len: u32 = size;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, bytes.ptr, &len));
    try std.testing.expectEqual(size, len);

    var json_len: u32 = 0;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, null, &json_len),
    );
    const json = try std.testing.allocator.alloc(u8, json_len);
    defer std.testing.allocator.free(json);
    var json_out: u32 = json_len;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, json.ptr, &json_out),
    );

    // The verdict lives at header_claim.match, NOT at the top level — the top
    // level only reports whether the verifier ran. A mismatch also names the
    // offending fields, so a failure points straight at the header value or
    // captured input that diverged.
    const Verdict = struct {
        status: []const u8 = "",
        header_claim: struct {
            match: bool = false,
            mismatched_fields: []const []const u8 = &.{},
        } = .{},
    };
    const parsed = try std.json.parseFromSlice(
        Verdict,
        std.testing.allocator,
        json[0..json_out],
        .{ .ignore_unknown_fields = true },
    );
    defer parsed.deinit();
    if (!parsed.value.header_claim.match) {
        std.debug.print("verify rejected the recording:\n{s}\n", .{json[0..json_out]});
    }
    try std.testing.expectEqualStrings("ok", parsed.value.status);
    try std.testing.expect(parsed.value.header_claim.match);
}

test "recorded replay verifies over a realistic run length" {
    // Length is the point. This gate failed on shots_hit by exactly one while
    // the 600-tick gate passed, and the cause was not length-sensitive code but
    // a corrupt header that needs time to matter: recordingHeaderFor sliced a
    // by-value copy of the config, so every recording carried a weapon-usage
    // array made of dead stack. Usage counts reroll weapon drops, so the replay
    // eventually took a different weapon than the run and desynced — at tick
    // 1021 here, well past where a short gate stops looking.
    //
    // Keep it long. A recorder can be perfectly faithful about inputs (this one
    // is, bit for bit) and still produce replays that diverge, and the gap only
    // opens once the run is long enough for one rerolled decision to land.
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(handle));

    var result: exports.CrimsonHostTickResult = undefined;
    for (0..4000) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(handle, &inputs, 1, &result),
        );
    }

    var size: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, null, &size));
    const bytes = try std.testing.allocator.alloc(u8, size);
    defer std.testing.allocator.free(bytes);
    var len: u32 = size;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, bytes.ptr, &len));

    var json_len: u32 = 0;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, null, &json_len),
    );
    const json = try std.testing.allocator.alloc(u8, json_len);
    defer std.testing.allocator.free(json);
    var json_out: u32 = json_len;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, json.ptr, &json_out),
    );

    const Verdict = struct {
        header_claim: struct { match: bool = false } = .{},
    };
    const parsed = try std.json.parseFromSlice(
        Verdict,
        std.testing.allocator,
        json[0..json_out],
        .{ .ignore_unknown_fields = true },
    );
    defer parsed.deinit();
    if (!parsed.value.header_claim.match) {
        std.debug.print("long-run recording rejected:\n{s}\n", .{json[0..json_out]});
    }
    try std.testing.expect(parsed.value.header_claim.match);
}

test "recorded replay verifies with weapon usage history" {
    // The case every previous gate missed. Persisted usage counts reroll weapon
    // drops, so a session started with history takes a different weapon stream
    // than one started fresh -- and the encoder used to substitute an all-zero
    // array for the header whenever the runtime's 54-slot status array failed an
    // equality check against the wire's 53. Every recording therefore claimed a
    // fresh save, and real VR replays diverged on exactly the weapon-shaped
    // fields (kills, shots, xp, most_used_weapon_id) while ticks stayed exact.
    //
    // Scripted gates all passed because their config had no usage history.
    const config_with_usage =
        \\{"seed": 1234, "game_mode": 1, "player_count": 1, "world_size": 1024.0,
        \\ "status_weapon_usage_counts": [0,0,1,0,0,1,1,0,0,0,1,2,1,0,1,0,0,2,0,0,
        \\ 0,0,3,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]}
    ;
    var handle: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_session_create(
        config_with_usage.ptr,
        @intCast(config_with_usage.len),
        &handle,
    ));
    defer exports.crimson_host_session_destroy(handle);

    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(handle));
    for (0..600) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(handle, &inputs, 1, null),
        );
    }

    var size: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, null, &size));
    const bytes = try std.testing.allocator.alloc(u8, size);
    defer std.testing.allocator.free(bytes);
    var len: u32 = size;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, bytes.ptr, &len));

    var json_len: u32 = 0;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, null, &json_len),
    );
    const json = try std.testing.allocator.alloc(u8, json_len);
    defer std.testing.allocator.free(json);
    var json_out: u32 = json_len;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_verify_replay_json(bytes.ptr, len, json.ptr, &json_out),
    );

    const Verdict = struct {
        header_claim: struct { match: bool = false } = .{},
    };
    const parsed = try std.json.parseFromSlice(
        Verdict,
        std.testing.allocator,
        json[0..json_out],
        .{ .ignore_unknown_fields = true },
    );
    defer parsed.deinit();
    if (!parsed.value.header_claim.match) {
        std.debug.print("usage-history recording rejected:\n{s}\n", .{json[0..json_out]});
    }
    try std.testing.expect(parsed.value.header_claim.match);

    // Read the header back and check it against what the session was configured
    // with. Verifying is NOT enough on its own: the header is what the replay is
    // re-simulated FROM, so a corrupt value is applied to both sides of the
    // comparison and a short run still matches. This is the assertion that
    // caught the encoder being handed a slice of dead stack — a usage array made
    // of the encoder's own locals, which rerolled weapon drops and desynced
    // longer runs while every short gate stayed green.
    const replay = try crimson_zig.replay_codec.parseReplay(std.testing.allocator, bytes[0..len]);
    defer replay.deinit(std.testing.allocator);
    const expected_usage = [_]u32{
        0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 1, 2, 1, 0, 1, 0, 0, 2, 0, 0,
        0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };
    try std.testing.expectEqualSlices(u32, &expected_usage, &replay.header.status.weapon_usage_counts);
    try std.testing.expectEqual(@as(u32, 1234), replay.header.seed);
    try std.testing.expectEqual(@as(i32, 5), replay.header.detail_preset);
    try std.testing.expectEqual(@as(f32, 1024.0), replay.header.world_size);
}

test "replay finish refuses a session that was never recording" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    var inputs = [_]exports.CrimsonHostInput{scriptedInput(0)};
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_session_tick(handle, &inputs, 1, null),
    );

    // Emitting an empty replay would produce a file that fails verification
    // later, for a reason far removed from the missing begin call.
    var size: u32 = 0;
    try std.testing.expect(exports.crimson_host_replay_finish(handle, null, &size) != exports.ok);
}

test "replay finish reports an empty recording as size zero, not an error" {
    // The other half of the rule above: recording ON but nothing captured is a
    // session that ended before it ticked. The frontend hits this every time a
    // run is started from the menu, because it finishes the outgoing session on
    // the way out — and while that was an error it logged one line per visit.
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(handle));

    var size: u32 = 123; // must be overwritten, not left as the caller set it
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_finish(handle, null, &size));
    try std.testing.expectEqual(@as(u32, 0), size);
}

test "abi player snapshot carries the death timer" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    var inputs = [_]exports.CrimsonHostInput{scriptedInput(0)};
    var buf: [1 << 20]u8 = undefined;
    var len: u32 = @intCast(buf.len);

    // Alive: death_timer sits at its 16.0 reset value.
    try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, &buf, &len));
    const header = std.mem.bytesToValue(exports.SnapshotHeader, buf[0..@sizeOf(exports.SnapshotHeader)]);
    try std.testing.expect(header.player_count >= 1);
    const player = std.mem.bytesToValue(
        exports.PlayerSnap,
        buf[@sizeOf(exports.SnapshotHeader)..][0..@sizeOf(exports.PlayerSnap)],
    );
    try std.testing.expectEqual(@as(f32, 16.0), player.death_timer);
    // ABI v19 bonus-HUD fields: all timers idle at spawn.
    try std.testing.expectEqual(@as(f32, 0.0), player.fire_bullets_timer);
    try std.testing.expectEqual(@as(f32, 0.0), player.speed_bonus_timer);
    try std.testing.expectEqual(@as(f32, 0.0), player.aux_timer);
    try std.testing.expectEqual(@as(f32, 0.0), header.weapon_power_up_timer);
    try std.testing.expectEqual(@as(f32, 0.0), header.reflex_boost_timer);
    try std.testing.expectEqual(@as(f32, 0.0), header.double_experience_timer);
}

test "dead run keeps simulating and drains the death timer" {
    // Reference behavior (gameplay.py player_update / survival_mode): a dead
    // player only drains death_timer while the rest of the world keeps
    // ticking; the game-over transition waits for the timer. The runner used
    // to freeze the whole frame on allPlayersDead, stalling the VR death
    // cinematic on its first corpse frame. Pinned here because the
    // live_runner.zig inline tests are not yet collected by `zig build test`.
    var runner = try crimson_zig.live_runner.LiveRunner.init(.{});
    runner.session.players()[0].health = 0.0;

    const update = try runner.stepFrame(runner.session.dt_nominal, .{});
    try std.testing.expect(update.all_players_dead);
    try std.testing.expectEqual(@as(usize, 1), update.ticks_advanced);
    try std.testing.expect(runner.session.players()[0].death_timer < 16.0);

    // The corpse ramp completes: ~1s of ticks takes the timer below zero.
    for (0..70) |_| {
        _ = try runner.stepFrame(runner.session.dt_nominal, .{});
    }
    try std.testing.expect(runner.session.players()[0].death_timer < 0.0);
}

test "lifecycle-killed creatures die through the ramp with hp remaining" {
    // Breathing Room kills every creature by nudging lifecycle_stage below the
    // alive sentinel while hp stays untouched. The creature update loop must
    // route stage != alive into the dead path like the reference
    // (creature_update_all: not alive OR hp <= 0) — it used to gate on hp
    // only, leaving Breathing-Room'd creatures alive forever in a sub-alive
    // stage (rendered flat on the ground in VR, still milling). Pinned here
    // because the creatures.zig inline tests are not yet collected by
    // `zig build test`.
    var runner = try crimson_zig.live_runner.LiveRunner.init(.{});
    const creature = &runner.session.creatures.entries[0];
    creature.* = .{
        .active = true,
        .presentation_generation = 1,
        .type_id = 0,
        .pos = .{ .x = 200.0, .y = 200.0 },
        .size = 44.0,
        .hp = 40.0,
        .max_hp = 40.0,
        .lifecycle_stage = 15.98,
    };

    _ = try runner.stepFrame(runner.session.dt_nominal, .{});
    // Dead path ran this tick: the ramp drains at 28/s; an alive creature's
    // stage would not have moved at all.
    try std.testing.expect(runner.session.creatures.entries[0].lifecycle_stage < 15.98 - 0.2);
    try std.testing.expect(runner.session.creatures.entries[0].hp > 0.0);

    // The ramp completes into the corpse fade within ~1.5s of ticks.
    for (0..90) |_| {
        _ = try runner.stepFrame(runner.session.dt_nominal, .{});
    }
    try std.testing.expect(runner.session.creatures.entries[0].lifecycle_stage < 0.0);
}

test "abi weapon usage counts: seeded via config, queryable" {
    // Seed shotgun (id 3) with 7 prior uses. Spawn does NOT increment usage
    // (resetPlayers hands out the 10-round pistol without going through
    // weapon assignment — native parity; only pickups increment), so right
    // after create the query must return the seeded values verbatim.
    const config_json =
        \\{"seed": 99, "game_mode": 1, "player_count": 1, "world_size": 1024.0,
        \\ "tick_rate": 60,
        \\ "status_weapon_usage_counts": [0,0,0,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        \\  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        \\  0,0,0,0]}
    ;
    var handle: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_session_create(
        config_json.ptr,
        @intCast(config_json.len),
        &handle,
    ));
    defer exports.crimson_host_session_destroy(handle);

    // Size query.
    const needed = exports.crimson_host_status_weapon_usage(handle, null, 0);
    try std.testing.expectEqual(-@as(i32, @intCast(state_mod.weapon_count_size)), needed);

    var counts: [state_mod.weapon_count_size]u32 = undefined;
    const written = exports.crimson_host_status_weapon_usage(handle, &counts, counts.len);
    try std.testing.expectEqual(@as(i32, @intCast(state_mod.weapon_count_size)), written);
    try std.testing.expectEqual(@as(u32, 7), counts[3]); // seeded roundtrip
    try std.testing.expectEqual(@as(u32, 0), counts[1]); // spawn pistol does NOT count
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

test "abi snapshot exposes the sprite-effect pool stream" {
    const allocator = std.testing.allocator;
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    const buf = try allocator.alloc(u8, exports.snapshotMaxSize());
    defer allocator.free(buf);

    // Every pistol shot spawns two muzzle-puff sprite effects
    // (spawnNativeFireMuzzleSprites), so a firing run must surface entries in
    // the ABI v13 sprite-effect stream, packed after the glow pool.
    var max_sprites: u32 = 0;
    var checked_entry = false;
    for (0..900) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, null));

        var len: u32 = @intCast(buf.len);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_snapshot(handle, buf.ptr, &len));

        var header: exports.SnapshotHeader = undefined;
        @memcpy(std.mem.asBytes(&header), buf[0..@sizeOf(exports.SnapshotHeader)]);
        if (header.sprite_effect_count > max_sprites) max_sprites = header.sprite_effect_count;

        if (header.sprite_effect_count > 0 and !checked_entry) {
            const off = @sizeOf(exports.SnapshotHeader) +
                @sizeOf(exports.PlayerSnap) * header.player_count +
                @sizeOf(exports.CreatureSnap) * header.creature_count +
                @sizeOf(exports.ProjectileSnap) * header.projectile_count +
                @sizeOf(exports.SecondarySnap) * header.secondary_count +
                @sizeOf(exports.BonusSnap) * header.bonus_count +
                @sizeOf(exports.ParticleSnap) * header.particle_count +
                @sizeOf(exports.ParticleGlowSnap) * header.glow_count;
            var s: exports.SpriteEffectSnap = undefined;
            @memcpy(std.mem.asBytes(&s), buf[off..][0..@sizeOf(exports.SpriteEffectSnap)]);
            try std.testing.expect(s.scale > 0.0);
            try std.testing.expect(s.a >= 0.0 and s.a <= 1.0);
            try std.testing.expect(s.x >= -128.0 and s.x <= 1152.0);
            checked_entry = true;
        }
    }
    try std.testing.expect(max_sprites > 0);
    try std.testing.expect(checked_entry);
}

test "abi tick result carries kill count and most-used weapon" {
    const handle = try createTestSession();
    defer exports.crimson_host_session_destroy(handle);

    // Over a scripted firing run creatures die, so the ABI v10 stats must show
    // kills, and the most-used weapon must be the weapon that did the firing
    // (the run never swaps weapons, so it matches the current weapon id).
    var result: exports.CrimsonHostTickResult = undefined;
    for (0..900) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, &result));
    }
    try std.testing.expect(result.creature_kill_count > 0);
    try std.testing.expect(result.shots_fired > 0);
    try std.testing.expectEqual(result.player_weapon_id, result.most_used_weapon_id);
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
            try std.testing.expect(c.pool_index >= 0);
            try std.testing.expect(c.pool_index < crimson_zig.creatures.max_creatures);
            try std.testing.expect(c.generation > 0);
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
