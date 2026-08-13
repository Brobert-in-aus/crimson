const std = @import("std");

const live_runner = @import("../runtime/live_runner.zig");
const canonical_capture = @import("canonical_capture.zig");
const lockstep_live_bridge = @import("lockstep_live_bridge.zig");
const lockstep_protocol = @import("lockstep_protocol.zig");
const lockstep_session = @import("lockstep_session.zig");
const lockstep_state = @import("lockstep_state.zig");
const packed_input = @import("packed_input.zig");

const Io = std.Io;
const max_players: usize = @intCast(lockstep_protocol.max_players);

pub const HostLiveSessionError = lockstep_live_bridge.BridgeError || live_runner.LiveRunnerError;
pub const ClientLiveSessionError = lockstep_live_bridge.BridgeError || live_runner.LiveRunnerError;
pub const ClientLiveStepError = lockstep_live_bridge.StepCanonicalFrameError || error{ MatchNotStarted, OutOfMemory };

pub const HostStepSummary = struct {
    frames_advanced: usize = 0,
    ticks_advanced: usize = 0,
    last_tick_index: ?i32 = null,
    last_player_count: usize = 0,
    last_input_flags: [max_players]u32 = [_]u32{0} ** max_players,
    last_update: ?live_runner.FrameUpdate = null,
    audio: live_runner.FrameAudioEvents = .{},
    terrain_fx: @import("../runtime/terrain_fx.zig").TerrainFxBatch = .{},
};

pub const ClientStepSummary = struct {
    frames_advanced: usize = 0,
    ticks_advanced: usize = 0,
    last_tick_index: ?i32 = null,
    last_player_count: usize = 0,
    last_input_flags: [max_players]u32 = [_]u32{0} ** max_players,
    last_update: ?live_runner.FrameUpdate = null,
    audio: live_runner.FrameAudioEvents = .{},
    terrain_fx: @import("../runtime/terrain_fx.zig").TerrainFxBatch = .{},
};

pub const HostLiveSession = struct {
    session: lockstep_session.HostSession,
    runner: live_runner.LiveRunner,
    captures: std.ArrayList(canonical_capture.Frame) = .empty,
    perk_state: PerkCommandState = .{},

    pub fn init(options: lockstep_session.HostSessionOptions) HostLiveSessionError!HostLiveSession {
        const session = lockstep_session.HostSession.init(options);
        return .{
            .session = session,
            .runner = try live_runner.LiveRunner.init(try lockstep_live_bridge.liveConfigFromHostRuntime(session.runtime)),
        };
    }

    pub fn deinit(self: *HostLiveSession, allocator: std.mem.Allocator, io: Io) void {
        self.session.deinit(allocator, io);
        self.captures.deinit(allocator);
        self.* = undefined;
    }

    pub fn open(self: *HostLiveSession, io: Io) !void {
        try self.session.open(io);
    }

    pub fn close(self: *HostLiveSession, io: Io) void {
        self.session.close(io);
    }

    pub fn update(self: *HostLiveSession, allocator: std.mem.Allocator, io: Io, now_ms: i64) !lockstep_session.UpdateStats {
        return self.session.update(allocator, io, now_ms);
    }

    pub fn submitLocalInput(self: *HostLiveSession, allocator: std.mem.Allocator, input: packed_input.PackedPlayerInput) !void {
        try self.session.submitLocalInput(allocator, input);
    }

    pub fn submitLocalCommand(self: *HostLiveSession, allocator: std.mem.Allocator, command: lockstep_protocol.GameCommand) !void {
        try self.session.submitLocalCommand(allocator, command);
    }

    pub fn stepReadyFrames(self: *HostLiveSession, allocator: std.mem.Allocator, now_ms: i64) !HostStepSummary {
        var ready_frames = try self.session.popReadyFrames(allocator, now_ms);
        defer lockstep_state.deinitHostReadyTicks(allocator, &ready_frames);

        var summary: HostStepSummary = .{};
        for (ready_frames.items) |ready| {
            const commands = self.session.takePendingCommands();
            const frame: lockstep_protocol.TickFrame = .{
                .tick_index = ready.tick_index,
                .frame_inputs = ready.frame_inputs,
                .commands = commands,
            };
            const stepped = try stepNetworkFrame(&self.runner, frame.frame_inputs, frame.commands, &self.perk_state);
            if (stepped.capture) |capture| try self.captures.append(allocator, capture);
            const update_result = stepped.update;
            summary.frames_advanced += 1;
            summary.ticks_advanced += update_result.ticks_advanced;
            summary.last_tick_index = ready.tick_index;
            const captured = captureInputFlags(ready.frame_inputs);
            summary.last_player_count = captured.player_count;
            summary.last_input_flags = captured.flags;
            summary.last_update = update_result;
            summary.audio.mergeFrom(update_result.audio);
            summary.terrain_fx.mergeFrom(update_result.terrain_fx);
            try self.session.broadcastTickFrame(allocator, frame, now_ms);
            self.session.clearPendingCommands(allocator);
        }
        return summary;
    }
};

pub const ClientLiveSession = struct {
    session: lockstep_session.ClientSession,
    runner: ?live_runner.LiveRunner = null,
    captures: std.ArrayList(canonical_capture.Frame) = .empty,
    perk_state: PerkCommandState = .{},

    pub fn init(options: lockstep_session.ClientSessionOptions) ClientLiveSession {
        return .{
            .session = lockstep_session.ClientSession.init(options),
        };
    }

    pub fn deinit(self: *ClientLiveSession, allocator: std.mem.Allocator, io: Io) void {
        self.session.deinit(allocator, io);
        self.captures.deinit(allocator);
        self.* = undefined;
    }

    pub fn open(self: *ClientLiveSession, io: Io) !void {
        try self.session.open(io);
    }

    pub fn close(self: *ClientLiveSession, io: Io) void {
        self.session.close(io);
    }

    pub fn update(self: *ClientLiveSession, allocator: std.mem.Allocator, io: Io, now_ms: i64) !lockstep_session.UpdateStats {
        return self.session.update(allocator, io, now_ms);
    }

    pub fn sendHello(self: *ClientLiveSession, allocator: std.mem.Allocator, now_ms: i64) !void {
        try self.session.sendHello(allocator, now_ms);
    }

    pub fn queueLocalInput(
        self: *ClientLiveSession,
        allocator: std.mem.Allocator,
        input: packed_input.PackedPlayerInput,
        now_ms: i64,
    ) !void {
        try self.session.queueLocalInput(allocator, input, now_ms);
    }

    pub fn submitLocalCommand(self: *ClientLiveSession, allocator: std.mem.Allocator, command: lockstep_protocol.GameCommand, now_ms: i64) !void {
        try self.session.submitLocalCommand(allocator, command, now_ms);
    }

    pub fn ensureLiveRunner(self: *ClientLiveSession) ClientLiveSessionError!bool {
        if (self.runner != null) return false;
        const config = try (lockstep_live_bridge.liveConfigFromClientRuntime(self.session.runtime) orelse return false);
        self.runner = try live_runner.LiveRunner.init(config);
        return true;
    }

    pub fn stepCanonicalFrames(self: *ClientLiveSession, allocator: std.mem.Allocator) ClientLiveStepError!ClientStepSummary {
        if (self.runner == null) return error.MatchNotStarted;

        var summary: ClientStepSummary = .{};
        while (self.session.popCanonicalFrame()) |frame_value| {
            var frame = frame_value;
            defer lockstep_state.deinitTickFrame(allocator, &frame);
            const stepped = try stepNetworkFrame(&self.runner.?, frame.frame_inputs, frame.commands, &self.perk_state);
            if (stepped.capture) |capture| try self.captures.append(allocator, capture);
            const update_result = stepped.update;
            summary.frames_advanced += 1;
            summary.ticks_advanced += update_result.ticks_advanced;
            summary.last_tick_index = frame.tick_index;
            const captured = captureInputFlags(frame.frame_inputs);
            summary.last_player_count = captured.player_count;
            summary.last_input_flags = captured.flags;
            summary.last_update = update_result;
            summary.audio.mergeFrom(update_result.audio);
            summary.terrain_fx.mergeFrom(update_result.terrain_fx);
        }
        return summary;
    }
};

const PerkCommandState = struct {
    active: bool = false,
    pending: canonical_capture.Frame = .{ .tick_index = 0, .player_count = 0 },
};

fn stepNetworkFrame(
    runner: *live_runner.LiveRunner,
    inputs: []const packed_input.PackedPlayerInput,
    commands: []const lockstep_protocol.GameCommand,
    state: *PerkCommandState,
) (lockstep_live_bridge.BridgeError || live_runner.LiveRunnerError)!struct { update: live_runner.FrameUpdate, capture: ?canonical_capture.Frame } {
    for (commands) |command| switch (command) {
        .perk_menu_open => state.active = true,
        else => {},
    };
    var input = try lockstep_live_bridge.frameInputFromPacked(inputs);
    try lockstep_live_bridge.applyCommandsToFrameInput(&input, commands);
    input.perk_menu_active = input.perk_menu_active or state.active;
    const tick_before: i32 = @intCast(runner.session.tick_index);
    const update = try runner.stepFrame(runner.session.dt_nominal, input);

    canonical_capture.appendCommands(&state.pending, commands);
    if (input.perk_choice_index != null and runner.perkPendingCount() == 0) state.active = false;
    if (update.ticks_advanced == 0) return .{ .update = update, .capture = null };

    var capture = canonical_capture.fromInputs(tick_before, inputs, &.{});
    for (state.pending.commands[0..state.pending.command_count]) |command| canonical_capture.appendCommand(&capture, command);
    state.pending.command_count = 0;
    return .{ .update = update, .capture = capture };
}

const CapturedInputFlags = struct {
    player_count: usize = 0,
    flags: [max_players]u32 = [_]u32{0} ** max_players,
};

fn captureInputFlags(inputs: []const packed_input.PackedPlayerInput) CapturedInputFlags {
    var captured: CapturedInputFlags = .{};
    captured.player_count = @min(inputs.len, captured.flags.len);
    for (inputs[0..captured.player_count], 0..) |input, idx| {
        captured.flags[idx] = input.flags;
    }
    return captured;
}

test "host live session starts live runner from host settings" {
    var host = try HostLiveSession.init(.{
        .mode_id = 2,
        .player_count = 2,
        .build_id = "0.1.0",
        .session_id = "session",
        .seed = 1234,
        .input_delay_ticks = 0,
    });
    defer host.deinit(std.testing.allocator, std.Io.Threaded.global_single_threaded.io());

    try std.testing.expectEqual(@as(u32, 1234), host.runner.seed);
    try std.testing.expectEqual(@as(usize, 2), host.runner.session.players().len);
}

test "host live session step summary records canonical inputs" {
    var host = try HostLiveSession.init(.{
        .mode_id = 2,
        .player_count = 2,
        .build_id = "0.1.0",
        .session_id = "session",
        .input_delay_ticks = 0,
    });
    defer host.deinit(std.testing.allocator, std.Io.Threaded.global_single_threaded.io());

    host.session.runtime.lockstep = .{ .player_count = 2, .input_delay_ticks = 0 };
    try host.session.runtime.lockstep.?.submitInputSample(std.testing.allocator, 0, 0, .{ .flags = 3 });
    try host.session.runtime.lockstep.?.submitInputSample(std.testing.allocator, 1, 0, .{ .flags = 7 });

    const summary = try host.stepReadyFrames(std.testing.allocator, 10);
    try std.testing.expectEqual(@as(usize, 1), summary.frames_advanced);
    try std.testing.expectEqual(@as(?i32, 0), summary.last_tick_index);
    try std.testing.expectEqual(@as(usize, 2), summary.last_player_count);
    try std.testing.expectEqual(@as(u32, 3), summary.last_input_flags[0]);
    try std.testing.expectEqual(@as(u32, 7), summary.last_input_flags[1]);
}

test "host live session stamps reliable perk commands into canonical capture" {
    var host = try HostLiveSession.init(.{
        .mode_id = 1,
        .player_count = 1,
        .build_id = "command-test",
        .session_id = "command-test",
        .input_delay_ticks = 0,
    });
    defer host.deinit(std.testing.allocator, std.Io.Threaded.global_single_threaded.io());
    host.session.runtime.lockstep = .{ .player_count = 1, .input_delay_ticks = 0 };
    host.session.runtime.started = true;
    try host.submitLocalCommand(std.testing.allocator, .{ .perk_menu_open = .{ .player_index = 0 } });
    try host.session.runtime.lockstep.?.submitInputSample(std.testing.allocator, 0, 0, .{});
    _ = try host.stepReadyFrames(std.testing.allocator, 10);
    try std.testing.expectEqual(@as(usize, 1), host.captures.items.len);
    try std.testing.expectEqual(@as(usize, 1), host.captures.items[0].command_count);
    try std.testing.expectEqualStrings("perk_menu_open", @tagName(host.captures.items[0].commands[0].kind));
}

test "client live session creates runner after match start" {
    var client = ClientLiveSession.init(.{
        .mode_id = 2,
        .player_count = 2,
        .build_id = "0.1.0",
        .host_addr = lockstep_session.PeerAddr.loopback(lockstep_protocol.default_port),
        .input_delay_ticks = 0,
    });
    defer {
        client.session.runtime.lobby.match_start = null;
        client.deinit(std.testing.allocator, std.Io.Threaded.global_single_threaded.io());
    }

    try std.testing.expect(!try client.ensureLiveRunner());
    client.session.runtime.lobby.ingestMatchStart(.{
        .session_id = "session",
        .mode_id = 2,
        .player_count = 2,
        .seed = 4321,
    });

    try std.testing.expect(try client.ensureLiveRunner());
    try std.testing.expect(!try client.ensureLiveRunner());
    try std.testing.expectEqual(@as(u32, 4321), client.runner.?.seed);
}
