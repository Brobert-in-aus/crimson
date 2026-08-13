const std = @import("std");

const Io = std.Io;
const IpAddress = std.Io.net.IpAddress;
const Socket = std.Io.net.Socket;

const queue_allocator = std.heap.page_allocator;
const max_queued_packets = 512;

pub const RawPacket = struct {
    from: IpAddress,
    data: []u8,
};

pub const RawPackets = struct {
    items: std.ArrayList(RawPacket) = .empty,

    pub fn deinit(self: *RawPackets, allocator: std.mem.Allocator) void {
        for (self.items.items) |packet| queue_allocator.free(packet.data);
        self.items.deinit(allocator);
        self.* = undefined;
    }
};

/// Windows' current std.Io.Threaded backend cannot put a datagram receive in a
/// timed concurrent batch. Keep one ordinary blocking receive on a stable
/// worker instead, then wake the game thread through a timed futex when raw
/// datagrams are ready. Protocol decoding remains on the game thread.
pub const ReceiveQueue = struct {
    socket: Socket,
    recv_buffer_size: usize,
    io_backend: std.Io.Threaded,
    thread: std.Thread,
    mutex: Io.Mutex = .init,
    epoch: std.atomic.Value(u32) = .init(0),
    stopping: std.atomic.Value(bool) = .init(false),
    packets: std.ArrayList(RawPacket) = .empty,
    failure: ?anyerror = null,

    pub fn init(socket: Socket, recv_buffer_size: usize) !*ReceiveQueue {
        const self = try queue_allocator.create(ReceiveQueue);
        errdefer queue_allocator.destroy(self);
        self.* = .{
            .socket = socket,
            .recv_buffer_size = recv_buffer_size,
            .io_backend = std.Io.Threaded.init(queue_allocator, .{}),
            .thread = undefined,
        };
        errdefer self.io_backend.deinit();
        self.thread = try std.Thread.spawn(.{ .stack_size = 1024 * 1024 }, receiveMain, .{self});
        return self;
    }

    pub fn deinit(self: *ReceiveQueue) void {
        self.stopping.store(true, .release);

        // Closing a handle with an outstanding AFD receive currently reaches
        // an `unreachable` in std.Io.Threaded. A loopback datagram lets the
        // blocking call finish normally before the handle is closed.
        const bound = self.socket.address.ip4;
        const wake_addr: IpAddress = .{ .ip4 = .{
            .bytes = if (std.mem.allEqual(u8, &bound.bytes, 0)) .{ 127, 0, 0, 1 } else bound.bytes,
            .port = bound.port,
        } };
        self.socket.send(self.io_backend.io(), &wake_addr, &.{0}) catch {};
        self.thread.join();
        self.socket.close(self.io_backend.io());

        for (self.packets.items) |packet| queue_allocator.free(packet.data);
        self.packets.deinit(queue_allocator);
        self.io_backend.deinit();
        queue_allocator.destroy(self);
    }

    pub fn take(
        self: *ReceiveQueue,
        allocator: std.mem.Allocator,
        io: Io,
        max_packets: usize,
        first_timeout_ms: i64,
    ) !RawPackets {
        var out: RawPackets = .{};
        errdefer out.deinit(allocator);
        if (max_packets == 0) return out;

        const observed_epoch = self.epoch.load(.acquire);
        self.mutex.lockUncancelable(io);
        const initially_empty = self.packets.items.len == 0 and self.failure == null;
        self.mutex.unlock(io);

        if (initially_empty and first_timeout_ms > 0) {
            try io.futexWaitTimeout(
                u32,
                &self.epoch.raw,
                observed_epoch,
                .{ .duration = .{ .raw = .fromMilliseconds(first_timeout_ms), .clock = .awake } },
            );
        }
        // A zero-timeout game-frame poll must remain genuinely non-blocking.
        // Thread.yield() can surrender the remainder of a Windows scheduler
        // quantum; the ABI pumps twice per frame, which reduced a real 60 Hz
        // PC/Quest match to roughly 36 simulation ticks per second. The receive
        // worker is independent and publishes through the queue/epoch.

        self.mutex.lockUncancelable(io);
        defer self.mutex.unlock(io);
        if (self.failure) |err| return err;

        const count = @min(max_packets, self.packets.items.len);
        try out.items.ensureTotalCapacity(allocator, count);
        for (0..count) |_| out.items.appendAssumeCapacity(self.packets.orderedRemove(0));
        return out;
    }

    fn receiveMain(self: *ReceiveQueue) void {
        const io = self.io_backend.io();
        const buffer = queue_allocator.alloc(u8, self.recv_buffer_size) catch {
            self.publishFailure(io, error.OutOfMemory);
            return;
        };
        defer queue_allocator.free(buffer);

        while (true) {
            const incoming = self.socket.receive(io, buffer) catch |err| switch (err) {
                // Windows reports an ICMP response from an unavailable UDP
                // destination against the next receive. UDP is connectionless;
                // this is a transient peer/relay condition, not a dead socket.
                error.PortUnreachable, error.ConnectionResetByPeer => {
                    if (self.stopping.load(.acquire)) return;
                    continue;
                },
                else => {
                    if (!self.stopping.load(.acquire)) self.publishFailure(io, err);
                    return;
                },
            };
            if (self.stopping.load(.acquire)) return;

            const data = queue_allocator.dupe(u8, incoming.data) catch {
                self.publishFailure(io, error.OutOfMemory);
                return;
            };
            self.mutex.lockUncancelable(io);
            if (self.packets.items.len >= max_queued_packets) {
                self.mutex.unlock(io);
                queue_allocator.free(data);
                continue;
            }
            self.packets.append(queue_allocator, .{ .from = incoming.from, .data = data }) catch {
                self.mutex.unlock(io);
                queue_allocator.free(data);
                self.publishFailure(io, error.OutOfMemory);
                return;
            };
            self.mutex.unlock(io);
            self.notify(io);
        }
    }

    fn publishFailure(self: *ReceiveQueue, io: Io, err: anyerror) void {
        self.mutex.lockUncancelable(io);
        self.failure = err;
        self.mutex.unlock(io);
        self.notify(io);
    }

    fn notify(self: *ReceiveQueue, io: Io) void {
        _ = self.epoch.fetchAdd(1, .release);
        io.futexWake(u32, &self.epoch.raw, 1);
    }
};
