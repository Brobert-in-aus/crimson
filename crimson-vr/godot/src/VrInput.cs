using Godot;

namespace CrimsonVR;

/// <summary>
/// Per-hand VR state sampled from a controller for one sim tick: the reticle's
/// projected game-space point plus the trigger/reload button state. Pure data
/// so the mapping in <see cref="VrInput"/> stays unit-testable without XR nodes.
/// </summary>
public struct HandSample
{
    public Vector2 ReticleGame;
    public bool TriggerHeld;
    public bool TriggerPressed;
    public bool ReloadPressed;
}

/// <summary>
/// Maps resolved VR hand state to a <see cref="Sim.HostInput"/> exactly per
/// PLAN.md §4. This is the only place VR intent becomes simulation intent, so
/// it stays pure (no Godot node access) and is covered by unit tests.
/// </summary>
public static class VrInput
{
    /// <summary>Dead zone / stop radius around the player where move-hand
    /// reticle input is ignored, to stop jitter when "standing on" your own
    /// marker (PLAN §4). Defaults to the desktop point-click stop radius
    /// (local_input.point_click_stop_radius = 20 game units) for parity; PLAN's
    /// ~12 is a comfort-tuning target for M4.</summary>
    public const float DefaultDeadZoneGameUnits = 20.0f;

    /// <summary>
    /// Resolve physical left/right controllers into move/aim roles. Default
    /// (locked) mapping is left = movement, right = aim/fire; the settings hand
    /// swap flips them (PLAN §1, §4).
    /// </summary>
    public static (HandSample Move, HandSample Aim) ResolveRoles(HandSample left, HandSample right, bool swapped)
        => swapped ? (right, left) : (left, right);

    /// <summary>
    /// Build the packed sim input for one tick. <paramref name="move"/> and
    /// <paramref name="aim"/> are the already role-resolved hands;
    /// <paramref name="playerGame"/> is the player's current game-space position
    /// (for the dead zone).
    /// </summary>
    public static Sim.HostInput Build(
        HandSample move,
        HandSample aim,
        Vector2 playerGame,
        float deadZone = DefaultDeadZoneGameUnits)
    {
        var input = new Sim.HostInput
        {
            AimX = aim.ReticleGame.X,
            AimY = aim.ReticleGame.Y,
            AimScheme = Sim.HostInput.AimSchemeMouse,
            MoveMode = Sim.HostInput.MoveModeMousePointClick,
            PerkChoiceIndex = -1,
            PerkMenuActive = 0,
        };

        uint flags = 0;

        // move_x/move_y is a normalized DIRECTION vector, not a world point: the
        // host ABI feeds it straight into the runtime, which treats
        // (move_x, move_y) as the analog move axis (movement.zig). The desktop's
        // point-click mode derives exactly this — dir = normalized(target -
        // player), zero when within the stop radius (local_input.zig §
        // point_click_stop_radius). We move only while the move-hand trigger is
        // held (PLAN §4 overrides the desktop click-to-latch behavior).
        Vector2 dir = Vector2.Zero;
        if (move.TriggerHeld)
        {
            Vector2 delta = move.ReticleGame - playerGame;
            if (delta.Length() > deadZone)
            {
                dir = delta.Normalized();
                flags |= Sim.HostInput.FlagMoveToCursor;
            }
        }
        input.MoveX = dir.X;
        input.MoveY = dir.Y;

        if (aim.TriggerHeld)
        {
            flags |= Sim.HostInput.FlagFireDown;
        }
        if (aim.TriggerPressed)
        {
            flags |= Sim.HostInput.FlagFirePressed;
        }
        if (aim.ReloadPressed)
        {
            flags |= Sim.HostInput.FlagReloadPressed;
        }

        input.Flags = flags;
        return input;
    }
}
