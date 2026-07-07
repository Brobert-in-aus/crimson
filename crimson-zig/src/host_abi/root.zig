//! Shared-library root for the crimson_host C ABI (`zig build host-lib`).
comptime {
    _ = @import("exports.zig");
}
