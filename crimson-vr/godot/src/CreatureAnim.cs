namespace CrimsonVR;

/// <summary>
/// Creature animation frame selection — a faithful port of
/// <c>creature_anim_select_frame</c> (src/crimson/creatures/anim.py), which is
/// itself the reverse-engineered <c>creature_render_type</c> from the original
/// binary. Given a creature's live <c>anim_phase</c> (from the host-ABI
/// snapshot), its per-type base frame + mirror flag (from the sprite manifest),
/// and its runtime flags (snapshot), this picks the 8x8 atlas frame index the
/// desktop renderer would draw.
///
/// Pure integer/float math with no Godot engine dependency, so it compiles into
/// the xUnit test project alongside Mapper/VrInput and is covered by golden
/// values taken from the Python reference (see CreatureAnimTests).
/// </summary>
public static class CreatureAnim
{
    // CreatureFlags bits (src/crimson/creatures/spawn_ids.py). These arrive
    // verbatim in CreatureSnap.Flags (host_abi passes entry.flags through).
    public const uint FlagAnimPingPong = 0x04;
    public const uint FlagRangedAttackShock = 0x10;
    public const uint FlagAnimLongStrip = 0x40;

    /// <summary>Long strip when PING_PONG is clear OR LONG_STRIP is set
    /// (creature_update_all / creature_render_type).</summary>
    public static bool IsLongStrip(uint flags)
        => (flags & FlagAnimPingPong) == 0 || (flags & FlagAnimLongStrip) != 0;

    /// <summary>Select an 8x8 atlas frame index for a creature. Mirroring is
    /// folded into the index (long-strip ping-pong), never a texture flip — so
    /// callers just render this frame with no extra state. Never returns &lt; 0
    /// for the values the sim produces.</summary>
    public static int SelectFrame(float phase, int baseFrame, bool mirrorLong, uint flags)
    {
        if (IsLongStrip(flags))
        {
            int frame;
            if (phase < 0.0f)
            {
                // Negative phase is a special render state; fixed fallback frame.
                frame = baseFrame + 0x0F;
            }
            else
            {
                // __ftol(phase + 0.5f) in the original (truncate toward zero).
                frame = (int)(phase + 0.5f);
                if (mirrorLong && frame > 0x0F)
                {
                    frame = 0x1F - frame;
                }
            }
            if ((flags & FlagRangedAttackShock) != 0)
            {
                frame += 0x20;
            }
            return frame;
        }

        // Ping-pong strip:
        //   idx = (__ftol(phase + 0.5f) & 0x8000000f); normalize negatives; mirror >7.
        int raw = (int)(phase + 0.5f);
        int idx = unchecked((int)((uint)raw & 0x8000000Fu));
        if (idx < 0)
        {
            idx = unchecked((int)((uint)(((idx - 1) | unchecked((int)0xFFFFFFF0)) + 1)));
        }
        if (idx > 7)
        {
            idx = 0x0F - idx;
        }
        return baseFrame + 0x10 + idx;
    }
}
