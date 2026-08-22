using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

public class SpatialMenuPlacementTests
{
    [Fact]
    public void PlayerFacing_AddsFifteenCentimetresToSharedMenuPlane()
    {
        Godot.Vector3 position = SpatialMenuPlacement.PlayerFacing(0.4f);

        Assert.Equal(0.34f, position.Y, 3);
        Assert.Equal(0.25f, position.Z, 3);
    }

    [Fact]
    public void PlayerFacing_PreservesSurfaceSpecificHeightAndHorizontalOffset()
    {
        Godot.Vector3 position = SpatialMenuPlacement.PlayerFacing(
            0.4f, heightFactor: 0.70f, horizontalOffset: -0.42f);

        Assert.Equal(-0.42f, position.X, 3);
        Assert.Equal(0.28f, position.Y, 3);
        Assert.Equal(0.25f, position.Z, 3);
    }

    [Theory]
    [InlineData(-1.0f, 0.05f)]
    [InlineData(0.15f, 0.15f)]
    [InlineData(1.0f, 0.45f)]
    public void MenuDistance_IsClampedToComfortRange(float input, float expected)
        => Assert.Equal(expected, SpatialMenuPlacement.ClampAdditionalDistance(input), 3);

    [Fact]
    public void PlayerFacing_AcceptsPersistedSharedMenuDistance()
    {
        Godot.Vector3 position = SpatialMenuPlacement.PlayerFacing(
            0.4f, additionalDistanceMeters: 0.30f);

        Assert.Equal(0.40f, position.Z, 3);
    }

    [Fact]
    public void MenuDistanceGrab_IgnoresHorizontalAndVerticalMovement()
    {
        float adjusted = SpatialMenuPlacement.AdjustAdditionalDistance(
            0.15f, new Godot.Vector3(0.8f, -0.6f, 0.07f));

        Assert.Equal(0.22f, adjusted, 3);
    }
}
