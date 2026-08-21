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
}
