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

test "abi version reports v27" {
    try std.testing.expectEqual(@as(u32, 27), exports.crimson_host_abi_version());
}

const NetworkStatus = struct {
    phase: []const u8,
    local_slot: i32,
    bound_port: u16,
    connected: usize,
    expected: usize,
    ready: usize,
    started: bool,
    mode_id: i32,
    failure: []const u8,
};

fn readNetworkStatus(handle: u64) !std.json.Parsed(NetworkStatus) {
    var len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_status(handle, null, &len));
    const bytes = try std.testing.allocator.alloc(u8, len);
    defer std.testing.allocator.free(bytes);
    var written = len;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_status(handle, bytes.ptr, &written));
    return std.json.parseFromSlice(NetworkStatus, std.testing.allocator, bytes[0..written], .{
        .ignore_unknown_fields = true,
        .allocate = .alloc_always,
    });
}

fn expectNetOk(rc: i32) !void {
    if (rc == exports.ok) return;
    var error_buf: [1024]u8 = undefined;
    const error_len = exports.crimson_host_last_error(&error_buf, error_buf.len);
    if (error_len > 0) std.debug.print("network ABI error ({d}): {s}\n", .{ rc, error_buf[0..@intCast(error_len)] });
    try std.testing.expectEqual(exports.ok, rc);
}

fn setNetworkReady(handle: u64, player_index: i32) !void {
    try expectNetOk(exports.crimson_host_net_command(
        handle,
        exports.net_command_set_ready,
        player_index,
        1,
    ));
}

test "network ABI validates config and destroys a waiting host" {
    try std.testing.expectEqual(@as(usize, 60), @sizeOf(exports.CrimsonHostNetUpdate));

    const invalid =
        \\{"role":"spectator","netcode":"lockstep"}
    ;
    var invalid_handle: u64 = 0;
    try std.testing.expectEqual(exports.err_invalid_config, exports.crimson_host_net_create(invalid.ptr, invalid.len, &invalid_handle));

    const host_config =
        \\{"role":"host","netcode":"lockstep","seed":17,"mode_id":1,"player_count":2,"port":0,"build_id":"abi-loopback"}
    ;
    var host: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(host_config.ptr, host_config.len, &host));
    const status = try readNetworkStatus(host);
    defer status.deinit();
    try std.testing.expect(status.value.bound_port != 0);
    try std.testing.expectEqual(@as(usize, 2), status.value.expected);
    try std.testing.expectEqual(@as(i32, 0), status.value.local_slot);
    try std.testing.expectEqual(@as(i32, 1), status.value.mode_id);
    var wrong_api_len: u32 = 0;
    try std.testing.expectEqual(exports.err_invalid_handle, exports.crimson_host_snapshot(host, null, &wrong_api_len));
    exports.crimson_host_net_destroy(host);
    try std.testing.expectEqual(exports.err_invalid_handle, exports.crimson_host_net_update(host, 1, null, null));
}

test "network ABI resolves DNS relay and lockstep hostnames" {
    const lockstep =
        \\{"role":"join","netcode":"lockstep","seed":1,"mode_id":1,"player_count":2,"host":"localhost","port":31993,"build_id":"dns-test"}
    ;
    var lockstep_handle: u64 = 0;
    try expectNetOk(exports.crimson_host_net_create(lockstep.ptr, lockstep.len, &lockstep_handle));
    exports.crimson_host_net_destroy(lockstep_handle);

    const relay =
        \\{"role":"join","netcode":"rollback","seed":1,"mode_id":1,"player_count":2,"host":"localhost","port":31993,"room_code":"a1b2","build_id":"dns-test"}
    ;
    var relay_handle: u64 = 0;
    try expectNetOk(exports.crimson_host_net_create(relay.ptr, relay.len, &relay_handle));
    exports.crimson_host_net_destroy(relay_handle);
}

test "network ABI lockstep loopback stays deterministic for 10000 ticks" {
    const host_config =
        \\{"role":"host","netcode":"lockstep","seed":4660,"mode_id":1,"player_count":2,"port":0,"build_id":"abi-loopback","session_id":"abi-loopback"}
    ;
    var host: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(host_config.ptr, host_config.len, &host));
    defer exports.crimson_host_net_destroy(host);

    const host_status = try readNetworkStatus(host);
    const port = host_status.value.bound_port;
    host_status.deinit();
    var client_config_buf: [256]u8 = undefined;
    const client_config = try std.fmt.bufPrint(&client_config_buf, "{{\"role\":\"join\",\"netcode\":\"lockstep\",\"seed\":4660,\"mode_id\":1,\"player_count\":2,\"host\":\"127.0.0.1\",\"port\":{d},\"build_id\":\"abi-loopback\",\"session_id\":\"abi-loopback\"}}", .{port});
    var client: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(client_config.ptr, @intCast(client_config.len), &client));
    defer exports.crimson_host_net_destroy(client);

    const input: exports.CrimsonHostInput = .{
        .move_x = 0.25,
        .move_y = -0.5,
        .aim_x = 700.0,
        .aim_y = 300.0,
        .flags = 1,
        .move_mode = -1,
        .aim_scheme = -1,
        .perk_choice_index = -1,
        .perk_menu_active = 0,
    };
    var host_update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    var client_update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    var now_ms: i64 = 1;
    var pumps: usize = 0;
    var host_ready = false;
    var client_ready = false;
    while (host_update.phase != exports.net_phase_running or client_update.phase != exports.net_phase_running) : (pumps += 1) {
        if (pumps > 2000) {
            const timed_out_host = try readNetworkStatus(host);
            defer timed_out_host.deinit();
            const timed_out_client = try readNetworkStatus(client);
            defer timed_out_client.deinit();
            std.debug.print("loopback lobby timeout: host={any} client={any}\n", .{ timed_out_host.value, timed_out_client.value });
            return error.NetworkLobbyTimeout;
        }
        try expectNetOk(exports.crimson_host_net_update(host, now_ms, null, &host_update));
        try expectNetOk(exports.crimson_host_net_update(client, now_ms, null, &client_update));
        if (!host_ready and host_update.local_slot == 0) {
            try setNetworkReady(host, 0);
            host_ready = true;
        }
        if (!client_ready and client_update.local_slot == 1) {
            try setNetworkReady(client, 1);
            client_ready = true;
        }
        now_ms += 1;
    }
    try std.testing.expectEqual(@as(i32, 0), host_update.local_slot);
    try std.testing.expectEqual(@as(i32, 1), client_update.local_slot);

    var host_tick: i32 = -1;
    var client_tick: i32 = -1;
    pumps = 0;
    while (host_tick < 9_999 or client_tick < 9_999) : (pumps += 1) {
        if (pumps > 30_000) return error.NetworkTickTimeout;
        try std.testing.expectEqual(exports.ok, exports.crimson_host_net_update(host, now_ms, &input, &host_update));
        try std.testing.expectEqual(exports.ok, exports.crimson_host_net_update(client, now_ms, &input, &client_update));
        now_ms += 1;
        if (host_update.last_tick_index >= 0) host_tick = host_update.last_tick_index;
        if (client_update.last_tick_index >= 0) client_tick = client_update.last_tick_index;
    }

    while (host_tick != client_tick) {
        if (host_tick < client_tick) {
            try std.testing.expectEqual(exports.ok, exports.crimson_host_net_update(host, now_ms, null, &host_update));
            if (host_update.last_tick_index >= 0) host_tick = host_update.last_tick_index;
        } else {
            try std.testing.expectEqual(exports.ok, exports.crimson_host_net_update(client, now_ms, null, &client_update));
            if (client_update.last_tick_index >= 0) client_tick = client_update.last_tick_index;
        }
        now_ms += 1;
    }
    try std.testing.expect(host_tick >= 9_999);
    try std.testing.expectEqual(@as(i32, 1), host_update.game_mode);
    try std.testing.expectEqual(@as(i32, 1), client_update.game_mode);
    try std.testing.expect(host_update.local_shots_fired > 0);
    try std.testing.expect(client_update.local_shots_fired > 0);
    try std.testing.expectEqual(host_update.creature_kill_count, client_update.creature_kill_count);

    var host_len: u32 = 0;
    var client_len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(host, null, &host_len));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(client, null, &client_len));
    try std.testing.expectEqual(host_len, client_len);
    const host_bytes = try std.testing.allocator.alloc(u8, host_len);
    defer std.testing.allocator.free(host_bytes);
    const client_bytes = try std.testing.allocator.alloc(u8, client_len);
    defer std.testing.allocator.free(client_bytes);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(host, host_bytes.ptr, &host_len));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(client, client_bytes.ptr, &client_len));
    try std.testing.expectEqualSlices(u8, host_bytes, client_bytes);
}

test "network ABI twin headless hosts stay deterministic for 10000 ticks" {
    const config =
        \\{"role":"host","netcode":"lockstep","seed":4660,"mode_id":1,"player_count":1,"port":0,"build_id":"abi-headless","session_id":"abi-headless","max_recv_packets":0}
    ;
    var left: u64 = 0;
    var right: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(config.ptr, config.len, &left));
    defer exports.crimson_host_net_destroy(left);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(config.ptr, config.len, &right));
    defer exports.crimson_host_net_destroy(right);
    try setNetworkReady(left, 0);
    try setNetworkReady(right, 0);

    const input: exports.CrimsonHostInput = .{
        .move_x = 0.25,
        .move_y = -0.5,
        .aim_x = 700.0,
        .aim_y = 300.0,
        .flags = 1,
        .move_mode = -1,
        .aim_scheme = -1,
        .perk_choice_index = -1,
        .perk_menu_active = 0,
    };
    var left_update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    var right_update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    for (0..10_000) |tick| {
        try expectNetOk(exports.crimson_host_net_update(left, @intCast(tick), &input, &left_update));
        try expectNetOk(exports.crimson_host_net_update(right, @intCast(tick), &input, &right_update));
        try std.testing.expectEqual(left_update.last_tick_index, right_update.last_tick_index);
        try std.testing.expectEqual(left_update.input_flags, right_update.input_flags);
    }
    try std.testing.expectEqual(@as(i32, 9_999), left_update.last_tick_index);

    var left_len: u32 = 0;
    var right_len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(left, null, &left_len));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(right, null, &right_len));
    try std.testing.expectEqual(left_len, right_len);
    const left_bytes = try std.testing.allocator.alloc(u8, left_len);
    defer std.testing.allocator.free(left_bytes);
    const right_bytes = try std.testing.allocator.alloc(u8, right_len);
    defer std.testing.allocator.free(right_bytes);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(left, left_bytes.ptr, &left_len));
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(right, right_bytes.ptr, &right_len));
    try std.testing.expectEqualSlices(u8, left_bytes, right_bytes);

    var audio_len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_audio_events(left, null, &audio_len));
    try std.testing.expect(audio_len > @sizeOf(exports.AudioHeader));
    const audio = try std.testing.allocator.alloc(u8, audio_len);
    defer std.testing.allocator.free(audio);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_audio_events(left, audio.ptr, &audio_len));
    var drained_len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_audio_events(left, null, &drained_len));
    try std.testing.expectEqual(@as(u32, @sizeOf(exports.AudioHeader)), drained_len);
}

test "network ABI runs Rush and reports local-slot results" {
    const config =
        \\{"role":"host","netcode":"lockstep","seed":99,"mode_id":2,"player_count":1,"port":0,"build_id":"abi-rush","session_id":"abi-rush","max_recv_packets":0}
    ;
    var handle: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_create(config.ptr, config.len, &handle));
    defer exports.crimson_host_net_destroy(handle);
    try setNetworkReady(handle, 0);

    const input: exports.CrimsonHostInput = .{
        .move_x = 0.0,
        .move_y = 0.0,
        .aim_x = 512.0,
        .aim_y = 0.0,
        .flags = 1,
        .move_mode = -1,
        .aim_scheme = -1,
        .perk_choice_index = -1,
        .perk_menu_active = 0,
    };
    var update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    for (0..600) |tick| {
        try expectNetOk(exports.crimson_host_net_update(handle, @intCast(tick), &input, &update));
    }
    try std.testing.expectEqual(exports.net_phase_running, update.phase);
    try std.testing.expectEqual(@as(i32, 2), update.game_mode);
    try std.testing.expect(update.local_shots_fired > 0);

    var len: u32 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(handle, null, &len));
    const bytes = try std.testing.allocator.alloc(u8, len);
    defer std.testing.allocator.free(bytes);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_net_snapshot(handle, bytes.ptr, &len));
    var header: exports.SnapshotHeader = undefined;
    @memcpy(std.mem.asBytes(&header), bytes[0..@sizeOf(exports.SnapshotHeader)]);
    try std.testing.expectEqual(@as(i32, 2), header.game_mode);
}

test "network ABI records every canonical player row into a verifiable replay" {
    const config =
        \\{"role":"host","netcode":"lockstep","seed":321,"mode_id":1,"player_count":1,"port":0,"build_id":"abi-replay","session_id":"abi-replay","max_recv_packets":0}
    ;
    var handle: u64 = 0;
    try expectNetOk(exports.crimson_host_net_create(config.ptr, config.len, &handle));
    defer exports.crimson_host_net_destroy(handle);
    try setNetworkReady(handle, 0);
    try expectNetOk(exports.crimson_host_replay_begin(handle));

    var update = std.mem.zeroes(exports.CrimsonHostNetUpdate);
    for (0..600) |tick| {
        const input = scriptedInput(tick);
        try expectNetOk(exports.crimson_host_net_update(handle, @intCast(tick), &input, &update));
    }
    try std.testing.expectEqual(@as(i32, 599), update.last_tick_index);

    var recording: u64 = 0;
    try expectNetOk(exports.crimson_host_replay_detach(handle, &recording));
    defer _ = exports.crimson_host_recording_destroy(recording);
    try std.testing.expect(recording != 0);
    var len: u32 = 0;
    try expectNetOk(exports.crimson_host_recording_encode(recording, null, &len));
    const bytes = try std.testing.allocator.alloc(u8, len);
    defer std.testing.allocator.free(bytes);
    try expectNetOk(exports.crimson_host_recording_encode(recording, bytes.ptr, &len));
    var report_len: u32 = 0;
    try expectNetOk(exports.crimson_host_verify_replay_json(bytes.ptr, len, null, &report_len));
    const report = try std.testing.allocator.alloc(u8, report_len);
    defer std.testing.allocator.free(report);
    try expectNetOk(exports.crimson_host_verify_replay_json(bytes.ptr, len, report.ptr, &report_len));
    try std.testing.expect(std.mem.indexOf(u8, report[0..report_len], "\"status\":\"ok\"") != null);
}

test "tutorial presentation state crosses the tick ABI" {
    const config =
        \\{"seed":7,"game_mode":8,"player_count":1,"world_size":1024.0,"tick_rate":60}
    ;
    var handle: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_session_create(config.ptr, config.len, &handle));
    defer exports.crimson_host_session_destroy(handle);

    var result: exports.CrimsonHostTickResult = undefined;
    var saw_prompt = false;
    for (0..180) |tick| {
        const inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(exports.ok, exports.crimson_host_session_tick(handle, &inputs, 1, &result));
        saw_prompt = saw_prompt or result.tutorial_stage_index >= 0;
    }
    try std.testing.expect(saw_prompt);
    try std.testing.expect(result.tutorial_prompt_alpha >= 0.0 and result.tutorial_prompt_alpha <= 1.0);
    try std.testing.expect(result.tutorial_hint_alpha >= 0.0 and result.tutorial_hint_alpha <= 1.0);
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
    //
    // KNOW WHAT THIS DOES NOT COVER. The scripted player dies around tick 1100
    // and the remaining ~2900 ticks simulate a corpse, so "4000 ticks" is not
    // 4000 ticks of gameplay. It finishes on 144 xp against a level-2 threshold
    // of 2000, which means no level-up, no perk offer and no pick -- the entire
    // perk path is untested here, and that is exactly where a query was found
    // rolling the offer between ticks. Attempts to script a bot that survives
    // to level 2 all died inside ~1150 ticks; until one exists, the perk flow is
    // covered by the invariant test above and by real in-headset runs.
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
    const payload = try crimson_zig.replay_codec.inflateZstdFilePayload(
        std.testing.allocator,
        bytes[0..len],
        crimson_zig.replay_codec.max_replay_payload_bytes,
    );
    defer std.testing.allocator.free(payload);
    const replay = try crimson_zig.replay_codec.parseReplay(std.testing.allocator, payload);
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

test "claimed shot counts follow the verifier, not the scoreboard" {
    // The live runner answers "how many projectiles left the barrel, and how
    // many hits did anyone land" (shots_fired_total, every hit slot summed).
    // The verifier answers "how many shots did player 0 take, and how many of
    // THOSE hit" (per-player slots, hits clamped to fired). Both are reasonable;
    // they are just different questions, and a multi-projectile weapon splits
    // them wide open -- a real run claimed 4862 shots against 677 re-simulated
    // while hits matched to within 1%, which looks like a catastrophic
    // divergence and is nothing of the sort.
    //
    // Every scripted gate here misses it: they fire a pistol, where one trigger
    // pull is one projectile and the two counts coincide.
    const staging = try std.testing.allocator.create(live_runner.LiveRunnerSnapshot);
    defer std.testing.allocator.destroy(staging);
    staging.runner = try live_runner.LiveRunner.init(.{
        .seed = 1234,
        .game_mode = .survival,
        .player_count = 1,
        .world_size = 1024.0,
    });
    const runner = try std.testing.allocator.create(live_runner.LiveRunner);
    defer std.testing.allocator.destroy(runner);
    runner.restoreSnapshot(staging);

    // Pull the two apart the way a shotgun does: many projectiles, few pulls.
    const st = &runner.session.state;
    st.shots_fired_total = 700;
    st.shots_fired[0] = 100;
    st.shots_hit[0] = 40;
    if (st.shots_hit.len > 1) st.shots_hit[1] = 9; // another slot the sum would swallow

    const claim = exports.claimedShots(&runner.session);
    try std.testing.expectEqual(@as(i32, 100), claim.fired);
    try std.testing.expectEqual(@as(i32, 40), claim.hit);

    // And the scoreboard keeps its own answer -- this is not a change to what
    // the player is shown, only to what the recording claims.
    const update = try runner.stepFrame(0.0, .{});
    try std.testing.expectEqual(@as(i32, 700), update.shots_fired);
    try std.testing.expectEqual(@as(i32, 49), update.shots_hit);
}

test "reading perk choices must not advance the rng" {
    // The snapshot the frontend polls EVERY FRAME reports the pending perk
    // offer. Generating that offer draws from the sim rng, so if the read is
    // what triggers generation, a query has just moved the deterministic stream
    // -- outside the tick sequence a replay reproduces. The replay generates its
    // offer when it reaches the recorded perk_menu_open event instead, so the
    // two streams part company at the player's first level-up and never rejoin.
    //
    // This is invisible to every scripted gate here because none of them ever
    // levels up: the 4000-tick run finishes on 144 xp against a 2000 threshold,
    // so pending_count stays 0 and the offer is never read.
    const staging = try std.testing.allocator.create(live_runner.LiveRunnerSnapshot);
    defer std.testing.allocator.destroy(staging);
    staging.runner = try live_runner.LiveRunner.init(.{
        .seed = 1234,
        .game_mode = .survival,
        .player_count = 1,
        .world_size = 1024.0,
    });
    const runner = try std.testing.allocator.create(live_runner.LiveRunner);
    defer std.testing.allocator.destroy(runner);
    runner.restoreSnapshot(staging);

    // Stand in for a level-up: an offer is owed and has not been rolled yet.
    runner.session.state.perk_selection.pending_count = 1;
    runner.session.state.perk_selection.choices_dirty = true;
    runner.session.state.perk_selection.choice_count = 0;

    const before = runner.session.state.rng.state;
    _ = runner.preparedPerkChoices();
    try std.testing.expectEqual(before, runner.session.state.rng.state);

    // The accessor the snapshot path uses; this is the one that generates.
    _ = runner.currentPerkChoices();
    const after = runner.session.state.rng.state;
    try std.testing.expect(after != before);
}

test "a detached recording outlives its session and still verifies" {
    // The point of detaching: the frame cost of ending a run stops growing with
    // the run. Encoding is one msgpack row per tick, so inline it stalls by an
    // amount proportional to match length -- and making it merely faster only
    // moves the cliff. Detach is O(1), and the encode then happens off the main
    // thread while the session is already gone.
    //
    // So this destroys the session FIRST and encodes afterwards. If the
    // recording ever went back to borrowing anything the session owns -- its
    // config, most likely, which is where the weapon-usage header comes from --
    // this reads freed memory instead of failing politely.
    const handle = try createTestSession();
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(handle));
    for (0..600) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(handle, &inputs, 1, null),
        );
    }

    var recording: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_detach(handle, &recording));
    try std.testing.expect(recording != 0);

    // One live rng sample per recorded tick, so the sidecar and the replay's
    // tick stream index the same way. A mismatch here would make every bisect
    // point at the wrong tick, which is worse than having no bisect at all.
    var rng_len: u32 = 0;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_recording_rng(recording, null, &rng_len),
    );
    try std.testing.expectEqual(@as(u32, 600 * @sizeOf(u32)), rng_len);

    exports.crimson_host_session_destroy(handle);

    var size: u32 = 0;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_recording_encode(recording, null, &size),
    );
    try std.testing.expect(size > 0);
    const bytes = try std.testing.allocator.alloc(u8, size);
    defer std.testing.allocator.free(bytes);
    var len: u32 = size;
    try std.testing.expectEqual(
        exports.ok,
        exports.crimson_host_recording_encode(recording, bytes.ptr, &len),
    );
    try std.testing.expectEqual(exports.ok, exports.crimson_host_recording_destroy(recording));
    // The handle is dead now, and using it again must not touch freed memory.
    try std.testing.expect(exports.crimson_host_recording_destroy(recording) != exports.ok);

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
        std.debug.print("detached recording rejected:\n{s}\n", .{json[0..json_out]});
    }
    try std.testing.expect(parsed.value.header_claim.match);
}

test "detach hands the rows over and leaves the session holding none" {
    // Detach moves the ArrayLists by value. If it forgot to reset the session's
    // own, the session would still point at rows now owned by a recording being
    // encoded on another thread -- a double free at best.
    //
    // Modelled on the real sequence, which is detach-then-restart: the frontend
    // recreates the session per run (SimSession.Restart), so a fresh capture
    // always begins at tick 0. Calling begin again mid-run instead is what the
    // short-recording guard exists to refuse, since the claimed stats would
    // cover ticks the rows do not.
    const first_handle = try createTestSession();
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(first_handle));
    for (0..30) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(first_handle, &inputs, 1, null),
        );
    }
    var first: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_detach(first_handle, &first));
    try std.testing.expect(first != 0);

    // Detach stops recording, so a second one must refuse rather than hand back
    // the empty tail of a run already given away.
    var again: u64 = 123;
    try std.testing.expect(exports.crimson_host_replay_detach(first_handle, &again) != exports.ok);
    exports.crimson_host_session_destroy(first_handle);

    // The next run, on its own session, gets its own recording.
    const second_handle = try createTestSession();
    defer exports.crimson_host_session_destroy(second_handle);
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_begin(second_handle));
    for (0..30) |tick| {
        var inputs = [_]exports.CrimsonHostInput{scriptedInput(tick)};
        try std.testing.expectEqual(
            exports.ok,
            exports.crimson_host_session_tick(second_handle, &inputs, 1, null),
        );
    }
    var second: u64 = 0;
    try std.testing.expectEqual(exports.ok, exports.crimson_host_replay_detach(second_handle, &second));
    try std.testing.expect(second != first);

    // Both still encode: neither was invalidated by the other's session going.
    for ([_]u64{ first, second }) |rec| {
        var size: u32 = 0;
        try std.testing.expectEqual(exports.ok, exports.crimson_host_recording_encode(rec, null, &size));
        try std.testing.expect(size > 0);
        try std.testing.expectEqual(exports.ok, exports.crimson_host_recording_destroy(rec));
    }
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

test "abi verify mirrors native verifier result" {
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
        if (expected.exit_code == 1 and expected.stdout.len == 0 and expected.stderr.len > 0) {
            try std.testing.expectEqual(exports.err_generic, exports.crimson_host_verify_replay_json(
                fixture.ptr,
                @intCast(fixture.len),
                null,
                &out_len,
            ));
            var err_buf: [1024]u8 = undefined;
            const err_len = exports.crimson_host_last_error(&err_buf, err_buf.len);
            try std.testing.expect(err_len > 0);
            try std.testing.expectEqualStrings(
                std.mem.trimEnd(u8, expected.stderr, "\n"),
                err_buf[0..@intCast(err_len)],
            );
            continue;
        }

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
