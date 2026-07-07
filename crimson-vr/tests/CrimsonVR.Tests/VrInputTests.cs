using CrimsonVR;
using Godot;
using Xunit;

namespace CrimsonVR.Tests;

public class VrInputTests
{
    private static readonly Vector2 PlayerCenter = new(512, 512);

    private static HandSample Hand(
        Vector2 reticle,
        bool triggerHeld = false,
        bool triggerPressed = false,
        bool reloadPressed = false)
        => new()
        {
            ReticleGame = reticle,
            TriggerHeld = triggerHeld,
            TriggerPressed = triggerPressed,
            ReloadPressed = reloadPressed,
        };

    [Fact]
    public void ResolveRoles_DefaultIsLeftMoveRightAim()
    {
        HandSample left = Hand(new Vector2(1, 1));
        HandSample right = Hand(new Vector2(2, 2));

        (HandSample move, HandSample aim) = VrInput.ResolveRoles(left, right, swapped: false);
        Assert.Equal(left.ReticleGame, move.ReticleGame);
        Assert.Equal(right.ReticleGame, aim.ReticleGame);
    }

    [Fact]
    public void ResolveRoles_SwappedFlipsHands()
    {
        HandSample left = Hand(new Vector2(1, 1));
        HandSample right = Hand(new Vector2(2, 2));

        (HandSample move, HandSample aim) = VrInput.ResolveRoles(left, right, swapped: true);
        Assert.Equal(right.ReticleGame, move.ReticleGame);
        Assert.Equal(left.ReticleGame, aim.ReticleGame);
    }

    [Fact]
    public void Build_AlwaysSetsAimAndControlSchemes()
    {
        Sim.HostInput input = VrInput.Build(
            move: Hand(PlayerCenter),
            aim: Hand(new Vector2(700, 300)),
            playerGame: PlayerCenter);

        Assert.Equal(700f, input.AimX, 3);
        Assert.Equal(300f, input.AimY, 3);
        Assert.Equal(Sim.HostInput.AimSchemeMouse, input.AimScheme);
        Assert.Equal(Sim.HostInput.MoveModeMousePointClick, input.MoveMode);
        Assert.Equal(-1, input.PerkChoiceIndex);
        Assert.Equal(0u, input.PerkMenuActive);
    }

    [Fact]
    public void Build_MoveHeldOutsideDeadZone_EmitsUnitDirection()
    {
        // Reticle east of the player -> move_x/move_y is a unit vector pointing
        // +x (a direction, not the absolute target point).
        var target = new Vector2(700, 512);
        Sim.HostInput input = VrInput.Build(
            move: Hand(target, triggerHeld: true),
            aim: Hand(PlayerCenter),
            playerGame: PlayerCenter);

        Assert.NotEqual(0u, input.Flags & Sim.HostInput.FlagMoveToCursor);
        Assert.Equal(1.0f, input.MoveX, 3);
        Assert.Equal(0.0f, input.MoveY, 3);
        Assert.Equal(1.0f, new Vector2(input.MoveX, input.MoveY).Length(), 3);
    }

    [Fact]
    public void Build_MoveHeldDiagonal_IsNormalized()
    {
        var target = PlayerCenter + new Vector2(300, 300);
        Sim.HostInput input = VrInput.Build(
            move: Hand(target, triggerHeld: true),
            aim: Hand(PlayerCenter),
            playerGame: PlayerCenter);

        Assert.Equal(1.0f, new Vector2(input.MoveX, input.MoveY).Length(), 3);
        Assert.Equal(input.MoveX, input.MoveY, 3); // symmetric diagonal
    }

    [Fact]
    public void Build_MoveHeldInsideDeadZone_DoesNotMove()
    {
        // Reticle within the dead zone radius of the player -> zero move vector.
        var target = PlayerCenter + new Vector2(VrInput.DefaultDeadZoneGameUnits * 0.5f, 0);
        Sim.HostInput input = VrInput.Build(
            move: Hand(target, triggerHeld: true),
            aim: Hand(PlayerCenter),
            playerGame: PlayerCenter);

        Assert.Equal(0u, input.Flags & Sim.HostInput.FlagMoveToCursor);
        Assert.Equal(0.0f, input.MoveX, 3);
        Assert.Equal(0.0f, input.MoveY, 3);
    }

    [Fact]
    public void Build_MoveNotHeld_DoesNotMove()
    {
        Sim.HostInput input = VrInput.Build(
            move: Hand(new Vector2(50, 50), triggerHeld: false),
            aim: Hand(PlayerCenter),
            playerGame: PlayerCenter);

        Assert.Equal(0u, input.Flags & Sim.HostInput.FlagMoveToCursor);
        Assert.Equal(0.0f, input.MoveX, 3);
        Assert.Equal(0.0f, input.MoveY, 3);
    }

    [Fact]
    public void Build_AimTriggerMapsToFireBits()
    {
        Sim.HostInput held = VrInput.Build(
            move: Hand(PlayerCenter),
            aim: Hand(new Vector2(700, 300), triggerHeld: true, triggerPressed: false),
            playerGame: PlayerCenter);
        Assert.NotEqual(0u, held.Flags & Sim.HostInput.FlagFireDown);
        Assert.Equal(0u, held.Flags & Sim.HostInput.FlagFirePressed);

        Sim.HostInput pressed = VrInput.Build(
            move: Hand(PlayerCenter),
            aim: Hand(new Vector2(700, 300), triggerHeld: true, triggerPressed: true),
            playerGame: PlayerCenter);
        Assert.NotEqual(0u, pressed.Flags & Sim.HostInput.FlagFireDown);
        Assert.NotEqual(0u, pressed.Flags & Sim.HostInput.FlagFirePressed);
    }

    [Fact]
    public void Build_ReloadFromAimHandOnly()
    {
        // Reload comes from the aim hand; a reload flag on the move hand is
        // ignored by construction.
        Sim.HostInput fromAim = VrInput.Build(
            move: Hand(PlayerCenter),
            aim: Hand(new Vector2(700, 300), reloadPressed: true),
            playerGame: PlayerCenter);
        Assert.NotEqual(0u, fromAim.Flags & Sim.HostInput.FlagReloadPressed);

        Sim.HostInput fromMove = VrInput.Build(
            move: Hand(PlayerCenter, reloadPressed: true),
            aim: Hand(new Vector2(700, 300)),
            playerGame: PlayerCenter);
        Assert.Equal(0u, fromMove.Flags & Sim.HostInput.FlagReloadPressed);
    }
}
