using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

public class ArenaLayoutValuesTests
{
    [Theory]
    [InlineData(0.50f, 0.75f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(1.00f, 1.00f)]
    [InlineData(1.25f, 1.25f)]
    [InlineData(3.75f, 1.25f)]
    public void TabletopScale_SnapsToNamedSize(float input, float expected)
        => Assert.Equal(expected, ArenaLayoutValues.SnapTabletopScale(input));

    [Theory]
    [InlineData(1.00f, 0.00f)]
    [InlineData(0.50f, 0.50f)]
    [InlineData(0.00f, 1.00f)]
    public void Height_InvertsLegacyDownwardDrop(float drop, float height)
    {
        Assert.Equal(height, ArenaLayoutValues.HeightFromDrop(drop));
        Assert.Equal(drop, ArenaLayoutValues.DropFromHeight(height));
    }
}
