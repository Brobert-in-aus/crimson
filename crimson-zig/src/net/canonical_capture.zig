const lockstep_protocol = @import("lockstep_protocol.zig");
const packed_input = @import("packed_input.zig");
const relay_protocol = @import("relay_protocol.zig");
const rollback_runtime = @import("rollback_runtime.zig");

pub const max_players: usize = @intCast(relay_protocol.max_players);
pub const max_commands: usize = 8;

pub const Command = struct {
    kind: enum { perk_menu_open, perk_pick },
    player_index: i32,
    value: i32 = 0,
};

pub const Frame = struct {
    tick_index: i32,
    player_count: usize,
    inputs: [max_players]packed_input.PackedPlayerInput = [_]packed_input.PackedPlayerInput{.{}} ** max_players,
    commands: [max_commands]Command = undefined,
    command_count: usize = 0,
};

pub fn fromLockstep(frame: lockstep_protocol.TickFrame) Frame {
    return fromInputs(frame.tick_index, frame.frame_inputs, frame.commands);
}

pub fn fromInputs(tick_index: i32, inputs: []const packed_input.PackedPlayerInput, commands: []const lockstep_protocol.GameCommand) Frame {
    var out: Frame = .{ .tick_index = tick_index, .player_count = @min(inputs.len, max_players) };
    for (inputs[0..out.player_count], 0..) |input, idx| out.inputs[idx] = input;
    appendCommands(&out, commands);
    return out;
}

pub fn appendCommand(out: *Frame, command: Command) void {
    if (out.command_count >= out.commands.len) return;
    out.commands[out.command_count] = command;
    out.command_count += 1;
}

pub fn fromRollback(frame: rollback_runtime.TickFrame) Frame {
    var out: Frame = .{ .tick_index = frame.tick_index, .player_count = @min(frame.player_count, max_players) };
    for (0..out.player_count) |idx| out.inputs[idx] = frame.frame_inputs[idx];
    appendCommands(&out, frame.commands[0..frame.command_count]);
    return out;
}

pub fn appendCommands(out: *Frame, commands: []const lockstep_protocol.GameCommand) void {
    for (commands) |command| {
        if (out.command_count >= out.commands.len) break;
        out.commands[out.command_count] = switch (command) {
            .perk_menu_open => |value| .{ .kind = .perk_menu_open, .player_index = value.player_index },
            .perk_pick => |value| .{ .kind = .perk_pick, .player_index = value.player_index, .value = value.choice_index },
            else => continue,
        };
        out.command_count += 1;
    }
}
