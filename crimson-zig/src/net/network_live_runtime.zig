const std = @import("std");

const formats = @import("../formats/mod.zig");
const canonical_capture = @import("canonical_capture.zig");
const live_runner = @import("../runtime/live_runner.zig");
const state_mod = @import("../runtime/state.zig");
const lockstep_input_adapter = @import("lockstep_input_adapter.zig");
const lockstep_live_bridge = @import("lockstep_live_bridge.zig");
const lockstep_live_session = @import("lockstep_live_session.zig");
const lockstep_protocol = @import("lockstep_protocol.zig");
const lockstep_session = @import("lockstep_session.zig");
const packed_input = @import("packed_input.zig");
const relay_transport = @import("relay_transport.zig");
const rollback_live_bridge = @import("rollback_live_bridge.zig");
const rollback_live_session = @import("rollback_live_session.zig");
const room_code = @import("room_code.zig");

pub const Role = enum {
    host,
    join,
};

pub const Netcode = enum {
    rollback,
    lockstep,
};

// LiveRunner is a multi-megabyte value. Windows/.NET caller threads commonly
// have a 1 MiB stack, so constructing a runner when MatchStart/RoomStart first
// arrives must not happen on the ABI caller's stack. Host runners are created
// by the host ABI's existing big-stack initialization path; clients create
// theirs later, while pumping the lobby, and need the same treatment here.
const runner_init_stack_size: usize = 64 * 1024 * 1024;

fn ensureRunnerOnBigStack(comptime Session: type, session: *Session) !void {
    const Task = struct {
        session: *Session,
        err: ?anyerror = null,

        fn run(task: *@This()) void {
            _ = task.session.ensureLiveRunner() catch |err| {
                task.err = err;
                return;
            };
        }
    };
    var task = Task{ .session = session };
    const thread = try std.Thread.spawn(
        .{ .stack_size = runner_init_stack_size },
        Task.run,
        .{&task},
    );
    thread.join();
    if (task.err) |err| return err;
}

/// Presentation-independent configuration shared by desktop and XR hosts.
pub const LaunchConfig = struct {
    role: Role,
    mode_id: i32,
    player_count: i32,
    quest_level: ?@import("../quest_level.zig").QuestLevel = null,
    netcode: Netcode,
    bind_host: []const u8 = "0.0.0.0",
    host: []const u8 = "127.0.0.1",
    port: u16 = 31993,
    room_code_text: ?[]const u8 = null,
    build_id: []const u8 = "0.1.0-dev",
    peer_name: []const u8 = "window",
    session_id: []const u8 = "window-lockstep",
    input_delay_ticks: i32 = 0,
    max_recv_packets: usize = 512,
};

pub const Update = struct {
    stats: lockstep_session.UpdateStats = .{},
    frames_advanced: usize = 0,
    ticks_advanced: usize = 0,
    last_tick_index: ?i32 = null,
    last_player_count: usize = 0,
    last_input_flags: [state_mod.max_players]u32 = [_]u32{0} ** state_mod.max_players,
    last_frame_update: ?live_runner.FrameUpdate = null,
    audio: live_runner.FrameAudioEvents = .{},
    terrain_fx: @import("../runtime/terrain_fx.zig").TerrainFxBatch = .{},
};

pub const NetworkLiveRuntime = union(enum) {
    host: lockstep_live_session.HostLiveSession,
    client: lockstep_live_session.ClientLiveSession,
    rollback: rollback_live_session.LiveSession,

    pub fn init(config: LaunchConfig, seed: i32) !NetworkLiveRuntime {
        return initWithStatus(config, seed, null);
    }

    pub fn initInto(out: *NetworkLiveRuntime, config: LaunchConfig, seed: i32, status: ?formats.game_cfg.Status) !void {
        out.* = try initWithStatus(config, seed, status);
        rebindAfterInit(out);
    }

    /// Resolve a DNS host before constructing the transport. Numeric IPv4
    /// remains the fast path; public relay clients can use a stable hostname.
    pub fn initIntoResolved(out: *NetworkLiveRuntime, config: LaunchConfig, seed: i32, status: ?formats.game_cfg.Status, io: std.Io) !void {
        const resolved_host = if (config.role == .join or config.netcode == .rollback)
            try resolveIpv4(io, config.host, config.port)
        else
            null;
        out.* = try initWithStatusHost(config, seed, status, resolved_host);
        rebindAfterInit(out);
    }

    fn rebindAfterInit(out: *NetworkLiveRuntime) void {
        switch (out.*) {
            .host => |*host| host.runner.rebindAfterMove(),
            .client => {},
            .rollback => {},
        }
    }

    pub fn initWithStatus(config: LaunchConfig, seed: i32, status: ?formats.game_cfg.Status) !NetworkLiveRuntime {
        return initWithStatusHost(config, seed, status, null);
    }

    fn initWithStatusHost(config: LaunchConfig, seed: i32, status: ?formats.game_cfg.Status, resolved_host: ?[4]u8) !NetworkLiveRuntime {
        // Both lockstep MatchStart and rollback room creation require a host
        // status snapshot. Callers without a persisted profile get the same
        // deterministic zeroed baseline as a clean install.
        const host_status = if (config.role == .host)
            status orelse std.mem.zeroes(formats.game_cfg.Status)
        else
            null;
        return switch (config.netcode) {
            .lockstep => switch (config.role) {
                .host => .{
                    .host = try lockstep_live_session.HostLiveSession.init(.{
                        .bind_host = config.bind_host,
                        .bind_port = config.port,
                        .mode_id = config.mode_id,
                        .player_count = config.player_count,
                        .build_id = config.build_id,
                        .session_id = config.session_id,
                        .seed = seed,
                        .input_delay_ticks = config.input_delay_ticks,
                        .quest_level = config.quest_level,
                        .status = host_status,
                        .host_ready = false,
                        .pump_options = .{ .max_recv_packets = config.max_recv_packets, .first_timeout_ms = 0 },
                    }),
                },
                .join => .{
                    .client = lockstep_live_session.ClientLiveSession.init(.{
                        .bind_host = "0.0.0.0",
                        .bind_port = 0,
                        .mode_id = config.mode_id,
                        .player_count = config.player_count,
                        .build_id = config.build_id,
                        .host_addr = .{ .host = resolved_host orelse try parseIpv4(config.host), .port = config.port },
                        .input_delay_ticks = config.input_delay_ticks,
                        .quest_level = config.quest_level,
                        .pump_options = .{ .max_recv_packets = config.max_recv_packets, .first_timeout_ms = 0 },
                    }),
                },
            },
            .rollback => .{
                .rollback = rollback_live_session.LiveSession.init(.{
                    .server_addr = .{ .host = resolved_host orelse try parseIpv4(config.host), .port = config.port },
                    .bind_host = config.bind_host,
                    .session = .{
                        .role = switch (config.role) {
                            .host => .host,
                            .join => .join,
                        },
                        .mode_id = config.mode_id,
                        .player_count = config.player_count,
                        .build_id = config.build_id,
                        .peer_name = config.peer_name,
                        .room_code = if (config.room_code_text) |code_text|
                            try room_code.parseRoomCode(code_text)
                        else
                            null,
                        .quest_level = config.quest_level,
                        .input_delay_ticks = config.input_delay_ticks,
                        .status = host_status,
                    },
                }),
            },
        };
    }

    pub fn deinit(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io) void {
        switch (self.*) {
            .host => |*host| host.deinit(allocator, io),
            .client => |*client| client.deinit(allocator, io),
            .rollback => |*rollback| rollback.deinit(allocator, io),
        }
        self.* = undefined;
    }

    pub fn open(self: *NetworkLiveRuntime, io: std.Io) !void {
        switch (self.*) {
            .host => |*host| try host.open(io),
            .client => |*client| try client.open(io),
            .rollback => |*rollback| try rollback.open(io),
        }
    }

    pub fn start(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, now_ms: i64) !void {
        try self.open(io);
        switch (self.*) {
            .host => {},
            .client => |*client| try client.sendHello(allocator, now_ms),
            .rollback => |*rollback| try rollback.update(allocator, io, now_ms),
        }
    }

    pub fn update(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, now_ms: i64) !Update {
        return switch (self.*) {
            .host => |*host| blk: {
                const stats = try host.update(allocator, io, now_ms);
                const step_summary = if (host.session.runtime.started)
                    try host.stepReadyFrames(allocator, now_ms)
                else
                    lockstep_live_session.HostStepSummary{};
                break :blk .{
                    .stats = stats,
                    .frames_advanced = step_summary.frames_advanced,
                    .ticks_advanced = step_summary.ticks_advanced,
                    .last_tick_index = step_summary.last_tick_index,
                    .last_player_count = step_summary.last_player_count,
                    .last_input_flags = step_summary.last_input_flags,
                    .last_frame_update = step_summary.last_update,
                    .audio = step_summary.audio,
                    .terrain_fx = step_summary.terrain_fx,
                };
            },
            .client => |*client| blk: {
                const stats = try client.update(allocator, io, now_ms);
                if (client.runner == null and client.session.runtime.lobby.match_start != null) {
                    try ensureRunnerOnBigStack(lockstep_live_session.ClientLiveSession, client);
                    client.runner.?.rebindAfterMove();
                }
                const step_summary = if (client.runner != null)
                    try client.stepCanonicalFrames(allocator)
                else
                    lockstep_live_session.ClientStepSummary{};
                break :blk .{
                    .stats = stats,
                    .frames_advanced = step_summary.frames_advanced,
                    .ticks_advanced = step_summary.ticks_advanced,
                    .last_tick_index = step_summary.last_tick_index,
                    .last_player_count = step_summary.last_player_count,
                    .last_input_flags = step_summary.last_input_flags,
                    .last_frame_update = step_summary.last_update,
                    .audio = step_summary.audio,
                    .terrain_fx = step_summary.terrain_fx,
                };
            },
            .rollback => |*rollback| blk: {
                try rollback.update(allocator, io, now_ms);
                if (rollback.runner == null and rollback.session.match_config != null) {
                    try ensureRunnerOnBigStack(rollback_live_session.LiveSession, rollback);
                }
                const step_summary = if (rollback.hostRemoteInputsReady())
                    try rollback.stepFrames(allocator)
                else
                    rollback_live_session.StepSummary{};
                break :blk .{
                    .frames_advanced = step_summary.frames_advanced,
                    .ticks_advanced = step_summary.ticks_advanced,
                    .last_tick_index = step_summary.last_tick_index,
                    .last_player_count = step_summary.last_player_count,
                    .last_input_flags = step_summary.last_input_flags,
                    .last_frame_update = step_summary.last_update,
                    .audio = step_summary.audio,
                    .terrain_fx = step_summary.terrain_fx,
                };
            },
        };
    }

    pub fn submitLocalInput(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, input: packed_input.PackedPlayerInput, now_ms: i64) !void {
        switch (self.*) {
            .host => |*host| try host.submitLocalInput(allocator, input),
            .client => |*client| try client.queueLocalInput(allocator, input, now_ms),
            .rollback => |*rollback| try rollback.queueLocalInput(allocator, io, input, now_ms),
        }
    }

    pub fn submitLocalCommand(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, command: lockstep_protocol.GameCommand, now_ms: i64) !void {
        switch (self.*) {
            .host => |*host| try host.submitLocalCommand(allocator, command),
            .client => |*client| try client.submitLocalCommand(allocator, command, now_ms),
            .rollback => |*rollback| try rollback.submitLocalCommand(allocator, io, command, now_ms),
        }
    }

    pub fn setLocalReady(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, ready: bool, now_ms: i64) !void {
        switch (self.*) {
            .host => |*host| try host.session.setLocalReady(allocator, io, ready, now_ms),
            .client => |*client| try client.session.setLocalReady(allocator, io, ready, now_ms),
            .rollback => |*rollback| try rollback.session.setLocalReady(allocator, ready, now_ms),
        }
    }

    pub fn resumeAfterSuspend(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, now_ms: i64) !void {
        switch (self.*) {
            .rollback => |*rollback| try rollback.session.forceReconnect(allocator, now_ms),
            .host, .client => {},
        }
    }

    pub fn takeCanonicalCaptures(self: *NetworkLiveRuntime) std.ArrayList(canonical_capture.Frame) {
        return switch (self.*) {
            .host => |*host| blk: {
                const value = host.captures;
                host.captures = .empty;
                break :blk value;
            },
            .client => |*client| blk: {
                const value = client.captures;
                client.captures = .empty;
                break :blk value;
            },
            .rollback => |*rollback| blk: {
                const value = rollback.captures;
                rollback.captures = .empty;
                break :blk value;
            },
        };
    }

    pub fn hostRemoteInputsReady(self: *const NetworkLiveRuntime) bool {
        return switch (self.*) {
            .host, .client => true,
            .rollback => |*rollback| rollback.hostRemoteInputsReady(),
        };
    }

    pub fn submitLocalFrameInput(self: *NetworkLiveRuntime, allocator: std.mem.Allocator, io: std.Io, frame_input: live_runner.FrameInput, now_ms: i64) !bool {
        const slot = self.localInputSlot() orelse return false;
        if (frame_input.player_count != 0 and slot >= frame_input.player_count) return false;
        const local_input_value = if (frame_input.player_count == 0 and slot == 0)
            frame_input.player
        else
            frame_input.players[slot];
        try self.submitLocalInput(allocator, io, lockstep_input_adapter.packGameInput(local_input_value), now_ms);
        return true;
    }

    pub fn runnerForLocalInput(self: *NetworkLiveRuntime) ?*live_runner.LiveRunner {
        return switch (self.*) {
            .host => |*host| if (host.session.runtime.started and host.session.runtime.lockstep != null) &host.runner else null,
            .client => |*client| if (client.runner) |*runner| runner else null,
            .rollback => |*rollback| if (rollback.runner) |*runner| runner else null,
        };
    }

    pub fn runConfigForResults(self: *const NetworkLiveRuntime) ?live_runner.LiveModeConfig {
        return switch (self.*) {
            .host => |host| lockstep_live_bridge.liveConfigFromHostRuntime(host.session.runtime) catch null,
            .client => |client| blk: {
                const maybe_config = lockstep_live_bridge.liveConfigFromClientRuntime(client.session.runtime) orelse break :blk null;
                break :blk maybe_config catch null;
            },
            .rollback => |rollback| blk: {
                const match_config = rollback.session.match_config orelse break :blk null;
                break :blk rollback_live_bridge.liveConfigFromMatchConfig(match_config) catch null;
            },
        };
    }

    pub fn localInputSlot(self: *const NetworkLiveRuntime) ?usize {
        return switch (self.*) {
            .host => 0,
            .client => |client| blk: {
                const slot_index = if (client.session.runtime.lockstep) |lockstep|
                    lockstep.local_slot_index
                else
                    client.session.runtime.lobby.slotIndex();
                if (slot_index < 0) break :blk null;
                const slot: usize = @intCast(slot_index);
                if (slot >= state_mod.max_players) break :blk null;
                break :blk slot;
            },
            .rollback => |rollback| blk: {
                if (rollback.session.local_slot_index < 0) break :blk null;
                const slot: usize = @intCast(rollback.session.local_slot_index);
                if (slot >= state_mod.max_players) break :blk null;
                break :blk slot;
            },
        };
    }

    pub fn boundPort(self: *const NetworkLiveRuntime) u16 {
        return switch (self.*) {
            .host => |host| host.session.boundPort(),
            .client => |client| client.session.boundPort(),
            .rollback => |rollback| rollback.boundPort(),
        };
    }

    pub fn rollbackRoomCode(self: *const NetworkLiveRuntime) ?room_code.RoomCode {
        return switch (self.*) {
            .host, .client => null,
            .rollback => |rollback| rollback.session.room_code_latest,
        };
    }
};

fn parsePeerAddr(host: []const u8, port: u16) !lockstep_session.PeerAddr {
    return .{ .host = try parseIpv4(host), .port = port };
}

fn parseRelayPeerAddr(host: []const u8, port: u16) !relay_transport.PeerAddr {
    return .{ .host = try parseIpv4(host), .port = port };
}

fn parseIpv4(host: []const u8) ![4]u8 {
    var parts: [4]u8 = undefined;
    var iter = std.mem.splitScalar(u8, host, '.');
    var idx: usize = 0;
    while (iter.next()) |part| {
        if (idx >= parts.len or part.len == 0) return error.InvalidNetworkHost;
        parts[idx] = std.fmt.parseInt(u8, part, 10) catch return error.InvalidNetworkHost;
        idx += 1;
    }
    if (idx != parts.len) return error.InvalidNetworkHost;
    return parts;
}

fn resolveIpv4(io: std.Io, host: []const u8, port: u16) !?[4]u8 {
    if (parseIpv4(host)) |numeric| return numeric else |_| {}
    const host_name = std.Io.net.HostName.init(host) catch return error.InvalidNetworkHost;
    var storage: [16]std.Io.net.HostName.LookupResult = undefined;
    var resolved: std.Io.Queue(std.Io.net.HostName.LookupResult) = .init(&storage);
    try host_name.lookup(io, &resolved, .{ .port = port, .family = .ip4 });
    while (resolved.getOne(io)) |result| switch (result) {
        .address => |address| switch (address) {
            .ip4 => |ip4| return ip4.bytes,
            .ip6 => {},
        },
        .canonical_name => {},
    } else |err| switch (err) {
        error.Closed => return error.InvalidNetworkHost,
        error.Canceled => return error.InvalidNetworkHost,
    }
}

test "launch config rejects invalid lockstep and rollback hosts consistently" {
    const base: LaunchConfig = .{
        .role = .join,
        .mode_id = 0,
        .player_count = 2,
        .netcode = .lockstep,
        .host = "localhost",
        .build_id = "test",
        .peer_name = "test-peer",
        .session_id = "test-session",
    };
    try std.testing.expectError(error.InvalidNetworkHost, NetworkLiveRuntime.init(base, 7));

    var rollback = base;
    rollback.netcode = .rollback;
    try std.testing.expectError(error.InvalidNetworkHost, NetworkLiveRuntime.init(rollback, 7));
}

test "host initialization retains deterministic match configuration" {
    var runtime = try NetworkLiveRuntime.init(.{
        .role = .host,
        .mode_id = 1,
        .player_count = 3,
        .netcode = .lockstep,
        .port = 0,
        .build_id = "cross-host-test",
        .peer_name = "native",
        .session_id = "deterministic-session",
    }, 42);
    defer runtime.host.deinit(std.testing.allocator, std.testing.io);

    try std.testing.expectEqual(@as(i32, 1), runtime.host.session.runtime.lobby.mode_id);
    try std.testing.expectEqual(@as(i32, 3), runtime.host.session.runtime.lobby.player_count);
    try std.testing.expectEqual(@as(i32, 42), runtime.host.session.runtime.seed);
    try std.testing.expectEqualStrings("cross-host-test", runtime.host.session.runtime.lobby.build_id);
}

const DeterminismSummary = struct {
    tick_index: usize,
    pos_x: f32,
    pos_y: f32,
    aim_heading: f32,
    creature_count: usize,
};

fn runDeterminismScript() !DeterminismSummary {
    var runtime = try NetworkLiveRuntime.init(.{
        .role = .host,
        .mode_id = 1,
        .player_count = 1,
        .netcode = .lockstep,
        .port = 0,
        .build_id = "determinism-test",
        .peer_name = "native",
        .session_id = "determinism-session",
        .input_delay_ticks = 0,
    }, 0x1234);
    defer runtime.host.deinit(std.testing.allocator, std.testing.io);

    runtime.host.session.runtime.lockstep = .{ .player_count = 1, .input_delay_ticks = 0 };
    for (0..8) |tick| {
        const tick_index: i32 = @intCast(tick);
        const input: packed_input.PackedPlayerInput = .{
            .move_x = if (tick % 2 == 0) 1.0 else -0.5,
            .move_y = 0.25,
            .aim_x = 1.0,
            .aim_y = 0.0,
        };
        try runtime.host.session.runtime.lockstep.?.submitInputSample(std.testing.allocator, 0, tick_index, input);
        const step = try runtime.host.stepReadyFrames(std.testing.allocator, @intCast(10 + tick));
        try std.testing.expectEqual(@as(usize, 1), step.ticks_advanced);
        try std.testing.expectEqual(@as(?i32, tick_index), step.last_tick_index);
    }

    const player = runtime.host.runner.session.playersConst()[0];
    return .{
        .tick_index = runtime.host.runner.session.tick_index,
        .pos_x = player.pos.x,
        .pos_y = player.pos.y,
        .aim_heading = player.aim_heading,
        .creature_count = runtime.host.runner.session.creatures.activeCount(),
    };
}

test "identical shared runtimes advance canonical inputs deterministically" {
    const left = try runDeterminismScript();
    const right = try runDeterminismScript();
    try std.testing.expectEqual(left, right);
}
